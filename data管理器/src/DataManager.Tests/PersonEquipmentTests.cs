using System.Data;
using System.Globalization;
using System.Text;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Migration;
using DataManager.Core.Models;
using DataManager.Core.Profiles;
using DataManager.Core.Workbooks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Control = System.Windows.Forms.Control;

namespace DataManager.Tests;

internal static class PersonEquipmentTests
{
    private static readonly (string Directory, string Profile, string Variant)[] Samples =
    [
        ("洗隋", "ccz-6.4", "64-xisui-v1"),
        ("加强版6.5未加密版", "ccz-6.5", "65-standard-v1"),
        ("曹操传6.6-V7.16 解压密码：66", "ccz-6.6", "66-standard-v1")
    ];
    private static readonly (string Field, ItemCategory Category, int Id)[] Equipment =
    [("默认武器", ItemCategory.Weapon, 200), ("默认防具", ItemCategory.Armor, 2), ("默认辅助", ItemCategory.Accessory, 3)];

    public static void Run(string repository, bool include64 = true)
    {
        ItemTypeTests.Run(); RecordCodecTests.Run();
        var originals = Samples.Select(sample => Path.Combine(repository, "基底", sample.Directory))
            .SelectMany(root => ProfileDetector.CoreFiles.Select(name => ProjectFileLocator.Resolve(root, name)))
            .ToDictionary(path => path, HashService.Sha256);
        foreach (var sample in Samples.Where(sample => include64 || sample.Profile != "ccz-6.4"))
        {
            var root = TestSupport.CreateCoreCopy(Path.Combine(repository, "基底", sample.Directory), restoreKnown65Snapshot: false);
            try
            {
                Seed(root, sample.Profile, sample.Variant);
                var workspace = Open(root, sample.Profile, sample.Variant);
                TestPickersAndDraftRefresh(workspace);
                TestAllEditEntrypoints(workspace);
                TestFinalDraftCategories(workspace);
                TestRawGrid(root);
                Console.WriteLine($"PERSON_EQUIPMENT_OK {sample.Profile} type-only options draft-refresh legacy-values CSV XLSX byte-capacity");
            }
            finally { Directory.Delete(root, true); }
        }
        Migrate(repository, Samples[1], Samples[2]);
        Migrate(repository, Samples[2], Samples[1]);
        TestSupport.Assert(originals.All(pair => HashService.Sha256(pair.Key) == pair.Value), "默认装备回归改写了基底原件");
        Console.WriteLine("PERSON_EQUIPMENT_TESTS_OK");
    }

    private static void TestPickersAndDraftRefresh(ProjectWorkspace workspace)
    {
        var person = workspace.GetDocument("persons");
        TestSupport.Assert(Equipment.All(item => person.Data.Columns.Contains(item.Field)), "当前Profile未开放默认装备三字段");
        using var host = new Form { Location = new Point(-32000, -32000), StartPosition = FormStartPosition.Manual, Size = new Size(1400, 900) };
        using var panel = new BusinessEditorPanel(workspace); host.Controls.Add(panel); host.Show();
        panel.Bind(BusinessPageKind.Persons, 0); Application.DoEvents();
        foreach (var (field, category, id) in Equipment)
        {
            var combo = Editor(panel, field);
            TestSupport.Assert(combo.Options.Any(option => option.Id == id) && combo.Options.Any(option => option.Id == 255) &&
                combo.Options.Where(option => option.Id != 255).All(option => Category(workspace, option.Id) == category),
                field + "下拉混入其他类别或按传统编号排除了正确宝物");
            TestSupport.Assert(combo.Text.Contains("原值（类别不匹配）", StringComparison.Ordinal), field + "存量错类值未明确标记");
        }
        TestSupport.Assert(panel.Flush() && !workspace.IsDirty, "只浏览/离开错类存量值自动改写了默认装备");
        panel.Bind(BusinessPageKind.Persons, 1); Application.DoEvents();
        var item = FindItem(workspace, 200);
        workspace.ApplyCompositeTransaction("未保存类型变更", [new(item.Document.Definition.Key, item.Row, "宝物类型", 20)]);
        panel.Bind(BusinessPageKind.Persons, 1); Application.DoEvents();
        TestSupport.Assert(Editor(panel, "默认武器").Options.All(option => option.Id != 200) &&
            Editor(panel, "默认防具").Options.Any(option => option.Id == 200) &&
            Editor(panel, "默认武器").Text.Contains("类别不匹配", StringComparison.Ordinal), "物品类型草稿没有刷新默认装备筛选/显示");
        workspace.Commands.Undo(); panel.Bind(BusinessPageKind.Persons, 1); Application.DoEvents();
        TestSupport.Assert(Editor(panel, "默认武器").Options.Any(option => option.Id == 200) && !workspace.IsDirty,
            "撤回物品类型后默认装备列表仍缓存旧类别");

        var items = workspace.Documents.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var original = FindItem(workspace, 200); var data = original.Document.Data.Clone();
        data.Rows.Add(original.Document.Data.Rows[original.Row].ItemArray); data.Columns["ID"]!.ReadOnly = false; data.Rows[0]["ID"] = 300;
        var raw = original.Document.OriginalFileBytes.AsSpan(Offset(original.Document, original.Row), 25).ToArray();
        items["items-extended"] = new TableDocument { Definition = original.Document.Definition with { Key = "items-extended", DataOffset = 0, RowCount = 1, BeginId = 300 },
            Data = data, OriginalFileBytes = raw, OriginalFileHash = "", FilePath = "" };
        TestSupport.Assert(PersonEquipmentService.GetOptions(items, "默认武器", 0, 65535).All(option => option.Id != 300),
            "byte默认装备字段允许选择扩展ID300");
        host.Close();
    }

