using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EffectBindingGroupTests
{
    public static void Run(string repositoryRoot)
    {
        foreach (var name in new[] { "洗隋", "加强版6.5未加密版", "复刻版6.6山贼 国庆" })
        {
            var source = Path.Combine(repositoryRoot, "基底", name);
            var originalHash = HashService.Sha256(Path.Combine(source, "Ekd5.exe"));
            var copy = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
            try
            {
                var workspace = new ProjectWorkspace(); workspace.Open(copy);
                var report = new EngineEffectScanService().Scan(workspace);
                var service = new EngineEffectAssignmentService();
                var catalog = service.Read(workspace, report);
                var candidates = catalog.Effects.Where(effect => effect.CanBindItem && effect.BindingGroup != null).ToArray();
                TestSupport.Assert(candidates.Length > 0, "没有实际装备绑定字段。");
                var ordinary = catalog.Items.First(item => item.NeedsSpecialType);
                var reserved = catalog with
                {
                    Items = [ordinary], KnownItemSelectors = Enumerable.Range(0x80, 0x7D).ToArray(),
                    Effects = [new() { Key = "allocator", CanBindItem = true, ItemSelectors = [37] }]
                };
                var allocation = service.CreateDraft(reserved);
                TestSupport.Assert(service.StageItemBind(allocation, "allocator", ordinary.Id).Success &&
                    allocation.ItemSelectorTargets[ordinary.Id] == 0xFD, "自动分号占用了已知只读消费者selector。");
                var selected = candidates.First(effect => effect.BindingPoints.Any(call =>
                    call.Pattern is EngineEffectCallPattern.LoopDerived or EngineEffectCallPattern.NestedDerived));
                var related = candidates.Where(effect => effect.BindingGroup!.Id == selected.BindingGroup!.Id).ToArray();
                TestSupport.Assert(selected.BindingPoints.All(call => call.ItemPush.WritePlan.IsWritable), "真实来源被展开形态禁写。");
                TestSupport.Assert(selected.BindingPoints.Select(call => call.Identity).Distinct().Count() == selected.BindingPoints.Count,
                    "同物理调用的表/循环身份丢失。");
                var item = catalog.Items.First(item => item.SelectorFileOffset >= 0 && !item.NeedsSpecialType &&
                    item.Selector != 255 && !selected.ItemSelectors.Contains(item.Selector));
                var draft = service.CreateDraft(catalog);
                TestSupport.Assert(service.StageItemBind(draft, selected.Key, item.Id).Success, "已定位展开字段不能分配。");
                TestSupport.Assert(related.All(effect => draft.ItemTargets[effect.Key] == item.Selector), "共享组草稿未同步全部关联行。");
                var preview = service.BuildPreview(workspace, report, draft);
                TestSupport.Assert(preview.CanWrite, "共享组预览失败：" + string.Join("；", preview.Errors));
                if (related.Length > 1)
                {
                    draft.ItemTargets[related[^1].Key] = item.Selector == 42 ? 43 : 42;
                    TestSupport.Assert(!service.BuildPreview(workspace, report, draft).CanWrite, "同字段不同目标未报冲突。");
                    service.StageItemBind(draft, selected.Key, item.Id);
                }
                var commit = service.Commit(workspace, report, draft, preview);
                TestSupport.Assert(commit.Success && commit.VerifiedReport != null, "绑定组事务/验证报告失败：" + commit.Message);
                var after = service.Read(workspace, commit.VerifiedReport!);
                var afterGroup = after.Effects.Where(effect => effect.BindingGroup?.Id == selected.BindingGroup!.Id).ToArray();
                TestSupport.Assert(afterGroup.Length == related.Length && afterGroup.All(effect => effect.ItemSelectors.SequenceEqual([item.Selector])),
                    "共享调用未完整重新读取到同一装备号。");
                draft = service.CreateDraft(after);
                service.StageItemUnbind(draft, afterGroup[0].Key);
                preview = service.BuildPreview(workspace, commit.VerifiedReport!, draft);
                commit = service.Commit(workspace, commit.VerifiedReport!, draft, preview);
                TestSupport.Assert(commit.Success, "共享展开卸下255失败：" + commit.Message);
                after = service.Read(workspace, commit.VerifiedReport!);
                TestSupport.Assert(after.Effects.Where(effect => effect.BindingGroup?.Id == selected.BindingGroup!.Id)
                    .All(effect => effect.CanBindItem && effect.ItemSelectors.SequenceEqual([255])), "255后共享源消失或不可再分配。");

                // A located field remains editable when another consumer's equipment argument is unknown.
                var known = selected.BindingPoints[0];
                var partial = service.Read(workspace, new EngineEffectScanReport
                {
                    ExePath = commit.VerifiedReport!.ExePath, ExeSha256 = commit.VerifiedReport.ExeSha256,
                    Effects = [new() { EffectId = known.PersonalEffectId!.Value, Name = "部分实现",
                        CallSiteDetails = [after.Effects.First(effect => effect.BindingGroup?.Id == selected.BindingGroup!.Id).BindingPoints[0],
                            new() { CallAddress = 0x400001, PersonalEffectId = known.PersonalEffectId, EntryTarget = 0x41301E }] }]
                });
                TestSupport.Assert(partial.Effects.Any(effect => effect.CanBindItem && effect.BindingGroup?.Id == selected.BindingGroup!.Id),
                    "未定位消费者错误地封锁独立已定位字段。");
                Console.WriteLine($"EFFECT_BINDING_GROUP_OK {name}: group={selected.BindingGroup!.Id} aliases={related.Length} points={selected.BindingPoints.Count} transfer/conflict/FF/partial-consumer");
            }
            finally
            {
                Directory.Delete(copy, true);
                TestSupport.Assert(HashService.Sha256(Path.Combine(source, "Ekd5.exe")) == originalHash, "修改了原基底。");
            }
        }
    }
}
