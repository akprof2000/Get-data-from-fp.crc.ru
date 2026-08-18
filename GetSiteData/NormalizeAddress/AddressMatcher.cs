using GetSiteData.Common;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace NormalizeAddress;

/// <summary>
/// Сопоставляет сырой адрес из документа с иерархией ГАР: регион → район/МО →
/// город/населённый пункт → планировочная структура → улица. База целиком
/// поднимается в память (1,6 млн объектов, ~250 МБ) — на прогоне в 141 тысячу
/// адресов SQL-запросы на каждый сегмент были бы на порядки медленнее.
/// Сопоставление регистронезависимое (свой ToLowerInvariant — SQLite кириллицу
/// в NOCASE не умеет) и словопорядко-независимое («ул. 3-я линия» = «Линия 3-я ул.»).
/// </summary>
public sealed partial class AddressMatcher
{
    private sealed record Node(long Id, string Guid, string Name, string Type, int Level, int Region, long Parent);

    private readonly Dictionary<long, Node> _nodes = [];
    // Ключ поиска: (регион, уровень-группа, нормализованное имя) → кандидаты.
    // Уровень-группы: 1 — регион, 2..3 — район/МО, 4..6 — город/НП, 7 — тер., 8 — улица.
    private readonly Dictionary<(int Region, int Group, string Key), List<Node>> _index = [];
    private readonly Dictionary<string, Node> _regionsByKey = [];
    // Районы по имени БЕЗ привязки к региону: адреса без региона («Майкопский район,
    // п. Удобный…») восстанавливают регион по уникальному имени района.
    private readonly Dictionary<string, List<Node>> _districtsByKey = [];
    private readonly Dictionary<int, Node> _regionByCode = [];
    private readonly Dictionary<string, List<Node>> _citiesByKey = [];

    // Открытое readonly-соединение для точечных запросов домов: 34 млн домов в память
    // не поднять, а по индексу иерархии выборка домов одной улицы мгновенна.
    private readonly SqliteConnection? _housesDb;

