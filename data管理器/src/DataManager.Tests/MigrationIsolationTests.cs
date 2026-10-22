using DataManager.Core.Binary;
using DataManager.Core.Migration;

namespace DataManager.Tests;

/// <summary>
/// Regression coverage for selection isolation and migration boundary checks.
/// These tests intentionally use disposable core-only projects so the checked-in
/// sample base is never modified.
/// </summary>
internal static class MigrationIsolationTests
{
    public static void Run(string basePath)
    {
        TestAmbiguousArchiveCopiesAreIncompatible(basePath);
        TestSelectedFieldDoesNotRewriteOtherFiles(basePath);
        TestEvidenceSnapshotIsStale(basePath);
        TestTraversalPlanIsRejected(basePath);
    }

    private static void TestAmbiguousArchiveCopiesAreIncompatible(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Write(source, "Item.e5", [1, 2, 3]);
            Write(source, Path.Combine("E5", "Item.e5"), [4, 5, 6]);
            Write(target, "Item.e5", [7, 8, 9]);
            Write(target, Path.Combine("E5", "Item.e5"), [10, 11, 12]);

            var service = new MigrationService();
            var plan = service.Preview(source, target);
            var ambiguous = plan.Changes.Where(change =>
                change.TableKey == "__resource__" &&
                change.Kind == MigrationChangeKind.Incompatible &&
                change.Reason?.Contains("多个同名副本", StringComparison.Ordinal) == true).ToArray();
            TestSupport.Assert(ambiguous.Length >= 1, "根目录/E5 同名资源未标记为不可自动判断");
            TestSupport.Assert(!plan.WritableChanges.Any(change =>
                change.TableKey == "__resource__" &&
                Path.GetFileName(change.TableDisplayName).Equals("Item.e5", StringComparison.OrdinalIgnoreCase)),
                "同名资源歧义仍暴露为可写迁移项");

            var sourceBefore = HashService.Sha256(Path.Combine(source, "Item.e5"));
            var targetBefore = Directory.EnumerateFiles(target, "Item.e5", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(target, path), HashService.Sha256,
                    StringComparer.OrdinalIgnoreCase);
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success, "歧义资源跳过后迁移应成功：" + string.Join("；", result.Messages));
            TestSupport.Assert(sourceBefore == HashService.Sha256(Path.Combine(source, "Item.e5")),
                "歧义资源迁移修改了源文件");
            foreach (var pair in targetBefore)
                TestSupport.Assert(pair.Value == HashService.Sha256(Path.Combine(target, pair.Key)),
                    "歧义资源迁移覆盖了目标副本：" + pair.Key);
        });
    }

    private static void TestSelectedFieldDoesNotRewriteOtherFiles(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var sourceWorkspace = new DataManager.Core.Editing.ProjectWorkspace();
            sourceWorkspace.Open(source);
            var person = sourceWorkspace.GetDocument("persons");
            var current = Convert.ToInt32(person.Data.Rows[0]["武力"], System.Globalization.CultureInfo.InvariantCulture);
            person.Data.Rows[0]["武力"] = current + 1;
            var save = sourceWorkspace.Save();
            TestSupport.Assert(save.Saved, "隔离测试源项目保存失败");

            var before = Directory.EnumerateFiles(target, "*", SearchOption.TopDirectoryOnly)
                .Where(path => new[] { "Data.e5", "Star.e5", "Ekd5.exe", "Imsg.e5" }
                    .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                .ToDictionary(path => Path.GetFileName(path), HashService.Sha256,
                    StringComparer.OrdinalIgnoreCase);
            var plan = new MigrationService().Preview(source, target);
            var selected = plan.Changes.Select((change, index) => (change, index))
                .Where(item => item.change.TableKey == "persons" && item.change.FieldName == "武力")
                .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            TestSupport.Assert(selected.Count == 1, "隔离测试未得到唯一人物字段迁移项");

            var result = new MigrationService().Apply(plan, selected);
            TestSupport.Assert(result.Success && result.AppliedChanges == 1, "单字段迁移失败：" + string.Join("；", result.Messages));
            foreach (var pair in before)
            {
                var now = HashService.Sha256(Path.Combine(target, pair.Key));
                if (pair.Key.Equals("Data.e5", StringComparison.OrdinalIgnoreCase))
                    TestSupport.Assert(now != pair.Value, "选中人物字段没有修改其物理文件");
                else
                    TestSupport.Assert(now == pair.Value, "单字段迁移改写了未选中的物理文件：" + pair.Key);
            }
        });
    }

    private static void TestEvidenceSnapshotIsStale(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            Write(source, "System.ini", [1, 2, 3]);
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(!result.Success && result.Messages.Any(message =>
                message.Contains("配置", StringComparison.Ordinal) || message.Contains("预览后发生变化", StringComparison.Ordinal)),
                "迁移预览后的 System.ini 变更未使计划失效");
            TestSupport.Assert(!File.Exists(Path.Combine(target, "System.ini")), "过期配置计划写入了目标");
        });
    }

    private static void TestTraversalPlanIsRejected(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var outside = Path.Combine(Path.GetDirectoryName(target)!, "migration-outside.bin");
            if (File.Exists(outside)) File.Delete(outside);
            try
            {
                var plan = new MigrationPlan
                {
                    SourceRoot = source,
                    TargetRoot = target,
                    SourceProfileId = "ccz-6.5",
                    SourceVariantId = "65-baseline",
                    TargetProfileId = "ccz-6.5",
                    TargetVariantId = "65-baseline",
                    Changes = [new MigrationChange("__resource__", "../migration-outside.bin", string.Empty, -1,
                        "deadbeef", null, MigrationChangeKind.ResourceAdded)]
                };
                var result = new MigrationService().Apply(plan, includeAllWritable: true);
                TestSupport.Assert(!result.Success && result.Messages.Any(message =>
                    message.Contains("相对路径", StringComparison.Ordinal) || message.Contains("项目目录", StringComparison.Ordinal)),
                    "迁移计划路径穿越未被拒绝");
                TestSupport.Assert(!File.Exists(outside), "路径穿越创建了项目外文件");
            }
            finally
            {
                if (File.Exists(outside)) File.Delete(outside);
            }
        });
    }

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void WithProjects(string basePath, Action<string, string> test)
    {
        var source = TestSupport.CreateCoreCopy(basePath);
        var target = TestSupport.CreateCoreCopy(basePath);
        try { test(source, target); }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, true);
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
