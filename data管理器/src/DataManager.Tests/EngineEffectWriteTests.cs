﻿using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineEffectWriteTests
{
    public static void Run(string sourceRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ekd5.exe"] = HashService.Sha256(Path.Combine(sourceRoot, "Ekd5.exe")),
            ["Imsg.e5"] = HashService.Sha256(Path.Combine(sourceRoot, "Imsg.e5"))
        };
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var report = new EngineEffectScanService().Scan(workspace);
            var row = report.Effects.First(item => item.HasCalls && item.HasNameSlot && item.HasDescriptionSlot);
            var site = row.CallSiteDetails[0];
            var personal = site.PersonalEffectId!.Value == 1 ? 2 : 1;
            var item = site.ItemEffectId is <= 100 ? site.ItemEffectId!.Value + 1 : 1;
            var suffix = (byte)((row.SuffixByte!.Value + 1) % (EngineEffectSuffixLabels.MaximumSuffix(report.EngineVersion) + 1));
            var entry = site.EntryTarget == EngineEffectScanService.CoreEntry
                ? EngineEffectScanService.WrapperEntry
                : EngineEffectScanService.CoreEntry;
            var request = new EngineEffectWriteRequest
            {
                EffectId = row.EffectId,
                NewName = "测试特效",
                NewSuffix = suffix,
                NewDescription = "测试说明",
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [site.CallAddress] = new()
                    {
                        CallAddress = site.CallAddress,
                        ValueMode = site.ValueMode == 1 ? 0 : 1,
                        StackingMode = site.StackingMode == 1 ? 0 : 1,
                        ItemEffectId = item,
                        PersonalEffectId = personal,
                        EntryTarget = entry
                    }
                }
            };
            var service = new EngineEffectWriteService();
            var preview = service.BuildPreview(workspace, report, request);
            TestSupport.Assert(preview.CanWrite, "写入预览被拒绝：" + string.Join("；", preview.Errors));
            TestSupport.Assert(preview.Changes.Count >= 7, $"写入项不足：{preview.Changes.Count}");
            TestSupport.Assert(preview.FollowEffectId == personal, "修改个人特效号后没有跟随 ID。");
            TestSupport.Assert(preview.Changes.Any(change => change.Kind == EngineEffectWriteChangeKind.NameSlot), "缺少名称槽写入项。");
            TestSupport.Assert(preview.Changes.Any(change => change.Kind == EngineEffectWriteChangeKind.Description), "缺少说明写入项。");
            TestSupport.Assert(preview.Changes.Count(change => change.Kind == EngineEffectWriteChangeKind.Push) == 4, "四个 push 没有全部进入预览。");
            TestSupport.Assert(preview.Changes.Any(change => change.Kind == EngineEffectWriteChangeKind.Entry), "缺少判定入口写入项。");
            foreach (var change in preview.Changes)
            {
                TestSupport.Assert(change.BeforeBytes.Length == change.AfterBytes.Length, "字节补丁改变了长度。");
                TestSupport.Assert(!change.BeforeBytes.AsSpan().SequenceEqual(change.AfterBytes), "预览包含无变化的补丁。");
            }

            var result = service.Commit(workspace, preview);
            TestSupport.Assert(result.Success, "写入失败：" + result.Message);
            TestSupport.Assert(result.AppliedChanges == preview.Changes.Count, "写入项数与预览不一致。");
            TestSupport.Assert(result.BackupPath != null && Directory.Exists(result.BackupPath), "缺少写前备份目录。");
            foreach (var name in new[] { "Ekd5.exe", "Imsg.e5" })
            {
                var backup = Path.Combine(result.BackupPath!, name);
                TestSupport.Assert(File.Exists(backup), $"备份缺少 {name}。");
                var expected = name == "Ekd5.exe" ? report.ExeSha256 : report.ImsgSha256;
                TestSupport.Assert(HashService.Sha256(backup).Equals(expected, StringComparison.OrdinalIgnoreCase),
                    $"备份 {name} 与写前 SHA 不一致。");
            }

            var after = new EngineEffectScanService().Scan(workspace);
            var moved = after.Effects.First(itemRow => itemRow.EffectId == personal);
            var movedCall = moved.CallSiteDetails.FirstOrDefault(call => call.CallAddress == site.CallAddress);
            TestSupport.Assert(movedCall != null, "个人特效号修改后调用点没有移动到新 ID。");
            TestSupport.Assert(movedCall!.ItemEffectId == item, "装备特效号复读不一致。");
            TestSupport.Assert(movedCall.ValueMode == request.NewCalls[site.CallAddress].ValueMode, "效果值模式复读不一致。");
            TestSupport.Assert(movedCall.StackingMode == request.NewCalls[site.CallAddress].StackingMode, "叠加方式复读不一致。");
            TestSupport.Assert(movedCall.EntryTarget == entry, "判定入口复读不一致。");
            var original = after.Effects.First(itemRow => itemRow.EffectId == row.EffectId);
            TestSupport.Assert(original.Name == "测试特效", "名称复读不一致。");
            TestSupport.Assert(original.SuffixByte == suffix, "后缀复读不一致。");
            TestSupport.Assert(original.SuffixLabel.Contains(EngineEffectSuffixLabels.Name(report.EngineVersion, suffix), StringComparison.Ordinal), "后缀标签不一致。");
            TestSupport.Assert(original.Description == "测试说明", "说明复读不一致。");
            TestSupport.Assert(!original.HasCalls, "原名称行仍绑定已移动的调用点。");
            TestSupport.Assert(!after.ExeSha256.Equals(report.ExeSha256, StringComparison.OrdinalIgnoreCase), "Ekd5.exe 未改变。");
            TestSupport.Assert(!after.ImsgSha256.Equals(report.ImsgSha256, StringComparison.OrdinalIgnoreCase), "Imsg.e5 未改变。");

            TestSupport.Assert(result.History != null, "写入没有历史记录。");
            var undo = workspace.UndoSaved(result.History!.OperationId);
            TestSupport.Assert(undo.Success, "历史撤回失败：" + undo.Message);
            var restored = new EngineEffectScanService().Scan(workspace);
            TestSupport.Assert(restored.ExeSha256 == report.ExeSha256, "撤回后 Ekd5.exe SHA 未恢复。");
            TestSupport.Assert(restored.ImsgSha256 == report.ImsgSha256, "撤回后 Imsg.e5 SHA 未恢复。");
            var restoredRow = restored.Effects.First(itemRow => itemRow.EffectId == row.EffectId);
            TestSupport.Assert(restoredRow.Name == row.Name && restoredRow.SuffixByte == row.SuffixByte, "撤回后名称/后缀未恢复。");
            var restoredCall = restored.Effects.SelectMany(itemRow => itemRow.CallSiteDetails)
                .FirstOrDefault(call => call.CallAddress == site.CallAddress);
            TestSupport.Assert(restoredCall != null && restoredCall.PersonalEffectId == site.PersonalEffectId, "撤回后调用点未恢复。");

            RunConflictTest(service, workspace, restored);
            RunInvalidTests(service, workspace, restored);
            RunCallOnlyPreviewTests(service, workspace, restored);
            RunComputedWriteTest(sourceRoot);
            RunDerivedWriteTest(sourceRoot);
            RunCodeCaveWriteTest(sourceRoot);
            RunTableWriteTest(sourceRoot);
            RunNameSlot15Test(sourceRoot);
            RunCombinedNameSuffixTest(sourceRoot);
            Console.WriteLine("ENGINE_EFFECT_WRITE_TESTS_OK");
        }
        finally
        {
            Directory.Delete(copy, true);
            foreach (var pair in sourceHashes)
                TestSupport.Assert(HashService.Sha256(Path.Combine(sourceRoot, pair.Key)) == pair.Value,
                    $"真实基底 {pair.Key} 被测试修改。");
        }
    }

    private static void RunConflictTest(EngineEffectWriteService service, ProjectWorkspace workspace, EngineEffectScanReport report)
    {
        var row = report.Effects.First(item => item.HasNameSlot);
        var preview = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
        {
            EffectId = row.EffectId,
            NewSuffix = (byte)((row.SuffixByte!.Value + 1) % (EngineEffectSuffixLabels.MaximumSuffix(report.EngineVersion) + 1))
        });
        TestSupport.Assert(preview.CanWrite, "冲突测试的预览被拒绝。");
        var path = workspace.RootPath + Path.DirectorySeparatorChar + "Ekd5.exe";
        var original = File.ReadAllBytes(path);
        var modified = (byte[])original.Clone();
        modified[0x100] ^= 0xFF;
        File.WriteAllBytes(path, modified);
        var result = service.Commit(workspace, preview);
        TestSupport.Assert(!result.Success, "外部修改后仍然写入成功。");
        TestSupport.Assert(result.Message.Contains("SHA", StringComparison.OrdinalIgnoreCase) ||
                            result.Message.Contains("外部修改", StringComparison.Ordinal), "冲突提示不明确：" + result.Message);
        File.WriteAllBytes(path, original);
        workspace.ReloadLoadedDocuments();
    }

    private static void RunInvalidTests(EngineEffectWriteService service, ProjectWorkspace workspace, EngineEffectScanReport report)
    {
        var row = report.Effects.First(item => item.HasCalls && item.HasNameSlot && item.HasDescriptionSlot);
        var site = row.CallSiteDetails[0];
        void AssertRejected(string name, EngineEffectWriteRequest request)
        {
            var preview = service.BuildPreview(workspace, report, request);
            TestSupport.Assert(!preview.CanWrite && preview.Errors.Count > 0, $"{name} 没有被拒绝。");
        }
        AssertRejected("名称超长", new() { EffectId = row.EffectId, NewName = new string('测', 20) });
        AssertRejected("说明超长", new() { EffectId = row.EffectId, NewDescription = new string('测', 200) });
        AssertRejected("后缀越界", new() { EffectId = row.EffectId, NewSuffix = 0x15 });
        AssertRejected("效果值模式越界", new()
        {
            EffectId = row.EffectId,
            NewCalls = new Dictionary<uint, EngineEffectCallEdit> { [site.CallAddress] = new() { CallAddress = site.CallAddress, ValueMode = 256 } }
        });
        AssertRejected("叠加方式越界", new()
        {
            EffectId = row.EffectId,
            NewCalls = new Dictionary<uint, EngineEffectCallEdit> { [site.CallAddress] = new() { CallAddress = site.CallAddress, StackingMode = 4 } }
        });
        AssertRejected("装备特效号越界", new()
        {
            EffectId = row.EffectId,
            NewCalls = new Dictionary<uint, EngineEffectCallEdit> { [site.CallAddress] = new() { CallAddress = site.CallAddress, ItemEffectId = 256 } }
        });
        AssertRejected("个人特效号越界", new()
        {
            EffectId = row.EffectId,
            NewCalls = new Dictionary<uint, EngineEffectCallEdit> { [site.CallAddress] = new() { CallAddress = site.CallAddress, PersonalEffectId = 256 } }
        });
        AssertRejected("判定入口未知", new()
        {
            EffectId = row.EffectId,
            NewCalls = new Dictionary<uint, EngineEffectCallEdit> { [site.CallAddress] = new() { CallAddress = site.CallAddress, EntryTarget = 0x00410000 } }
        });
        var dynamicCall = report.UnassignedCalls.FirstOrDefault(call => !call.PersonalPush.WritePlan.IsWritable &&
                                                                        call.PersonalPush.SourceKind != EngineEffectArgumentSource.Immediate);
        if (dynamicCall != null)
            AssertRejected("动态只读参数", new()
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [dynamicCall.CallAddress] = new() { CallAddress = dynamicCall.CallAddress, PersonalEffectId = 1 }
                }
            });
        var internalCall = report.InternalCalls.FirstOrDefault();
        if (internalCall != null)
            AssertRejected("引擎内部调用", new()
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [internalCall.CallAddress] = new() { CallAddress = internalCall.CallAddress, ValueMode = 0 }
                }
            });
        var stackCall = report.UnassignedCalls.FirstOrDefault(call =>
            call.PersonalPush.SourceKind == EngineEffectArgumentSource.StackParameter);
        if (stackCall != null)
            AssertRejected("上层参数", new()
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [stackCall.CallAddress] = new() { CallAddress = stackCall.CallAddress, PersonalEffectId = 1 }
                }
            });
        var loopCall = report.LoopCalls.FirstOrDefault();
        if (loopCall != null)
        {
            var preview = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [loopCall.CallAddress] = new()
                    {
                        CallAddress = loopCall.CallAddress, Identity = loopCall.Identity,
                        PersonalEffectId = loopCall.PersonalEffectId == 6 ? 7 : 6
                    }
                }
            });
            TestSupport.Assert(!preview.CanWrite &&
                              preview.Errors.Any(error => error.Contains("循环", StringComparison.Ordinal)),
                "循环/二级包装调用写入没有被拒绝。");
        }
        var tableCall = report.TableCalls.FirstOrDefault();
        if (tableCall != null)
        {
            var preview = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [tableCall.CallAddress] = new()
                    {
                        CallAddress = tableCall.CallAddress,
                        TableIndex = tableCall.TableIndex,
                        PersonalEffectId = tableCall.PersonalEffectId == 6 ? 7 : 6
                    }
                }
            });
            TestSupport.Assert(!preview.CanWrite &&
                              preview.Errors.Any(error => error.Contains("只能修改表项装备号", StringComparison.Ordinal)),
                "表驱动个人号写入没有被拒绝。");
        }
        var memoryCall = report.UnassignedCalls.FirstOrDefault(call =>
            call.ItemPush.SourceKind == EngineEffectArgumentSource.Memory);
        if (memoryCall != null)
            AssertRejected("内存参数", new()
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [memoryCall.CallAddress] = new() { CallAddress = memoryCall.CallAddress, ItemEffectId = 1 }
                }
            });
    }

    private static void RunCallOnlyPreviewTests(EngineEffectWriteService service, ProjectWorkspace workspace,
        EngineEffectScanReport report)
    {
        var call = report.UnassignedCalls.FirstOrDefault(item => item.ItemPush.SourceKind == EngineEffectArgumentSource.Immediate &&
                                                                 item.ItemPush.WritePlan.IsWritable &&
                                                                 item.ItemPush.Value is >= 0 and <= 200);
        if (call == null) return;
        var newValue = (int)call.ItemPush.Value!.Value + 1;
        var preview = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
        {
            EffectId = -1,
            NewCalls = new Dictionary<uint, EngineEffectCallEdit>
            {
                [call.CallAddress] = new() { CallAddress = call.CallAddress, ItemEffectId = newValue }
            }
        });
        TestSupport.Assert(preview.CanWrite, "未归属调用的已知参数预览被拒绝：" + string.Join("；", preview.Errors));
        TestSupport.Assert(preview.Changes.Count == 1, "未归属调用预览项数不正确。");
        TestSupport.Assert(preview.EffectId == -1 && preview.FollowEffectId == null, "调用点级预览不应跟随特效 ID。");
    }

    private static void RunTableWriteTest(string sourceRoot)
    {
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var service = new EngineEffectWriteService();
            var report = new EngineEffectScanService().Scan(workspace);
            var site = report.TableCalls.FirstOrDefault(call => call.TableIndex == 0);
            if (site == null) return;
            var newItem = (site.ItemEffectId ?? 0) == byte.MaxValue
                ? (int)(site.ItemEffectId ?? 0) - 1
                : (int)(site.ItemEffectId ?? 0) + 1;
            TestSupport.Assert(site.ItemPush.WritePlan.IsWritable, "表项装备号不可写。");
            var preview = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [site.CallAddress] = new()
                    {
                        CallAddress = site.CallAddress,
                        TableIndex = site.TableIndex,
                        ItemEffectId = newItem
                    }
                }
            });
            TestSupport.Assert(preview.CanWrite, "表项写入预览被拒绝：" + string.Join("；", preview.Errors));
            TestSupport.Assert(preview.Changes.Count == 1 &&
                              preview.Changes[0].FileOffset == site.ItemPush.WritePlan.ImmediateFileOffset,
                "表项补丁没有落在静态表字节上。");
            var result = service.Commit(workspace, preview);
            TestSupport.Assert(result.Success, "表项写入失败：" + result.Message);
            var after = new EngineEffectScanService().Scan(workspace);
            var again = after.TableCalls.First(call => call.TableIndex == 0 &&
                                                        call.TableAddress == site.TableAddress);
            TestSupport.Assert(again.ItemEffectId == newItem, "表项写入后没有复读为新值。");
            var sibling = after.TableCalls.FirstOrDefault(call => call.TableIndex == 1 &&
                                                                   call.TableAddress == site.TableAddress);
            if (sibling != null)
                TestSupport.Assert(sibling.ItemEffectId == report.TableCalls
                        .First(call => call.TableIndex == 1 && call.TableAddress == site.TableAddress).ItemEffectId,
                    "表项写入影响了相邻表项。");
            var undo = workspace.UndoSaved(result.History!.OperationId);
            TestSupport.Assert(undo.Success, "表项历史撤回失败：" + undo.Message);
            var restored = new EngineEffectScanService().Scan(workspace);
            var restoredTable = restored.TableCalls.First(call => call.TableIndex == 0 &&
                                                                   call.TableAddress == site.TableAddress);
            TestSupport.Assert(restoredTable.ItemEffectId == site.ItemEffectId, "撤回后表项没有恢复原值。");
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    /// <summary>
    /// A 15-byte name slot ends at +0x0F. Renaming must clear all 15 payload bytes,
    /// keep the slot terminated, preserve the zero suffix byte, and only then allow
    /// a nonzero suffix to be set (which would re-limit the name to 14 bytes).
    /// </summary>
    private static void RunNameSlot15Test(string sourceRoot)
    {
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var service = new EngineEffectWriteService();
            var slots = new EngineEffectSlotService();
            var report = new EngineEffectScanService().Scan(workspace);
            var exeBytes = File.ReadAllBytes(Path.Combine(copy, "Ekd5.exe"));
            var row = report.Effects.FirstOrDefault(item => item.HasNameSlot &&
                Array.IndexOf(slots.ReadNameSlot(workspace, item.EffectId, exeBytes).RawBytes, (byte)0) == 15);
            if (row == null) return; // 6.6 has no 15-byte name slots.
            TestSupport.Assert(row.SuffixByte == 0, $"15 字节名称槽 {row.EffectId} 的 +0x0F 应为结束符 0。");
            TestSupport.Assert(row.Name.Length >= 7, $"15 字节名称槽 {row.EffectId} 解码过短：{row.Name}");
            var blocked = slots.PreviewSuffix(workspace, row.EffectId, 4);
            TestSupport.Assert(blocked.ValidationError != null &&
                               blocked.ValidationError.Contains("先缩短名称", StringComparison.Ordinal),
                "15 字节名称槽在未缩短名称时仍允许写入非零后缀。");
            var rename = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
            {
                EffectId = row.EffectId, NewName = "改名测试"
            });
            TestSupport.Assert(rename.CanWrite, "15 字节名称槽改名预览被拒绝：" + string.Join("；", rename.Errors));
            var nameChange = rename.Changes.Single(change => change.Kind == EngineEffectWriteChangeKind.NameSlot);
            TestSupport.Assert(nameChange.AfterBytes.Length == 16 && nameChange.AfterBytes[15] == 0 &&
                               nameChange.AfterBytes[8] == 0 &&
                               nameChange.AfterBytes.Take(8).SequenceEqual(
                                   System.Text.Encoding.GetEncoding(936).GetBytes("改名测试")),
                "改名后名称槽未清空 15 字节或未保留 +0x0F 结束符。");
            var committed = service.Commit(workspace, rename);
            TestSupport.Assert(committed.Success, "15 字节名称槽改名写入失败：" + committed.Message);
            var after = new EngineEffectScanService().Scan(workspace);
            var renamed = after.Effects.First(item => item.EffectId == row.EffectId);
            TestSupport.Assert(renamed.Name == "改名测试" && renamed.SuffixByte == 0,
                $"改名复读不符：{renamed.Name}/{renamed.SuffixByte}");
            TestSupport.Assert(renamed.NameCapacity >= 15, "改名后容量没有恢复为可写满 15 字节。");
            var suffix = service.BuildPreview(workspace, after, new EngineEffectWriteRequest
            {
                EffectId = row.EffectId, NewSuffix = 4
            });
            TestSupport.Assert(suffix.CanWrite, "缩短名称后仍不能设置后缀：" + string.Join("；", suffix.Errors));
            var suffixCommit = service.Commit(workspace, suffix);
            TestSupport.Assert(suffixCommit.Success, "后缀写入失败：" + suffixCommit.Message);
            var final = new EngineEffectScanService().Scan(workspace);
            var finalRow = final.Effects.First(item => item.EffectId == row.EffectId);
            TestSupport.Assert(finalRow.SuffixByte == 4 && finalRow.Name == "改名测试",
                $"后缀写入复读不符：{finalRow.Name}/{finalRow.SuffixByte}");
            TestSupport.Assert(finalRow.NameCapacity == 14, "写入非零后缀后容量应回到 14。");
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    /// <summary>
    /// A 15-byte name written together with a nonzero suffix must be rejected:
    /// the name needs a NUL before +0x0F, otherwise the engine reads past the slot.
    /// A 14-byte name with the same suffix is valid.
    /// </summary>
    private static void RunCombinedNameSuffixTest(string sourceRoot)
    {
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var slots = new EngineEffectSlotService();
            var report = new EngineEffectScanService().Scan(workspace);
            var row = report.Effects.FirstOrDefault(item =>
            {
                if (!item.HasNameSlot || item.SuffixByte != 0 || item.NameCapacity != 15) return false;
                var raw = slots.ReadNameSlot(workspace, item.EffectId).RawBytes;
                var terminator = Array.IndexOf(raw, (byte)0);
                return terminator is >= 0 and < 15;
            });
            if (row == null) return;

            var service = new EngineEffectWriteService();
            var blocked = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
            {
                EffectId = row.EffectId,
                NewName = "测试名称十五字A",
                NewSuffix = 4
            });
            TestSupport.Assert(!blocked.CanWrite &&
                               blocked.Errors.Any(error => error.Contains("15 字节", StringComparison.Ordinal) ||
                                                           error.Contains("缩短", StringComparison.Ordinal)),
                "15 字节名称 + 非零后缀未被拒绝：" + string.Join("；", blocked.Errors));

            var allowed = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
            {
                EffectId = row.EffectId,
                NewName = "测试名称十四字",
                NewSuffix = 4
            });
            TestSupport.Assert(allowed.CanWrite,
                "14 字节名称 + 非零后缀被拒绝：" + string.Join("；", allowed.Errors));
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    private static void RunCodeCaveWriteTest(string sourceRoot)
    {
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var service = new EngineEffectWriteService();
            var report = new EngineEffectScanService().Scan(workspace);
            var site = report.Effects.SelectMany(row => row.CallSiteDetails)
                .FirstOrDefault(call => call.CallAddress == 0x004D0023);
            if (site == null) return;
            TestSupport.Assert(site.RedirectSourceAddress == 0x004CFFFD,
                "代码洞调用的跳转来源不是 0x004CFFFD。");
            TestSupport.Assert(site.PersonalEffectId == 159 && site.PersonalPush.WritePlan.IsWritable,
                "代码洞调用的个人号 159 不可写。");
            var preview = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [site.CallAddress] = new() { CallAddress = site.CallAddress, PersonalEffectId = 160 }
                }
            });
            TestSupport.Assert(preview.CanWrite, "代码洞调用写入预览被拒绝：" + string.Join("；", preview.Errors));
            TestSupport.Assert(preview.Changes.Count == 1 &&
                              preview.Changes[0].FileOffset == site.PersonalPush.WritePlan.ImmediateFileOffset,
                "代码洞调用补丁没有落在跳转前的个人号立即数上。");
            var result = service.Commit(workspace, preview);
            TestSupport.Assert(result.Success, "代码洞调用写入失败：" + result.Message);
            var after = new EngineEffectScanService().Scan(workspace);
            var again = after.Effects.SelectMany(row => row.CallSiteDetails)
                .FirstOrDefault(call => call.CallAddress == site.CallAddress);
            TestSupport.Assert(again is { PersonalEffectId: 160 }, "代码洞调用写入后没有复读为特效 160。");
            var undo = workspace.UndoSaved(result.History!.OperationId);
            TestSupport.Assert(undo.Success, "代码洞调用历史撤回失败：" + undo.Message);
            var restored = new EngineEffectScanService().Scan(workspace);
            var restoredCall = restored.Effects.SelectMany(row => row.CallSiteDetails)
                .FirstOrDefault(call => call.CallAddress == site.CallAddress);
            TestSupport.Assert(restoredCall is { PersonalEffectId: 159 }, "撤回后代码洞调用没有恢复为特效 159。");
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    private static void RunDerivedWriteTest(string sourceRoot)
    {
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var service = new EngineEffectWriteService();
            var report = new EngineEffectScanService().Scan(workspace);
            var site = report.DerivedCalls.FirstOrDefault(call =>
                call.WrapperStubCallAddress == 0x004E0010 && call.PersonalEffectId == 196);
            if (site == null) return;
            var sibling = report.DerivedCalls.FirstOrDefault(call =>
                call.WrapperStubCallAddress == 0x004E0025 && call.CallAddress == site.CallAddress);
            TestSupport.Assert(sibling != null, "缺少同一调用者的第二个派生调用。");
            TestSupport.Assert(!site.ValuePush.WritePlan.IsWritable &&
                              site.ValuePush.WritePlan.BlockReason?.Contains("共享", StringComparison.Ordinal) == true,
                "派生调用共享的效果值参数应标记为只读。");
            TestSupport.Assert(site.PersonalPush.WritePlan.IsWritable, "派生调用独有的个人号应可写。");
            var preview = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [site.CallAddress] = new() { CallAddress = site.CallAddress, Identity = site.Identity, PersonalEffectId = 197 }
                }
            });
            TestSupport.Assert(preview.CanWrite, "派生调用写入预览被拒绝：" + string.Join("；", preview.Errors));
            TestSupport.Assert(preview.Changes.Count == 1, "派生调用预览项数不正确。");
            var change = preview.Changes[0];
            TestSupport.Assert(change.FileOffset == site.PersonalPush.WritePlan.ImmediateFileOffset,
                "派生调用补丁没有落在调用者个人号立即数上。");
            TestSupport.Assert(change.BeforeBytes.AsSpan().SequenceEqual(new byte[] { 0xC4, 0, 0, 0 }) &&
                               change.AfterBytes.AsSpan().SequenceEqual(new byte[] { 0xC5, 0, 0, 0 }),
                "派生调用补丁字节不正确。");
            var result = service.Commit(workspace, preview);
            TestSupport.Assert(result.Success, "派生调用写入失败：" + result.Message);
            var after = new EngineEffectScanService().Scan(workspace);
            var again = after.DerivedCalls.First(call =>
                call.CallAddress == site.CallAddress && call.WrapperStubCallAddress == 0x004E0010);
            TestSupport.Assert(again.PersonalEffectId == 197, "写入后派生调用个人号没有复读为 197。");
            var undo = workspace.UndoSaved(result.History!.OperationId);
            TestSupport.Assert(undo.Success, "派生调用历史撤回失败：" + undo.Message);
            var restored = new EngineEffectScanService().Scan(workspace);
            var restoredCall = restored.DerivedCalls.First(call =>
                call.CallAddress == site.CallAddress && call.WrapperStubCallAddress == 0x004E0010);
            TestSupport.Assert(restoredCall.PersonalEffectId == 196, "撤回后派生调用个人号没有恢复为 196。");
            var entryPreview = service.BuildPreview(workspace, restored, new EngineEffectWriteRequest
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [site.CallAddress] = new() { CallAddress = site.CallAddress, Identity = site.Identity,
                        EntryTarget = EngineEffectScanService.CoreEntry }
                }
            });
            TestSupport.Assert(!entryPreview.CanWrite &&
                              entryPreview.Errors.Any(error => error.Contains("派生调用", StringComparison.Ordinal)),
                "派生调用改写判定入口没有被拒绝。");
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    private static void RunComputedWriteTest(string sourceRoot)
    {
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var service = new EngineEffectWriteService();
            var report = new EngineEffectScanService().Scan(workspace);
            var site = report.UnassignedCalls.FirstOrDefault(call => call.CallAddress == 0x0043D037);
            if (site == null) return;
            var argument = site.ItemPush;
            TestSupport.Assert(argument.ResolvedValue == 0x66, "安全计算立即数没有解析为 0x66。");
            TestSupport.Assert(argument.WritePlan.IsWritable && argument.WritePlan.ImmediateLength == 1,
                "安全计算立即数没有生成 1 字节写回计划。");
            TestSupport.Assert(argument.WritePlan.Definition?.Address == 0x0043D02C,
                "安全计算立即数的定义地址不是 mov cl,0x66。");
            TestSupport.Assert(argument.WritePlan.BeforeBytes.AsSpan().SequenceEqual(new byte[] { 0x66 }),
                "安全计算立即数的原字节不是 0x66。");
            var preview = service.BuildPreview(workspace, report, new EngineEffectWriteRequest
            {
                EffectId = -1,
                NewCalls = new Dictionary<uint, EngineEffectCallEdit>
                {
                    [site.CallAddress] = new() { CallAddress = site.CallAddress, ItemEffectId = 0x67 }
                }
            });
            TestSupport.Assert(preview.CanWrite, "安全计算立即数预览被拒绝：" + string.Join("；", preview.Errors));
            TestSupport.Assert(preview.Changes.Count == 1, "安全计算立即数预览项数不正确。");
            var change = preview.Changes[0];
            TestSupport.Assert(change.FileOffset == argument.WritePlan.ImmediateFileOffset, "补丁偏移不是定义立即数。");
            TestSupport.Assert(change.BeforeBytes.AsSpan().SequenceEqual(new byte[] { 0x66 }) &&
                               change.AfterBytes.AsSpan().SequenceEqual(new byte[] { 0x67 }), "安全计算立即数补丁字节不正确。");
            var result = service.Commit(workspace, preview);
            TestSupport.Assert(result.Success, "安全计算立即数写入失败：" + result.Message);
            var after = new EngineEffectScanService().Scan(workspace);
            var again = after.UnassignedCalls.First(call => call.CallAddress == site.CallAddress);
            TestSupport.Assert(again.ItemPush.ResolvedValue == 0x67, "写入后定义立即数没有复读为 0x67。");
            TestSupport.Assert(again.PersonalPush.ResolvedValue is null, "第 4 参数不应被本次写入解析。");
            TestSupport.Assert(result.History != null, "安全计算立即数写入没有历史记录。");
            var undo = workspace.UndoSaved(result.History!.OperationId);
            TestSupport.Assert(undo.Success, "安全计算立即数历史撤回失败：" + undo.Message);
            var restored = new EngineEffectScanService().Scan(workspace);
            var restoredCall = restored.UnassignedCalls.FirstOrDefault(call => call.CallAddress == site.CallAddress);
            TestSupport.Assert(restoredCall != null && restoredCall.ItemPush.ResolvedValue == 0x66,
                "撤回后定义立即数没有恢复为 0x66。");
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }
}
