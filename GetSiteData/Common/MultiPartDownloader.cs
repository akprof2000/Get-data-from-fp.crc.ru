using System.Net;
using System.Text.Json;

namespace GetSiteData.Common;

/// <summary>
/// Скачивание больших файлов в несколько потоков с докачкой после обрыва.
/// Одиночное соединение сервера часто искусственно придушено (Geofabrik отдаёт
/// ~0,3 МБ/с в один поток и ~80 МБ/с в шестнадцать), поэтому файл режется на
/// сегменты, каждый качается своим Range-запросом, прогресс сегментов хранится
/// в сайдкаре «*.part.meta» — повторный запуск продолжает с места обрыва.
/// Сервер не поддерживает Range или не сообщает размер — честный фоллбэк в один
/// поток (без сегментной докачки).
/// </summary>
public static class MultiPartDownloader
{
    private const int DefaultSegments = 16;
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromMinutes(3);

    private sealed class Meta
    {
        public string Url { get; set; } = "";
        public long Length { get; set; }
        public long[] Done { get; set; } = [];
        public long[] From { get; set; } = [];
        public long[] To { get; set; } = [];
    }

    /// <summary>
    /// Качает url в target (атомарно через «*.part»). Возвращает true при успехе.
    /// Прогресс пишется в лог каждые ~512 МБ.
    /// </summary>
    public static bool Download(HttpClient http, string url, string target, int segments = DefaultSegments)
    {
        var part = target + ".part";
        var metaPath = part + ".meta";

        long length = -1;
        bool ranges = false;
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = http.Send(head, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            length = resp.Content.Headers.ContentLength ?? -1;
            ranges = resp.Headers.AcceptRanges.Contains("bytes");
        }
        catch (Exception ex)
        {
            Log.Warn($"HEAD не удался ({ex.Message}) — пробую однопоточно.");
        }

        if (length <= 0 || !ranges)
        {
            Log.Info("Сервер не поддерживает сегментную докачку — качаю в один поток.");
            return DownloadSingle(http, url, part, target);
        }

        // Восстановление сегментов из сайдкара (тот же url и размер) либо новая раскладка.
        Meta meta;
        if (File.Exists(metaPath) && File.Exists(part)
            && JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath)) is { } m
            && m.Url == url && m.Length == length)
        {
            meta = m;
            Log.Info($"Докачка: продолжаю с {m.Done.Sum() / (double)(1L << 20):F0} МБ из {length / (double)(1L << 20):F0} МБ");
        }
        else
        {
            segments = (int)Math.Clamp(length / (8L << 20), 1, segments); // мелкий файл — меньше потоков
            var size = length / segments;
            meta = new Meta
            {
                Url = url,
                Length = length,
                From = [.. Enumerable.Range(0, segments).Select(i => i * size)],
                To = [.. Enumerable.Range(0, segments).Select(i => i == segments - 1 ? length - 1 : (i + 1) * size - 1)],
                Done = new long[segments]
            };
            using var fs = new FileStream(part, FileMode.Create, FileAccess.Write);
            fs.SetLength(length);
            Log.Info($"Скачивание {length / (double)(1L << 20):F0} МБ в {segments} потоков…");
        }

        var metaLock = new object();
        long reported = 0;
        var ok = true;

        Parallel.For(0, meta.From.Length, new ParallelOptions { MaxDegreeOfParallelism = meta.From.Length }, seg =>
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    var from = meta.From[seg] + meta.Done[seg];
                    var to = meta.To[seg];
                    if (from > to) return;   // сегмент уже докачан

                    using var cts = new CancellationTokenSource(ChunkTimeout);
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                    using var resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                        throw new IOException($"ожидался 206, получен {(int)resp.StatusCode}");

                    using var body = resp.Content.ReadAsStream(cts.Token);
                    using var fs = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    fs.Position = from;
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = body.ReadAsync(buffer, 0, buffer.Length, cts.Token).GetAwaiter().GetResult()) > 0)
                    {
                        // Таймаут — на каждый кусок: заглохшее соединение переоткрывается,
                        // а не висит вечно.
                        cts.CancelAfter(ChunkTimeout);
                        fs.Write(buffer, 0, read);
                        lock (metaLock)
                        {
                            meta.Done[seg] += read;
                            var total = meta.Done.Sum();
                            if (total - reported >= 512L << 20)
                            {
                                reported = total;
                                File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));
                                Log.Info($"  {total / (double)(1L << 30):F1} ГБ из {length / (double)(1L << 30):F1} ГБ");
                            }
                        }
                        if (meta.From[seg] + meta.Done[seg] > to) break;
                    }
                    return;
                }
                catch (Exception ex) when (attempt < 5)
                {
                    lock (metaLock) File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));
                    Log.Warn($"сегмент {seg}, попытка {attempt}: {ex.Message}");
                    Thread.Sleep(TimeSpan.FromSeconds(5 * attempt));
                }
                catch (Exception ex)
                {
                    lock (metaLock) File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));
                    Log.Error($"сегмент {seg} не докачался: {ex.Message}");
                    ok = false;
                    return;
                }
            }
        });

        if (!ok || meta.Done.Select((d, i) => meta.From[i] + d - 1 >= meta.To[i]).Contains(false))
        {
            Log.Warn("Файл докачан не полностью — «*.part» сохранён, следующий запуск продолжит.");
            return false;
        }

        File.Delete(metaPath);
        File.Move(part, target, overwrite: true);
        return true;
    }

    private static bool DownloadSingle(HttpClient http, string url, string part, string target)
    {
        try
        {
            using var resp = http.Send(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            using var src = resp.Content.ReadAsStream();
            using var dst = new FileStream(part, FileMode.Create, FileAccess.Write);
            src.CopyTo(dst, 1 << 20);
        }
        catch (Exception ex)
        {
            Log.Error($"Скачивание не удалось: {ex.Message}");
            return false;
        }
        File.Move(part, target, overwrite: true);
        return true;
    }
}
