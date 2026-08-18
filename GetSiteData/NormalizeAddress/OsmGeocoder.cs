using GetSiteData.Common;
using Microsoft.Data.Sqlite;

namespace NormalizeAddress;

/// <summary>
/// Офлайн-геокодер по OSM-таблицам в gar.sqlite: заполняет address.lat/lon
/// координатой найденного адресного объекта. Никаких сетевых запросов:
/// либо мгновенный поиск в памяти, либо поля остаются пустыми — конвейер
/// на геокодировании не задерживается.
/// Тёзки («село Александровка» есть в каждой области) разрешаются якорем —
/// координатами станции из самого документа: берётся кандидат ближе всех к якорю
/// и не дальше 150 км; без якоря координата ставится только при единственном
/// кандидате по стране.
/// </summary>
public sealed class OsmGeocoder
{
    private readonly Dictionary<string, List<(double Lat, double Lon)>> _places = [];
    private readonly Dictionary<string, List<(double Lat, double Lon)>> _streets = [];

    // Сетка ~5 км для обратного геокодирования: точка → ближайший НП/улица.
    private readonly Dictionary<(int, int), List<(string Key, double Lat, double Lon)>> _placeGrid = [];
    private readonly Dictionary<(int, int), List<(string Key, double Lat, double Lon)>> _streetGrid = [];

    private static (int, int) Cell(double lat, double lon) => ((int)(lat * 20), (int)(lon * 20));

    public bool IsAvailable { get; }

    public OsmGeocoder(string dbPath)
    {
        using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        db.Open();
        using (var chk = db.CreateCommand())
        {
            chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='osm_places'";
            IsAvailable = Convert.ToInt64(chk.ExecuteScalar()) > 0;
        }
        if (!IsAvailable)
        {
            Log.Info("OSM-геоданных в базе нет — координаты адресов не заполняются (см. import-osm).");
            return;
        }

        Load(db, "SELECT name_key, lat, lon FROM osm_places", _places, null);
        Load(db, "SELECT name_key, lat, lon FROM osm_streets", _streets, _streetGrid);
        // Обратный поиск НП — только по настоящим городам/посёлкам: микрорайоны и
        // урочища (suburb/locality/…) дают ложные «расхождения» внутри города.
        Load(db, "SELECT name_key, lat, lon FROM osm_places WHERE place IN ('city','town','village','hamlet')",
             [], _placeGrid);
        Log.Info($"Геокодер: {_places.Count:N0} имён НП, {_streets.Count:N0} имён улиц в памяти");
    }

    private static void Load(SqliteConnection db, string sql,
        Dictionary<string, List<(double, double)>> map,
        Dictionary<(int, int), List<(string, double, double)>>? grid)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = r.GetString(0);
            var lat = r.GetDouble(1);
            var lon = r.GetDouble(2);
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add((lat, lon));
            if (grid == null) continue;
            var cell = Cell(lat, lon);
            if (!grid.TryGetValue(cell, out var g)) grid[cell] = g = [];
            g.Add((key, lat, lon));
        }
    }

    /// <summary>
    /// Обратное геокодирование: ближайшие к точке населённый пункт и улица
    /// (ключи имён + дистанции в км). Поиск по клеткам сетки 3×3 (~15 км).
    /// </summary>
    public (string? PlaceKey, double PlaceKm, string? StreetKey, double StreetKm) Reverse(double lat, double lon)
    {
        var (pk, pd) = Nearest(_placeGrid, lat, lon);
        var (sk, sd) = Nearest(_streetGrid, lat, lon);
        return (pk, pd, sk, sd);
    }

    /// <summary>
    /// Минимальная дистанция от точки до любого НП с данным именем (все типы,
    /// включая городские районы — они тоже подтверждают адрес). Имени в OSM нет — null.
    /// </summary>
    public double? MinDistanceToNamedPlace(string nameKey, double lat, double lon)
    {
        if (nameKey.Length == 0 || !_places.TryGetValue(nameKey, out var pts)) return null;
        var best = double.MaxValue;
        foreach (var (la, lo) in pts)
            best = Math.Min(best, HaversineKm(lat, lon, la, lo));
        return best;
    }

    private static (string?, double) Nearest(
        Dictionary<(int, int), List<(string Key, double Lat, double Lon)>> grid, double lat, double lon)
    {
        var (cx, cy) = Cell(lat, lon);
        string? best = null;
        double bestKm = double.MaxValue;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy), out var list)) continue;
                foreach (var (key, la, lo) in list)
                {
                    var km = HaversineKm(lat, lon, la, lo);
                    if (km < bestKm) { bestKm = km; best = key; }
                }
            }
        return (best, bestKm);
    }

    private const double MaxAnchorKm = 150;

    /// <summary>Заполняет lat/lon адреса; anchor — координаты станции из документа (могут отсутствовать).</summary>
    public void Fill(StructuredAddress addr, double? anchorLat, double? anchorLon)
    {
        if (Locate(addr, anchorLat, anchorLon) is { } loc)
        {
            addr.Lat = Math.Round(loc.Lat, 6);
            addr.Lon = Math.Round(loc.Lon, 6);
            addr.GeoLevel = loc.Level;
        }
    }

    /// <summary>
    /// Геокодирует разобранный адрес БЕЗ записи: точка улицы (если нашлась рядом с НП)
    /// либо точка НП. Используется и для заполнения lat/lon, и для обратной сверки
    /// «адрес ↔ координаты станции».
    /// </summary>
    public (double Lat, double Lon, string Level)? Locate(StructuredAddress addr, double? anchorLat, double? anchorLon)
    {
        if (!IsAvailable) return null;

        // Сначала точка населённого пункта — она же опора для выбора улицы-тёзки.
        (double Lat, double Lon)? placePt = null;
        var placeName = addr.City ?? addr.Settlement;
        if (placeName != null)
            placePt = Pick(_places, OsmImporter.NameKey(placeName), anchorLat, anchorLon);

        if (addr.Street != null)
        {
            // Улицу ищем ТОЛЬКО при известной опоре (точка НП или якорь): без опоры
            // выбор из тёзок — лотерея, «единственная» кривая тёзка в OSM утаскивала
            // сверку за сотни км (02-БЦ-01, «Ленина ул.» в 513 км от Уфы).
            var refLat = placePt?.Lat ?? anchorLat;
            var refLon = placePt?.Lon ?? anchorLon;
            if (refLat != null
                && Pick(_streets, OsmImporter.NameKey(addr.Street), refLat, refLon, maxKm: 50) is { } streetPt)
                return (streetPt.Lat, streetPt.Lon, "улица");
        }
        return placePt is { } p ? (p.Lat, p.Lon, "нп") : null;
    }

    private static (double Lat, double Lon)? Pick(
        Dictionary<string, List<(double Lat, double Lon)>> map, string key,
        double? refLat, double? refLon, double maxKm = MaxAnchorKm)
    {
        if (key.Length == 0 || !map.TryGetValue(key, out var cands)) return null;
        if (refLat is null || refLon is null)
            return cands.Count == 1 ? cands[0] : null;   // без опоры тёзок не угадываем

        (double, double)? best = null;
        double bestKm = maxKm;
        foreach (var c in cands)
        {
            var km = HaversineKm(refLat.Value, refLon.Value, c.Lat, c.Lon);
            if (km < bestKm) { bestKm = km; best = c; }
        }
        return best;
    }

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
