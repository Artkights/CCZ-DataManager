using System.Buffers.Binary;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineEffectAssignmentTests
{
    public static void Run(string sourceRoot)
    {
        var sourceHash = HashService.Sha256(Path.Combine(sourceRoot, "Ekd5.exe"));
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace(); workspace.Open(copy);
            var scanner = new EngineEffectScanService();
            var service = new EngineEffectAssignmentService();
            var report = scanner.Scan(workspace);
            var catalog = service.Read(workspace, report);
            Console.WriteLine($"ASSIGNMENT_BASELINE {Path.GetFileName(sourceRoot)} SHA256={sourceHash} " +
                $"items={catalog.Items.Count} jobs={catalog.Jobs.Count} native={catalog.NativeSupported} jobVerified={catalog.JobLayoutVerified}");
            Console.WriteLine("JOB_LAYOUT " + catalog.JobLayoutEvidence);
            TestSupport.Assert(catalog.Items.Count > 0 && catalog.Jobs.Count == 80, "实体宝物/80兵种未读取。");
            AssertReadOnlyContracts(workspace, report, service, catalog);
            AssertNationalAxe(sourceHash, service, catalog);

            var ordinary = catalog.Items.First(item => item.NeedsSpecialType && item.Value != null);
            TestSupport.Assert(service.GetItemAssignments(service.CreateDraft(catalog), ordinary.Id).Count == 0,
                "普通偶数类型把未启用selector误显示为已分配。");
            var group = catalog.Effects.Where(effect => effect.Kind == EngineEffectAssignmentKind.SharedLookup &&
                    effect.CanBindItem && effect.ItemSelectors.Any(selector => selector != 255))
                .OrderByDescending(effect => effect.BindingPoints.Count).First();
            var groupIdentities = group.BindingPoints.Select(call => call.Identity).ToHashSet();
            var draft = service.CreateDraft(catalog);
            TestSupport.Assert(!draft.HasPendingChanges && !workspace.IsDirty, "只读目录产生草稿。");
            var usedSelectors = catalog.Items.Select(item => item.Selector)
                .Concat(catalog.Effects.SelectMany(effect => effect.ItemSelectors)).ToHashSet();
            TestSupport.Assert(service.StageItemBind(draft, group.Key, ordinary.Id).Success, "普通装备初绑失败。");
            var allocated = draft.ItemSelectorTargets[ordinary.Id];
            TestSupport.Assert(allocated is >= 0x80 and <= 0xFD && !usedSelectors.Contains(allocated) &&
                draft.SpecialItemIds.Contains(ordinary.Id) && draft.ItemTargets[group.Key] == allocated,
                "普通装备初绑没有分配未占用装备号并转同家族特殊类型。");
            TestSupport.Assert(service.GetItemAssignments(draft, ordinary.Id).Any(effect => effect.Key == group.Key),
                "转换特殊类型的分配草稿没有显示新关系。");
            var value = ordinary.Value == 31 ? 32 : 31;
            TestSupport.Assert(service.StageItemValue(draft, ordinary.Id, value).Success, "真实N值草稿失败。");
            if (catalog.NativeSupported)
            {
                TestSupport.Assert(service.StageItemBind(draft, "native:hp", ordinary.Id).Success &&
                    service.StageItemBind(draft, "native:mp", ordinary.Id).Success, "原生HP/MP组合绑定失败。");
                TestSupport.Assert(draft.ItemTargets["native:hp"] == allocated && draft.ItemTargets["native:mp"] == allocated,
                    "原生绑定没有复用宝物真实新selector，或把物品ID当作selector。");
            }
            string? jobKey = null;
            if (catalog.JobLayoutVerified) jobKey = StageJobSlots(service, draft);
            else
            {
                TestSupport.Assert(catalog.Effects.All(effect => !effect.CanBindJob && effect.JobSlots.Count == 0),
                    "未验证旧版兵种布局仍开放分配。");
                TestSupport.Assert(!service.StageJobBind(draft, group.Key, 0, 19).Success,
                    "未验证布局没有拒绝兵种改写。");
            }
            Commit("ordinary-first-bind/native-both/group-transfer/job-slots", workspace, report, service, draft);
            report = scanner.Scan(workspace); catalog = service.Read(workspace, report);
            var written = catalog.Items.Single(item => item.Id == ordinary.Id);
            TestSupport.Assert(written.Selector == allocated && written.TypeValue == ordinary.TypeValue + 1 &&
                !written.NeedsSpecialType && written.Value == value, "普通初绑后的selector/type/真实N语义不符。");
            AssertGroup(catalog, group.Key, allocated, groupIdentities);
            if (catalog.NativeSupported)
                TestSupport.Assert(service.GetItemAssignments(service.CreateDraft(catalog), ordinary.Id)
                    .Count(effect => effect.Kind is EngineEffectAssignmentKind.NativeHp or EngineEffectAssignmentKind.NativeMp) == 2,
                    "同宝物HP与MP未独立共存。");
            if (jobKey != null) AssertJobSlots(catalog, jobKey);

            draft = service.CreateDraft(catalog);
            TestSupport.Assert(service.StageItemUnbind(draft, group.Key).Success, "四参数组卸下草稿失败。");
            if (catalog.NativeSupported)
                TestSupport.Assert(service.StageItemUnbind(draft, "native:hp").Success, "原生HP卸下草稿失败。");
            Commit("group-FF/native-hp-only-unbind", workspace, report, service, draft);
            report = scanner.Scan(workspace); catalog = service.Read(workspace, report);
            AssertGroup(catalog, group.Key, 255, groupIdentities);
            if (catalog.NativeSupported)
            {
                var state = NativeEffectAdapter.Read(File.ReadAllBytes(report.ExePath));
                TestSupport.Assert(state.IsInstalled && state.HasFfGuard && state.HpCurrentSelector == 255 &&
                    state.MpCurrentSelector == allocated, "卸下HP或四参数FF破坏独立MP/停用守卫。");
            }

            draft = service.CreateDraft(catalog);
            var existing = catalog.Items.First(item => item.SelectorFileOffset >= 0 && !item.NeedsSpecialType &&
                item.Selector is >= 0 and < 255 && item.Selector != allocated);
            TestSupport.Assert(service.StageItemBind(draft, group.Key, existing.Id).Success, "四参数组FF后再分配失败。");
            TestSupport.Assert(draft.ItemTargets[group.Key] == existing.Selector && draft.ItemSelectorTargets.Count == 0,
                "已有特殊装备绑定错误使用了ItemId或重新分配selector。");
            Commit("group-reassign-after-FF", workspace, report, service, draft);
            // Commit already rescans every binding point. Check disk bytes here
            // rather than repeating a final full-engine scan.
            var exe = File.ReadAllBytes(report.ExePath);
            foreach (var call in catalog.Effects.Single(effect => effect.Key == group.Key).BindingPoints)
                TestSupport.Assert(exe[checked((int)call.ItemPush.WritePlan.ImmediateFileOffset)] == existing.Selector,
                    "四参数组再分配未覆盖所有绑定点。");
            TestSupport.Assert(!draft.HasPendingChanges && !workspace.IsDirty, "事务后草稿/工作区未清理。");

            report = scanner.Scan(workspace); catalog = service.Read(workspace, report);
            TestSupport.Assert(catalog.Effects.SingleOrDefault(effect => effect.Key == "effect:73")?.CanBindItem == true,
                "辅助攻击力 73 未按宝物数据通道开放。");
            var staticEffect = catalog.Effects.FirstOrDefault(effect =>
                effect.Kind == EngineEffectAssignmentKind.StaticStat && effect.CanBindItem && effect.ItemSelectors.Count == 1);
            TestSupport.Assert(staticEffect != null, "没有可测试的静态数值特效。");
            var staticTarget = staticEffect!.ItemSelectors[0];
            var staticItem = catalog.Items.FirstOrDefault(item => item.Category == "防具" &&
                    item.SelectorFileOffset >= 0 && item.Selector != staticTarget && item.Selector is >= 0 and < 255)
                ?? catalog.Items.FirstOrDefault(item => item.Category == "辅助装备" &&
                    item.SelectorFileOffset >= 0 && item.Selector != staticTarget && item.Selector is >= 0 and < 255);
            TestSupport.Assert(staticItem != null, "没有可写装备号的宝物。");
            draft = service.CreateDraft(catalog);
            TestSupport.Assert(service.StageItemBind(draft, staticEffect.Key, staticItem!.Id).Success, "静态数值特效绑定失败。");
            TestSupport.Assert(draft.ItemSelectorTargets.TryGetValue(staticItem.Id, out var staticSelector) &&
                staticSelector == staticTarget && !draft.ItemTargets.ContainsKey(staticEffect.Key),
                "静态数值特效没有把宝物装备号改为特效号。");
            Commit("static-stat-selector", workspace, report, service, draft);
            report = scanner.Scan(workspace); catalog = service.Read(workspace, report);
            TestSupport.Assert(catalog.Items.Single(item => item.Id == staticItem.Id).Selector == staticTarget,
                "静态数值特效写后装备号不符。");
            TestSupport.Assert(catalog.Effects.Single(effect => effect.Key == staticEffect.Key)
                .OwnerItemIds.Contains(staticItem.Id), "静态数值特效写后没有归属到目标宝物。");
            Console.WriteLine($"ENGINE_EFFECT_STATIC_STAT_OK effect={staticEffect.Key} selector={staticTarget} item={staticItem.Id}");

            // Only 6.4 carries the shared range stub; the other bases expose 144 as
            // a plain static effect, so the five-key linkage does not apply there.
            var rangeEffect = catalog.Effects.FirstOrDefault(effect => effect.Key == "effect:144" &&
                effect.Kind == EngineEffectAssignmentKind.SharedLookup && effect.CanBindItem);
            if (rangeEffect != null)
            {
                var rangeItem = catalog.Items.First(item => item.SelectorFileOffset >= 0 && !item.NeedsSpecialType &&
                    item.Selector is >= 0 and < 255);
                draft = service.CreateDraft(catalog);
                TestSupport.Assert(service.StageItemBind(draft, rangeEffect.Key, rangeItem.Id).Success, "范围分派绑定失败。");
                TestSupport.Assert(draft.ItemTargets.Count(pair => pair.Key.StartsWith("effect:14", StringComparison.Ordinal)) == 5,
                    "范围分派没有联动 144–148 五个特效。");
                Commit("range-144-148", workspace, report, service, draft);
                report = scanner.Scan(workspace); catalog = service.Read(workspace, report);
                var offset = rangeEffect.BindingPoints[0].ItemPush.WritePlan.ImmediateFileOffset;
                TestSupport.Assert(File.ReadAllBytes(report.ExePath)[checked((int)offset)] == rangeItem.Selector,
                    "范围分派写后装备号不符。");
                Console.WriteLine($"ENGINE_EFFECT_RANGE_OK effect={rangeEffect.Key} selector={rangeItem.Selector}");
            }
            Console.WriteLine("ENGINE_EFFECT_ASSIGNMENTS_OK entities/ordinary-new-selector/type/value/native-independent/group-transfer/FF/reassign/job-contract/two-slots/replacement/readonly/transaction/static-stat/range");
        }
        finally
        {
            Directory.Delete(copy, true);
            TestSupport.Assert(HashService.Sha256(Path.Combine(sourceRoot, "Ekd5.exe")) == sourceHash, "测试修改了真实基底。");
            Console.WriteLine("SOURCE_BASE_UNCHANGED " + sourceHash);
        }
    }

    private static void Commit(string scenario, ProjectWorkspace workspace, EngineEffectScanReport report,
        EngineEffectAssignmentService service, EngineEffectAssignmentDraft draft)
    {
        var preview = service.BuildPreview(workspace, report, draft);
        TestSupport.Assert(preview.CanWrite, scenario + "预览失败：" + string.Join("；", preview.Errors));
        var result = service.Commit(workspace, report, draft, preview);
        TestSupport.Assert(result.Success, scenario + "事务失败：" + result.Message);
        Console.WriteLine($"ASSIGNMENT_SCENARIO_OK {scenario} files={preview.Candidates.Count} backup={result.BackupPath}");
    }

    private static string StageJobSlots(EngineEffectAssignmentService service, EngineEffectAssignmentDraft draft)
    {
        var count = draft.Catalog.JobLayout!.SlotCount;
        TestSupport.Assert(draft.Catalog.Effects.Where(effect => effect.CanBindJob)
            .All(effect => effect.JobSlots.Count == count), "真实兵种槽数量未完整生成。");
        var effect = draft.Catalog.Effects.First(effect => effect.CanBindJob);
        for (var slot = 0; slot < count; slot++) service.StageJobUnbind(draft, effect.Key, slot);
        TestSupport.Assert(service.GetEffects(draft).Single(row => row.Key == effect.Key).JobPoolDisplay.EndsWith("*" + count), "空槽标记错误。");
        TestSupport.Assert(service.StageJobBind(draft, effect.Key, 0, 19).Success, "优先空槽失败。");
        if (count == 2) TestSupport.Assert(service.StageJobBind(draft, effect.Key, 1, 23).Success, "第二槽写入失败。");
        TestSupport.Assert(service.GetEffects(draft).Single(row => row.Key == effect.Key).IsJobPoolFull, "满槽*0错误。");
        var full = service.StageJobBind(draft, effect.Key, 2, 29);
        TestSupport.Assert(!full.Success && full.NeedsReplacement && full.ExistingSlots.Count == count, "满槽未要求替换选择。");
        TestSupport.Assert(service.StageJobBind(draft, effect.Key, 2, 29, count - 1).Success, "明确选槽替换失败。");
        return effect.Key;
    }

    private static void AssertJobSlots(EngineEffectAssignmentCatalog catalog, string key)
    {
        var slots = catalog.Effects.Single(effect => effect.Key == key).JobSlots;
        TestSupport.Assert(slots.Count == 1 ? slots[0].JobId == 2 && slots[0].Value == 29 :
            slots[0].JobId == 0 && slots[0].Value == 19 && slots[1].JobId == 2 && slots[1].Value == 29,
            "真实兵种槽复读或替换值错误。");
    }

    private static void AssertGroup(EngineEffectAssignmentCatalog catalog, string key, int target,
        HashSet<EngineEffectCallIdentity> original)
    {
        var effect = catalog.Effects.Single(effect => effect.Key == key);
        TestSupport.Assert(effect.ItemSelectors.SequenceEqual(new[] { target }) && effect.CanBindItem &&
            original.SetEquals(effect.BindingPoints.Select(call => call.Identity)) &&
            effect.BindingPoints.All(call => call.ItemEffectId == target), "四参数组没有完整转移或遗失消费者身份。");
    }

    private static void AssertNationalAxe(string sha, EngineEffectAssignmentService service, EngineEffectAssignmentCatalog catalog)
    {
        if (!sha.StartsWith("F20C", StringComparison.OrdinalIgnoreCase)) return;
        var axe = catalog.Items.Single(item => item.Id == 16);
        var effects = service.GetItemAssignments(service.CreateDraft(catalog), 16);
        TestSupport.Assert(axe.Selector == 37 && axe.SharedItemIds.Contains(112) && axe.SharedItemIds.Contains(146),
            "国庆16/112/146的实际装备号共享关系错误。");
        TestSupport.Assert(effects.Any(effect => effect.Key == "native:hp") &&
            effects.Any(effect => effect.PersonalEffectId == 2 &&
                effect.BindingPoints.Any(call => call.CallAddress == 0x43C749)),
            "无敌大斧37没有同时显示原生HP和回合恢复消费者：" +
                string.Join("；", effects.Select(effect => effect.Key + ":" + effect.Name)));
        Console.WriteLine("NATIONAL_AXE_OK Item16→selector37 shared=16/112/146 HP+turn-recovery");
    }

    private static void AssertReadOnlyContracts(ProjectWorkspace workspace, EngineEffectScanReport report,
        EngineEffectAssignmentService service, EngineEffectAssignmentCatalog catalog)
    {
        var known = catalog.Effects.First(effect => effect.BindingPoints.Count > 0).BindingPoints[0];
        var partialReport = new EngineEffectScanReport
        {
            ExePath = report.ExePath, ExeSha256 = report.ExeSha256,
            Effects = [new() { EffectId = 253, Name = "未扫描名称行" },
                new() { EffectId = 254, Name = "未知必要装备通道", CallSiteDetails = [known,
                    new() { CallAddress = 0x400001, EntryTarget = 0x41301E, Pattern = EngineEffectCallPattern.Unknown,
                        IsReachable = true, PersonalEffectId = 254, ItemEffectId = null }] }]
        };
        var partial = service.Read(workspace, partialReport);
        var absent = partial.Effects.Single(effect => effect.Key == "effect:253");
        var unknown = partial.Effects.Single(effect => effect.Key == "effect:254");
        TestSupport.Assert(absent.CanBindItem && absent.Kind == EngineEffectAssignmentKind.StaticStat &&
            absent.ItemSelectors.SequenceEqual(new[] { 253 }) &&
            absent.CanBindJob == (catalog.JobLayout?.RowCount > 253) && !unknown.CanBindItem,
            "无调用编号未按装备号通道开放，或未知必要通道仍被开放。");
        var draft = service.CreateDraft(partial);
        TestSupport.Assert(!service.StageItemBind(draft, unknown.Key, catalog.Items[0].Id).Success && !draft.HasPendingChanges,
            "未知必要装备通道仍可产生分配草稿。");
        var absentItem = catalog.Items.First(item => item.SelectorFileOffset >= 0 && item.Selector != 253);
        TestSupport.Assert(service.StageItemBind(draft, absent.Key, absentItem.Id).Success &&
            draft.ItemSelectorTargets.GetValueOrDefault(absentItem.Id) == 253 && !draft.ItemTargets.ContainsKey(absent.Key),
            "无调用编号没有通过装备号通道写入特效号。");
        // Most bases initialise ordinary selectors to FF. Supply a non-FF
        // counterexample in the read-only catalog to prove category gating is
        // independent of the empty-selector convention, without touching a file.
        var ordinary = catalog.Items.First(item => item.NeedsSpecialType) with { Selector = 37 };
        var gateCatalog = catalog with
        {
            Items = catalog.Items.Select(item => item.Id == ordinary.Id ? ordinary : item).ToArray(),
            Effects = [new() { Key = "gate", Name = "类别门槛", CanBindItem = true, ItemSelectors = [37] }]
        };
        var gateDraft = service.CreateDraft(gateCatalog);
        TestSupport.Assert(service.GetItemAssignments(gateDraft, ordinary.Id).Count == 0,
            "普通偶数类型的非FF selector误判为已分配。");
        TestSupport.Assert(service.StageItemBind(gateDraft, "gate", ordinary.Id).Success &&
            service.GetItemAssignments(gateDraft, ordinary.Id).Any(effect => effect.Key == "gate"),
            "同类特殊转换草稿未开放真实新selector关系。");
        var original = File.ReadAllBytes(report.ExePath);
        var changed = (byte[])original.Clone(); changed[Offset(changed, 0x41301E)] ^= 1;
        try
        {
            File.WriteAllBytes(report.ExePath, changed);
            var changedReport = new EngineEffectScanReport { ExePath = report.ExePath, ExeSha256 = HashService.Sha256(changed), Effects = report.Effects };
            var rejected = service.Read(workspace, changedReport);
            TestSupport.Assert(!rejected.JobLayoutVerified && rejected.Effects.All(effect => !effect.CanBindJob && effect.JobSlots.Count == 0),
                "未知provider机器码仍开放真实兵种槽。");
        }
        finally { File.WriteAllBytes(report.ExePath, original); }
        Console.WriteLine("READONLY_CONTRACTS_OK unscanned/unknown-required-channel/unknown-job-provider");
    }

    private static int Offset(byte[] image, uint va)
    {
        int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(offset, 4));
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));
        var pe = I32(0x3c); var optional = pe + 24; var rva = va - (uint)I32(optional + 28);
        var table = optional + U16(pe + 20);
        for (var index = 0; index < U16(pe + 6); index++)
        {
            var header = table + index * 40; var start = (uint)I32(header + 12);
            if (rva >= start && rva < start + (uint)I32(header + 16))
                return checked((int)(rva - start) + I32(header + 20));
        }
        throw new InvalidDataException("测试VA没有文件映射。");
    }
}
