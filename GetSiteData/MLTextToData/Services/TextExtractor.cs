namespace CellsClassifier.Services;

/// <summary>
/// Извлекает текст документа. Сейчас документы — простые .txt (выход ParseHTML),
/// поэтому чтение тривиально; класс выделен как точка расширения на случай
/// других форматов (PDF, DOCX) без переделки ProcessCommand.
/// </summary>
public class TextExtractor
{
    public string ExtractText(string path) => File.ReadAllText(path);
}
