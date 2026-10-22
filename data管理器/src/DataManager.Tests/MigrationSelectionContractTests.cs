using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Migration;
using DataManager.Core.Models;

namespace DataManager.Tests;

/// <summary>Checks the observable byte and resource contract of selected migrations.</summary>
internal static class MigrationSelectionContractTests
{
    private static readonly (string Profile, string Variant, int IconOffset)[] Layouts =
    [
        ("ccz-6.5", "65-standard-v1", 0x14),
        ("ccz-6.6", "66-standard-v1", 0x0F),
        ("ccz-6.6", "66-legacy-data-v1", 0x14),
        ("ccz-6.6", "66-shanzhai-legacy", 0x0F)
    ];

    public static void Run(string basePath)
    {
        foreach (var source in Layouts)
            foreach (var target in Layouts.Where(layout => layout != source))
                TestSelectedIcons(basePath, source, target);
        TestMixedCommitRollback(basePath);
    }

    /// <summary>Allows the same contract to be checked against two independent real sample layouts.</summary>
    public static void RunCrossSample(string sourceBasePath, string targetBasePath, string sourceVariant, string targetVariant)
        => TestSelectedIcons(sourceBasePath, Layouts.Single(layout => layout.Variant == sourceVariant),
            Layouts.Single(layout => layout.Variant == targetVariant), targetBasePath);

