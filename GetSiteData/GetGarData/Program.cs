// GetGarData — скачивание выгрузки Государственного адресного реестра (ГАР, бывший
// ФИАС) с автообновлением. Запускается на машине С доступом в интернет; скачанный
// архив затем переносится в закрытый контур для этапа нормализации адресов.
//
// Как работает:
//   1. Спрашивает у официального сервиса ФНС (GetLastDownloadFileInfo) номер
//      актуальной версии и прямую ссылку на полную выгрузку gar_xml.zip.
//   2. Сравнивает с локальным состоянием (gar/version.json): версия не новее — выход.
//   3. Скачивает архив во временный файл «*.part» с докачкой после обрыва
//      (HTTP Range) и повторами; по завершении сверяет размер и атомарно
//      переименовывает в gar_xml_<версия>.zip.
//   4. Обновляет version.json — следующий запуск скачает только новую версию.
//
// Запускать можно хоть каждый день (по расписанию): реальное скачивание происходит
// только при выходе новой версии реестра. Аргумент «check» — только проверить
// наличие обновления, ничего не скачивая.
//
// Настройки — в общем appsettings.json конвейера, секция «GetGarData»:
//   OutputPath — каталог для архивов и version.json (относительный — под WorkRoot);
//   Delta      — true: качать дельту (gar_delta_xml.zip) вместо полной выгрузки.
using GetSiteData.Common;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace GetGarData;

public class Program
{
    private const string ServiceUrl = "https://fias.nalog.ru/WebServices/Public/GetLastDownloadFileInfo";
    private const string StateFileName = "version.json";

    private static string OutputPath = "gar";
    private static bool UseDelta;
    private static int MaxAttempts = 5;

