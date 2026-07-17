using CellsClassifier.Models;
using CellsClassifier.Services;
using GetSiteData.Common;

namespace CellsClassifier.Commands;

/// <summary>
/// Массовая разметка обучающей выборки: все .txt из указанного каталога
/// (рекурсивно) получают метку «про сотовую связь» (cells) или «нет» (other).
/// Используется для подготовки данных перед «train»: положительные — выход
/// поиска по «станционным» терминам, отрицательные — выборка посторонних СЭЗ
/// (склады, котельные, АЗС и т.п.), скачанных по нейтральным терминам.
/// </summary>
public class LabelCommand(HashService hashService, LiteDbRepository repo)
{
    public async Task RunAsync(string directory, bool isCells)
    {
        if (!Directory.Exists(directory))
        {
            Log.Error($"Каталог не найден: {directory}");
            return;
        }

        // Обучающая выборка — те же документы конвейера: разметку получают только
        // файлы единого формата «Номер заключения и дата».
        var files = Directory.EnumerateFiles(directory, "*.txt", SearchOption.AllDirectories)
            .Where(DocumentName.IsValidFileName)
            .ToList();
        Log.Phase($"Разметка «{(isCells ? "cells" : "other")}»: файлов — {files.Count}");

        int labeled = 0;
        foreach (var path in files)
        {
            var fullPath = Path.GetFullPath(path);
            var doc = await repo.GetByPathAsync(fullPath)
                ?? new DocumentInfo
                {
                    Id = Guid.NewGuid().ToString("N"),
                    FilePath = fullPath,
                    CreatedAtUtc = DateTime.UtcNow
                };

            doc.Hash = hashService.ComputeHash(fullPath);
            doc.LastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            doc.IsCellsLabel = isCells;

            await repo.UpsertAsync(doc);
            labeled++;
        }

        Log.Ok($"Размечено документов: {labeled}");
    }
}
