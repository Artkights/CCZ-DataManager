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
            var exact = new ProjectWorkspace().Open(exactRoot);
            TestSupport.Assert(exact.ProfileMatch?.MatchKind == ProfileMatchKind.Exact && exact.Diagnostics.Any(line => line.Contains("PE", StringComparison.OrdinalIgnoreCase)), "6.5 精确扫描或 PE 诊断失败");
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
