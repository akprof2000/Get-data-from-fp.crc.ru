// GetSiteData — первый этап пайплайна: помесячно скачивает страницы результатов
// поиска с реестра fp.crc.ru по набору поисковых терминов, режет каждую страницу
// на отдельные документы-заключения и складывает их в
// output/<термин>/<ГГГГ>/<ММ>/<типографский номер бланка>.html для последующего
// разбора утилитой ParseHTML.
//
// Настройки — в общем appsettings.json конвейера, секция «GetSiteData»:
//   Search:Terms       — массив терминов (включая частые опечатки слова «базовая»,
//                        встречающиеся в самом реестре);
//   Search:PeriodStart — начало сбора, «ММ.ГГГГ»;
//   Search:PeriodEnd   — конец сбора, «ММ.ГГГГ» (включительно; равен началу — один месяц).
//
// Помесячная фильтрация делается через октеты номера заключения в форме поиска сайта:
// text_n_char = месяц («08»), text_n_year = двузначный год («24») — они соответствуют
// «…Т.002218.08.24» в номере документа.
//
// Документы на странице разделены блоками «<hr size="1" width="100%"><b>4951.</b>»;
// именем файла становится «Номер заключения и дата»
// («77.01.09.000.Т.002236.06.26 от 08.06.2026» → одноимённый .html) — он есть у каждого
// документа; типографский номер бланка — запасной вариант (бывает не у всех).
// Уже существующие файлы пропускаются.
using Flurl;
using Flurl.Http;
using GetSiteData.Common;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

internal partial class Program
{
    // ── Конфигурация (значения по умолчанию перекрываются appsettings.json) ──
    private static string[] _searchTerms = ["базовая"];
    private static DateOnly _periodStart;
    private static DateOnly _periodEnd;
    private static string _outputPath = "output";
    private static int _resultsPerPage = 50;    // параметр rpp сайта
    private static int _parallelism = 4;        // одновременных запросов (бережём чужой сайт)
    private static int _maxAttempts = 5;        // максимум повторов одного запроса
    private static int _requestTimeoutSeconds = 60;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GetSiteData/1.0 (+research)";

    // Сайт отвечает в windows-1251.
    private static Encoding _win1251 = null!;

    // Начало блока документа в результатах: «<hr size="1" width="100%"><b>4951.</b>»
    [GeneratedRegex("<hr size=\"1\" width=\"100%\"><b>(\\d+)\\.</b>")]
    private static partial Regex DocumentMarkerRx();

    // Номер заключения и дата — основной идентификатор документа (есть у всех):
    // «<td class=w30r>Номер заключения и дата —&nbsp;</td> <td><b>77.01.09.000.Т.002236.06.26 от 08.06.2026</b>»
    [GeneratedRegex(@"Номер\s+заключения\s+и\s+дата[^<]*</td>\s*<td>\s*<b>\s*([^<]+?)\s*</b>", RegexOptions.IgnoreCase)]
    private static partial Regex ConclusionNumberRx();

    // Типографский номер бланка — запасной идентификатор, если номера заключения
    // вдруг нет: «<td class=w30r>Типографский номер бланка —&nbsp;</td> <td> <b>2607785</b> …»
    [GeneratedRegex(@"Типографский\s+номер\s+бланка[^<]*</td>\s*<td>\s*<b>\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BlankNumberRx();

    // Символы, недопустимые в имени файла Windows/Linux.
    [GeneratedRegex("[\\\\/:*?\"<>|\\r\\n\\t]+")]
    private static partial Regex InvalidFileCharsRx();

    private static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _win1251 = Encoding.GetEncoding("windows-1251");

        if (!LoadConfiguration())
        {
            return;
        }

        Log.Phase("Старт сбора данных с fp.crc.ru");
        Log.Info($"Термины : {string.Join(", ", _searchTerms)}");
        Log.Info($"Период  : {_periodStart:MM.yyyy} — {_periodEnd:MM.yyyy}");
        Log.Info($"Выходная директория: {_outputPath}");

        foreach (string searchTerm in _searchTerms)
        {
            Log.Phase($"Обработка термина: {searchTerm}");

            // Помесячный цикл: сайт фильтруется по октетам месяца/года номера заключения.
            for (DateOnly month = _periodStart; month <= _periodEnd; month = month.AddMonths(1))
            {
                try
                {
                    await ProcessMonthAsync(searchTerm, month);
                }
                catch (Exception ex)
                {
                    // Сбой одного месяца (после всех повторов) не должен прерывать остальные.
                    Log.Error($"{searchTerm} {month:yyyyMM}: месяц не обработан: {ex.Message}");
                }
            }
        }

        Log.Phase("Сбор завершён");
    }

    // ── Обработка одного месяца одного термина ─────────────────────────

    private static async Task ProcessMonthAsync(string searchTerm, DateOnly month)
    {
        // Раскладка: output/<термин>/<ГГГГ>/<ММ>/<типографский номер бланка>.html
        string outputPath = Path.Combine(_outputPath, searchTerm, month.ToString("yyyy"), month.ToString("MM"));
        _ = Directory.CreateDirectory(outputPath);

        // Первая страница нужна сразу — из неё узнаём общее число страниц.
        string initialResult = await FetchPageWithRetryAsync(searchTerm, month, 1, CancellationToken.None);
        int totalPages = ExtractTotalPages(initialResult);
        Log.Info($"{searchTerm} {month:yyyyMM}: всего страниц — {totalPages}");

        int saved = SaveDocuments(initialResult, outputPath);

        // Остальные страницы качаем параллельно с ограничением степени.
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _parallelism };
        int savedRest = 0;
        await Parallel.ForAsync(2, totalPages + 1, parallelOptions, async (pageIndex, cancellationToken) =>
        {
            string result = await FetchPageWithRetryAsync(searchTerm, month, pageIndex, cancellationToken);
            _ = Interlocked.Add(ref savedRest, SaveDocuments(result, outputPath));
        });

        Log.Ok($"{searchTerm} {month:yyyyMM}: сохранено документов — {saved + savedRest}");
    }

