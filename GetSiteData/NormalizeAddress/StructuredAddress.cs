using System.Text.Json.Serialization;

namespace NormalizeAddress;

/// <summary>
/// Адрес по частям — результат сопоставления с ГАР. Набор полей переменный:
/// null-поля не сериализуются, в JSON попадают только реально найденные части.
/// Всё, что адресом не является, но идентифицирует место («столб в 10 метрах
/// направо на высоте 135 м», «труба котельной»), складывается в Extra как есть.
/// </summary>
public class StructuredAddress
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? District { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Settlement { get; set; }

    /// <summary>Планировочная структура: СНТ, тер., промзона.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Territory { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Street { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Building { get; set; }

    /// <summary>Неадресные идентификаторы места: опоры, столбы, расстояния, высоты.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Extra { get; set; }

    /// <summary>
    /// До какого уровня ГАР дошло сопоставление:
    /// регион | район | город | населённый пункт | территория | улица | дом | нет.
    /// «дом» возможен только с полной адресной книгой (IncludeHouses).
    /// </summary>
    public string MatchLevel { get; set; } = "нет";

    /// <summary>ФИАС-GUID самого глубокого сопоставленного объекта.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Guid { get; set; }

    /// <summary>Код региона (01–99) по ГАР.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RegionCode { get; set; }

    /// <summary>Координаты места ИЗ ГЕОРЕЕСТРА (не из документа). Заполняются этапом
    /// геокодирования по офлайн-выгрузке OSM; в самом ГАР координат нет.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Lat { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Lon { get; set; }
}