    private static void TestSelectedIcons(string basePath,
        (string Profile, string Variant, int IconOffset) sourceLayout,
        (string Profile, string Variant, int IconOffset) targetLayout, string? targetBasePath = null)
    {
        WithProjects(basePath, targetBasePath ?? basePath, (source, target) =>
        {
            var sourceIcons = new[] { (Id: 0, Icon: 0), (Id: 103, Icon: 17), (Id: 104, Icon: 1), (Id: 254, Icon: 255) };
            SeedIcons(source, sourceLayout, sourceIcons);
            SeedIcons(target, targetLayout, sourceIcons.Select(item => (item.Id, 63)).ToArray());
            Write(source, Path.Combine("E5", "Item.e5"), [0, 1, 2, 3]);
            Write(target, "Item.e5", [4, 5, 6, 7]);
            Write(source, "Unselected.png", [8]);
            Write(target, "Unselected.png", [9]);
            var sourceBefore = Hashes(source);
            var targetBefore = CoreBytes(target);
            var service = new MigrationService();
            var plan = service.Preview(source, target, sourceLayout.Profile, sourceLayout.Variant,
                targetLayout.Profile, targetLayout.Variant);
            var iconChoices = plan.Changes.Select((change, index) => (change, index))
                .Where(item => item.change.TableKey is "items-low" or "items-high" &&
                    item.change.FieldName == "图标" && sourceIcons.Any(icon => icon.Id == item.change.Id) &&
                    item.change.Kind == MigrationChangeKind.Changed)
                .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            TestSupport.Assert(iconChoices.Count == sourceIcons.Length,
                $"{sourceLayout.Variant}→{targetLayout.Variant}没有生成四项图标逻辑字段迁移");

            var result = service.Apply(plan, iconChoices);
            TestSupport.Assert(result.Success && result.AppliedChanges == sourceIcons.Length,
                $"{sourceLayout.Variant}→{targetLayout.Variant}所选图标迁移失败：{string.Join("；", result.Messages)}");
            var after = Open(target, targetLayout);
            var allowed = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, icon) in sourceIcons)
            {
                var document = after.GetDocument(id < 104 ? "items-low" : "items-high");
                var rowIndex = id - document.Definition.BeginId;
                TestSupport.Assert(Convert.ToInt32(document.Data.Rows[rowIndex]["图标"], CultureInfo.InvariantCulture) == icon,
                    $"图标{id}迁移后没有按零起始逻辑值复读，预期{icon}");
                var offset = checked((int)document.Definition.DataOffset + rowIndex * document.Definition.RowSize + targetLayout.IconOffset);
                var relative = Path.GetRelativePath(target, document.FilePath);
                if (!allowed.TryGetValue(relative, out var offsets)) allowed[relative] = offsets = [];
                offsets.Add(offset);
                TestSupport.Assert(File.ReadAllBytes(document.FilePath)[offset] == icon, "图标值没有写入目标Variant正确偏移");
            }
            AssertOnlyAllowedBytesChanged(targetBefore, target, allowed);
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(target, "Item.e5")).SequenceEqual(new byte[] { 4, 5, 6, 7 }) &&
                !File.Exists(Path.Combine(target, "E5", "Item.e5")) &&
                File.ReadAllBytes(Path.Combine(target, "Unselected.png")).SequenceEqual(new byte[] { 9 }),
                "只选图标字段时迁移了未选资源");
            AssertHashes(source, sourceBefore, "图标字段迁移修改了源文件");

            // Select only the archive next: root/E5 storage is relocated to the
            // established target path, while every data byte stays unchanged.
            var dataAfterIcons = CoreBytes(target);
            plan = service.Preview(source, target, sourceLayout.Profile, sourceLayout.Variant,
                targetLayout.Profile, targetLayout.Variant);
            var resourceChoices = plan.Changes.Select((change, index) => (change, index))
                .Where(item => item.change.Kind == MigrationChangeKind.ResourceChanged &&
                    item.change.TableDisplayName == Path.Combine("E5", "Item.e5"))
                .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            TestSupport.Assert(resourceChoices.Count == 1, "宝物资源未唯一映射至目标原有位置");
            result = service.Apply(plan, resourceChoices);
            TestSupport.Assert(result.Success && result.AppliedChanges == 1,
                "只选宝物图标档案迁移失败：" + string.Join("；", result.Messages));
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(target, "Item.e5")).SequenceEqual(new byte[] { 0, 1, 2, 3 }) &&
                !File.Exists(Path.Combine(target, "E5", "Item.e5")), "宝物资源归位时创建了遮蔽副本或改动了索引内容");
            AssertOnlyAllowedBytesChanged(dataAfterIcons, target, new Dictionary<string, HashSet<int>>());
            AssertHashes(source, sourceBefore, "资源归位迁移修改了源文件");
        });
    }

    private static void TestMixedCommitRollback(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(source, "ccz-6.5", "65-standard-v1");
            var persons = workspace.GetDocument("persons");
            persons.Data.Rows[0]["武力"] = (Convert.ToInt32(persons.Data.Rows[0]["武力"], CultureInfo.InvariantCulture) + 1) % 256;
            TestSupport.Assert(workspace.Save().Saved, "混合回滚夹具保存失败");
            Write(source, "A.png", [1]); Write(target, "A.png", [2]);
            Write(source, "Z.png", [3]); Write(target, "Z.png", [4]);
            var sourceBefore = Hashes(source);
            var targetBefore = CoreBytes(target);
            var resourceBefore = new Dictionary<string, string>
            {
                ["A.png"] = HashService.Sha256(Path.Combine(target, "A.png")),
                ["Z.png"] = HashService.Sha256(Path.Combine(target, "Z.png"))
            };
            var service = new MigrationService();
            var plan = service.Preview(source, target, "ccz-6.5", "65-standard-v1", "ccz-6.5", "65-standard-v1");
            var choices = plan.Changes.Select((change, index) => (change, index))
                .Where(item => item.change.TableKey == "persons" && item.change.Id == 0 && item.change.FieldName == "武力" ||
                    item.change.Kind == MigrationChangeKind.ResourceChanged && item.change.TableDisplayName is "A.png" or "Z.png")
                .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            TestSupport.Assert(choices.Count == 3, "混合回滚夹具未生成一项数据和两项资源");
            var lastResource = plan.Changes.Select((change, index) => (change, index))
                .Last(item => choices.ContainsKey(item.index) && item.change.Kind == MigrationChangeKind.ResourceChanged).change;
            using var blocker = new FileStream(Path.Combine(target, lastResource.TargetRelativePath ?? lastResource.TableDisplayName),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = service.Apply(plan, choices);
            TestSupport.Assert(!result.Success && result.BackupPath != null && File.Exists(Path.Combine(result.BackupPath, "failure.json")),
                "混合数据/资源提交失败没有产生回滚记录");
            AssertOnlyAllowedBytesChanged(targetBefore, target, new Dictionary<string, HashSet<int>>());
            AssertHashes(target, resourceBefore, "混合提交失败后资源没有恢复");
            AssertHashes(source, sourceBefore, "混合提交失败修改了源文件");
            TestSupport.Assert(!File.Exists(Path.Combine(target, ".data管理器历史", "manifest.jsonl")), "混合提交失败遗留了目标保存历史");
            TestSupport.Assert(!Directory.EnumerateFiles(target, "*.tmp", SearchOption.AllDirectories).Any(), "混合提交失败遗留临时文件");
        });
    }

    private static void SeedIcons(string root, (string Profile, string Variant, int IconOffset) layout,
        (int Id, int Icon)[] icons)
    {
        var workspace = Open(root, layout);
        foreach (var group in icons.GroupBy(item => item.Id < 104 ? "items-low" : "items-high"))
        {
            var document = workspace.GetDocument(group.Key);
            var iconField = document.Definition.Fields.Single(field => field.Name == "图标");
            TestSupport.Assert(iconField.Kind == FieldKind.UInt8 && iconField.Policy == FieldPolicy.Editable && iconField.Offset == layout.IconOffset,
                $"{layout.Variant}图标字段不是已约定的可写UInt8/目标偏移");
            var bytes = File.ReadAllBytes(document.FilePath);
            foreach (var (id, icon) in group)
            {
                var offset = checked((int)document.Definition.DataOffset + (id - document.Definition.BeginId) * document.Definition.RowSize);
                // Keep category geometry identical and unrelated to this test.
                bytes[offset + 0x11] = 4;
                bytes[offset + 0x12] = 5;
                bytes[offset + layout.IconOffset] = checked((byte)icon);
            }
            File.WriteAllBytes(document.FilePath, bytes);
        }
    }

    private static ProjectWorkspace Open(string root, (string Profile, string Variant, int IconOffset) layout)
    {
        var workspace = new ProjectWorkspace(); workspace.Open(root, layout.Profile, layout.Variant); return workspace;
    }

    private static Dictionary<string, byte[]> CoreBytes(string root)
        => new[] { "Data.e5", "Star.e5", "Ekd5.exe", "Imsg.e5" }
            .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Hashes(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), HashService.Sha256, StringComparer.OrdinalIgnoreCase);

    private static void AssertHashes(string root, IReadOnlyDictionary<string, string> hashes, string reason)
    {
        foreach (var pair in hashes)
            TestSupport.Assert(File.Exists(Path.Combine(root, pair.Key)) && HashService.Sha256(Path.Combine(root, pair.Key)) == pair.Value,
                reason + "：" + pair.Key);
    }

    private static void AssertOnlyAllowedBytesChanged(IReadOnlyDictionary<string, byte[]> before, string root,
        IReadOnlyDictionary<string, HashSet<int>> allowed)
    {
        foreach (var pair in before)
        {
            var after = File.ReadAllBytes(Path.Combine(root, pair.Key));
            TestSupport.Assert(after.Length == pair.Value.Length, "迁移改变了目标文件长度：" + pair.Key);
            for (var index = 0; index < after.Length; index++)
                if (after[index] != pair.Value[index] && !(allowed.TryGetValue(pair.Key, out var offsets) && offsets.Contains(index)))
                    throw new InvalidOperationException($"所选字段迁移改写了目标未选字节：{pair.Key}+0x{index:X}");
        }
    }

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
    }

    private static void WithProjects(string basePath, Action<string, string> test)
        => WithProjects(basePath, basePath, test);

    private static void WithProjects(string sourceBasePath, string targetBasePath, Action<string, string> test)
    {
        var source = TestSupport.CreateCoreCopy(sourceBasePath); var target = TestSupport.CreateCoreCopy(targetBasePath);
        try { test(source, target); }
        finally { Directory.Delete(source, true); Directory.Delete(target, true); }
    }
}
