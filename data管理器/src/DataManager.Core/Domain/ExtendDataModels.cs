namespace DataManager.Core.Domain;

/// <summary>Each category has its own entity IDs and selection state.</summary>
public enum ExtendDataCategory { Effects, Growth, Strategies, Forge }

public sealed record ExtendDataStatus(
    ExtendDataCategory Category, bool Available, int RowCount, string FilePath, string Message);

public sealed record EffectNamePoolProbeResult(
    bool Success, IReadOnlyDictionary<int, string> Names, string Message,
    string SourceSha256, long? FileOffset = null, ulong? VirtualAddress = null);

public sealed record ExtendEffectSlot(int Slot, int EffectId, int Value, int Experience, int RemainingTurns)
{
    public bool IsEmpty => EffectId == 255;
}

public sealed record ExtendStrategySlot(int Slot, int StrategyId, int UseCount)
{
    public bool IsEmpty => StrategyId == 255;
}

public sealed record ExtendGrowthPreview(bool Enabled, int InitialValue, int GrowthCap,
    int TemporaryTurns, int BookExperience, int AttackExperience, int HitExperience, int KillExperience)
{
    public string Description => Enabled
        ? $"吃书初始值 {InitialValue}，成长上限 {GrowthCap}，临时书持续 {TemporaryTurns} 回合；经验加成：成长书 {BookExperience}、攻击 {AttackExperience}、受击 {HitExperience}、击杀 {KillExperience}。经验达到 200 后每次只结算一次。"
        : "当前特技未启用成长。";
}

public sealed record ExtendForgeSlot(int Slot, int ItemId, int RawAmount, bool IsMaterial)
{
    public bool IsEmpty => (ItemId & 255) == 255;
    public bool IsEquipment => ItemId is >= 0 and <= 254;
    public bool IsConsumable => ItemId is >= 256 and <= 510;
    public bool ExactLevel => IsMaterial && IsEquipment && (RawAmount & 128) != 0;
    public int Amount => IsMaterial && (IsEquipment || IsConsumable) ? RawAmount & 127 : RawAmount;
}
