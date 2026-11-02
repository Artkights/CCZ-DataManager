using System.Buffers.Binary;

namespace DataManager.Core.Models;

/// <summary>Storage geometry recovered from the current engine's allocation reader and writer.</summary>
public sealed record EffectAssignmentLayout
{
    public bool Verified { get; init; }
    public long TableOffset { get; init; } = -1;
    public uint TableAddress { get; init; }
    public uint PointerAddress { get; init; }
    public int RowCount { get; init; }
    public int RowSize { get; init; }
    public int SlotCount => JobIdOffsets.Count;
    public IReadOnlyList<int> JobIdOffsets { get; init; } = [];
    public IReadOnlyList<int> JobValueOffsets { get; init; } = [];
    public IReadOnlyList<int> PersonIdOffsets { get; init; } = [];
    public IReadOnlyList<int> PersonValueOffsets { get; init; } = [];
    public bool SharedValue { get; init; }
    public bool ExactMode { get; init; }
    public string Evidence { get; init; } = string.Empty;

    public long GetRowOffset(int effectId)
    {
        if (!Verified) throw new InvalidOperationException("当前引擎的特效分配存储尚未定位。");
        ArgumentOutOfRangeException.ThrowIfNegative(effectId);
        if (effectId >= RowCount) throw new ArgumentOutOfRangeException(nameof(effectId));
        return checked(TableOffset + (long)effectId * RowSize);
    }

    public long GetJobIdOffset(int effectId, int slot) => GetFieldOffset(effectId, slot, JobIdOffsets);
    public long GetJobValueOffset(int effectId, int slot) => GetFieldOffset(effectId, slot, JobValueOffsets);
    public long GetPersonIdOffset(int effectId, int slot) => GetFieldOffset(effectId, slot, PersonIdOffsets);
    public long GetPersonValueOffset(int effectId, int slot) => GetFieldOffset(effectId, slot, PersonValueOffsets);

    /// <summary>Returns raw UInt16 IDs, retaining opaque empty values such as 0x400+effectId.</summary>
    public IReadOnlyList<int> ReadPersonIds(byte[] image, int effectId)
    {
        ArgumentNullException.ThrowIfNull(image);
        var row = GetRowOffset(effectId);
        if (row < 0 || row > image.LongLength - RowSize)
            throw new InvalidDataException("特效分配行超出当前文件范围。");
        return PersonIdOffsets.Select(offset => (int)BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan(checked((int)(row + offset)), 2))).ToArray();
    }

    private long GetFieldOffset(int effectId, int slot, IReadOnlyList<int> offsets)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        if (slot >= offsets.Count) throw new ArgumentOutOfRangeException(nameof(slot));
        return checked(GetRowOffset(effectId) + offsets[slot]);
    }
}
