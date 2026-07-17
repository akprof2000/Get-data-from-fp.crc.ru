using System.Diagnostics;

namespace GetSiteData.Common;

/// <summary>
/// Единый логгер для всех утилит пайплайна (GetSiteData → ParseHTML →
/// MLTextToData → ParseTextHeader).
///
/// Пишет одновременно в консоль и в файл <c>logs/&lt;приложение&gt;_ГГГГММДД_ЧЧММСС.log</c>
/// рядом с исполняемым файлом (UTF-8). Формат строки:
/// <c>[HH:mm:ss] [УРОВЕНЬ] сообщение</c> — уровень опускается для Info,
/// чтобы не зашумлять массовый построчный вывод (сотни тысяч строк на полном корпусе).
///
/// Потокобезопасен: вывод сериализуется блокировкой — и для цветной консоли
/// (пара «установить цвет → написать → сбросить» перемешивается при параллельной
/// обработке), и для общего файлового потока.
/// </summary>
public static class Log
{
    private static readonly Lock ConsoleLock = new();

    // Файл лога открывается лениво при первой записи: имя — процесс + время запуска.
    // AutoFlush: при аварийном завершении процесса последние строки не теряются.
    private static readonly Lazy<StreamWriter?> LogFile = new(() =>
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            _ = Directory.CreateDirectory(dir);
            var name = $"{Process.GetCurrentProcess().ProcessName}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            return new StreamWriter(Path.Combine(dir, name), append: false, System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch
        {
            // Нет прав на запись рядом с приложением — работаем только с консолью,
            // падать из-за лога нельзя.
            return null;
        }
    });

    /// <summary>Полный путь к файлу лога текущего запуска (null — файл недоступен).</summary>
    public static string? FilePath => (LogFile.Value?.BaseStream as FileStream)?.Name;

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
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (ConsoleLock)
        {
            Console.WriteLine();
            Console.WriteLine(line);
            LogFile.Value?.WriteLine();
            LogFile.Value?.WriteLine(line);
        }
    }

    private static void Write(string? level, string message, ConsoleColor? color)
    {
        var line = level is null
            ? $"[{DateTime.Now:HH:mm:ss}] {message}"
            : $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";

        lock (ConsoleLock)
        {
            if (color is not null)
            {
                Console.ForegroundColor = color.Value;
            }

            Console.WriteLine(line);

            if (color is not null)
            {
                Console.ResetColor();
            }

            LogFile.Value?.WriteLine(line);
        }
    }
}
