using System.Security.Cryptography;

namespace CellsClassifier.Services;

/// <summary>
/// SHA-256-хэш содержимого файла — ключ дедупликации: файл с тем же хэшем и
/// датой изменения повторно не классифицируется.
/// </summary>
public class HashService
{
    public string ComputeHash(string path)
    {
        // Статический SHA256.HashData не создаёт объект алгоритма на каждый файл
        // (в отличие от SHA256.Create()) — заметно на десятках тысяч файлов.
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
