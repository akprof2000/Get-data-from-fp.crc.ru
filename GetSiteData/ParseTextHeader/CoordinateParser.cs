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
    // Терминатором секунд, кроме кавычки, бывает лишний знак градуса:
    // «N55°10'18.7 °, Е61°33'18.9"» (74-50-03).
    [GeneratedRegex(@"\bN\s*(\d{1,3})°\s*(\d{1,2})\s*'\s*\.?(\d+(?:\s*[.,]\s*\d+)?)\s*[""°]")]
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
    // Разделителем пары бывает «/»: «45°02'02" С.Ш. / 39°00'21" В.Д.» (23-КК-10);
    // дробная часть секунд бывает разорвана пробелом: «55°48'27. 25" с. ш.» (77-01-09).
    [GeneratedRegex(@"(\d{1,3})\s*°\s*(\d{1,2})\s*'\s*\.?(\d+(?:\s*[.,]\s*\d+)?)\s*""?[)\s]*([СсCcNnюЮSs]\.?\s*[Шш]?\.?)[,;/\s]+(\d{2,3})\s*°\s*(\d{1,2})\s*'\s*\.?(\d+(?:\s*[.,]\s*\d+)?)\s*""?[)\s]*([ВвEeЕеЗзWw]\.?\s*[Дд]?\.?)", RegexOptions.IgnoreCase)]
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
    // Между числом и меткой допускаем «мусорный» знак градуса: «57.736217"с.ш.» (44-КЦ-01),
    // «45.039167°" с.ш.» (26-01-05), «44.862051о с.ш.» — русская «о» вместо ° (23-КК-10).
    // Разделителем пары бывает «/»: «43.575323 С.Ш./ 44.06378 В.Д)» (07-01-04).
    // Между числом и меткой бывает вставка со «вторым мнением» — та же координата
    // градусами-минутами-секундами: «43.240200 (43°14'24.7") с. ш. 45.066600 …» (06-ИЦ-01).
    [GeneratedRegex(@"(\d{2,3}[\.,]\d+)\s*(?:\([^)]{5,30}\))?\s*[°""'оО]*\s*(с\.ш\.?|ю\.ш\.?|n|s)[,;/\s]+(\d{2,3}[\.,]\d+)\s*(?:\([^)]{5,30}\))?\s*[°""'оО]*\s*(в\.д\.?|з\.д\.?|e|w)", RegexOptions.IgnoreCase)]
    private static partial Regex DecDirRx();

    // «СШ: 43°10'18.3" ВД: 132°04'59.4"» (25-ПЦ-01), «Ш: 56° 16' 48.6" Д:30° 31' 40.4"» (60-01-06),
    // «широта 53°48'28.4", долгота 88°12'10.1"» (42-21-02) — метка стоит ПЕРЕД градусами
    // и записана без точек, поэтому под шаблоны с «с.ш.» не подходила.
    [GeneratedRegex(@"(?:СШ|Ш|широта)\s*:?\s*(\d{1,3})\s*°\s*(\d{1,2})\s*'\s*(\d+(?:[.,]\d+)?)\s*""?[,;\s]*(?:ВД|Д|долгота)\s*:?\s*(\d{2,3})\s*°\s*(\d{1,2})\s*'\s*(\d+(?:[.,]\d+)?)\s*""?", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledDmsRx();

    // «Координаты WGS широта 51.495996°, WGS долгота 38.626761°» (36-ВЦ-20) — то же,
    // но в десятичных градусах.
    [GeneratedRegex(@"широта\s*:?\s*(\d{2,3}[.,]\d{4,})\s*°?[,;\s]*(?:WGS\s*)?долгота\s*:?\s*(\d{2,3}[.,]\d{4,})", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledDecRx();

    // «62.072731N, 42.790079Е» (29-01-02) — латинские/кириллические буквы направления
    // ВПЛОТНУЮ за числом. Кириллическая «Е» неотличима от латинской «E».
    [GeneratedRegex(@"(\d{2,3}[.,]\d{4,})\s*°?\s*[NnСс][,;\s]+(\d{2,3}[.,]\d{4,})\s*°?\s*[EeЕе]")]
    private static partial Regex DecDirSuffixRx();

    // «N50.69295729 Е37.14713081» (31-БО-16), «N55,941756° Е60,806280°» (74-50-03) —
    // буква направления вплотную ПЕРЕД числом, без пробела.
    [GeneratedRegex(@"[NnСс]\s*(\d{2,3}[.,]\d{4,})\s*°?[,;\s]+[EeЕе]\s*(\d{2,3}[.,]\d{4,})\s*°?")]
    private static partial Regex DecDirPrefixGluedRx();

    // «57.728227°С- 40.897347°В» (44-КЦ-01) — однобуквенные метки после знака градуса.
    [GeneratedRegex(@"(\d{2,3}[.,]\d{4,})\s*°\s*[СC]\s*[-–—]?\s*(\d{2,3}[.,]\d{4,})\s*°\s*[ВB]")]
    private static partial Regex DecShortDirRx();

    // «координаты: 44°59'47.000", 38°55'29.600"» — пара «градусы-минуты-секунды» БЕЗ меток
    // направления вообще (23-КК-10, 81-99-01, 46-01-12 — сотни документов). Самый широкий
    // шаблон, поэтому применяется последним и только если пара проходит проверку диапазонов
    // территории РФ: у азимутов и секторов ЗОЗ («в секторе 196,15°-199,85°») нет минут и
    // секунд, а случайная пара «градус-минута-секунда» из техтекста в диапазон не попадёт.
    // Дробная часть секунд бывает разорвана пробелом: «38°5'31. 70"» (23-КК-10),
    // «55°48'27. 25"» (77-01-09). Терминатором секунд, кроме кавычки, изредка
    // оказывается ещё один знак градуса: «N55°10'18.7 °» (74-50-03).
    [GeneratedRegex(@"(\d{1,3})\s*°\s*(\d{1,2})\s*'\s*(\d+(?:\s*[.,]\s*\d+)?)\s*[""°][,;/\s]+(\d{2,3})\s*°\s*(\d{1,2})\s*'\s*(\d+(?:\s*[.,]\s*\d+)?)\s*[""°]")]
    private static partial Regex DmsBareRx();

    // «43.232426*, 46.869779*» (05-01-02) — десятичная пара со знаком градуса, но
    // вообще без меток направления.
    [GeneratedRegex(@"(\d{2,3}[.,]\d{4,})\s*[°*][,;/\s]+(\d{2,3}[.,]\d{4,})\s*[°*]")]
    private static partial Regex DecBareDegRx();

    // «51.498222 (51°29'53.6") / 46.125556 (46°7'32.0")» (64-01-02),
    // «53.344444 (53°20'40.0"); 59.063861 (59°3'49.9")» (74-50-03) — десятичная пара,
    // у каждой координаты в скобках продублирована запись градусами-минутами-секундами.
    [GeneratedRegex(@"(\d{2,3}\.\d{4,})\s*\([^)]{5,30}\)\s*[,;/]\s*(\d{2,3}\.\d{4,})\s*\([^)]{5,30}\)")]
    private static partial Regex DecWithDmsInParensRx();

    // «координаты объекта 52,118250, 47,200283» (64-01-02) — запятая и как десятичный
    // разделитель, и как разделитель пары. Требуем 5+ знаков дробной части: у обычных
    // чисел техтекста («мощность 20,5 Вт») такой точности не бывает.
    [GeneratedRegex(@"(\d{2,3},\d{5,})\s*,\s*(\d{2,3},\d{5,})")]
    private static partial Regex DecCommaPairRx();

    // «N51.1810.02 Е37.5407.29» (31-БО-16) — потерянные знаки градуса и минуты: это
    // 51°18'10.02" и 37°54'07.29". После точки идут слитно минуты и целые секунды,
    // затем точка и дробная часть. Опознаётся по букве направления вплотную перед числом.
    [GeneratedRegex(@"[NnСс](\d{2,3})\.(\d{2})(\d{2})\.(\d{1,2})\s+[EeЕе](\d{2,3})\.(\d{2})(\d{2})\.(\d{1,2})")]
    private static partial Regex DottedDmsRx();

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

    // «60?43?07.2''с.ш. 114?56'25.7''в.д.» (14-01-01) — знаки градуса и минуты потерялись
    // при перекодировке и стали «?». Восстанавливаем только в связке «число?число?число»
    // или «число?число'», то есть внутри координаты, а не в обычном тексте с вопросами.
    [GeneratedRegex(@"(\d{1,3})\?(\d{1,2})\?(?=\d)")]
    private static partial Regex QuestionDmsRx();

    [GeneratedRegex(@"(\d{1,3})\?(?=\d{1,2}\s*')")]
    private static partial Regex QuestionDegreeRx();

    // «44.862051о с.ш.» (23-КК-10) — русская «о» вместо знака градуса. Меняем только
    // перед меткой направления, иначе пострадают обычные слова, начинающиеся с «о».
    [GeneratedRegex(@"(\d)\s*[оО](?=\s*[сcюСЮвВзЗ]\s*\.?\s*[шШдД])")]
    private static partial Regex RuOhDegreeRx();

    // «6Г18'56.7"Е» (74-50-03) — распознаватель прочитал «1°» как «Г»: это 61°18'56.7".
    // Замена узкая: только между цифрой и парой «минуты-апостроф», и результат всё равно
    // проходит проверку диапазонов, поэтому ошибочная подстановка не попадёт в вывод.
    [GeneratedRegex(@"(\d)Г(?=\d{1,2}\s*'\s*\d)")]
    private static partial Regex OcrGeDegreeRx();

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

        // Сначала — раздел «Проектная документация»: там назван предмет заключения, то есть
        // та самая станция. Дальше по тексту попадаются координаты соседних станций и
        // прямые ошибки копирования: в 02-БЦ-01-002246 заголовок описывает станцию в Уфе
        // «(54.77908 56.031667)», а ниже стоит «координаты 55°24'32.54", 56°37'15.42"»
        // (деревня Яман-Порт) — по метке нашлась бы именно она.
        var projStart = fullText.IndexOf("Проектная документация", StringComparison.OrdinalIgnoreCase);
        if (projStart >= 0)
        {
            var projEnd = fullText.IndexOf("СООТВЕТСТВУЕТ", projStart, StringComparison.OrdinalIgnoreCase);
            var slice = projEnd > projStart
                ? fullText[projStart..projEnd]
                : fullText[projStart..Math.Min(projStart + 1500, fullText.Length)];
            var fromProj = ExtractFrom(slice);
            if (fromProj != null) return fromProj;
        }

        return ExtractFrom(fullText);
    }

    /// <summary>Ищет координаты в переданном фрагменте: сначала по меткам, затем по всему тексту.</summary>
    private static string? ExtractFrom(string text)
    {
        // Сначала пробуем после метки «Географические координаты» — самый надёжный контекст.
        var labelMatch = GeoLabelRx().Match(text);
        if (labelMatch.Success)
        {
            var fromLabel = TryParseLine(labelMatch.Groups[1].Value.Trim());
            if (fromLabel != null) return fromLabel;
        }

        // Потом рядом с более коротким «Координаты».
        var coordLabelMatch = CoordLabelRx().Match(text);
        if (coordLabelMatch.Success)
        {
            var fromLabel = TryParseLine(coordLabelMatch.Groups[1].Value.Trim());
            if (fromLabel != null) return fromLabel;
        }

        // И как фоллбэк — по всему фрагменту.
        return TryParseLine(text);
    }

    private static string? TryParseLine(string text)
    {
        // TryDmsCompact идёт раньше TryDmsWithSeparateDirection: он требует ОБЕ метки
        // направления на своих местах, поэтому в записях вида «54°43'44,8" с.ш. 55°56'23,4 в.д.»
        // не спутает долготу с широтой (шаблон «с.ш. <число>» иначе цепляет вторую координату).
        return TryLabeledLatLon(text)
            ?? TryDecimalDegrees(text)
            ?? TryDecimalWithDirection(text)
            ?? TryLabeledDms(text)
            ?? TryDmsCompact(text)
            ?? TryDmsWithSeparateDirection(text)
            // Пара без меток направления — последняя попытка: шаблон самый широкий,
            // и запускать его стоит, только когда все явные формы записи не подошли.
            ?? TryDmsBare(text);
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
        text = QuestionDmsRx().Replace(text, "$1°$2'");
        text = QuestionDegreeRx().Replace(text, "$1°");
        text = RuOhDegreeRx().Replace(text, "$1°");
        text = OcrGeDegreeRx().Replace(text, "${1}1°");
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
        foreach (var rx in (ReadOnlySpan<Regex>)[
                     ColonDirRx(), ParenDirRx(), GluedDirRx(), LabeledDecRx(),
                     DecDirSuffixRx(), DecDirPrefixGluedRx(), DecShortDirRx()])
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

    // «СШ: 43°10'18.3" ВД: 132°04'59.4"» / «широта 53°48'28.4", долгота 88°12'10.1"» —
    // метка направления словом или одной буквой ПЕРЕД значением.
    private static string? TryLabeledDms(string text)
    {
        foreach (Match m in LabeledDmsRx().Matches(text))
        {
            double? lat = DmsToDec(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            double? lon = DmsToDec(m.Groups[4].Value, m.Groups[5].Value, m.Groups[6].Value);
            if (lat is null || lon is null) continue;
            var formatted = FormatIfValid(lat.Value, lon.Value);
            if (formatted != null) return formatted;
        }
        return null;
    }

    // Пары координат без каких-либо меток направления: «координаты: 44°59'47.000",
    // 38°55'29.600"», «43.232426*, 46.869779*», «52,118250, 47,200283».
    // Диапазонная проверка здесь не «санитарная», а единственная защита от ложных
    // срабатываний, поэтому перебираем все совпадения и возвращаем первое правдоподобное.
    private static string? TryDmsBare(string text)
    {
        // Запись с потерянными знаками градуса/минуты — у неё секунды разнесены
        // по двум группам («…1810.02» → 10 целых и 02 дробных).
        foreach (Match m in DottedDmsRx().Matches(text))
        {
            double? lat = DmsToDec(m.Groups[1].Value, m.Groups[2].Value, $"{m.Groups[3].Value}.{m.Groups[4].Value}");
            double? lon = DmsToDec(m.Groups[5].Value, m.Groups[6].Value, $"{m.Groups[7].Value}.{m.Groups[8].Value}");
            if (lat is null || lon is null) continue;
            var formatted = FormatIfValid(lat.Value, lon.Value);
            if (formatted != null) return formatted;
        }

        foreach (Match m in DmsBareRx().Matches(text))
        {
            double? lat = DmsToDec(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            double? lon = DmsToDec(m.Groups[4].Value, m.Groups[5].Value, m.Groups[6].Value);
            if (lat is null || lon is null) continue;
            var formatted = FormatIfValid(lat.Value, lon.Value);
            if (formatted != null) return formatted;
        }

        foreach (var rx in (ReadOnlySpan<Regex>)[DecWithDmsInParensRx(), DecBareDegRx(), DecCommaPairRx()])
        {
            foreach (Match m in rx.Matches(text))
            {
                if (TryParseCoord(m.Groups[1].Value, out var lat)
                    && TryParseCoord(m.Groups[2].Value, out var lon))
                {
                    var formatted = FormatIfValid(lat, lon);
                    if (formatted != null) return formatted;
                }
            }
        }

        return null;
    }

    // Перевод «градусы/минуты/секунды» в десятичные градусы.
    // Возвращает null вместо исключения при некорректном числе (опечатки в исходных
    // документах, например «.29.1» вместо «29.1» — лишний разделитель). Раньше здесь
    // падал необработанный FormatException и весь файл уходил в ошибку чтения.
    private static double? DmsToDec(string deg, string min, string sec)
    {
        if (!int.TryParse(deg, out int d)) return null;
        if (!int.TryParse(min, out int mi)) return null;
        // Пробел внутри дробной части секунд — частый артефакт вёрстки: «27. 25"»,
        // «31. 70"» (77-01-09, 23-КК-10). Без очистки double.TryParse возвращал false
        // и координата терялась целиком.
        if (!double.TryParse(sec.Replace(" ", "").Replace(',', '.'), NumberStyles.Float, Inv, out double s)) return null;
        // Минуты и секунды в DMS всегда < 60. Без проверки случай «44'.97"» (лишняя точка +
        // пропущенная цифра) тихо давал бы 97 «секунд» — физически невозможное значение (86-ХЦ-23).
        // Ровно 60 секунд — не опечатка, а огрубление в исходнике («53°27'60"», 02-БЦ-01):
        // трактуем как +1 минуту. Всё, что больше, по-прежнему считаем мусором.
        if (mi is < 0 or >= 60 || s is < 0 or > 60) return null;
        return d + mi / 60.0 + s / 3600.0;
    }
}
