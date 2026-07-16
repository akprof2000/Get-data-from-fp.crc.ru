using CellsClassifier.Models;
using CellsClassifier.Services;
using GetSiteData.Common;

namespace CellsClassifier.Commands;

/// <summary>
/// Обучение (или переобучение) модели по размеченным документам.
/// Без force обучение пропускается, если размеченная выборка не изменилась
/// со времени последнего обучения (сравнение по числу размеченных).
/// </summary>
public class TrainCommand(LiteDbRepository repo, MlTrainer trainer)
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

        // Разметки мало или нет совсем: при обычном запуске (process) это не ошибка —
        // классификация пойдёт по эвристике ключевых слов. Ошибкой это является только
        // при явном запросе обучения (train --force).
        if (labeled.Count < MlTrainer.MinLabeledDocuments)
        {
            if (force)
            {
                throw new InvalidOperationException(
                    $"Недостаточно размеченных документов для обучения: {labeled.Count} (нужно минимум {MlTrainer.MinLabeledDocuments}).");
            }

            Log.Warn($"Размеченных документов {labeled.Count} < {MlTrainer.MinLabeledDocuments} — модель не обучаем, классификация по эвристике.");
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