    public AddressMatcher(string dbPath)
    {
        using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        db.Open();
        using (var chk = db.CreateCommand())
        {
            chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='houses'";
            if (Convert.ToInt64(chk.ExecuteScalar()) > 0)
            {
                _housesDb = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                _housesDb.Open();
            }
        }
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT a.objectid, a.guid, a.name, a.typename, a.level, a.region,
                   COALESCE(h.parentobjid, 0)
            FROM addr_obj a LEFT JOIN hierarchy h ON h.objectid = a.objectid
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var n = new Node(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                             r.GetInt32(4), r.GetInt32(5), r.GetInt64(6));
            _nodes[n.Id] = n;
            var key = NormalizeName(n.Name);
            if (n.Level == 1)
            {
                _regionsByKey.TryAdd(key, n);
                _regionByCode.TryAdd(n.Region, n);
                // Регион ищется и по «опорному» слову: «Башкортостан» из «Республика
                // Башкортостан», «Саха» и «Якутия» из «Саха /Якутия/».
                foreach (var word in SignificantWords(n.Name))
                    _regionsByKey.TryAdd(word, n);
            }
            else
            {
                var group = LevelGroup(n.Level);
                var k = (n.Region, group, key);
                if (!_index.TryGetValue(k, out var list)) _index[k] = list = [];
                list.Add(n);
                if (group == 2)
                {
                    if (!_districtsByKey.TryGetValue(key, out var dl)) _districtsByKey[key] = dl = [];
                    dl.Add(n);
                }
                // Только города (уровень 5): сёл-тёзок по стране тысячи, а имя города
                // почти всегда уникально — по нему восстанавливаем регион. После
                // муниципальной реформы часть городов лежит на уровне 2 (Нижний
                // Новгород) — их тоже учитываем.
                if (n.Level == 5 || (n.Level == 2 && TypeKey(n.Type) == "г"))
                {
                    if (!_citiesByKey.TryGetValue(key, out var cl)) _citiesByKey[key] = cl = [];
                    cl.Add(n);
                }
            }
        }
        Log.Info($"Матчер: {_nodes.Count:N0} объектов ГАР в памяти");
    }

    private static int LevelGroup(int level) => level switch
    {
        <= 3 => 2,      // район / муниципальный округ
        <= 6 => 4,      // город / посёлок / населённый пункт
        7 => 7,         // планировочная структура (СНТ, тер.)
        _ => 8          // улица
    };

    // ── Нормализация имён ──────────────────────────────────────────────

    [GeneratedRegex(@"[^а-яё0-9\s-]", RegexOptions.IgnoreCase)]
    private static partial Regex JunkCharsRx();

    /// <summary>«3-я Линия» и «линия 3-я» дают один ключ: слова сортируются.</summary>
    private static string NormalizeName(string name)
    {
        var lc = name.ToLowerInvariant().Replace('ё', 'е');
        // Латинские буквы-омографы (OCR-мешанина «райoн», «c. Шипуново») приводим
        // к кириллице — иначе ключи не совпадают с индексом (22-01-46).
        lc = ReplaceLatinHomoglyphs(lc);
        lc = JunkCharsRx().Replace(lc, " ");
        var words = lc.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(words, StringComparer.Ordinal);
        return string.Join(' ', words);
    }

    private static string ReplaceLatinHomoglyphs(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        for (int i = 0; i < s.Length; i++)
            buf[i] = s[i] switch
            {
                'a' => 'а', 'e' => 'е', 'o' => 'о', 'p' => 'р', 'c' => 'с',
                'x' => 'х', 'y' => 'у', 'k' => 'к', 'm' => 'м', 'b' => 'в',
                _ => s[i]
            };
        return new string(buf);
    }

    // Родовые слова не «опорные»: по одинокому «республика» регион не угадать.
    private static readonly HashSet<string> GenericRegionWords =
        ["республика", "область", "край", "округ", "автономный", "автономная", "народная"];

    private static IEnumerable<string> SignificantWords(string name) =>
        NormalizeName(name).Split(' ').Where(w => w.Length > 3 && !GenericRegionWords.Contains(w));

    // ── Разбор сырого адреса на сегменты ───────────────────────────────

    private enum Kind { Region, District, City, Settlement, Territory, Street, Building, Unknown }

    // Словарь типов: слово-маркер сегмента → какого уровня объект искать.
    private static readonly Dictionary<string, Kind> TypeMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["область"] = Kind.Region, ["обл"] = Kind.Region, ["край"] = Kind.Region,
        ["республика"] = Kind.Region, ["респ"] = Kind.Region, ["ао"] = Kind.Region,
        ["округ"] = Kind.Region,
        ["район"] = Kind.District, ["р-н"] = Kind.District, ["рн"] = Kind.District,
        ["муниципальный"] = Kind.District, ["мо"] = Kind.District, ["го"] = Kind.District,
        ["г"] = Kind.City, ["город"] = Kind.City, ["гор"] = Kind.City,
        ["с"] = Kind.Settlement, ["село"] = Kind.Settlement, ["п"] = Kind.Settlement,
        ["пос"] = Kind.Settlement, ["посёлок"] = Kind.Settlement, ["поселок"] = Kind.Settlement,
        ["пгт"] = Kind.Settlement, ["д"] = Kind.Settlement, ["дер"] = Kind.Settlement,
        ["деревня"] = Kind.Settlement, ["ст"] = Kind.Settlement, ["станица"] = Kind.Settlement,
        ["аул"] = Kind.Settlement, ["хутор"] = Kind.Settlement, ["х"] = Kind.Settlement,
        ["рп"] = Kind.Settlement, ["сл"] = Kind.Settlement, ["нп"] = Kind.Settlement,
        ["снт"] = Kind.Territory, ["тер"] = Kind.Territory, ["территория"] = Kind.Territory,
        ["мкр"] = Kind.Territory, ["микрорайон"] = Kind.Territory, ["промзона"] = Kind.Territory,
        ["квартал"] = Kind.Territory,
        ["ул"] = Kind.Street, ["улица"] = Kind.Street, ["пер"] = Kind.Street,
        ["переулок"] = Kind.Street, ["пр-т"] = Kind.Street, ["пр-кт"] = Kind.Street,
        ["проспект"] = Kind.Street, ["пр"] = Kind.Street, ["ш"] = Kind.Street,
        ["шоссе"] = Kind.Street, ["наб"] = Kind.Street, ["набережная"] = Kind.Street,
        ["б-р"] = Kind.Street, ["бульвар"] = Kind.Street, ["проезd"] = Kind.Street,
        ["проезд"] = Kind.Street, ["тракт"] = Kind.Street, ["аллея"] = Kind.Street,
        ["линия"] = Kind.Street, ["пл"] = Kind.Street, ["площадь"] = Kind.Street,
        ["прд"] = Kind.Street,
    };

    // Аббревиатуры регионов, встречающиеся в документах. Неоднозначные («ЧР», «РК»)
    // намеренно отсутствуют.
    private static readonly Dictionary<string, int> RegionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ур"] = 18, ["рд"] = 5, ["рб"] = 2, ["кбр"] = 7, ["кчр"] = 9, ["рт"] = 16,
        ["рм"] = 13, ["рх"] = 19, ["рсо"] = 15, ["еао"] = 79, ["хмао"] = 86,
        ["янао"] = 89, ["нао"] = 83, ["мо"] = 50, ["ло"] = 47,
    };

    // Служебные префиксы перед адресом: «Ориентир: …», «РФ, …», «Россия, …».
    [GeneratedRegex(@"^\s*(?:ориентир|адрес|рф|россия|российская\s+федерация)\s*[:.,]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadJunkRx();

    // «д. 97», «дом 12а», «зд. 5», «влд. 3», «уч. 15», «97к2», «12/4», «15-1»,
    // «д.15 корпус 1-3», «д. 4/1 стр. 2, лит. Б» — номер с дробью/диапазоном/буквой
    // плюс сколько угодно частей-приставок (корпус/строение/литера/помещение) следом.
    [GeneratedRegex(@"^(?:д|дом|зд|здание|стр|строение|соор|влд|владение|уч|участок|корп|корпус|лит|литера)?\.?\s*№?\s*(\d+[а-яa-z]?(?:\s*[/к-]\s*\d+[а-яa-z]?)?(?:[\s,]+(?:корп|корпус|к|стр|строение|соор|сооружение|лит|литера|литер|пом|помещение|оф|офис|кв)\.?\s*№?\s*[0-9а-яa-z/-]{1,6})*)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BuildingRx();

    // Продолжение дома отдельным сегментом: «корп. 2», «стр. 3», «лит. Б», «к2»,
    // а также голая цифра/буква сразу после дома («д. 28, 2» — корпус без слова).
    [GeneratedRegex(@"^(?:(корп|корпус|к|стр|строение|соор|сооружение|лит|литера|литер|пом|помещение|оф|офис|кв)\.?\s*№?\s*([0-9а-яa-z/-]{1,6})|([0-9]{1,3}[а-яa-z]?))\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BuildingContinuationRx();

    // Десятичная дробь расстояния, разорванная запятой: «0» + «09 км юго-западнее…».
    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex DecimalDistanceHeadRx();

    // Единица бывает пропущена вовсе: «0,02 западнее дома №254» (23-КК-10).
    [GeneratedRegex(@"^\d{1,3}\s*(?:(?:км|м|километр\w*|метр\w*)\b|[а-я-]{0,8}(?:западнее|восточнее|севернее|южнее))", RegexOptions.IgnoreCase)]
    private static partial Regex DecimalDistanceTailRx();

    // Хвост дома внутри сегмента без запятой: «Казбекская, д. 3», «Улица Новочерёмушкинская Дом 63»
    [GeneratedRegex(@",?\s+(?:д|дом|зд|влд|уч)\.?\s*№?\s*(\d+\S*)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BuildingTailRx();

    // «…р-н г. …», «…район с. …» — пропущенная запятая между районом и НП.
    [GeneratedRegex(@"(\bр-н|\bрайон)\s+(?=(?:г|с|п|пгт|д|ст|х|рп)\.\s?[А-ЯЁ])")]
    private static partial Regex MissingCommaRx();

    // Почтовый индекс отдельным сегментом («117418») — служебный, выбрасываем.
    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex PostalIndexRx();

    // ── Сопоставление ──────────────────────────────────────────────────

    public StructuredAddress Match(string rawAddress)
    {
        var result = new StructuredAddress();
        var extras = new List<string>();

        Node? region = null, district = null, place = null, territory = null, street = null;

        rawAddress = LeadJunkRx().Replace(rawAddress, "");
        // «Курганинский р-н г. Курганинск» — два топонима без запятой: вставляем её,
        // иначе сегмент с двумя маркерами давал нечитаемый ключ (23-КК-10).
        rawAddress = MissingCommaRx().Replace(rawAddress, "$1, ");

        var segments = rawAddress.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (int si = 0; si < segments.Length; si++)
        {
            var seg = segments[si].Trim();
            if (seg.Length == 0) continue;

            // «0,09 км юго-западнее д. 4» — запятая десятичной дроби расщепила
            // расстояние на сегменты: склеиваем обратно и целиком в описание места.
            if (si + 1 < segments.Length && DecimalDistanceHeadRx().IsMatch(seg)
                && DecimalDistanceTailRx().IsMatch(segments[si + 1]))
            {
                extras.Add($"{seg},{segments[si + 1].Trim()}");
                si++;
                continue;
            }

            // Продолжение уже найденного дома: «корп. 2», «стр. 3», «лит. Б», голая цифра.
            // Скобочный хвост («корп. а (на антенной мачте)») отделяем: часть до скобки —
            // к дому, скобка — в описание места.
            if (result.Building != null)
            {
                var head = seg; string? parenTail = null;
                var paren = seg.IndexOf('(');
                if (paren > 0) { head = seg[..paren].Trim().TrimEnd(','); parenTail = seg[paren..].Trim(); }
                var cm = BuildingContinuationRx().Match(head);
                if (cm.Success && parenTail != null) extras.Add(parenTail);
                if (cm.Success) seg = head;
                if (cm.Success)
                {
                    // Тип части не выдумываем: «корп./стр./лит.» — как в документе,
                    // голая цифра так и остаётся цифрой через запятую.
                    result.Building += cm.Groups[1].Success
                        ? $", {cm.Groups[1].Value.ToLowerInvariant().TrimEnd('.')}. {cm.Groups[2].Value}"
                        : $", {cm.Groups[3].Value}";
                    continue;
                }
            }

            // Дом/строение? Скобочный хвост отщепляем и здесь: «дом 88 (торговый центр)» —
            // дом 88, скобка — примета места (77-01-09).
            var bSeg = seg; string? bTail = null;
            var bParen = seg.IndexOf('(');
            if (bParen > 0) { bSeg = seg[..bParen].Trim().TrimEnd(','); bTail = seg[bParen..].Trim(); }
            var bm = BuildingRx().Match(bSeg);
            if (bm.Success && result.Building == null && (street != null || place != null))
            {
                result.Building = bm.Groups[1].Value;
                if (bTail != null) extras.Add(bTail);
                continue;
            }

            // Почтовый индекс («117418, Город Москва…») — не адресный сегмент.
            if (PostalIndexRx().IsMatch(seg)) continue;

            // Дом, приклеенный к сегменту улицы без запятой («Улица Новочерёмушкинская
            // Дом 63»), — отщепляем до классификации; номер станет Building, когда
            // улица/место в этом же сегменте найдётся.
            string? tailBuilding = null;
            var bt = BuildingTailRx().Match(seg);
            if (bt.Success && result.Building == null && seg.Length - bt.Length > 3)
            {
                tailBuilding = bt.Groups[1].Value;
                seg = seg[..bt.Index].TrimEnd(',', ' ');
            }

            var (kind, nameKey, typeMarker) = ClassifySegment(seg);

            // Города федерального значения («г. Москва», «г. Санкт-Петербург»,
            // «г. Севастополь») — регионы 1-го уровня, хотя маркер у них городской.
            if (region == null && kind == Kind.City && _regionsByKey.TryGetValue(nameKey, out var fedCity))
            {
                region = fedCity;
                continue;
            }

            // «УР, г. Ижевск» — регион аббревиатурой.
            if (region == null && RegionAliases.TryGetValue(nameKey, out var code))
            {
                _regionByCode.TryGetValue(code, out region);
                continue;
            }

            if (region == null && (kind is Kind.Region or Kind.Unknown))
            {
                if ((_regionsByKey.TryGetValue(nameKey, out var rn)
                    || SignificantWordLookup(nameKey, out rn)) && rn != null)
                {
                    region = rn;
                    // «Чувашская Республика- Чувашия г. Чебоксары» — сегмент склеен без
                    // запятой: остаток слов после имени региона пробуем как город/НП.
                    var regionWords = SignificantWords(rn.Name)
                        .Concat(NormalizeName(rn.Name).Split(' ')).ToHashSet();
                    var rest = nameKey.Split(' ')
                        .Where(w => !regionWords.Contains(w) && !TypeMarkers.ContainsKey(w))
                        .ToArray();
                    if (rest.Length > 0)
                    {
                        Array.Sort(rest, StringComparer.Ordinal);
                        place ??= Find(region.Region, 4, string.Join(' ', rest));
                    }
                    continue;
                }
                if (kind == Kind.Region) { extras.Add(seg); continue; }
            }

            // Регион не указан, но имя ГОРОДА уникально по стране («Ориентир: г. Бородино»).
            // Без маркера тоже пробуем («Великий Новгород, ул. Ломоносова» — 53-01-01):
            // уникальность по стране защищает от случайных слов.
            if (region == null && kind is Kind.City or Kind.Unknown
                && _citiesByKey.TryGetValue(nameKey, out var ccands) && ccands.Count == 1)
            {
                place = ccands[0];
                _regionByCode.TryGetValue(place.Region, out region);
                continue;
            }

            // Регион не указан, но имя района уникально по стране («Майкопский район» —
            // только в Адыгее): восстанавливаем регион по району.
            if (region == null && kind == Kind.District
                && _districtsByKey.TryGetValue(nameKey, out var dcands)
                && dcands.Select(d => d.Region).Distinct().Count() == 1)
            {
                district = dcands[0];
                _regionByCode.TryGetValue(district.Region, out region);
                continue;
            }

            if (region != null)
            {
                var node = kind switch
                {
                    // «городской округ Чебоксары» — маркер районный, а объект — город.
                    // Фолбэк к городу — ТОЛЬКО для маркеров «го/мо» и только точным
                    // совпадением: обычный «Шенкурский м.р-н» с фуззи превращался
                    // в город Шенкурск, «Предгорный» — в хутор Подгорный (26-01-05, 29-01-02).
                    Kind.District => Find(region.Region, 2, nameKey)
                        ?? (typeMarker is "го" or "мо" ? Find(region.Region, 4, nameKey, allowFuzzy: false) : null)
                        ?? FindDistrictByStem(region.Region, nameKey),
                    // Города Подмосковья и ряда регионов после муниципальной реформы
                    // лежат на уровне 2 («Серпухов г. level 2») — ищем и там (50-99-02).
                    Kind.City or Kind.Settlement => Find(region.Region, 4, nameKey, typeMarker: typeMarker)
                        ?? Find(region.Region, 2, nameKey, allowFuzzy: false, typeMarker: typeMarker),
                    Kind.Territory => Find(region.Region, 7, nameKey),
                    Kind.Street => FindStreet(region.Region, nameKey, place ?? territory ?? district, typeMarker),
                    _ => Find(region.Region, 4, nameKey)          // сегмент без маркера — чаще всего НП
                         ?? Find(region.Region, 2, nameKey)
                         ?? FindStreet(region.Region, nameKey, place ?? territory ?? district, typeMarker)
                };
                if (node != null)
                {
                    switch (LevelGroup(node.Level))
                    {
                        // Город уровня 2 (реформа МО) — это место, а не район.
                        case 2 when TypeKey(node.Type) == "г": place ??= node; break;
                        case 2: district ??= node; break;
                        case 4: place ??= node; break;
                        case 7: territory ??= node; break;
                        default: street ??= node; break;
                    }
                    // Отщеплённый хвост-дом относится к найденной улице/месту.
                    if (tailBuilding != null) result.Building ??= tailBuilding;
                    continue;
                }
            }

            // Ничего не нашли — это описательная часть места (опора, столб, расстояние).
            // Отщеплённый дом возвращаем сегменту, чтобы примета места осталась целой.
            extras.Add(tailBuilding != null ? $"{seg}, д. {tailBuilding}" : seg);
        }

        // Улица без дома: дом мог остаться хвостом в сегменте улицы.
        Fill(result, region, district, place, territory, street);
        if (extras.Count > 0) result.Extra = string.Join(", ", extras);
        return result;
    }

    private bool SignificantWordLookup(string key, out Node? region)
    {
        region = null;
        foreach (var w in key.Split(' '))
            if (w.Length > 3 && _regionsByKey.TryGetValue(w, out region)) return true;
        return false;
    }

    private (Kind, string, string?) ClassifySegment(string seg)
    {
        var lc = ReplaceLatinHomoglyphs(seg.ToLowerInvariant().Replace('ё', 'е'));
        // Составные типы схлопываем в один маркер ДО разбиения на слова: иначе
        // «городской округ Чебоксары» давал ключ «городской чебоксары» — «округ»
        // съедался как маркер, а «городской» портил имя (21-01-04).
        lc = lc.Replace("городской округ", "го")
               .Replace("муниципальный округ", "го")
               .Replace("муниципальный район", "рн")
               .Replace("сельское поселение", "сп")
               .Replace("городское поселение", "сп")
               .Replace("сельский округ", "го")
               .Replace("внутригородской район", "рн")
               .Replace("рабочий поселок", "рп")
               .Replace("поселок городского типа", "пгт")
               .Replace("дачный поселок", "дп")
               .Replace("курортный поселок", "кп")
               // Точечные сокращения, которые деление на слова разорвало бы в мусор
               // («м.о.» → «м о», «ст-ца» → «ст ца» с бесхозной «ца», 26-01-05).
               .Replace("м.о.", " мо ")
               .Replace("г.о.", " го ")
               .Replace("м.р-н", " рн ")
               .Replace("ст-ца", " станица ")
               .Replace("ст-це", " станица ")
               .Replace("р.п.", " рп ")
               .Replace("п.г.т.", " пгт ")
               .Replace("ж/д ст", " ждст ")
               .Replace("пр-зд", " прд ");
        var words = JunkCharsRx().Replace(lc, " ")
            .Split([' '], StringSplitOptions.RemoveEmptyEntries).ToList();
        // Хвост дома внутри сегмента улицы срезаем до классификации.
        var kind = Kind.Unknown;
        string? marker = null;
        var nameWords = new List<string>();
        foreach (var w in words)
        {
            // «республика-» (хвостовой дефис от «Республика- Чувашия») — тоже маркер.
            // Маркеры поглощаем ВСЕ, а не только первый: «г.о. город Казань» содержит
            // и «го», и «город» — оба служебные, имя только «Казань». Тип сегмента
            // определяет первый маркер; само слово-маркер сохраняем — тип улицы различает
            // тёзок («Кутузовский пр-кт/пр-д/пер.» в Москве, 77-01-09).
            var wClean = w.Trim('-');
            if (wClean.Length > 0 && TypeMarkers.TryGetValue(wClean, out var k))
            {
                if (kind == Kind.Unknown) { kind = k; marker = wClean; }
                continue;
            }
            // Одинокие буквы-обломки («о» от «г.о.») именем не являются.
            if (wClean.Length <= 1 && !char.IsDigit(wClean.FirstOrDefault())) continue;
            nameWords.Add(w);
        }
        // Ключ имени строим Тем же NormalizeName, что и индекс: иначе «Кабардино-Балкарская»
        // (дефис) в сегменте и в индексе давали бы разные ключи.
        return (kind, NormalizeName(string.Join(' ', nameWords)), marker);
    }

    // Слово-маркер типа улицы из документа → допустимые типы ГАР (ключи без точек/дефисов).
    private static readonly Dictionary<string, string[]> StreetTypeSynonyms = new()
    {
        ["улица"] = ["ул"], ["ул"] = ["ул"],
        ["проспект"] = ["пркт"], ["прт"] = ["пркт"], ["пркт"] = ["пркт"],
        ["переулок"] = ["пер"], ["пер"] = ["пер"],
        ["проезд"] = ["прд"], ["прд"] = ["прд"],
        // Голое «пр.» пишут и про проспект, и про проезд — допускаем оба.
        ["пр"] = ["пркт", "прд"],
        ["шоссе"] = ["ш"], ["ш"] = ["ш"],
        ["набережная"] = ["наб"], ["наб"] = ["наб"],
        ["бульвар"] = ["бр"], ["бр"] = ["бр"],
        ["площадь"] = ["пл"], ["пл"] = ["пл"],
        ["тракт"] = ["тракт"], ["аллея"] = ["аллея"],
        ["линия"] = ["лн", "линия"], ["тупик"] = ["туп"],
    };

    private static string TypeKey(string t) => new([.. t.ToLowerInvariant().Where(char.IsLetter)]);

    /// <summary>Допуск опечаток «80 % сходства»: расстояние Левенштейна до 1/5 длины имени.</summary>
    private static int FuzzyLimit(string key) => Math.Max(1, key.Length / 5);

    // Тип НП из документа → допустимые типы ГАР: тёзки разных типов в одном регионе
    // нередки («г. Солигалич» и «ж/д ст. Солигалич»), и без фильтра выигрывал
    // первый попавшийся (44-КЦ-01, 45-01-01, 47-13-04).
    private static readonly Dictionary<string, string[]> SettlementTypeSynonyms = new()
    {
        ["г"] = ["г"], ["город"] = ["г"],
        ["с"] = ["с"], ["село"] = ["с"],
        ["п"] = ["п"], ["пос"] = ["п"], ["поселок"] = ["п"],
        ["рп"] = ["рп"], ["пгт"] = ["пгт"],
        ["д"] = ["д"], ["дер"] = ["д"], ["деревня"] = ["д"],
        ["х"] = ["х"], ["хутор"] = ["х"], ["аул"] = ["аул"],
        ["станица"] = ["стца", "ст"], ["сл"] = ["сл"],
    };

    private Node? Find(int region, int group, string key, bool allowFuzzy = true, string? typeMarker = null)
    {
        if (key.Length == 0) return null;
        if (_index.TryGetValue((region, group, key), out var exact))
        {
            if (typeMarker != null && exact.Count > 1
                && SettlementTypeSynonyms.TryGetValue(typeMarker, out var allowed))
            {
                var filtered = exact.Where(c => allowed.Contains(TypeKey(c.Type))).ToList();
                if (filtered.Count > 0) return filtered[0];
            }
            // Единственный кандидат, но тип из документа ему противоречит — при наличии
            // маркера это скорее НЕ тот объект… однако лучше согласованный НП с другим
            // типом, чем ничего: канонизация типов по ГАР — осознанное правило.
            return exact[0];
        }
        if (!allowFuzzy) return null;
        // Нечёткое совпадение на кандидатах региона того же уровня — лечит OCR-опечатки
        // («Кировоская», латинская «c», пропущенные буквы).
        var max = FuzzyLimit(key);
        Node? best = null;
        int bestDist = max + 1;
        foreach (var ((r, g, k), nodes) in _index)
        {
            if (r != region || g != group) continue;
            if (Math.Abs(k.Length - key.Length) > max) continue;
            var d = Levenshtein(k, key, max);
            if (d < bestDist) { bestDist = d; best = nodes[0]; }
            else if (d == bestDist && best != null && nodes[0].Id != best.Id) best = null;   // ничья — не угадываем
        }
        return bestDist <= max ? best : null;
    }

    private Node? FindStreet(int region, string key, Node? parentHint, string? typeMarker = null)
    {
        if (key.Length == 0) return null;
        if (_index.TryGetValue((region, 8, key), out var cands))
        {
            // Тип улицы из документа («проспект» ≠ «переулок» ≠ «проезд») сужает тёзок:
            // «Кутузовский» в Москве — пр-кт, пр-д и пер., и без типа выбрать нельзя (77-01-09).
            var byType = cands;
            if (typeMarker != null && StreetTypeSynonyms.TryGetValue(typeMarker, out var allowed))
            {
                var filtered = cands.Where(c => allowed.Contains(TypeKey(c.Type))).ToList();
                if (filtered.Count > 0) byType = filtered;
            }

            if (parentHint == null) return byType.Count == 1 ? byType[0] : null;
            // Улиц с одним именем в регионе много — выбираем ту, что лежит под найденным
            // городом/районом (подъём по иерархии до 8 уровней).
            foreach (var c in byType)
                if (IsDescendantOf(c, parentHint.Id)) return c;
            // Под якорем не нашлась, но по региону+типу кандидат единственный — берём его:
            // в городах федерального значения «районы» документа в ГАР отсутствуют,
            // и якорь-подсказка не срабатывает (78-01-05).
            return byType.Count == 1 ? byType[0] : null;
        }

        // Нечёткий поиск улицы — только в границах найденного города/района:
        // на всём регионе похожих имён слишком много.
        if (parentHint == null) return null;
        var max = FuzzyLimit(key);
        Node? best = null;
        int bestDist = max + 1;
        foreach (var ((r, g, k), nodes) in _index)
        {
            if (r != region || g != 8) continue;
            if (Math.Abs(k.Length - key.Length) > max) continue;
            var d = Levenshtein(k, key, max);
            if (d > max || d > bestDist) continue;
            foreach (var c in nodes)
            {
                if (!IsDescendantOf(c, parentHint.Id)) continue;
                if (d < bestDist) { bestDist = d; best = c; }
                else if (best != null && c.Id != best.Id) best = null;
            }
        }
        if (best != null) return best;

        // «Фролова ул» в документе против «ул. Генерала Фролова» в ГАР — почётная
        // приставка опущена. Уникальный под якорем суффикс-тёзка — совпадение (51-01-04).
        if (key.Length >= 5)
        {
            Node? suffixHit = null;
            var suffix = " " + key;
            foreach (var ((r, g, k), nodes) in _index)
            {
                if (r != region || g != 8 || !k.EndsWith(suffix, StringComparison.Ordinal)) continue;
                foreach (var c in nodes)
                {
                    if (!IsDescendantOf(c, parentHint.Id)) continue;
                    if (suffixHit != null && c.Id != suffixHit.Id) return null;   // неоднозначно
                    suffixHit = c;
                }
            }
            return suffixHit;
        }
        return null;
    }

    // «Борский район» после реформы в ГАР — «м.о. город Бор»: имя-прилагательное
    // сводим к основе и пробуем «бор» и «город бор» точным совпадением (52-НЦ-09).
    private Node? FindDistrictByStem(int region, string key)
    {
        if (key.Contains(' ')) return null;
        foreach (var suf in (string[])["ский", "цкий", "ской", "ская", "цкая", "ское"])
        {
            if (!key.EndsWith(suf, StringComparison.Ordinal) || key.Length - suf.Length < 3) continue;
            var stem = key[..^suf.Length];
            var n = Find(region, 2, stem, allowFuzzy: false)
                 ?? Find(region, 2, NormalizeName("город " + stem), allowFuzzy: false);
            if (n != null) return n;
        }
        return null;
    }

    private bool IsDescendantOf(Node node, long ancestorId)
    {
        var cur = node;
        for (int i = 0; i < 8 && cur != null; i++)
        {
            if (cur.Parent == ancestorId) return true;
            _nodes.TryGetValue(cur.Parent, out cur);
        }
        return false;
    }

    private void Fill(StructuredAddress a, Node? region, Node? district, Node? place, Node? territory, Node? street)
    {
        Node? deepest = null;
        if (region != null) { a.Region = Full(region); a.RegionCode = region.Region; a.MatchLevel = "регион"; deepest = region; }
        if (district != null) { a.District = Full(district); a.MatchLevel = "район"; deepest = district; }
        if (place != null)
        {
            if (place.Level == 5 || place.Type is "г." or "г") { a.City = place.Name; a.MatchLevel = "город"; }
            else { a.Settlement = Full(place); a.MatchLevel = "населённый пункт"; }
            deepest = place;
        }
        if (territory != null) { a.Territory = Full(territory); a.MatchLevel = "территория"; deepest = territory; }
        if (street != null) { a.Street = Full(street); a.MatchLevel = "улица"; deepest = street; }
        if (deepest != null) a.Guid = deepest.Guid;

        // Полная адресная книга: спускаемся до дома. Совпал — matchLevel «дом»
        // и GUID уже конкретного здания из ГАР.
        if (street != null && a.Building != null && TryMatchHouse(street.Id, a.Building) is { } house)
        {
            a.MatchLevel = "дом";
            a.Guid = house;
        }
    }

    /// <summary>
    /// Ищет дом на улице по номеру из документа. Сравнение по нормализованному ключу
    /// (только буквы-цифры: «28, корп. 2» ↔ ГАР «28 2»); неоднозначность — честный отказ:
    /// при нескольких кандидатах с тем же ведущим номером дом не угадываем.
    /// </summary>
    private string? TryMatchHouse(long streetId, string building)
    {
        if (_housesDb == null) return null;
        var want = HouseKey(building);
        if (want.Length == 0) return null;
        var wantLead = LeadNumber(building);

        using var cmd = _housesDb.CreateCommand();
        cmd.CommandText = """
            SELECT h.guid, h.housenum FROM hierarchy hi
            JOIN houses h ON h.objectid = hi.objectid
            WHERE hi.parentobjid = $p
            """;
        cmd.Parameters.AddWithValue("$p", streetId);
        using var r = cmd.ExecuteReader();

        string? exact = null;
        var leadMatches = new List<string>();
        while (r.Read())
        {
            var guid = r.GetString(0);
            var num = r.GetString(1);
            if (HouseKey(num) == want) { exact = guid; break; }
            if (wantLead.Length > 0 && LeadNumber(num) == wantLead) leadMatches.Add(guid);
        }
        return exact ?? (leadMatches.Count == 1 ? leadMatches[0] : null);
    }

    /// <summary>Ключ номера дома: нижний регистр, только буквы и цифры («28, корп. 2» → «28корп2»→«282»…).</summary>
    private static string HouseKey(string s)
    {
        var lc = s.ToLowerInvariant()
            .Replace("корпус", "").Replace("корп", "").Replace("строение", "")
            .Replace("стр", "").Replace("литера", "").Replace("лит", "").Replace("дом", "");
        return new string(lc.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>Ведущий номер: «28а, стр. 2» → «28а».</summary>
    private static string LeadNumber(string s)
    {
        var m = LeadNumberRx().Match(s.ToLowerInvariant());
        return m.Success ? m.Value : "";
    }

    [GeneratedRegex(@"^\s*\d+[а-яa-z]?")]
    private static partial Regex LeadNumberRx();

    /// <summary>
    /// Канонические имена НП и улицы по ключам обратного геокодирования (для объекта
    /// «адрес по координатам»): ищем их в ГАР региона, не нашли — null.
    /// </summary>
    public (string? Place, string? Street) CanonicalNames(int region, string? placeKey, string? streetKey)
    {
        if (region <= 0) return (null, null);
        var place = placeKey != null ? Find(region, 4, placeKey) : null;
        var street = streetKey != null ? FindStreet(region, streetKey, place) : null;
        return (place != null ? Full(place) : null, street != null ? Full(street) : null);
    }

    // «Красноярский край», «Мотыгинский р-н», но «респ. Дагестан», «с. Чепца»:
    // имена-прилагательные ставим перед типом, существительные — после.
    private static string Full(Node n)
    {
        // «Чувашская Республика -» (21) — хвостовой дефис прямо в имени ГАР.
        var name = n.Name.Trim(' ', '-');
        // Родовое слово уже в имени («Чувашская Республика») — тип не дублируем.
        if (GenericRegionWords.Overlaps(NormalizeName(name).Split(' ')))
            return name;
        // НП, территории и улицы — всегда «тип имя» («с. Топольное», «ул. Комсомольская»):
        // привычная форма записи. Правило прилагательных остаётся регионам и районам
        // («Красноярский край», «Учалинский р-н»).
        if (n.Level >= 4)
            return $"{n.Type} {name}".Trim();
        return AdjectiveNameRx().IsMatch(name) ? $"{name} {n.Type}".Trim() : $"{n.Type} {name}".Trim();
    }

    [GeneratedRegex(@"(?:ий|ый|ая|яя|ое|ье|ская|цкая)$")]
    private static partial Regex AdjectiveNameRx();

    /// <summary>
    /// Дамерау-Левенштейн (OSA) с ранним выходом: пропуск, замена, вставка и
    /// ПЕРЕСТАНОВКА соседних букв («Кирвоа» ← «Кирова») считаются одной правкой —
    /// в документах буквы чаще всего именно путают местами или пропускают.
    /// </summary>
    private static int Levenshtein(string a, string b, int max)
    {
        if (a == b) return 0;
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    cur[j] = Math.Min(cur[j], prev2[j - 2] + 1);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > max) return max + 1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }
}
