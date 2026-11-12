namespace RangeManager.App;

/// <summary>A frozen, in-memory save draft. No member commits files.</summary>
public sealed record RangeSaveCandidate(
    string RootPath,
    string ExecutablePath,
    byte[] BaselineExecutable,
    byte[] Executable,
    IReadOnlyDictionary<string, byte[]> Files,
    IReadOnlyDictionary<string, string> ExpectedHashes,
    Func<byte[], byte[]> BuildExecutable)
{
    public Action<byte[]>? ExecutableVerifier { get; init; }
}
