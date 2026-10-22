using System.Buffers.Binary;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class AssignmentEvidenceTests
{
    public static void Run(string basePath)
    {
        SixSixConsumerEvidence(basePath);
        SixSixWritableConsumerRoundTrip(basePath);
        CompactSAssignmentRoundTrip(basePath);
        KnownSampleAndSavedHistory(basePath);
        ContradictoryIni(basePath);
        TruncatedExecutable(basePath);
        UnknownExecutableWithoutEvidence(basePath);
        UnknownExecutableWithStaticEvidenceStaysReadOnly(basePath);
    }

    private static void CompactSAssignmentRoundTrip(string source)
    {
        WithProject(source, root =>
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            foreach (var compactId in new[] { 0, 1, 2, 32, 33, 252 })
            {
                var document = workspace.GetDocument("person-s-assign");
                TestSupport.Assert(document.Definition.Capability == TableCapability.Editable,
                    "人物紧凑S保存测试缺少已验证写入证据");
                var offset = checked((int)document.Definition.DataOffset);
                var before = File.ReadAllBytes(document.FilePath);
                document.Data.Rows[0]["S形象ID"] = compactId;
                TestSupport.Assert(workspace.Save().Saved, $"人物紧凑S{compactId}不能保存");
                var after = File.ReadAllBytes(document.FilePath);
                TestSupport.Assert(after.Length == before.Length &&
                    Enumerable.Range(0, before.Length).Where(index => before[index] != after[index])
                        .All(index => index >= offset && index < offset + 2) &&
                    BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(offset, 2)) == compactId,
                    $"人物紧凑S{compactId}保存时误写物理图号、表几何或其他字段");
                workspace.Open(root);
                TestSupport.Assert(Convert.ToInt32(workspace.GetDocument("person-s-assign").Data.Rows[0]["S形象ID"]) == compactId,
                    $"人物紧凑S{compactId}保存后复读改变了原始UInt16值");
            }
        });
    }

    private static void SixSixConsumerEvidence(string source)
    {
        var repo = TestSupport.RepositoryRootFromBase(source);
        var path = Path.Combine(repo, "基底", "曹操传6.6-V7.16 解压密码：66", "Ekd5.exe");
        if (!File.Exists(path)) return;
        var executable = File.ReadAllBytes(path);
        var assignments = EmbeddedEngineLayoutProbe.InspectAssignments(executable);
        TestSupport.Assert(assignments.R.Verified && assignments.R.Offset == ProfileAugmentationService.RAssignmentOffset65,
            "6.6 样本未通过独立 R 形象消费者/PE VA 映射证据");
        TestSupport.Assert(assignments.S.Verified && assignments.S.Offset == ProfileAugmentationService.SAssignmentOffset65,
            "6.6 样本未通过独立 S 形象消费者/PE VA 映射证据");
        var terrain = EmbeddedEngineLayoutProbe.InspectStrategyTerrain(executable);
        TestSupport.Assert(terrain.Verified && terrain.Offset == 0x1FECC,
            "6.6 样本未通过独立策略地形读取/掩码消费者证据");
    }

    private static void SixSixWritableConsumerRoundTrip(string source)
    {
        var sample = Path.Combine(TestSupport.RepositoryRootFromBase(source), "基底", "曹操传6.6-V7.16 解压密码：66");
        if (!Directory.Exists(sample)) return;
        var root = TestSupport.CreateCoreCopy(sample);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root, "ccz-6.6", "66-standard-v1");
            foreach (var key in new[] { "person-r-assign", "person-s-assign", "strategy-terrain-availability" })
                TestSupport.Assert(workspace.Documents.ContainsKey(key) && workspace.GetDocument(key).Definition.Capability == TableCapability.Editable,
                    $"6.6 动态消费者证据未开放 {key} 写入");

            var r = workspace.GetDocument("person-r-assign");
            var s = workspace.GetDocument("person-s-assign");
            var terrain = workspace.GetDocument("strategy-terrain-availability");
            var oldR = Convert.ToInt32(r.Data.Rows[0]["R形象ID"]);
            var oldS = Convert.ToInt32(s.Data.Rows[0]["S形象ID"]);
            var oldTerrain = Convert.ToInt32(terrain.Data.Rows[0]["策略施展地形掩码"]);
            var newR = oldR == 0 ? 1 : 0;
            var newS = oldS == 0 ? 1 : 0;
            var newTerrain = oldTerrain ^ 1;
            workspace.ValidateCellValue(r, 0, "R形象ID", newR);
            workspace.ValidateCellValue(s, 0, "S形象ID", newS);
            workspace.ValidateCellValue(terrain, 0, "策略施展地形掩码", newTerrain);
            r.Data.Rows[0]["R形象ID"] = newR;
            s.Data.Rows[0]["S形象ID"] = newS;
            terrain.Data.Rows[0]["策略施展地形掩码"] = newTerrain;
            TestSupport.Assert(workspace.Save().Saved, "6.6 R/S/策略地形写回失败");

            workspace.Open(root, "ccz-6.6", "66-standard-v1");
            TestSupport.Assert(Convert.ToInt32(workspace.GetDocument("person-r-assign").Data.Rows[0]["R形象ID"]) == newR &&
                Convert.ToInt32(workspace.GetDocument("person-s-assign").Data.Rows[0]["S形象ID"]) == newS &&
                Convert.ToInt32(workspace.GetDocument("strategy-terrain-availability").Data.Rows[0]["策略施展地形掩码"]) == newTerrain,
                "6.6 R/S/策略地形写回后复读值不一致");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void KnownSampleAndSavedHistory(string source)
    {
        WithProject(source, root =>
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            AssertCapability(workspace, TableCapability.Editable, "已知 SHA 的核心文件副本不需要图片资源或 INI 即可沿用已验证几何");
            var layout = workspace.ActiveProfile.LayoutRevision;
            var executable = ProjectFileLocator.Resolve(root, "Ekd5.exe");
            var before = File.ReadAllBytes(executable);
            var animation = workspace.GetDocument("strategy-small-animation");
            animation.Data.Rows[0]["动画ID"] = (Convert.ToInt32(animation.Data.Rows[0]["动画ID"]) + 1) % 256;
            TestSupport.Assert(workspace.Save().Saved, "证据连续性测试的 EXE 数据保存失败");
            TestSupport.Assert(HashService.Sha256(executable) != HashService.Sha256(before), "测试没有实际改变 EXE SHA");
            workspace.Open(root);
            AssertCapability(workspace, TableCapability.Editable, "已验证历史快照没有保留 EXE 数据保存后的 R/S 能力");
            TestSupport.Assert(workspace.ActiveProfile.LayoutRevision == layout, "保存后的 SHA 变化导致布局身份变化");
            TestSupport.Assert(workspace.GetDocument("person-r-assign").Definition.CapabilityEvidence.Contains("保存历史", StringComparison.Ordinal),
                "改变 SHA 后没有说明历史回溯证据");
            TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(executable).SequenceEqual(before), "证据门破坏了保存后撤回");
            TestSupport.Assert(workspace.RedoSaved().Success, "证据门破坏了保存后重做");
            var r = workspace.GetDocument("person-r-assign");
            r.Data.Rows[0]["R形象ID"] = Convert.ToInt32(r.Data.Rows[0]["R形象ID"]) + 1;
            TestSupport.Assert(workspace.Save().Saved, "有历史证据时 R 指定不能保存");
            workspace.Open(root);
            AssertCapability(workspace, TableCapability.Editable, "R 指定保存后的多步历史证据链丢失");
            TestSupport.Assert(workspace.ActiveProfile.LayoutRevision == layout && workspace.UndoSaved().Success && workspace.RedoSaved().Success,
                "R/S 自身保存改变了能力或历史布局身份");

            // A modified history chunk cannot serve as a checked baseline.
            var history = workspace.GetHistory().ToArray();
            history[0].Files.Single(file => file.RelativePath == "Ekd5.exe").Chunks[0].OldBytes[0] ^= 1;
            workspace.History.Replace(root, history);
            // An independently valid consumer no longer depends on the damaged
            // journal. Disable it explicitly to keep this a missing-evidence test.
            LegacyDynamicLayoutTests.InvalidateAssignmentConsumer(root);
            workspace.Open(root);
            AssertCapability(workspace, TableCapability.Unavailable, "损坏的历史字节不应继承已验证 SHA 证据");
        });
    }

    private static void ContradictoryIni(string source)
    {
        WithProject(source, root =>
        {
            File.WriteAllText(Path.Combine(root, "System.ini"), "[Main]\r\nFileHead=D2800\r\nRFileHead=E2000\r\n", Encoding.ASCII);
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            AssertCapability(workspace, TableCapability.Unavailable, "项目 INI 与已验证布局冲突时不得继续写入或自动搬动偏移");
            TestSupport.Assert(workspace.Definitions.Single(table => table.Key == "person-r-assign").CapabilityEvidence.Contains("冲突", StringComparison.Ordinal),
                "INI 偏移冲突未给出明确原因");
            File.WriteAllText(Path.Combine(root, "System.ini"), "FileHead=0xD2800\r\nRFileHead=0xE1000\r\n", Encoding.ASCII);
            workspace.Open(root);
            AssertCapability(workspace, TableCapability.Editable, "有效的十六进制 INI 不应阻断已验证样本");
        });
    }

    private static void TruncatedExecutable(string source)
    {
        WithProject(source, root =>
        {
            var path = ProjectFileLocator.Resolve(root, "Ekd5.exe");
            using (var stream = File.OpenWrite(path)) stream.SetLength(ProfileAugmentationService.RAssignmentOffset65 + 2047);
            File.WriteAllText(Path.Combine(root, "System.ini"), "FileHead=D2800\r\nRFileHead=E1000\r\n", Encoding.ASCII);
            var workspace = new ProjectWorkspace();
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            AssertCapability(workspace, TableCapability.Unavailable, "R/S 指定表末行越过 EXE 边界仍开放了能力");
            TestSupport.Assert(workspace.Definitions.Single(table => table.Key == "person-r-assign").CapabilityEvidence.Contains("文件边界", StringComparison.Ordinal),
                "R/S 文件边界错误未给出明确原因");
        });
    }

    private static void UnknownExecutableWithoutEvidence(string source)
    {
        WithProject(source, root =>
        {
            ChangeExecutableSha(root);
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            AssertCapability(workspace, TableCapability.Unavailable, "未知 SHA 且缺少 INI/资源时不得默认套用 6.5 偏移");
            File.WriteAllText(Path.Combine(root, "System.ini"), "FileHead=D2800\r\nRFileHead=E1000\r\n", Encoding.ASCII);
            workspace.Open(root);
            AssertCapability(workspace, TableCapability.Unavailable, "单独的 INI 不能代替资源与样本证据");
            TestSupport.Assert(workspace.Definitions.Single(table => table.Key == "person-r-assign").CapabilityEvidence.Contains("资源或样本证据不足", StringComparison.Ordinal),
                "未知 SHA 缺资源未给出原因");
        });
    }

    private static void UnknownExecutableWithStaticEvidenceStaysReadOnly(string source)
    {
        WithProject(source, root =>
        {
            ChangeExecutableSha(root);
            File.WriteAllText(Path.Combine(root, "System.ini"), "FileHead=D2800\r\nRFileHead=E1000\r\n", Encoding.ASCII);
            WriteDirectoryFixture(Path.Combine(root, "Pmapobj.e5"), 660, 48 * 64 * 20);
            WriteDirectoryFixture(Path.Combine(root, "Unit_mov.e5"), 700, 48 * 48 * 11);
            WriteDirectoryFixture(Path.Combine(root, "Unit_atk.e5"), 700, 64 * 64 * 12);
            WriteDirectoryFixture(Path.Combine(root, "Unit_spc.e5"), 700, 48 * 48 * 5);
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            AssertCapability(workspace, TableCapability.ReadOnly, "未知引擎只有 INI/目录/数值证据时应保持只读");
            var r = workspace.GetDocument("person-r-assign");
            var rejected = false;
            try { workspace.ValidateCellValue(r, 0, "R形象ID", 1); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("不可写", StringComparison.Ordinal); }
            TestSupport.Assert(rejected && !workspace.IsDirty, "未知引擎 R/S 只读能力没有阻止字段写入");
            var path = ProjectFileLocator.Resolve(root, "Ekd5.exe");
            var bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((int)ProfileAugmentationService.RAssignmentOffset65, 2), ushort.MaxValue);
            File.WriteAllBytes(path, bytes);
            workspace.Open(root);
            AssertCapability(workspace, TableCapability.Unavailable, "R/S 样本越过资源组上限后仍显示为已验证结构");
        });
    }

    private static void AssertCapability(ProjectWorkspace workspace, TableCapability expected, string message)
    {
        foreach (var key in new[] { "person-r-assign", "person-s-assign" })
        {
            var definition = workspace.Definitions.Single(table => table.Key == key);
            TestSupport.Assert(definition.Capability == expected, message + "；" + definition.CapabilityEvidence);
            TestSupport.Assert(workspace.Documents.ContainsKey(key) == (expected != TableCapability.Unavailable), "不可用的 R/S 表不应读取继承偏移");
        }
    }

    private static void ChangeExecutableSha(string root)
    {
        var path = ProjectFileLocator.Resolve(root, "Ekd5.exe");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        LegacyDynamicLayoutTests.InvalidateAssignmentConsumer(root);
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

    private static void WithProject(string source, Action<string> test)
    {
        var root = TestSupport.CreateCoreCopy(source);
        try { test(root); }
        finally { Directory.Delete(root, true); }
    }
}
