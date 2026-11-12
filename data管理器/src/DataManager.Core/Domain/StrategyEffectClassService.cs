using System.Globalization;
using System.Runtime.CompilerServices;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Core.Domain;

public static class StrategyEffectClassService
{
    public const string TableKey = "strategy-effect-class";
    public const string FieldName = "效果类";
    private static readonly ConditionalWeakTable<byte[], StrategyEffectClassProbe.Layout> LayoutCache = new();

    // Confidence is deliberately internal metadata, not text added to a user's dropdown.
    internal sealed record Effect(int Id, string Name, string Family, bool MediumConfidence = false);
    private static readonly Effect[] Effects =
    [new(0,"四系伤害","damage"),new(1,"全场伤害","damage"),new(2,"控制敌军","debuff",true),new(3,"吸取MP","damage"),
     new(4,"爆发下降","debuff"),new(5,"士气下降","debuff"),new(6,"攻击或精神下降","debuff"),new(7,"防御下降","debuff"),
     new(8,"混乱","status"),new(9,"中毒","status"),new(10,"定身","status"),new(11,"封咒","status"),
     new(12,"八阵图","composite"),new(13,"恢复HP","recovery"),new(14,"恢复MP","recovery"),new(15,"恢复状态","recovery"),
     new(16,"移动力上升","buff"),new(17,"爆发上升","buff"),new(18,"士气上升","buff"),new(19,"攻击或精神上升","buff"),new(20,"防御上升","buff"),
     new(21,"再行动","action"),new(22,"天气变化","weather"),new(23,"气合","self"),new(24,"冥想","self"),new(25,"霸气","self"),
     new(26,"青龙","composite",true),new(27,"地形修改","terrain",true),new(28,"玄武","composite"),new(29,"白虎","composite"),
     new(30,"全能力下降","debuff"),new(31,"疾行","movement",true),new(32,"诱敌","debuff",true),new(33,"经验功勋加倍","buff",true),
     new(34,"连击","special",true),new(35,"瞬移","movement"),new(36,"召唤","summon"),new(37,"换位","movement"),
     new(38,"号令","special",true),new(39,"持续或被动状态","buff",true),new(40,"自身强化或嘲讽","self",true),new(41,"地形变火","terrain",true)];

    private static readonly IReadOnlyDictionary<int, Effect> ShanzeiEffects = new Dictionary<int, Effect>
    {
        [0x02] = new(0x02,"特殊减益","debuff",true),
        [0x03] = new(0x03,"吸取或侦察","damage",true),
        [0x06] = new(0x06,"能力下降","debuff"),
        [0x13] = new(0x13,"能力上升","buff"),
        [0x1A] = new(0x1A,"合体或青龙","composite",true),
        [0x1B] = new(0x1B,"地形修改（非火）","terrain",true),
        [0x1E] = new(0x1E,"特殊减益","debuff",true),
        [0x20] = new(0x20,"特殊减益","debuff",true),
        [0x21] = new(0x21,"自身强化","self",true),
        [0x22] = new(0x22,"凤求凰特殊","special",true),
        [0x27] = new(0x27,"持续或被动状态","passive",true)
    };

    public static TableDefinition BuildTable(byte[] executable)
    {
        var layout = Inspect(executable);
        return new(TableKey, "策略效果类", "Ekd5.exe", layout.Verified ? layout.Offset : 0, 144, 1, 0,
            [new(FieldName, 0, 1, FieldKind.UInt8)], "策略", ParentKey: "strategies",
            VerificationLevel: layout.Verified ? VerificationLevel.SourceReviewed : VerificationLevel.InheritedUnverified,
            Capability: layout.Verified ? TableCapability.Editable : TableCapability.Unavailable, CapabilityEvidence: layout.Evidence);
    }

    public static IReadOnlyList<IdNameOption> GetOptions(ProjectWorkspace workspace, int rowIndex)
    {
        if (!workspace.Documents.TryGetValue(TableKey, out var document) || rowIndex < 0 || rowIndex >= document.Data.Rows.Count) return [];
        var layout = Inspect(document.OriginalFileBytes);
        var current = Convert.ToInt32(document.Data.Rows[rowIndex][FieldName], CultureInfo.InvariantCulture);
        var result = layout.Verified ? Effects.Where(effect => effect.Id <= layout.MaximumClass)
            .Select(effect => new IdNameOption(effect.Id, EffectFor(layout, effect.Id).Name)).ToList() : [];
        if (result.All(option => option.Id != current))
            result.Add(new(current, GetName(layout, current)));
        return result.OrderBy(option => option.Id).ToArray();
    }

