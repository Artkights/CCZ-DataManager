using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Profiles;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class SourceFileReadSessionTests
{
    public static void Run(string basePath)
    {
        SharedSourceAndPathIsolation();
        SharedOpenPipeline(basePath);
        Console.WriteLine("SOURCE_FILE_READ_SESSION_TESTS_OK");
    }

    private static void SharedSourceAndPathIsolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "DataManagerSourceSession_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "E5"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Shared.bin"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(root, "Item.e5"), [7, 8]);
            File.WriteAllBytes(Path.Combine(root, "E5", "Item.e5"), [9, 10, 11, 12]);
            var first = Definition("first", "Shared.bin", 0);
            var second = Definition("second", "Shared.bin", 2);
            var reader = new BinaryTableReader();
            var session = new SourceFileReadSession();
            var a = reader.Read(root, first, session);
            var b = reader.Read(root, second, session);
            TestSupport.Assert(ReferenceEquals(a.OriginalFileBytes, b.OriginalFileBytes) &&
                a.OriginalFileHash == b.OriginalFileHash && (int)a.Data.Rows[0]["数值"] == 1 && (int)b.Data.Rows[0]["数值"] == 3,
                "同一源文件的不同物理表没有共享字节与哈希，或独立偏移读取错误");
            TestSupport.Assert(session.Statistics.FileReads == 1 && session.Statistics.HashComputations == 1 &&
                session.Statistics.SnapshotHits == 1, "同源物理表重复读取或计算 SHA256");
            var extension = reader.Read(root, Definition("items-extended", "Item.e5", 0) with { RowCount = 2 }, session);
            TestSupport.Assert((int)extension.Data.Rows[0]["数值"] == 7 &&
                Path.GetDirectoryName(extension.FilePath) == root, "根目录扩展数据误读 E5 同名档案");
            var truncated = reader.Validate(root, first with { RowCount = 5 }, session);
            TestSupport.Assert(!truncated.IsUsable, "源缓存绕过了各物理表独立边界校验");
            var otherRoot = Path.Combine(root, "OtherProject");
            Directory.CreateDirectory(otherRoot);
            File.WriteAllBytes(Path.Combine(otherRoot, "Shared.bin"), [21, 22, 23, 24]);
            var other = reader.Read(otherRoot, first, session);
            var archive = reader.Read(Path.Combine(root, "E5"), Definition("icons", "Item.e5", 0), session);
            TestSupport.Assert((int)other.Data.Rows[0]["数值"] == 21 && (int)archive.Data.Rows[0]["数值"] == 9 &&
                !ReferenceEquals(a.OriginalFileBytes, other.OriginalFileBytes) &&
                !ReferenceEquals(extension.OriginalFileBytes, archive.OriginalFileBytes),
                "读取会话按文件名合并了不同根目录或根目录/E5的同名源文件");
            File.Delete(Path.Combine(root, "Item.e5"));
            var missingRoot = reader.Validate(root, Definition("items-extended", "Item.e5", 0) with { RowCount = 2 },
                new SourceFileReadSession());
            TestSupport.Assert(!missingRoot.IsUsable && !missingRoot.FileExists,
                "缺少根目录扩展表时错误地把E5图标档案当作扩展数据");

            File.WriteAllBytes(Path.Combine(root, "Shared.bin"), [5, 6, 7, 8]);
            var consistent = reader.Read(root, first, session);
            var fresh = reader.Read(root, first, new SourceFileReadSession());
            TestSupport.Assert((int)consistent.Data.Rows[0]["数值"] == 1 && (int)fresh.Data.Rows[0]["数值"] == 5 &&
                fresh.OriginalFileHash != consistent.OriginalFileHash, "读取会话未提供一致快照，或新会话复用旧磁盘内容");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("DataManagerSourceSession_", StringComparison.Ordinal))
                throw new InvalidOperationException("源读取测试目录清理范围无效。");
            Directory.Delete(full, true);
        }
    }

    private static void SharedOpenPipeline(string basePath)
    {
        var workspace = new ProjectWorkspace();
        workspace.Open(basePath);
        var files = workspace.Documents.Values.Select(document => Path.GetFullPath(document.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var statistics = workspace.LastSourceReadStatistics;
        TestSupport.Assert(statistics.FileReads == statistics.HashComputations && statistics.FileReads == files.Length,
            $"项目打开未按实际文件共享检测/扫描/读取：reads={statistics.FileReads}, hashes={statistics.HashComputations}, files={files.Length}");
        foreach (var group in workspace.Documents.Values.GroupBy(document => Path.GetFullPath(document.FilePath), StringComparer.OrdinalIgnoreCase))
            TestSupport.Assert(group.All(document => ReferenceEquals(document.OriginalFileBytes, group.First().OriginalFileBytes)),
                "项目内相同完整路径的表未共享源字节");
        var session = new SourceFileReadSession();
        var registry = new ProfileRegistry();
        var match = new ProfileDetector(registry).Detect(basePath, session);
        var profile = new ProfileAugmentationService().Apply(basePath, registry.Resolve(match.ProfileId, match.VariantId), session);
        _ = new BaseScanner(registry).Scan(basePath, profile, match, session);
        foreach (var definition in profile.Tables)
        {
            var reader = new BinaryTableReader();
            if (reader.Validate(basePath, definition, session).IsUsable) _ = reader.Read(basePath, definition, session);
        }
        TestSupport.Assert(session.Statistics.FileReads == statistics.FileReads && session.Statistics.HashComputations == statistics.HashComputations,
            "显式检测/扫描/解码流程与项目打开的源读取次数不一致");
    }

    private static TableDefinition Definition(string key, string file, long offset)
        => new(key, key, file, offset, 1, 1, 0, [new("数值", 0, 1, FieldKind.UInt8)], "test");
}