    // Режет страницу результатов на документы по маркерам «<hr…><b>N.</b>» и сохраняет
    // каждый как <номер заключения и дата>.html (запасной вариант — типографский номер
    // бланка). Возвращает число записанных (не пропущенных) файлов.
    private static int SaveDocuments(string pageHtml, string outputPath)
    {
        var markers = DocumentMarkerRx().Matches(pageHtml);
        if (markers.Count == 0)
        {
            return 0; // пустая страница (например, за месяц нет документов)
        }

        // Конец последнего документа — закрывающий тег таблицы результатов.
        int tableEnd = pageHtml.IndexOf("</table>", markers[^1].Index, StringComparison.OrdinalIgnoreCase);
        if (tableEnd < 0)
        {
            tableEnd = pageHtml.Length;
        }

        int saved = 0;
        for (int i = 0; i < markers.Count; i++)
        {
            // Фрагмент — от начала строки таблицы с маркером до следующего маркера.
            int start = pageHtml.LastIndexOf("<tr", markers[i].Index, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                start = markers[i].Index;
            }

            int end = i + 1 < markers.Count
                ? pageHtml.LastIndexOf("<tr", markers[i + 1].Index, StringComparison.OrdinalIgnoreCase)
                : tableEnd;
            if (end <= start)
            {
                end = i + 1 < markers.Count ? markers[i + 1].Index : tableEnd;
            }

            string fragment = pageHtml[start..end];

            // Имя файла — «Номер заключения и дата» (есть у каждого документа);
            // если его вдруг нет — типографский номер бланка (бывает не у всех).
            string? id = null;
            var conclusion = ConclusionNumberRx().Match(fragment);
            if (conclusion.Success)
            {
                id = HttpUtility.HtmlDecode(conclusion.Groups[1].Value).Trim();
            }
            else
            {
                var blank = BlankNumberRx().Match(fragment);
                if (blank.Success) id = blank.Groups[1].Value;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                // Совсем без идентификаторов документ не сохранить: сквозной индекс
                // выдачи в качестве имени не годится — он зависит от страницы.
                Log.Warn($"Документ №{markers[i].Groups[1].Value} без номера заключения и номера бланка — пропущен.");
                continue;
            }

            string filePath = Path.Combine(outputPath, $"{InvalidFileCharsRx().Replace(id, " ").Trim()}.html");
            if (File.Exists(filePath))
            {
                Log.Skip($"Файл {filePath} уже существует.");
                continue;
            }

            // Оборачиваем строки в таблицу, чтобы файл был самодостаточным HTML,
            // а ParseHTML видел привычную структуру «последняя таблица страницы».
            string document = $"<html><body><table>{fragment}</table></body></html>";
            File.WriteAllText(filePath, document, new UTF8Encoding(false));
            saved++;
        }

        return saved;
    }

    // ── Сетевые запросы ─────────────────────────────────────────────────

