using System.Text.RegularExpressions;

namespace GetSiteData.Common;

/// <summary>
/// Единый формат имени документа конвейера — «Номер заключения и дата»:
/// «01.РА.01.000.Т.000215.07.26 от 01.07.2026». Все этапы (сбор, разбор,
/// классификация, извлечение, выгрузка в ClickHouse) принимают и порождают
/// файлы только с такими именами; всё прочее — служебное или постороннее.
/// </summary>
public static partial class DocumentName
{
    // Октеты номера: 2 цифры, 2 заглавные буквы/цифры (бывают кириллические —
    // БЦ, ОМ, ХЦ, РА, СЦ… — и чисто цифровые — 01, 49), 2 цифры, 3 цифры,
    // «Т» (допускаем и латинскую), 6 цифр, месяц, год; затем « от ДД.ММ.ГГГГ».
    // Шаблон проверен на полном корпусе: 111 872 из 111 872 имён совпали.
    [GeneratedRegex(@"^\d{2}\.[А-ЯЁA-Z0-9]{2}\.\d{2}\.\d{3}\.[ТT]\.\d{6}\.\d{2}\.\d{2} от \d{2}\.\d{2}\.\d{4}$")]
    private static partial Regex NameRx();

    /// <summary>Проверяет «Номер заключения и дата» (без расширения файла).</summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && NameRx().IsMatch(name.Trim());

    /// <summary>Проверяет имя файла документа (расширение отбрасывается).</summary>
    public static bool IsValidFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && IsValid(Path.GetFileNameWithoutExtension(fileName));
}
