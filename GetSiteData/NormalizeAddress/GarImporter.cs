using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using GetSiteData.Common;
using Microsoft.Data.Sqlite;

namespace NormalizeAddress;

/// <summary>
/// Импорт выгрузки ГАР (gar_xml.zip) в компактную SQLite-базу адресных объектов.
/// Берём только два вида файлов на регион: AS_ADDR_OBJ (объекты уровней «регион …
/// улица») и AS_ADM_HIERARCHY (административная иерархия «родитель-потомок»).
/// Дома (AS_HOUSES) не импортируются: это ~80 % объёма выгрузки, а номер дома
/// нормализация переносит в канонический адрес текстом как есть.
/// </summary>
public static partial class GarImporter
{
    // «01/AS_ADDR_OBJ_20260814_….XML» — именно объекты, не DIVISION/PARAMS/TYPES.
    [GeneratedRegex(@"^\d{2}/AS_ADDR_OBJ_\d{8}_", RegexOptions.IgnoreCase)]
    private static partial Regex AddrObjEntryRx();

    [GeneratedRegex(@"^\d{2}/AS_ADM_HIERARCHY_\d{8}_", RegexOptions.IgnoreCase)]
    private static partial Regex HierarchyEntryRx();

    [GeneratedRegex(@"^\d{2}/AS_HOUSES_\d{8}_", RegexOptions.IgnoreCase)]
    private static partial Regex HousesEntryRx();

    // Уровни ГАР: 1 — субъект, 2 — адм. район, 3 — МО, 4 — сельское/городское поселение,
    // 5 — город, 6 — населённый пункт, 7 — элемент планировочной структуры (СНТ и т.п.),
    // 8 — улица. Всё, что глубже (дома, квартиры, машино-места), не нужно.
    private const int MaxLevel = 8;

