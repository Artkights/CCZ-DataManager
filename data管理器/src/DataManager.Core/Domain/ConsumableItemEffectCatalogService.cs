using System.Globalization;

namespace DataManager.Core.Domain;

/// <summary>
/// The item-effect byte has a separate namespace for consumables.  It must not
/// be looked up in the equipment-effect table: in 6.6 the same numeric byte
/// can describe a completely different operation.  Unknown values remain
/// addressable and are deliberately reported as unconfirmed.
/// </summary>
public sealed record ConsumableItemEffectEntry(
    int Id,
    string Name,
    string Description,
    bool Confirmed,
    string Evidence);

public static class ConsumableItemEffectCatalogService
{
    private static readonly IReadOnlyDictionary<int, (string Name, string Description, string Evidence)> Known =
        new Dictionary<int, (string, string, string)>
        {
            [0x1A] = ("恢复生命", "按效果值恢复 HP", "6.5 Star.e5 消耗品样本；6.6 Ekd5 固定名称表"),
            [0x1B] = ("恢复 MP", "按效果值恢复 MP", "6.5 Star.e5 消耗品样本；6.6 Ekd5 固定名称表"),
            [0x1C] = ("治疗混乱", "解除混乱状态", "6.5 Star.e5 消耗品样本"),
            [0x1D] = ("治疗中毒", "解除中毒状态", "6.5 Star.e5 消耗品样本"),
            [0x1E] = ("治疗麻痹", "解除麻痹状态", "6.5 Star.e5 消耗品样本"),
            [0x1F] = ("治疗禁咒", "解除禁咒/封锁状态", "6.5 Star.e5 消耗品样本"),
            [0x20] = ("恢复异常状态", "解除已确认的异常状态", "6.5 Star.e5 样本"),
            [0x21] = ("武力上升", "提升武力", "6.5 Star.e5 样本"),
            [0x22] = ("统率上升", "提升统率", "6.5 Star.e5 样本"),
            [0x23] = ("智力上升", "提升智力", "6.5 Star.e5 样本"),
            [0x24] = ("敏捷上升", "提升敏捷", "6.5 Star.e5 样本"),
            [0x25] = ("运气上升", "提升运气", "6.5 Star.e5 样本"),
            [0x26] = ("增加经验", "按效果值增加经验", "6.5 Star.e5 样本"),
            [0x27] = ("兵种进阶", "印绶/兵种进阶", "6.5 Star.e5 样本；6.6 Ekd5 固定名称表"),
            [0x28] = ("恢复SP", "恢复 SP", "用户确认的消耗品特效映射（2026-10-20）"),
            [0x29] = ("获得功勋", "按效果值增加功勋", "6.5 Star.e5 样本"),
            [0x2A] = ("HP上升", "永久提升 HP", "用户确认的消耗品特效映射（2026-10-20）；6.5 Star.e5 仙桃样本"),
            [0x2B] = ("MP上升", "永久提升 MP", "用户确认的消耗品特效映射（2026-10-20）；6.5 Star.e5 仙酒样本"),
            [0x2C] = ("2C道具号", "使用 0x2C 对应的道具号", "用户指定按道具号显示（2026-10-20）"),
            [0x2D] = ("2D道具号", "使用 0x2D 对应的道具号", "用户指定按道具号显示（2026-10-20）"),
            [0x2E] = ("2E道具号", "使用 0x2E 对应的道具号", "用户指定按道具号显示（2026-10-20）"),
            [0x2F] = ("2F道具号", "使用 0x2F 对应的道具号", "用户指定按道具号显示（2026-10-20）"),
            [0x30] = ("30道具号", "使用 0x30 对应的道具号", "用户指定按道具号显示（2026-10-20）"),
            [0x31] = ("单挑", "单挑类道具效果", "用户确认的消耗品特效映射（2026-10-20）"),
            [0x32] = ("学会策略", "学习策略", "用户确认的消耗品特效映射（2026-10-20）"),
            [0x33] = ("使用策略", "使用指定策略", "用户确认的消耗品特效映射（2026-10-20）"),
            [0x34] = ("附加技能", "附加指定技能", "用户确认的消耗品特效映射（2026-10-20）"),
            [0x35] = ("35道具号", "使用 0x35 对应的道具号", "用户指定按道具号显示（2026-10-20）"),
            [0x36] = ("36道具号", "使用 0x36 对应的道具号", "用户指定按道具号显示（2026-10-20）"),
            [0x37] = ("37道具号", "使用 0x37 对应的道具号", "用户指定按道具号显示（2026-10-20）"),
            [0x38] = ("38道具号", "使用 0x38 对应的道具号", "用户指定按道具号显示（2026-10-20）"),
            [0x39] = ("39道具号", "使用 0x39 对应的道具号", "用户指定按道具号显示（2026-10-20）"),
            [0x3A] = ("任务物品", "任务物品", "用户确认的消耗品特效映射（2026-10-20）"),
            [0x3B] = ("合成材料", "合成材料", "用户确认的消耗品特效映射（2026-10-20）"),
            [0x3C] = ("工具物品", "工具物品", "用户确认的消耗品特效映射（2026-10-20）"),
            [0x3D] = ("装备升级", "提升装备等级或装备能力", "6.5 Star.e5 装备升级书；6.6 Ekd5 固定名称表")
        };

    public static IReadOnlyList<ConsumableItemEffectEntry> GetAll(int minimum = 0x1A, int maximum = 0x3D)
    {
        if (minimum < 0 || maximum > 0xFF || minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        return Enumerable.Range(minimum, maximum - minimum + 1)
            .Select(Resolve)
            .ToArray();
    }

    public static ConsumableItemEffectEntry Resolve(int id)
    {
        if (id is < 0 or > 0xFF) throw new ArgumentOutOfRangeException(nameof(id));
        if (Known.TryGetValue(id, out var value))
            return new(id, value.Name, value.Description, true, value.Evidence);
        return new(id,
            $"未确认道具特效 0x{id:X2}",
            "当前仅确认编号，尚无足够的目标基底消费者证据",
            false,
            "保留原始数字；禁止套用装备特效名称");
    }

    public static string DisplayName(int id) => Resolve(id).Name;

    public static string DisplayId(int id) => $"0x{id.ToString("X2", CultureInfo.InvariantCulture)}：{DisplayName(id)}";
}

/// <summary>Short public name used by business and migration callers.</summary>
public static class ConsumableEffectCatalog
{
    public static ConsumableItemEffectEntry Resolve(int id) => ConsumableItemEffectCatalogService.Resolve(id);
    public static IReadOnlyList<ConsumableItemEffectEntry> GetAll(int minimum = 0x1A, int maximum = 0x3D)
        => ConsumableItemEffectCatalogService.GetAll(minimum, maximum);
    public static string DisplayName(int id) => ConsumableItemEffectCatalogService.DisplayName(id);
}
