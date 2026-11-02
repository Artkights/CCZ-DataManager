using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Scanner;

/// <summary>Entity views over proven binding locations. Item IDs and effect selectors are distinct.</summary>
public sealed partial class EngineEffectAssignmentService
{
    public EngineEffectAssignmentCatalog Read(ProjectWorkspace workspace, EngineEffectScanReport report)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(report);
        var bytes = File.ReadAllBytes(report.ExePath);
        if (!HashService.Sha256(bytes).Equals(report.ExeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("引擎文件已变化，请重新扫描。");
        workspace = CurrentAssignmentWorkspace(workspace);
        var image = EngineEffectBinaryImage.Read(bytes);
        var diagnostics = new List<string>();
        var native = NativeEffectAdapter.Read(bytes);
        var items = ReadItems(workspace);
        var jobs = new BusinessDataService(workspace).LoadDetailedJobs().Records.Where(row => row.Id < 80)
            .Select(row => new EngineEffectJobEntity(row.Id, row.Name, Family(row.Id))).ToArray();
        var layout = ProbeJobLayout(image);
        if (!layout.Verified) diagnostics.Add(layout.Evidence);
        var effects = BuildBindingEffects(report, bytes, layout, items, jobs);
        if (native.IsSupported)
        {
            effects.Add(NativeRow("native:hp", "提高HP", EngineEffectAssignmentKind.NativeHp, native.HpCurrentSelector, items));
            effects.Add(NativeRow("native:mp", "提高MP", EngineEffectAssignmentKind.NativeMp, native.MpCurrentSelector, items));
        }
        else diagnostics.Add("原生 HP/MP 绑定适配器未通过当前引擎结构校验；未开放改绑。");
        return new EngineEffectAssignmentCatalog
        {
            ExePath = report.ExePath, ExeSha256 = report.ExeSha256, Items = items, Jobs = jobs, Effects = effects,
            JobLayoutVerified = layout.Verified, JobLayout = layout, JobLayoutEvidence = layout.Evidence,
            JobMatchMode = layout.ExactMode ? "当前文件初始化为精确兵种；运行时开关可改为同族继承。" :
                "当前文件初始化为同族且分配ID≤实际ID；运行时开关可改为精确兵种。",
            NativeSupported = native.IsSupported, Diagnostics = diagnostics,
            KnownItemSelectors = report.Effects.SelectMany(row => row.CallSiteDetails.Concat(row.DerivedCallDetails)
                    .Concat(row.TableCallDetails).Concat(row.LoopCallDetails))
                .Concat(report.UnassignedCalls).Concat(report.DerivedCalls).Concat(report.TableCalls).Concat(report.LoopCalls)
                .Where(call => call.Pattern != EngineEffectCallPattern.EngineInternal && call.ItemEffectId != null)
                .Select(call => call.ItemEffectId!.Value).Concat(effects.SelectMany(effect => effect.ItemSelectors))
                .Distinct().Order().ToArray()
        };
    }

    public EngineEffectAssignmentDraft CreateDraft(EngineEffectAssignmentCatalog catalog) => new(catalog);

    public IReadOnlyList<EngineEffectAssignmentEffect> GetEffects(EngineEffectAssignmentDraft draft)
        => draft.Catalog.Effects.Select(effect => effect with
        {
            ItemSelectors = draft.ItemTargets.TryGetValue(effect.Key, out var target) ? [target] : effect.ItemSelectors,
            JobSlots = draft.JobTargets.TryGetValue(effect.Key, out var slots) ? slots : effect.JobSlots
        }).ToArray();

    public IReadOnlyList<EngineEffectAssignmentEffect> GetItemAssignments(EngineEffectAssignmentDraft draft, int itemId)
    {
        var item = draft.Catalog.Items.FirstOrDefault(item => item.Id == itemId);
        var selector = item == null ? 255 : draft.ItemSelectorTargets.GetValueOrDefault(itemId, item.Selector);
        return item == null || selector == 255 || item.SelectorFileOffset < 0 ||
            item.NeedsSpecialType && !draft.SpecialItemIds.Contains(itemId) ? [] :
            GetEffects(draft).Where(effect => effect.ItemPoolVisible && effect.ItemSelectors.Contains(selector)).ToArray();
    }

    public IReadOnlyList<EngineEffectAssignmentEffect> GetJobAssignments(EngineEffectAssignmentDraft draft, int jobId)
        => GetEffects(draft).Where(effect => effect.JobSlots.Any(slot => !slot.IsFree &&
            (slot.JobId == jobId || slot.AffectedJobIds.Contains(jobId)))).ToArray();

    public EngineEffectAssignmentStageResult StageItemBind(EngineEffectAssignmentDraft draft, string effectKey, int itemId)
    {
        var item = draft.Catalog.Items.FirstOrDefault(item => item.Id == itemId);
        if (item == null) return EngineEffectAssignmentStageResult.Failed("当前宝物不存在。");
        var effect = draft.Catalog.Effects.FirstOrDefault(effect => effect.Key == effectKey);
        if (effect?.CanBindItem != true) return EngineEffectAssignmentStageResult.Failed(effect?.ReadOnlyReason ?? "未找到特效。");
        if (item.SelectorFileOffset < 0) return EngineEffectAssignmentStageResult.Failed("该物品没有已验证的装备selector字段。");
        if (effect.Kind == EngineEffectAssignmentKind.StaticStat)
        {
            if (effect.PersonalEffectId is not { } staticId) return EngineEffectAssignmentStageResult.Failed("静态数值特效缺少特效号。");
            if (staticId is < 0 or > 254) return EngineEffectAssignmentStageResult.Failed("特效号超出 0..254。");
            if (staticId < 26 && item.Category != "辅助装备")
                return EngineEffectAssignmentStageResult.Failed("低于 26 的特效号只写入辅助装备的装备号字段，避免覆盖武器/防具类型标记。");
            draft.ItemTargets.Remove(effectKey);
            if (item.Selector == staticId) draft.ItemSelectorTargets.Remove(itemId);
            else draft.ItemSelectorTargets[itemId] = staticId;
            return EngineEffectAssignmentStageResult.Done(
                $"静态数值特效按宝物装备号生效：{item.Name} 装备号 {item.Selector} → {staticId}。");
        }
        var selector = draft.ItemSelectorTargets.GetValueOrDefault(itemId, item.Selector);
        if (selector == 255 || item.NeedsSpecialType && !draft.ItemSelectorTargets.ContainsKey(itemId))
        {
            var used = draft.Catalog.Items.Select(entity => entity.Selector)
                .Concat(draft.Catalog.KnownItemSelectors)
                .Concat(draft.Catalog.Effects.SelectMany(row => row.ItemSelectors))
                .Concat(draft.ItemSelectorTargets.Values).Concat(draft.ItemTargets.Values).ToHashSet();
            selector = Enumerable.Range(0x80, 0x7E).FirstOrDefault(candidate => !used.Contains(candidate), -1);
            if (selector < 0) return EngineEffectAssignmentStageResult.Failed("80..FD范围没有未占用合法装备号。");
            draft.ItemSelectorTargets[itemId] = selector;
        }
        if (item.NeedsSpecialType) draft.SpecialItemIds.Add(itemId);
        return StageItemTarget(draft, effectKey, selector);
    }

    public EngineEffectAssignmentStageResult StageItemUnbind(EngineEffectAssignmentDraft draft, string effectKey)
    {
        var effect = draft.Catalog.Effects.FirstOrDefault(effect => effect.Key == effectKey);
        if (effect?.Kind == EngineEffectAssignmentKind.StaticStat)
        {
            draft.ItemTargets.Remove(effectKey);
            return EngineEffectAssignmentStageResult.Done(
                "静态数值特效按宝物装备号生效；解绑请把对应宝物的装备号改成其他值，本次不写任何字节。");
        }
        return StageItemTarget(draft, effectKey, 255);
    }

    private static EngineEffectAssignmentStageResult StageItemTarget(EngineEffectAssignmentDraft draft, string key, int target)
    {
        var effect = draft.Catalog.Effects.FirstOrDefault(effect => effect.Key == key);
        if (effect == null || !effect.CanBindItem)
            return EngineEffectAssignmentStageResult.Failed(effect?.ReadOnlyReason ?? "没有找到特效。");
        foreach (var related in draft.Catalog.Effects.Where(candidate => candidate.Key == key ||
                     effect.BindingGroup != null && candidate.BindingGroup?.Id == effect.BindingGroup.Id))
        {
            if (related.ItemSelectors.Count == 1 && related.ItemSelectors[0] == target) draft.ItemTargets.Remove(related.Key);
            else draft.ItemTargets[related.Key] = target;
        }
        return EngineEffectAssignmentStageResult.Done();
    }

    public EngineEffectAssignmentStageResult StageItemValue(EngineEffectAssignmentDraft draft, int itemId, int value)
    {
        var item = draft.Catalog.Items.FirstOrDefault(item => item.Id == itemId);
        if (value is < 0 or > 255 || item?.ValueFileOffset is not >= 0)
            return EngineEffectAssignmentStageResult.Failed("该宝物没有已证明可写的单字节特殊效果值，或值超出0..255。");
        if (item.Value == value) draft.ItemValues.Remove(itemId); else draft.ItemValues[itemId] = value;
        return EngineEffectAssignmentStageResult.Done("已修改该宝物共享的真实特殊效果值，所有读取它的效果均受影响。");
    }

    public EngineEffectAssignmentStageResult StageJobBind(EngineEffectAssignmentDraft draft, string key, int jobId,
        int value, int? replaceSlot = null)
    {
        var effect = GetEffects(draft).FirstOrDefault(effect => effect.Key == key);
        if (effect == null || !effect.CanBindJob) return EngineEffectAssignmentStageResult.Failed("当前特效未验证真实兵种槽布局。");
        if (!draft.Catalog.Jobs.Any(job => job.Id == jobId) || value is < 0 or > 255)
            return EngineEffectAssignmentStageResult.Failed("兵种ID或真实槽值无效。");
        var slots = effect.JobSlots.ToArray();
        var existing = Array.FindIndex(slots, slot => slot.JobId == jobId);
        var free = Array.FindIndex(slots, slot => slot.IsFree);
        var index = existing >= 0 ? existing : free;
        if (index < 0)
        {
            if (replaceSlot is not { } replacement || replacement < 0 || replacement >= slots.Length)
                return new(false, true, "分配槽均已占用，请选择替换现有槽，或取消。", slots);
            index = replaceSlot.Value;
        }
        slots[index] = MakeSlot(index, jobId, value, slots[index].FileOffset, draft.Catalog.Jobs,
            draft.Catalog.JobMatchMode.StartsWith("当前文件初始化为精确", StringComparison.Ordinal)) with { ValueFileOffset = slots[index].ValueFileOffset };
        StoreSlots(draft, key, slots);
        return EngineEffectAssignmentStageResult.Done();
    }

    public EngineEffectAssignmentStageResult StageJobUnbind(EngineEffectAssignmentDraft draft, string key, int slot)
    {
        var effect = GetEffects(draft).FirstOrDefault(effect => effect.Key == key);
        if (effect == null || !effect.CanBindJob || slot < 0 || slot >= effect.JobSlots.Count)
            return EngineEffectAssignmentStageResult.Failed("没有可卸下的真实槽。");
        var slots = effect.JobSlots.ToArray();
        // Free only the job ID; both shared and independent N retain their original value.
        slots[slot] = slots[slot] with { JobId = 255, AffectedJobIds = [], MatchScope = "空槽" };
        StoreSlots(draft, key, slots);
        return EngineEffectAssignmentStageResult.Done();
    }

    public EngineEffectAssignmentStageResult StageJobValue(EngineEffectAssignmentDraft draft, string key, int slot, int value)
    {
        var effect = GetEffects(draft).FirstOrDefault(effect => effect.Key == key);
        if (effect == null || !effect.CanBindJob || slot < 0 || slot >= effect.JobSlots.Count || value is < 0 or > 255)
            return EngineEffectAssignmentStageResult.Failed("真实槽或值无效。");
        var slots = effect.JobSlots.ToArray(); slots[slot] = slots[slot] with { Value = value };
        StoreSlots(draft, key, slots);
        return EngineEffectAssignmentStageResult.Done();
    }

    private static void StoreSlots(EngineEffectAssignmentDraft draft, string key, EngineEffectJobSlot[] slots)
    {
        var original = draft.Catalog.Effects.Single(effect => effect.Key == key).JobSlots;
        if (slots.Select(slot => (slot.JobId, slot.Value)).SequenceEqual(original.Select(slot => (slot.JobId, slot.Value))))
            draft.JobTargets.Remove(key);
        else draft.JobTargets[key] = slots;
    }

    private static EngineEffectAssignmentEffect NativeRow(string key, string name, EngineEffectAssignmentKind kind, int selector,
        IReadOnlyList<EngineEffectItemEntity> items)
        => new()
        {
            Key = key, Name = name, Kind = kind, ItemSelectors = [selector], CanBindItem = true,
            OwnerItemIds = items.Where(item => item.SelectorFileOffset >= 0 && !item.NeedsSpecialType &&
                selector != 255 && item.Selector == selector).Select(item => item.Id).ToArray(),
            OwnerSummary = string.Join("、", items.Where(item => item.SelectorFileOffset >= 0 && !item.NeedsSpecialType &&
                selector != 255 && item.Selector == selector).Select(item => $"{item.Id}:{item.Name}")),
            Source = "原生装备属性汇总 00407C29；武器/防具+辅助装备双路径适配",
            Description = $"按装备真实特殊效果值及等级计算{name[2..]}增量；模式表决定百分比/直接值。",
            ValueSource = "读取当前物品真实特殊效果值，沿原生等级计算及452825换算；HP/MP绑定独立，可能共享同一个N。",
            Conditions = "保留类别门槛、装备槽循环及未修改的原分类；本适配仅改变HP/MP是否匹配。"
        };

    private static IReadOnlyList<EngineEffectItemEntity> ReadItems(ProjectWorkspace workspace)
    {
        var result = new List<EngineEffectItemEntity>();
        foreach (var key in new[] { "items-low", "items-high", "items-extended" })
        {
            if (!workspace.Documents.TryGetValue(key, out var document)) continue;
            for (var index = 0; index < document.Data.Rows.Count; index++)
            {
                var row = document.Data.Rows[index];
                var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
                var semantic = ItemSemanticResolver.Resolve(document, index);
                var relative = semantic.Semantics.Layout.EffectValueOffset;
                var offset = relative is { } position ? document.Definition.DataOffset + (long)index * document.Definition.RowSize + position : -1;
                var ordinaryEquipment = semantic.Semantics.Category is ItemCategory.Weapon or ItemCategory.Armor;
                var supported = document.Definition.RowSize == 25 && semantic.Semantics.Category is
                    ItemCategory.Weapon or ItemCategory.Armor or ItemCategory.Accessory;
                var rowOffset = document.Definition.DataOffset + (long)index * document.Definition.RowSize;
                result.Add(new(id, Convert.ToString(row["名称"], CultureInfo.InvariantCulture) ?? string.Empty,
                    semantic.ActualEffectNumber, semantic.Semantics.CategoryName, semantic.EffectValue,
                    relative is { } valueOffset ? $"{Path.GetFileName(document.FilePath)} 第{id}行 +0x{valueOffset:X2}；等级计算/开关路径另见消费者证据" : "未确认真实值来源", [])
                {
                    ValueFileOffset = offset, FilePath = document.FilePath,
                    SelectorFileOffset = supported ? rowOffset + (ordinaryEquipment ? 0x12 : 0x11) : -1,
                    TypeFileOffset = supported && ordinaryEquipment ? rowOffset + 0x11 : -1,
                    TypeValue = semantic.TypeValue,
                    NeedsSpecialType = supported && ordinaryEquipment && semantic.TypeValue is >= 0 and <= 25 && semantic.TypeValue % 2 == 0
                });
            }
        }
        return result.OrderBy(item => item.Id).Select(item => item with
        { SharedItemIds = item.Selector == 255 || item.SelectorFileOffset < 0 || item.NeedsSpecialType ? [] :
            result.Where(other => other.SelectorFileOffset >= 0 && !other.NeedsSpecialType &&
                other.Selector == item.Selector && other.Id != item.Id).Select(other => other.Id).ToArray() }).ToArray();
    }

    private static int Family(int id) => id < 60 ? id / 3 : id < 80 ? id - 40 : 40;

    private static EngineEffectJobSlot MakeSlot(int slot, int job, int value, long offset,
        IReadOnlyList<EngineEffectJobEntity> jobs, bool exact)
        => new(slot, job, value, offset, job == 255 ? "空槽" : exact ? "当前精确；切换后可能同族继承" : "当前同族且槽ID≤实际ID；可切换精确",
            job == 255 ? [] : jobs.Where(entity => entity.Id == job || !exact && entity.FamilyId == Family(job) && job <= entity.Id)
                .Select(entity => entity.Id).ToArray())
        {
            PotentialAffectedJobIds = job == 255 ? [] : jobs.Where(entity => entity.Id == job || entity.FamilyId == Family(job) && job <= entity.Id)
                .Select(entity => entity.Id).ToArray()
        };

}
