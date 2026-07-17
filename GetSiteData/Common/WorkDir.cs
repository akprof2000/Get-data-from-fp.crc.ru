using Microsoft.Extensions.Configuration;

namespace GetSiteData.Common;

/// <summary>
/// Общий корень рабочих данных конвейера (ключ «WorkRoot» в appsettings.json,
/// по умолчанию «works»). Все утилиты складывают свои каталоги под ним:
/// works/output → works/documents → works/cells → works/OutputJson…
/// </summary>
public static class WorkDir
{
    /// <summary>Каталог по умолчанию, если ключ «WorkRoot» не задан.</summary>
    public const string DefaultRoot = "works";

    /// <summary>Читает «WorkRoot» из конфигурации (корневой ключ, общий для всех этапов).</summary>
    public static string GetRoot(IConfiguration config) =>
        config["WorkRoot"] is { Length: > 0 } root ? root : DefaultRoot;

    /// <summary>
    /// Приводит путь этапа к общему корню: относительный — собирается под WorkRoot,
    /// абсолютный — используется как есть (позволяет вынести отдельный каталог на другой диск).
    /// </summary>
    public static string Resolve(string root, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(root, path);
}
