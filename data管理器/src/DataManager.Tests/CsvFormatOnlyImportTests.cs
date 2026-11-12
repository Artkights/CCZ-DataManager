using System.Globalization;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class CsvFormatOnlyImportTests
{
    public static void Run(string basePath)
    {
        WithProject(basePath, ImportSaveAndHistory);
        WithProject(basePath, FailedImportLeavesNoAcceptedValues);
        Console.WriteLine("CSV_FORMAT_ONLY_IMPORT_TESTS_OK");
    }

    private static void ImportSaveAndHistory(string root, ProjectWorkspace workspace)
    {
        var persons = workspace.GetDocument("persons");
        var before = File.ReadAllBytes(persons.FilePath);
        var csv = Path.Combine(root, "不同来源人物.csv");
        Write(csv, "ID,默认武器\r\n0,4\r\n");
        TestSupport.Assert(workspace.ImportCsv(persons, csv).ChangedCells == 1 && Value(persons, 0, "默认武器") == 4,
            "合法 byte CSV 仍被装备类别或来源身份拒绝");
        Write(csv, "ID,默认武器\r\n0,5\r\n");
        TestSupport.Assert(workspace.ImportCsv(persons, csv).ChangedCells == 1 && Value(persons, 0, "默认武器") == 5,
            "连续 CSV 导入未替换同一字段");
        workspace.Commands.Undo();
        TestSupport.Assert(Value(persons, 0, "默认武器") == 4, "撤回第二次 CSV 未恢复第一次导入值");
        workspace.Commands.Undo();
        TestSupport.Assert(!workspace.IsDirty && Value(persons, 0, "默认武器") == 255, "连续 CSV 未完整撤回");
        workspace.Commands.Redo();
        TestSupport.Assert(Value(persons, 0, "默认武器") == 4, "第一次 CSV 无法重做");
        workspace.Commands.Redo();
        TestSupport.Assert(Value(persons, 0, "默认武器") == 5 && File.ReadAllBytes(persons.FilePath).SequenceEqual(before),
            "第二次 CSV 无法重做或导入提前修改了磁盘");
        Reject(() => workspace.ApplyCompositeTransaction("手工入口仍检查类别", [new("persons", 0, "默认武器", 4)]),
            "CSV 具体值来源放宽了普通手工编辑入口");

        // A business reference and an uninstalled strategy class are both byte
        // values. Their CSV import must survive the later Save validation too.
        var shop = workspace.GetDocument("shops");
        var shopField = shop.Definition.Fields.First(field => field.ReferenceTable == "items" && field.Policy == FieldPolicy.Editable);
        var shopCsv = Path.Combine(root, "商店.csv");
        Write(shopCsv, $"ID,{shopField.Name}\r\n0,4\r\n");
        var strategy = workspace.GetDocument(StrategyEffectClassService.TableKey);
        var strategyCsv = Path.Combine(root, "策略.csv");
        Write(strategyCsv, "ID," + StrategyEffectClassService.FieldName + "\r\n0,255\r\n");
        var imports = workspace.ImportCsvBundle(new Dictionary<string, string>
            { [shop.Definition.Key] = shopCsv, [strategy.Definition.Key] = strategyCsv });
        TestSupport.Assert(imports.ChangedCells == 2 && Value(shop, 0, shopField.Name) == 4 &&
            Value(strategy, 0, StrategyEffectClassService.FieldName) == 255,
            "合法 CSV 商店引用或自定义策略效果类未导入");
        var save = workspace.Save();
        TestSupport.Assert(save.Saved && !workspace.IsDirty, "CSV 合法字节在保存阶段仍受业务限制：" + string.Join("；", save.Messages));
        var reopened = Open(root);
        TestSupport.Assert(Value(reopened.GetDocument("persons"), 0, "默认武器") == 5 &&
            Value(reopened.GetDocument("shops"), 0, shopField.Name) == 4 &&
            Value(reopened.GetDocument(StrategyEffectClassService.TableKey), 0, StrategyEffectClassService.FieldName) == 255,
            "CSV 保存后重新打开丢失物理值");
        Reject(() => reopened.ApplyCompositeTransaction("重新打开后手工仍校验", [new("persons", 0, "默认武器", 4)]),
            "重新打开后普通手工编辑绕过装备类别规则");
        TestSupport.Assert(reopened.UndoSaved(save.History!.OperationId).Success &&
            Value(reopened.GetDocument("persons"), 0, "默认武器") == 255,
            "CSV 已保存历史不能撤回");
        TestSupport.Assert(reopened.RedoSaved(save.History.OperationId).Success &&
            Value(reopened.GetDocument("persons"), 0, "默认武器") == 5 &&
            Value(reopened.GetDocument(StrategyEffectClassService.TableKey), 0, StrategyEffectClassService.FieldName) == 255,
            "CSV 已保存历史不能重做");
    }

    private static void FailedImportLeavesNoAcceptedValues(string root, ProjectWorkspace workspace)
    {
        var persons = workspace.GetDocument("persons");
        var before = File.ReadAllBytes(persons.FilePath);
        var csv = Path.Combine(root, "晚失败人物.csv");
        Write(csv, "ID,默认武器\r\n0,4\r\n1,256\r\n");
        Reject(() => workspace.ImportCsv(persons, csv), "UInt8 越界值未拒绝");
        TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo && Value(persons, 0, "默认武器") == 255,
            "晚失败 CSV 留下前序值或草稿命令");
        AssertDirectEditStillRejected();

        Write(csv, "ID,默认武器\r\n0,4\r\n");
        var invalid = Path.Combine(root, "错误列.csv");
        Write(invalid, "ID,不存在的列\r\n0,1\r\n");
        Reject(() => workspace.ImportCsvBundle(new Dictionary<string, string>
            { ["persons"] = csv, ["job-series"] = invalid }), "后续文件错误未整体拒绝 CSV 包");
        TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo && Value(persons, 0, "默认武器") == 255,
            "失败 CSV 包留下已解析表的数据或命令");
        AssertDirectEditStillRejected();
        TestSupport.Assert(File.ReadAllBytes(persons.FilePath).SequenceEqual(before), "失败 CSV 操作或保存改写了磁盘");

        void AssertDirectEditStillRejected()
        {
            persons.Data.Rows[0]["默认武器"] = 4;
            TestSupport.Assert(!workspace.Save().Saved && File.ReadAllBytes(persons.FilePath).SequenceEqual(before),
                "失败 CSV 泄漏了具体值许可，导致随后直接草稿绕过保存校验");
            persons.Data.Rows[0]["默认武器"] = 255;
            TestSupport.Assert(!workspace.IsDirty, "失败保存恢复原值后仍有草稿");
        }
    }

    private static void WithProject(string source, Action<string, ProjectWorkspace> test)
    {
        var root = TestSupport.CreateCoreCopy(source);
        try
        {
            var setup = Open(root);
            var persons = setup.GetDocument("persons");
            var personBytes = File.ReadAllBytes(persons.FilePath);
            var weapon = persons.Definition.Fields.Single(field => field.Name == "默认武器");
            foreach (var row in new[] { 0, 1 }) personBytes[Offset(persons, row) + weapon.Offset] = 255;
            File.WriteAllBytes(persons.FilePath, personBytes);
            var items = setup.GetDocument("items-low");
            var itemBytes = File.ReadAllBytes(items.FilePath);
            var type = items.Definition.Fields.Single(field => field.Name == "宝物类型");
            var name = items.Definition.Fields.Single(field => field.Name == "名称");
            foreach (var id in new[] { 4, 5 })
            {
                var offset = Offset(items, id - items.Definition.BeginId);
                itemBytes[offset + type.Offset] = 20;
                itemBytes.AsSpan(offset + name.Offset, name.Size).Clear();
            }
            File.WriteAllBytes(items.FilePath, itemBytes);
            var shop = setup.GetDocument("shops");
            var shopField = shop.Definition.Fields.First(field => field.ReferenceTable == "items" && field.Policy == FieldPolicy.Editable);
            var shopBytes = File.ReadAllBytes(shop.FilePath);
            BinaryValueCodec.Encode(255, shopField).CopyTo(shopBytes, Offset(shop, 0) + shopField.Offset);
            File.WriteAllBytes(shop.FilePath, shopBytes);
            var strategy = setup.GetDocument(StrategyEffectClassService.TableKey);
            var strategyBytes = File.ReadAllBytes(strategy.FilePath);
            strategyBytes[Offset(strategy, 0)] = 0;
            File.WriteAllBytes(strategy.FilePath, strategyBytes);
            test(root, Open(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private static ProjectWorkspace Open(string root)
    {
        var workspace = new ProjectWorkspace();
        workspace.Open(root, "ccz-6.5", "65-standard-v1");
        return workspace;
    }

    private static int Offset(TableDocument document, int row)
        => checked((int)(document.Definition.DataOffset + (long)row * document.Definition.RowSize));
    private static int Value(TableDocument document, int row, string field)
        => Convert.ToInt32(document.Data.Rows[row][field], CultureInfo.InvariantCulture);
    private static void Write(string path, string body)
        => File.WriteAllText(path, "#DataManagerCSV,999,other-profile,other-variant,other-table,other-layout\r\n" + body, new UTF8Encoding(true));
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
}
