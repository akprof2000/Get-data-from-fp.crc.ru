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
    // По умолчанию база живёт в data/ рядом с приложением — как модель классификатора:
    // это долгоживущий справочник, а не рабочие данные прогона, и чистка works
    // не должна его уносить (пересборка — 30+ ГБ трафика и часы).
    private static string DbPath = Path.Combine(AppContext.BaseDirectory, "data", "gar.sqlite");
    private static bool IncludeHouses;
    private static bool DeleteSourceAfterImport = true;
    private static string OsmSource = "https://download.geofabrik.de/russia-latest.osm.pbf";
    private static int OsmMaxAgeDays = 90;
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
            "import-osm" => RunImportOsm(args.Length > 1 ? args[1] : null),
            "fix-osm-keys" => RunFixOsmKeys(),
            _ => Usage()
        };
    }

    /// <summary>
    /// Миграция: пересчитывает ключи имён в OSM-таблицах текущим NameKey (идемпотентно).
    /// Нужна после обновлений нормализации ключей, чтобы не перекачивать выгрузку.
    /// </summary>
    private static int RunFixOsmKeys()
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}");
        db.Open();
        long total = 0;
        foreach (var table in (string[])["osm_places", "osm_streets"])
        {
            var updates = new List<(string Old, string New)>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = $"SELECT DISTINCT name_key FROM {table}";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var old = r.GetString(0);
                    var fixedKey = OsmImporter.NameKey(old);
                    if (fixedKey != old && fixedKey.Length > 0) updates.Add((old, fixedKey));
                }
            }
            using var tx = db.BeginTransaction();
            foreach (var (oldK, newK) in updates)
            {
                using var up = db.CreateCommand();
                up.Transaction = tx;
                up.CommandText = $"UPDATE {table} SET name_key=$n WHERE name_key=$o";
                up.Parameters.AddWithValue("$n", newK);
                up.Parameters.AddWithValue("$o", oldK);
                total += up.ExecuteNonQuery();
            }
            tx.Commit();
            Log.Info($"{table}: обновлено ключей {updates.Count}");
        }
        Log.Ok($"Строк переключено: {total:N0}");
        return 0;
    }

    /// <summary>
    /// Импорт OSM-выгрузки (*.osm.pbf) в геотаблицы базы: источник — аргумент команды,
    /// иначе ключ OsmSource (локальный файл или URL; полная Россия с Geofabrik — ~4 ГБ,
    /// можно и региональный срез для проверки). По URL файл скачивается во временное
    /// место рядом с базой и после укладки удаляется.
    /// </summary>
    private static int RunImportOsm(string? sourceArg)
    {
        var source = sourceArg ?? OsmSource;
        Log.Phase("Импорт OSM-геоданных");
        Log.Info($"Источник: {source}");
        if (!File.Exists(DbPath))
        {
            Log.Error("Сначала соберите базу ГАР (import/update) — OSM-таблицы добавляются в неё.");
            return 1;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string pbfPath;
            bool downloaded = false;
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                pbfPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DbPath))!, "osm_download.pbf");
                DownloadFile(source, pbfPath);
                downloaded = true;
            }
            else
            {
                pbfPath = source;
                if (!File.Exists(pbfPath)) { Log.Error($"Файл не найден: {pbfPath}"); return 1; }
            }

            OsmImporter.Import(pbfPath, DbPath);

            if ((downloaded || DeleteSourceAfterImport) && File.Exists(pbfPath))
            {
                File.Delete(pbfPath);
                Log.Info($"Исходный PBF удалён: {pbfPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Импорт OSM не удался: {ex.Message}");
            return 1;
        }
        GarImporter.SetMeta(DbPath, "osmImportedAt", DateTime.UtcNow.ToString("O"));
        Log.Ok($"Готово за {sw.Elapsed:hh\\:mm\\:ss}; база: {new FileInfo(DbPath).Length / (double)(1L << 20):F0} МБ");
        return 0;
    }

    private static void DownloadFile(string url, string target)
    {
        // Многопоточно с докачкой: одиночное соединение сервера бывает придушено
        // на порядок (Geofabrik), сегментное скачивание это обходит.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (!MultiPartDownloader.Download(http, url, target))
            throw new IOException("скачивание не завершилось — повторите запуск (докачка продолжит)");
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
        // Файл есть, но маркера готовности нет — параллельный import/update ещё пишет
        // базу (или прошлый оборвался). Ждём готовности, а не работаем с половиной данных.
        if (File.Exists(DbPath) && !GarImporter.IsDbReady(DbPath))
        {
            Log.Info("База ГАР ещё строится (нет маркера готовности) — жду завершения импорта…");
            var waited = TimeSpan.Zero;
            var step = TimeSpan.FromSeconds(30);
            while (waited < TimeSpan.FromHours(4) && File.Exists(DbPath) && !GarImporter.IsDbReady(DbPath))
            {
                Thread.Sleep(step);
                waited += step;
                if (waited.TotalMinutes % 10 < 0.5) Log.Info($"  всё ещё жду ({waited.TotalMinutes:F0} мин)…");
            }
            if (File.Exists(DbPath) && !GarImporter.IsDbReady(DbPath))
            {
                Log.Warn("База так и не достроилась — нормализация пропущена, перезапустите этап позже.");
                return 0;
            }
        }

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

        // Координаты адресов заполняются ТОЛЬКО в режиме полной адресной книги
        // (IncludeHouses + скачанные дома): в облегчённой базе адресная точность
        // не подтверждена домами, и геокодирование не выполняется вовсе.
        OsmGeocoder? geocoder = null;
        if (IncludeHouses && GarImporter.DbHasHouses(DbPath))
        {
            geocoder = new OsmGeocoder(DbPath);
            if (!geocoder.IsAvailable) geocoder = null;
        }
        else
        {
            Log.Info("Координаты адресов не заполняются: полная адресная книга (IncludeHouses) выключена.");
        }

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

            // Геокоординаты адресного объекта из OSM; якорь для тёзок — координаты
            // станции из документа.
            if (geocoder != null)
            {
                var (aLat, aLon) = ParseDocCoordinates(node["coordinates"]?.GetValue<string>());
                // Координаты адресного объекта — только когда адрес подтверждён до дома:
                // центроиды НП у недоразобранных адресов вводят в заблуждение (точка
                // станции и так лежит в поле coordinates).
                if (addr.MatchLevel == "дом")
                    geocoder.Fill(addr, aLat, aLon);

                // Улица не нашлась в ГАР, но осталась в extra и ПОДТВЕРЖДАЕТСЯ обратным
                // геокодом OSM у координат станции — доверяем документу (50-99-02:
                // «ул. Королева» в extra, та же улица в OSM в сотне метров).
                if (addr.Street == null && addr.Extra != null && aLat is { } sla && aLon is { } slo)
                {
                    var pm = System.Text.RegularExpressions.Regex.Match(addr.Extra,
                        @"(?:ул\.?|улица)\s*([А-ЯЁ][^,()]{2,40})", System.Text.RegularExpressions.RegexOptions.None);
                    if (pm.Success)
                    {
                        var streetName = pm.Groups[1].Value.Trim();
                        var rev2 = geocoder.Reverse(sla, slo);
                        if (rev2.StreetKey != null && rev2.StreetKm <= 1.0
                            && rev2.StreetKey == OsmImporter.NameKey(streetName))
                        {
                            addr.Street = "ул. " + streetName;
                            if (addr.MatchLevel is "территория" or "населённый пункт" or "город" or "район")
                                addr.MatchLevel = "улица";
                        }
                    }
                }

                // Обратная сверка: по координатам станции определяем фактические НП и
                // улицу; расходятся с разобранным адресом — добавляем addressByCoords
                // (совпадают — объект не пишется).
                if (aLat is { } la && aLon is { } lo)
                {
                    var byCoords = BuildAddressByCoords(matcher, geocoder, addr, la, lo);
                    if (byCoords != null) node["addressByCoords"] = byCoords;
                }
            }

            node["address"] = System.Text.Json.JsonSerializer.SerializeToNode(addr, AddrJsonOpts);

            var target = Path.Combine(OutputNormalizedPath, x.Rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, node.ToJsonString(AddrJsonOpts));

            switch (addr.MatchLevel)
            {
                case "дом": case "улица": Interlocked.Increment(ref matchedStreet); break;
                case "нет": Interlocked.Increment(ref none); break;
                case "регион": Interlocked.Increment(ref regionOnly); break;
                default: Interlocked.Increment(ref matchedPlace); break;
            }
            var n = Interlocked.Increment(ref done);
            if (n % 10000 == 0) Log.Info($"  {n:N0}/{files.Count:N0}");
        });

        Log.Phase("Готово:");
        Log.Info($"  До улицы/дома       : {matchedStreet:N0}");
        Log.Info($"  До района/города/тер: {matchedPlace:N0}");
        Log.Info($"  Только регион       : {regionOnly:N0}");
        Log.Info($"  Не сопоставлено     : {none:N0}");
        return 0;
    }

    // Пороги: адрес подтверждён, если одноимённый НП есть в этом радиусе от станции
    // (у городов центроид бывает далеко от окраинных вышек — радиус больше);
    // ближайший НП для объекта-подсказки ищем в разумном радиусе; улица — справочно.
    // Города — щедрый радиус: городские округа (Уфа, Пермь) тянутся на десятки км,
    // и вышка пригорода с адресом «г. Уфа» — не ошибка.
    private const double CityConfirmKm = 25.0;
    private const double SettlementConfirmKm = 8.0;
    // Порог расхождения, когда адрес геокодирован до улицы.
    private const double MismatchKm = 3.0;
    private const double PlaceProximityKm = 7.0;
    private const double StreetProximityKm = 0.4;

    /// <summary>
    /// «Адрес по координатам»: пишется ТОЛЬКО при реальном конфликте — населённый
    /// пункт из разобранного адреса не подтверждается координатами станции (ни одной
    /// одноимённой точки OSM в радиусе доверия) либо НП в адресе вовсе нет. Улицы в
    /// детекции не участвуют: «станция на соседней улице» — шум застройки, не ошибка.
    /// </summary>
    private static System.Text.Json.Nodes.JsonObject? BuildAddressByCoords(
        AddressMatcher matcher, OsmGeocoder geocoder, StructuredAddress addr, double lat, double lon)
    {
        var rev = geocoder.Reverse(lat, lon);
        if (rev.PlaceKey == null || rev.PlaceKm > PlaceProximityKm) return null;

        // Сверка «адрес ↔ координаты»: геокодируем разобранный адрес БЕЗ якоря станции
        // (с якорем тёзка выбралась бы поближе и сверка всегда сходилась бы) и меряем
        // расстояние до точки станции. Улица найдена — порог строгий (3 км); только
        // центроид НП — порог по типу (города растянуты на десятки км).
        var parsedPlaceKey = OsmImporter.NameKey(addr.City ?? addr.Settlement ?? "");
        if (parsedPlaceKey.Length == 0) return null;

        double? parsedKm = null;
        double threshold;
        if (geocoder.Locate(addr, null, null) is { } located && located.Level == "улица")
        {
            parsedKm = OsmGeocoder.HaversineKm(lat, lon, located.Lat, located.Lon);
            threshold = MismatchKm;
        }
        else
        {
            // Улица не геокодировалась — сверяем по ближайшей одноимённой точке НП.
            parsedKm = geocoder.MinDistanceToNamedPlace(parsedPlaceKey, lat, lon);
            if (parsedKm == null) return null;   // НП нет в OSM — не судим
            threshold = addr.City != null ? CityConfirmKm : SettlementConfirmKm;
        }
        if (parsedKm <= threshold) return null;
        // Экстремальные расстояния (>300 км) — шум сверочного геокода: одноимённой
        // точки в OSM поблизости просто нет, и «ближайшая» тёзка нашлась в другом
        // регионе. Это не признак неверного адреса — не пишем (57-01-04: 1849 км).
        if (parsedKm > 300) return null;
        // Разобранный адрес дальше порога от станции — расхождение фиксируем.

        var derivedStreetKey = rev.StreetKey != null && rev.StreetKm <= StreetProximityKm ? rev.StreetKey : null;
        var (canonPlace, canonStreet) = matcher.CanonicalNames(addr.RegionCode ?? 0, rev.PlaceKey, derivedStreetKey);
        var obj = new System.Text.Json.Nodes.JsonObject
        {
            ["place"] = canonPlace ?? rev.PlaceKey,
            ["placeDistanceKm"] = Math.Round(rev.PlaceKm, 2)
        };
        if (derivedStreetKey != null) obj["street"] = canonStreet ?? derivedStreetKey;
        if (parsedKm is { } pk) obj["parsedAddressDistanceKm"] = Math.Round(pk, 2);
        obj["source"] = "osm";
        return obj;
    }

    private static (double? Lat, double? Lon) ParseDocCoordinates(string? coords)
    {
        if (string.IsNullOrWhiteSpace(coords)) return (null, null);
        var parts = coords.Split(',');
        if (parts.Length != 2) return (null, null);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, inv, out var lat)
            && double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, inv, out var lon))
            return (lat, lon);
        return (null, null);
    }

    private static readonly System.Text.Json.JsonSerializerOptions AddrJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Диагностика: выполнить SQL к базе ГАР и напечатать строки (если есть).</summary>
    private static int RunSql(string query)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}");
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
            RefreshOsmIfStale();
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
            if (rc == 0)
            {
                GarImporter.SetDbVersion(DbPath, latest.VersionId);
                // OSM-геоданные нужны и после полной пересборки, не только в ветках
                // «актуально»/«дельты» (при пересборке старые osm-таблицы стираются).
                RefreshOsmIfStale();
            }
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
        RefreshOsmIfStale();
        return 0;
    }

    /// <summary>
    /// Автообновление OSM-геоданных: в режиме полной адресной книги, если геотаблицы
    /// отсутствуют или старше OsmMaxAgeDays, переимпортирует их из OsmSource
    /// (у Geofabrik выгрузки обновляются ежедневно, версионного API нет — поэтому
    /// критерий давности, дата хранится в meta).
    /// </summary>
    private static void RefreshOsmIfStale()
    {
        if (!IncludeHouses || !GarImporter.DbHasHouses(DbPath)) return;
        try
        {
            var importedAt = GarImporter.GetMeta(DbPath, "osmImportedAt");
            if (importedAt != null
                && DateTime.TryParse(importedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                && (DateTime.UtcNow - dt).TotalDays < OsmMaxAgeDays)
            {
                Log.Info($"OSM-геоданные свежие ({dt:dd.MM.yyyy}) — обновление не требуется.");
                return;
            }
            Log.Info(importedAt == null
                ? "OSM-геоданных нет — импортирую."
                : $"OSM-геоданные старше {OsmMaxAgeDays} дней — обновляю.");
            if (RunImportOsm(null) == 0)
                GarImporter.SetMeta(DbPath, "osmImportedAt", DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Обновление OSM-геоданных не удалось: {ex.Message} — работаем с прежними.");
        }
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
        // Явно заданный DbPath уважает WorkRoot (прежнее поведение);
        // без настройки — data/ рядом с приложением.
        if (config["DbPath"] is { Length: > 0 } dbp)
            DbPath = WorkDir.Resolve(workRoot, dbp);
        IncludeHouses = bool.TryParse(config["IncludeHouses"], out var ih) && ih;
        if (bool.TryParse(config["DeleteSourceAfterImport"], out var ds))
            DeleteSourceAfterImport = ds;
        OsmSource = config["OsmSource"] ?? OsmSource;
        if (int.TryParse(config["OsmMaxAgeDays"], out var oma) && oma > 0)
            OsmMaxAgeDays = oma;
        InputJsonPath = WorkDir.Resolve(workRoot, config["InputJsonPath"] ?? InputJsonPath);
        OutputNormalizedPath = WorkDir.Resolve(workRoot, config["OutputNormalizedPath"] ?? OutputNormalizedPath);
    }
}
