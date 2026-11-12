namespace TransformManager.App;

public enum ItemCategory { Weapon, Armor, Accessory, Consumable, Unknown }

public sealed record RuleStage(int Level, int Value);

public sealed record EffectRule(int EffectId, List<RuleStage> Stages);

public sealed record PreviewRow(int Level, int Value, string Source);

public enum ExtraMode { Auto = 0, ForceScale = 1, SkipScale = 2 }

public sealed class EffectSlot
{
    public required int Id { get; init; }
    public string Name { get; init; } = "";
    public byte Coverage { get; init; }
    public byte Extra { get; init; }
    public IReadOnlyList<ItemRef> Refs { get; init; } = Array.Empty<ItemRef>();
    public List<RuleStage> Stages { get; set; } = new();

    public string Display => Name.Length > 0
        ? $"0x{Id:X2}  {Name}  （{Refs.Count} 件）"
        : $"0x{Id:X2}  （未命名）  （{Refs.Count} 件）";
}

public sealed record SaveOutcome(bool Success, string Message, string? BackupPath);
