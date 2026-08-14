namespace NormalizeAddress;

/// <summary>
/// Поток с произвольным доступом поверх HTTP Range-запросов. Позволяет открыть
/// ZipArchive прямо по URL и вытащить из 53-гигабайтной выгрузки ГАР только нужные
/// файлы (адресные объекты и иерархию), не скачивая дома — то есть единицы гигабайт
/// вместо всего архива, который к тому же не помещается на диск.
/// Чтение буферизовано кусками по 8 МБ: ZipArchive читает сжатый поток
/// последовательно, и почти все Read попадают в уже скачанный буфер.
/// </summary>
public sealed class HttpRangeStream : Stream
{
    // 32 МБ: чем крупнее кусок, тем меньше Range-запросов ловит сервер ФНС —
    // на частые мелкие он начинает отдавать по капле.
    private const int ChunkSize = 32 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly long _length;
    private long _position;

    private byte[] _buffer = [];
    private long _bufferStart = -1;

    public HttpRangeStream(HttpClient http, string url)
    {
        _http = http;
        _url = url;
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = _http.Send(head, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        _length = resp.Content.Headers.ContentLength
                  ?? throw new InvalidOperationException("сервер не сообщил размер файла");
        if (resp.Headers.AcceptRanges.Count == 0)
            throw new InvalidOperationException("сервер не поддерживает Range-запросы");
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length) return 0;

        if (_bufferStart < 0 || _position < _bufferStart || _position >= _bufferStart + _buffer.Length)
            FillBuffer(_position);

        var inBuffer = (int)(_position - _bufferStart);
        var available = _buffer.Length - inBuffer;
        var toCopy = Math.Min(count, available);
        Array.Copy(_buffer, inBuffer, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }

    private void FillBuffer(long from)
    {
        var to = Math.Min(from + ChunkSize, _length) - 1;
        // До пяти попыток на кусок: обрыв соединения не должен ронять весь импорт.
        // На каждый кусок — жёсткий таймаут: сервер ФНС при долгих сессиях начинает
        // отдавать по капле (20 КБ/с), и без таймаута чтение висело бы вечно;
        // переподключение возвращает нормальную скорость.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                using var req = new HttpRequestMessage(HttpMethod.Get, _url);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                using var resp = _http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                    throw new IOException($"ожидался 206 Partial Content, получен {(int)resp.StatusCode}");

                var expected = (int)(to - from + 1);
                var data = new byte[expected];
                using var body = resp.Content.ReadAsStream(cts.Token);
                int filled = 0;
                while (filled < expected)
                {
                    int read = body.ReadAsync(data, filled, expected - filled, cts.Token)
                                   .GetAwaiter().GetResult();
                    if (read == 0) throw new IOException($"поток оборвался на {filled} из {expected} байт");
                    filled += read;
                }
                _buffer = data;
                _bufferStart = from;
                TotalDownloaded += filled;
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromSeconds(5 * attempt));
            }
        }
    }

    /// <summary>Суммарно скачано байт (для итоговой статистики).</summary>
    public long TotalDownloaded { get; private set; }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
