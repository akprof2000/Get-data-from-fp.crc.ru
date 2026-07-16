namespace CellsClassifier.Models;

/// <summary>
/// Запись LiteDB об одном документе: где лежит, когда обработан,
/// ручная метка (для обучения) и последнее предсказание модели.
/// </summary>
public class DocumentInfo
{
    public string Id { get; set; } = default!;
    public string FilePath { get; set; } = default!;

    /// <summary>SHA-256 содержимого — ключ дедупликации при повторных прогонах.</summary>
    public string Hash { get; set; } = default!;

    public DateTime LastWriteTimeUtc { get; set; }
    public bool Processed { get; set; }

    /// <summary>Ручная разметка: документ про сотовую связь? null — не размечен.</summary>
    public bool? IsCellsLabel { get; set; }

    /// <summary>Предсказание модели (после обработки).</summary>
    public bool? PredictedIsCells { get; set; }
    public float? PredictedScore { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
}
