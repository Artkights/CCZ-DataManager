namespace DataManager.Core.Models;

/// <summary>One person slot in Effect.e5 that assigns the current effect id.</summary>
public sealed record EngineEffectPersonReference(
    int PersonId,
    string PersonName,
    int Slot,
    int Value,
    int Experience,
    int RemainingTurns)
{
    public string SlotDisplay => $"槽 {Slot}：效果值 {Value}，经验 {Experience}，剩余回合 {RemainingTurns}";
}

/// <summary>Effectmsg.e5 growth row for one effect id.</summary>
public sealed record EngineEffectGrowthSettings(
    bool Available,
    string FileName,
    bool Enabled,
    int InitialValue,
    int GrowthLimit,
    int TemporaryTurns,
    int GrowthBookExperience,
    int AttackExperience,
    int HitExperience,
    int KillExperience,
    bool IsWritable,
    string Evidence)
{
    public static EngineEffectGrowthSettings Missing(string evidence)
        => new(false, string.Empty, false, 0, 0, 0, 0, 0, 0, 0, false, evidence);

    public string Summary => !Available
        ? "成长拓展：不可用（" + Evidence + "）"
        : "成长拓展 " + FileName + "：" + (Enabled ? "已启用" : "未启用") +
          $"；初始值 {InitialValue}，上限 {GrowthLimit}，临时书 {TemporaryTurns} 回合，" +
          $"成长书经验 {GrowthBookExperience}，攻击 {AttackExperience}，受击 {HitExperience}，击杀 {KillExperience}" +
          (IsWritable ? "（可在拓展数据页写入）" : "（当前布局只读）");
}

public sealed class EngineEffectReferenceSummary
{
    public IReadOnlyList<EngineEffectPersonReference> PersonAssignments { get; init; } = [];
    public EngineEffectGrowthSettings Growth { get; init; } =
        EngineEffectGrowthSettings.Missing("尚未扫描");
}
