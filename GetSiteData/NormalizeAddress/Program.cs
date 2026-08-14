// NormalizeAddress — нормализация адресов «под одну гребёнку» по офлайн-базе ГАР.
//
// Команды:
//   import  — конвертирует выгрузку ГАР (gar_xml.zip) в компактную SQLite-базу
//             (works/gar/gar.sqlite). Источник: локальный файл ИЛИ прямой URL —
//             по URL архив читается Range-запросами, и скачиваются только нужные
//             файлы (адресные объекты + иерархия, единицы ГБ из 53 ГБ архива).
//   (этап normalize — следующий шаг, будет добавлен отдельно)
//
// Настройки — секция «NormalizeAddress» общего appsettings.json:
//   GarSource — путь к gar_xml.zip или https-URL выгрузки;
//   DbPath    — путь к SQLite-базе (относительный — под WorkRoot).
using GetSiteData.Common;
using Microsoft.Extensions.Configuration;

namespace NormalizeAddress;

public class Program
{
    private static string GarSource = "";
    private static string DbPath = Path.Combine("gar", "gar.sqlite");
    private static bool IncludeHouses;
    private static bool DeleteSourceAfterImport = true;
    private static string InputJsonPath = "OutputJson";
    private static string OutputNormalizedPath = "OutputNormalized";

    public static int Main(string[] args)
    {
        LoadConfiguration();

        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
        return command switch
        {
            "import" => RunImport(),
            "update" => RunUpdate(),
            "set-version" when args.Length > 1 && long.TryParse(args[1], out var v) => RunSetVersion(v),
            "sql" when args.Length > 1 => RunSql(args[1]),
            "normalize" => RunNormalize(),
            _ => Usage()
        };
    }

    /// <summary>
    /// Обогащение готовых JSON структурированным адресом: каждый документ из InputJsonPath
    /// получает объект «address» (адрес по частям из ГАР + неадресные приметы места в extra)
    /// и записывается в OutputNormalizedPath тем же относительным путём. Уже обработанные
    /// файлы пропускаются — этап инкрементален и перезапускаем.
    /// </summary>
    private static int RunNormalize()
    {
        Log.Phase("Нормализация адресов по ГАР");
        Log.Info($"Вход : {InputJsonPath}");
        Log.Info($"Выход: {OutputNormalizedPath}");

        // Базы нет — не падаем, а сначала собираем её сами (update выберет полную
        // выгрузку). Не получилось (нет интернета и файла выгрузки — закрытый контур
        // без занесённого архива) — этап честно пропускается, конвейер продолжается.
        if (!File.Exists(DbPath))
        {
            Log.Info($"Базы ГАР нет ({DbPath}) — пробую собрать автоматически.");
            try { RunUpdate(); }
            catch (Exception ex) { Log.Warn($"Автосборка базы не удалась: {ex.Message}"); }
            if (!File.Exists(DbPath))
            {
                Log.Warn("База ГАР недоступна — нормализация пропущена. " +
                         "Занесите выгрузку (GarSource) и выполните import, либо дайте доступ к сервису ФНС.");
                return 0;
            }
        }
        else
        {
            // Смена режима «с домами/без домов» подхватывается и здесь.
            try { EnsureHousesMode(); }
            catch (Exception ex) { Log.Warn($"Согласование режима домов не удалось: {ex.Message}"); }
        }
        var matcher = new AddressMatcher(DbPath);

        var files = Directory.EnumerateFiles(InputJsonPath, "*.json", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_processed.json"))
            .Select(f => (Src: f, Rel: Path.GetRelativePath(InputJsonPath, f)))
            .Where(x => !File.Exists(Path.Combine(OutputNormalizedPath, x.Rel)))
            .ToList();
        Log.Info($"К обработке: {files.Count:N0}");

        long done = 0, matchedStreet = 0, matchedPlace = 0, regionOnly = 0, none = 0;
        Parallel.ForEach(files, x =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(x.Src))!;
            var raw = node["baseStationAddress"]?.GetValue<string>();
            var addr = string.IsNullOrWhiteSpace(raw) ? new StructuredAddress() : matcher.Match(raw);
            node["address"] = System.Text.Json.JsonSerializer.SerializeToNode(addr, AddrJsonOpts);

            var target = Path.Combine(OutputNormalizedPath, x.Rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, node.ToJsonString(AddrJsonOpts));

            switch (addr.MatchLevel)
            {
                case "street": Interlocked.Increment(ref matchedStreet); break;
                case "none": Interlocked.Increment(ref none); break;
                case "region": Interlocked.Increment(ref regionOnly); break;
                default: Interlocked.Increment(ref matchedPlace); break;
            }
            var n = Interlocked.Increment(ref done);
            if (n % 10000 == 0) Log.Info($"  {n:N0}/{files.Count:N0}");
        });

