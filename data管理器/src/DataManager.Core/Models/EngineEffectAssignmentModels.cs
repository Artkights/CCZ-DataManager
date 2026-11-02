using DataManager.Core.Editing;

namespace DataManager.Core.Models;

public enum EngineEffectAssignmentKind { SharedLookup, NativeHp, NativeMp, StaticStat }

public sealed record EffectBindingLocation(string FilePath, long FileOffset, int Width,
    byte[] BeforeBytes, string Encoding, IReadOnlyList<string> SourceIdentities);

public sealed record EffectBindingGroup(string Id, IReadOnlyList<EffectBindingLocation> Locations,
    IReadOnlyList<int> EffectIds);

public sealed record EngineEffectItemEntity(int Id, string Name, int Selector, string Category,
    int? Value, string ValueSource, IReadOnlyList<int> SharedItemIds)
{
    public string Display => $"{Id}：{Name}";
    public long ValueFileOffset { get; init; } = -1;
    public string FilePath { get; init; } = string.Empty;
    public long SelectorFileOffset { get; init; } = -1;
    public long TypeFileOffset { get; init; } = -1;
    public int TypeValue { get; init; }
    public bool NeedsSpecialType { get; init; }
}

public sealed record EngineEffectJobEntity(int Id, string Name, int FamilyId)
{
    public string Display => $"{Id}：{Name}";
}

public sealed record EngineEffectJobSlot(int Slot, int JobId, int Value, long FileOffset,
    string MatchScope, IReadOnlyList<int> AffectedJobIds)
{
    public bool IsFree => JobId == 255;
    public IReadOnlyList<int> PotentialAffectedJobIds { get; init; } = [];
    public long ValueFileOffset { get; init; } = -1;
}

public sealed record EngineEffectAssignmentEffect
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int? PersonalEffectId { get; init; }
    public EngineEffectAssignmentKind Kind { get; init; }
    public IReadOnlyList<EngineEffectCallSite> BindingPoints { get; init; } = [];
    public EffectBindingGroup? BindingGroup { get; init; }
    public bool ItemPoolVisible { get; init; } = true;
    public bool JobPoolVisible { get; init; } = true;
    public string ImplementationStatus { get; init; } = "实现未解析";
    public bool SharedJobValue { get; init; }
    public IReadOnlyList<int> SharedPersonIds { get; init; } = [];
    public string ItemReadOnlyReason { get; init; } = string.Empty;
    public string JobReadOnlyReason { get; init; } = string.Empty;
    public IReadOnlyList<int> ItemSelectors { get; init; } = [];
    public IReadOnlyList<int> OwnerItemIds { get; init; } = [];
    public string OwnerSummary { get; init; } = string.Empty;
    public IReadOnlyList<EngineEffectJobSlot> JobSlots { get; init; } = [];
    public bool CanBindItem { get; init; }
    public bool CanBindJob { get; init; }
    public string ReadOnlyReason { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Conditions { get; init; } = string.Empty;
    public string ValueSource { get; init; } = string.Empty;
    public int FreeJobSlots => JobSlots.Count(slot => slot.IsFree);
    public string JobPoolDisplay => JobSlots.Count > 0 ? $"{Name} *{FreeJobSlots}" : Name;
    public bool IsJobPoolFull => JobSlots.Count > 0 && FreeJobSlots == 0;
}

public sealed record EngineEffectAssignmentCatalog
{
    public string ExePath { get; init; } = string.Empty;
    public string ExeSha256 { get; init; } = string.Empty;
    public IReadOnlyList<EngineEffectItemEntity> Items { get; init; } = [];
    public IReadOnlyList<EngineEffectJobEntity> Jobs { get; init; } = [];
    public IReadOnlyList<EngineEffectAssignmentEffect> Effects { get; init; } = [];
    public IReadOnlyList<EngineEffectAssignmentEffect> Pool => Effects;
    public IReadOnlyList<int> KnownItemSelectors { get; init; } = [];
    public bool JobLayoutVerified { get; init; }
    public EffectAssignmentLayout? JobLayout { get; init; }
    public string JobLayoutEvidence { get; init; } = string.Empty;
    public string JobMatchMode { get; init; } = string.Empty;
    public bool NativeSupported { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

/// <summary>Edits stay here until an explicit preview/commit. Reading never mutates the workspace.</summary>
public sealed class EngineEffectAssignmentDraft
{
    public EngineEffectAssignmentCatalog Catalog { get; }
    public Dictionary<string, int> ItemTargets { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, EngineEffectJobSlot[]> JobTargets { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, int> ItemValues { get; } = [];
    public Dictionary<int, int> ItemSelectorTargets { get; } = [];
    public HashSet<int> SpecialItemIds { get; } = [];
    public bool HasPendingChanges => ItemTargets.Count > 0 || JobTargets.Count > 0 || ItemValues.Count > 0 || ItemSelectorTargets.Count > 0 || SpecialItemIds.Count > 0;
    public EngineEffectAssignmentDraft(EngineEffectAssignmentCatalog catalog) => Catalog = catalog;
    public void Clear() { ItemTargets.Clear(); JobTargets.Clear(); ItemValues.Clear(); ItemSelectorTargets.Clear(); SpecialItemIds.Clear(); }
}

public sealed record EngineEffectAssignmentStageResult(bool Success, bool NeedsReplacement,
    string Message, IReadOnlyList<EngineEffectJobSlot> ExistingSlots)
{
    public static EngineEffectAssignmentStageResult Done(string message = "已加入草稿。") => new(true, false, message, []);
    public static EngineEffectAssignmentStageResult Failed(string message) => new(false, false, message, []);
}

public sealed record EngineEffectAssignmentPreview
{
    public IReadOnlyList<WorkspaceBinaryCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Impact { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
    public bool CanWrite => Errors.Count == 0 && Candidates.Count > 0;
    public IReadOnlyDictionary<string, int> ExpectedItemTargets { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, EngineEffectJobSlot[]> ExpectedJobTargets { get; init; } = new Dictionary<string, EngineEffectJobSlot[]>();
    public IReadOnlyDictionary<int, int> ExpectedItemValues { get; init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, int> ExpectedItemSelectors { get; init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, int> ExpectedItemTypes { get; init; } = new Dictionary<int, int>();
}
