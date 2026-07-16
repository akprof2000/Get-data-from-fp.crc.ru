using CellsClassifier.Models;
using CellsClassifier.Services;
using GetSiteData.Common;

namespace CellsClassifier.Commands;

/// <summary>
/// Обучение (или переобучение) модели по размеченным документам.
/// Без force обучение пропускается, если размеченная выборка не изменилась
/// со времени последнего обучения (сравнение по числу размеченных).
/// </summary>
public class TrainCommand(LiteDbRepository repo, MlTrainer trainer, MlPredictor predictor)
{
    public async Task RunAsync(bool force = false)
    {
        // Материализуем сразу: выборка нужна и для подсчёта, и для обучения —
        // иначе ленивый IEnumerable дважды ходил бы в базу.
        var labeled = (await repo.GetLabeledAsync()).ToList();
        var meta = await repo.GetTrainingMetadataAsync();

        if (!force && meta != null && meta.LabeledCount == labeled.Count)
        {
            Log.Skip($"Обучение не требуется: выборка не изменилась ({labeled.Count} размеченных).");
            return;
        }

        // Разметки мало или нет совсем. При обычном запуске (process) это не ошибка:
        // в поставку входит готовая модель — она и будет классифицировать; своя разметка
        // нужна только для дообучения. Ошибка — лишь при явном запросе обучения (train).
        if (labeled.Count < MlTrainer.MinLabeledDocuments)
        {
            if (force)
            {
                throw new InvalidOperationException(
                    $"Недостаточно размеченных документов для обучения: {labeled.Count} (нужно минимум {MlTrainer.MinLabeledDocuments}).");
            }

            if (predictor.IsReady)
            {
                Log.Info($"Дообучение не требуется (размеченных документов {labeled.Count}) — используется готовая модель.");
            }
            else
            {
                Log.Warn($"Нет ни модели, ни разметки ({labeled.Count} документов) — классификация по эвристике ключевых слов.");
            }

            return;
        }

        Log.Phase($"Обучение модели: размеченных документов — {labeled.Count}");
        trainer.TrainFromDocuments(labeled);

        await repo.SaveTrainingMetadataAsync(new TrainingMetadata
        {
            LabeledCount = labeled.Count,
            TrainedAtUtc = DateTime.UtcNow
        });
        Log.Ok("Модель обучена и сохранена.");
    }
}
