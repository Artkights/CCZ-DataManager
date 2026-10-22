using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.History;
using DataManager.Core.Migration;
using DataManager.Images;
using ImageManager.Core;

namespace DataManager.Tests;

/// <summary>Storage ownership, legacy recovery and migration rollback must survive the history relocation.</summary>
internal static class HistoryLocationTests
{
    public static void Run(string basePath)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "DataManagerHistoryLocation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var executableDirectory = Path.Combine(sandbox, "工具所在目录");
            using var scope = ProjectHistoryPaths.UseBaseDirectory(executableDirectory);
            var a = Project("甲", "同名项目");
            var b = Project("乙", "同名项目");
            DataIsolation(a, b, executableDirectory);
            LegacyData(Project("旧版", "历史兼容项目"));
            ImageIsolationAndLegacy(a, b, Project("旧版", "形象兼容项目"), basePath, sandbox);
            MigrationRollback(Project("迁移", "源项目"), Project("迁移", "目标项目"));
            Console.WriteLine("HISTORY_LOCATION_TESTS_OK");

            string Project(string parent, string name)
            {
                var root = Path.Combine(sandbox, parent, name);
                Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                var copy = TestSupport.CreateCoreCopy(basePath);
                Directory.Move(copy, root);
                return root;
            }
        }
        finally
        {
            if (!Path.GetFullPath(sandbox).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(sandbox).StartsWith("DataManagerHistoryLocation_", StringComparison.Ordinal))
                throw new InvalidOperationException("历史测试清理范围无效。");
            Directory.Delete(sandbox, true);
        }
    }

    private static void DataIsolation(string a, string b, string executableDirectory)
    {
        var store = new HistoryStore();
        TestSupport.Assert(store.Load(a).Count == 0 && store.Load(b).Count == 0 && !Directory.Exists(executableDirectory),
            "只读历史查询为新项目创建了目录");
        TestSupport.Assert(ProjectHistoryPaths.GetProjectStorageRoot(a) != ProjectHistoryPaths.GetProjectStorageRoot(b) &&
            Path.GetDirectoryName(ProjectHistoryPaths.GetProjectStorageRoot(a)) == Path.Combine(executableDirectory, "同名项目_历史记录"),
            "同名项目未在 exe 同级名称目录内按完整路径隔离");
        using (ProjectHistoryPaths.UseBaseDirectory(AppContext.BaseDirectory))
            TestSupport.Assert(ProjectHistoryPaths.GetProjectStorageRoot(a).StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
                "默认历史路径没有使用应用程序目录");
        var wa = new ProjectWorkspace(); wa.Open(a);
        var wb = new ProjectWorkspace(); wb.Open(b);
        var originalA = File.ReadAllBytes(Path.Combine(a, "Data.e5"));
        SaveAbility(wa, 1); SaveAbility(wb, 2);
        var savedB = HashService.Sha256(Path.Combine(b, "Data.e5"));
        var alias = a.ToUpperInvariant() + Path.DirectorySeparatorChar;
        TestSupport.Assert(store.Load(alias).Count == 1 && store.GetRoot(alias).Equals(store.GetRoot(a), StringComparison.OrdinalIgnoreCase),
            "路径大小写或末尾分隔符导致同项目数据历史分裂");
        TestSupport.Assert(wa.UndoSaved().Success && File.ReadAllBytes(Path.Combine(a, "Data.e5")).SequenceEqual(originalA) &&
            HashService.Sha256(Path.Combine(b, "Data.e5")) == savedB && wb.GetHistory().Single().Status == DataManager.Core.Models.HistoryStatus.Active,
            "同名项目的数据撤回串到了另一个项目");
        TestSupport.Assert(!Directory.Exists(Path.Combine(a, ".data管理器历史")) && !Directory.Exists(Path.Combine(b, ".data管理器历史")),
            "新数据记录仍写入项目目录");
    }

    private static void LegacyData(string root)
    {
        var workspace = new ProjectWorkspace(); workspace.Open(root);
        var original = File.ReadAllBytes(Path.Combine(root, "Data.e5"));
        SaveAbility(workspace, 1);
        var current = Path.Combine(workspace.History.GetRoot(root), "manifest.jsonl");
        var legacy = ProjectHistoryPaths.GetLegacyDataManifest(root);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.Move(current, legacy);
        File.AppendAllText(legacy, "{malformed legacy evidence\n");
        var legacyHash = HashService.Sha256(legacy);
        workspace.Open(root);
        TestSupport.Assert(workspace.GetHistory().Count == 1 && !File.Exists(current), "旧数据历史读取丢失或只读查询产生了新 manifest");
        SaveAbility(workspace, 1);
        TestSupport.Assert(workspace.GetHistory().Count == 2 && File.ReadAllText(current).Contains("{malformed legacy evidence", StringComparison.Ordinal) &&
            HashService.Sha256(legacy) == legacyHash, "首次新位置保存没有保留旧历史原行，或改写了旧证据");
        TestSupport.Assert(workspace.UndoSaved().Success && workspace.UndoSaved().Success &&
            File.ReadAllBytes(Path.Combine(root, "Data.e5")).SequenceEqual(original), "新旧数据历史不能连续逐字节撤回");
    }

    private static void ImageIsolationAndLegacy(string a, string b, string legacyRoot, string basePath, string sandbox)
    {
        foreach (var root in new[] { a, b, legacyRoot }) SeedImageProject(root, basePath);
        var profile = new ProjectProfileService().Detect(legacyRoot);
        var old = new HistoryService(Path.Combine(legacyRoot, ".data管理器形象历史"));
        var image = Path.Combine(legacyRoot, "Logo.e5");
        var original = File.ReadAllBytes(image);
        var source = Path.Combine(legacyRoot, "素材.png");
        var descriptor = new ImageCatalogService().BuildCatalog(profile).Single(item => item.Key == "Logo.e5");
        var mutations = new ImageMutationService(old);
        var result = mutations.Commit(profile, mutations.PlanAppend(profile, descriptor, [source]));
        TestSupport.Assert(result.Success, "旧形象历史前置保存失败");
        var saved = File.ReadAllBytes(image);
        var external = Path.Combine(sandbox, "项目外改名素材"); Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "旧素材.txt"), "保留外部目录批量改名能力");
        var renames = new BatchRenameService(old);
        var renamed = renames.Apply(profile, renames.Preview(external, new RenameRule("旧", "新", "", "", null, false, false)));
        TestSupport.Assert(renamed.Success, "旧批量改名历史前置操作失败");
        var oldJournal = Path.Combine(old.BackupRoot(profile), "History", "history.jsonl");
        var escaping = JsonNode.Parse(File.ReadAllLines(oldJournal)[0])!;
        escaping["Id"] = Guid.NewGuid().ToString("N");
        escaping["Targets"]![0]!["SnapshotPath"] = Path.Combine(sandbox, "旧历史越界快照.bin");
        File.AppendAllText(oldJournal, escaping.ToJsonString() + Environment.NewLine);
        var currentService = ProjectImageHistory.Create(ProjectImageHistory.Canonicalize(profile));
        var currentJournal = Path.Combine(currentService.BackupRoot(ProjectImageHistory.Canonicalize(profile)), "History", "history.jsonl");
        var history = new ResourceHistoryAdapter();
        TestSupport.Assert(history.List(legacyRoot).Count == 2 && !File.Exists(currentJournal), "旧形象历史的只读列表未兼容、接受越界快照或产生了新日志");
        // A byte-identical copied project must not gain the original absolute resource targets or external rename authority.
        CopyTree(Path.Combine(legacyRoot, ".data管理器形象历史"), Path.Combine(b, ".data管理器形象历史"));
        File.WriteAllBytes(Path.Combine(b, "Logo.e5"), saved);
        TestSupport.Assert(history.List(b).Count == 0 && !history.Undo(b, result.HistoryId!).Success &&
            File.ReadAllBytes(image).SequenceEqual(saved), "复制旧形象目录后，另一个项目取得了原项目的恢复权限");
        TestSupport.Assert(history.Undo(legacyRoot, result.HistoryId!).Success && File.ReadAllBytes(image).SequenceEqual(original),
            "旧形象历史导入新位置后不能撤回");
        var records = currentService.List(ProjectImageHistory.Canonicalize(profile));
        TestSupport.Assert(records.SelectMany(record => record.Targets).All(target =>
            ProjectHistoryPaths.IsInside(target.SnapshotPath, currentService.BackupRoot(ProjectImageHistory.Canonicalize(profile))) &&
            ProjectHistoryPaths.IsInside(target.AfterSnapshotPath, currentService.BackupRoot(ProjectImageHistory.Canonicalize(profile)))),
            "迁移后的形象历史仍引用旧快照");
        Directory.Delete(Path.Combine(legacyRoot, ".data管理器形象历史"), true);
        TestSupport.Assert(history.Redo(legacyRoot, result.HistoryId!).Success && File.ReadAllBytes(image).SequenceEqual(saved) &&
            history.Undo(legacyRoot, renamed.HistoryId!).Success && File.Exists(Path.Combine(external, "旧素材.txt")) &&
            history.Redo(legacyRoot, renamed.HistoryId!).Success && File.Exists(Path.Combine(external, "新素材.txt")),
            "旧目录删除后无法用新快照恢复，或外部批量改名撤回能力受损");
        using var resources = new ResourceCatalogAdapter();
        var aa = resources.Append(a, "Logo.e5", [Path.Combine(a, "素材.png")]);
        var bb = resources.Append(b, "Logo.e5", [Path.Combine(b, "素材.png")]);
        TestSupport.Assert(aa.Success && bb.Success && history.List(a.ToUpperInvariant() + Path.DirectorySeparatorChar).Count == 1 && history.List(b).Count == 1,
            "同名项目形象历史串用，或大小写/尾分隔符产生内部 hash 分裂");
        var savedB = HashService.Sha256(Path.Combine(b, "Logo.e5"));
        TestSupport.Assert(history.Undo(a.ToUpperInvariant() + Path.DirectorySeparatorChar, aa.HistoryId!).Success &&
            HashService.Sha256(Path.Combine(b, "Logo.e5")) == savedB && history.Redo(a, aa.HistoryId!).Success,
            "形象路径变体无法恢复，或修改了同名项目");
        var pa = ProjectImageHistory.Canonicalize(new ProjectProfileService().Detect(a));
        var journal = Path.Combine(ProjectImageHistory.Create(pa).BackupRoot(pa), "History", "history.jsonl");
        var journalBytes = File.ReadAllBytes(journal);
        var before = HashService.Sha256(Path.Combine(a, "Logo.e5"));
        try
        {
            var lines = File.ReadAllLines(journal);
            var node = JsonNode.Parse(lines[0])!;
            node["Targets"]![0]!["SnapshotPath"] = Path.Combine(sandbox, "越界快照.bin");
            lines[0] = node.ToJsonString(); File.WriteAllLines(journal, lines);
            TestSupport.Assert(!history.List(a).Single(entry => !entry.IsTransition).CanUndo && !history.Undo(a, aa.HistoryId!).Success &&
                HashService.Sha256(Path.Combine(a, "Logo.e5")) == before, "形象恢复允许读取新历史目录以外的快照");
            node["Targets"]![0]!["TargetPath"] = Path.Combine(b, "Logo.e5");
            lines[0] = node.ToJsonString(); File.WriteAllLines(journal, lines);
            TestSupport.Assert(!history.Undo(a, aa.HistoryId!).Success && HashService.Sha256(Path.Combine(b, "Logo.e5")) == savedB,
                "形象历史绝对 TargetPath 可以写入另一个项目");
        }
        finally { File.WriteAllBytes(journal, journalBytes); }
    }

    private static void MigrationRollback(string source, string target)
    {
        var sourceWorkspace = new ProjectWorkspace(); sourceWorkspace.Open(source); SaveAbility(sourceWorkspace, 3);
        var targetWorkspace = new ProjectWorkspace(); targetWorkspace.Open(target); SaveAbility(targetWorkspace, 1);
        var manifest = Path.Combine(targetWorkspace.History.GetRoot(target), "manifest.jsonl");
        var beforeManifest = File.ReadAllBytes(manifest);
        var before = File.ReadAllBytes(Path.Combine(target, "Data.e5"));
        var service = new MigrationService();
        var result = service.Apply(service.Preview(source, target), includeAllWritable: true, progress: new FailAtCompletion());
        TestSupport.Assert(!result.Success && File.ReadAllBytes(manifest).SequenceEqual(beforeManifest) &&
            File.ReadAllBytes(Path.Combine(target, "Data.e5")).SequenceEqual(before), "迁移提交末尾失败没有恢复新位置 manifest 和文件");
        TestSupport.Assert(result.BackupPath != null && ProjectHistoryPaths.IsInside(result.BackupPath, ProjectHistoryPaths.GetMigrationRoot(target)) &&
            !Directory.Exists(Path.Combine(target, ".data管理器迁移临时")), "迁移备份没有写入新历史目录，或留下临时项目/历史");
        result = service.Apply(service.Preview(source, target), includeAllWritable: true);
        TestSupport.Assert(result.Success && targetWorkspace.History.Load(target).Count == 2 &&
            !Directory.Exists(Path.Combine(target, ".data管理器迁移备份")), "迁移成功未延续目标历史或仍使用旧备份目录");
    }

    private static void SaveAbility(ProjectWorkspace workspace, int delta)
    {
        var row = workspace.GetDocument("persons").Data.Rows[0];
        row["武力"] = (Convert.ToInt32(row["武力"]) + delta) % 256;
        var saved = workspace.Save();
        TestSupport.Assert(saved.Saved, "历史位置前置保存失败：" + string.Join("；", saved.Messages));
    }

    private static void SeedImageProject(string root, string basePath)
    {
        foreach (var name in new[] { "Itemicon.dll", "Mgcicon.dll" })
        {
            var source = new[] { Path.Combine(basePath, name), Path.Combine(basePath, "E5", name) }.First(File.Exists);
            File.Copy(source, Path.Combine(root, name), true);
        }
        var png = Png(Color.Blue);
        var offset = E5Archive.IndexOffset + E5Archive.EntrySize;
        var bytes = new byte[offset + png.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset), png.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset + 4), png.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset + 8), offset);
        png.CopyTo(bytes, offset);
        File.WriteAllBytes(Path.Combine(root, "Logo.e5"), bytes);
        File.WriteAllBytes(Path.Combine(root, "素材.png"), Png(Color.Orange));
    }

    private static byte[] Png(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
    }

    private sealed class FailAtCompletion : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value)
        {
            if (value.Stage == "迁移完成") throw new IOException("测试：提交完成阶段故障");
        }
    }
}