    private static void TestAllEditEntrypoints(ProjectWorkspace workspace)
    {
        var person = workspace.GetDocument("persons");
        var power = Convert.ToInt32(person.Data.Rows[0]["武力"]);
        foreach (var (field, _, id) in Equipment)
        {
            workspace.ValidateCellValue(person, 0, field, id);
            workspace.ValidateCellValue(person, 0, field, 255);
            Reject(() => workspace.ValidateCellValue(person, 1, field, 4), "消耗品进入默认装备");
            Reject(() => workspace.ValidateCellValue(person, 1, field, 5), "未知类型进入默认装备");
            Reject(() => workspace.ValidateCellValue(person, 1, field, 256), "默认装备突破byte容量");
        }
        Reject(() => workspace.ApplyCompositeTransaction("错误默认装备整体拒绝",
            [new("persons", 0, "武力", (power + 1) % 100), new("persons", 0, "默认武器", 4)]), "业务事务绕过默认类别");
        TestSupport.Assert(!workspace.IsDirty && Convert.ToInt32(person.Data.Rows[0]["武力"]) == power, "事务失败留下部分草稿");

        var csv = Path.Combine(workspace.RootPath, "错误人物.csv");
        File.WriteAllText(csv, "ID,武力,默认武器\r\n0," + ((power + 1) % 100) + ",4\r\n", new UTF8Encoding(true));
        Reject(() => workspace.ImportCsv(person, csv), "CSV绕过默认类别");
        TestSupport.Assert(!workspace.IsDirty, "错误默认装备CSV留下部分草稿");
        workspace.ExportCsv(person, csv);
        TestSupport.Assert(workspace.ImportCsv(person, csv).ChangedCells == 0, "存量错类值不能原样CSV回导");

        var xlsx = Path.Combine(workspace.RootPath, "人物.xlsx"); var workbook = new PageWorkbookService(workspace);
        workbook.Export(BusinessPageKind.Persons, xlsx);
        TestSupport.Assert(workbook.PreviewImport(BusinessPageKind.Persons, xlsx).ChangedCells == 0, "存量错类值不能原样XLSX回导");
        EditPersonWorkbook(xlsx, "默认武器", "4");
        Reject(() => workbook.PreviewImport(BusinessPageKind.Persons, xlsx), "XLSX绕过默认类别");
        TestSupport.Assert(!workspace.IsDirty, "错误默认装备XLSX预检改变草稿");
        EditPersonWorkbook(xlsx, "默认武器", "200");
        TestSupport.Assert(workbook.ApplyImport(workbook.PreviewImport(BusinessPageKind.Persons, xlsx)) == 1, "XLSX未接受跨编号武器200");
        workspace.Commands.Undo();

        var before = ProfileDetector.CoreFiles.ToDictionary(name => name, name => File.ReadAllBytes(ProjectFileLocator.Resolve(workspace.RootPath, name)));
        workspace.ApplyCompositeTransaction("无关字段不阻断旧默认值", [new("persons", 0, "武力", (power + 1) % 100)]);
        TestSupport.Assert(workspace.Save().Saved, "存量错类默认装备阻断无关字段保存");
        TestSupport.Assert(Equipment.All(item => person.Data.Rows[0][item.Field].Equals(person.GetOriginal(0, item.Field))), "保存自动修正了旧默认值");
        TestSupport.Assert(workspace.UndoSaved().Success && before.All(pair => File.ReadAllBytes(ProjectFileLocator.Resolve(workspace.RootPath, pair.Key)).SequenceEqual(pair.Value)),
            "无关字段保存历史不能精确撤回");
    }

