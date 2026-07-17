using System.Diagnostics;
using System.Text;

namespace GetSiteData.Common;

/// <summary>
/// Единый логгер для всех утилит пайплайна (GetSiteData → ParseHTML →
/// MLTextToData → ParseTextHeader).
///
/// Пишет одновременно в консоль и в файл <c>logs/&lt;приложение&gt;_ГГГГММДД_ЧЧММСС.log</c>
/// рядом с исполняемым файлом (UTF-8). При старте каталог логов очищается от
/// файлов прошлых запусков, а текущий файл при превышении <see cref="MaxFileSizeMb"/>
/// закрывается и продолжается в «..._part2.log», «..._part3.log» и т.д. —
/// гигантских файлов не бывает.
///
/// Формат строки: <c>[HH:mm:ss] [УРОВЕНЬ] сообщение</c> — уровень опускается для Info,
/// чтобы не зашумлять массовый построчный вывод (сотни тысяч строк на полном корпусе).
///
/// Потокобезопасен: вывод сериализуется блокировкой — и для цветной консоли
/// (пара «установить цвет → написать → сбросить» перемешивается при параллельной
/// обработке), и для общего файлового потока с ротацией.
/// </summary>
public static class Log
{
    /// <summary>Предел размера одного файла лога, МБ. Настраивается до первой записи.</summary>
    public static int MaxFileSizeMb { get; set; } = 20;

    private static readonly Lock ConsoleLock = new();

    private static bool _initialized;
    private static StreamWriter? _writer;
    private static string? _baseName;   // logs/<процесс>_<время> — без «_partN.log»
    private static int _part;
    private static long _written;       // байт в текущем файле (грубая оценка по длине строк)

    /// <summary>Полный путь к текущему файлу лога (null — файл недоступен).</summary>
    public static string? FilePath => (_writer?.BaseStream as FileStream)?.Name;

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
            WriteToFile(string.Empty);
            WriteToFile(line);
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

            WriteToFile(line);
        }
    }

    // Вызывается только под ConsoleLock.
    private static void WriteToFile(string line)
    {
        if (!_initialized)
        {
            Initialize();
        }

        if (_writer is null)
        {
            return; // каталог логов недоступен — работаем только с консолью
        }

        try
        {
            _writer.WriteLine(line);
            // Честный размер в байтах: кириллица в UTF-8 занимает 2 байта на символ,
            // подсчёт по длине строки давал файлы почти вдвое больше предела.
            _written += Encoding.UTF8.GetByteCount(line) + 2;

            // Ротация: файл вырос до предела — закрываем и продолжаем в следующем.
            if (_written >= (long)MaxFileSizeMb * 1024 * 1024)
            {
                _writer.Dispose();
                _part++;
                _written = 0;
                _writer = OpenWriter($"{_baseName}_part{_part}.log");
            }
        }
        catch
        {
            // Проблема с диском не должна валить обработку — далее только консоль.
            _writer = null;
        }
    }

    // Первая запись: чистим каталог от логов прошлых запусков и открываем свой файл.
    private static void Initialize()
    {
        _initialized = true;
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            _ = Directory.CreateDirectory(dir);

            // Каждый запуск начинается с чистой папки логов. Файлы, занятые другим
            // работающим процессом (параллельный этап конвейера), пропускаем молча.
            foreach (var old in Directory.EnumerateFiles(dir, "*.log"))
            {
                try { File.Delete(old); } catch { /* занят — оставляем */ }
            }

            _baseName = Path.Combine(dir, $"{Process.GetCurrentProcess().ProcessName}_{DateTime.Now:yyyyMMdd_HHmmss}");
            _part = 1;
            _writer = OpenWriter($"{_baseName}.log");
        }
        catch
        {
            _writer = null; // нет прав на запись — падать из-за лога нельзя
        }
    }

    // AutoFlush: при аварийном завершении процесса последние строки не теряются.
    private static StreamWriter? OpenWriter(string path)
    {
        try
        {
            return new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        }
        catch
        {
            return null;
        }
    }
}
