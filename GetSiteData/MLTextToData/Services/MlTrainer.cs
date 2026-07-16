using CellsClassifier.Models;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace CellsClassifier.Services;

/// <summary>
/// Обучает бинарный классификатор «документ про сотовую связь / нет» на
/// вручную размеченных документах и сохраняет модель на диск.
/// </summary>
public class MlTrainer
{
    /// <summary>Минимум размеченных документов, при котором обучение имеет смысл.</summary>
    public const int MinLabeledDocuments = 10;

    private readonly string _modelPath;
    private readonly MLContext _ml;

    public MlTrainer(string modelPath)
    {
        _modelPath = modelPath;
        // Фиксированный seed — воспроизводимое обучение между запусками.
        _ml = new MLContext(seed: 42);
    }

    private sealed class TrainingRow
    {
        [LoadColumn(0)] public bool Label { get; set; }
        [LoadColumn(1)] public string Text { get; set; } = default!;
    }

    public void TrainFromDocuments(IEnumerable<DocumentInfo> docs)
    {
        // Тексты читаем с диска здесь, а не храним в БД: разметка живёт в LiteDB,
        // а содержимое — в исходных файлах (единственный источник правды).
        var labeled = docs
            .Where(d => d.IsCellsLabel.HasValue)
            .Select(d => new TrainingRow
            {
                Label = d.IsCellsLabel!.Value,
                Text = File.ReadAllText(d.FilePath)
            })
            .ToList();

        if (labeled.Count < MinLabeledDocuments)
        {
            throw new InvalidOperationException($"Недостаточно размеченных документов для обучения (нужно минимум {MinLabeledDocuments}).");
        }

        var data = _ml.Data.LoadFromEnumerable(labeled);

        // FeaturizeText (мешок слов + n-граммы) + логистическая регрессия — простой
        // и быстрый базовый вариант, которого достаточно для такой бинарной задачи.
        var pipeline = _ml.Transforms.Text.FeaturizeText("Features", nameof(TrainingRow.Text))
            .Append(_ml.BinaryClassification.Trainers.SdcaLogisticRegression());

        // Честная оценка качества: обучаем на 80 %, метрики считаем на отложенных 20 %,
        // затем итоговую модель обучаем на всей выборке.
        var split = _ml.Data.TrainTestSplit(data, testFraction: 0.2, seed: 42);
        var evalModel = pipeline.Fit(split.TrainSet);
        var metrics = _ml.BinaryClassification.Evaluate(evalModel.Transform(split.TestSet));
        GetSiteData.Common.Log.Info(
            $"Метрики на отложенных 20 %: Accuracy={metrics.Accuracy:P2}, AUC={metrics.AreaUnderRocCurve:P2}, " +
            $"F1={metrics.F1Score:P2}, Precision={metrics.PositivePrecision:P2}, Recall={metrics.PositiveRecall:P2}");

        var model = pipeline.Fit(data);
        _ml.Model.Save(model, data.Schema, _modelPath);
    }
}
