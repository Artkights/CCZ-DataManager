using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineEffectContractTests
{
    public static void Run(string sourceRoot)
    {
        var sourceHash = HashService.Sha256(Path.Combine(sourceRoot, "Ekd5.exe"));
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace(); workspace.Open(copy);
            var scanner = new EngineEffectScanService(); var report = scanner.Scan(workspace);
            TestSupport.Assert(!EngineEffectScanService.KnownEntryTargets.Contains(0x00413009u) &&
                report.Effects.SelectMany(row => row.CallSiteDetails).All(call => call.EntryTarget != 0x00413009),
                "寄存器聚合器被误判为四参数可分配入口。");
            var site = report.Effects.SelectMany(row => row.CallSiteDetails).First(call =>
                call.ItemEffectId is >= 0 and < 128 && call.ItemPush.Length == 2 && call.ItemPush.WritePlan.IsWritable);
            var writer = new EngineEffectWriteService();
            EngineEffectWritePreview ItemPreview(int selector, EngineEffectScanReport scan, EngineEffectCallSite call) =>
                writer.BuildPreview(workspace, scan, new EngineEffectWriteRequest { EffectId = -1,
                    CallEdits = [new() { Identity = call.Identity, CallAddress = call.CallAddress, ItemEffectId = selector }] });
            var preview = ItemPreview(255, report, site);
            TestSupport.Assert(preview.CanWrite && preview.Changes.Any(change => change.AfterBytes.SequenceEqual(new byte[] { 255 })),
                "已证明字节渠道不接受原长度 6A FF：" + string.Join("；", preview.Errors));
            var result = writer.Commit(workspace, preview);
            TestSupport.Assert(result.Success, "255写回失败：" + result.Message);
            var scan255 = scanner.Scan(workspace);
            var afterSite = scan255.Effects.SelectMany(row => row.CallSiteDetails).Single(call => call.Identity == site.Identity);
            TestSupport.Assert(afterSite.ItemEffectId == 255, "6A FF未按逻辑255复读。");
            var rebind = ItemPreview(site.ItemEffectId!.Value, scan255, afterSite);
            TestSupport.Assert(rebind.CanWrite, "卸下后无法重新分配：" + string.Join("；", rebind.Errors));
            TestSupport.Assert(workspace.UndoSaved(result.History!.OperationId).Success, "255历史撤回失败。");

            var group = report.TableCalls.Where(call => call.ItemPush.WritePlan.IsWritable)
                .GroupBy(call => call.CallAddress).FirstOrDefault(group => group.Count() >= 2);
            if (group != null)
            {
                var instances = group.Take(2).ToArray();
                var tablePreview = writer.BuildPreview(workspace, report, new EngineEffectWriteRequest { EffectId = -1,
                    CallEdits = instances.Select((call, index) => new EngineEffectCallEdit { Identity = call.Identity,
                        CallAddress = call.CallAddress, TableIndex = call.TableIndex,
                        ItemEffectId = call.ItemEffectId == 80 + index ? 90 + index : 80 + index }).ToArray() });
                TestSupport.Assert(tablePreview.CanWrite && tablePreview.Changes.Count == 2,
                    "同一调用地址的多个表项草稿丢失：" + string.Join("；", tablePreview.Errors));
                TestSupport.Assert(instances[0].Identity != instances[1].Identity, "表实例身份相同。");
            }
            // Detail-page FF and assignment-page native edits must install one
            // adapter in the same baseline, rather than conflicting payloads.
            var assignments = new EngineEffectAssignmentService();
            var catalog = assignments.Read(workspace, report);
            if (catalog.NativeSupported)
            {
                var draft = assignments.CreateDraft(catalog);
                var target = catalog.Items.First(item => item.SelectorFileOffset >= 0 && !item.NeedsSpecialType &&
                    item.Selector is >= 0 and < 255 && item.Selector != catalog.Effects.Single(effect => effect.Key == "native:hp").ItemSelectors[0]);
                TestSupport.Assert(assignments.StageItemBind(draft, "native:hp", target.Id).Success,
                    "联合草稿原生HP改绑失败。");
                var assignmentPreview = assignments.BuildPreview(workspace, report, draft, ensureEquipmentFfGuard: true);
                var detailPreview = writer.BuildPreview(workspace, report, new EngineEffectWriteRequest
                {
                    EffectId = -1, DeferEquipmentFfGuard = true,
                    CallEdits = [new() { Identity = site.Identity, CallAddress = site.CallAddress, ItemEffectId = 255 }]
                });
                TestSupport.Assert(assignmentPreview.CanWrite && detailPreview.CanWrite,
                    "联合草稿预览失败：" + string.Join("；", assignmentPreview.Errors.Concat(detailPreview.Errors)));
                var combined = EngineEffectTransactionService.MergeCandidates(assignmentPreview.Candidates.Concat(detailPreview.Candidates));
                var combinedResult = new EngineEffectTransactionService().Commit(workspace, "详情FF与原生HP联合事务", combined, () =>
                {
                    writer.Verify(workspace, detailPreview);
                    assignments.Verify(workspace, assignmentPreview);
                });
                TestSupport.Assert(combinedResult.Success, "详情/分配联合提交失败：" + combinedResult.Message);
                var native = NativeEffectAdapter.Read(File.ReadAllBytes(report.ExePath));
                TestSupport.Assert(native.HasFfGuard && native.HpCurrentSelector == target.Selector,
                    "联合事务没有保留独立HP与装备FF守卫。");
                TestSupport.Assert(workspace.UndoSaved(combinedResult.History!.OperationId).Success &&
                    HashService.Sha256(report.ExePath) == report.ExeSha256,
                    "联合事务历史撤回没有恢复基线文件长度和SHA。");
                Console.WriteLine("UNIFIED_DETAIL_FF_NATIVE_HP_OK");
            }
            var path = Path.Combine(copy, "Ekd5.exe"); var before = File.ReadAllBytes(path);
            var enlarged = before.Concat(new byte[512]).ToArray();
            var candidate = new WorkspaceBinaryCandidate(path, before, enlarged, "变长回归");
            var transaction = new WorkspaceBinaryTransaction();
            var failed = transaction.Commit(workspace, "验证失败回滚", [candidate], () => throw new InvalidDataException("预期语义失败"));
            TestSupport.Assert(!failed.Saved && File.ReadAllBytes(path).SequenceEqual(before), "变长失败未完整回滚。");
            var saved = transaction.Commit(workspace, "验证变长历史", [candidate]);
            TestSupport.Assert(saved.Saved && saved.History!.Files.Single().WholeFileReplacement &&
                saved.History.Files.Single().BeforeLength == before.Length, "变长事务没有完整历史快照。");
            TestSupport.Assert(workspace.UndoSaved(saved.History!.OperationId).Success &&
                File.ReadAllBytes(path).SequenceEqual(before), "历史撤回未恢复原EXE长度与SHA。");
            var a = (byte[])before.Clone(); a[0x20] ^= 1;
            var merged = EngineEffectTransactionService.MergeCandidates([
                new(path, before, a, "A"), new(path, before, a, "B"), candidate]);
            TestSupport.Assert(merged.Single().AfterBytes.Length == enlarged.Length &&
                merged.Single().AfterBytes[0x20] == a[0x20], "相同位置修改/变长合并错误。");
            var b = (byte[])before.Clone(); b[0x20] ^= 2;
            var conflict = false;
            try { EngineEffectTransactionService.MergeCandidates([new(path, before, a, "A"), new(path, before, b, "B")]); }
            catch (InvalidDataException) { conflict = true; }
            TestSupport.Assert(conflict, "不同字节修改没有报冲突。");
            var shorter = before[..^16];
            var shrinkMerge = EngineEffectTransactionService.MergeCandidates([
                new(path, before, a, "独立字节"), new(path, before, shorter, "缩短自己的适配节")]);
            TestSupport.Assert(shrinkMerge.Single().AfterBytes.Length == shorter.Length &&
                shrinkMerge.Single().AfterBytes[0x20] == a[0x20], "缩短与独立字节草稿无法联合提交。");
            Console.WriteLine($"ENGINE_EFFECT_CONTRACT_TESTS_OK {report.EngineVersion} {report.ExeSha256}");
        }
        finally
        {
            Directory.Delete(copy, true);
            TestSupport.Assert(HashService.Sha256(Path.Combine(sourceRoot, "Ekd5.exe")) == sourceHash, "测试修改了真实基底。");
        }
    }
}