    public static void ValidateValue(ProjectWorkspace workspace, TableDocument document, int rowIndex, object? value)
    {
        var candidate = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        var original = Convert.ToInt32(document.GetOriginal(rowIndex, FieldName), CultureInfo.InvariantCulture);
        if (candidate == original) return;
        var layout = Inspect(document.OriginalFileBytes);
        if (!layout.Verified || document.Definition.DataOffset != layout.Offset || document.Definition.RowSize != 1 || document.Definition.RowCount != 144)
            throw new InvalidOperationException("策略效果类缺少已验证的分派与处理器布局。");
        if (candidate < 0 || candidate > layout.MaximumClass)
            throw new InvalidOperationException("只能选择本引擎已验证的内置效果类；保留值只允许维持原值。");
    }

    public static string? GetCompatibilityWarning(ProjectWorkspace workspace, int rowIndex, int newClass,
        IReadOnlyList<CompositeCellEdit>? pendingEdits = null)
    {
        if (!workspace.Documents.TryGetValue(TableKey, out var effects) || rowIndex < 0 || rowIndex >= effects.Data.Rows.Count) return null;
        var oldClass = Convert.ToInt32(effects.Data.Rows[rowIndex][FieldName], CultureInfo.InvariantCulture);
        if (newClass == oldClass) return null;
        var layout = Inspect(effects.OriginalFileBytes);
        var oldFamily = Family(layout, oldClass);
        var newFamily = Family(layout, newClass);
        var conflicts = new List<string>();
        if (oldFamily != newFamily) conflicts.Add("效果类别不同");
        if (workspace.Documents.TryGetValue("strategies", out var strategies) && rowIndex < strategies.Data.Rows.Count)
        {
            int Read(string field)
            {
                var pending = pendingEdits?.LastOrDefault(edit => edit.TableKey == "strategies" && edit.RowIndex == rowIndex && edit.FieldName == field);
                return Convert.ToInt32(pending?.NewValue ?? strategies.Data.Rows[rowIndex][field], CultureInfo.InvariantCulture);
            }
            var target = Read("施展对象");
            var targetMismatch = newClass switch
            {
                0x16 => target != 2,
                0x1D => target != 1,
                0x1A or 0x1C => target != 0,
                _ => newFamily is "damage" or "debuff" or "status" && target != 0 ||
                     newFamily is "recovery" or "buff" or "self" && target != 1
            };
            if (targetMismatch)
                conflicts.Add("施展对象可能不匹配");
            var range = Read("施法范围");
            var penetration = Read("穿透范围");
            if (range is < 0 or > 255 || penetration is < 0 or > 255 ||
                newFamily is "weather" or "self" && penetration != 0)
                conflicts.Add("范围可能不匹配");
        }
        return conflicts.Count == 0 ? null : $"策略ID {effects.Definition.BeginId + rowIndex}：{string.Join("、", conflicts)}。";
    }

    public static void ValidateCurrentExecutable(ProjectWorkspace workspace)
    {
        if (!workspace.Documents.TryGetValue(TableKey, out var document) || !document.IsDirty) return;
        var current = StrategyEffectClassProbe.Inspect(File.ReadAllBytes(document.FilePath));
        var original = Inspect(document.OriginalFileBytes);
        if (!current.Verified || current.Offset != document.Definition.DataOffset || current.HandlerFamily != original.HandlerFamily)
            throw new InvalidDataException("Ekd5.exe的策略效果分派或处理器在打开后发生变化，请重新打开项目。");
    }

    private static StrategyEffectClassProbe.Layout Inspect(byte[] executable)
        => LayoutCache.GetValue(executable, StrategyEffectClassProbe.Inspect);

    private static string GetName(StrategyEffectClassProbe.Layout layout, int id)
        => layout.Verified && id >= 0 && id <= layout.MaximumClass ? EffectFor(layout, id).Name
            : layout.HandlerFamily == "legacy" && id is >= 0x25 and <= 0x2F || layout.HandlerFamily == "shanzei66" && id is >= 0x2A and <= 0x2F
                ? "默认伤害" : "未识别效果";
    private static string Family(StrategyEffectClassProbe.Layout layout, int id)
        => layout.Verified && id >= 0 && id <= layout.MaximumClass ? EffectFor(layout, id).Family
            : layout.HandlerFamily == "legacy" && id is >= 0x25 and <= 0x2F || layout.HandlerFamily == "shanzei66" && id is >= 0x2A and <= 0x2F
                ? "damage" : "unknown";
    private static Effect EffectFor(StrategyEffectClassProbe.Layout layout, int id)
        => layout.HandlerFamily == "shanzei66" && ShanzeiEffects.TryGetValue(id, out var effect) ? effect : Effects[id];
}
