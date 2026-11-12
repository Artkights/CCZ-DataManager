namespace Ccz.HistoryStorage;

public interface IHistoryBlobStore
{
    BlobReference Put(byte[] bytes);
    BlobReference PutFile(string path);
    BlobReference Put(Stream input);
    byte[] Read(BlobReference reference);
    void Verify(BlobReference reference);
    void CopyVerified(BlobReference reference, Stream output);
}

public interface IHistoryLedger
{
    string Root { get; }
    bool Exists { get; }
    IReadOnlyList<LedgerEvent> Read();
    void Append(string kind, string operationId, object payload, string? transactionId = null);
}

/// <summary>Listings are payload-free; the restore path explicitly requests one operation.</summary>
public interface IHistoryRepository<in TContext, TMetadata, TPayload>
{
    IReadOnlyList<TMetadata> ListMetadata(TContext context);
    TPayload LoadOperation(TContext context, string operationId);
}