    private static void TestRawGrid(string root)
    {
        using var form = new MainForm(openDefaultProject: false) { Location = new Point(-32000, -32000), StartPosition = FormStartPosition.Manual };
        form.Show(); form.LoadProjectForSmokeTest(root); form.ShowPhysicalTableForSmokeTest("persons"); Application.DoEvents();
        var grid = form.PhysicalGridForSmokeTest;
        foreach (var (field, category, _) in Equipment)
        {
            var column = (DataGridViewComboBoxColumn)grid.Columns[field];
            var choices = ((System.Collections.IEnumerable)column.DataSource!).Cast<object>().ToArray();
            var ids = choices.Select(choice => Convert.ToInt32(choice.GetType().GetProperty("Value")!.GetValue(choice))).ToArray();
            TestSupport.Assert(ids.All(id => id == 255 || Category(form.WorkspaceForSmokeTest, id) == category), "原始人物表默认下拉未筛选：" + field);
            TestSupport.Assert(Convert.ToString(grid.Rows[0].Cells[field].FormattedValue)!.Contains("类别不匹配", StringComparison.Ordinal), "原始表没有显示错类存量值：" + field);
            var before = grid.Rows[0].Cells[field].Value;
            try { grid.CurrentCell = grid.Rows[0].Cells[field]; grid.BeginEdit(false); grid.CancelEdit(); }
            catch (Exception ex) { throw new InvalidOperationException(field + "：" + form.StatusForSmokeTest + " / 当前=" + grid.CurrentCell?.OwningColumn.Name + " / " + grid.CurrentCell?.ErrorText, ex); }
            TestSupport.Assert(Equals(grid.Rows[0].Cells[field].Value, before) && !form.WorkspaceForSmokeTest.IsDirty, "原始表只进入/退出下拉自动改写存量默认值");
        }
        form.Close();
    }

    private static void TestFinalDraftCategories(ProjectWorkspace workspace)
    {
        var item = FindItem(workspace, 2);
        foreach (var reverse in new[] { false, true })
        {
            var edits = new CompositeCellEdit[] { new("persons", 1, "默认武器", 2), new(item.Document.Definition.Key, item.Row, "宝物类型", 2) };
            workspace.ApplyCompositeTransaction("按最终类别校验", reverse ? edits.Reverse().ToArray() : edits);
            TestSupport.Assert(Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[1]["默认武器"]) == 2 && Category(workspace, 2) == ItemCategory.Weapon &&
                Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[1]["默认防具"]) == 2,
                "复合事务未按最终物品类别校验，或自动改变未修改的默认防具");
            workspace.Commands.Undo(); TestSupport.Assert(!workspace.IsDirty, "复合类别事务未完整撤回");