    // Один клиент на всё скачивание; таймаут отключён — большой файл читается потоком,
    // от зависаний защищает таймаут на чтение каждого куска в CopyWithProgressAsync.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> Main(string[] args)
    {
        LoadConfiguration();
        var checkOnly = args.Any(a => a.Equals("check", StringComparison.OrdinalIgnoreCase));

        Log.Phase("Проверка версии ГАР");
        Log.Info($"Каталог выгрузок : {OutputPath}");
        Log.Info($"Режим            : {(UseDelta ? "дельта" : "полная выгрузка")}{(checkOnly ? " (только проверка)" : "")}");

        DownloadInfo latest;
        try
        {
            latest = await GetLatestVersionAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Сервис версий недоступен: {ex.Message}");
            return 1;
        }

        var url = UseDelta ? latest.GarXMLDeltaURL : latest.GarXMLFullURL;
        if (string.IsNullOrWhiteSpace(url))
        {
            Log.Error("Сервис не вернул ссылку на выгрузку.");
            return 1;
        }

        Log.Info($"Актуальная версия: {latest.VersionId} ({latest.TextVersion})");

        Directory.CreateDirectory(OutputPath);
        var state = LoadState();
        if (state != null)
            Log.Info($"Локальная версия : {state.VersionId} (скачана {state.DownloadedAt:dd.MM.yyyy HH:mm})");
        else
            Log.Info("Локальная версия : нет (первый запуск)");

        if (state != null && state.VersionId >= latest.VersionId && !UseDelta)
        {
            Log.Ok("Обновление не требуется.");
            return 0;
        }

        if (checkOnly)
        {
            Log.Info($"Доступно обновление: {url}");
            return 0;
        }

        var suffix = UseDelta ? "delta" : "full";
        var targetFile = Path.Combine(OutputPath, $"gar_xml_{latest.VersionId}_{suffix}.zip");
        if (File.Exists(targetFile))
        {
            // Архив уже скачан, но version.json отстал (например, прерван прошлый запуск
            // между переименованием и записью состояния) — просто фиксируем состояние.
            Log.Skip($"Файл уже существует: {targetFile}");
            SaveState(latest, url, targetFile);
            return 0;
        }

        Log.Phase($"Скачивание версии {latest.VersionId}");
        Log.Info(url);
        try
        {
            await DownloadWithResumeAsync(url, targetFile);
        }
        catch (Exception ex)
        {
            Log.Error($"Скачивание не удалось: {ex.Message}");
            Log.Info("Частично скачанный «.part» сохранён — следующий запуск продолжит с места обрыва.");
            return 1;
        }

        SaveState(latest, url, targetFile);
        Log.Ok($"Готово: {targetFile}");

        // Старые версии не удаляем автоматически (архив мог ещё не переехать в контур) —
        // только напоминаем о них.
        var old = Directory.EnumerateFiles(OutputPath, "gar_xml_*.zip")
            .Where(f => !f.Equals(targetFile, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (old.Count > 0)
            Log.Info($"Старых версий в каталоге: {old.Count} — можно удалить после переноса новой в контур.");

        return 0;
    }

    // ── Configuration ──────────────────────────────────────────────────

    private static void LoadConfiguration()
    {
        var fullConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        var workRoot = WorkDir.GetRoot(fullConfig);
        var config = fullConfig.GetSection("GetGarData");

        OutputPath = WorkDir.Resolve(workRoot, config["OutputPath"] ?? OutputPath);
        UseDelta = bool.TryParse(config["Delta"], out var d) && d;
        if (int.TryParse(config["MaxAttempts"], out var ma) && ma > 0)
            MaxAttempts = ma;
    }

    // ── Version service ────────────────────────────────────────────────

    private sealed class DownloadInfo
    {
        public long VersionId { get; set; }
        public string? TextVersion { get; set; }
        public string? GarXMLFullURL { get; set; }
        public string? GarXMLDeltaURL { get; set; }
    }

    private static async Task<DownloadInfo> GetLatestVersionAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var json = await Http.GetStringAsync(ServiceUrl, cts.Token);
        return JsonSerializer.Deserialize<DownloadInfo>(json)
               ?? throw new InvalidOperationException("пустой ответ сервиса");
    }

    // ── Local state ────────────────────────────────────────────────────

    private sealed class State
    {
        public long VersionId { get; set; }
        public string? TextVersion { get; set; }
        public string? Url { get; set; }
        public string? File { get; set; }
        public DateTime DownloadedAt { get; set; }
    }

    private static State? LoadState()
    {
        var path = Path.Combine(OutputPath, StateFileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Warn($"Состояние {path} не читается ({ex.Message}) — считаем, что версий ещё нет.");
            return null;
        }
    }

    private static void SaveState(DownloadInfo info, string url, string file)
    {
        var path = Path.Combine(OutputPath, StateFileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new State
        {
            VersionId = info.VersionId,
            TextVersion = info.TextVersion,
            Url = url,
            File = file,
            DownloadedAt = DateTime.Now
        }, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    // ── Download with resume ───────────────────────────────────────────

    private static async Task DownloadWithResumeAsync(string url, string targetFile)
    {
        var partFile = targetFile + ".part";

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                long already = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (already > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(already, null);
                    Log.Info($"Докачка с {FmtSize(already)} (попытка {attempt}/{MaxAttempts})");
                }

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                // Сервер не принял Range (отдаёт 200 вместо 206) — начинаем файл заново.
                if (already > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    Log.Warn("Сервер не поддержал докачку — качаем файл сначала.");
                    already = 0;
                    File.Delete(partFile);
                }
                response.EnsureSuccessStatusCode();

                long total = already + (response.Content.Headers.ContentLength ?? -1);
                Log.Info($"Размер файла     : {(response.Content.Headers.ContentLength.HasValue ? FmtSize(total) : "неизвестен")}");

                await using (var src = await response.Content.ReadAsStreamAsync())
                await using (var dst = new FileStream(partFile, already > 0 ? FileMode.Append : FileMode.Create,
                                                      FileAccess.Write, FileShare.None, 1 << 20))
                {
                    await CopyWithProgressAsync(src, dst, already, total);
                }

                // Сверка размера: недокачанный файл не должен сойти за готовый.
                if (response.Content.Headers.ContentLength.HasValue
                    && new FileInfo(partFile).Length != total)
                {
                    throw new IOException(
                        $"размер не сошёлся: {new FileInfo(partFile).Length} из {total} байт");
                }

                File.Move(partFile, targetFile, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5);
                Log.Warn($"Попытка {attempt}/{MaxAttempts}: {ex.Message} — повтор через {delay.TotalSeconds:F0} с");
                await Task.Delay(delay);
            }
        }
    }

    private static async Task CopyWithProgressAsync(Stream src, Stream dst, long already, long total)
    {
        var buffer = new byte[1 << 20];
        long done = already;
        long nextReport = done + ReportEveryBytes;
        var started = DateTime.UtcNow;

        while (true)
        {
            // Таймаут на каждый кусок: мёртвое соединение обрывается и уходит на повтор
            // с докачкой, а не висит бесконечно (общий таймаут клиента отключён).
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            int read = await src.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token);
            done += read;

            if (done >= nextReport)
            {
                nextReport = done + ReportEveryBytes;
                var speed = (done - already) / Math.Max(1, (DateTime.UtcNow - started).TotalSeconds);
                var pct = total > 0 ? $" ({done * 100 / total} %)" : "";
                Log.Info($"  {FmtSize(done)}{pct}, {FmtSize((long)speed)}/с");
            }
        }
    }

    private const long ReportEveryBytes = 512L * 1024 * 1024;   // отчёт каждые 512 МБ

    private static string FmtSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} ГБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} МБ",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} КБ",
        _ => $"{bytes} Б"
    };
}
