using GetSiteData.Common;
using Microsoft.Data.Sqlite;
using OsmSharp;
using OsmSharp.Streams;

namespace NormalizeAddress;

/// <summary>
/// Импорт OSM-выгрузки (*.osm.pbf) в таблицы геокодера внутри той же gar.sqlite:
///   osm_places  — населённые пункты (точки place=city/town/village/… с именем);
///   osm_streets — именованные улицы (координата середины линии highway).
/// Два прохода по файлу: PBF хранит сначала все точки, потом линии, поэтому
/// какие точки нужны улицам — известно только после чтения линий.
/// В самом ГАР координат нет — эти таблицы и есть офлайн-источник для address.lat/lon.
/// </summary>
public static class OsmImporter
{
    private static readonly HashSet<string> PlaceTypes =
        ["city", "town", "village", "hamlet", "suburb", "allotments", "isolated_dwelling", "farm", "locality"];

    // Улицы: только именованные проезжие типы (без троп и служебных).
    private static readonly HashSet<string> HighwayTypes =
        ["motorway", "trunk", "primary", "secondary", "tertiary", "unclassified",
         "residential", "living_street", "service", "road", "pedestrian", "track"];

    public static void Import(string pbfPath, string dbPath)
    {
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        Exec(db, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        Exec(db, """
            DROP TABLE IF EXISTS osm_places;
            DROP TABLE IF EXISTS osm_streets;
            CREATE TABLE osm_places(name_key TEXT NOT NULL, place TEXT NOT NULL,
                                    lat REAL NOT NULL, lon REAL NOT NULL);
            CREATE TABLE osm_streets(name_key TEXT NOT NULL,
                                     lat REAL NOT NULL, lon REAL NOT NULL);
            """);

        // Проход 1: точки-населённые пункты пишем сразу; у именованных улиц
        // запоминаем id СРЕДНЕЙ точки линии (координата «центра» улицы) — все
        // точки линии держать в памяти не нужно.
        var wantedNodes = new Dictionary<long, List<(string Key, int Slot)>>();
        var streets = new List<(string Key, double Lat, double Lon)>();
        long places = 0;

        using (var tx = db.BeginTransaction())
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO osm_places VALUES ($k, $p, $lat, $lon)";
            var pK = cmd.Parameters.Add("$k", SqliteType.Text);
            var pP = cmd.Parameters.Add("$p", SqliteType.Text);
            var pLat = cmd.Parameters.Add("$lat", SqliteType.Real);
            var pLon = cmd.Parameters.Add("$lon", SqliteType.Real);

            using var stream = File.OpenRead(pbfPath);
            var source = new PBFOsmStreamSource(stream);
            foreach (var element in source)
            {
                if (element is Node node)
                {
                    if (node.Tags == null || node.Latitude is not { } lat || node.Longitude is not { } lon) continue;
                    if (!node.Tags.TryGetValue("place", out var place) || !PlaceTypes.Contains(place)) continue;
                    if (!node.Tags.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)) continue;
                    pK.Value = NameKey(name);
                    pP.Value = place;
                    pLat.Value = lat;
                    pLon.Value = lon;
                    cmd.ExecuteNonQuery();
                    places++;
                }
                else if (element is Way way)
                {
                    if (way.Tags == null || way.Nodes == null || way.Nodes.Length == 0) continue;
                    if (!way.Tags.TryGetValue("highway", out var hw) || !HighwayTypes.Contains(hw)) continue;
                    if (!way.Tags.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)) continue;
                    var mid = way.Nodes[way.Nodes.Length / 2];
                    var slot = streets.Count;
                    streets.Add((NameKey(name), 0, 0));
                    if (!wantedNodes.TryGetValue(mid, out var list)) wantedNodes[mid] = list = [];
                    list.Add((NameKey(name), slot));
                }
            }
            tx.Commit();
        }
        Log.Info($"OSM: населённых пунктов {places:N0}, именованных улиц {streets.Count:N0}");

        // Проход 2: координаты средних точек улиц.
        using (var stream = File.OpenRead(pbfPath))
        {
            var source = new PBFOsmStreamSource(stream);
            foreach (var element in source)
            {
                if (element is not Node node) continue;
                if (!wantedNodes.TryGetValue(node.Id ?? 0, out var slots)) continue;
                if (node.Latitude is not { } lat || node.Longitude is not { } lon) continue;
                foreach (var (key, slot) in slots)
                    streets[slot] = (key, lat, lon);
            }
        }

        using (var tx = db.BeginTransaction())
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO osm_streets VALUES ($k, $lat, $lon)";
            var pK = cmd.Parameters.Add("$k", SqliteType.Text);
            var pLat = cmd.Parameters.Add("$lat", SqliteType.Real);
            var pLon = cmd.Parameters.Add("$lon", SqliteType.Real);
            long written = 0;
            foreach (var (key, lat, lon) in streets)
            {
                if (lat == 0 && lon == 0) continue;   // средняя точка не нашлась
                pK.Value = key; pLat.Value = lat; pLon.Value = lon;
                cmd.ExecuteNonQuery();
                written++;
            }
            tx.Commit();
            Log.Info($"OSM: улиц с координатами {written:N0}");
        }

        Exec(db, """
            CREATE INDEX ix_osm_places_name ON osm_places(name_key);
            CREATE INDEX ix_osm_streets_name ON osm_streets(name_key);
            """);
        Log.Info("OSM-геоданные готовы.");
    }

    /// <summary>Тот же ключ имени, что у матчера ГАР: нижний регистр, ё→е, слова по алфавиту.</summary>
    public static string NameKey(string name)
    {
        var lc = name.ToLowerInvariant().Replace('ё', 'е');
        var cleaned = new string(lc.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            // Родовые слова OSM-имён («улица Ленина» ↔ ГАР «Ленина») выбрасываем.
            .Where(w => w is not ("улица" or "переулок" or "проспект" or "шоссе" or "бульвар"
                        or "проезд" or "площадь" or "набережная" or "тракт" or "аллея" or "тупик"))
            .ToArray();
        Array.Sort(words, StringComparer.Ordinal);
        return string.Join(' ', words);
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
