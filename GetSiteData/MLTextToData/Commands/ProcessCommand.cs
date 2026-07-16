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
    /// Классифицирует текст: обученной моделью, а до её появления — эвристикой
    /// по ключевым словам. Эвристика повторяет правило IsBaseStationDocument из
    /// ParseTextHeader, отлаженное регрессионно на корпусе 111 917 документов
    /// (пропуски серий с сокращёнными диапазонами «D1800; L1800» и т.п. учтены).
    /// </summary>
    private (bool IsCells, float Score) Classify(string text)
    {
        if (predictor.IsReady)
        {
            var prediction = predictor.Predict(text);
            return (prediction.PredictedLabel && prediction.Score >= threshold, prediction.Score);
        }

        return (IsBaseStationHeuristic(text), 0.5f);
    }

    private static bool IsBaseStationHeuristic(string text)
    {
        // Явное упоминание базовой станции — достаточный признак сам по себе
        // (покрывает «базовая/базовой/базовую станцию»).
        if (text.Contains("базовая станци", StringComparison.OrdinalIgnoreCase)
            || text.Contains("базовой станци", StringComparison.OrdinalIgnoreCase)
            || text.Contains("базовую станци", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Без явной фразы — требуем сотовый/радио-маркер И маркер РЭС/ПРТО.
        bool hasCellular = text.Contains("GSM", StringComparison.OrdinalIgnoreCase)
            || text.Contains("LTE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("UMTS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("DCS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("сотовой", StringComparison.OrdinalIgnoreCase)
            || text.Contains("радиотелефонной", StringComparison.OrdinalIgnoreCase)
            || text.Contains("подвижной", StringComparison.OrdinalIgnoreCase)
            || text.Contains("BTS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ПРТО", StringComparison.OrdinalIgnoreCase)
            || text.Contains("радиорелейн", StringComparison.OrdinalIgnoreCase)
            || text.Contains("высокоскоростной железнодорожной", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ВСМ", StringComparison.OrdinalIgnoreCase);

        if (!hasCellular)
        {
            return false;
        }

        return text.Contains("радиоэлектронных средств", StringComparison.OrdinalIgnoreCase)
            || text.Contains("радиоэлектронного средства", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ПРТО", StringComparison.OrdinalIgnoreCase);
    }
}
