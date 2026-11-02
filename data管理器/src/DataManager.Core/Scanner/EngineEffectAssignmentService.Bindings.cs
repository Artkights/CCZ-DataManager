using DataManager.Core.Models;

namespace DataManager.Core.Scanner;

public sealed partial class EngineEffectAssignmentService
{
    private static List<EngineEffectAssignmentEffect> BuildBindingEffects(EngineEffectScanReport report,
        byte[] bytes, EffectAssignmentLayout layout, IReadOnlyList<EngineEffectItemEntity> items,
        IReadOnlyList<EngineEffectJobEntity> jobs)
    {
        var allCalls = report.Effects.SelectMany(row => row.CallSiteDetails.Concat(row.DerivedCallDetails)
                .Concat(row.TableCallDetails).Concat(row.LoopCallDetails))
            .Concat(report.UnassignedCalls).Concat(report.DerivedCalls).Concat(report.TableCalls).Concat(report.LoopCalls)
            .Where(call => call.Pattern != EngineEffectCallPattern.EngineInternal &&
                (call.EntryTarget is 0x004101D9 or 0x0042518F or 0x0041301E ||
                 call.Pattern is EngineEffectCallPattern.WrapperDerived or EngineEffectCallPattern.TableDerived or
                     EngineEffectCallPattern.LoopDerived or EngineEffectCallPattern.NestedDerived))
            .DistinctBy(call => call.Identity).ToArray();
        // A group represents a physical definition, not a guessed formula or a synthetic loop operand.
        var groups = allCalls.Where(HasWritableItemLocation)
            .GroupBy(call => (call.ItemPush.WritePlan.ImmediateFileOffset, call.ItemPush.WritePlan.ImmediateLength))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var rows = report.Effects.ToDictionary(row => row.EffectId);
        var ids = rows.Keys.Concat(layout.Verified ? Enumerable.Range(0, layout.RowCount) : [])
            .Where(id => id is >= 0 and <= 254).Distinct().Order();
        var result = new List<EngineEffectAssignmentEffect>();
        foreach (var id in ids)
        {
            rows.TryGetValue(id, out var row);
            // Range dispatchers (one stub covering several personal ids) keep
            // PersonalEffectId null; attach the same shared binding to every id
            // the range covers so each named effect can be staged individually.
            var calls = allCalls.Where(call => call.PersonalEffectId == id ||
                call.PersonalEffectId == null && call.EffectRange.Contains(id)).ToArray();
            var locations = calls.Where(HasWritableItemLocation)
                .Select(call => (call.ItemPush.WritePlan.ImmediateFileOffset, call.ItemPush.WritePlan.ImmediateLength))
                .Distinct().OrderBy(key => key.ImmediateFileOffset).ToArray();
            var hasJob = layout.Verified && id < layout.RowCount;
            var slots = hasJob ? Enumerable.Range(0, layout.SlotCount).Select(slot =>
            {
                var offset = layout.GetJobIdOffset(id, slot);
                var valueOffset = layout.GetJobValueOffset(id, slot);
                return MakeSlot(slot, bytes[checked((int)offset)], bytes[checked((int)valueOffset)], offset,
                    jobs, layout.ExactMode) with { ValueFileOffset = valueOffset };
            }).ToArray() : [];
            var name = string.IsNullOrWhiteSpace(row?.Name) ? $"未命名特效 {id}" : row.Name;
            // Policy: every effect with no call site at all is assignable through
            // the item effect-number byte (the engine reads that field for
            // accessories and static 26..66 handlers). A call whose item number or
            // write field is still unlocated stays read-only until disassembly
            // finds it, because guessing that selector would be a silent no-op.
            var staticStat = calls.Length == 0;
            var blockReasons = calls.Select(call => call.ItemPush.WritePlan.BlockReason)
                .Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct().ToArray();
            var itemReason = staticStat ? string.Empty :
                blockReasons.Length == 0 ? "未定位到可写的装备分配字段；需要继续反汇编定位特效号。" :
                "装备分配字段未证明可写：" + string.Join("；", blockReasons);
            var basis = new EngineEffectAssignmentEffect
            {
                Key = $"effect:{id}", Name = name, ItemPoolVisible = locations.Length <= 1, Description = row?.Description ?? string.Empty,
                PersonalEffectId = id, Kind = staticStat ? EngineEffectAssignmentKind.StaticStat : EngineEffectAssignmentKind.SharedLookup,
                BindingPoints = calls, JobSlots = slots, CanBindJob = hasJob, CanBindItem = staticStat,
                ItemSelectors = staticStat ? [id] : locations.Length == 0 ? calls.Where(call => call.ItemEffectId != null)
                    .Select(call => call.ItemEffectId!.Value).Distinct().Order().ToArray() : [],
                OwnerItemIds = staticStat ? items.Where(item => item.SelectorFileOffset >= 0 && item.Selector == id)
                    .Select(item => item.Id).ToArray() : [],
                OwnerSummary = staticStat ? string.Join("、", items.Where(item => item.SelectorFileOffset >= 0 && item.Selector == id)
                    .Select(item => item.Display)) : string.Empty,
                ImplementationStatus = staticStat ? "装备号绑定（宝物数据直读）" : "已定位消费者；分配权限按实际字段判断",
                SharedJobValue = hasJob && layout.SharedValue,
                SharedPersonIds = hasJob ? layout.ReadPersonIds(bytes, id).Where(person => person < 1024).ToArray() : [],
                ItemReadOnlyReason = itemReason, JobReadOnlyReason = hasJob ? string.Empty : layout.Evidence,
                ReadOnlyReason = itemReason,
                Source = staticStat ? "装备号绑定：宝物装备号即特效号；26..66 由 0x0044F2AC 选处理函数，其余按宝物数据直读" :
                    hasJob ? $"活动表文件+0x{layout.GetRowOffset(id):X}；{layout.RowSize}字节/行" : "分配位置未定位",
                Conditions = string.Join("；", calls.Select(call => call.TriggerSummary).Where(text => text.Length > 0).Distinct()),
                ValueSource = staticStat ? "宝物真实效果值字段决定强度；写入只改装备号。" :
                    hasJob ? layout.SharedValue ? "兵种与本行人物槽共用N；修改值会联动本行人物。" :
                    "兵种读取所选真实槽的独立N。" : "未定位兵种值字段。"
            };
            var unresolved = calls.Where(call => call.ItemEffectId != null && !HasWritableItemLocation(call)).ToArray();
            if (locations.Length > 0 && unresolved.Length > 0)
                result.Add(basis with
                {
                    Key = basis.Key + ":unlocated", Name = name + "（未定位绑定）", CanBindJob = false, JobSlots = [],
                    JobPoolVisible = false, ItemPoolVisible = false, BindingPoints = unresolved,
                    ItemSelectors = unresolved.Select(call => call.ItemEffectId!.Value).Distinct().Order().ToArray(),
                    Source = "已有装备匹配号已解析，分配字段尚未定位；" + itemReason
                });
            // Keep one canonical job row. Additional item groups never duplicate the job slots.
            if (locations.Length != 1) result.Add(basis);
            for (var index = 0; index < locations.Length; index++)
            {
                var key = locations[index];
                var points = groups[key];
                var plan = points[0].ItemPush.WritePlan;
                var selectors = points.Select(call => call.ItemEffectId!.Value).Distinct().Order().ToArray();
                var groupId = $"item:{key.ImmediateFileOffset:X}:{key.ImmediateLength}";
                var owners = items.Where(item => item.SelectorFileOffset >= 0 && !item.NeedsSpecialType &&
                    item.Selector != 255 && selectors.Contains(item.Selector)).ToArray();
                result.Add(basis with
                {
                    Key = locations.Length == 1 ? basis.Key : $"{basis.Key}:binding:{key.ImmediateFileOffset:X}",
                    Name = locations.Length == 1 ? name : $"{name}（绑定组{index + 1}/{locations.Length}）",
                    BindingPoints = points,
                    BindingGroup = new(groupId,
                        [new(report.ExePath, key.ImmediateFileOffset, key.ImmediateLength, plan.BeforeBytes.ToArray(),
                            "装备selector低8位；按原指令长度写回", points.Select(call => call.Identity.ToString()).ToArray())],
                        points.Where(call => call.PersonalEffectId != null).Select(call => call.PersonalEffectId!.Value).Distinct().Order().ToArray()),
                    CanBindItem = true, ItemPoolVisible = true, JobPoolVisible = locations.Length == 1, ItemReadOnlyReason = string.Empty, ReadOnlyReason = string.Empty,
                    CanBindJob = locations.Length == 1 && hasJob,
                    JobSlots = locations.Length == 1 ? slots : [],
                    ItemSelectors = selectors, OwnerItemIds = owners.Select(item => item.Id).ToArray(),
                    OwnerSummary = string.Join("、", owners.Select(item => item.Display)),
                    Source = $"装备分配字段文件+0x{key.ImmediateFileOffset:X}，宽度{key.ImmediateLength}；" +
                        $"共享消费者{points.Length}个；本次操作仅覆盖此绑定组。" + basis.Source,
                    ValueSource = "装备数值路径读取宝物共用效果值；开关路径不读取该值。" + basis.ValueSource
                });
            }
        }
        foreach (var (key, points) in groups.Where(pair => !pair.Value.Any(call =>
                     call.PersonalEffectId is >= 0 and <= 254 || call.EffectRange.Count > 0)))
        {
            var selectors = points.Select(call => call.ItemEffectId!.Value).Distinct().Order().ToArray();
            var plan = points[0].ItemPush.WritePlan;
            result.Add(new()
            {
                Key = $"binding:{key.ImmediateFileOffset:X}", Name = $"未归属实现（装备绑定0x{key.ImmediateFileOffset:X}）",
                Kind = EngineEffectAssignmentKind.SharedLookup, BindingPoints = points, CanBindItem = true,
                JobPoolVisible = false, ItemSelectors = selectors,
                BindingGroup = new($"item:{key.ImmediateFileOffset:X}:{key.ImmediateLength}",
                    [new(report.ExePath, key.ImmediateFileOffset, key.ImmediateLength, plan.BeforeBytes.ToArray(),
                        "装备selector低8位", points.Select(call => call.Identity.ToString()).ToArray())], []),
                Source = $"装备分配字段文件+0x{key.ImmediateFileOffset:X}；个人号/实现尚未解析，仅调整已定位装备绑定。",
                ValueSource = "宝物真实效果值字段可独立编辑；读取模式见消费者。",
                JobReadOnlyReason = "未归属到有效个人/兵种特效行。"
            });
        }
        foreach (var call in allCalls.Where(call => call.PersonalEffectId is not (>= 0 and <= 254) &&
                     call.ItemEffectId != null && !HasWritableItemLocation(call)))
            result.Add(new()
            {
                Key = $"unlocated:{call.Identity}", Name = $"未归属实现（0x{call.CallAddress:X8}）",
                Kind = EngineEffectAssignmentKind.SharedLookup, BindingPoints = [call], JobPoolVisible = false,
                ItemPoolVisible = false,
                ItemSelectors = [call.ItemEffectId!.Value],
                ItemReadOnlyReason = call.ItemPush.WritePlan.BlockReason ?? "没有定位到唯一可写的装备字段。",
                ReadOnlyReason = call.ItemPush.WritePlan.BlockReason ?? "没有定位到唯一可写的装备字段。",
                Source = "已有装备匹配号已解析；未定位实际可写字段，保留当前归属。"
            });
        return result;
    }

    private static bool HasWritableItemLocation(EngineEffectCallSite call)
    {
        var plan = call.ItemPush.WritePlan;
        return call.ItemEffectId != null && plan.IsWritable && plan.Definition != null &&
            plan.ImmediateFileOffset > 0 && plan.ImmediateLength is 1 or 4 &&
            plan.BeforeBytes.Length == plan.ImmediateLength;
    }
}
