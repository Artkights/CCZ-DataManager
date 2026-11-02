using System.Buffers.Binary;

namespace DataManager.Core.Scanner;

/// <summary>Progress is separate from the report so timing never changes scan evidence.</summary>
public sealed record EngineEffectScanProgress(string Stage, int CompletedStages, int TotalStages,
    long ElapsedMilliseconds);

internal readonly record struct EngineEffectRawCall(uint Address, uint Target);

internal enum EngineEffectAnalysisKind
{
    FunctionEntry, Reachable, Profile, ControlFlowGraph, CallEffects, BranchCandidates
}

/// <summary>
/// Caches belong to one immutable PE byte snapshot, never to a file name or mtime.
/// Keys retain every analysis limit; a conservative bounded walk is not interchangeable
/// with a larger one. Mutable argument/write plans are deliberately not cached here.
/// </summary>
internal sealed class EngineEffectScanContext
{
    private readonly EngineEffectBinaryImage _image;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<(EngineEffectAnalysisKind Kind, uint Address, int Limit), object?> _analyses = [];
    internal readonly Dictionary<uint, DecodedInstruction?> Instructions = [];
    internal readonly Dictionary<(uint Address, int Bytes, bool StopAtCall), IReadOnlyList<DecodedInstruction>> Forward = [];
    internal readonly Dictionary<(uint Address, int Bytes), IReadOnlyList<DecodedInstruction>?> Backtrack = [];
    private IReadOnlyList<EngineEffectRawCall>? _rawCalls;

    public EngineEffectScanContext(EngineEffectBinaryImage image, CancellationToken cancellationToken)
    {
        _image = image;
        _cancellationToken = cancellationToken;
    }

    public void CheckCancellation() => _cancellationToken.ThrowIfCancellationRequested();

    public T GetOrBuild<T>(EngineEffectAnalysisKind kind, uint address, int limit, Func<T> build)
    {
        CheckCancellation();
        var key = (kind, address, limit);
        if (_analyses.TryGetValue(key, out var cached)) return (T)cached!;
        var result = build();
        _analyses.Add(key, result);
        return result;
    }

    public IReadOnlyList<EngineEffectRawCall> RawCalls => _rawCalls ??= ReadRawCalls();

    private IReadOnlyList<EngineEffectRawCall> ReadRawCalls()
    {
        var result = new List<EngineEffectRawCall>();
        foreach (var section in _image.ExecutableSections)
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, _image.Bytes.LongLength - start));
            if (length < 5) continue;
            var span = _image.Bytes.AsSpan(start, length);
            for (var index = 0; index <= length - 5; index++)
            {
                if ((index & 0xFFFF) == 0) CheckCancellation();
                if (span[index] != 0xE8) continue;
                var relative = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(index + 1, 4));
                var address = section.VirtualAddress + (uint)index;
                result.Add(new(address, unchecked((uint)(address + 5 + relative))));
            }
        }
        return result;
    }
}
