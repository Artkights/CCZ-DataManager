﻿using DataManager.Core.Binary;
using DataManager.Core.Editing;

namespace DataManager.Core.Models;

public sealed class EngineEffectWriteRequest
{
    public int EffectId { get; init; }
    public string? NewName { get; init; }
    public byte? NewSuffix { get; init; }
    public string? NewDescription { get; init; }
    public IReadOnlyDictionary<uint, EngineEffectCallEdit> NewCalls { get; init; } =
        new Dictionary<uint, EngineEffectCallEdit>();
    // A physical call can expand to several table/wrapper/loop instances.
    public IReadOnlyList<EngineEffectCallEdit> CallEdits { get; init; } = [];
    // Only a coordinating transaction may defer this installation: its other
    // candidate must then supply the same equipment-channel FF guard.
    public bool DeferEquipmentFfGuard { get; init; }
}

public sealed record EngineEffectCallEdit
{
    public EngineEffectCallIdentity? Identity { get; init; }
    public uint CallAddress { get; init; }
    public int? TableIndex { get; init; }
    public int? ValueMode { get; init; }
    public int? StackingMode { get; init; }
    public int? ItemEffectId { get; init; }
    public int? PersonalEffectId { get; init; }
    public uint? EntryTarget { get; init; }
}

public sealed class EngineEffectWriteChange
{
    public int EffectId { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public long FileOffset { get; init; }
    public byte[] BeforeBytes { get; init; } = [];
    public byte[] AfterBytes { get; init; } = [];
    public EngineEffectWriteChangeKind Kind { get; init; }
    public uint? ExpectedValue { get; init; }
    public uint? ExpectedTarget { get; init; }
    public string? ExpectedName { get; init; }
    public int? ExpectedSuffix { get; init; }
    public string? ExpectedDescription { get; init; }
}

public enum EngineEffectWriteChangeKind
{
    NameSlot,
    Description,
    Push,
    Entry,
    NativeAdapter
}

public sealed class EngineEffectWritePreview
{
    public int EffectId { get; init; }
    public int? FollowEffectId { get; init; }
    public IReadOnlyList<EngineEffectWriteChange> Changes { get; init; } = [];
    public IReadOnlyList<WorkspaceBinaryCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool CanWrite { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class EngineEffectWriteResult
{
    public EngineEffectScanReport? VerifiedReport { get; set; }
    public bool Success { get; init; }
    public int AppliedChanges { get; init; }
    public int? FollowEffectId { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? BackupPath { get; init; }
    public HistoryEntry? History { get; init; }
}

public sealed record EngineEffectNameSlot(
    int EffectId,
    string FilePath,
    long FileOffset,
    byte[] RawBytes,
    string Name,
    byte SuffixByte,
    int Capacity,
    TextFieldConstraint Constraint,
    bool Exists,
    string? Error = null);

public sealed record EngineEffectDescriptionSlot(
    int EffectId,
    string FilePath,
    long FileOffset,
    byte[] RawBytes,
    string Text,
    int ByteCount,
    TextFieldConstraint Constraint,
    bool Exists,
    string? Error = null);

public sealed record EngineEffectSlotEdit(
    string FilePath,
    long FileOffset,
    byte[] BeforeBytes,
    byte[] AfterBytes,
    string FieldName,
    string? ValidationError = null)
{
    public bool IsChanged => !BeforeBytes.AsSpan().SequenceEqual(AfterBytes);
}