        Log.Phase("Готово:");
        Log.Info($"  До улицы            : {matchedStreet:N0}");
        Log.Info($"  До района/города/тер: {matchedPlace:N0}");
        Log.Info($"  Только регион       : {regionOnly:N0}");
        Log.Info($"  Не сопоставлено     : {none:N0}");
        return 0;
    }

    private static readonly System.Text.Json.JsonSerializerOptions AddrJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Диагностика: выполнить SELECT к базе ГАР и напечатать строки.</summary>
    private static int RunSql(string query)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = query;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            Console.WriteLine(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.GetValue(i))));
        return 0;
    }

    private static int Usage()
    {
        Log.Info("Использование: NormalizeAddress <команда>");
        Log.Info("  import           — полная конвертация выгрузки ГАР (NormalizeAddress:GarSource) в SQLite");
        Log.Info("  update           — САМА решает, что нужно: ничего / применить дельты / полная пересборка.");
        Log.Info("                     Сравнивает версию базы с сервисом ФНС; годится для запуска по расписанию.");
        Log.Info("  set-version <id> — записать номер версии в базу (после ручного import)");
        return 1;
    }

    private static int RunSetVersion(long versionId)
    {
        GarImporter.SetDbVersion(DbPath, versionId);
        Log.Ok($"Версия базы: {versionId}");
        return 0;
    }

    /// <summary>
    /// Согласует базу с настройкой IncludeHouses: выключили дома — лишнее удаляется
    /// из базы (быстро, без пересборки); включили — ДОскачиваются только дома и их
    /// связи. Что не менялось (адресные объекты, иерархия НП/улиц) — не трогается.
    /// </summary>
    private static void EnsureHousesMode()
    {
        if (!File.Exists(DbPath)) return;
        var dbHasHouses = GarImporter.DbHasHouses(DbPath);
        if (dbHasHouses == IncludeHouses) return;

        if (!IncludeHouses)
        {
            Log.Info("Режим сменился: дома выключены — удаляю их из базы.");
            GarImporter.RemoveHouses(DbPath);
            return;
        }

        Log.Info("Режим сменился: дома включены — доскачиваю только дома и их связи.");
        if (GarSource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            GarImporter.AddHouses(new HttpRangeStream(http, GarSource), DbPath);
        }
        else if (File.Exists(GarSource))
        {
            using var file = File.OpenRead(GarSource);
            GarImporter.AddHouses(file, DbPath);
        }
        else
        {
            Log.Warn($"Выгрузка недоступна ({GarSource}) — дома добавить не из чего, база остаётся без них.");
        }
    }

    // ── Автообновление ─────────────────────────────────────────────────

    private const string AllVersionsUrl = "https://fias.nalog.ru/WebServices/Public/GetAllDownloadFileInfo";

    private sealed class VersionInfo
    {
        public long VersionId { get; set; }
        public string? GarXMLFullURL { get; set; }
        public string? GarXMLDeltaURL { get; set; }
    }

    // Больше стольких дельт подряд не применяем — полная пересборка быстрее и надёжнее.
    private const int MaxDeltasToApply = 45;

    /// <summary>
    /// Автоматически отслеживает необходимость обновления базы: сравнивает версию в
    /// meta-таблице с актуальной на сервисе ФНС и сам выбирает путь — «уже актуально»,
    /// последовательное применение дельт (мегабайты) или полная пересборка (когда базы
    /// нет, версия неизвестна или отставание слишком велико).
    /// </summary>
    private static int RunUpdate()
    {
        Log.Phase("Проверка актуальности базы ГАР");
        try { EnsureHousesMode(); }
        catch (Exception ex) { Log.Warn($"Согласование режима домов не удалось: {ex.Message}"); }
        var current = GarImporter.GetDbVersion(DbPath);
        Log.Info($"Версия базы   : {(current > 0 ? current : "неизвестна (базы нет или собрана вручную)")}");

        List<VersionInfo> versions;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            var json = http.GetStringAsync(AllVersionsUrl).GetAwaiter().GetResult();
            versions = System.Text.Json.JsonSerializer.Deserialize<List<VersionInfo>>(json) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Сервис версий ФНС недоступен: {ex.Message}");
            // Закрытый контур: базы нет, но выгрузка занесена локальным файлом —
            // собираем из него, версию оставляем неизвестной (проставьте set-version).
            if (current == 0 && !GarSource.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && File.Exists(GarSource))
            {
                Log.Info($"Собираю базу из локальной выгрузки: {GarSource}");
                return RunImport();
            }
            return 1;
        }
        if (versions.Count == 0) { Log.Error("Сервис вернул пустой список версий."); return 1; }

        var latest = versions.MaxBy(v => v.VersionId)!;
        Log.Info($"Актуальная    : {latest.VersionId}");

        if (current >= latest.VersionId)
        {
            Log.Ok("База актуальна — обновление не требуется.");
            return 0;
        }

        var deltas = versions.Where(v => v.VersionId > current)
                             .OrderBy(v => v.VersionId).ToList();
        var canDelta = current > 0
                       && deltas.Count <= MaxDeltasToApply
                       && deltas.All(d => !string.IsNullOrEmpty(d.GarXMLDeltaURL));

        if (!canDelta)
        {
            Log.Info(current == 0
                ? "Версия базы неизвестна — полная пересборка."
                : $"Отставание {deltas.Count} версий — полная пересборка выгоднее дельт.");
            GarSource = latest.GarXMLFullURL ?? GarSource;
            var rc = RunImport();
            if (rc == 0) GarImporter.SetDbVersion(DbPath, latest.VersionId);
            return rc;
        }

        Log.Info($"К применению дельт: {deltas.Count}");
        foreach (var d in deltas)
        {
            Log.Info($"Дельта {d.VersionId}: {d.GarXMLDeltaURL}");
            try
            {
                // Дельты маленькие (единицы-десятки МБ) — качаем целиком в память.
                var bytes = http.GetByteArrayAsync(d.GarXMLDeltaURL!).GetAwaiter().GetResult();
                using var ms = new MemoryStream(bytes);
                GarImporter.ApplyDelta(ms, DbPath);
                GarImporter.SetDbVersion(DbPath, d.VersionId);
            }
            catch (Exception ex)
            {
                // База осталась на последней успешно применённой версии —
                // следующий запуск продолжит с неё же.
                Log.Error($"Дельта {d.VersionId} не применилась: {ex.Message}");
                return 1;
            }
        }

        Log.Ok($"База обновлена до версии {latest.VersionId}.");
        return 0;
    }

    private static int RunImport()
    {
        Log.Phase("Импорт ГАР → SQLite");
        Log.Info($"Источник: {GarSource}");
        Log.Info($"База    : {DbPath}");

        if (string.IsNullOrWhiteSpace(GarSource))
        {
            Log.Error("NormalizeAddress:GarSource не задан (путь к gar_xml.zip или URL).");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DbPath))!);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (GarSource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var stream = new HttpRangeStream(http, GarSource);
                Log.Info($"Архив по HTTP: {stream.Length / (1L << 30):N1} ГБ (скачаются только нужные файлы)");
                Log.Info($"Дома (полная адресная книга): {(IncludeHouses ? "ВКЛЮЧЕНЫ — скачивание вырастет на десятки ГБ" : "выключены")}");
                GarImporter.Import(stream, DbPath, IncludeHouses);
                Log.Info($"Скачано по факту: {stream.TotalDownloaded / (double)(1L << 30):F1} ГБ");
            }
            else
            {
                using (var file = File.OpenRead(GarSource))
                    GarImporter.Import(file, DbPath, IncludeHouses);
                // Скачанный архив после укладки в базу больше не нужен — удаляем,
                // чтобы не держать десятки ГБ. Отключается ключом DeleteSourceAfterImport
                // (например, если тот же файл ещё нужен для включения домов).
                if (DeleteSourceAfterImport)
                {
                    File.Delete(GarSource);
                    Log.Info($"Исходный архив удалён: {GarSource}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Импорт не удался: {ex.Message}");
            return 1;
        }

        Log.Ok($"Готово за {sw.Elapsed:hh\\:mm\\:ss}; база: {new FileInfo(DbPath).Length / (double)(1L << 20):F0} МБ");
        return 0;
    }

    private static void LoadConfiguration()
    {
        var fullConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        var workRoot = WorkDir.GetRoot(fullConfig);
        var config = fullConfig.GetSection("NormalizeAddress");

        GarSource = config["GarSource"] ?? GarSource;
        DbPath = WorkDir.Resolve(workRoot, config["DbPath"] ?? DbPath);
        IncludeHouses = bool.TryParse(config["IncludeHouses"], out var ih) && ih;
        if (bool.TryParse(config["DeleteSourceAfterImport"], out var ds))
            DeleteSourceAfterImport = ds;
        InputJsonPath = WorkDir.Resolve(workRoot, config["InputJsonPath"] ?? InputJsonPath);
        OutputNormalizedPath = WorkDir.Resolve(workRoot, config["OutputNormalizedPath"] ?? OutputNormalizedPath);
    }
}
