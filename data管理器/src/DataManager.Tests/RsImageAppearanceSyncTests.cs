using System.Buffers.Binary;
using System.Collections;
using DataManager.Core.Appearance;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Migration;
using DataManager.Core.Profiles;
using DataManager.Images;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class RsImageAppearanceSyncTests
{
    public static void Run(string basePath)
    {
        if (!File.Exists(Path.Combine(basePath, "Ekd5.exe")))
            basePath = Path.Combine(basePath, "基底", "加强版6.5未加密版");
        PayloadDifferencesIgnoreOffsets();
        SavedAssignmentsAndOneHistory(basePath);
        SpecialSAndMigrationProjection(basePath);
        LockedSaveDoesNotBlockOthers(basePath);
        UnsupportedAssignmentsDoNotBlockImages(basePath);
        WholeArchiveReplaceUsesTheSameTransaction(basePath);
        AddedArchiveMigrationUndoRedo(basePath);
        FailedHistoryRestoresPicturesAndSaves(basePath);
        LateHistoryFailurePreservesExternalSave(basePath);
        Console.WriteLine("RS_IMAGE_APPEARANCE_SYNC_TESTS_OK");
    }

    private static void PayloadDifferencesIgnoreOffsets()
    {
        var analyzer = new RsResourceImpactAnalyzer();
        var before = Archive([1], [2], [3], [4]);
        var replaced = E5Archive.Parse("Pmapobj.e5", before).Rebuild(
            replacementDecoded: new Dictionary<int, byte[]> { [1] = [8, 9, 10, 11, 12] });
        var impact = analyzer.Analyze([new("Pmapobj.e5", before, replaced)]);
        TestSupport.Assert(impact.AffectedRIds.SetEquals([0]) && impact.AffectedSPhysicalNumbers.Count == 0,
            "前面R载荷增长造成后续目录偏移变化时，同步范围错误扩大");
        var appended = E5Archive.Parse("Pmapobj.e5", before).Rebuild(appendDecoded: [[5], [6]]);
        TestSupport.Assert(analyzer.Analyze([new("Pmapobj.e5", before, appended)]).AffectedRIds.SetEquals([2]),
            "R追加误报已有形象变化");
        var deleted = E5Archive.Parse("Pmapobj.e5", before).Rebuild(deleteImageNumbers: [1, 2]);
        TestSupport.Assert(analyzer.Analyze([new("Pmapobj.e5", before, deleted)]).AffectedRIds.SetEquals([0, 1]),
            "R删除未覆盖前移槽和已消失尾槽");
        TestSupport.Assert(analyzer.Analyze([new("Pmapobj.e5", before, null)]).AffectedRIds.SetEquals([0, 1]),
            "整档删除没有标记原有R槽");
        TestSupport.Assert(!analyzer.Analyze([new("Face.e5", before, replaced), new("Pmapobj.e5", before, before)]).HasChanges,
            "非R/S或无变化包触发了同步");

        var compressed = Archive([0]);
        BinaryPrimitives.WriteInt32BigEndian(compressed.AsSpan(E5Archive.IndexOffset + 4), 4);
        compressed[16] = 7;
        var newDictionary = (byte[])compressed.Clone(); newDictionary[16] = 9;
        TestSupport.Assert(analyzer.Analyze([new("Unit_mov.e5", compressed, newDictionary)])
            .AffectedSPhysicalNumbers.SetEquals([1]), "只改变LS字典时漏掉了实际解压载荷变化");
        Console.WriteLine("[RS_APPEARANCE] R正背、追加、删除、整档、无变化和LS字典差异范围通过");
    }

    private static void SavedAssignmentsAndOneHistory(string basePath) => WithProject(basePath, fixture =>
    {
        var draft = new ProjectWorkspace(); draft.Open(fixture.Root);
        draft.GetDocument("person-r-assign").Data.Rows[0]["R形象ID"] = 1;
        var imageBefore = File.ReadAllBytes(fixture.RPath);
        var saveBefore = File.ReadAllBytes(fixture.SavePath);
        var expected = (byte[])saveBefore.Clone(); Write16(expected, fixture.Layout.ROffset, 0);
        var result = ProjectImageHistory.Commit(fixture.Profile, Replace(fixture.RPath, 1, [19, 20, 21]));
        TestSupport.Assert(result.Success, "图片与存档保存失败：" + string.Join("；", result.Diagnostics));
        TestSupport.Assert(File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(expected),
            "图片保存没有只同步已保存指定命中的人物R，或改动了其他剧情R/S和正文");
        TestSupport.Assert(Convert.ToInt32(draft.GetDocument("person-r-assign").Data.Rows[0]["R形象ID"]) == 1 && draft.IsDirty,
            "图片立即写入提前保存/丢弃了数据草稿");
        var record = ProjectImageHistory.List(fixture.Profile).Single();
        TestSupport.Assert(record.Targets.Count == 2 && record.Targets.Any(target =>
            target.TargetPath.Equals(fixture.SavePath, StringComparison.OrdinalIgnoreCase)), "存档未加入同一图片历史");
        TestSupport.Assert(result.Summary.Contains("已更新 1 个存档", StringComparison.Ordinal), "图片成功结果未显示存档同步统计");
        var imageAfter = File.ReadAllBytes(fixture.RPath);
        var history = new ResourceHistoryAdapter();
        TestSupport.Assert(history.Undo(fixture.Root, result.HistoryId!).Success &&
            File.ReadAllBytes(fixture.RPath).AsSpan().SequenceEqual(imageBefore) &&
            File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(saveBefore), "一次图片撤回未同时恢复存档");
        TestSupport.Assert(history.Redo(fixture.Root, result.HistoryId!).Success &&
            File.ReadAllBytes(fixture.RPath).AsSpan().SequenceEqual(imageAfter) &&
            File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(expected), "一次图片重做未同时恢复存档");
        Console.WriteLine("[RS_APPEARANCE] 真实6.5存档、只更新人物R、保留草稿、同一历史撤回重做通过");
    });

    private static void SpecialSAndMigrationProjection(string basePath) => WithProject(basePath, fixture =>
    {
        var sPath = Path.Combine(fixture.Root, "Unit_atk.e5");
        var before = File.ReadAllBytes(fixture.SavePath);
        var expected = (byte[])before.Clone(); Write16(expected, fixture.Layout.SOffset, 1);
        var plan = Replace(sPath, 242, [31, 32]); // Compact S=1 owns physical 241, 242 and 243.
        var stage = Path.Combine(fixture.Root, "appearance-staging"); Directory.CreateDirectory(stage);
        foreach (var name in Known65FixtureSnapshot.CoreNames) File.Copy(Path.Combine(fixture.Root, name), Path.Combine(stage, name));
        File.WriteAllBytes(Path.Combine(stage, "Unit_atk.e5"), plan.Files.Single().CandidateBytes);
        var projected = new ImageMigrationResourceConverter().GetAppearanceUpdates(stage, fixture.Root, ["Unit_atk.e5"]);
        TestSupport.Assert(projected.Count == 1 && projected[0].PersonId == 0 && projected[0].RId == null && projected[0].SId == 1,
            "迁移资源差异没有按最终人物紧凑S指定匹配物理转职槽");
        var result = ProjectImageHistory.Commit(fixture.Profile, plan);
        TestSupport.Assert(result.Success && File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(expected),
            "特殊S动作变更错误扩大了人物/字段范围或把物理Unit编号写成S指定");

        var workspace = new ProjectWorkspace(); workspace.Open(fixture.Root);
        workspace.GetDocument("person-s-assign").Data.Rows[1]["S形象ID"] = 0;
        workspace.GetDocument("persons").Data.Rows[1]["兵种"] = 4;
        var update = new RsResourceImpactAnalyzer().CreatePersonUpdates(workspace,
            new RsResourceImpact(new HashSet<int>(), new HashSet<int> { 15 })); // Job 4's enemy slot.
        TestSupport.Assert(update.Count == 1 && update[0].PersonId == 1 && update[0].SId == 0 && update[0].RId == null,
            "S=0的兵种阵营物理槽未映射回对应人物");
        Console.WriteLine("[RS_APPEARANCE] S三转/兵种阵营映射、只写S、迁移公开接口通过");
    });

    private static void LockedSaveDoesNotBlockOthers(string basePath) => WithProject(basePath, fixture =>
    {
        var lockedPath = Path.Combine(fixture.Root, "SV", "SV002.E5S");
        File.Copy(fixture.SavePath, lockedPath);
        var before = File.ReadAllBytes(lockedPath);
        MutationResult result;
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = ProjectImageHistory.Commit(fixture.Profile, Replace(fixture.RPath, 2, [54]));
        TestSupport.Assert(result.Success && Read16(File.ReadAllBytes(fixture.SavePath), fixture.Layout.ROffset) == 0 &&
            File.ReadAllBytes(lockedPath).AsSpan().SequenceEqual(before), "占用一个存档阻止了图片/其他健康存档保存，或写坏了占用档");
        var history = ProjectImageHistory.List(fixture.Profile).Single();
        TestSupport.Assert(history.Targets.Count == 2 && !history.Targets.Any(target =>
            target.TargetPath.Equals(lockedPath, StringComparison.OrdinalIgnoreCase)), "被跳过存档混入了成功图片历史");
        TestSupport.Assert(result.Diagnostics.Any(message => message.StartsWith("存档同步跳过：", StringComparison.Ordinal)) &&
            result.Diagnostics.Any(message => message.Contains("SV002.E5S", StringComparison.OrdinalIgnoreCase)),
            "部分同步没有提供可在界面显示的逐档诊断");
        Console.WriteLine("[RS_APPEARANCE] 单档占用不阻塞图片/其他存档，成功历史排除跳过组通过");
    });

    private static void FailedHistoryRestoresPicturesAndSaves(string basePath) => WithProject(basePath, fixture =>
    {
        var imageBefore = File.ReadAllBytes(fixture.RPath);
        var saveBefore = File.ReadAllBytes(fixture.SavePath);
        var canonical = ProjectImageHistory.Canonicalize(fixture.Profile);
        var history = ProjectImageHistory.Create(canonical);
        var journal = Path.Combine(history.BackupRoot(canonical), "History", "history.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        MutationResult result;
        using (var locked = new FileStream(journal, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            result = ProjectImageHistory.Commit(fixture.Profile, Replace(fixture.RPath, 1, [71, 72]));
        TestSupport.Assert(!result.Success && File.ReadAllBytes(fixture.RPath).AsSpan().SequenceEqual(imageBefore) &&
            File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(saveBefore), "图片历史最终写入失败没有回滚图片与成功同步的存档");
        TestSupport.Assert(ProjectImageHistory.List(fixture.Profile).Count == 0, "图片失败后留下成功同步历史");
        Console.WriteLine("[RS_APPEARANCE] 历史晚期失败回滚图片和存档通过");
    });

    private static void UnsupportedAssignmentsDoNotBlockImages(string basePath) => WithProject(basePath, fixture =>
    {
        var saveBefore = File.ReadAllBytes(fixture.SavePath);
        LegacyDynamicLayoutTests.InvalidateAssignmentConsumer(fixture.Root);
        var plan = Replace(fixture.RPath, 1, [81, 82]);
        var result = ProjectImageHistory.Commit(new ProjectProfileService().Detect(fixture.Root), plan);
        TestSupport.Assert(result.Success && File.ReadAllBytes(fixture.RPath).AsSpan().SequenceEqual(plan.Files[0].CandidateBytes) &&
            File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(saveBefore), "R指定证据不足阻塞了图片或改写了存档");
        TestSupport.Assert(result.Diagnostics.Any(message => message.StartsWith("存档同步跳过：", StringComparison.Ordinal)),
            "R指定证据不足没有提供跳过说明");
        TestSupport.Assert(ProjectImageHistory.List(fixture.Profile).Single().Targets.Count == 1,
            "证据不足时未同步的存档加入了图片历史");
        Console.WriteLine("[RS_APPEARANCE] 人物指定格式/证据无效只跳过同步、图片和历史正常通过");
    });

    private static void WholeArchiveReplaceUsesTheSameTransaction(string basePath) => WithProject(basePath, fixture =>
    {
        var source = Path.Combine(fixture.Root, "whole-Pmapobj.e5");
        File.WriteAllBytes(source, Replace(fixture.RPath, 1, [83, 84]).Files[0].CandidateBytes);
        var history = ProjectImageHistory.Create(ProjectImageHistory.Canonicalize(fixture.Profile));
        var plan = new ImageMutationService(history).PlanWholeFileReplace(fixture.Profile, fixture.RPath, source);
        var before = File.ReadAllBytes(fixture.SavePath);
        var expected = (byte[])before.Clone(); Write16(expected, fixture.Layout.ROffset, 0);
        var result = ProjectImageHistory.Commit(fixture.Profile, plan);
        TestSupport.Assert(result.Success && File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(expected) &&
            ProjectImageHistory.List(fixture.Profile).Single().Targets.Count == 2,
            "整资源替换没有接入同一图片与存档事务");
        Console.WriteLine("[RS_APPEARANCE] 整资源文件替换与同一存档历史通过");
    });

    private static void AddedArchiveMigrationUndoRedo(string basePath) => WithProject(basePath, fixture =>
    {
        var source = TestSupport.CreateCoreCopy(basePath);
        try
        {
            File.Copy(Path.Combine(fixture.Root, "Ekd5.exe"), Path.Combine(source, "Ekd5.exe"), true);
            var resourcePath = Path.Combine(fixture.Root, "Unit_atk.e5"); File.Delete(resourcePath);
            // Only S=1 overlaps the newly present 1..242 slots; S=2 uses 244..246.
            var resource = Archive(Enumerable.Range(1, 242).Select(_ => new byte[64 * 768]).ToArray());
            File.WriteAllBytes(Path.Combine(source, "Unit_atk.e5"), resource);
            var saveBefore = File.ReadAllBytes(fixture.SavePath);
            var expected = (byte[])saveBefore.Clone(); Write16(expected, fixture.Layout.SOffset, 1);
            var service = new MigrationService(new ImageMigrationResourceConverter());
            var plan = service.Preview(source, fixture.Root);
            var choices = plan.Changes.Select((change, index) => (change, index)).Where(item =>
                item.change.TableKey == "__resource__" && item.change.Kind == MigrationChangeKind.ResourceAdded &&
                item.change.TargetRelativePath?.Equals("Unit_atk.e5", StringComparison.OrdinalIgnoreCase) == true)
                .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            TestSupport.Assert(choices.Count == 1, "迁移预览未提供目标缺失的Unit_atk.e5新增项");
            var result = service.Apply(plan, choices);
            TestSupport.Assert(result.Success && result.AppearanceSync?.UpdatedSaves == 1 &&
                File.ReadAllBytes(resourcePath).AsSpan().SequenceEqual(resource) &&
                File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(expected),
                "实际新增资源迁移未同步相关人物S：" + string.Join("；", result.Messages));
            var workspace = new ProjectWorkspace(); workspace.Open(fixture.Root);
            TestSupport.Assert(workspace.UndoSaved().Success && !File.Exists(resourcePath) &&
                File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(saveBefore),
                "新增资源与存档同一次撤回未删除新包或恢复存档");
            TestSupport.Assert(workspace.RedoSaved().Success && File.ReadAllBytes(resourcePath).AsSpan().SequenceEqual(resource) &&
                File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(expected),
                "新增资源与存档同一次重做未重建新包或恢复同步存档");
            Console.WriteLine("[RS_APPEARANCE] 实际新增Unit资源迁移、同步存档、同次撤回删除新包及重做重建通过");
        }
        finally { Directory.Delete(source, true); }
    });

    private static void LateHistoryFailurePreservesExternalSave(string basePath) => WithProject(basePath, fixture =>
    {
        var imageBefore = File.ReadAllBytes(fixture.RPath);
        var savedBefore = File.ReadAllBytes(fixture.SavePath);
        var external = (byte[])savedBefore.Clone(); Write16(external, fixture.Layout.ROffset, 123);
        var session = SaveAppearanceSyncService.Prepare(fixture.Root, "ccz-6.5",
            File.ReadAllBytes(Path.Combine(fixture.Root, "Ekd5.exe")), [new PersonAppearanceUpdate(0, RId: 0)]);
        var history = ProjectImageHistory.Create(ProjectImageHistory.Canonicalize(fixture.Profile));
        var plan = Replace(fixture.RPath, 1, [85, 86]) with
        {
            Metadata = new LateHistoryFailureMetadata(() => File.WriteAllBytes(fixture.SavePath, external))
        };
        var result = new ImageMutationService(history).Commit(fixture.Profile, plan, directory =>
        {
            session.Commit();
            return session.AppliedFiles.Select((file, index) =>
            {
                var beforePath = Path.Combine(directory, $"companion_{index}.before");
                var afterPath = Path.Combine(directory, $"companion_{index}.after");
                File.WriteAllBytes(beforePath, file.BeforeBytes); File.WriteAllBytes(afterPath, file.AfterBytes);
                return new HistoryTarget
                {
                    TargetPath = Path.Combine(fixture.Root, file.RelativePath), SnapshotPath = beforePath, AfterSnapshotPath = afterPath,
                    BeforeSha256 = file.Change.BeforeHash, AfterSha256 = file.Change.AfterHash,
                    BeforeLength = file.BeforeBytes.Length, AfterLength = file.AfterBytes.Length,
                    BeforeExists = true, AfterExists = true, BeforeAttributes = file.BeforeAttributes, AfterAttributes = file.BeforeAttributes
                };
            }).ToArray();
        }, session.Rollback);
        try
        {
            TestSupport.Assert(!result.Success && File.ReadAllBytes(fixture.RPath).AsSpan().SequenceEqual(imageBefore) &&
                File.ReadAllBytes(fixture.SavePath).AsSpan().SequenceEqual(external),
                "历史晚期失败覆盖了同步后外部更新的存档，或未恢复图片");
            TestSupport.Assert(result.Diagnostics.Any(message => message.Contains("冲突", StringComparison.Ordinal)) &&
                session.RecoverySnapshotDirectory != null && File.Exists(Path.Combine(session.RecoverySnapshotDirectory, "manifest.json")),
                "外部存档冲突没有保留恢复快照和清晰诊断");
            Console.WriteLine("[RS_APPEARANCE] 历史晚期失败保留外部新存档、恢复图片并保留冲突快照通过");
        }
        finally { if (session.RecoverySnapshotDirectory != null) Directory.Delete(session.RecoverySnapshotDirectory, true); }
    });

    private sealed class LateHistoryFailureMetadata(Action beforeFailure) : IReadOnlyDictionary<string, string>
    {
        public int Count => 1;
        public IEnumerable<string> Keys => ["Failure"];
        public IEnumerable<string> Values => ["Failure"];
        public string this[string key] => "Failure";
        public bool ContainsKey(string key) => key == "Failure";
        public bool TryGetValue(string key, out string value) { value = "Failure"; return ContainsKey(key); }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            beforeFailure();
            throw new IOException("测试：存档已被游戏更新后，图片历史在持久化边界失败。");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed record Fixture(string Root, ProjectProfile Profile, SaveAppearanceLayoutProbe.Layout Layout,
        string RPath, string SavePath);

    private static void WithProject(string basePath, Action<Fixture> action)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var exePath = Path.Combine(root, "Ekd5.exe");
            var exe = File.ReadAllBytes(exePath);
            var layout = SaveAppearanceLayoutProbe.Inspect(exe, "ccz-6.5");
            TestSupport.Assert(layout.Verified, "图片同步真实fixture原生存档布局缺证：" + layout.Evidence);
            for (var id = 0; id < 1024; id++)
            {
                Write16(exe, layout.RExecutableOffset + id * 2, id == 0 ? (ushort)0 : (ushort)1);
                Write16(exe, layout.SExecutableOffset + id * 2, id == 0 ? (ushort)1 : (ushort)2);
            }
            File.WriteAllBytes(exePath, exe);
            var rPath = Path.Combine(root, "Pmapobj.e5"); File.WriteAllBytes(rPath, Archive([1], [2], [3], [4]));
            foreach (var name in new[] { "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5" })
                File.WriteAllBytes(Path.Combine(root, name), Archive(Enumerable.Range(1, 246).Select(number => new[] { (byte)number }).ToArray()));
            var saveDirectory = Path.Combine(root, "SV"); Directory.CreateDirectory(saveDirectory);
            var savePath = Path.Combine(saveDirectory, "SV001.E5S");
            var native = File.ReadAllBytes(Path.Combine(basePath, "SV", "SV001.E5S"));
            TestSupport.Assert(native.Length == 0x40650, "图片同步fixture应使用没有扩展尾部的真实6.5原生存档");
            Write16(native, layout.ROffset, 999); Write16(native, layout.ROffset + 2, 998);
            Write16(native, layout.SOffset, 997); Write16(native, layout.SOffset + 2, 996);
            File.WriteAllBytes(savePath, native);
            action(new Fixture(root, new ProjectProfileService().Detect(root), layout, rPath, savePath));
        }
        finally { Directory.Delete(root, true); }
    }

    private static MutationPlan Replace(string path, int number, byte[] payload)
        => new(Guid.NewGuid().ToString("N"), MutationKind.Replace, "测试R/S图片变更",
            [new MutationFile(path, E5Archive.OpenFresh(path).Rebuild(
                replacementDecoded: new Dictionary<int, byte[]> { [number] = payload }), "专项图片候选")], [], false);

    private static byte[] Archive(params byte[][] payloads)
    {
        var first = E5Archive.IndexOffset + payloads.Length * E5Archive.EntrySize;
        var bytes = new byte[first + payloads.Sum(payload => payload.Length)];
        var offset = first;
        for (var index = 0; index < payloads.Length; index++)
        {
            var at = E5Archive.IndexOffset + index * E5Archive.EntrySize;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + 4), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + 8), offset);
            payloads[index].CopyTo(bytes, offset); offset += payloads[index].Length;
        }
        return bytes;
    }

    private static void Write16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    private static ushort Read16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
}
