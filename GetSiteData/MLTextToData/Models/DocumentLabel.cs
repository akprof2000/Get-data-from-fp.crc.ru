namespace CellsClassifier.Models;

public class DocumentLabel
{
    public bool Label { get; set; } // true = cells
    public string Text { get; set; } = default!;
}