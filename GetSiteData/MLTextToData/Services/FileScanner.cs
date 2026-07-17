using GetSiteData.Common;

namespace CellsClassifier.Services;

/// <summary>
/// Обходит входной каталог и отдаёт пути текстовых документов конвейера (*.txt
/// с именем «Номер заключения и дата» — единый формат всех этапов).
/// </summary>
public class FileScanner(string root)
{
    public string Root { get; } = root;

    public IEnumerable<string> Scan()
    {
        // Ранее здесь было "txt".Contains(Path.GetExtension(f)) — ОШИБКА:
        // GetExtension возвращает расширение С ТОЧКОЙ (".txt"), а строка "txt"
        // точку не содержит, поэтому отбирались только файлы БЕЗ расширения.
        return Directory.EnumerateFiles(Root, "*.txt", SearchOption.AllDirectories)
            .Where(DocumentName.IsValidFileName);
    }
}
