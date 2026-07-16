// ParseHTML — второй этап пайплайна: разбирает скачанные GetSiteData страницы
// результатов (output/**/page-N.html), выделяет из последней таблицы каждой
// страницы отдельные документы-заключения и сохраняет каждый в текстовый файл
// documents/<год>/<месяц>/<номер заключения>.txt для дальнейшей обработки
// (MLTextToData → ParseTextHeader).
using GetSiteData.Common;
using HtmlAgilityPack;
using System.Text;
using System.Text.RegularExpressions;

internal partial class Program
{
    private const string OutputDirectory = "output";       // вход: HTML от GetSiteData
    private const string DocumentsDirectory = "documents"; // выход: тексты документов

    // Имя документа заканчивается датой «ДД.ММ.ГГГГ» — из неё берём подкаталоги год/месяц.
    [GeneratedRegex(@"\d{2}\.\d{2}\.\d{4}$")]
    private static partial Regex TrailingDateRegex();

    private static void Main()
    {
        try
        {
            ProcessHtmlFiles();
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
        }
    }

    private static void ProcessHtmlFiles()
    {
        _ = Directory.CreateDirectory(OutputDirectory);
        _ = Directory.CreateDirectory(DocumentsDirectory);

        foreach (string item in Directory.EnumerateFiles(OutputDirectory, "*.html", SearchOption.AllDirectories))
        {
            try
            {
                Log.Info($"Читаю файл: {item}");
                HtmlDocument doc = new();
                doc.Load(item);

                // Результаты поиска лежат в последней таблице страницы.
                HtmlNodeCollection tables = doc.DocumentNode.SelectNodes("//table");
                if (tables == null || tables.Count == 0)
                {
                    continue;
                }

                ProcessTableRows(tables[^1]);
            }
            catch (Exception ex)
            {
                // Один повреждённый файл не должен прерывать всю пачку.
                Log.Error($"Не удалось обработать «{item}»: {ex.Message}");
            }
        }
    }

    // Идёт по строкам таблицы, накапливая текст очередного документа в sb.
    // Горизонтальная линия <hr> в ячейке — разделитель между документами.
    private static void ProcessTableRows(HtmlNode table)
    {
        bool nextIsName = false;
        StringBuilder sb = new();
        string fileName = "";

        foreach (HtmlNode row in table.SelectNodes(".//tr"))
        {
            HtmlNodeCollection cells = row.SelectNodes(".//td");
            if (cells == null)
            {
                continue;
            }

            foreach (HtmlNode cell in cells)
            {
                ProcessCell(cell, sb, ref fileName, ref nextIsName);
            }
        }

        // Последний документ страницы — за ним разделителя уже нет.
        WriteToFile(sb, fileName);
    }

    private static void ProcessCell(HtmlNode cell, StringBuilder sb, ref string fileName, ref bool nextIsName)
    {
        string cellHtml = cell.InnerHtml;

        // <hr> — граница документов: сбрасываем накопленное в файл и начинаем новый.
        if (cellHtml.Contains("<hr size=\"1\" width=\"100%\">"))
        {
            if (!string.IsNullOrEmpty(fileName))
            {
                WriteToFile(sb, fileName);
            }

            _ = sb.Clear();
            fileName = "";
        }

        HtmlDocument cll = new();
        cll.LoadHtml(cellHtml);

        HtmlNodeCollection nodes = cll.DocumentNode.SelectNodes("//p | //b");
        if (nodes != null)
        {
            ProcessNodes(nodes, sb, ref fileName, ref nextIsName);
        }
        else
        {
            // Ячейка без разметки — обычный текст.  — управляющий символ
            // от длинного тире windows-1251 (U+0097), в тексте не нужен.
            string text = HtmlEntity.DeEntitize(cell.InnerText).Replace("\n", Environment.NewLine).Replace('\u0097', ' ');
            _ = sb.AppendLine(text);
            if (text.Contains("Номер заключения и дата"))
            {
                // Следующий текстовый узел — номер заключения, он станет именем файла.
                nextIsName = true;
            }
        }
    }

    private static void ProcessNodes(HtmlNodeCollection nodes, StringBuilder sb, ref string fileName, ref bool nextIsName)
    {
        string lastStr = string.Empty;

        foreach (HtmlNode node in nodes)
        {
            // <br> внутри абзаца — реальный перенос строки в тексте документа.
            string parsedText = node.InnerHtml.Replace("<br>", Environment.NewLine);
            HtmlDocument cl = new();
            cl.LoadHtml(parsedText);
            string str = HtmlEntity.DeEntitize(cl.DocumentNode.InnerText);

            // Вложенные <b> внутри <p> дают тот же текст дважды — пропускаем повтор.
            if (lastStr != str)
            {
                _ = sb.AppendLine(str);
                lastStr = str;
            }

            if (nextIsName)
            {
                nextIsName = false;
                fileName = Path.Combine(DocumentsDirectory, $"{str}.txt");
            }
        }
    }

    private static void WriteToFile(StringBuilder sb, string fileName)
    {
        // Ничего не накоплено или имя не определено (маркер «Номер заключения и дата»
        // не встретился) — просто пропускаем, не падая на срезах ниже.
        if (sb.Length == 0 || string.IsNullOrEmpty(fileName))
        {
            return;
        }

        // Имя вида «01.РА.01.000.Т.000001.01.22 от 10.01.2022» — год и месяц
        // берём из завершающей даты «ДД.ММ.ГГГГ».
        string nm = Path.GetFileNameWithoutExtension(fileName);
        if (!TrailingDateRegex().IsMatch(nm))
        {
            Log.Warn($"Неожиданное имя документа, пропускаю: {nm}");
            return;
        }

        string yr = nm[^4..];    // год: последние 4 символа
        string mh = nm[^7..^5];  // месяц: 2 символа перед годом

        string fn = Path.GetFileName(fileName);
        string pth = Path.GetDirectoryName(fileName)!;
        string fp = Path.Combine(pth, yr, mh);

        _ = Directory.CreateDirectory(fp);

        fn = Path.Combine(fp, fn);

        if (!File.Exists(fn))
        {
            // Второй Replace: неразрывный пробел (U+00A0) → обычный, чтобы регулярные
            // выражения ParseTextHeader видели однородные пробелы.
            File.WriteAllText(fn, sb.ToString().Replace('\u00A0', ' '));
            Log.Ok($"Записан файл: {fn}");
        }
    }
}
