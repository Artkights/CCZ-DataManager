namespace DataManager.Core.Models;

public sealed record ModuleDefinition(string Key, string DisplayName, IReadOnlyList<string> TableKeys);

public sealed record FileByteChange(
    string RelativePath,
    string BeforeHash,
    string AfterHash,
    long FileLength,
    IReadOnlyList<ByteChunk> Chunks)
{
    // Absent on 2.0 history entries, which always contain equal-length byte patches.
    public bool WholeFileReplacement { get; init; }
    public long? BeforeLength { get; init; }
}

public sealed record ByteChunk(long Offset, byte[] OldBytes, byte[] NewBytes)
{
    public int Length => NewBytes.Length;
}

public sealed record SaveResult(
    bool Saved,
    int ChangedTables,
    int ChangedFields,
    int ChangedBytes,
    HistoryEntry? History,
    IReadOnlyList<string> Messages);

public sealed class WorkspaceStatus
{
    public string RootPath { get; init; } = string.Empty;
    public string ScanSummary { get; set; } = "尚未扫描";
    public bool IsDirty { get; set; }
    public int DirtyTableCount { get; set; }
}
