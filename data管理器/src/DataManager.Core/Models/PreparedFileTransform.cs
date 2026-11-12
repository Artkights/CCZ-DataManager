namespace DataManager.Core.Models;

/// <summary>
/// Builds a format-specific candidate after ordinary edits to the same file.
/// The transform must preserve their original byte locations; format-specific
/// relocation and overlapping-write checks remain the transform's responsibility.
/// </summary>
public sealed record PreparedFileTransform(
    string RelativePath,
    string ExpectedFileHash,
    Func<PreparedFileTransformContext, byte[]> Transform,
    string Module,
    string Summary,
    Action<byte[]>? CandidateVerifier = null);

/// <summary>
/// Owned snapshots of the common disk baseline and the ordinary-edit candidate.
/// AppliedPatches contains contiguous actual differences from baseline to candidate,
/// with offsets in the original file. Mutating these snapshots cannot change the
/// workspace's retained baseline or its checks after the transform returns.
/// </summary>
public sealed record PreparedFileTransformContext(
    byte[] BaselineBytes,
    byte[] CandidateBytes,
    IReadOnlyList<PreparedBytePatch> AppliedPatches);
