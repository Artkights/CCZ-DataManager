using System.Buffers.Binary;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

/// <summary>Independent consumers and complete physical rows, without sample writes.</summary>
internal static class LegacyDynamicLayoutTests
{
    public static void Run(string sourceRoot)
    {
        var sourceExecutable = File.ReadAllBytes(ProjectFileLocator.Resolve(sourceRoot, "Ekd5.exe"));
        TestStrictConsumers(sourceExecutable);
        TestRelocatedAssignments(sourceRoot, sourceExecutable);
        TestAssignmentRoundTrip(sourceRoot);
        TestCompleteExtensionRows(sourceRoot);
    }

    internal static void InvalidateAssignmentConsumer(string root)
    {
        var path = ProjectFileLocator.Resolve(root, "Ekd5.exe");
        var bytes = File.ReadAllBytes(path);
        var offset = Find(bytes, "55 8B EC 51 E8 ?? ?? ?? ?? 0F B7 04 45 ?? ?? ?? ?? 59 5D C3");
        bytes[offset] = 0x90;
        var s = Find(bytes, "8B 55 08 D1 E2 0F B7 82 ?? ?? ?? ?? 85 C0 74 18 83 F8 21 73 0C 48 6B C0 03 83 C0 50 03 45 FC EB 0C 05 8F 00 00 00");
        bytes[s + 5] = 0x90;
        File.WriteAllBytes(path, bytes);
    }

    private static void TestStrictConsumers(byte[] original)
    {
        var probe = EmbeddedEngineLayoutProbe.InspectAssignments65(original);
        TestSupport.Assert(probe.R.Verified && probe.S.Verified, "样本未通过严格 6.5 R/S 消费者检查");
        TestSupport.Assert(probe.R.Evidence.Contains("72", StringComparison.Ordinal) &&
            probe.S.Evidence.Contains("1024", StringComparison.Ordinal), "独立证据未包含运行态记录步长与人物边界");

        var broken = (byte[])original.Clone();
        var runtimeId = Find(broken, "55 8B EC 8B 45 08 2B 05 ?? ?? ?? ?? 99 33 C9 B1 48 F7 F9 5D C3");
        broken[runtimeId + 16] = 0x49;
        TestSupport.Assert(!EmbeddedEngineLayoutProbe.InspectAssignments65(broken).R.Verified,
            "人物运行态记录除数被修改后仍开放 R 指定");

        broken = (byte[])original.Clone();
        var bounds = Find(broken, "55 8B EC 83 EC 08 81 7D 08 00 04 00 00 72 04 33 C9 EB 4D");
        broken[bounds + 9] = 0xFF;
        TestSupport.Assert(!EmbeddedEngineLayoutProbe.InspectAssignments65(broken).S.Verified,
            "人物 ID<1024 边界被修改后仍开放 S 指定");

        broken = (byte[])original.Clone();
        var r = Find(broken, "55 8B EC 51 E8 ?? ?? ?? ?? 0F B7 04 45 ?? ?? ?? ?? 59 5D C3");
        broken.AsSpan(r, 20).CopyTo(broken.AsSpan(0x80000, 20));
        TestSupport.Assert(!EmbeddedEngineLayoutProbe.InspectAssignments65(broken).R.Verified,
            "重复 R 消费者不能构成唯一地址证据");
    }

    private static void TestRelocatedAssignments(string sourceRoot, byte[] original)
    {
        WithProject(sourceRoot, root =>
        {
            var executable = (byte[])original.Clone();
            var before = EmbeddedEngineLayoutProbe.InspectAssignments65(executable);
            var rConsumer = Find(executable, "55 8B EC 51 E8 ?? ?? ?? ?? 0F B7 04 45 ?? ?? ?? ?? 59 5D C3");
            var sConsumer = Find(executable, "8B 55 08 D1 E2 0F B7 82 ?? ?? ?? ?? 85 C0 74 18 83 F8 21 73 0C 48 6B C0 03 83 C0 50 03 45 FC EB 0C 05 8F 00 00 00");
            const int shift = 0x1000;
            var rVa = BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(rConsumer + 13, 4));
            var sVa = BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(sConsumer + 8, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(executable.AsSpan(rConsumer + 13, 4), rVa + shift);
            BinaryPrimitives.WriteUInt32LittleEndian(executable.AsSpan(sConsumer + 8, 4), sVa + shift);
            File.WriteAllBytes(ProjectFileLocator.Resolve(root, "Ekd5.exe"), executable);
            var workspace = new ProjectWorkspace();
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(workspace.GetDocument("person-r-assign").Definition.DataOffset == before.R.Offset + shift &&
                workspace.GetDocument("person-s-assign").Definition.DataOffset == before.S.Offset + shift,
                "6.5 消费者移动地址后仍读取固定继承偏移");
            TestSupport.Assert(workspace.GetDocument("person-r-assign").Definition.Capability == TableCapability.Editable,
                "独立消费者和可写 PE 映射未开放未知 SHA 的有效布局");
            File.WriteAllText(Path.Combine(root, "System.ini"), "FileHead=D2800\r\nRFileHead=E1000\r\n", Encoding.ASCII);
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(workspace.Definitions.Where(table => table.Key is "person-r-assign" or "person-s-assign")
                .All(table => table.Capability == TableCapability.Unavailable), "INI 与实际消费地址冲突时未阻止写入");
        });
    }

