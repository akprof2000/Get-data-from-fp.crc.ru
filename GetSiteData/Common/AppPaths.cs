namespace GetSiteData.Common;

/// <summary>
/// Пути поставки. Приложения лежат в подкаталоге «bin», а всё, с чем работает
/// человек — appsettings.json, data/, works/, logs/, скрипты запуска — в корне
/// поставки рядом с лаунчерами. Поэтому конфигурацию и данные ищем сначала
/// рядом с exe, затем на уровень выше.
/// </summary>
public static class AppPaths
{
    /// <summary>Каталог с исполняемым файлом (обычно «…/bin»).</summary>
    public static string BinDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>
    /// Корень поставки: каталог, где лежит appsettings.json. Это каталог exe,
    /// а если конфигурации там нет — родительский (раскладка с «bin»).
    /// </summary>
    public static string Root { get; } = ResolveRoot();

    /// <summary>Полный путь к общему appsettings.json (может не существовать).</summary>
    public static string ConfigFile => Path.Combine(Root, "appsettings.json");

    /// <summary>Путь внутри поставки: «data/model.zip» → «&lt;корень&gt;/data/model.zip».</summary>
    public static string InRoot(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string ResolveRoot()
    {
        var bin = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(bin, "appsettings.json")))
            return bin;

        var parent = Directory.GetParent(bin.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        if (parent != null && File.Exists(Path.Combine(parent, "appsettings.json")))
            return parent;

        // Конфигурации нет нигде — работаем от каталога exe на значениях по умолчанию.
        return bin;
    }
}
