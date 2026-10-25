namespace DataManager.Core.Models;

public enum HistoryStatus
{
    Pending,
    Active,
    Reverted,
    Failed,
    Conflict
}

public sealed class HistoryEntry
{
    public string SchemaVersion { get; set; } = "2.0";
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string OperationKind { get; set; } = "数据保存";
    public string Summary { get; set; } = string.Empty;
    public HistoryStatus Status { get; set; } = HistoryStatus.Active;
    public string ProfileId { get; set; } = string.Empty;
    public string VariantId { get; set; } = string.Empty;
    public string LayoutRevision { get; set; } = string.Empty;
    public string DetectionMethod { get; set; } = string.Empty;
    public Dictionary<string, string> CoreFileFingerprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Modules { get; set; } = [];
    public List<HistoryTableChange> Tables { get; set; } = [];
    public List<FileByteChange> Files { get; set; } = [];
    public List<string> AppearanceSyncMessages { get; set; } = [];
}

public sealed record HistoryTableChange(
    string TableName,
    int RowIndex,
    int Id,
    string FieldName,
    string OldValue,
    string NewValue,
    long AbsoluteOffset,
    int ByteLength);

public sealed record HistoryOperationResult(bool Success, string Message, HistoryEntry? Entry = null);