    // Скачивает одну страницу результатов, декодируя ответ как windows-1251.
    private static async Task<string> FetchPageAsync(string searchTerm, DateOnly month, int pageIndex)
    {
        byte[] bytes = await @"https://fp.crc.ru/doc/"
            .WithHeader("User-Agent", UserAgent)
            .WithTimeout(_requestTimeoutSeconds)
            .SetQueryParam("pg", pageIndex.ToString())
            .SetQueryParam("oper", "s")
            .SetQueryParam("rpp", _resultsPerPage.ToString())
            .SetQueryParam("type", "max")
            .SetQueryParam("text_prodnm", HttpUtility.UrlEncode(searchTerm, _win1251), true)
            // Помесячный фильтр: октеты месяца и года в номере заключения («…Т.002218.08.24»).
            .SetQueryParam("text_n_char", month.ToString("MM"))
            .SetQueryParam("text_n_year", month.ToString("yy"))
            .SetQueryParam("pril", "on")
            .SetQueryParam("use", "0")
            .GetBytesAsync();

        // Сайт не всегда указывает charset в Content-Type — декодируем явно.
        return _win1251.GetString(bytes);
    }

    // Скачивание с повторами и экспоненциальной задержкой.
    private static async Task<string> FetchPageWithRetryAsync(string searchTerm, DateOnly month, int pageIndex, CancellationToken cancellationToken)
    {
        int delaySeconds = 1;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Log.Info($"{searchTerm} {month:yyyyMM}: качаю страницу {pageIndex} (попытка {attempt}/{_maxAttempts})");
                return await FetchPageAsync(searchTerm, month, pageIndex);
            }
            catch (Exception ex) when (attempt < _maxAttempts)
            {
                Log.Warn($"{searchTerm} {month:yyyyMM}: ошибка на странице {pageIndex} (попытка {attempt}): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                delaySeconds = Math.Min(delaySeconds * 2, 30); // экспоненциальный backoff с потолком
            }
        }
    }

    // Извлекает общее число страниц из блока «Страницы (всего N)» первой страницы.
    private static int ExtractTotalPages(string result)
    {
        const string marker = "<hr size=\"1\"><br> Страницы (всего ";
        int markerIndex = result.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            // У выдачи в одну страницу блока «Страницы» нет — это штатная ситуация.
            return 1;
        }

        int startIndex = markerIndex + marker.Length;
        int closeIndex = result.IndexOf(')', startIndex);
        if (closeIndex <= startIndex)
        {
            Log.Warn("Блок числа страниц повреждён — считаем, что страница одна.");
            return 1;
        }

        string number = result[startIndex..closeIndex].Trim();
        return int.TryParse(number, out int total) && total > 0 ? total : 1;
    }

    // ── Конфигурация ────────────────────────────────────────────────────

    private static bool LoadConfiguration()
    {
        // Единый appsettings.json на весь конвейер — читаем СВОЮ секцию «GetSiteData».
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build()
            .GetSection("GetSiteData");

        string[] terms = [.. config.GetSection("Search:Terms").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()];
        if (terms.Length > 0)
        {
            _searchTerms = terms;
        }

        if (!TryParseMonth(config["Search:PeriodStart"], out _periodStart)
            || !TryParseMonth(config["Search:PeriodEnd"], out _periodEnd))
        {
            Log.Error("GetSiteData:Search:PeriodStart / PeriodEnd не заданы или не в формате «ММ.ГГГГ» — сбор невозможен.");
            return false;
        }

        if (_periodEnd < _periodStart)
        {
            Log.Error($"Конец периода ({_periodEnd:MM.yyyy}) раньше начала ({_periodStart:MM.yyyy}).");
            return false;
        }

        _outputPath = config["OutputPath"] ?? _outputPath;

        if (int.TryParse(config["Processing:ResultsPerPage"], out int rpp) && rpp > 0)
        {
            _resultsPerPage = rpp;
        }

        if (int.TryParse(config["Processing:Parallelism"], out int par) && par > 0)
        {
            _parallelism = par;
        }

        if (int.TryParse(config["Processing:MaxAttempts"], out int ma) && ma > 0)
        {
            _maxAttempts = ma;
        }

        if (int.TryParse(config["Processing:RequestTimeoutSeconds"], out int ts) && ts > 0)
        {
            _requestTimeoutSeconds = ts;
        }

        return true;
    }

    // «ММ.ГГГГ» → первый день месяца.
    private static bool TryParseMonth(string? value, out DateOnly month)
    {
        month = default;
        if (DateTime.TryParseExact(value, "MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
        {
            month = DateOnly.FromDateTime(dt);
            return true;
        }

        return false;
    }
}
