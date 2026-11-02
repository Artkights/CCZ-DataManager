namespace DataManager.Core.Scanner;

internal sealed record EngineEffectJump(uint Address, uint Target, string Kind);

/// <summary>
/// Byte-level index of direct jumps (E9/EB/7x/0F8x) in the executable sections.
/// Injected code caves are entered by jumps from elsewhere, so the ordinary
/// call graph cannot tell whether they are live; this index supplies the
/// missing inbound edges.
/// </summary>
internal sealed class EngineEffectJumpIndex
{
    private readonly IReadOnlyDictionary<uint, IReadOnlyList<EngineEffectJump>> _byTarget;
    private readonly uint[] _targets;

    internal int InstructionCount { get; }
    internal int IndexedJumpAnchorCount { get; }

    private EngineEffectJumpIndex(IReadOnlyDictionary<uint, IReadOnlyList<EngineEffectJump>> byTarget,
        int instructionCount, int indexedJumpAnchorCount)
    {
        _byTarget = byTarget;
        _targets = byTarget.Keys.OrderBy(address => address).ToArray();
        InstructionCount = instructionCount;
        IndexedJumpAnchorCount = indexedJumpAnchorCount;
    }

    public static EngineEffectJumpIndex Build(EngineEffectBinaryImage image)
    {
        var instructions = EngineEffectInstructionMap.Build(image);
        var byTarget = new Dictionary<uint, List<EngineEffectJump>>();
        foreach (var candidate in EngineEffectInstructionMap.EnumerateBranches(image))
        {
            if (!IsAlignedBranch(image, instructions, candidate)) continue;
            var jump = new EngineEffectJump(candidate.Address, candidate.Target, candidate.Kind);
            if (!byTarget.TryGetValue(candidate.Target, out var list))
            {
                list = [];
                byTarget[candidate.Target] = list;
            }
            list.Add(jump);
        }
        return new EngineEffectJumpIndex(byTarget.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<EngineEffectJump>)pair.Value.OrderBy(jump => jump.Address).ToArray()),
            instructions.InstructionCount, instructions.IndexedJumpAnchorCount);
    }

    /// <summary>
    /// Confirms the opcode starts a real instruction (not an immediate byte that
    /// happens to look like a jump), so inline data cannot create inbound edges.
    /// Both the branch address and its target must be instruction boundaries:
    /// an opcode found inside another instruction, or a target that lands in the
    /// middle of one, is an immediate byte rather than real control flow.
    /// </summary>
    private static bool IsAlignedBranch(EngineEffectBinaryImage image, EngineEffectInstructionMap instructions,
        EngineEffectBranchCandidate candidate)
    {
        if (instructions.IsInterior(candidate.Address) || instructions.IsInterior(candidate.Target)) return false;
        return EngineEffectInstructionMap.IsBranchAt(image, candidate.Address, candidate.Target, candidate.Length);
    }

    public IReadOnlyList<EngineEffectJump> SourcesOf(uint target)
        => _byTarget.TryGetValue(target, out var jumps) ? jumps : [];

    /// <summary>Inbound jumps whose target lies at or before the address within the given distance.</summary>
    public IReadOnlyList<EngineEffectJump> SourcesNear(uint address, int distance)
    {
        if (distance <= 0) return [];
        var lowest = address > (uint)distance ? address - (uint)distance : uint.MinValue;
        var highest = Array.BinarySearch(_targets, address);
        if (highest < 0) highest = ~highest - 1;
        var result = new List<EngineEffectJump>();
        for (var index = highest; index >= 0 && _targets[index] >= lowest; index--)
            result.AddRange(_byTarget[_targets[index]]);
        return result;
    }
}
