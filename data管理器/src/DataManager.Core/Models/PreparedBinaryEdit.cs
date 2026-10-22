namespace DataManager.Core.Models;

/// <summary>A bounded contribution to the workspace's existing, single save transaction.</summary>
public sealed record PreparedBinaryEdit(
    string RelativePath,
    string ExpectedFileHash,
    IReadOnlyList<PreparedBytePatch> Patches,
    string Module,
    string Summary,
    Action<byte[]>? CandidateVerifier = null);

public sealed record PreparedBytePatch(long Offset, byte[] ExpectedBytes, byte[] NewBytes);
