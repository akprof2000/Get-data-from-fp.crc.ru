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
    [GeneratedRegex(@"высот[аы]\s+подвеса[^:\n]{0,40}[:\s(]\s*(?:высота\s+)?([0-9][0-9.,;/\s-]{0,80}?(?:\sи\s[0-9][0-9.,]{0,6})?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex PodvesaRx();

    // «высота установки антенны над уровнем земли/кровли: 39,48/- м» (86-ХЦ-23 и др.)
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн[^:\n]{0,60}:\s*([0-9][0-9.,;/\s-]{0,80}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex UstanovkiRx();

    // «высота размещения антенн: 25 м», «антенны размещены на высоте 19-23 м»
    [GeneratedRegex(@"высот[аы]\s+размещения\s+антенн[^:\n]{0,40}[:\s]\s*([0-9][0-9.,;/\s-]{0,80}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex RazmeshcheniyaRx();

    [GeneratedRegex(@"антенн\w*[^\n]{0,30}?на\s+высоте\s*([0-9][0-9.,;/\s-]{0,40}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex NaVysoteRx();

    // Единица ПЕРЕД значениями: «высота установки антенны от поверхности земли, м: 50; 49,5»
    [GeneratedRegex(@"высот[аы][^\n:]{0,70}?,\s*м\s*[:;]\s*([0-9][0-9.,;/\s]{0,60}?)(?=\s*(?:[-–]|[а-яА-Яa-zA-Z(]|$))", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex UnitBeforeColonRx();

    // «Высота установки антенн от поверхности земли, м: БС - 42/42/42; РРС - 42, 42»
    // — значения с метками групп после «м:».
    [GeneratedRegex(@"высот[аы][^\n:]{0,70}?,\s*м\s*:\s*((?:БС|РРС)\s*[-–—][^\n]{1,100})", RegexOptions.IgnoreCase)]
    private static partial Regex MetkiPosleMRx();

    // «высота установки от поверхности земли / от опорной поверхности – 13,5/9 м»
    [GeneratedRegex(@"высот[аы]\s+установки\s+от\s+поверхности[^\n:0-9]{0,60}?[-–—:]\s*([0-9][0-9.,;/\s-]{0,40}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex OtPoverkhnostiRx();

    // Табличная форма: «Высота установки антенны над уровнем земли (над уровнем кровли), м 27.50, Азимут»
    [GeneratedRegex(@"высот[аы]\s+(?:установки|подвеса|размещения)\s+антенн[^\n:]{0,70}?,\s*м\s+([0-9]+(?:[.,][0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex TableFormRx();

    // «…с азимутами 50/150/310 … на высоте подвеса 38 метров»
    [GeneratedRegex(@"на\s+высоте\s+подвеса\s+([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex NaVysotePodvesaRx();

    // Строка таблицы «…Высота подвеса…отн. земли отн. кровли…» без координатной
    // колонки: хвост строки «21.5 2.5 20 -5 2.72» — высоты, азимут, наклон, мощность.
    [GeneratedRegex(@"([0-9]{1,3}(?:[.,][0-9]+)?)\s+([0-9]{1,3}(?:[.,][0-9]+)?)\s+[0-9]{1,3}\s+-?[0-9]+\s+[0-9]+(?:[.,][0-9]+)?\s*$", RegexOptions.Multiline)]
    private static partial Regex TailTableRowRx();

    [GeneratedRegex(@"высота\s+подвеса[\s\S]{0,200}?отн\.\s*земли\s+отн\.\s*кровли", RegexOptions.IgnoreCase)]
    private static partial Regex TailTableHeaderRx();

    // Строка таблицы с высотой «32,5/-» перед режимом работы «Круглосуточно».
    [GeneratedRegex(@"([0-9]{1,3}(?:[.,][0-9]+)?)\s*/\s*(-|[0-9]{1,3}(?:[.,][0-9]+)?)\s+Круглосуточно", RegexOptions.IgnoreCase)]
    private static partial Regex KruglosutochnoRx();

    // «Высота подвеса антенн (м): БС: 38.0/38.0/38.0; РРС: 39.0» — единица в скобках,
    // значения с метками групп; числа выцепляем из остатка строки.
    [GeneratedRegex(@"высот[аы]\s+подвеса[^\n(]{0,40}?\(м\)\s*:\s*([^\n]{1,100})", RegexOptions.IgnoreCase)]
    private static partial Regex PodvesMetkiRx();

    // «на антенной мачте …, высотой 25 м», «опоре высотой 72 м» — высота сооружения,
    // на котором размещены антенны (единственная высота в документе такого типа).
    [GeneratedRegex(@"(?:мачт|опор|башн|столб|трубостойк)[а-яё]*[^\n.;]{0,60}?высотой\s+([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м\b", RegexOptions.IgnoreCase)]
    private static partial Regex MachtaRx();

    // «высота подвеса отн. зем./кров. 24,2/2,2» — пара «земля/кровля» без единицы
    // («40/-» — от кровли значения нет).
    [GeneratedRegex(@"высот[аы]\s+подвеса\s+отн\.?\s*зем(?:ли|\.)?\s*/\s*кров(?:ли|\.)?,?\s*(?:м\.?)?\s*[-–—]?\s*([0-9]+(?:,[0-9]+)?)\s*/\s*([0-9]+(?:,[0-9]+)?|-)", RegexOptions.IgnoreCase)]
    private static partial Regex ZemKrovRx();

    // «на кровле здания, Н=14м», «опора Н=+33.00 м» — высота размещения через «Н=».
    [GeneratedRegex(@"[НH]\s*=\s*\+?([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*м\b", RegexOptions.IgnoreCase)]
    private static partial Regex HEqualsRx();

    // «высота фазового центра антенн от уровня земли/от уровня кровли - 32/- м; 32/- м»
    [GeneratedRegex(@"высот[аы]\s+(?:установки\s+)?фазового\s+центра[^\n:0-9]{0,70}?[-–—:\s]\s*([0-9][0-9.,;/\s-]{0,80}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex FazovogoRx();

    // «Высота от уровня земли до точки подвеса проектируемых антенн - 22,0 м»
    [GeneratedRegex(@"высот[аы][^\n]{0,50}?до\s+точки\s+подвеса[^\n:0-9]{0,50}?[-–—:]?\s*([0-9][0-9.,;/\s-]{0,40}?)\s*(?:м\b|метр\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex TochkiPodvesaRx();

    // «высота установки антенн от поверхности земли (кровли), м - 75.0 (-)»:
    // единица перед тире, значение от кровли — в скобках («(-)» — нет значения).
    [GeneratedRegex(@"высот[аы][^\n:]{0,80}?,\s*м\s*[-–—]\s*([0-9]+(?:[.,][0-9]+)?)(?:\s*\(([^)\n]{1,12})\))?", RegexOptions.IgnoreCase)]
    private static partial Regex UnitDashRx();

    /// <summary>
    /// Все высоты подвеса из документа (уникальные пары «высота+база», в порядке
    /// появления); null — не найдены. База отсчёта берётся из контекста строки:
    /// «земля», «кровля» или null, когда в документе не сказано.
    /// </summary>
    public static List<AntennaHeight>? Extract(string fullText)
    {
        var result = new List<AntennaHeight>();
        var seen = new HashSet<(double, string?, string?)>();

        foreach (var rx in (ReadOnlySpan<Regex>)[PodvesaRx(), UstanovkiRx(), RazmeshcheniyaRx(),
                     NaVysoteRx(), UnitBeforeColonRx(), TableFormRx(), FazovogoRx(),
                     TochkiPodvesaRx(), UnitDashRx(), HEqualsRx(), PodvesMetkiRx(), MachtaRx(),
                     NaVysotePodvesaRx(), OtPoverkhnostiRx(), MetkiPosleMRx()])
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
                        result.Add(new AntennaHeight(h, baseKind, antenna, number));
                }
            }
        }

        // Пара «отн. зем./кров. 24,2/2,2»: первое значение — от земли, второе — от кровли.
        foreach (Match m in ZemKrovRx().Matches(fullText))
        {
            var (antenna, number) = FindAntenna(fullText, m);
            if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                && seen.Add((gh, BaseGround, antenna)))
                result.Add(new AntennaHeight(gh, BaseGround, antenna, number));
            if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, antenna)))
                result.Add(new AntennaHeight(rh, BaseRoof, antenna, number));
        }

        // Значение «от кровли» из формы «…, м - 75.0 (12.5)»: скобочная часть —
        // вторая база, «(-)» — значения нет.
        foreach (Match m in UnitDashRx().Matches(fullText))
        {
            if (!m.Groups[2].Success || !TryParse(m.Groups[2].Value.Trim('-', ' '), out var roofH)) continue;
            if (roofH is < 1 or > 500) continue;
            var (antenna, number) = FindAntenna(fullText, m);
            if (seen.Add((roofH, BaseRoof, antenna)))
                result.Add(new AntennaHeight(roofH, BaseRoof, antenna, number));
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
                    result.Add(new AntennaHeight(h, BaseGround, null, null));
            }
        }

        // Таблица с колонкой «Высота (м) подвеса антенн от уровня земли/крыши»:
        // в строках высоты помечены плюсом («… 2х60,0 +61,0 120° …»).
        var plusHeader = PlusTableHeaderRx().Match(fullText);
        if (plusHeader.Success)
        {
            foreach (Match m in PlusValueRx().Matches(fullText, plusHeader.Index))
            {
                if (!TryParse(m.Groups[1].Value, out var h) || h is < 1 or > 500) continue;
                if (seen.Add((h, BaseGround, null)))
                    result.Add(new AntennaHeight(h, BaseGround, null, null));
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
                    result.Add(new AntennaHeight(gh, BaseGround, null, null));
                if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                    && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                    result.Add(new AntennaHeight(rh, BaseRoof, null, null));
            }
        }

        // Таблица с последней колонкой «Высота от земли, м».
        if (LastColHeaderRx().IsMatch(fullText))
        {
            foreach (Match m in LastColRowRx().Matches(fullText))
            {
                if (TryParse(m.Groups[1].Value, out var h) && h is >= 1 and <= 500
                    && seen.Add((h, BaseGround, null)))
                    result.Add(new AntennaHeight(h, BaseGround, null, null));
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
                    result.Add(new AntennaHeight(gh, BaseGround, null, null));
                if (TryParse(m.Groups[2].Value, out var rh) && rh is >= 1 and <= 500
                    && seen.Add((rh, BaseRoof, null)))
                    result.Add(new AntennaHeight(rh, BaseRoof, null, null));
            }
        }

        // Таблица с высотой «32,5/-» перед колонкой режима «Круглосуточно».
        foreach (Match m in KruglosutochnoRx().Matches(fullText))
        {
            if (TryParse(m.Groups[1].Value, out var gh) && gh is >= 1 and <= 500
                && seen.Add((gh, BaseGround, null)))
                result.Add(new AntennaHeight(gh, BaseGround, null, null));
            if (m.Groups[2].Value != "-" && TryParse(m.Groups[2].Value, out var rh)
                && rh is >= 1 and <= 500 && seen.Add((rh, BaseRoof, null)))
                result.Add(new AntennaHeight(rh, BaseRoof, null, null));
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

        return result.Count > 0 ? result : null;
    }

    // Заголовок широкой таблицы антенн с координатной колонкой.
    [GeneratedRegex(@"высота\s+подвеса[\s\S]{0,300}?координаты\s+установки", RegexOptions.IgnoreCase)]
    private static partial Regex WideTableHeaderRx();

    // Строка широкой таблицы: «… 32 - 70 -5 0;0 …» — высоты, азимут, наклон, X;Y.
    [GeneratedRegex(@"\s([0-9]{1,3}(?:[.,][0-9]+)?)\s+(-|[0-9]{1,3}(?:[.,][0-9]+)?)\s+[0-9]{1,3}\s+-?[0-9]+(?:[.,][0-9]+)?\s+-?[0-9]+(?:[.,][0-9]+)?;-?[0-9]+")]
    private static partial Regex WideTableRowRx();

    // Таблица с последней колонкой «Высота от … земли, м»: высота — последнее число
    // строки «А1 RRU5502 1 10.0 RX1004M6R015 DCS-1800 GMSK 17.0 0 86.3».
    [GeneratedRegex(@"высота\s+от[\s\S]{0,120}?земли,\s*м", RegexOptions.IgnoreCase)]
    private static partial Regex LastColHeaderRx();

    [GeneratedRegex(@"^[АA]\d{1,2}\s+\S[^\n]*\s([0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*$", RegexOptions.Multiline)]
    private static partial Regex LastColRowRx();

    // Заголовок перечня «по каждой антенне»: высота — второе поле записи после «шт.,».
    [GeneratedRegex(@"высот[аы]\s+установки\s+антенн\w*\s+от\s+поверхности\s+земли\s*,", RegexOptions.IgnoreCase)]
    private static partial Regex ShtHeaderRx();

    // Запись перечня: «<тип> - 3 шт., 43 м, …».
    [GeneratedRegex(@"шт\.?\s*,\s*([0-9]+(?:[.,][0-9]+)?)\s*м\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShtItemRx();

    // Заголовок табличной колонки высот с плюсовой пометкой значений.
    [GeneratedRegex(@"высота\s*\(м\)\s*(?:подвеса|фазового\s+центра)", RegexOptions.IgnoreCase)]
    private static partial Regex PlusTableHeaderRx();

    // «+61,0» — высота из строки такой таблицы (плюс её и метит).
    [GeneratedRegex(@"\+([0-9]{1,3}(?:[.,][0-9]{1,2})?)\b")]
    private static partial Regex PlusValueRx();

    // «…; тип антенны - A1- ODV-065R17E18; …» — тип в той же записи ПОСЛЕ высоты.
    [GeneratedRegex(@"тип\s+антенн\w*\s*[-:—]\s*([^;\n]{2,60}?)\s*(?:[;\n]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AntennaTypeAfterRx();

    // «№1 Коллинеарная антенна; высота установки …» — название ПЕРЕД высотой.
    [GeneratedRegex(@"№\s*\d+\s+([А-ЯЁA-Z][^;:№\n]{2,50}?)\s*;[^;№\n]{0,60}$", RegexOptions.IgnoreCase)]
    private static partial Regex AntennaNameBeforeRx();

    // «A1- ODV-065R17E18», «A4- Параболическая…» — позиционный код антенны в типе.
    [GeneratedRegex(@"^[AА](\d{1,2})\s*[-–]\s*(.{2,})$")]
    private static partial Regex PositionCodeRx();

    // «Антенна типа RFS APXVLL13-C (панельная) имеет ширину…» — модель отдельным
    // предложением, не привязанным к строке высоты. Хвост «имеет/имеют…» отрезаем.
    [GeneratedRegex(@"антенн[аы]?\s+типа\s+([A-Za-zА-ЯЁ0-9][^\n;]{2,60}?)\s*(?:имеет|имеют|с\s+шириной|[;\n]|$)", RegexOptions.IgnoreCase)]
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

    [GeneratedRegex(@",\s+")]
    private static partial Regex ListCommaRx();

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d{1,2})?)")]
    private static partial Regex LooseNumberRx();

    [GeneratedRegex(@"^\s*(\d+(?:[.,]\d+)?)\s*-\s*(\d+(?:[.,]\d+)?)\s*$")]
    private static partial Regex RangeRx();

    private static bool TryParse(string s, out double value) =>
        double.TryParse(s.Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
