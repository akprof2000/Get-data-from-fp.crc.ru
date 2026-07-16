using CellsClassifier.Models;
using CellsClassifier.Services;
using GetSiteData.Common;

namespace CellsClassifier.Commands;

/// <summary>
/// Основная команда классификатора: обходит входной каталог, для каждого нового
/// или изменённого документа предсказывает класс («про сотовую связь» / «нет»)
/// и раскладывает копии по каталогам CellsOutput / NonCellsOutput.
/// Уже обработанные документы (по хэшу и дате изменения) пропускаются.
/// </summary>
public class ProcessCommand(
    FileScanner scanner,
    TextExtractor extractor,
    HashService hashService,
    LiteDbRepository repo,
    MlPredictor predictor,
    string cellsOutput,
    string otherOutput,
    int parallelDegree,
    float threshold)
{
    public async Task RunAsync()
    {
        _ = Directory.CreateDirectory(cellsOutput);
        _ = Directory.CreateDirectory(otherOutput);

        var files = scanner.Scan().ToList();
        Log.Phase($"Классификация: файлов к обработке — {files.Count}");

        int processed = 0;
        int failed = 0;

        // Parallel.ForEachAsync вместо прежнего Parallel.ForEach с
        // .GetAwaiter().GetResult(): не блокируем потоки пула на асинхронном
        // вводе-выводе (LiteDB.Async) и не рискуем взаимоблокировкой.
        var options = new ParallelOptions { MaxDegreeOfParallelism = parallelDegree };
        await Parallel.ForEachAsync(files, options, async (file, ct) =>
        {
            try
            {
                await ProcessFileAsync(file);
                _ = Interlocked.Increment(ref processed);
            }
            catch (Exception ex)
            {
                _ = Interlocked.Increment(ref failed);
                Log.Error($"{file}: {ex.Message}");
            }
        });

        Log.Phase($"Готово: обработано {processed}, ошибок {failed}.");
    }

    private async Task ProcessFileAsync(string path)
    {
        var hash = hashService.ComputeHash(path);
        var lastWrite = File.GetLastWriteTimeUtc(path);

        // Дедупликация в два ключа: тот же контент (хэш) или тот же путь,
        // при неизменной дате записи — файл уже классифицирован, пропускаем.
        var byHash = await repo.GetByHashAsync(hash);
        if (byHash is { Processed: true } && byHash.LastWriteTimeUtc == lastWrite)
        {
            return;
        }

        var existing = await repo.GetByPathAsync(path);
        if (existing is { Processed: true } && existing.LastWriteTimeUtc == lastWrite)
        {
            return;
        }

        var text = extractor.ExtractText(path);
        var (predictedLabel, score) = Classify(text);

        var doc = existing ?? new DocumentInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            FilePath = path,
            CreatedAtUtc = DateTime.UtcNow
        };

        doc.Hash = hash;
        doc.LastWriteTimeUtc = lastWrite;
        doc.Processed = true;
        doc.ProcessedAtUtc = DateTime.UtcNow;
        doc.PredictedIsCells = predictedLabel;
        doc.PredictedScore = score;

        await repo.UpsertAsync(doc);

        // Копия уходит в каталог своего класса. Имена документов уникальны
        // (номер заключения), поэтому конфликт имён из разных подкаталогов
        // практически исключён; overwrite — на случай переобработки.
        var target = predictedLabel ? cellsOutput : otherOutput;
        var dest = Path.Combine(target, Path.GetFileName(path));
        File.Copy(path, dest, overwrite: true);
    }

    /// <summary>
    /// Классифицирует текст: обученной моделью, а до её появления — простой
    /// эвристикой по ключевым словам предметной области.
    /// </summary>
    private (bool IsCells, float Score) Classify(string text)
    {
        if (predictor.IsReady)
        {
            var prediction = predictor.Predict(text);
            return (prediction.PredictedLabel && prediction.Score >= threshold, prediction.Score);
        }

        var lower = text.ToLowerInvariant();
        var hasCells = lower.Contains("базовая станция") ||
                       lower.Contains("сотовой связи") ||
                       lower.Contains("lte") ||
                       lower.Contains("gsm") ||
                       lower.Contains("умts") ||
                       lower.Contains("передающего радиотехнического объекта") ||
                       lower.Contains("рэс");
        return (hasCells, hasCells ? 0.7f : 0.3f);
    }
}
