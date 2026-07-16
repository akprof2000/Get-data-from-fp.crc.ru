namespace ParseTextHeader;

/// <summary>
/// Результат разбора одного документа СЭЗ: извлечённые поля базовой станции.
/// Сериализуется в JSON (camelCase) в OutputJson (все обязательные поля есть)
/// или OutputErrors (какое-то из обязательных полей не найдено).
/// </summary>
public class BaseStationDocument
{
    public string SourceFileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Порядковый номер записи в реестре (первая строка документа).</summary>
    public string? Index { get; set; }

    /// <summary>Номер заключения и дата, напр. «26.01.05.000.Т.000270.02.24 от 29.02.2024».</summary>
    public string? DocumentNumberAndDate { get; set; }

    /// <summary>Номер/идентификатор базовой станции в любом встречающемся формате.</summary>
    public string? BaseStationNumber { get; set; }

    /// <summary>Адрес размещения станции (после локальной очистки/нормализации).</summary>
    public string? BaseStationAddress { get; set; }

    /// <summary>Координаты «широта, долгота» в десятичных градусах, если найдены.</summary>
    public string? Coordinates { get; set; }

    /// <summary>Оператор/владелец станции (канонизированное имя, если оператор известен).</summary>
    public string? Operator { get; set; }

    public DateTime ProcessingDate { get; set; }

    /// <summary>Первые строки исходного файла — для ручной проверки спорных случаев.</summary>
    public List<string> RawFirstLines { get; set; } = [];

    /// <summary>Все ли обязательные поля заполнены (критерий попадания в OutputJson).</summary>
    public bool HasAllRequiredFields() =>
        !string.IsNullOrWhiteSpace(Index)
        && !string.IsNullOrWhiteSpace(DocumentNumberAndDate)
        && !string.IsNullOrWhiteSpace(BaseStationNumber)
        && !string.IsNullOrWhiteSpace(BaseStationAddress)
        && !string.IsNullOrWhiteSpace(Operator);

    /// <summary>Список незаполненных обязательных полей — для диагностики в логе.</summary>
    public List<string> GetMissingFields()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Index)) missing.Add(nameof(Index));
        if (string.IsNullOrWhiteSpace(DocumentNumberAndDate)) missing.Add(nameof(DocumentNumberAndDate));
        if (string.IsNullOrWhiteSpace(BaseStationNumber)) missing.Add(nameof(BaseStationNumber));
        if (string.IsNullOrWhiteSpace(BaseStationAddress)) missing.Add(nameof(BaseStationAddress));
        if (string.IsNullOrWhiteSpace(Operator)) missing.Add(nameof(Operator));
        return missing;
    }
}

/// <summary>Итог обработки одного файла (для итоговой статистики прогона).</summary>
public enum ProcessingResult
{
    /// <summary>Все обязательные поля извлечены — JSON записан в OutputJson.</summary>
    Success,
    /// <summary>Часть полей не найдена — JSON записан в OutputErrors.</summary>
    SuccessWithErrors,
    /// <summary>Файл уже обрабатывался ранее (есть в _processed.json).</summary>
    AlreadyProcessed,
    /// <summary>Ошибка чтения/обработки файла.</summary>
    Error,
    /// <summary>Файл пропущен (пустой или не документ базовой станции).</summary>
    Skipped
}
