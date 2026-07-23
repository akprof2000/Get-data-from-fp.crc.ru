using System.Globalization;
using System.Text.RegularExpressions;

namespace ParseTextHeader;

/// <summary>
/// Извлекает географические координаты станции из текста документа.
/// Поддерживает основные встречающиеся в корпусе форматы: десятичные градусы
/// (с точкой и запятой, со скобками и без), градусы-минуты-секунды (DMS) в
/// русской («С.Ш./В.Д.») и латинской (N/E) нотациях, включая типовые OCR-опечатки.
/// Результат — строка «широта, долгота» в десятичных градусах (инвариантная культура).
/// </summary>
public static partial class CoordinateParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Все регулярные выражения предкомпилированы: Extract вызывается на каждом из
    // ~112 тыс. документов, а встроенный кэш Regex (15 паттернов) переполняется
    // остальными инлайн-паттернами пайплайна — без предкомпиляции паттерны
    // перекомпилировались бы на каждом вызове.

    [GeneratedRegex(@"[Гг]еографические\s+координаты[:\s]*([^\n]{5,80})", RegexOptions.IgnoreCase)]
    private static partial Regex GeoLabelRx();

    [GeneratedRegex(@"[Кк]оординаты[:\s]*([^\n]{5,80})", RegexOptions.IgnoreCase)]
    private static partial Regex CoordLabelRx();

    // «(53.540542°, 49.342503°)» — скобки + знак градуса (63-СЦ-04)
    [GeneratedRegex(@"\(\s*(\d{2,3}[.,]\d{4,})°\s*,\s*(\d{2,3}[.,]\d{4,})°\s*\)")]
    private static partial Regex BracketDegRx();

    // «Широта: 54,719556° … Долгота: 20,300216°» — запятая как десятичный разделитель (39-КС-14)
    [GeneratedRegex(@"(?:[Шш]ирота[:\s]*|[Лл]атитуд[:\s]*)(\d{2,3},\d{4,})°[^,]*(?:[Дд]олгота[:\s]*|[Лл]онгитуд[:\s]*)(\d{2,3},\d{4,})°", RegexOptions.IgnoreCase)]
    private static partial Regex CommaDegRx();

    // «(53.563167, 49.397336)» — скобки, допускаем пробел внутри дробной части (63-СЦ-04)
    [GeneratedRegex(@"\(\s*(\d{2,3}\.[\s\d]{4,})\s*,\s*(\d{2,3}\.[\s\d]{4,})\s*\)")]
    private static partial Regex BracketDecRx();

    // «54.03436288, 85.89949510» / «55. 635969, 37. 318399» — общий десятичный формат
    [GeneratedRegex(@"(\d{2,3}\.\s*\d{4,})[,\s]+(\d{2,3}\.\s*\d{4,})")]
    private static partial Regex PlainDecRx();

    // Широта в DMS: «С.Ш. 51°42'57.1"» / «51°42'57.1"N».
    // Секунды: необязательная лишняя точка сразу после апострофа минут — частая OCR-опечатка
    // («12'.29.1"» вместо «12'29.1"») + цифры с максимум ОДНИМ десятичным разделителем.
    // Раньше «[\d\.,]+» пропускал несколько разделителей и ронял double.Parse.
    [GeneratedRegex(@"(?:С\.Ш\.|с\.ш\.)\s*(\d{1,3})°\s*(\d{1,2})'\s*\.?(\d+(?:[.,]\d+)?)""", RegexOptions.IgnoreCase)]
    private static partial Regex LatDmsRuRx();

    [GeneratedRegex(@"(\d{1,3})°\s*(\d{1,2})'\s*\.?(\d+(?:[.,]\d+)?)""\s*[Nn]", RegexOptions.IgnoreCase)]
    private static partial Regex LatDmsEnRx();

    // Долгота в DMS: «В.Д. 94°22'43.6"» / «94°22'43.6"E»
    [GeneratedRegex(@"(?:В\.Д\.|в\.д\.)\s*(\d{2,3})°\s*(\d{1,2})'\s*\.?(\d+(?:[.,]\d+)?)""", RegexOptions.IgnoreCase)]
    private static partial Regex LonDmsRuRx();

    [GeneratedRegex(@"(\d{2,3})°\s*(\d{1,2})'\s*\.?(\d+(?:[.,]\d+)?)""\s*[Ee]", RegexOptions.IgnoreCase)]
    private static partial Regex LonDmsEnRx();

    // «54°45'2.33" С.Ш., 55°59'46.78" В.Д.» — направление ПОСЛЕ значений
    [GeneratedRegex(@"(\d{1,3})°\s*(\d{1,2})'\s*\.?(\d+(?:[.,]\d+)?)""\s*([СсNnюЮSs]\.?\s*[Шш]?\.?)[,;\s]+(\d{2,3})°\s*(\d{1,2})'\s*\.?(\d+(?:[.,]\d+)?)""\s*([ВвEeЗзWw]\.?\s*[Дд]?\.?)", RegexOptions.IgnoreCase)]
    private static partial Regex DmsCompactRx();

    // «55-25-47 с.ш.; 65-18-27 в.д.» — дефисы вместо символов градусов/минут
    [GeneratedRegex(@"(\d{1,3})[°\-]\s*(\d{1,2})['\-]\s*\.?(\d+(?:[.,]\d+)?)[""'\s]*\s*(?:с\.ш|N)[;\s,]+(\d{2,3})[°\-]\s*(\d{1,2})['\-]\s*\.?(\d+(?:[.,]\d+)?)[""'\s]*\s*(?:в\.д|E)", RegexOptions.IgnoreCase)]
    private static partial Regex DmsDashRx();

    // «(С.Ш.: 49.264318, В.Д.: 44.040832)» — направление с двоеточием перед числом (34-12-01)
    [GeneratedRegex(@"С\.Ш\.\s*:\s*(\d{2,3}[.,]\d{4,})\s*,\s*В\.Д\.\s*:\s*(\d{2,3}[.,]\d{4,})", RegexOptions.IgnoreCase)]
    private static partial Regex ColonDirRx();

    // «55.882170(С.Ш.), 37.548570(В.Д.)» — цифры слитно со скобкой (77-01-09)
    [GeneratedRegex(@"(\d{2,3}[\.,]\d{4,})\s*\(С\.Ш\.\)[,;\s]+(\d{2,3}[\.,]\d{4,})\s*\(В\.Д\.\)", RegexOptions.IgnoreCase)]
    private static partial Regex ParenDirRx();

    // «46.055931СШ 40.885779ВД» — цифры слитно с направлением (23-КК-10)
    [GeneratedRegex(@"(\d{2,3}[\.,]\d{4,})\s*СШ[,;\s]+(\d{2,3}[\.,]\d{4,})\s*ВД", RegexOptions.IgnoreCase)]
    private static partial Regex GluedDirRx();

    // «51.715914 с.ш., 94.383377 в.д.» — десятичные с текстовым направлением
    [GeneratedRegex(@"(\d{2,3}[\.,]\d+)°?\s*(с\.ш\.|ю\.ш\.|n|s)[,;\s]+(\d{2,3}[\.,]\d+)°?\s*(в\.д\.|з\.д\.|e|w)", RegexOptions.IgnoreCase)]
    private static partial Regex DecDirRx();

    // «ш.: 55.772190, д.: 37.678168» — метки-направления ПЕРЕД числами (77-01-09, WGS84)
    [GeneratedRegex(@"ш\.?\s*:\s*(\d{2,3}[\.,]\d{4,})[,;\s]+д\.?\s*:\s*(\d{2,3}[\.,]\d{4,})", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledLatLonRx();

    // «55. 772190» — пробел после десятичной точки (встречается в поле «Проектная
    // документация», видимо после автопереноса). Склеиваем только когда за точкой
    // идёт длинная дробная часть (4+ цифр) — обычные «д. 4» и даты не трогаются.
    [GeneratedRegex(@"(\d)\.\s+(\d{4,})")]
    private static partial Regex SpacedDecimalRx();

    public static string? Extract(string fullText)
    {
        // Нормализуем разорванные пробелом десятичные дроби: «ш. : 55. 772190» → «ш. : 55.772190».
        fullText = SpacedDecimalRx().Replace(fullText, "$1.$2");
        // Сначала пробуем после метки «Географические координаты» — самый надёжный контекст.
        var labelMatch = GeoLabelRx().Match(fullText);
        if (labelMatch.Success)
        {
            var fromLabel = TryParseLine(labelMatch.Groups[1].Value.Trim());
            if (fromLabel != null) return fromLabel;
        }

        // Потом рядом с более коротким «Координаты».
        var coordLabelMatch = CoordLabelRx().Match(fullText);
        if (coordLabelMatch.Success)
        {
            var fromLabel = TryParseLine(coordLabelMatch.Groups[1].Value.Trim());
            if (fromLabel != null) return fromLabel;
        }

        // И как фоллбэк — по всему тексту.
        return TryParseLine(fullText);
    }

    private static string? TryParseLine(string text)
    {
        return TryLabeledLatLon(text)
            ?? TryDecimalDegrees(text)
            ?? TryDecimalWithDirection(text)
            ?? TryDmsWithSeparateDirection(text)
            ?? TryDmsCompact(text);
    }

    /// <summary>Форматирует пару координат после проверки диапазонов территории РФ.</summary>
    private static string? FormatIfValid(double lat, double lon)
    {
        // Санитарная проверка: широта России 41–82°, долгота 19–190°.
        if (lat is < 41 or > 82 || lon is < 19 or > 190) return null;
        return $"{lat.ToString(Inv)}, {lon.ToString(Inv)}";
    }

    /// <summary>Разбирает строку с запятой или точкой как десятичным разделителем.</summary>
    private static bool TryParseCoord(string s, out double value) =>
        double.TryParse(s.Replace(',', '.').Replace(" ", ""), NumberStyles.Float, Inv, out value);

    // «ш.: 55.772190, д.: 37.678168» — метки-направления перед числами (77-01-09)
    private static string? TryLabeledLatLon(string text)
    {
        var m = LabeledLatLonRx().Match(text);
        if (m.Success
            && TryParseCoord(m.Groups[1].Value, out var lat)
            && TryParseCoord(m.Groups[2].Value, out var lon))
        {
            return FormatIfValid(lat, lon);
        }

        return null;
    }

    // Десятичные градусы: «54.03436288, 85.89949510», «(53.563167, 49.397336)»,
    // «(53.540542°, 49.342503°)», «Широта: 54,719556°…»
    private static string? TryDecimalDegrees(string text)
    {
        foreach (var rx in (ReadOnlySpan<Regex>)[BracketDegRx(), CommaDegRx(), BracketDecRx(), PlainDecRx()])
        {
            var m = rx.Match(text);
            if (m.Success
                && TryParseCoord(m.Groups[1].Value, out var lat)
                && TryParseCoord(m.Groups[2].Value, out var lon))
            {
                var formatted = FormatIfValid(lat, lon);
                if (formatted != null) return formatted;
            }
        }
        return null;
    }

    // DMS с отдельными метками направления: «С.Ш. 51°42'57.1"» + «В.Д. 94°22'43.6"»
    // или латиницей «58°49'06.92"N 65°58'14.05"E» (86-ХЦ-23).
    private static string? TryDmsWithSeparateDirection(string text)
    {
        var lm = FirstMatch(text, LatDmsRuRx(), LatDmsEnRx());
        var lnm = FirstMatch(text, LonDmsRuRx(), LonDmsEnRx());
        if (lm == null || lnm == null) return null;

        double? lat = DmsToDec(lm.Groups[1].Value, lm.Groups[2].Value, lm.Groups[3].Value);
        double? lon = DmsToDec(lnm.Groups[1].Value, lnm.Groups[2].Value, lnm.Groups[3].Value);
        if (lat is null || lon is null) return null;

        // Южная широта / западная долгота в корпусе не встречаются, но обозначения бывают.
        var latV = text.Contains("ю.ш", StringComparison.OrdinalIgnoreCase) ? -lat.Value : lat.Value;
        var lonV = text.Contains("з.д", StringComparison.OrdinalIgnoreCase) ? -lon.Value : lon.Value;
        return $"{latV.ToString(Inv)}, {lonV.ToString(Inv)}";
    }

    private static Match? FirstMatch(string text, params ReadOnlySpan<Regex> regexes)
    {
        foreach (var rx in regexes)
        {
            var m = rx.Match(text);
            if (m.Success) return m;
        }
        return null;
    }

    // DMS одним выражением: «54°45'2.33" С.Ш. 55°59'46.78" В.Д.» (направление после значений)
    private static string? TryDmsCompact(string text)
    {
        var m = DmsCompactRx().Match(text);
        if (!m.Success)
        {
            // Вариант с дефисами и без кавычек секунд: «55-25-47 с.ш.; 65-18-27 в.д.»
            var dm = DmsDashRx().Match(text);
            if (!dm.Success) return null;
            double? lat2 = DmsToDec(dm.Groups[1].Value, dm.Groups[2].Value, dm.Groups[3].Value);
            double? lon2 = DmsToDec(dm.Groups[4].Value, dm.Groups[5].Value, dm.Groups[6].Value);
            if (lat2 is null || lon2 is null) return null;
            return $"{lat2.Value.ToString(Inv)}, {lon2.Value.ToString(Inv)}";
        }

        double? lat = DmsToDec(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        double? lon = DmsToDec(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value);
        if (lat is null || lon is null) return null;

        var latDir = m.Groups[4].Value.ToUpperInvariant();
        var lonDir = m.Groups[8].Value.ToUpperInvariant();
        var latV = latDir.StartsWith('Ю') || latDir.StartsWith('S') ? -lat.Value : lat.Value;
        var lonV = lonDir.StartsWith('З') || lonDir.StartsWith('W') ? -lon.Value : lon.Value;
        return $"{latV.ToString(Inv)}, {lonV.ToString(Inv)}";
    }

    // Десятичные градусы с текстовым направлением: «51.715914 с.ш., 94.383377 в.д.»
    // и слитные/скобочные варианты (см. паттерны ColonDir/ParenDir/GluedDir).
    private static string? TryDecimalWithDirection(string text)
    {
        foreach (var rx in (ReadOnlySpan<Regex>)[ColonDirRx(), ParenDirRx(), GluedDirRx()])
        {
            var m2 = rx.Match(text);
            if (m2.Success
                && TryParseCoord(m2.Groups[1].Value, out var lat2)
                && TryParseCoord(m2.Groups[2].Value, out var lon2))
            {
                var formatted = FormatIfValid(lat2, lon2);
                if (formatted != null) return formatted;
            }
        }

        var m = DecDirRx().Match(text);
        if (!m.Success) return null;
        if (!TryParseCoord(m.Groups[1].Value, out var lat)) return null;
        if (!TryParseCoord(m.Groups[3].Value, out var lon)) return null;

        var latDir = m.Groups[2].Value.ToLowerInvariant();
        var lonDir = m.Groups[4].Value.ToLowerInvariant();
        if (latDir.Contains('ю') || latDir == "s") lat = -lat;
        if (lonDir.Contains('з') || lonDir == "w") lon = -lon;
        return $"{lat.ToString(Inv)}, {lon.ToString(Inv)}";
    }

    // Перевод «градусы/минуты/секунды» в десятичные градусы.
    // Возвращает null вместо исключения при некорректном числе (опечатки в исходных
    // документах, например «.29.1» вместо «29.1» — лишний разделитель). Раньше здесь
    // падал необработанный FormatException и весь файл уходил в ошибку чтения.
    private static double? DmsToDec(string deg, string min, string sec)
    {
        if (!int.TryParse(deg, out int d)) return null;
        if (!int.TryParse(min, out int mi)) return null;
        if (!double.TryParse(sec.Replace(',', '.'), NumberStyles.Float, Inv, out double s)) return null;
        // Минуты и секунды в DMS всегда < 60. Без проверки случай «44'.97"» (лишняя точка +
        // пропущенная цифра) тихо давал бы 97 «секунд» — физически невозможное значение (86-ХЦ-23).
        if (mi is < 0 or >= 60 || s is < 0 or >= 60) return null;
        return d + mi / 60.0 + s / 3600.0;
    }
}
