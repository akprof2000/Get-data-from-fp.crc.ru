using CellsClassifier.Models;
using Microsoft.ML;

namespace CellsClassifier.Services;

/// <summary>
/// Обёртка над обученной ML-моделью: загружает модель с диска (если она есть)
/// и классифицирует текст документа («про сотовую связь» / «нет»).
/// </summary>
public class MlPredictor
{
    private readonly string _modelPath;
    private readonly MLContext _ml;
    private PredictionEngine<DocumentLabel, PredictionResult>? _engine;

    // PredictionEngine НЕ потокобезопасен, а ProcessCommand зовёт Predict из
    // параллельных потоков — сериализуем доступ блокировкой. Однократный вызов
    // на документ дёшев по сравнению с чтением файла, потери на lock ничтожны.
    private readonly Lock _engineLock = new();

    public MlPredictor(string modelPath)
    {
        _modelPath = modelPath;
        _ml = new MLContext();
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_modelPath))
        {
            return; // модель ещё не обучена — ProcessCommand перейдёт на эвристику
        }

        var model = _ml.Model.Load(_modelPath, out _);
        _engine = _ml.Model.CreatePredictionEngine<DocumentLabel, PredictionResult>(model);
    }

    /// <summary>Модель загружена и готова к предсказаниям.</summary>
    public bool IsReady => _engine != null;

    public PredictionResult Predict(string text)
    {
        if (_engine == null)
        {
            throw new InvalidOperationException("ML-модель не загружена.");
        }

        lock (_engineLock)
        {
            return _engine.Predict(new DocumentLabel { Label = false, Text = text });
        }
    }
}
