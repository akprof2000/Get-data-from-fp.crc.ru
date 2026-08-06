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
    // Пробелы допускаются перед знаками минут и секунд: «с.ш. 55° 50 ' 10,23"» (02-БЦ-01),
    // закрывающая кавычка секунд необязательна — её часто теряют при наборе.
    [GeneratedRegex(@"(?:С\.Ш\.|с\.ш\.)\s*(\d{1,3})°\s*(\d{1,2})\s*'\s*\.?(\d+(?:[.,]\d+)?)\s*""?", RegexOptions.IgnoreCase)]
    private static partial Regex LatDmsRuRx();

    // «(42°3'50.8") N» — между кавычкой секунд и буквой направления бывает скобка (05-01-02)
    [GeneratedRegex(@"(\d{1,3})°\s*(\d{1,2})\s*'\s*\.?(\d+(?:[.,]\d+)?)""[)\s]*[Nn]", RegexOptions.IgnoreCase)]
    private static partial Regex LatDmsEnRx();

    // «N 52°02'23.10" E113°28'26.50"» — латинское направление ПЕРЕД значением (75-ОЦ-05).
    // Без RegexOptions.IgnoreCase: строчная «n»/«e» — обычные буквы русского текста рядом
    // с числами («…на 52°…»), а заглавная в этой позиции почти всегда значит направление.
    [GeneratedRegex(@"\bN\s*(\d{1,3})°\s*(\d{1,2})\s*'\s*\.?(\d+(?:[.,]\d+)?)""")]
    private static partial Regex LatDmsEnPrefixRx();

    // Долгота в DMS: «В.Д. 94°22'43.6"» / «94°22'43.6"E»
    [GeneratedRegex(@"(?:В\.Д\.|в\.д\.)\s*(\d{2,3})°\s*(\d{1,2})\s*'\s*\.?(\d+(?:[.,]\d+)?)\s*""?", RegexOptions.IgnoreCase)]
    private static partial Regex LonDmsRuRx();

    // Направление «на восток» бывает и кириллической «Е» — визуально неотличимой от латинской «E».
    [GeneratedRegex(@"(\d{2,3})°\s*(\d{1,2})\s*'\s*\.?(\d+(?:[.,]\d+)?)""[)\s]*[EeЕе]", RegexOptions.IgnoreCase)]
    private static partial Regex LonDmsEnRx();

    [GeneratedRegex(@"[EЕ]\s*(\d{2,3})°\s*(\d{1,2})\s*'\s*\.?(\d+(?:[.,]\d+)?)""")]
    private static partial Regex LonDmsEnPrefixRx();

    // «(N 52.03975° E113.474028°)» — то же, но в десятичных градусах
    [GeneratedRegex(@"\bN\s*(\d{2,3}[.,]\d{4,})\s*°?\s*[,;]?\s*E\s*(\d{2,3}[.,]\d{4,})")]
    private static partial Regex DecEnPrefixRx();

    // «54°45'2.33" С.Ш., 55°59'46.78" В.Д.» — направление ПОСЛЕ значений.
    // \s* перед знаком градуса — координата бывает разорвана переносом строки
    // («координаты: 42\n°59'20.7" с.ш.», 05-01-02-14); кавычка секунд необязательна
    // («55°56'23,4 в.д.», 02-БЦ-01) — от ложных срабатываний защищает обязательная метка направления.
    [GeneratedRegex(@"(\d{1,3})\s*°\s*(\d{1,2})\s*'\s*\.?(\d+(?:[.,]\d+)?)\s*""?[)\s]*([СсCcNnюЮSs]\.?\s*[Шш]?\.?)[,;\s]+(\d{2,3})\s*°\s*(\d{1,2})\s*'\s*\.?(\d+(?:[.,]\d+)?)\s*""?[)\s]*([ВвEeЕеЗзWw]\.?\s*[Дд]?\.?)", RegexOptions.IgnoreCase)]
    private static partial Regex DmsCompactRx();

    // «55-25-47 с.ш.; 65-18-27 в.д.» — дефисы вместо символов градусов/минут.
    // После метки широты допускаем точку («с.ш.; 65-…»): нормализация направлений
    // всегда дописывает её, а прежний разделитель «[;\s,]+» точку не принимал.
    [GeneratedRegex(@"(\d{1,3})[°\-]\s*(\d{1,2})['\-]\s*\.?(\d+(?:[.,]\d+)?)[""'\s]*\s*(?:с\.ш|N)\.?[;\s,]*(\d{2,3})[°\-]\s*(\d{1,2})['\-]\s*\.?(\d+(?:[.,]\d+)?)[""'\s]*\s*(?:в\.д|E)", RegexOptions.IgnoreCase)]
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

    // «51.715914 с.ш., 94.383377 в.д.» — десятичные с текстовым направлением.
    // Завершающая точка в метке необязательна: «(42.570164 с.ш. 47.193683 в.д)» (05-01-01-33).
    [GeneratedRegex(@"(\d{2,3}[\.,]\d+)°?\s*(с\.ш\.?|ю\.ш\.?|n|s)[,;\s]+(\d{2,3}[\.,]\d+)°?\s*(в\.д\.?|з\.д\.?|e|w)", RegexOptions.IgnoreCase)]
    private static partial Regex DecDirRx();

    // «ш.: 55.772190, д.: 37.678168» — метки-направления ПЕРЕД числами (77-01-09, WGS84)
    [GeneratedRegex(@"ш\.?\s*:\s*(\d{2,3}[\.,]\d{4,})[,;\s]+д\.?\s*:\s*(\d{2,3}[\.,]\d{4,})", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledLatLonRx();

    // «55. 772190» — пробел после десятичной точки (встречается в поле «Проектная
    // документация», видимо после автопереноса). Склеиваем только когда за точкой
    // идёт длинная дробная часть (4+ цифр) — обычные «д. 4» и даты не трогаются.
    [GeneratedRegex(@"(\d)\.\s+(\d{4,})")]
    private static partial Regex SpacedDecimalRx();

    // «42*59`11.2``» — звёздочка вместо знака градуса. Заменяем только там, где за ней идёт
    // число (минуты) или метка направления, чтобы не трогать сноски и «*» из таблиц.
    [GeneratedRegex(@"(\d)\s*\*(?=\s*(?:\d|[сcСCвВbBnNeEЕе]))")]
    private static partial Regex StarDegreeRx();

    // Метки направления с латинскими и «соседними по клавиатуре» буквами и лишними пробелами:
    // «c.ш.» (латинская c), «с. ш.», «с.щ.» (щ вместо ш) — всё приводим к «с.ш.» / «в.д.» и т.п.
    [GeneratedRegex(@"[сcСC]\.\s*[шщШЩ]\.?")]
    private static partial Regex DirNorthRx();

    [GeneratedRegex(@"[вВbB]\.\s*[дД]\.?")]
    private static partial Regex DirEastRx();

    [GeneratedRegex(@"[юЮ]\.\s*[шщШЩ]\.?")]
    private static partial Regex DirSouthRx();

    [GeneratedRegex(@"[зЗ]\.\s*[дД]\.?")]
    private static partial Regex DirWestRx();

    public static string? Extract(string fullText)
    {
        fullText = NormalizeMarks(fullText);
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
        // TryDmsCompact идёт раньше TryDmsWithSeparateDirection: он требует ОБЕ метки
        // направления на своих местах, поэтому в записях вида «54°43'44,8" с.ш. 55°56'23,4 в.д.»
        // не спутает долготу с широтой (шаблон «с.ш. <число>» иначе цепляет вторую координату).
        return TryLabeledLatLon(text)
            ?? TryDecimalDegrees(text)
            ?? TryDecimalWithDirection(text)
            ?? TryDmsCompact(text)
            ?? TryDmsWithSeparateDirection(text);
    }

    /// <summary>
    /// Приводит «самодельные» обозначения градусов/минут/секунд и метки направления
    /// к каноническому виду: «42*59`11.2`` с.щ.» → «42°59'11.2" с.ш.».
    /// </summary>
    private static string NormalizeMarks(string text)
    {
        // Апострофы-минуты и кавычки-секунды: обратные и типографские варианты.
        // Двойные обратные кавычки разбираем до одиночных, иначе `` превратится в ''.
        text = text.Replace("``", "\"")
                   .Replace("''", "\"")  // два апострофа вместо кавычки секунд: «52°53'02,7''»
                   .Replace('`', '\'')
                   .Replace('´', '\'')
                   .Replace('′', '\'')  // ′ prime
                   .Replace('’', '\'')  // ’
                   .Replace('″', '"')   // ″ double prime
                   .Replace('”', '"')   // ”
                   .Replace('“', '"');  // “

        text = StarDegreeRx().Replace(text, "$1°");
        text = DirNorthRx().Replace(text, "с.ш.");
        text = DirEastRx().Replace(text, "в.д.");
        text = DirSouthRx().Replace(text, "ю.ш.");
        text = DirWestRx().Replace(text, "з.д.");
        return text;
    }

    /// <summary>Форматирует пару координат после проверки диапазонов территории РФ.</summary>
    private static string? FormatIfValid(double lat, double lon)
    {
        // Санитарная проверка: широта России 41–82°, долгота 19–190°.
        // Чукотка заходит за 180-й меридиан: там координаты записывают западной
        // долготой («172°51'38,69" з.д.»), то есть отрицательным значением.
        if (lat is < 41 or > 82) return null;
        if ((lon is < 19 or > 190) && (lon is < -180 or > -168)) return null;
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
        foreach (var rx in (ReadOnlySpan<Regex>)[DecEnPrefixRx(), BracketDegRx(), CommaDegRx(), BracketDecRx(), PlainDecRx()])
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
        var lm = FirstMatch(text, LatDmsRuRx(), LatDmsEnRx(), LatDmsEnPrefixRx());
        var lnm = FirstMatch(text, LonDmsRuRx(), LonDmsEnRx(), LonDmsEnPrefixRx());
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
        // Перебираем ВСЕ совпадения обоих шаблонов: первое может оказаться мусорным
        // (например, из-за опечатки минуты > 60), а дальше по тексту лежит корректная пара.
        // Раньше неудача на первом совпадении роняла разбор всего документа в null.
        foreach (Match m in DmsCompactRx().Matches(text))
        {
            double? lat = DmsToDec(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            double? lon = DmsToDec(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value);
            if (lat is null || lon is null) continue;

            var latDir = m.Groups[4].Value.ToUpperInvariant();
            var lonDir = m.Groups[8].Value.ToUpperInvariant();
            var latV = latDir.StartsWith('Ю') || latDir.StartsWith('S') ? -lat.Value : lat.Value;
            var lonV = lonDir.StartsWith('З') || lonDir.StartsWith('W') ? -lon.Value : lon.Value;
            var formatted = FormatIfValid(latV, lonV);
            if (formatted != null) return formatted;
        }

        // Вариант с дефисами и без кавычек секунд: «55-25-47 с.ш.; 65-18-27 в.д.»
        foreach (Match dm in DmsDashRx().Matches(text))
        {
            double? lat2 = DmsToDec(dm.Groups[1].Value, dm.Groups[2].Value, dm.Groups[3].Value);
            double? lon2 = DmsToDec(dm.Groups[4].Value, dm.Groups[5].Value, dm.Groups[6].Value);
            if (lat2 is null || lon2 is null) continue;
            var formatted = FormatIfValid(lat2.Value, lon2.Value);
            if (formatted != null) return formatted;
        }

        return null;
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
        // Ровно 60 секунд — не опечатка, а огрубление в исходнике («53°27'60"», 02-БЦ-01):
        // трактуем как +1 минуту. Всё, что больше, по-прежнему считаем мусором.
        if (mi is < 0 or >= 60 || s is < 0 or > 60) return null;
        return d + mi / 60.0 + s / 3600.0;
    }
}
