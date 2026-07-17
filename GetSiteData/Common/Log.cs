using Microsoft.Extensions.Configuration;
using NLog;
using NLog.Extensions.Logging;

namespace GetSiteData.Common;

/// <summary>
/// Единый логгер всех утилит пайплайна — тонкий фасад над NLog с прежним
/// API (Info/Ok/Skip/Warn/Error/Phase), чтобы приложения не зависели от
/// библиотеки логирования напрямую.
///
/// Вся настройка (куда писать, размер и число файлов, формат строки) — в секции
/// «NLog» общего appsettings.json рядом с приложением. Если секции нет, работает
/// запасная конфигурация: цветная консоль + файл logs/&lt;приложение&gt;.log
/// с ротацией по 20 МБ.
/// </summary>
public static class Log
{
    private static readonly Logger Logger = CreateLogger();

    private static Logger CreateLogger()
    {
        // Каждый запуск начинается с чистой папки логов (файлы, занятые другим
        // работающим процессом конвейера, пропускаем молча).
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        try
        {
            if (Directory.Exists(logsDir))
            {
                foreach (var old in Directory.EnumerateFiles(logsDir, "*.log"))
                {
                    try { File.Delete(old); } catch { /* занят — оставляем */ }
                }
            }
        }
        catch { /* очистка не должна мешать запуску */ }

        var nlogSection = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build()
            .GetSection("NLog");

        LogManager.Configuration = nlogSection.Exists()
            ? new NLogLoggingConfiguration(nlogSection)
            : FallbackConfiguration();

        // Гасим логгер при выходе процесса: асинхронные цели дописывают хвост на диск.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => LogManager.Shutdown();

        return LogManager.GetLogger("Pipeline");
    }

    // Запасная конфигурация на случай отсутствия секции «NLog» в appsettings.json.
    private static NLog.Config.LoggingConfiguration FallbackConfiguration()
    {
        var config = new NLog.Config.LoggingConfiguration();
        var layout = "[${date:format=HH\\:mm\\:ss}]${when:when=level>LogLevel.Info:inner= [${uppercase:${level}}]} ${message}";
        config.AddRule(LogLevel.Info, LogLevel.Fatal,
            new NLog.Targets.ColoredConsoleTarget("console") { Layout = layout });
        config.AddRule(LogLevel.Info, LogLevel.Fatal,
            new NLog.Targets.FileTarget("file")
            {
                FileName = "${basedir}/logs/${processname}.log",
                Layout = layout,
                ArchiveAboveSize = 20 * 1024 * 1024,
                MaxArchiveFiles = 10,
                DeleteOldFileOnStartup = true
            });
        return config;
    }

    /// <summary>Обычное сообщение хода работы (без префикса уровня).</summary>
    public static void Info(string message) => Logger.Info(message);

    /// <summary>Успешно обработанный элемент.</summary>
    public static void Ok(string message) => Logger.Info("[OK] " + message);

    /// <summary>Элемент пропущен (уже обработан, не подходит по типу и т.п.).</summary>
    public static void Skip(string message) => Logger.Info("[SKIP] " + message);

    /// <summary>Неполные данные или подозрительная ситуация — работа продолжается.</summary>
    public static void Warn(string message) => Logger.Warn(message);

    /// <summary>Ошибка обработки элемента — элемент пропущен, работа продолжается.</summary>
    public static void Error(string message) => Logger.Error(message);

    /// <summary>Разделитель фаз (начало загрузки, итоги и т.п.) — выделен пустой строкой.</summary>
    public static void Phase(string message)
    {
        Logger.Info(string.Empty);
        Logger.Info(message);
    }
}
