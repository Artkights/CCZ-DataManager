namespace DataManager.Core.Models;

/// <summary>A verified complete candidate for a format whose parameter edits may change file length.</summary>
public sealed record PreparedFileEdit(
    string RelativePath,
    string ExpectedFileHash,
    byte[] CandidateBytes,
    string Module,
    string Summary,
    Action<byte[]>? CandidateVerifier = null);
