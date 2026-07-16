namespace GetSiteData.Common;

/// <summary>
/// Единый консольный логгер для всех утилит пайплайна (GetSiteData → ParseHTML →
/// MLTextToData → ParseTextHeader).
///
/// Формат строки: <c>[HH:mm:ss] [УРОВЕНЬ] сообщение</c> — уровень опускается для Info,
/// чтобы не зашумлять массовый построчный вывод (сотни тысяч строк на полном корпусе).
///
/// Потокобезопасен: цветной вывод сериализуется блокировкой, т.к. пара
/// «установить цвет → написать → сбросить цвет» без неё перемешивается при
/// параллельной обработке файлов.
/// </summary>
public static class Log
{
    private static readonly Lock ConsoleLock = new();

    /// <summary>Обычное сообщение хода работы (без префикса уровня).</summary>
    public static void Info(string message) => Write(null, message, null);

    /// <summary>Успешно обработанный элемент.</summary>
    public static void Ok(string message) => Write("OK", message, null);

    /// <summary>Элемент пропущен (уже обработан, не подходит по типу и т.п.).</summary>
    public static void Skip(string message) => Write("SKIP", message, ConsoleColor.DarkGray);

    /// <summary>Неполные данные или подозрительная ситуация — работа продолжается.</summary>
    public static void Warn(string message) => Write("WARN", message, ConsoleColor.Yellow);

    /// <summary>Ошибка обработки элемента — элемент пропущен, работа продолжается.</summary>
    public static void Error(string message) => Write("ERROR", message, ConsoleColor.Red);

    /// <summary>Разделитель фаз (начало загрузки, итоги и т.п.) — с пустой строкой сверху.</summary>
    public static void Phase(string message)
    {
        lock (ConsoleLock)
        {
            Console.WriteLine();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }

    private static void Write(string? level, string message, ConsoleColor? color)
    {
        var line = level is null
            ? $"[{DateTime.Now:HH:mm:ss}] {message}"
            : $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";

        if (color is null)
        {
            // Бесцветный вывод не требует блокировки: Console.WriteLine сам атомарен.
            Console.WriteLine(line);
            return;
        }

        lock (ConsoleLock)
        {
            Console.ForegroundColor = color.Value;
            Console.WriteLine(line);
            Console.ResetColor();
        }
    }
}
