using System.Globalization;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;

namespace DataManager.Tests;

internal static class ExtendDataRealBaseMatrixTests
{
    private static readonly string[] FileNames = ["Data.e5", "Star.e5", "Imsg.e5", "Ekd5.exe", "Effect.e5", "Effectmsg.e5", "MeffStudy.e5", "Forge.e5"];

    public static void Run(string repositoryRoot, string reportDirectory)
    {
        var baseRoot = Path.Combine(repositoryRoot, "基底");
        Directory.CreateDirectory(reportDirectory);
        var results = new List<object>();
        var roots = Directory.EnumerateFiles(baseRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("6.4", StringComparison.OrdinalIgnoreCase) && !path.Contains("_CCZModStudio_Backups", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetDirectoryName(path)!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(root => File.Exists(ProjectFileLocator.Resolve(root, "Data.e5")))
            .OrderBy(root => root, StringComparer.CurrentCultureIgnoreCase).ToArray();
        foreach (var root in roots)
        {
            Console.WriteLine($"EXTEND_REAL_BASE_START {Path.GetRelativePath(baseRoot, root)}");
            var before = HashSources(root);
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            Console.WriteLine("EXTEND_REAL_STAGE original-open");
            var service = new ExtendDataService(workspace);
            var categories = new List<object>();
            foreach (var category in Enum.GetValues<ExtendDataCategory>())
            {
                var status = service.GetStatus(category);
                categories.Add(new { Category = category.ToString(), status.Available, status.RowCount, status.FilePath,
                    FileLength = File.Exists(status.FilePath) ? (long?)new FileInfo(status.FilePath).Length : null, status.Message });
                var expectedSize = category switch { ExtendDataCategory.Effects => 1024, ExtendDataCategory.Growth => 8, _ => 64 };
                if (File.Exists(status.FilePath))
                {
                    var length = new FileInfo(status.FilePath).Length;
                    TestSupport.Assert(status.Available && status.RowCount == length / expectedSize,
                        $"真实基底容量错误：{root}/{category}；{status.Message}");
                }
                else TestSupport.Assert(!status.Available, $"缺失文件错误开放：{root}/{category}");
            }
            TestSupport.Assert(!workspace.IsDirty, "打开真实基底产生了草稿：" + root);
            if (service.GetStatus(ExtendDataCategory.Effects).Available)
            {
                var names = service.GetEntityOptions(ExtendDataCategory.Effects);
                if (workspace.Documents.TryGetValue("persons", out var persons) && persons.Data.Rows.Count > 0)
                {
                    var name = Convert.ToString(persons.Data.Rows[0]["名称"], CultureInfo.InvariantCulture);
                    var readable = !string.IsNullOrWhiteSpace(name) && !EncodingService.HasPreservedInvalidBytes(name) && !name.Any(char.IsControl);
                    TestSupport.Assert(names[0].Id == 0 && (readable ? names[0].Name == name : !string.IsNullOrWhiteSpace(names[0].Name)),
                        "人物特效列表没有使用人物名或对损坏名称保留ID：" + root);
                }
                TestSupport.Assert(service.GetEffectOptions().Single(option => option.Id == 255).Name == "无", "特效缺少255无选项");
            }
            var editedFiles = VerifyWriteCopy(root);
            var after = HashSources(root);
            TestSupport.Assert(before.Count == after.Count && before.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value),
                "原始真实基底被测试修改：" + root);
            results.Add(new { Root = root, Profile = workspace.ActiveProfile.ProfileId, Variant = workspace.ActiveProfile.VariantId,
                workspace.ActiveProfile.LayoutRevision, Categories = categories, NameSource = service.GetEffectNameSource(),
                Diagnostics = service.GetDiagnostics(), CopyWriteFiles = editedFiles, OriginalHashesUnchanged = true,
                PhysicalFileEditingVerified = true, EngineConsumptionNotExecuted = true,
                ReportNote = "本次验证物理文件读取、编辑副本、保存复读及历史撤销重做；未运行游戏验证四类拓展功能。名称探测中的两个消费者仅证明名称池来源。" });
            Console.WriteLine($"EXTEND_REAL_BASE_OK {Path.GetRelativePath(baseRoot, root)} files={editedFiles}");
            GC.Collect(); GC.WaitForPendingFinalizers();
        }
        TestSupport.Assert(roots.Length >= 10, "真实基底矩阵没有覆盖足够的6.5/6.6项目");
        File.WriteAllText(Path.Combine(reportDirectory, "真实基底矩阵.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"EXTEND_REAL_BASE_MATRIX_OK projects={roots.Length}");
    }

    private static Dictionary<string, string> HashSources(string root) => FileNames.Select(name => ProjectFileLocator.Resolve(root, name))
        .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, path => HashService.Sha256(path), StringComparer.OrdinalIgnoreCase);

    private static int VerifyWriteCopy(string root)
    {
        var extensionNames = FileNames.Skip(4).Where(name => File.Exists(ProjectFileLocator.Resolve(root, name))).ToArray();
        if (extensionNames.Length == 0) return 0;
        var copy = TestSupport.CreateCoreCopy(root, restoreKnown65Snapshot: false);
        try
        {
            foreach (var name in extensionNames)
            {
                var relative = Path.GetRelativePath(root, ProjectFileLocator.Resolve(root, name));
                var target = Path.Combine(copy, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(ProjectFileLocator.Resolve(root, name), target);
            }
            var workspace = new ProjectWorkspace(); workspace.Open(copy);
            Console.WriteLine("EXTEND_REAL_STAGE copy-open");
            var service = new ExtendDataService(workspace);
            var originals = extensionNames.ToDictionary(name => name, name => File.ReadAllBytes(ProjectFileLocator.Resolve(copy, name)));
            var targets = new[]
            {
                (ExtendDataCategory.Effects, "槽0特效值", "Effect.e5", 1),
                (ExtendDataCategory.Growth, "Param1", "Effectmsg.e5", 1),
                (ExtendDataCategory.Strategies, "策略0次数", "MeffStudy.e5", 1),
                (ExtendDataCategory.Forge, "价格", "Forge.e5", 54)
            };
            foreach (var (category, field, _, _) in targets)
            {
                if (!service.GetStatus(category).Available) continue;
                var document = service.GetDocument(category)!;
                var old = Convert.ToInt64(document.Data.Rows[0][field], CultureInfo.InvariantCulture);
                service.ApplyEdits(category, 0, new Dictionary<string, object?> { [field] = old ^ 1L });
                Console.WriteLine("EXTEND_REAL_STAGE edit " + category);
            }
            var saved = workspace.Save();
            Console.WriteLine("EXTEND_REAL_STAGE save");
            TestSupport.Assert(saved.Saved, $"真实副本写回失败：{root}；{string.Join("；", saved.Messages)}");
            foreach (var (_, _, name, offset) in targets.Where(target => originals.ContainsKey(target.Item3)))
            {
                var old = originals[name]; var actual = File.ReadAllBytes(ProjectFileLocator.Resolve(copy, name));
                TestSupport.Assert(actual.Length == old.Length && Enumerable.Range(0, old.Length).Where(index => actual[index] != old[index]).SequenceEqual([offset]),
                    $"真实副本单字段改动触碰了无关字节：{root}/{name}");
            }
            var reopened = new ProjectWorkspace(); reopened.Open(copy);
            Console.WriteLine("EXTEND_REAL_STAGE reopened");
            TestSupport.Assert(!reopened.IsDirty, "保存重开后产生意外草稿");
            TestSupport.Assert(workspace.UndoSaved().Success, "真实副本历史撤回失败");
            Console.WriteLine("EXTEND_REAL_STAGE undo");
            foreach (var pair in originals)
                TestSupport.Assert(pair.Value.SequenceEqual(File.ReadAllBytes(ProjectFileLocator.Resolve(copy, pair.Key))), "历史撤回未还原扩展文件：" + pair.Key);
            TestSupport.Assert(workspace.RedoSaved().Success, "真实副本历史重做失败");
            Console.WriteLine("EXTEND_REAL_STAGE redo");
            return originals.Count;
        }
        finally { Directory.Delete(copy, true); }
    }
}
