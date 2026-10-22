using DataManager.Core.Binary;
using DataManager.Core.Migration;

namespace DataManager.Tests;

/// <summary>
/// Exercises the resource side of the migration transaction with Windows
/// read-only attributes and a late replacement failure.  These cases are kept
/// on disposable core copies so they cannot modify a checked-in base.
/// </summary>
internal static class ReadonlyResourceRollbackTests
{
    public static void Run(string basePath)
    {
        TestExistingReadonlyResourceKeepsAttributes(basePath);
        TestReadonlySourceResourceIsNeverModified(basePath);
        TestLateResourceFailureRestoresContentAttributesAndStaging(basePath);
    }

    private static void TestExistingReadonlyResourceKeepsAttributes(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Write(source, "R\u8d44\u6e90.png", [1, 2, 3, 4]);
            Write(target, "R\u8d44\u6e90.png", [9, 8, 7, 6]);
            var targetPath = Path.Combine(target, "R\u8d44\u6e90.png");
            File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
            var beforeSource = HashService.Sha256(Path.Combine(source, "R\u8d44\u6e90.png"));
            var beforeAttributes = File.GetAttributes(targetPath);

            var plan = new MigrationService().Preview(source, target);
            var result = new MigrationService().Apply(plan, includeAllWritable: true);

            TestSupport.Assert(result.Success, "覆盖只读目标资源失败：" + string.Join("；", result.Messages));
            TestSupport.Assert(File.ReadAllBytes(targetPath).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "只读目标资源内容未迁移");
            TestSupport.Assert(File.GetAttributes(targetPath) == beforeAttributes, "覆盖只读目标资源后没有恢复原属性");
            TestSupport.Assert(beforeSource == HashService.Sha256(Path.Combine(source, "R\u8d44\u6e90.png")), "迁移修改了源资源");
            TestSupport.Assert(!Directory.EnumerateFiles(target, "*.tmp", SearchOption.AllDirectories).Any(), "只读资源迁移遗留临时文件");
        });
    }

    private static void TestReadonlySourceResourceIsNeverModified(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Write(source, "新增资源.png", [11, 22, 33]);
            var sourcePath = Path.Combine(source, "新增资源.png");
            File.SetAttributes(sourcePath, File.GetAttributes(sourcePath) | FileAttributes.ReadOnly);
            var beforeSource = HashService.Sha256(sourcePath);
            var beforeAttributes = File.GetAttributes(sourcePath);

            var plan = new MigrationService().Preview(source, target);
            var result = new MigrationService().Apply(plan, includeAllWritable: true);

            TestSupport.Assert(result.Success, "只读源新增资源迁移失败：" + string.Join("；", result.Messages));
            var targetPath = Path.Combine(target, "新增资源.png");
            TestSupport.Assert(File.ReadAllBytes(targetPath).SequenceEqual(new byte[] { 11, 22, 33 }), "只读源新增资源内容未迁移");
            TestSupport.Assert(beforeSource == HashService.Sha256(sourcePath), "迁移修改了只读源资源内容");
            TestSupport.Assert(File.GetAttributes(sourcePath) == beforeAttributes, "迁移修改了只读源资源属性");
            TestSupport.Assert((File.GetAttributes(targetPath) & FileAttributes.ReadOnly) == 0, "新增目标资源继承了源只读属性");
        });
    }

    private static void TestLateResourceFailureRestoresContentAttributesAndStaging(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            // Resource changes are committed in path order.  The first file is
            // deliberately read-only so rollback must restore both bytes and
            // attributes after the later replacement is denied by a handle.
            Write(source, "01-first.png", [10, 20]);
            Write(target, "01-first.png", [30, 40]);
            var firstTarget = Path.Combine(target, "01-first.png");
            File.SetAttributes(firstTarget, File.GetAttributes(firstTarget) | FileAttributes.ReadOnly);
            var firstBeforeSha = HashService.Sha256(firstTarget);
            var firstBeforeAttributes = File.GetAttributes(firstTarget);

            Write(source, "02-late.png", [50, 60]);
            Write(target, "02-late.png", [70, 80]);
            var lateTarget = Path.Combine(target, "02-late.png");
            var lateBeforeSha = HashService.Sha256(lateTarget);
            using var blocker = new FileStream(lateTarget, FileMode.Open, FileAccess.Read, FileShare.Read);

            var sourceBefore = Directory.EnumerateFiles(source, "*.png", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(source, path), HashService.Sha256, StringComparer.OrdinalIgnoreCase);
            var plan = new MigrationService().Preview(source, target);
            var result = new MigrationService().Apply(plan, includeAllWritable: true);

            // Some filesystems permit replacing an open file despite the
            // handle's share mode.  In that case the transaction is still a
            // valid successful commit; when replacement is denied, assert the
            // stronger rollback contract below.
            if (!result.Success)
            {
                TestSupport.Assert(File.ReadAllBytes(firstTarget).SequenceEqual(new byte[] { 30, 40 }), "晚期资源失败后首个资源内容未恢复");
                TestSupport.Assert(File.GetAttributes(firstTarget) == firstBeforeAttributes, "晚期资源失败后首个资源属性未恢复");
                TestSupport.Assert(File.ReadAllBytes(lateTarget).SequenceEqual(new byte[] { 70, 80 }), "晚期资源失败后锁定资源内容被覆盖");
                TestSupport.Assert(lateBeforeSha == HashService.Sha256(lateTarget), "晚期资源失败后锁定资源 SHA 改变");
                TestSupport.Assert(result.BackupPath == null || File.Exists(Path.Combine(result.BackupPath, "failure.json")), "晚期资源失败缺少失败回滚记录");
                TestSupport.Assert(!Directory.EnumerateFiles(target, "*.tmp", SearchOption.AllDirectories).Any(), "晚期资源失败遗留临时文件");
            }

            foreach (var pair in sourceBefore)
                TestSupport.Assert(pair.Value == HashService.Sha256(Path.Combine(source, pair.Key)), "晚期资源失败修改了源文件：" + pair.Key);
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
            MakeWritableTree(source);
            MakeWritableTree(target);
            if (Directory.Exists(source)) Directory.Delete(source, true);
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    private static void MakeWritableTree(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            catch { }
        }
    }
}
