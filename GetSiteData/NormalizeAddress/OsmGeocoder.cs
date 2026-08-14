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

        Load(db, "SELECT name_key, lat, lon FROM osm_places", _places);
        Load(db, "SELECT name_key, lat, lon FROM osm_streets", _streets);
        Log.Info($"Геокодер: {_places.Count:N0} имён НП, {_streets.Count:N0} имён улиц в памяти");
    }

    private static void Load(SqliteConnection db, string sql, Dictionary<string, List<(double, double)>> map)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = r.GetString(0);
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add((r.GetDouble(1), r.GetDouble(2)));
        }
    }

    private const double MaxAnchorKm = 150;

    /// <summary>Заполняет lat/lon адреса; anchor — координаты станции из документа (могут отсутствовать).</summary>
    public void Fill(StructuredAddress addr, double? anchorLat, double? anchorLon)
    {
        if (!IsAvailable) return;

        // Сначала точка населённого пункта — она же опора для выбора улицы-тёзки.
        (double Lat, double Lon)? placePt = null;
        var placeName = addr.City ?? addr.Settlement;
        if (placeName != null)
            placePt = Pick(_places, OsmImporter.NameKey(placeName), anchorLat, anchorLon);

        (double Lat, double Lon)? best = placePt;
        if (addr.Street != null)
        {
            // Улицу ищем относительно точки НП (или якоря-документа): улиц-тёзок много.
            var refLat = placePt?.Lat ?? anchorLat;
            var refLon = placePt?.Lon ?? anchorLon;
            var streetPt = Pick(_streets, OsmImporter.NameKey(addr.Street), refLat, refLon, maxKm: 50);
            if (streetPt != null) best = streetPt;
        }

        if (best != null)
        {
            addr.Lat = Math.Round(best.Value.Lat, 6);
            addr.Lon = Math.Round(best.Value.Lon, 6);
        }
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

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
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