            var personsCsv = Path.Combine(workspace.RootPath, "同组人物.csv"); var itemsCsv = Path.Combine(workspace.RootPath, "同组宝物.csv");
            File.WriteAllText(personsCsv, "ID,默认武器\r\n1,2\r\n", new UTF8Encoding(true));
            File.WriteAllText(itemsCsv, "ID,宝物类型\r\n2,2\r\n", new UTF8Encoding(true));
            var files = new Dictionary<string, string>();
            if (reverse) { files[item.Document.Definition.Key] = itemsCsv; files["persons"] = personsCsv; }
            else { files["persons"] = personsCsv; files[item.Document.Definition.Key] = itemsCsv; }
            workspace.ImportCsvBundle(files);
            TestSupport.Assert(Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[1]["默认武器"]) == 2 && Category(workspace, 2) == ItemCategory.Weapon,
                "多表CSV按文件先后误拒最终有效的装备引用");
            workspace.Commands.Undo(); TestSupport.Assert(!workspace.IsDirty, "多表CSV类别事务未完整撤回");
        }
    }

    private static void Migrate(string repository, (string Directory, string Profile, string Variant) from,
        (string Directory, string Profile, string Variant) to)
    {
        var source = TestSupport.CreateCoreCopy(Path.Combine(repository, "基底", from.Directory), false);
        var target = TestSupport.CreateCoreCopy(Path.Combine(repository, "基底", to.Directory), false);
        try
        {
            Seed(source, from.Profile, from.Variant); Seed(target, to.Profile, to.Variant);
            var targetWorkspace = Open(target, to.Profile, to.Variant); var person = targetWorkspace.GetDocument("persons");
            var bytes = File.ReadAllBytes(person.FilePath);
            foreach (var row in new[] { 0, 1 }) foreach (var (field, _, _) in Equipment)
                bytes[Offset(person, row) + person.Definition.Fields.Single(f => f.Name == field).Offset] = 255;
            File.WriteAllBytes(person.FilePath, bytes);
            foreach (var (id, type, effect) in new[] { (8, 1, 7), (9, 21, 8) }) SeedItem(targetWorkspace, id, type, effect);
            SeedItem(targetWorkspace, 200, 20, 2);
            var before = ProfileDetector.CoreFiles.ToDictionary(name => name, name => File.ReadAllBytes(ProjectFileLocator.Resolve(target, name)));
            var service = new MigrationService(); var plan = service.Preview(source, target, from.Profile, from.Variant, to.Profile, to.Variant);
            TestSupport.Assert(plan.Changes.Any(change => change.TableKey == "persons" && change.Id == 0 && change.FieldName == "默认武器" && change.Kind == MigrationChangeKind.Incompatible),
                "迁移未拒绝源人物0的错类默认武器");
            var selected = plan.Changes.Select((change, index) => (change, index)).Where(item =>
                item.change.TableKey == "persons" && item.change.Id == 1 && Equipment.Any(e => e.Field == item.change.FieldName) ||
                item.change.TableKey == "items-low" && item.change.Id is 8 or 9 && item.change.FieldName is "宝物类型" or "装备特效号" ||
                item.change.TableKey == "items-high" && item.change.Id == 200 && item.change.FieldName == "宝物类型")
                .Where(item => item.change.Kind == MigrationChangeKind.Changed).ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            TestSupport.Assert(plan.WritableChanges.Any(change => change.TableKey == "persons" && change.Id == 1 && change.FieldName == "默认武器"),
                "迁移预览未考虑同时迁移的最终物品类型");
            var missingType = selected.Where(pair => plan.Changes[pair.Key].TableKey == "persons").ToDictionary(pair => pair.Key, pair => pair.Value);
            var rejected = service.Apply(plan, missingType);
            TestSupport.Assert(!rejected.Success && before.All(pair => File.ReadAllBytes(ProjectFileLocator.Resolve(target, pair.Key)).SequenceEqual(pair.Value)),
                "少选必要物品类型迁移时没有整体拒绝或修改了目标");
            var result = service.Apply(plan, selected);
            TestSupport.Assert(result.Success, "默认装备/普通effect2/3跨版迁移失败：" + string.Join("；", result.Messages));
            var reopened = Open(target, to.Profile, to.Variant);
            foreach (var (field, _, id) in Equipment) TestSupport.Assert(Convert.ToInt32(reopened.GetDocument("persons").Data.Rows[1][field]) == id, "跨编号默认装备迁移复读错误");
            foreach (var (id, type, effect) in new[] { (8, 0, 2), (9, 20, 3) })
            {
                var item = FindItem(reopened, id); var raw = File.ReadAllBytes(item.Document.FilePath); var offset = Offset(item.Document, item.Row);
                TestSupport.Assert(raw[offset + 0x11] == type && raw[offset + 0x12] == effect &&
                    Convert.ToInt32(item.Document.Data.Rows[item.Row]["宝物类型"]) == type && Convert.ToInt32(item.Document.Data.Rows[item.Row]["装备特效号"]) == effect,
                    "普通装备effect2/3迁移错误交换逻辑字段");
            }
            TestSupport.Assert(reopened.GetDocument("persons").Data.Rows[0]["默认武器"].Equals(255), "迁移修改未选的旧默认装备");
            TestSupport.Assert(reopened.UndoSaved().Success && before.All(pair => File.ReadAllBytes(ProjectFileLocator.Resolve(target, pair.Key)).SequenceEqual(pair.Value)), "迁移撤回未完整恢复目标");
            Console.WriteLine($"PERSON_EQUIPMENT_MIGRATION_OK {from.Profile}->{to.Profile}");
        }
        finally { Directory.Delete(source, true); Directory.Delete(target, true); }
    }

    private static void Seed(string root, string profile, string variant)
    {
        var workspace = Open(root, profile, variant);
        foreach (var (id, type, effect) in new[] { (200, 0, 2), (2, 20, 3), (3, 74, 2), (4, 46, 3), (5, 26, 255), (6, 70, 0), (7, 70, 1), (8, 0, 2), (9, 20, 3) })
            SeedItem(workspace, id, type, effect);
        var person = workspace.GetDocument("persons"); var bytes = File.ReadAllBytes(person.FilePath);
        foreach (var (row, values) in new[] { (0, new[] { 2, 200, 4 }), (1, new[] { 200, 2, 3 }) })
            for (var field = 0; field < Equipment.Length; field++)
                bytes[Offset(person, row) + 29 + field] = (byte)values[field];
        File.WriteAllBytes(person.FilePath, bytes);
        // Deliberately absurd image-assigner boundaries must have no effect.
        File.WriteAllText(Path.Combine(root, "System.ini"), "DefID=1\r\nAssID=2\r\n");
    }

    private static void SeedItem(ProjectWorkspace workspace, int id, int type, int effect)
    {
        var item = FindItem(workspace, id); var bytes = File.ReadAllBytes(item.Document.FilePath); var offset = Offset(item.Document, item.Row);
        Array.Clear(bytes, offset, 14); EncodingService.GameEncoding.GetBytes("测试" + id).CopyTo(bytes, offset);
        bytes[offset + 0x11] = (byte)type; bytes[offset + 0x12] = (byte)effect; File.WriteAllBytes(item.Document.FilePath, bytes);
    }

    private static ProjectWorkspace Open(string root, string profile, string variant)
    { var workspace = new ProjectWorkspace(); workspace.Open(root, profile, variant); return workspace; }
    private static (TableDocument Document, int Row) FindItem(ProjectWorkspace workspace, int id)
    {
        var document = new[] { "items-low", "items-high", "items-extended" }.Where(workspace.Documents.ContainsKey).Select(workspace.GetDocument)
            .Single(document => id >= document.Definition.BeginId && id < document.Definition.BeginId + document.Data.Rows.Count);
        return (document, id - document.Definition.BeginId);
    }
    private static ItemCategory Category(ProjectWorkspace workspace, int id)
    { var item = FindItem(workspace, id); return ItemTypeService.Resolve(item.Document, item.Row).Category; }
    private static int Offset(TableDocument document, int row) => checked((int)(document.Definition.DataOffset + (long)row * document.Definition.RowSize));
    private static SearchableIdComboBox Editor(Control root, string field)
    {
        var label = Descendants(root).OfType<Label>().First(label => label.Text == field); var layout = (TableLayoutPanel)label.Parent!;
        return (SearchableIdComboBox)layout.GetControlFromPosition(1, layout.GetPositionFromControl(label).Row)!;
    }
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void EditPersonWorkbook(string path, string field, string value)
    {
        using var package = SpreadsheetDocument.Open(path, true); var workbook = package.WorkbookPart!;
        var metadata = workbook.Workbook.Sheets!.Elements<Sheet>().Single(sheet => sheet.Name == "_DataManager");
        var json = string.Concat(((WorksheetPart)workbook.GetPartById(metadata.Id!)).Worksheet.Descendants<Text>().Select(text => text.Text));
        using var manifest = System.Text.Json.JsonDocument.Parse(json);
        var sheetId = manifest.RootElement.GetProperty("Datasets").EnumerateArray().Single(dataset => dataset.GetProperty("Key").GetString() == "persons").GetProperty("SheetId").GetUInt32();
        var registration = workbook.Workbook.Sheets.Elements<Sheet>().Single(sheet => sheet.SheetId?.Value == sheetId);
        var sheet = (WorksheetPart)workbook.GetPartById(registration.Id!);
        var data = sheet.Worksheet.GetFirstChild<SheetData>()!; var header = data.Elements<Row>().First().Elements<Cell>().Single(cell => cell.InnerText == field);
        var column = new string(header.CellReference!.Value!.TakeWhile(char.IsLetter).ToArray());
        var cell = data.Elements<Row>().Single(row => row.RowIndex!.Value == 3).Elements<Cell>().Single(cell => cell.CellReference == column + "3");
        cell.RemoveAllChildren(); cell.DataType = CellValues.InlineString; cell.InlineString = new InlineString(new Text(value)); sheet.Worksheet.Save();
    }
    private static void Reject(Action action, string message)
    { try { action(); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException) { return; } throw new InvalidOperationException(message); }
}