    private static void TestAssignmentRoundTrip(string sourceRoot)
    {
        WithProject(sourceRoot, root =>
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            var r = workspace.GetDocument("person-r-assign");
            var s = workspace.GetDocument("person-s-assign");
            var revision = workspace.ActiveProfile.LayoutRevision;
            var path = ProjectFileLocator.Resolve(root, "Ekd5.exe");
            var before = File.ReadAllBytes(path);
            var newR = Convert.ToInt32(r.Data.Rows[0]["R形象ID"]) ^ 1;
            var newS = Convert.ToInt32(s.Data.Rows[1023]["S形象ID"]) ^ 1;
            r.Data.Rows[0]["R形象ID"] = newR;
            s.Data.Rows[1023]["S形象ID"] = newS;
            var save = workspace.Save();
            TestSupport.Assert(save.Saved, "动态 6.5 R/S 保存失败：" + string.Join("；", save.Messages));
            var after = File.ReadAllBytes(path);
            var allowed = new[] { r.Definition.DataOffset, r.Definition.DataOffset + 1,
                s.Definition.DataOffset + 1023 * 2, s.Definition.DataOffset + 1023 * 2 + 1 }.ToHashSet();
            TestSupport.Assert(Enumerable.Range(0, before.Length).All(index => before[index] == after[index] || allowed.Contains(index)),
                "R/S 动态写入修改了未选中字节");
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(workspace.ActiveProfile.LayoutRevision == revision &&
                Convert.ToInt32(workspace.GetDocument("person-r-assign").Data.Rows[0]["R形象ID"]) == newR &&
                Convert.ToInt32(workspace.GetDocument("person-s-assign").Data.Rows[1023]["S形象ID"]) == newS,
                "动态 6.5 R/S 保存后布局身份或复读值变化");
            TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(path).SequenceEqual(before),
                "动态 R/S 保存撤回未完整恢复原始 EXE");
        });
    }

    private static void TestCompleteExtensionRows(string sourceRoot)
    {
        WithProject(sourceRoot, root =>
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            var defaultRevision = workspace.ActiveProfile.LayoutRevision;
            File.WriteAllBytes(Path.Combine(root, "Forge.e5"), new byte[1024 * 64]);
            File.WriteAllBytes(Path.Combine(root, "Effectmsg.e5"), new byte[255 * 8]);
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(workspace.ActiveProfile.LayoutRevision == defaultRevision,
                "默认完整扩展表改变既有历史布局身份");

            var forge = new byte[256 * 64];
            forge[^1] = 77;
            var messages = new byte[256 * 8];
            messages[^1] = 91;
            File.WriteAllBytes(Path.Combine(root, "Forge.e5"), forge);
            File.WriteAllBytes(Path.Combine(root, "Effectmsg.e5"), messages);
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(workspace.GetDocument("forge").Data.Rows.Count == 256 &&
                workspace.GetDocument("effect-messages").Data.Rows.Count == 256 &&
                Convert.ToInt32(workspace.GetDocument("effect-messages").Data.Rows[255]["Param7"]) == 91,
                "完整短锻造表或第 256 条特效说明丢失");
            TestSupport.Assert(workspace.ActiveProfile.LayoutRevision != defaultRevision,
                "动态扩展表几何未纳入布局身份");

            File.WriteAllBytes(Path.Combine(root, "Effectmsg.e5"), new byte[2047]);
            File.WriteAllBytes(Path.Combine(root, "Forge.e5"), new byte[16385]);
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(!workspace.Documents.ContainsKey("effect-messages") && !workspace.Documents.ContainsKey("forge") &&
                workspace.Definitions.Where(table => table.Key is "effect-messages" or "forge")
                    .All(table => table.Capability == TableCapability.Unavailable),
                "非完整记录文件按默认行数截断读取");
        });
    }

    private static int Find(byte[] bytes, string text)
    {
        var pattern = text.Split(' ').Select(part => part == "??" ? (byte?)null : Convert.ToByte(part, 16)).ToArray();
        var hits = new List<int>();
        for (var offset = 0; offset <= bytes.Length - pattern.Length; offset++)
        {
            var match = true;
            for (var index = 0; index < pattern.Length; index++)
                if (pattern[index] is { } expected && bytes[offset + index] != expected) { match = false; break; }
            if (match) hits.Add(offset);
        }
        TestSupport.Assert(hits.Count == 1, "测试消费函数定位不唯一：" + text);
        return hits[0];
    }

    private static void WithProject(string sourceRoot, Action<string> action)
    {
        var root = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
}
