using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Binary;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class ScannerTests
{
    public static void Run(string basePath)
    {
        var exactRoot = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace();
            var report = workspace.Open(exactRoot);
            var snapshot = Known65FixtureSnapshot.CoreNames.ToDictionary(name => name,
                name => File.ReadAllBytes(Path.Combine(exactRoot, name)), StringComparer.OrdinalIgnoreCase);
            if (Known65FixtureSnapshot.MatchesKnown(snapshot))
                TestSupport.Assert(report.ProfileMatch?.MatchKind == ProfileMatchKind.Exact,
                    "已知四文件快照未进行精确匹配");
            else
            {
                // The local fixture can contain deliberate engine patches that
                // have no history back to an embedded fingerprint. Exercise its
                // real consumers without claiming an exact sample identity.
                var consumers = EmbeddedEngineLayoutProbe.InspectAssignments65(snapshot["Ekd5.exe"]);
                TestSupport.Assert(report.ProfileMatch is { ProfileId: "ccz-6.5", MatchKind: ProfileMatchKind.Compatible or ProfileMatchKind.Partial } &&
                    consumers.R.Verified && consumers.S.Verified,
                    "非已知快照的 6.5 样本缺少兼容版本或严格 R/S 消费者证据");
                foreach (var (key, consumer) in new[] { ("person-r-assign", consumers.R), ("person-s-assign", consumers.S) })
                    TestSupport.Assert(workspace.GetDocument(key).Definition is { Capability: TableCapability.Editable } definition &&
                        definition.DataOffset == consumer.Offset && definition.CapabilityEvidence.Contains(consumer.Evidence, StringComparison.Ordinal),
                        "兼容样本未按本项目独立 PE 消费者开放 " + key);
                Console.WriteLine("SCANNER_SOURCE_EVIDENCE compatible-6.5/strict-PE-consumers; source is not an embedded four-file snapshot");
            }
            TestSupport.Assert(report.Diagnostics.Any(line => line.StartsWith("Ekd5.exe PE：", StringComparison.Ordinal)),
                "扫描未给出通过 MZ/PE 检查的节区诊断");
        }
        finally { Directory.Delete(exactRoot, true); }
        var unavailable = new ProfileRegistry().Resolve("ccz-6.6", "66-standard-v1").Tables.Single(table => table.Key == "strategy-terrain-availability");
        var unavailableValidation = new BinaryTableReader().Validate(basePath, unavailable);
        TestSupport.Assert(!unavailableValidation.IsUsable && unavailableValidation.Message.Contains("布局证据", StringComparison.Ordinal),
            "缺证表不应通过读取校验");
        var refusedUnavailableRead = false;
        try { new BinaryTableReader().Read(basePath, unavailable); }
        catch (InvalidDataException ex) { refusedUnavailableRead = ex.Message.Contains("布局证据", StringComparison.Ordinal); }
        TestSupport.Assert(refusedUnavailableRead, "Reader 必须拒绝读取 Unavailable 表的继承地址");

        var temp = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var dataPath = Path.Combine(temp, "Data.e5");
            var bytes = File.ReadAllBytes(dataPath);
            File.WriteAllBytes(dataPath, bytes[..100]);
            var partialWorkspace = new ProjectWorkspace();
            var partial = partialWorkspace.Open(temp);
            TestSupport.Assert(partial.AllowsEditing && partial.Status != ScanStatus.Matched, "扫描失败错误地禁止了编辑");
            TestSupport.Assert(!partialWorkspace.Documents.ContainsKey("persons") && partialWorkspace.Documents.ContainsKey("job-series"), "单表过短时没有隔离不可用表");
        }
        finally { Directory.Delete(temp, true); }

        var standard66 = Path.Combine(TestSupport.RepositoryRootFromBase(basePath), "基底", "曹操传6.6-V7.16 解压密码：66");
        if (Directory.Exists(standard66))
        {
            var evidenceWorkspace = new ProjectWorkspace();
            evidenceWorkspace.Open(standard66);
            foreach (var key in new[] { "person-r-assign", "person-s-assign", "strategy-terrain-availability" })
            {
                TestSupport.Assert(evidenceWorkspace.Definitions.Single(table => table.Key == key).Capability == TableCapability.Editable &&
                    evidenceWorkspace.Documents.ContainsKey(key),
                    $"真实 6.6 样本的独立消费者证据没有开放 {key}");
            }

            var unknown = TestSupport.CreateCoreCopy(standard66);
            try
            {
                foreach (var name in new[] { "Data.e5", "Ekd5.exe" })
                {
                    var path = Path.Combine(unknown, name);
                    var bytes = File.ReadAllBytes(path);
                    bytes[^1] ^= 0x01;
                    File.WriteAllBytes(path, bytes);
                }
                var unknownWorkspace = new ProjectWorkspace();
                var report = unknownWorkspace.Open(unknown);
                TestSupport.Assert(unknownWorkspace.ActiveProfile.ProfileId == "ccz-6.6" && unknownWorkspace.ActiveProfile.VariantId == "66-standard-v1", "未知 6.6 没有回退到标准布局");
                TestSupport.Assert(report.ProfileMatch?.MatchKind is ProfileMatchKind.Compatible or ProfileMatchKind.Partial, "未知 6.6 匹配等级错误");
            }
            finally { Directory.Delete(unknown, true); }
        }

        var forced = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var forcedWorkspace = new ProjectWorkspace();
            var report = forcedWorkspace.Open(forced, "ccz-6.6", "66-standard-v1");
            TestSupport.Assert(report.ProfileMatch is { MatchKind: ProfileMatchKind.Forced, IsManualOverride: true } && report.AllowsEditing, "手动强制 Profile 没有保持读写权限");
            // A forced 6.6 profile still goes through the PE consumer gate. The
            // 6.5 fixture has the same independently verified consumer, so the
            // table is loaded only when that physical evidence is present.
            var terrain = forcedWorkspace.Definitions.Single(table => table.Key == "strategy-terrain-availability");
            TestSupport.Assert(terrain.Capability == TableCapability.Editable &&
                forcedWorkspace.Documents.ContainsKey("strategy-terrain-availability"),
                "有独立 PE 消费者证据时不应继续把 6.6 策略地形标为缺证");
        }
        finally { Directory.Delete(forced, true); }
    }
}
