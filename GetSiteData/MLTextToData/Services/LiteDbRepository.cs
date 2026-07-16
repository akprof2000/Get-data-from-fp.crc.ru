using CellsClassifier.Models;
using LiteDB;
using LiteDB.Async;

namespace CellsClassifier.Services;

/// <summary>
/// Хранилище состояния классификатора в LiteDB: сведения об обработанных
/// документах (хэш, метки, предсказания) и метаданные последнего обучения.
/// </summary>
public class LiteDbRepository : IDisposable
{
    private readonly LiteDatabaseAsync _db;
    private readonly ILiteCollectionAsync<DocumentInfo> _docs;
    private readonly ILiteCollectionAsync<TrainingMetadata> _meta;

    public LiteDbRepository(string path)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new LiteDatabaseAsync(path);
        _docs = _db.GetCollection<DocumentInfo>("documents");
        // Индексы по пути и хэшу — оба используются как ключи поиска при дедупликации.
        _docs.EnsureIndexAsync(x => x.FilePath).Wait();
        _docs.EnsureIndexAsync(x => x.Hash).Wait();
        _meta = _db.GetCollection<TrainingMetadata>("training_meta");
    }

    public async Task<DocumentInfo?> GetByPathAsync(string path) =>
        await _docs.FindOneAsync(x => x.FilePath == path);

    public async Task<DocumentInfo?> GetByHashAsync(string hash) =>
        await _docs.FindOneAsync(x => x.Hash == hash);

    public async Task UpsertAsync(DocumentInfo doc) =>
        await _docs.UpsertAsync(doc);

    /// <summary>Документы с ручной разметкой — обучающая выборка.</summary>
    public async Task<IEnumerable<DocumentInfo>> GetLabeledAsync() =>
        await _docs.FindAsync(x => x.IsCellsLabel != null);

    public async Task<TrainingMetadata?> GetTrainingMetadataAsync() =>
        await _meta.FindOneAsync(Query.All());

    public async Task SaveTrainingMetadataAsync(TrainingMetadata meta)
    {
        // Метаданные обучения — одиночная запись: перезаписываем целиком.
        await _meta.DeleteAllAsync();
        await _meta.InsertAsync(meta);
    }

    public void Dispose() => _db.Dispose();
}
