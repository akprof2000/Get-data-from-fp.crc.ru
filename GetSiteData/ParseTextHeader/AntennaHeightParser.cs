using System.Globalization;
using System.Text.RegularExpressions;

namespace ParseTextHeader;

/// <summary>
/// Извлекает высоты подвеса антенн (в метрах) из текста документа.
/// Основной признак — строка с «высота подвеса …: 24, 27 м» (варианты: «антенны»,
/// «относительно земли», «(от земли)», окончания «м/метра/метров»); второй по массовости
/// формат — «высота установки антенны над уровнем земли/кровли: 39,48/- м».
/// Значения перечисляются через «,», «;» или «/»; запятая БЕЗ пробела после — десятичный
/// разделитель («62,5»), с пробелом — разделитель списка («24, 27»).
/// Результат — массив уникальных высот в порядке появления.
/// </summary>
public static partial class AntennaHeightParser
{
    // «высота подвеса антенны относительно земли: 29; 31 метра», «высота подвеса (от земли): 26,95; 29,8 м»
    [GeneratedRegex(@"высот[аы]\s+подвеса[^:\n]{0,60}?[:\s(]\s*(?:высота\s+)?([0-9][0-9.,;/\s-]{0,80}?(?:\sи\s[0-9][0-9.,]{0,6})?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex PodvesaRx();

    // «Высота подвеса от уровня земли, м +69 +69 +69» (53-01-01) — плюсовые значения
    // сразу после единицы, без двоеточия; база — из заголовка.
    [GeneratedRegex(@"высота\s+подвеса\s+от\s+уровня\s+(земли|кровли),\s*м\s+((?:\+[0-9]{1,3}(?:[.,][0-9]{1,2})?\s*)+)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex PodvesPlusListRx();

    // «Высота установки антенны от поверхности земли (м): 29» (71-ТЦ-04)
    [GeneratedRegex(@"высот[аы]\s+установки[^\n(]{0,60}?\(м\)\s*:\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex UstSkobkaMRx();

    // «Высота установки антенн относительно земли - 40,0 м», «Высота установки
    // антенн - 28,5м» (76-01-10) — значение(я) после тире.
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн\w*(?:\s+относительно\s+(?:уровня\s+)?земли)?\s*[-–—]\s*([0-9][0-9.,;/\s-]{0,60}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex UstTireRx();

    // «высота установки антенн (…) от поверхности земли, м:» + значения построчно.
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн[^\n]{0,120}?от\s+поверхности\s+земли,\s*м[^\n]{0,25}:\s*\r?\n((?:\s*[0-9]{1,3}(?:[.,][0-9]{1,2})?(?:\([0-9.,]{1,8}\))?\s*\r?\n){1,30})", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex LineListHeaderRx();

    [GeneratedRegex(@"^\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?)(?:\(([0-9.,]{1,8})\))?\s*$", RegexOptions.Multiline, 20000)]
    private static partial Regex LineValueRx();

    // «Высота установки антенн от поверхности земли (м) БС: 26(0)/ 26(0). РРС: 30(0)»
    // (78-01-05): единица в скобках, значения с метками групп, «(0)» — от кровли.
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн[^\n(]{0,50}?\(м\)\s*((?:БС|РРС)\s*:[^\n]{1,100})", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex UstSkobkaMetkiRx();

    // «высота установки антенны 45,0 м относительно уровня земли» (55-01-04)
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн\w*\s+([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м\b", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex UstanovkiPryamRx();

    // «высота установки антенны над уровнем земли/кровли: 39,48/- м» (86-ХЦ-23 и др.)
    // «+» в списке: «над уровнем земли/кровли: 20.8/+5.8 м» (64-01-02).
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн[^:\n]{0,60}:\s*([0-9][0-9.,;/+\s-]{0,80}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex UstanovkiRx();

    // «антенны установлены на отметке 34,8м, 33,5м относительно уровня земли» (74-50-03).
    // Ленивый ограниченный захват вместо «(группа)+»: вложенные квантификаторы
    // на длинных документах уходили в катастрофический бэктрекинг — процесс
    // намертво занимал ядро и переставал двигаться.
    [GeneratedRegex(@"на\s+отметк[а-яё]*\s+([0-9][0-9.,мМ\s;и]{0,80}?)относительно\s+уровня\s+земли", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex NaOtmetkeRx();

    // «Высота установки антенн от поверхности земли и от опорной поверхности:»
    // (25-ПЦ, 86-ХЦ) — значения строками ниже: «FA1/FA2 - 32 м», «26/6//23/3», «:23».
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн\s+от\s+поверхности\s+земли\s*(?:и\s+от|/)\s*опорной\s+поверхности[^\n]{0,25}:", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex OporIHeaderRx();

    // Пара «26/6» или «29,5/-» в окне после заголовка (земля/опора).
    [GeneratedRegex(@"(?<![0-9.,/])([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*/\s*(-|[0-9]{1,3}(?:[.,][0-9]{1,2})?)(?![0-9.,/])", RegexOptions.None, 20000)]
    private static partial Regex OporPairRx();

    // «FA1/FA2/FA3 - 32 м», «антенна ATU… - 22» — значение от земли после тире
    // (единица бывает опущена — значение в конце строки).
    [GeneratedRegex(@"[-–—]\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*(?:м\b|(?=\s*[;.]?\s*(?:\r?\n|$)))", RegexOptions.Multiline, 20000)]
    private static partial Regex OporSingleRx();

    // «Высоты установки антенн от поверхности земли: 19.5/19.5/19.5;» (86-ХЦ, 2023).
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн\s+от\s+поверхности\s+земли\s*:\s*([0-9][0-9.,/\s]{0,60}?)(?=\s*[;а-яА-ЯёЁ])", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex UstPovColonListRx();

    // «высота подвеса антенн - 16(5.5)/ 16(5.5)/ 16(5.5) м» — кровля в скобках (56-01-09, 2023).
    [GeneratedRegex(@"высот[аы]\s+подвеса\s+антенн\s*[-–—]\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?)\(([0-9]{1,3}(?:[.,][0-9]{1,2})?)\)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex PodvesSkobkaRx();

    // «высота установки антенн от уровня земли - 28.0 м» (40-01-05, 2023).
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн\s+от\s+уровня\s+земли\s*[-–—]\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex OtUrovnyaTireRx();

    // «высота установки антенны (ф.ц.а.) от земли 25,4 м» (26-01-05, 2023).
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн[^\n]{0,25}?от\s+земли\s+([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex OtZemliPryamRx();

    // «А1, А2, А3 - 34,95м над уровнем земли и 3,95м над уровнем крыши» (39-КС, 2023).
    [GeneratedRegex(@"[-–—]\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м\s+над\s+уровнем\s+земли(?:[^\n]{0,10}?([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м\s+над\s+уровн)?", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex NadUrovnemRx();

    // «высота установки антенн от земли - 27.0 м, от уровня кровли (площадки) - 2.5 м» (26-01-05).
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн\w*\s+от\s+земли\s*[-–—]\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м(?:[^\n]{0,40}?кровли[^\n]{0,15}?[-–—]\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м)?", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex OtZemliTireRx();

    // «Высота установки антенн: фазовый центр антенн от земли: 24 м» (64-01-02).
    [GeneratedRegex(@"фазовый\s+центр\s+антенн\s+от\s+земли\s*:\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex FazCentrOtZemliRx();

    // «высота установки антенн от поверхности земли 33 м» — без тире (27-99-24).
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн\w*\s+от\s+поверхности\s+земли\s+([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м\b", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex OtPovPryamRx();

    // «высота установки антенн, м:» + строки «…: 48;» — значение после двоеточия
    // в каждой строке окна (22-01-46).
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн,\s*м\s*:\s*\r?\n", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex UstMLineHeaderRx();

    [GeneratedRegex(@":\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*;", RegexOptions.None, 20000)]
    private static partial Regex ColonValueRx();

    // «высота размещения антенн: 25 м», «антенны размещены на высоте 19-23 м»
    [GeneratedRegex(@"высот[аы]\s+размещения\s+антенн[^:\n]{0,40}[:\s]\s*([0-9][0-9.,;/\s-]{0,80}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex RazmeshcheniyaRx();

    [GeneratedRegex(@"антенн\w*[^\n]{0,30}?на\s+высоте\s*([0-9][0-9.,;/\s-]{0,40}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex NaVysoteRx();

    // Единица ПЕРЕД значениями: «высота установки антенны от поверхности земли, м: 50; 49,5»
    [GeneratedRegex(@"высот[аы][^\n:]{0,70}?,\s*м\s*[:;]\s*([0-9][0-9.,;/\s]{0,60}?)(?=\s*(?:[-–;]|[а-яА-Яa-zA-Z(]|$))", RegexOptions.IgnoreCase | RegexOptions.Multiline, 20000)]
    private static partial Regex UnitBeforeColonRx();

    // «Высота установки антенн от поверхности земли, м: БС - 42/42/42; РРС - 42, 42»
    // — значения с метками групп после «м:».
    [GeneratedRegex(@"высот[аы][^\n:]{0,80}?,\s*м\s*:\s*((?:БС|РРС|indoor)[^\n]{0,45}?[:\-–—][^\n]{1,120})", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex MetkiPosleMRx();

    // «высота установки от поверхности земли / от опорной поверхности – 13,5/9 м»
    [GeneratedRegex(@"высот[аы]\s+установки\s+(?:антенн\w*\s+)?от\s+поверхности[^\n:0-9]{0,60}?[-–—:]\s*([0-9][0-9.,;/\s-]{0,40}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex OtPoverkhnostiRx();

    // Табличная форма: «Высота установки антенны над уровнем земли (над уровнем кровли), м 27.50, Азимут»
    [GeneratedRegex(@"высот[аы]\s+(?:установки|подвеса|размещения)\s+антенн[^\n:]{0,70}?,\s*м\s+([0-9]+(?:[.,][0-9]+)?)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex TableFormRx();

    // «…с азимутами 50/150/310 … на высоте подвеса 38 метров»
    [GeneratedRegex(@"на\s+высоте\s+подвеса\s+([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex NaVysotePodvesaRx();

    // Строка таблицы «…Высота подвеса…отн. земли отн. кровли…» без координатной
    // колонки: хвост строки «21.5 2.5 20 -5 2.72» — высоты, азимут, наклон, мощность.
    [GeneratedRegex(@"([0-9]{1,3}(?:[.,][0-9]+)?)\s+([0-9]{1,3}(?:[.,][0-9]+)?)\s+[0-9]{1,3}\s+-?[0-9]+\s+[0-9]+(?:[.,][0-9]+)?\s*$", RegexOptions.Multiline, 20000)]
    private static partial Regex TailTableRowRx();

    [GeneratedRegex(@"высота\s+подвеса[\s\S]{0,200}?отн\.\s*земли\s+отн\.\s*кровли", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex TailTableHeaderRx();

    // Строка таблицы с высотой «32,5/-» перед режимом работы «Круглосуточно».
    [GeneratedRegex(@"([0-9]{1,3}(?:[.,][0-9]+)?)\s*/\s*(-|[0-9]{1,3}(?:[.,][0-9]+)?)\s+Круглосуточно", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex KruglosutochnoRx();

    // «высота установки антенны от уровня земли/от уровня кровли 54,5/5,2 м» (26-01-05).
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн\w*\s+от\s+уровня\s+земли\s*/\s*от\s+уровня\s+кровли\s+([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*/\s*(-|[0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex OtUrovnyaParaRx();

    // Таблица «от уровня земли/от опорной поверхности, м» — пара высот в КОНЦЕ строки
    // после наклона «0/-2»: «… 0 0/-2 22,0/-» (63-СЦ, апрель).
    [GeneratedRegex(@"от\s+уровня\s+земли\s*/\s*от\s+опорной\s+поверхности,\s*м", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex TailPairHeaderRx();

    [GeneratedRegex(@"-?[0-9]+/-?[0-9]+\s+([0-9]{1,3}(?:[.,][0-9]{1,2})?)/(-|[0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*$", RegexOptions.Multiline, 20000)]
    private static partial Regex TailPairRowRx();

    // Таблица «от поверхности земли (кровли), м;» — пара «15.7/9.2» перед азимутом
    // и наклоном «0/-4» (10-КЦ, апрель).
    [GeneratedRegex(@"от\s+поверхности\s+земли\s*\(кровли\),\s*м\s*;", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex ZemKrovTblHeaderRx();

    [GeneratedRegex(@"\s([0-9]{1,3}(?:[.,][0-9]{1,2})?)/([0-9]{1,3}(?:[.,][0-9]{1,2})?|-)\s+[0-9]{1,3}\s+-?[0-9]+/-?[0-9]+\s", RegexOptions.None, 20000)]
    private static partial Regex ZemKrovTblRowRx();

    // «Высота подвеса антенн (м): БС: 38.0/38.0/38.0; РРС: 39.0» — единица в скобках,
    // значения с метками групп; числа выцепляем из остатка строки.
    [GeneratedRegex(@"высот[аы]\s+подвеса[^\n(]{0,40}?\(м\)\s*:\s*([^\n]{1,100})", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex PodvesMetkiRx();

    // «на антенной мачте …, высотой 25 м», «опоре высотой 72 м» — высота сооружения,
    // на котором размещены антенны (единственная высота в документе такого типа).
    [GeneratedRegex(@"(?:мачт|опор|башн|столб|трубостойк)[а-яё]*[^\n.;]{0,60}?высотой\s+([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м\b", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex MachtaRx();

    // «высота подвеса отн. зем./кров. 24,2/2,2» — пара «земля/кровля» без единицы
    // («40/-» — от кровли значения нет).
    [GeneratedRegex(@"высот[аы]\s+подвеса\s+отн\.?\s*зем(?:ли|\.)?\s*/\s*кров(?:ли|\.)?,?\s*(?:м\.?)?\s*[-–—]?\s*([0-9]+(?:,[0-9]+)?)\s*/\s*([0-9]+(?:,[0-9]+)?|-)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex ZemKrovRx();

    // «на кровле здания, Н=14м», «опора Н=+33.00 м» — высота размещения через «Н=».
    [GeneratedRegex(@"[НH]\s*=\s*\+?([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м\b", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex HEqualsRx();

    // «Н=+17.45,» — без единицы, но с явным плюсом отметки высоты (23-КК-10).
    [GeneratedRegex(@"[НH]\s*=\s*\+([0-9]{1,3}(?:[.,][0-9]{1,2})?)(?![\d.,]*\s*(?:кВ|МГц|Вт|дБ))", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex HEqualsPlusRx();

    // «высота фазового центра антенн от уровня земли/от уровня кровли - 32/- м; 32/- м»
    [GeneratedRegex(@"высот[аы]\s+(?:установки\s+)?фазового\s+центра[^\n:0-9]{0,70}?[-–—:\s]\s*([0-9][0-9.,;/\s-]{0,80}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex FazovogoRx();

    // «Высота от уровня земли до точки подвеса проектируемых антенн - 22,0 м»
    [GeneratedRegex(@"высот[аы][^\n]{0,50}?до\s+точки\s+подвеса[^\n:0-9]{0,50}?[-–—:]?\s*([0-9][0-9.,;/\s-]{0,40}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex TochkiPodvesaRx();

    // «высота установки антенн от поверхности земли (кровли), м - 75.0 (-)»:
    // единица перед тире, значение от кровли — в скобках («(-)» — нет значения).
    [GeneratedRegex(@"высот[аы][^\n:]{0,80}?,\s*м\s*[-–—]\s*([0-9]+(?:[.,][0-9]+)?)(?:\s*\(([^)\n]{1,12})\))?", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex UnitDashRx();

    /// <summary>
    /// Все высоты подвеса из документа (уникальные пары «высота+база», в порядке
    /// появления); null — не найдены. База отсчёта берётся из контекста строки:
    /// «земля», «кровля» или null, когда в документе не сказано.
    /// </summary>
    /// <summary>
    /// Диагностика (режим --diag-heights): прогоняет ПОЛНЫЙ разбор и возвращает,
    /// какой шаблон дал каждую высоту.
    /// </summary>
    internal static List<(string Pattern, AntennaHeight Height)> DiagnoseHeights(string fullText)
    {
        _trace = [];
        try
        {
            _ = Extract(fullText);
            return _trace;
        }
        finally { _trace = null; }
    }

    // За списком высот в тех же строках идут ДРУГИЕ характеристики антенн —
    // азимут, угол места, ширина диаграммы, усиление. Окно поиска значений
    // обязано на них заканчиваться: иначе «Азимут, град.: 190/350» разбирался
    // как пара «высота от земли / от кровли» и в данные попадали градусы
    // (11-РЦ-09: высоты [90,3,190,350,64,15.5,2.3,8,1] вместо [90,64]).
    [GeneratedRegex(@"(?:азимут|\bугол\b|\bуглы\b|диаграмм|град\.|градус|дБи|коэффициент\s+усилен|поляризац|мощност|диапазон\s+частот|тип\s+модуляц|наклон)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex ForeignFieldRx();

    private static string CutAtForeignField(string window)
    {
        var m = ForeignFieldRx().Match(window);
        return m.Success ? window[..m.Index] : window;
    }

    // Трассировка источника каждой высоты: включается только режимом --diag-heights.
    // Без неё по списку высот в JSON нельзя понять, какой шаблон дал ложное значение.
    [ThreadStatic]
    private static List<(string Pattern, AntennaHeight Height)>? _trace;

    private static void AddTracedTo(List<AntennaHeight> list, string pattern, AntennaHeight h)
    {
        list.Add(h);
        _trace?.Add((pattern, h));
    }

    public static List<AntennaHeight>? Extract(string fullText)
    {
        var result = new List<AntennaHeight>();
        var seen = new HashSet<(double, string?, string?)>();

        foreach (var rx in (ReadOnlySpan<Regex>)[PodvesaRx(), UstanovkiRx(), RazmeshcheniyaRx(),
                     NaVysoteRx(), UnitBeforeColonRx(), TableFormRx(), FazovogoRx(),
                     TochkiPodvesaRx(), UnitDashRx(), HEqualsRx(), PodvesMetkiRx(),
                     NaVysotePodvesaRx(), OtPoverkhnostiRx(), MetkiPosleMRx(), HEqualsPlusRx(),
                     UstanovkiPryamRx(), UstSkobkaMRx(), UstTireRx(), UstSkobkaMetkiRx()])
        {
            foreach (Match m in rx.Matches(fullText))
            {
                var (antenna, number) = FindAntenna(fullText, m);
                foreach (var (h, baseKind) in ParseWithBase(m.Value, m.Groups[1].Value))
                {
                    // Санитарные рамки: подвес антенны БС — единицы…сотни метров.
                    // Отсекаем мусор вроде годов и координат, попавших в захват.
                    if (h is < 1 or > 500) continue;
                    if (seen.Add((h, baseKind, antenna)))
                        AddTracedTo(result, "UstSkobkaMetkiRx", new AntennaHeight(h, baseKind, antenna, number));
                }
            }
        }

        // Пара «отн. зем./кров. 24,2/2,2»: первое значение — от земли, второе — от кровли.
        foreach (Match m in ZemKrovRx().Matches(fullText))
        {
            var (antenna, number) = FindAntenna(fullText, m);
            if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                && seen.Add((gh, BaseGround, antenna)))
                AddTracedTo(result, "ZemKrovRx", new AntennaHeight(gh, BaseGround, antenna, number));
            if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, antenna)))
                AddTracedTo(result, "ZemKrovRx", new AntennaHeight(rh, BaseRoof, antenna, number));
        }

        // Значение «от кровли» из формы «…, м - 75.0 (12.5)»: скобочная часть —
        // вторая база, «(-)» — значения нет.
        foreach (Match m in UnitDashRx().Matches(fullText))
        {
            if (!m.Groups[2].Success || !TryParse(m.Groups[2].Value.Trim('-', ' '), out var roofH)) continue;
            if (roofH is < 1 or > 500) continue;
            var (antenna, number) = FindAntenna(fullText, m);
            if (seen.Add((roofH, BaseRoof, antenna)))
                AddTracedTo(result, "UnitDashRx", new AntennaHeight(roofH, BaseRoof, antenna, number));
        }

        // Перечень «по каждой антенне»: «тип, высота установки антенны от поверхности
        // земли, азимут…: D6-XI65… - 3 шт., 43 м, 60/180/290 град.» — высота идёт
        // сразу за «шт.,». Формат опознаём по заголовку перечня.
        if (ShtHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in ShtItemRx().Matches(fullText))
            {
                if (!TryParse(m.Groups[1].Value, out var h) || h is < 1 or > 500) continue;
                if (seen.Add((h, BaseGround, null)))
                    AddTracedTo(result, "ShtItemRx", new AntennaHeight(h, BaseGround, null, null));
            }
        }

        // Таблица с колонкой «Высота (м) подвеса антенн от уровня земли/крыши»:
        // в строках высоты помечены плюсом («… 2х60,0 +61,0 120° …»).
        var plusHeader = PlusTableHeaderRx().Match(fullText);
        if (WideTableHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in WideTableRowRfRx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                    && seen.Add((gh, BaseGround, null)))
                    AddTracedTo(result, "WideTableRowRfRx", new AntennaHeight(gh, BaseGround, null, null));
                if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                    && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                    AddTracedTo(result, "WideTableRowRfRx", new AntennaHeight(rh, BaseRoof, null, null));
            }
        }

        if (plusHeader.Success)
        {
            foreach (Match m in PlusValueRx().Matches(fullText, plusHeader.Index))
            {
                if (!TryParse(m.Groups[1].Value, out var h) || h is < 1 or > 500) continue;
                if (seen.Add((h, BaseGround, null)))
                    AddTracedTo(result, "PlusValueRx", new AntennaHeight(h, BaseGround, null, null));
            }
        }

        // Широкая таблица с колонками «… Высота подвеса … Координаты установки (X;Y) …»:
        // в строке подряд идут «h_земли h_кровли азимут наклон X;Y» — якорь «X;Y»
        // однозначно метит поле координат, две высоты стоят за три поля до него.
        if (WideTableHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in WideTableRowRx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                    && seen.Add((gh, BaseGround, null)))
                    AddTracedTo(result, "WideTableRowRx", new AntennaHeight(gh, BaseGround, null, null));
                if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                    && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                    AddTracedTo(result, "WideTableRowRx", new AntennaHeight(rh, BaseRoof, null, null));
            }
        }

        // Таблица с последней колонкой «Высота от земли, м».
        if (LastColHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in LastColRowRx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var h) && h is >= 1 and <= 500
                    && seen.Add((h, BaseGround, null)))
                    AddTracedTo(result, "LastColRowRx", new AntennaHeight(h, BaseGround, null, null));
            }
        }

        // «Высота установки антенны от поверхности земли / от опорной поверхности»
        // с переносом строки, значения ниже: «БС - Ант.1..3: 22/-м; РРС - 26,7/- м.»
        foreach (Match hm in OporPovHeaderRx().Matches(fullText))
        {
            var winEnd = Math.Min(fullText.Length, hm.Index + hm.Length + 250);
            var window = CutAtForeignField(fullText[(hm.Index + hm.Length)..winEnd]);
            bool gotPair = false;
            foreach (Match m in SlashPairValueRx().Matches(window))
            {
                gotPair = true;
                if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                    && seen.Add((gh, BaseGround, null)))
                    AddTracedTo(result, "SlashPairValueRx", new AntennaHeight(gh, BaseGround, null, null));
                if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                    && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                    AddTracedTo(result, "SlashPairValueRx", new AntennaHeight(rh, BaseRoof, null, null));
            }
            // Без слэш-пар — одиночные значения «БС - 40 м; РРС - 40 м» (33-ВЛ, апрель).
            if (!gotPair)
            {
                foreach (Match m in OporSingleRx().Matches(window))
                {
                    if (TryParse(m.Groups[1].Value, out var h) && h is >= 1 and <= 500
                        && seen.Add((h, BaseGround, null)))
                        AddTracedTo(result, "OporSingleRx", new AntennaHeight(h, BaseGround, null, null));
                }
            }
        }

        // «высота установки антенн (…) от поверхности земли, м:» и значения
        // построчно ниже: «39\n38\n38…» или «23.6(14.6)» (в скобках — от кровли).
        foreach (Match hm in LineListHeaderRx().Matches(fullText))
        {
            foreach (Match m in LineValueRx().Matches(hm.Groups[1].Value))
            {
                if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                    && seen.Add((gh, BaseGround, null)))
                    AddTracedTo(result, "LineValueRx", new AntennaHeight(gh, BaseGround, null, null));
                if (m.Groups[2].Success && TryParse(m.Groups[2].Value, out var rh)
                    && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                    AddTracedTo(result, "LineValueRx", new AntennaHeight(rh, BaseRoof, null, null));
            }
        }

        // «от уровня земли/от уровня кровли 54,5/5,2 м» (26-01-05).
        foreach (Match m in OtUrovnyaParaRx().Matches(fullText))
        {
            if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                && seen.Add((gh, BaseGround, null)))
                AddTracedTo(result, "OtUrovnyaParaRx", new AntennaHeight(gh, BaseGround, null, null));
            if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                AddTracedTo(result, "OtUrovnyaParaRx", new AntennaHeight(rh, BaseRoof, null, null));
        }

        // Пары высот в конце строк таблиц (63-СЦ и 10-КЦ, апрель).
        if (TailPairHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in TailPairRowRx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                    && seen.Add((gh, BaseGround, null)))
                    AddTracedTo(result, "TailPairRowRx", new AntennaHeight(gh, BaseGround, null, null));
                if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                    && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                    AddTracedTo(result, "TailPairRowRx", new AntennaHeight(rh, BaseRoof, null, null));
            }
        }
        if (ZemKrovTblHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in ZemKrovTblRowRx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                    && seen.Add((gh, BaseGround, null)))
                    AddTracedTo(result, "ZemKrovTblRowRx", new AntennaHeight(gh, BaseGround, null, null));
                if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                    && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                    AddTracedTo(result, "ZemKrovTblRowRx", new AntennaHeight(rh, BaseRoof, null, null));
            }
        }

        // «на отметке 34,8м, 33,5м относительно уровня земли» (74-50-03).
        foreach (Match m in NaOtmetkeRx().Matches(fullText))
        {
            foreach (Match v in LooseNumberRx().Matches(m.Groups[1].Value))
            {
                if (TryParse(v.Groups[1].Value, out var h) && h is >= 1 and <= 500
                    && seen.Add((h, BaseGround, null)))
                    AddTracedTo(result, "LooseNumberRx", new AntennaHeight(h, BaseGround, null, null));
            }
        }

        // «от поверхности земли и от опорной поверхности:» — значения строками ниже
        // (25-ПЦ: «FA1/FA2 - 32 м»; 86-ХЦ: «26/6//23/3», «29,5/-», «:23»).
        foreach (Match hm in OporIHeaderRx().Matches(fullText))
        {
            var winEnd = Math.Min(fullText.Length, hm.Index + hm.Length + 350);
            var window = CutAtForeignField(fullText[(hm.Index + hm.Length)..winEnd]);
            bool got = false;
            foreach (Match m in OporPairRx().Matches(window))
            {
                got = true;
                if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                    && seen.Add((gh, BaseGround, null)))
                    AddTracedTo(result, "OporPairRx", new AntennaHeight(gh, BaseGround, null, null));
                if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                    && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                    AddTracedTo(result, "OporPairRx", new AntennaHeight(rh, BaseRoof, null, null));
            }
            if (!got)
            {
                foreach (Match m in OporSingleRx().Matches(window))
                {
                    if (TryParse(m.Groups[1].Value, out var h) && h is >= 1 and <= 500
                        && seen.Add((h, BaseGround, null)))
                        AddTracedTo(result, "OporSingleRx", new AntennaHeight(h, BaseGround, null, null));
                }
            }
        }

        // «Высота подвеса от уровня земли/кровли, м +69 +69 +69».
        foreach (Match m in PodvesPlusListRx().Matches(fullText))
        {
            var baseKind = m.Groups[1].Value.ToLowerInvariant().StartsWith("зем") ? BaseGround : BaseRoof;
            foreach (Match v in LooseNumberRx().Matches(m.Groups[2].Value))
            {
                if (TryParse(v.Groups[1].Value, out var h) && h is >= 1 and <= 500
                    && seen.Add((h, baseKind, null)))
                    AddTracedTo(result, "LooseNumberRx", new AntennaHeight(h, baseKind, null, null));
            }
        }

        // Таблица с буквенными колонками: высота перед азимутом-диапазоном «0-360»
        // либо перед целым азимутом и наклоном «мех/эл».
        if (LetterTableHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in LetterTableRowRx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var h) && h is >= 1 and <= 500
                    && seen.Add((h, BaseGround, null)))
                    AddTracedTo(result, "LetterTableRowRx", new AntennaHeight(h, BaseGround, null, null));
            }
            foreach (Match m in LetterTableRow3Rx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var h3) && h3 is >= 1 and <= 500
                    && seen.Add((h3, BaseGround, null)))
                    AddTracedTo(result, "LetterTableRowRx", new AntennaHeight(h3, BaseGround, null, null));
            }
            foreach (Match m in LetterTableRow2Rx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var h) && h is >= 1 and <= 500
                    && seen.Add((h, BaseGround, null)))
                    AddTracedTo(result, "LetterTableRowRx", new AntennaHeight(h, BaseGround, null, null));
            }
        }

        // Формы 2023 года: слэш-список после «земли:», «(ф.ц.а.) от земли N м»,
        // «- N м над уровнем земли и M м над уровнем крыши».
        foreach (Match m in UstPovColonListRx().Matches(fullText))
        {
            foreach (Match v in LooseNumberRx().Matches(m.Groups[1].Value))
            {
                if (TryParse(v.Groups[1].Value, out var h) && h is >= 1 and <= 500
                    && seen.Add((h, BaseGround, null)))
                    AddTracedTo(result, "LooseNumberRx", new AntennaHeight(h, BaseGround, null, null));
            }
        }
        foreach (Match m in PodvesSkobkaRx().Matches(fullText))
        {
            if (TryParse(m.Groups[1].Value, out var gh2) && gh2 is >= 1 and <= 500
                && seen.Add((gh2, BaseGround, null)))
                AddTracedTo(result, "PodvesSkobkaRx", new AntennaHeight(gh2, BaseGround, null, null));
            if (TryParse(m.Groups[2].Value, out var rh2) && rh2 is >= 1 and <= 500
                && seen.Add((rh2, BaseRoof, null)))
                AddTracedTo(result, "PodvesSkobkaRx", new AntennaHeight(rh2, BaseRoof, null, null));
        }
        foreach (Match m in OtUrovnyaTireRx().Matches(fullText))
        {
            if (TryParse(m.Groups[1].Value, out var h4) && h4 is >= 1 and <= 500
                && seen.Add((h4, BaseGround, null)))
                AddTracedTo(result, "OtUrovnyaTireRx", new AntennaHeight(h4, BaseGround, null, null));
        }
        foreach (Match m in OtZemliPryamRx().Matches(fullText))
        {
            if (TryParse(m.Groups[1].Value, out var h) && h is >= 1 and <= 500
                && seen.Add((h, BaseGround, null)))
                AddTracedTo(result, "OtZemliPryamRx", new AntennaHeight(h, BaseGround, null, null));
        }
        foreach (Match m in NadUrovnemRx().Matches(fullText))
        {
            if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                && seen.Add((gh, BaseGround, null)))
                AddTracedTo(result, "NadUrovnemRx", new AntennaHeight(gh, BaseGround, null, null));
            if (m.Groups[2].Success && TryParse(m.Groups[2].Value, out var rh)
                && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                AddTracedTo(result, "NadUrovnemRx", new AntennaHeight(rh, BaseRoof, null, null));
        }

        // «от земли - 27.0 м, … кровли … - 2.5 м», «фазовый центр антенн от земли: 24 м»,
        // «от поверхности земли 33 м» — одиночные апрельские формы.
        foreach (Match m in OtZemliTireRx().Matches(fullText))
        {
            if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                && seen.Add((gh, BaseGround, null)))
                AddTracedTo(result, "OtZemliTireRx", new AntennaHeight(gh, BaseGround, null, null));
            if (m.Groups[2].Success && TryParse(m.Groups[2].Value, out var rh)
                && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                AddTracedTo(result, "OtZemliTireRx", new AntennaHeight(rh, BaseRoof, null, null));
        }
        foreach (Match m in FazCentrOtZemliRx().Matches(fullText))
        {
            if (TryParse(m.Groups[1].Value, out var h) && h is >= 1 and <= 500
                && seen.Add((h, BaseGround, null)))
                AddTracedTo(result, "FazCentrOtZemliRx", new AntennaHeight(h, BaseGround, null, null));
        }
        foreach (Match m in OtPovPryamRx().Matches(fullText))
        {
            if (TryParse(m.Groups[1].Value, out var h) && h is >= 1 and <= 500
                && seen.Add((h, BaseGround, null)))
                AddTracedTo(result, "OtPovPryamRx", new AntennaHeight(h, BaseGround, null, null));
        }

        // «высота установки антенн, м:» + значения после двоеточий в строках окна.
        foreach (Match hm in UstMLineHeaderRx().Matches(fullText))
        {
            var winEnd = Math.Min(fullText.Length, hm.Index + hm.Length + 300);
            foreach (Match m in ColonValueRx().Matches(CutAtForeignField(fullText[(hm.Index + hm.Length)..winEnd])))
            {
                if (TryParse(m.Groups[1].Value, out var h) && h is >= 1 and <= 500
                    && seen.Add((h, BaseGround, null)))
                    AddTracedTo(result, "ColonValueRx", new AntennaHeight(h, BaseGround, null, null));
            }
        }

        // Таблица с разделителем «;» и парой высот перед азимутом и «X;Y».
        if (SemiTableHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in SemiTableRowRx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                    && seen.Add((gh, BaseGround, null)))
                    AddTracedTo(result, "SemiTableRowRx", new AntennaHeight(gh, BaseGround, null, null));
                if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                    && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                    AddTracedTo(result, "SemiTableRowRx", new AntennaHeight(rh, BaseRoof, null, null));
            }
        }

        // Таблица «Высота подвеса относительно земли/кровли» (пара колонок).
        if (DvaColHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in DvaColRowRx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                    && seen.Add((gh, BaseGround, null)))
                    AddTracedTo(result, "DvaColRowRx", new AntennaHeight(gh, BaseGround, null, null));
                if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                    && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                    AddTracedTo(result, "DvaColRowRx", new AntennaHeight(rh, BaseRoof, null, null));
            }
        }

        // Таблица «отн. земли отн. кровли» без координатной колонки: высоты в хвосте
        // строки перед азимутом/наклоном/мощностью.
        if (TailTableHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in TailTableRowRx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                    && seen.Add((gh, BaseGround, null)))
                    AddTracedTo(result, "TailTableRowRx", new AntennaHeight(gh, BaseGround, null, null));
                if (TryParse(m.Groups[2].Value, out var rh) && rh is >= 1 and <= 500
                    && seen.Add((rh, BaseRoof, null)))
                    AddTracedTo(result, "TailTableRowRx", new AntennaHeight(rh, BaseRoof, null, null));
            }
        }

        // Таблица с высотой «32,5/-» перед колонкой режима «Круглосуточно».
        foreach (Match m in KruglosutochnoRx().Matches(fullText))
        {
            if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                && seen.Add((gh, BaseGround, null)))
                AddTracedTo(result, "KruglosutochnoRx", new AntennaHeight(gh, BaseGround, null, null));
            if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                AddTracedTo(result, "KruglosutochnoRx", new AntennaHeight(rh, BaseRoof, null, null));
        }

        // Фоллбэк уровня документа: высоты остались без антенн, но модели в тексте
        // названы («Антенна типа RFS APXVLL13-C…», «панельные антенны типа Mobi…»).
        // Однозначные случаи:
        //   - одна модель на документ  → все высоты её;
        //   - одна ВЫСОТА на документ  → все модели на ней (частый вид: «панельные
        //     антенны типа X…, типа Y… Высота подвеса секторных антенн 23,3 м»).
        // Несколько моделей И несколько высот — попарной связи нет, не гадаем.
        if (result.Count > 0 && result.All(r => r.Antenna == null))
        {
            var docTypes = AntennaTipaRx().Matches(fullText)
                .Select(m => m.Groups[1].Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (docTypes.Count == 1)
            {
                for (int i = 0; i < result.Count; i++)
                    result[i] = result[i] with { Antenna = docTypes[0] };
            }
            else if (docTypes.Count > 1 && result.Select(r => r.Height).Distinct().Count() == 1)
            {
                var one = result[0];
                result.Clear();
                result.AddRange(docTypes.Select(t => one with { Antenna = t }));
            }
        }

        // Высота МАЧТЫ/столба («на столбе (высотой 23м)») — это высота опоры, а не
        // подвеса антенны, и рядом обычно стоит настоящая отметка («на отметке 24м
        // относительно уровня земли»). Берём её только как ЗАПАСНОЙ вариант, когда
        // других высот в документе нет вовсе (01-РА-01: в данные попадали и 23, и 24).
        if (result.Count == 0)
        {
            foreach (Match m in MachtaRx().Matches(fullText))
            {
                foreach (var (h, baseKind) in ParseWithBase(m.Value, m.Groups[1].Value))
                {
                    if (h is < 1 or > 500) continue;
                    if (seen.Add((h, baseKind, null)))
                        AddTracedTo(result, "MachtaRx", new AntennaHeight(h, baseKind, null, null));
                }
            }
        }

        // Одна и та же высота, найденная разными формулировками, давала ДВЕ записи:
        // «высота фазового центра: 24» (база не указана) и «на отметке 24м
        // относительно уровня земли» (база «земля»). Оставляем вариант с базой —
        // он информативнее, а число антенн перестаёт быть завышенным.
        for (int i = result.Count - 1; i >= 0; i--)
        {
            if (result[i].Base != null) continue;
            var cur = result[i];
            if (result.Any(o => o.Base != null && o.Height == cur.Height
                                && (o.Antenna == cur.Antenna || cur.Antenna == null)))
                result.RemoveAt(i);
        }

        return result.Count > 0 ? result : null;
    }

    // Заголовок широкой таблицы антенн с координатной колонкой.
    [GeneratedRegex(@"высота\s+подвеса[\s\S]{0,300}?координаты\s+установки", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex WideTableHeaderRx();

    // Строка широкой таблицы: «… 32 - 70 -5 0;0 …» — высоты, азимут, наклон, X;Y.
    // Азимут бывает диапазоном с кавычкой OCR: «27.5 - '0-360 0 0;0» (63-СЦ, апрель).
    [GeneratedRegex(@"\s([0-9]{1,3}(?:[.,][0-9]+)?)\s+(-|[0-9]{1,3}(?:[.,][0-9]+)?)\s+'?[0-9]{1,3}(?:\s*-\s*[0-9]{2,3})?\s+-?[0-9]+(?:[.,][0-9]+)?\s+-?[0-9]+(?:[.,][0-9]+)?;-?[0-9]+", RegexOptions.None, 20000)]
    private static partial Regex WideTableRowRx();

    // Вариант той же таблицы с координатами через пробел и фидером «RF»:
    // «… 20.0 0.0 40 -3 0.0 0.0 RF 1/2"-50 …».
    [GeneratedRegex(@"\s([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s+(-|[0-9]{1,3}(?:[.,][0-9]{1,2})?)\s+[0-9]{1,3}\s+-?[0-9]+\s+-?[0-9]+(?:[.,][0-9]+)?\s+-?[0-9]+(?:[.,][0-9]+)?\s+RF\b", RegexOptions.None, 20000)]
    private static partial Regex WideTableRowRfRx();

    // Таблица с последней колонкой «Высота от … земли, м»: высота — последнее число
    // строки «А1 RRU5502 1 10.0 RX1004M6R015 DCS-1800 GMSK 17.0 0 86.3».
    [GeneratedRegex(@"высота\s+от[\s\S]{0,120}?земли,\s*м", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex LastColHeaderRx();

    [GeneratedRegex(@"^[АA]\d{1,2}\s+\S[^\n]*\s([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*$", RegexOptions.Multiline, 20000)]
    private static partial Regex LastColRowRx();

    // Заголовок «от поверхности земли / от опорной поверхности» (значения ниже).
    [GeneratedRegex(@"(?:высот[аы]\s+установки\s+антенн\w*\s*[-–—]?\s*от\s+поверхности\s+земли\s*(?:/\s*(?:от\s+опорной\s+поверхности|кровли)|\(м\)\s*:|\r?\n)|высот[аы]\s+установки\s+антенн\s+от\s+уровня\s+земли\s*/\s*кровли\s*:)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex OporPovHeaderRx();

    // Значение-пара «22/-м», «26,7/- м», «,26/-» в окне после заголовка
    // (окончание «м» бывает лишь у последней пары списка «26/-,26/-,26/-м»).
    // Перед числом — разделитель, скобка или пробел, но НЕ цифра и не запятая
    // дробной части: со старым классом «[:-–—,]» пара «61,3/-» разбиралась как
    // «,3/-» (высота 3 м вместо 61,3), а пары после «;» не находились вовсе
    // (11-РЦ-09: терялись 61,3 / 90 / 64).
    [GeneratedRegex(@"(?<![0-9.,])(?<=[:\-–—,;(\s])([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*(?:м\s*)?/\s*(-|[0-9]{1,3}(?:[.,][0-9]{1,2})?)(?=[,;\s]|м)", RegexOptions.None, 20000)]
    private static partial Regex SlashPairValueRx();

    // Таблица с колонками «Высота подвеса относительно земли, м … относительно кровли, м»
    // (38-ИЦ-06): в строке после типа антенны идёт пара «29,0 -» или «29,0 12,5».
    [GeneratedRegex(@"высота\s+подвеса\s+относительно\s+земли,\s*м\s+высота\s+подвеса\s+относительно\s+кровли", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex DvaColHeaderRx();

    [GeneratedRegex(@"^\d{1,2}\s+\S[^\n]*?\s([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s+(-|[0-9]{1,3}(?:[.,][0-9]{1,2})?)\s+[0-9]{1,3}(?:[.,][0-9]{1,2})?\s", RegexOptions.Multiline, 20000)]
    private static partial Regex DvaColRowRx();

    // Таблица с разделителем «;» (46-01-12): «…; 8; 5; 30; 0/-1; 0;0; …» —
    // высоты (земля; кровля) перед азимутом, наклоном и координатами «X;Y».
    [GeneratedRegex(@"высота\s+подвеса,\s*м\s*\(отн\.\s*земли;\s*отн\.\s*кровли\)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex SemiTableHeaderRx();

    [GeneratedRegex(@";\s*([0-9]{1,3}(?:[.,][0-9]{1,2})?);\s*(-|[0-9]{1,3}(?:[.,][0-9]{1,2})?);\s*[0-9]{1,3};\s*-?[0-9]+(?:/-?[0-9]+)?;\s*-?[0-9]+;-?[0-9]+;", RegexOptions.None, 20000)]
    private static partial Regex SemiTableRowRx();

    // Таблица с буквенными колонками (51-01-04): расшифровка «И - Высота установки
    // антенны от поверхности земли, м», в строках высота стоит перед азимутом «0-360».
    [GeneratedRegex(@"[А-Я]\s*[-–—]\s*Высота\s+установки\s+антенн\w*\s+от\s+поверхности\s+земли", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex LetterTableHeaderRx();

    [GeneratedRegex(@"\s([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s+0\s*-\s*360\s", RegexOptions.None, 20000)]
    private static partial Regex LetterTableRowRx();

    // Вариант строк буквенной таблицы (апрель): «… 16.07 22 100 -2/0» —
    // высота, целый азимут, наклон с дробью «мех/эл».
    [GeneratedRegex(@"\s([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s+[0-9]{1,3}\s+-?[0-9]+/-?[0-9]+\b", RegexOptions.None, 20000)]
    private static partial Regex LetterTableRow2Rx();

    // Строка буквенной таблицы 2023 года: «… 16,7 23 80 -4» — высота, азимут,
    // целый наклон в конце строки.
    [GeneratedRegex(@"\s([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s+[0-9]{1,3}\s+(?:-[0-9]{1,2}|0)\s*$", RegexOptions.Multiline, 20000)]
    private static partial Regex LetterTableRow3Rx();

    // Заголовок перечня «по каждой антенне»: высота — второе поле записи после «шт.,».
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн\w*\s+от\s+поверхности\s+земли\s*,", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex ShtHeaderRx();

    // Запись перечня: «<тип> - 3 шт., 43 м, …».
    [GeneratedRegex(@"шт\.?\s*,\s*([0-9]+(?:[.,][0-9]+)?)\s*м\b", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex ShtItemRx();

    // Заголовок табличной колонки высот с плюсовой пометкой значений.
    [GeneratedRegex(@"высота\s*\(м\)\s*(?:подвеса|фазового\s+центра)|высота\s+подвеса\s+от\s+уровня\s+земли,\s*м\s*;", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex PlusTableHeaderRx();

    // «+61,0» — высота из строки такой таблицы (плюс её и метит).
    [GeneratedRegex(@"\+([0-9]{1,3}(?:[.,][0-9]{1,2})?)\b", RegexOptions.None, 20000)]
    private static partial Regex PlusValueRx();

    // «…; тип антенны - A1- ODV-065R17E18; …» — тип в той же записи ПОСЛЕ высоты.
    [GeneratedRegex(@"тип\s+антенн\w*\s*[-:—]\s*([^;\n]{2,60}?)\s*(?:[;\n]|$)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex AntennaTypeAfterRx();

    // «№1 Коллинеарная антенна; высота установки …» — название ПЕРЕД высотой.
    [GeneratedRegex(@"№\s*\d+\s+([А-ЯЁA-Z][^;:№\n]{2,50}?)\s*;[^;№\n]{0,60}$", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex AntennaNameBeforeRx();

    // «A1- ODV-065R17E18», «A4- Параболическая…» — позиционный код антенны в типе.
    [GeneratedRegex(@"^[AА](\d{1,2})\s*[-–]\s*(.{2,})$", RegexOptions.None, 20000)]
    private static partial Regex PositionCodeRx();

    // «Антенна типа RFS APXVLL13-C (панельная) имеет ширину…» — модель отдельным
    // предложением, не привязанным к строке высоты. Хвост «имеет/имеют…» отрезаем.
    [GeneratedRegex(@"антенн[аы]?\s+типа\s+([A-Za-zА-ЯЁ0-9][^\n;]{2,60}?)\s*(?:имеет|имеют|с\s+шириной|[;\n]|$)", RegexOptions.IgnoreCase, 20000)]
    private static partial Regex AntennaTipaRx();

    /// <summary>
    /// Пытается определить антенну, к которой относится высота: «тип антенны - …»
    /// в ближайшем контексте после совпадения либо «№N Название;» перед ним.
    /// Возвращает тип/модель и номер антенны (из «№N» или позиционного кода «A1-»);
    /// не нашлись — null (поля в JSON опускаются).
    /// </summary>
    private static (string? Antenna, int? Number) FindAntenna(string fullText, Match m)
    {
        // Окно после: до 300 символов той же строки.
        var afterEnd = Math.Min(fullText.Length, m.Index + m.Length + 300);
        var after = fullText[(m.Index + m.Length)..afterEnd];
        var nl = after.IndexOf('\n');
        if (nl >= 0) after = after[..nl];
        var ta = AntennaTypeAfterRx().Match(after);
        if (ta.Success)
        {
            var val = ta.Groups[1].Value.Trim();
            // «A1- ODV-065…» — отделяем позиционный номер от модели.
            var pc = PositionCodeRx().Match(val);
            return pc.Success
                ? (pc.Groups[2].Value.Trim(), int.Parse(pc.Groups[1].Value))
                : (val, null);
        }

        // Окно до: 120 символов той же строки.
        var beforeStart = Math.Max(0, m.Index - 120);
        var before = fullText[beforeStart..m.Index];
        nl = before.LastIndexOf('\n');
        if (nl >= 0) before = before[(nl + 1)..];
        var nb = AntennaNameBeforeRx().Match(before);
        if (!nb.Success) return (null, null);
        var numM = System.Text.RegularExpressions.Regex.Match(nb.Value, @"№\s*(\d+)");
        return (nb.Groups[1].Value.Trim(), numM.Success ? int.Parse(numM.Groups[1].Value) : null);
    }

    private const string BaseGround = "земля";
    private const string BaseRoof = "кровля";

    /// <summary>
    /// Определяет базу отсчёта по контексту совпадения и раздаёт её значениям.
    /// Особый случай — парная форма «над уровнем земли/кровли: 39,48/- м»: слэш
    /// в списке значений разделяет базы (первое — от земли, второе — от кровли).
    /// </summary>
    private static IEnumerable<(double Height, string? Base)> ParseWithBase(string matchText, string rawList)
    {
        var lc = matchText.ToLowerInvariant();
        var hasGround = lc.Contains("земл");
        var hasRoof = lc.Contains("кровл") || lc.Contains("крыш");

        // «земли/кровли» (в т.ч. «от уровня земли/от уровня кровли») с ровно двумя
        // значениями через слэш — раскладываем по базам.
        if (hasGround && hasRoof && (lc.Contains("земли/кровли") || lc.Contains("земли/от")))
        {
            var pair = rawList.Split('/', StringSplitOptions.TrimEntries);
            if (pair.Length == 2)
            {
                foreach (var h in ParseList(pair[0])) yield return (h, BaseGround);
                foreach (var h in ParseList(pair[1])) yield return (h, BaseRoof);
                yield break;
            }
        }

        // Иначе одна база на всё совпадение: обе упомянуты — считаем «от земли»
        // (форма «над уровнем земли (над уровнем кровли)» первичной называет землю).
        string? baseKind = (hasGround, hasRoof) switch
        {
            (true, _) => BaseGround,
            (false, true) => BaseRoof,
            _ => null
        };
        foreach (var h in ParseList(rawList)) yield return (h, baseKind);
    }

    /// <summary>
    /// Разбирает список «32; 27, 38», «39,48/-», «62,5; 40; 55». Запятая с пробелом —
    /// разделитель списка, без пробела — десятичная; «-» и пустые элементы пропускаются.
    /// Диапазон «19-23» даёт обе границы.
    /// </summary>
    private static IEnumerable<double> ParseList(string raw)
    {
        // Запятая-разделитель списка (за ней пробел) превращается в «;», чтобы не
        // спутать с десятичной («26,95»); союз «и» перед последним элементом — тоже.
        raw = ListCommaRx().Replace(raw, ";").Replace(" и ", ";");

        foreach (var piece in raw.Split([';', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // Диапазон «19-23» (дефис между числами; не минус и не «-» — заглушка «нет значения»)
            var range = RangeRx().Match(piece);
            if (range.Success)
            {
                if (TryParse(range.Groups[1].Value, out var lo)) yield return lo;
                if (TryParse(range.Groups[2].Value, out var hi)) yield return hi;
                continue;
            }
            if (TryParse(piece.Trim('-', ' '), out var v)) { yield return v; continue; }
            // Кусок с меткой группы («БС: 38.0», «РРС: 39.0») — берём число из него.
            var num = LooseNumberRx().Match(piece);
            if (num.Success && TryParse(num.Groups[1].Value, out var lv)) yield return lv;
        }
    }

    [GeneratedRegex(@",\s+", RegexOptions.None, 20000)]
    private static partial Regex ListCommaRx();

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d{1,2})?)", RegexOptions.None, 20000)]
    private static partial Regex LooseNumberRx();

    [GeneratedRegex(@"^\s*(\d+(?:[.,]\d+)?)\s*-\s*(\d+(?:[.,]\d+)?)\s*$", RegexOptions.None, 20000)]
    private static partial Regex RangeRx();

    private static bool TryParse(string s, out double value) =>
        double.TryParse(s.Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