    /// <summary>Читает номер версии ГАР, записанный в базе (0 — версия неизвестна).</summary>
    public static long GetDbVersion(string dbPath)
    {
        if (!File.Exists(dbPath)) return 0;
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key='versionId'";
        try { return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); }
        catch (SqliteException) { return 0; }   // старая база без таблицы meta
    }

    public static string? GetMeta(string dbPath, string key)
    {
        if (!File.Exists(dbPath)) return null;
        using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        try { return cmd.ExecuteScalar() as string; }
        catch (SqliteException) { return null; }
    }

    public static void SetMeta(string dbPath, string key, string value)
    {
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        Exec(db, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT)");
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public static void SetDbVersion(string dbPath, long versionId)
    {
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        Exec(db, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT)");
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta VALUES ('versionId', $v)";
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Есть ли в базе дома (полная адресная книга).</summary>
    public static bool DbHasHouses(string dbPath)
    {
        if (!File.Exists(dbPath)) return false;
        using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='houses'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Режим переключили «с домами → без домов»: дома и их связи удаляются из
    /// существующей базы, остальное не трогается — пересборка не нужна.
    /// </summary>
    public static void RemoveHouses(string dbPath)
    {
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        Exec(db, """
            DELETE FROM hierarchy WHERE objectid IN (SELECT objectid FROM houses);
            DROP TABLE houses;
            VACUUM;
            """);
        Log.Info("Дома удалены из базы (адресные объекты и иерархия сохранены).");
    }

    /// <summary>
    /// Режим переключили «без домов → с домами»: в существующую базу ДОскачиваются
    /// только дома и их связи (адресные объекты не трогаются) — без полной пересборки.
    /// </summary>
    public static void AddHouses(Stream zipStream, string dbPath)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        Exec(db, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        Exec(db, """
            CREATE TABLE houses(
                objectid INTEGER PRIMARY KEY,
                guid     TEXT NOT NULL,
                housenum TEXT NOT NULL,
                region   INTEGER NOT NULL);
            """);

        var houseIds = new HashSet<long>();
        long houses = 0;
        foreach (var entry in zip.Entries.Where(e => HousesEntryRx().IsMatch(e.FullName)).OrderBy(e => e.FullName))
            houses += ImportHouses(db, entry, int.Parse(entry.FullName[..2]), houseIds);
        Log.Info($"Домов            : {houses:N0}");

        // Иерархию проходим повторно, но кладём только связи домов — прежние
        // связи адресных объектов уже в базе.
        long links = 0;
        foreach (var entry in zip.Entries.Where(e => HierarchyEntryRx().IsMatch(e.FullName)).OrderBy(e => e.FullName))
            links += ImportHierarchy(db, entry, houseIds);
        Log.Info($"Связей домов     : {links:N0}");
    }

    /// <summary>
    /// Применяет дельту ГАР (gar_delta_xml.zip) к существующей базе: активные записи
    /// перезаписываются, ставшие неактивными — удаляются. Формат файлов в дельте тот же,
    /// что в полной выгрузке, объём — мегабайты.
    /// </summary>
    public static void ApplyDelta(Stream zipStream, string dbPath)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        using var tx = db.BeginTransaction();

        long upserted = 0, deleted = 0, links = 0;
        // Сначала объекты (иерархия проверяет их наличие), затем связи.
        foreach (var entry in zip.Entries.Where(e => AddrObjEntryRx().IsMatch(e.FullName)))
        {
            using var xml = XmlReader.Create(entry.Open());
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element || xml.Name != "OBJECT") continue;
                var id = long.Parse(xml.GetAttribute("OBJECTID")!);
                var active = xml.GetAttribute("ISACTUAL") == "1" && xml.GetAttribute("ISACTIVE") == "1";
                var hasLevel = int.TryParse(xml.GetAttribute("LEVEL"), out var level) && level <= MaxLevel;
                if (active && hasLevel)
                {
                    using var cmd = db.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO addr_obj VALUES ($id, $guid, $name, $type, $level, $region)";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.Parameters.AddWithValue("$guid", xml.GetAttribute("OBJECTGUID") ?? "");
                    cmd.Parameters.AddWithValue("$name", xml.GetAttribute("NAME") ?? "");
                    cmd.Parameters.AddWithValue("$type", xml.GetAttribute("TYPENAME") ?? "");
                    cmd.Parameters.AddWithValue("$level", level);
                    cmd.Parameters.AddWithValue("$region", int.Parse(entry.FullName[..2]));
                    cmd.ExecuteNonQuery();
                    upserted++;
                }
                else if (!active)
                {
                    using var cmd = db.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM addr_obj WHERE objectid=$id; DELETE FROM hierarchy WHERE objectid=$id";
                    cmd.Parameters.AddWithValue("$id", id);
                    deleted += cmd.ExecuteNonQuery();
                }
            }
        }

        // Дома — только если включена полная адресная книга (таблица существует).
        bool hasHouses;
        using (var chk = db.CreateCommand())
        {
            chk.Transaction = tx;
            chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='houses'";
            hasHouses = Convert.ToInt64(chk.ExecuteScalar()) > 0;
        }
        long housesUpserted = 0;
        if (hasHouses)
        {
            foreach (var entry in zip.Entries.Where(e => HousesEntryRx().IsMatch(e.FullName)))
            {
                using var xml = XmlReader.Create(entry.Open());
                while (xml.Read())
                {
                    if (xml.NodeType != XmlNodeType.Element || xml.Name != "HOUSE") continue;
                    var id = long.Parse(xml.GetAttribute("OBJECTID")!);
                    var active = xml.GetAttribute("ISACTUAL") == "1" && xml.GetAttribute("ISACTIVE") == "1";
                    using var cmd = db.CreateCommand();
                    cmd.Transaction = tx;
                    if (active)
                    {
                        var num = string.Join(' ',
                            new[] { xml.GetAttribute("HOUSENUM"), xml.GetAttribute("ADDNUM1"), xml.GetAttribute("ADDNUM2") }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                        if (num.Length == 0) continue;
                        cmd.CommandText = "INSERT OR REPLACE INTO houses VALUES ($id, $guid, $num, $region)";
                        cmd.Parameters.AddWithValue("$id", id);
                        cmd.Parameters.AddWithValue("$guid", xml.GetAttribute("OBJECTGUID") ?? "");
                        cmd.Parameters.AddWithValue("$num", num);
                        cmd.Parameters.AddWithValue("$region", int.Parse(entry.FullName[..2]));
                        cmd.ExecuteNonQuery();
                        housesUpserted++;
                    }
                    else
                    {
                        cmd.CommandText = "DELETE FROM houses WHERE objectid=$id; DELETE FROM hierarchy WHERE objectid=$id";
                        cmd.Parameters.AddWithValue("$id", id);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        foreach (var entry in zip.Entries.Where(e => HierarchyEntryRx().IsMatch(e.FullName)))
        {
            using var xml = XmlReader.Create(entry.Open());
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element || xml.Name != "ITEM") continue;
                var id = long.Parse(xml.GetAttribute("OBJECTID")!);
                if (xml.GetAttribute("ISACTIVE") != "1") continue;
                if (!long.TryParse(xml.GetAttribute("PARENTOBJID"), out var parent)) continue;
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                // Только для объектов, которые есть в базе (при включённых домах — и для них).
                cmd.CommandText = hasHouses
                    ? """
                      INSERT OR REPLACE INTO hierarchy
                      SELECT $id, $parent WHERE EXISTS (SELECT 1 FROM addr_obj WHERE objectid=$id)
                         OR EXISTS (SELECT 1 FROM houses WHERE objectid=$id)
                      """
                    : """
                      INSERT OR REPLACE INTO hierarchy
                      SELECT $id, $parent WHERE EXISTS (SELECT 1 FROM addr_obj WHERE objectid=$id)
                      """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$parent", parent);
                links += cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        Log.Info($"  дельта: обновлено {upserted}, удалено {deleted}, домов {housesUpserted}, связей {links}");
    }

    public static void Import(Stream zipStream, string dbPath, bool includeHouses = false)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var addrEntries = zip.Entries.Where(e => AddrObjEntryRx().IsMatch(e.FullName))
                                     .OrderBy(e => e.FullName).ToList();
        var hierEntries = zip.Entries.Where(e => HierarchyEntryRx().IsMatch(e.FullName))
                                     .OrderBy(e => e.FullName).ToList();
        var houseEntries = includeHouses
            ? zip.Entries.Where(e => HousesEntryRx().IsMatch(e.FullName)).OrderBy(e => e.FullName).ToList()
            : [];
        Log.Info($"В архиве: {zip.Entries.Count} файлов; к импорту: " +
                 $"{addrEntries.Count} AS_ADDR_OBJ + {hierEntries.Count} AS_ADM_HIERARCHY" +
                 (includeHouses ? $" + {houseEntries.Count} AS_HOUSES (полная адресная книга)" : " (дома пропускаются)"));

        File.Delete(dbPath);
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        Exec(db, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        Exec(db, "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);");
        Exec(db, """
            CREATE TABLE addr_obj(
                objectid INTEGER PRIMARY KEY,
                guid     TEXT NOT NULL,
                name     TEXT NOT NULL,
                typename TEXT NOT NULL,
                level    INTEGER NOT NULL,
                region   INTEGER NOT NULL);
            CREATE TABLE hierarchy(
                objectid    INTEGER PRIMARY KEY,
                parentobjid INTEGER NOT NULL);
            """);

        // Проход 1: адресные объекты. Попутно копим их id — иерархию фильтруем по ним,
        // иначе (без IncludeHouses) в базу утекут строки про 40+ млн домов.
        var known = new HashSet<long>();
        long objects = 0;
        foreach (var entry in addrEntries)
        {
            var region = int.Parse(entry.FullName[..2]);
            objects += ImportAddrObjects(db, entry, region, known);
        }
        Log.Info($"Адресных объектов: {objects:N0}");

        if (includeHouses)
        {
            Exec(db, """
                CREATE TABLE houses(
                    objectid INTEGER PRIMARY KEY,
                    guid     TEXT NOT NULL,
                    housenum TEXT NOT NULL,
                    region   INTEGER NOT NULL);
                """);
            long houses = 0;
            foreach (var entry in houseEntries)
            {
                var region = int.Parse(entry.FullName[..2]);
                houses += ImportHouses(db, entry, region, known);
            }
            Log.Info($"Домов            : {houses:N0}");
        }

        long links = 0;
        foreach (var entry in hierEntries)
            links += ImportHierarchy(db, entry, known);
        Log.Info($"Связей иерархии  : {links:N0}");

        Exec(db, """
            CREATE INDEX ix_addr_name ON addr_obj(name COLLATE NOCASE);
            CREATE INDEX ix_addr_region_level ON addr_obj(region, level);
            CREATE INDEX ix_hier_parent ON hierarchy(parentobjid);
            """);
        Log.Info("Индексы построены.");
    }

    private static long ImportAddrObjects(SqliteConnection db, ZipArchiveEntry entry, int region, HashSet<long> known)
    {
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO addr_obj VALUES ($id, $guid, $name, $type, $level, $region)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pGuid = cmd.Parameters.Add("$guid", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pType = cmd.Parameters.Add("$type", SqliteType.Text);
        var pLevel = cmd.Parameters.Add("$level", SqliteType.Integer);
        var pRegion = cmd.Parameters.Add("$region", SqliteType.Integer);
        pRegion.Value = region;

        long count = 0;
        using var xml = XmlReader.Create(entry.Open());
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.Name != "OBJECT") continue;
            if (xml.GetAttribute("ISACTUAL") != "1" || xml.GetAttribute("ISACTIVE") != "1") continue;
            if (!int.TryParse(xml.GetAttribute("LEVEL"), out var level) || level > MaxLevel) continue;
            var id = long.Parse(xml.GetAttribute("OBJECTID")!);

            pId.Value = id;
            pGuid.Value = xml.GetAttribute("OBJECTGUID") ?? "";
            pName.Value = xml.GetAttribute("NAME") ?? "";
            pType.Value = xml.GetAttribute("TYPENAME") ?? "";
            pLevel.Value = level;
            cmd.ExecuteNonQuery();
            known.Add(id);
            count++;
        }
        tx.Commit();
        Log.Info($"  {entry.FullName}: {count:N0}");
        return count;
    }

    /// <summary>
    /// Дома (AS_HOUSES) — только при включённой полной адресной книге. Номер собирается
    /// из HOUSENUM и пристроек ADDNUM1/ADDNUM2 («12 к2 стр1»); тип пристройки в выгрузке
    /// задан кодом, для номера дома достаточно самих значений.
    /// </summary>
    private static long ImportHouses(SqliteConnection db, ZipArchiveEntry entry, int region, HashSet<long> known)
    {
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO houses VALUES ($id, $guid, $num, $region)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pGuid = cmd.Parameters.Add("$guid", SqliteType.Text);
        var pNum = cmd.Parameters.Add("$num", SqliteType.Text);
        var pRegion = cmd.Parameters.Add("$region", SqliteType.Integer);
        pRegion.Value = region;

        long count = 0;
        using var xml = XmlReader.Create(entry.Open());
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.Name != "HOUSE") continue;
            if (xml.GetAttribute("ISACTUAL") != "1" || xml.GetAttribute("ISACTIVE") != "1") continue;
            var num = string.Join(' ',
                new[] { xml.GetAttribute("HOUSENUM"), xml.GetAttribute("ADDNUM1"), xml.GetAttribute("ADDNUM2") }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            if (num.Length == 0) continue;
            var id = long.Parse(xml.GetAttribute("OBJECTID")!);

            pId.Value = id;
            pGuid.Value = xml.GetAttribute("OBJECTGUID") ?? "";
            pNum.Value = num;
            cmd.ExecuteNonQuery();
            known.Add(id);   // связи домов попадут в hierarchy
            count++;
        }
        tx.Commit();
        Log.Info($"  {entry.FullName}: {count:N0}");
        return count;
    }

    private static long ImportHierarchy(SqliteConnection db, ZipArchiveEntry entry, HashSet<long> known)
    {
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO hierarchy VALUES ($id, $parent)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pParent = cmd.Parameters.Add("$parent", SqliteType.Integer);

        long count = 0;
        using var xml = XmlReader.Create(entry.Open());
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.Name != "ITEM") continue;
            if (xml.GetAttribute("ISACTIVE") != "1") continue;
            var id = long.Parse(xml.GetAttribute("OBJECTID")!);
            if (!known.Contains(id)) continue;   // дома и прочие неимпортированные уровни
            if (!long.TryParse(xml.GetAttribute("PARENTOBJID"), out var parent)) continue;

            pId.Value = id;
            pParent.Value = parent;
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        Log.Info($"  {entry.FullName}: {count:N0}");
        return count;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
