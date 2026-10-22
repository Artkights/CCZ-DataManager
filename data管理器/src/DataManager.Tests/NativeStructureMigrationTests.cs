using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Migration;
using DataManager.Core.Models;
using System.Buffers.Binary;
using System.Text;

namespace DataManager.Tests;

internal static class NativeStructureMigrationTests
{
    public static void Run(string basePath)
    {
        TestLegacyToFixedAndMixedTransaction(basePath);
        TestFixedToLegacyAndCapacity(basePath);
        TestHiddenTailAndInvalidSource(basePath);
        TestCustomizedTargetHistory(basePath);
        TestMergeAndStagingFailure(basePath);
        TestAssignmentHistorySurvivesStaging(basePath);
        TestReadOnlyAssignmentEvidenceSurvivesStaging(basePath);
    }

    private static void TestLegacyToFixedAndMixedTransaction(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            SeedFixedPool(target);
            var names = new EffectNamePoolService();
            var sourceWorkspace = Open(source, false);
            names.WriteItemName(sourceWorkspace, 0x1A, "迁移名称");
            new AccessoryJobGroupService().Write(sourceWorkspace, [[0, 1], [2, 3]]);
            var persons = sourceWorkspace.GetDocument("persons");
            persons.Data.Rows[0]["武力"] = Convert.ToInt32(persons.Data.Rows[0]["武力"]) + 1;
            TestSupport.Assert(sourceWorkspace.Save().Saved, "准备迁移人物字段失败");
            var targetBefore = File.ReadAllBytes(Path.Combine(target, "Ekd5.exe"));
            var targetGroup = new AccessoryJobGroupService().Read(target);
            var sourceHash = HashService.Sha256(Path.Combine(source, "Ekd5.exe"));
            var service = new MigrationService();
            var plan = service.Preview(source, target, "ccz-6.5", "65-standard-v1", "ccz-6.6", "66-standard-v1");
            var choices = Select(plan, change => IsNative(change) && change.TableKey == "job-series-accessory-groups" ||
                change.TableKey == "persons" && change.Id == 0 && change.FieldName == "武力");
            var result = service.Apply(plan, choices);
            TestSupport.Assert(result.Success && result.AppliedChanges == 2, "混合物理/辅助分组迁移失败：" + string.Join("；", result.Messages));
            var targetWorkspace = Open(target, true);
            TestSupport.Assert(names.Read(targetWorkspace, EffectNameChannel.Item).First().Name == "恢复HP和状态", "特效名称池不应随数值迁移覆盖目标词汇");
            TestSupport.Assert(Convert.ToInt32(targetWorkspace.GetDocument("persons").Data.Rows[0]["武力"]) == Convert.ToInt32(persons.Data.Rows[0]["武力"]), "专用事务刷新丢失了普通字段迁移");
            TestSupport.Assert(new AccessoryJobGroupService().Read(target).Groups[0].JobSeriesIds.SequenceEqual([0, 1]), "辅助分组未在目标位置复读");
            var after = File.ReadAllBytes(Path.Combine(target, "Ekd5.exe"));
            var allowed = new bool[after.Length];
            Array.Fill(allowed, true, checked((int)EffectNamePoolService.ItemFixedOffset66), 16);
            Array.Fill(allowed, true, checked((int)targetGroup.FileOffset), targetGroup.WritableLength);
            TestSupport.Assert(targetBefore.Zip(after).Select((pair, index) => pair.First == pair.Second || allowed[index]).All(value => value), "专用逻辑迁移改写了目标槽/安全区以外的原始字节");
            TestSupport.Assert(HashService.Sha256(Path.Combine(source, "Ekd5.exe")) == sourceHash, "迁移修改了源目录");
            TestSupport.Assert(targetWorkspace.GetHistory().Count == 2 && targetWorkspace.GetHistory().All(entry => entry.OperationKind == "一键迁移"), "混合迁移没有保留两项可撤回历史");
            TestSupport.Assert(targetWorkspace.UndoSaved().Success, "辅助分组迁移不可撤回");
            TestSupport.Assert(targetWorkspace.UndoSaved().Success && names.Read(targetWorkspace, EffectNameChannel.Item).First().Name == "恢复HP和状态", "撤回数值/分组时不应修改特效名称");
        });
    }

    private static void TestFixedToLegacyAndCapacity(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            SeedFixedPool(source);
            var names = new EffectNamePoolService();
            var sourceWorkspace = Open(source, true);
            names.WriteItemName(sourceWorkspace, 0x1A, "短名");
            var service = new MigrationService();
            var plan = service.Preview(source, target, "ccz-6.6", "66-standard-v1", "ccz-6.5", "65-standard-v1");
            var result = service.Apply(plan, Select(plan, change => change.TableKey == "item-effect-name-pool" && change.Id == 0x1A));
            TestSupport.Assert(result.Success && result.AppliedChanges == 0 && names.Read(Open(target, false), EffectNameChannel.Item).First().Name != "短名", "特效名称池差异不应生成迁移写入");

            names.WriteItemName(sourceWorkspace, 0x1B, "超出容量的名称");
            plan = service.Preview(source, target, "ccz-6.6", "66-standard-v1", "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(!plan.Changes.Any(change => change.TableKey is "item-effect-name-pool" or "effect-names"), "名称池容量/缺槽差异不应阻塞特效迁移");
        });
    }

    private static void TestHiddenTailAndInvalidSource(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            SeedFixedPool(source); SeedFixedPool(target);
            var names = new EffectNamePoolService();
            names.WriteItemName(Open(source, true), 0x1B, "较长名称");
            var path = Path.Combine(target, "Ekd5.exe");
            var bytes = File.ReadAllBytes(path);
            var offset = checked((int)EffectNamePoolService.ItemFixedOffset66) + 16;
            bytes[offset] = (byte)'A'; bytes[offset + 1] = 0; bytes[offset + 2] = 0x7A;
            File.WriteAllBytes(path, bytes);
            var plan = new MigrationService().Preview(source, target, "ccz-6.6", "66-standard-v1", "ccz-6.6", "66-standard-v1");
            TestSupport.Assert(!plan.Changes.Any(change => change.TableKey is "item-effect-name-pool" or "effect-names"), "名称池隐藏尾部差异不应阻塞特效迁移");

            path = Path.Combine(source, "Ekd5.exe"); bytes = File.ReadAllBytes(path);
            var groups = new AccessoryJobGroupService().Read(source);
            bytes[checked((int)groups.FileOffset) + 1] = 0x80;
            File.WriteAllBytes(path, bytes);
            plan = new MigrationService().Preview(source, target, "ccz-6.6", "66-standard-v1", "ccz-6.6", "66-standard-v1");
            TestSupport.Assert(plan.Changes.Any(change => change.TableKey == "job-series-accessory-groups" && change.Kind == MigrationChangeKind.Incompatible && change.Reason!.Contains("错误", StringComparison.Ordinal)), "非法源辅助分组被标记为可写");
        });
    }

    private static void TestCustomizedTargetHistory(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            SeedFixedPool(source); SeedFixedPool(target);
            var names = new EffectNamePoolService();
            var targetWorkspace = Open(target, true);
            foreach (var slot in new[] { 0, 28, 56, 72 }) names.WriteItemName(targetWorkspace, 0x1A + slot, "自定义");
            var priorCount = targetWorkspace.GetHistory().Count;
            names.WriteItemName(Open(source, true), 0x1B, "迁移");
            var service = new MigrationService();
            var plan = service.Preview(source, target, "ccz-6.6", "66-standard-v1", "ccz-6.6", "66-standard-v1");
            var result = service.Apply(plan, Select(plan, change => change.TableKey == "item-effect-name-pool" && change.Id == 0x1B));
            TestSupport.Assert(result.Success && result.AppliedChanges == 0, "名称池差异不应产生迁移写入：" + string.Join("；", result.Messages));
            targetWorkspace = Open(target, true);
            var targetNames = names.Read(targetWorkspace, EffectNameChannel.Item);
            TestSupport.Assert(targetNames.Any(slot => slot.Name == "自定义") && targetNames.All(slot => slot.Name != "迁移"), "目标自定义名称不应被迁移覆盖");
            TestSupport.Assert(targetWorkspace.GetHistory().Count == priorCount, "名称池差异不应追加迁移历史");
        });
    }

    private static void TestMergeAndStagingFailure(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var names = new EffectNamePoolService();
            var sourceWorkspace = Open(source, false);
            names.WriteItemName(sourceWorkspace, 0x1A, "迁移");
            new AccessoryJobGroupService().Write(sourceWorkspace, [[0, 1], [2, 3]]);
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            var beforeHash = HashService.Sha256(Path.Combine(target, "Ekd5.exe"));
            var merge = Select(plan, IsNative).ToDictionary(pair => pair.Key, _ => MigrationChoice.Merge);
            var result = service.Apply(plan, merge);
            TestSupport.Assert(result.Success && result.AppliedChanges == 0 && HashService.Sha256(Path.Combine(target, "Ekd5.exe")) == beforeHash, "专用结构合并覆盖了已有目标数据");

            var brokenChanges = plan.Changes.Select(change => change.TableKey == "job-series-accessory-groups" && IsNative(change)
                ? change with { SourceValue = "0,99" } : change).ToArray();
            var broken = ClonePlan(plan, brokenChanges);
            result = service.Apply(broken, Select(broken, IsNative));
            TestSupport.Assert(!result.Success && HashService.Sha256(Path.Combine(target, "Ekd5.exe")) == beforeHash, "专用结构后续失败后临时名称修改泄漏到了目标");
        });
    }

    private static void TestAssignmentHistorySurvivesStaging(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var sourceWorkspace = Open(source, false);
            var r = sourceWorkspace.GetDocument("person-r-assign");
            var s = sourceWorkspace.GetDocument("person-s-assign");
            r.Data.Rows[0]["R形象ID"] = Convert.ToInt32(r.Data.Rows[0]["R形象ID"]) + 1;
            s.Data.Rows[0]["S形象ID"] = Convert.ToInt32(s.Data.Rows[0]["S形象ID"]) + 1;
            TestSupport.Assert(sourceWorkspace.Save().Saved, "准备有历史的源R/S修改失败");
            var targetWorkspace = Open(target, false);
            var animation = targetWorkspace.GetDocument("strategy-small-animation");
            animation.Data.Rows[0]["动画ID"] = (Convert.ToInt32(animation.Data.Rows[0]["动画ID"]) + 1) % 256;
            TestSupport.Assert(targetWorkspace.Save().Saved, "准备有历史的目标EXE修改失败");
            targetWorkspace = Open(target, false);
            TestSupport.Assert(targetWorkspace.GetDocument("person-r-assign").Definition.Capability == TableCapability.Editable, "目标历史未保持R/S能力");
            var revision = targetWorkspace.ActiveProfile.LayoutRevision;
            var service = new MigrationService();
            var plan = service.Preview(source, target, "ccz-6.5", "65-standard-v1", "ccz-6.5", "65-standard-v1");
            var result = service.Apply(plan, Select(plan, change => change.TableKey is "person-r-assign" or "person-s-assign"));
            TestSupport.Assert(result.Success && result.AppliedChanges == 2, "已有EXE历史后的R/S迁移失败：" + string.Join("；", result.Messages));
            targetWorkspace = Open(target, false);
            TestSupport.Assert(targetWorkspace.ActiveProfile.LayoutRevision == revision && targetWorkspace.GetDocument("person-r-assign").Definition.Capability == TableCapability.Editable,
                "R/S迁移改变了目标的已验证布局身份");
            TestSupport.Assert(Convert.ToInt32(targetWorkspace.GetDocument("person-r-assign").Data.Rows[0]["R形象ID"]) == Convert.ToInt32(r.Data.Rows[0]["R形象ID"]) &&
                Convert.ToInt32(targetWorkspace.GetDocument("person-s-assign").Data.Rows[0]["S形象ID"]) == Convert.ToInt32(s.Data.Rows[0]["S形象ID"]), "有历史证据的R/S写后复读不一致");
            TestSupport.Assert(targetWorkspace.GetHistory().Count == 2 && targetWorkspace.UndoSaved().Success && targetWorkspace.RedoSaved().Success,
                "R/S迁移复制旧历史或破坏撤回/重做布局身份");
        });
    }

    private static void TestReadOnlyAssignmentEvidenceSurvivesStaging(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var sourceWorkspace = Open(source, false);
            var person = sourceWorkspace.GetDocument("persons");
            person.Data.Rows[0]["武力"] = Convert.ToInt32(person.Data.Rows[0]["武力"]) + 1;
            TestSupport.Assert(sourceWorkspace.Save().Saved, "准备只读证据迁移失败");
            var path = Path.Combine(target, "Ekd5.exe");
            var exe = File.ReadAllBytes(path); exe[^1] ^= 1; File.WriteAllBytes(path, exe);
            LegacyDynamicLayoutTests.InvalidateAssignmentConsumer(target);
            exe = File.ReadAllBytes(path);
            File.WriteAllText(Path.Combine(target, "System.ini"), "FileHead=D2800\r\nRFileHead=E1000\r\n", Encoding.ASCII);
            foreach (var (name, count, length) in new[] { ("Pmapobj.e5", 660, 48 * 64 * 20), ("Unit_mov.e5", 700, 48 * 48 * 11),
                ("Unit_atk.e5", 700, 64 * 64 * 12), ("Unit_spc.e5", 700, 48 * 48 * 5) })
                WriteDirectoryFixture(Path.Combine(target, name), count, length);
            var before = Open(target, false);
            TestSupport.Assert(before.GetDocument("person-r-assign").Definition.Capability == TableCapability.ReadOnly, "未知EXE静态证据测试未处于只读状态");
            var service = new MigrationService();
            var plan = service.Preview(source, target, "ccz-6.5", "65-standard-v1", "ccz-6.5", "65-standard-v1");
            var result = service.Apply(plan, Select(plan, change => change.TableKey == "persons" && change.Id == 0 && change.FieldName == "武力"));
            TestSupport.Assert(result.Success, "只读目标的临时证据文件未保持布局能力：" + string.Join("；", result.Messages));
            var after = Open(target, false);
            TestSupport.Assert(after.ActiveProfile.LayoutRevision == before.ActiveProfile.LayoutRevision &&
                after.GetDocument("person-r-assign").Definition.Capability == TableCapability.ReadOnly, "临时探测改变了未知目标EXE的R/S只读能力");
            TestSupport.Assert(HashService.Sha256(path) == HashService.Sha256(exe), "只读探测迁移擅自改变了EXE");
            TestSupport.Assert(after.GetHistory().Count == 1 && after.UndoSaved().Success, "只读目标迁移记录使用了错误的临时布局身份");
        });
    }

    private static void WriteDirectoryFixture(string path, int count, int decodedLength)
    {
        var payload = 0x110 + count * 12;
        var bytes = new byte[payload + count];
        for (var index = 0; index < count; index++)
        {
            var slot = bytes.AsSpan(0x110 + index * 12, 12);
            BinaryPrimitives.WriteUInt32BigEndian(slot, 1);
            BinaryPrimitives.WriteUInt32BigEndian(slot[4..], (uint)decodedLength);
            BinaryPrimitives.WriteUInt32BigEndian(slot[8..], (uint)(payload + index));
        }
        File.WriteAllBytes(path, bytes);
    }

    private static bool IsNative(MigrationChange change) => change.Kind == MigrationChangeKind.NativeStructureChanged;

    private static Dictionary<int, MigrationChoice> Select(MigrationPlan plan, Func<MigrationChange, bool> predicate)
        => plan.Changes.Select((change, index) => (change, index)).Where(item => predicate(item.change))
            .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);

    private static ProjectWorkspace Open(string root, bool fixedPool)
    {
        var workspace = new ProjectWorkspace();
        workspace.Open(root, fixedPool ? "ccz-6.6" : "ccz-6.5", fixedPool ? "66-standard-v1" : "65-standard-v1");
        return workspace;
    }

    private static void SeedFixedPool(string root)
    {
        var path = Path.Combine(root, "Ekd5.exe");
        var bytes = File.ReadAllBytes(path);
        var offset = checked((int)EffectNamePoolService.ItemFixedOffset66);
        Array.Clear(bytes, offset, 230 * 16);
        foreach (var (slot, name) in new[] { (0, "恢复HP和状态"), (28, "引导攻击"), (56, "回复周围HP状态"), (72, "策略连击") })
            EncodingService.EncodeFixedString(name, 16).CopyTo(bytes, offset + slot * 16);
        File.WriteAllBytes(path, bytes);
    }

    private static MigrationPlan ClonePlan(MigrationPlan plan, IReadOnlyList<MigrationChange> changes) => new()
    {
        SourceRoot = plan.SourceRoot, TargetRoot = plan.TargetRoot,
        SourceProfileId = plan.SourceProfileId, SourceVariantId = plan.SourceVariantId,
        TargetProfileId = plan.TargetProfileId, TargetVariantId = plan.TargetVariantId,
        Changes = changes, PhysicalFileCreations = plan.PhysicalFileCreations,
        SourceCoreFileFingerprints = plan.SourceCoreFileFingerprints,
        TargetCoreFileFingerprints = plan.TargetCoreFileFingerprints
    };

    private static void WithProjects(string basePath, Action<string, string> test)
    {
        var source = TestSupport.CreateCoreCopy(basePath); var target = TestSupport.CreateCoreCopy(basePath);
        try { test(source, target); }
        finally { Directory.Delete(source, true); Directory.Delete(target, true); }
    }
}
