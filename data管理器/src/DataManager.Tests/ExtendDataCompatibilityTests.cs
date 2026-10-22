using System.Globalization;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Csv;
using DataManager.Core.Editing;
using DataManager.Core.History;
using DataManager.Core.Migration;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class ExtendDataCompatibilityTests
{
    public static void Run(string basePath)
    {
        TestCsvAndHistoryUpgrade(basePath);
        TestMigrationCapacityAndCreation(basePath);
        Console.WriteLine("EXTEND_DATA_COMPATIBILITY_TESTS_OK");
    }

    private static void TestCsvAndHistoryUpgrade(string basePath)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var effects = Enumerable.Repeat((byte)255, 1024).ToArray();
            effects[0] = 17; effects[1] = 21; effects[2] = 77; effects[3] = 9;
            var messages = new byte[2048]; messages[0] = 2; messages[1] = 33; messages[^1] = 99;
            var forge = new byte[64]; forge[58] = 9; forge[59] = 3; forge[60] = 2; forge[61] = 255; forge[62] = 0xCA; forge[63] = 0xFE;
            File.WriteAllBytes(Path.Combine(root, "Effect.e5"), effects);
            File.WriteAllBytes(Path.Combine(root, "Effectmsg.e5"), messages);
            File.WriteAllBytes(Path.Combine(root, "Forge.e5"), forge);
            var workspace = new ProjectWorkspace();
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            var legacy = LayoutRevisionCompatibility.GetLegacyRevisions(root, "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(legacy.Count > 0 && legacy.All(revision => revision != workspace.ActiveProfile.LayoutRevision),
                "未登记冻结的旧扩展布局身份");
            foreach (var revision in legacy)
                TestSupport.Assert(LayoutRevisionCompatibility.IsCompatible(root, "ccz-6.5", "65-standard-v1", revision, workspace.ActiveProfile.LayoutRevision),
                    "已登记的旧布局不能升级");
            TestSupport.Assert(!LayoutRevisionCompatibility.IsCompatible(root, "ccz-6.5", "65-standard-v1", "0000000000000000", workspace.ActiveProfile.LayoutRevision),
                "未知布局修订被错误接受");

            var path = Path.Combine(root, "upgrade.csv");
            var effect = workspace.GetDocument("effect-archive");
            File.WriteAllText(path, Metadata("effect-archive", legacy[0]) + "ID,槽0特效号,槽0特效值\r\n0,17,22\r\n", new UTF8Encoding(true));
            TestSupport.Assert(workspace.ImportCsv(effect, path).ChangedCells == 1 && Number(effect, 0, "槽0经验") == 77 && Number(effect, 0, "槽0剩余回合") == 9,
                "旧 CSV 导入覆盖了新增经验或回合字段");
            workspace.Commands.Undo();
            File.WriteAllText(path, Metadata("effect-archive", "0000000000000000") + "ID,槽0特效值\r\n0,22\r\n", new UTF8Encoding(true));
            AssertRejected(() => workspace.ImportCsv(effect, path), "未知修订 CSV 未拒绝");
            TestSupport.Assert(!workspace.IsDirty, "拒绝未知 CSV 后仍有草稿");

            var growth = workspace.GetDocument("effect-messages");
            TestSupport.Assert(CsvDisplayNameRegistry.ResolveTableKey("拓展特效说明.csv", workspace.Definitions) == "effect-messages",
                "历史成长 CSV 中文文件名不再兼容");
            File.WriteAllText(path, Metadata("effect-messages", legacy[0]) + "ID,启用,Param1\r\n0,2,34\r\n", new UTF8Encoding(true));
            TestSupport.Assert(workspace.ImportCsv(growth, path).ChangedCells == 1 && Number(growth, 0, "Enabled") == 2,
                "旧成长表头或非标准布尔原值未保留");
            workspace.Commands.Undo();
            workspace.ExportCsv(growth, path);
            TestSupport.Assert(File.ReadAllText(path).Contains("吃书初始效果值", StringComparison.Ordinal) && workspace.ImportCsv(growth, path).ChangedCells == 0,
                "新成长 CSV 语义表头或保留行回导失败");
            File.WriteAllText(path, "ID,Param7\r\n255,98\r\n", new UTF8Encoding(true));
            AssertRejected(() => workspace.ImportCsv(growth, path), "ID255 成长保留槽允许写入");

            var recipe = workspace.GetDocument("forge");
            workspace.ExportCsv(recipe, path);
            TestSupport.Assert(workspace.ImportCsv(recipe, path).ChangedCells == 0 && Number(recipe, 0, "材料项数") == 9,
                "未编辑的异常材料项数不能无损回导");
            File.WriteAllText(path, Metadata("forge", legacy[0]) + "ID,价格,保留58,保留59,保留61\r\n0,31,09,03,FF\r\n", new UTF8Encoding(true));
            TestSupport.Assert(workspace.ImportCsv(recipe, path).ChangedCells == 1 && Number(recipe, 0, "材料项数") == 9 && Number(recipe, 0, "剩余次数") == 255,
                "旧 Forge CSV 没有保留已知原始别名");
            workspace.Commands.Undo();
            File.WriteAllText(path, "ID,材料项数,保留58,商店编号,保留59,剩余次数,保留61\r\n0,1,01,4,04,7,07\r\n", new UTF8Encoding(true));
            TestSupport.Assert(workspace.ImportCsv(recipe, path).ChangedCells == 3, "语义列与相同物理字节别名被误判冲突");
            workspace.ExportCsv(recipe, path);
            TestSupport.Assert(workspace.ImportCsv(recipe, path).ChangedCells == 0, "未保存 Forge 草稿导出后无法无损回导");
            workspace.Commands.Undo();
            File.WriteAllText(path, "ID,保留58\r\n0,01\r\n", new UTF8Encoding(true));
            AssertRejected(() => workspace.ImportCsv(recipe, path), "只读原始别名单独篡改没有拒绝");

            // Build a real byte history through the current writer, then freeze
            // its layout identity to the exact v1 revision used by older releases.
            effect.Data.Rows[0]["槽0特效值"] = 22;
            var save = workspace.Save();
            TestSupport.Assert(save.Saved, "兼容测试准备保存失败：" + string.Join("；", save.Messages));
            var store = new HistoryStore();
            var entries = store.Load(root).ToArray();
            entries[^1].LayoutRevision = legacy[0];
            store.Replace(root, entries);
            TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(effect.FilePath).SequenceEqual(effects), "旧布局已保存历史不能撤回");
            TestSupport.Assert(workspace.RedoSaved().Success && File.ReadAllBytes(effect.FilePath)[1] == 22, "旧布局已保存历史不能重做");
            entries = store.Load(root).ToArray(); entries[^1].LayoutRevision = "0000000000000000"; store.Replace(root, entries);
            var before = File.ReadAllBytes(effect.FilePath);
            TestSupport.Assert(!workspace.UndoSaved().Success && File.ReadAllBytes(effect.FilePath).SequenceEqual(before), "未知布局历史被重放或修改文件");
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(root, "Forge.e5"))[62] == 0xCA && File.ReadAllBytes(Path.Combine(root, "Forge.e5"))[63] == 0xFE,
                "无关操作覆盖 Forge 保留尾字节");

            // Whole-profile v1 metadata remains usable for unchanged public tables too.
            foreach (var profile in new ProfileRegistry().Profiles)
            foreach (var variant in profile.Variants)
            {
                var resolved = new ProfileAugmentationService().Apply(root, new ProfileRegistry().Resolve(profile.Id, variant.Id));
                var revisions = LayoutRevisionCompatibility.GetLegacyRevisions(root, profile.Id, variant.Id);
                TestSupport.Assert(revisions.Count > 0 && revisions.All(revision => LayoutRevisionCompatibility.IsCompatible(root, profile.Id, variant.Id, revision, resolved.LayoutRevision)),
                    $"已登记变体旧布局兼容丢失：{profile.Id}/{variant.Id}");
            }
        }
        finally { Directory.Delete(root, true); }

        static string Metadata(string table, string revision) => $"#DataManagerCSV,1,ccz-6.5,65-standard-v1,{table},{revision}\r\n";
    }

    private static void TestMigrationCapacityAndCreation(string basePath)
    {
        var source = TestSupport.CreateCoreCopy(basePath);
        var target = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var effects = new byte[2 * 1024]; effects[2] = 19; effects[1024 + 2] = 71;
            effects[3] = 7;
            File.WriteAllBytes(Path.Combine(source, "Effect.e5"), effects);
            File.WriteAllBytes(Path.Combine(target, "Effect.e5"), new byte[1024]);
            var messages = new byte[255 * 8]; messages[1] = 27;
            File.WriteAllBytes(Path.Combine(source, "Effectmsg.e5"), messages);
            var recipe = new byte[64]; recipe[58] = 1; recipe[59] = 3; recipe[61] = 7;
            File.WriteAllBytes(Path.Combine(source, "Forge.e5"), recipe);
            File.WriteAllBytes(Path.Combine(target, "Forge.e5"), new byte[64]);
            var service = new MigrationService();
            var plan = service.Preview(source, target, "ccz-6.5", "65-standard-v1", "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(plan.Changes.Any(change => change.TableKey == "effect-archive" && change.FieldName == "槽0经验" && change.Id == 0 && change.Kind == MigrationChangeKind.Changed),
                "新增经验字段没有纳入迁移");
            TestSupport.Assert(plan.Changes.Any(change => change.TableKey == "effect-archive" && change.FieldName == "槽0剩余回合" && change.Id == 0 && change.Kind == MigrationChangeKind.Changed),
                "新增回合字段没有纳入迁移");
            foreach (var field in new[] { "材料项数", "商店编号", "剩余次数" })
                TestSupport.Assert(plan.Changes.Any(change => change.TableKey == "forge" && change.FieldName == field && change.Kind == MigrationChangeKind.Changed),
                    $"新增锻造字段没有纳入迁移：{field}");
            TestSupport.Assert(plan.Changes.Any(change => change.TableKey == "effect-archive" && change.Id == 1 && change.Kind == MigrationChangeKind.Incompatible) &&
                !plan.WritableChanges.Any(change => change.TableKey == "effect-archive" && change.Id == 1), "迁移越过目标人物容量");
            var creation = plan.PhysicalFileCreations.Single(item => Path.GetFileName(item.RelativePath) == "Effectmsg.e5");
            TestSupport.Assert(creation.Length == 2048, "从255行源创建的成长表不是完整2048字节");
            var choices = plan.Changes.Select((change, index) => (change, index)).Where(item =>
                item.change.Kind == MigrationChangeKind.PhysicalFileAdded && Path.GetFileName(item.change.TableDisplayName) == "Effectmsg.e5" ||
                item.change.Kind == MigrationChangeKind.Changed && item.change.TableKey is "effect-archive" or "effect-messages" or "forge")
                .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            var result = service.Apply(plan, choices);
            TestSupport.Assert(result.Success && new FileInfo(Path.Combine(target, "Effectmsg.e5")).Length == 2048,
                "迁移未按2048字节新建成长文件：" + string.Join("；", result.Messages));
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(target, "Effect.e5"))[2] == 19 &&
                File.ReadAllBytes(Path.Combine(target, "Effect.e5"))[3] == 7 &&
                File.ReadAllBytes(Path.Combine(target, "Effectmsg.e5"))[1] == 27 &&
                File.ReadAllBytes(Path.Combine(target, "Forge.e5"))[58] == 1 &&
                File.ReadAllBytes(Path.Combine(target, "Forge.e5"))[59] == 3 &&
                File.ReadAllBytes(Path.Combine(target, "Forge.e5"))[61] == 7,
                "新字段迁移后没有按目标容量和Codec复读");

            // The same full byte is valid for an output quantity, even when its
            // old target slot was a material. Preview must not validate it in
            // isolation against the target's pre-migration material boundary.
            var outputRecipe = new byte[64]; outputRecipe[31] = 1; outputRecipe[32] = 200;
            var materialRecipe = new byte[64]; materialRecipe[31] = 1; materialRecipe[32] = 1; materialRecipe[58] = 1;
            File.WriteAllBytes(Path.Combine(source, "Forge.e5"), outputRecipe);
            File.WriteAllBytes(Path.Combine(target, "Forge.e5"), materialRecipe);
            plan = service.Preview(source, target, "ccz-6.5", "65-standard-v1", "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(plan.WritableChanges.Any(change => change.TableKey == "forge" && change.FieldName == "材料1数量"),
                "合法产物完整数量字节按目标旧材料语义被误拒");
            choices = plan.Changes.Select((change, index) => (change, index)).Where(item =>
                item.change.TableKey == "forge" && item.change.Kind == MigrationChangeKind.Changed)
                .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            result = service.Apply(plan, choices);
            TestSupport.Assert(result.Success && File.ReadAllBytes(Path.Combine(target, "Forge.e5"))[32] == 200 &&
                File.ReadAllBytes(Path.Combine(target, "Forge.e5"))[58] == 0,
                "合法材料转产物的组合迁移未通过完整草稿验证");

            outputRecipe[58] = 201;
            File.WriteAllBytes(Path.Combine(source, "Forge.e5"), outputRecipe);
            plan = service.Preview(source, target, "ccz-6.5", "65-standard-v1", "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(plan.Changes.Any(change => change.TableKey == "forge" && change.FieldName == "材料项数" &&
                change.Kind == MigrationChangeKind.Incompatible) &&
                !plan.WritableChanges.Any(change => change.TableKey == "forge" && change.FieldName == "材料项数"),
                "材料项数超界没有在迁移预览标为不兼容");
        }
        finally { Directory.Delete(source, true); Directory.Delete(target, true); }
    }

    private static int Number(TableDocument document, int row, string field) => Convert.ToInt32(document.Data.Rows[row][field], CultureInfo.InvariantCulture);

    private static void AssertRejected(Action action, string message)
    {
        var rejected = false;
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException) { rejected = true; }
        TestSupport.Assert(rejected, message);
    }
}
