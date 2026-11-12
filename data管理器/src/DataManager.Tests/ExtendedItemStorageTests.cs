using System.Buffers.Binary;
using System.Data;
using System.Text;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Migration;
using DataManager.Core.Profiles;
using DataManager.Core.Workbooks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Control = System.Windows.Forms.Control;

namespace DataManager.Tests;

internal static class ExtendedItemStorageTests
{
    private static readonly string[] CoreFiles = ["Data.e5", "Star.e5", "Imsg.e5", "Ekd5.exe"];

    public static void Run(string repository)
    {
        var bases = Path.Combine(repository, "基底");
        var star = Path.Combine(bases, "烈焰同盟6.6x(新修版)黄线完结+红线5关");
        var rootItems = Path.Combine(bases, "18州", "十八州演义0.2版本");
        WithCopy(star, copy => StarRoundtrip(copy));
        WithCopy(star, CheckWorkbookRoundtrip);
        WithCopy(star, Check255LayoutStability);
        WithCopy(rootItems, copy => RootRoundtrip(copy));
        foreach (var (name, profile, variant) in new[]
                 { ("加强版6.5未加密版", "ccz-6.5", "65-standard-v1"), ("复刻版6.6山贼", "ccz-6.6", "66-standard-v1"), ("洗隋", "ccz-6.4", "64-xisui-v1") })
            WithCopy(Path.Combine(bases, name), copy =>
            {
                var workspace = Open(copy, profile, variant);
                TestSupport.Assert(!workspace.Documents.ContainsKey("items-extended") && workspace.GetDocument("items-high").Data.Rows.Count == 151,
                    "普通Star的FF-End/零/空格尾部被误判为物品扩展：" + name);
                Directory.CreateDirectory(Path.Combine(copy, "E5"));
                File.WriteAllBytes(Path.Combine(copy, "E5", "Item.e5"), new byte[6400]);
                TestSupport.Assert(!Open(copy, profile, variant).Documents.ContainsKey("items-extended"), "E5图标包被误读为扩展数据");
            });
        WithCopy(star, CheckShortStar);
        WithCopy(star, CheckShortDescriptions);
        WithCopy(star, CheckMissingDescriptions);
        WithCopy(star, CheckUnknownConsumer);
        WithCopy(star, CheckUnverifiedDescriptionConsumer);
        WithCopy(star, CheckDoubleSource);
        WithCopy(star, copy => CheckLayoutIsolation(copy, bases));
        foreach (var sourceStar in new[] { false, true })
            WithCopy(sourceStar ? star : rootItems, sourceCopy =>
                WithCopy(sourceStar ? rootItems : star, targetCopy => CheckMigration(sourceCopy, targetCopy, sourceStar, emptyTarget: false)));
        WithCopy(rootItems, sourceCopy => WithCopy(star, targetCopy => CheckMigration(sourceCopy, targetCopy, sourceStar: false, emptyTarget: true)));
        Console.WriteLine("EXTENDED_ITEM_STORAGE_TESTS_OK: Star512/根Item兼容，255/256/257/511物理字段，6.4/6.5/6.6尾部排除，双源/短文件/缺失说明降级，CSV/XLSX/历史/逐字节写回及源SHA保护通过。");
    }

    private static void StarRoundtrip(string copy)
    {
        var workspace = new ProjectWorkspace(); workspace.Open(copy);
        TestSupport.Assert(workspace.ActiveProfile.ProfileId == "ccz-6.6" && workspace.ActiveProfile.VariantId == "66-standard-v1",
            "真实烈焰默认打开没有自动选择6.6/15字节名称布局");
        var items = workspace.GetDocument("items-extended");
        var descriptions = workspace.GetDocument("item-description-extended");
        TestSupport.Assert(items.Definition.FileName == "Star.e5" && items.Definition.DataOffset == 3800 && items.Definition.RecordCodecKey == "item-66-15-v1" &&
            items.Definition.Fields.Single(field => field.Name == "名称").Size == 15 && items.Definition.Fields.Single(field => field.Name == "图标").Offset == 15,
            "Star扩展未使用真实6.6物理字段和codec");
        TestSupport.Assert(Name(items, 256) == "新扩充100" && Name(items, 257) == "新扩充101" && Name(items, 511) == "新扩充1FF" && Name(workspace.GetDocument("items-high"), 255) == "天工开物",
            "Star首末扩展和真实255记录读取错误");
        var originalStar = File.ReadAllBytes(Path.Combine(copy, "Star.e5"));
        foreach (var id in new[] { 255, 256, 257, 511 })
        {
            var document = workspace.GetDocument(id < 256 ? "items-high" : "items-extended");
            var rowIndex = RowIndex(document, id); var row = document.Data.Rows[rowIndex];
            var offset = (id - 104) * 25;
            var markerRow = originalStar[offset + 17] >= 26 && originalStar[offset + 18] is 2 or 3;
            var type = originalStar[offset + (markerRow ? 18 : 17)];
            var effect = originalStar[offset + (markerRow ? 17 : 18)];
            TestSupport.Assert(Convert.ToInt32(row["图标"]) == originalStar[offset + 15] &&
                Convert.ToInt32(row["宝物类型"]) == type && Convert.ToInt32(row["装备特效号"]) == effect &&
                Convert.ToInt32(row["初始能力"]) == originalStar[offset + 21] &&
                Convert.ToInt32(row["装备特效值"]) == originalStar[offset + 22] &&
                Convert.ToInt32(row["能力成长"]) == originalStar[offset + 23],
                $"Star物品{id}图标/类型/特效/能力物理读取错位");
            var semantic = ItemSemanticResolver.Resolve(document, rowIndex);
            TestSupport.Assert(semantic.TypeValue == type && semantic.ActualEffectNumber == effect &&
                semantic.Category == (id == 511 ? ItemCategory.Unknown : ItemCategory.Consumable),
                $"Star物品{id}类别或实际效果号语义错误");
        }
        TestSupport.Assert(descriptions.Definition.FileName == "Imsg.e5" && descriptions.Definition.DataOffset == 440400 &&
            Convert.ToString(descriptions.Data.Rows[255]["宝物说明"]) == "zzz1FF end", "Star扩展说明映射错误");
        var page = new BusinessDataService(workspace).LoadItems();
        TestSupport.Assert(page.Records.Count == 512 && new ExtendDataService(workspace).GetItemOptions().Count(option => option.Id == 511) == 1,
            "宝物页或关联目录缺少511或产生重复编号");
        var before = Snapshot(copy);
        CheckUi(workspace, "Star.e5", "Imsg.e5");
        var transaction = new BusinessDataService(workspace).BeginTransaction(BusinessPageKind.Items, "Star扩展首末与高段联合写回");
        var edits = new List<(string Key, int Id, string Field, object Value)>
        {
            ("items-high", 104, "名称", "高段复读"), ("items-high", 255, "名称", "天工复读"),
            ("items-extended", 256, "名称", "扩展复读甲"), ("items-extended", 511, "名称", "扩展复读末"),
            ("items-extended", 256, "图标", 7), ("items-extended", 511, "图标", 8),
            ("items-extended", 256, "装备特效号", 27), ("items-extended", 511, "装备特效号", 33),
            ("items-extended", 256, "初始能力", 31), ("items-extended", 511, "初始能力", 32),
            ("item-description-extended", 256, "宝物说明", "扩展首条说明复读"), ("item-description-extended", 511, "宝物说明", "扩展末条说明复读")
        };
        foreach (var edit in edits) transaction.Set(edit.Key, RowIndex(workspace.GetDocument(edit.Key), edit.Id), edit.Field, edit.Value);
        TestSupport.Assert(transaction.Apply().Applied, "扩展事务没有形成草稿");
        workspace.Commands.Undo(); TestSupport.Assert(!workspace.IsDirty, "扩展草稿没有整体撤销"); workspace.Commands.Redo();
        var saved = workspace.Save();
        TestSupport.Assert(saved.Saved && saved.History?.Files.Count == 2, "Star/Imsg联合保存失败：" + string.Join("；", saved.Messages));
        var after = Snapshot(copy);
        var reread = new ProjectWorkspace(); reread.Open(copy);
        TestSupport.Assert(reread.ActiveProfile.VariantId == "66-standard-v1", "保存后指纹变化使默认重开选错物品codec");
        TestSupport.Assert(after["Star.e5"][3800 + 17] == 27 && after["Star.e5"][3800 + 18] == 3 &&
            after["Star.e5"][(511 - 104) * 25 + 17] == 32 && after["Star.e5"][(511 - 104) * 25 + 18] == 33,
            "256/511特效没有写入各自真实物理字节或误改类别");
        foreach (var edit in edits)
            TestSupport.Assert(Convert.ToString(reread.GetDocument(edit.Key).Data.Rows[RowIndex(reread.GetDocument(edit.Key), edit.Id)][edit.Field]) == Convert.ToString(edit.Value),
                $"重开复读失败：{edit.Key}/{edit.Id}/{edit.Field}");
        foreach (var file in before)
        {
            var allowed = edits.Where(edit => reread.GetDocument(edit.Key).Definition.FileName.Equals(file.Key, StringComparison.OrdinalIgnoreCase))
                .Select(edit =>
                {
                    var definition = reread.GetDocument(edit.Key).Definition;
                    var field = definition.Fields.Single(field => field.Name == edit.Field);
                    var rowOffset = definition.DataOffset + (edit.Id - definition.BeginId) * definition.RowSize;
                    var fieldOffset = field.Offset;
                    if (definition.RecordCodecKey == "item-66-15-v1" && edit.Field == "装备特效号" &&
                        file.Value[checked((int)rowOffset + 17)] >= 26 && file.Value[checked((int)rowOffset + 18)] is 2 or 3)
                        fieldOffset = 17;
                    return (Start: rowOffset + fieldOffset, field.Size);
                }).ToArray();
            TestSupport.Assert(file.Value.Length == after[file.Key].Length && Enumerable.Range(0, file.Value.Length)
                .Where(index => file.Value[index] != after[file.Key][index]).All(index => allowed.Any(range => index >= range.Start && index < range.Start + range.Size)),
                "物品扩展保存改动非目标字节：" + file.Key);
        }
        var csv = Path.Combine(copy, "扩展.csv");
        reread.ExportCsv(reread.GetDocument("items-extended"), csv);
        var text = File.ReadAllText(csv, Encoding.UTF8);
        TestSupport.Assert(text.Contains("511,", StringComparison.Ordinal) && reread.ImportCsv(reread.GetDocument("items-extended"), csv).ChangedCells == 0,
            "扩展CSV首末记录原样导入不一致");
        File.WriteAllText(csv, text.Replace("扩展复读末", "末条CSV复读", StringComparison.Ordinal), new UTF8Encoding(true));
        TestSupport.Assert(reread.ImportCsv(reread.GetDocument("items-extended"), csv).ChangedCells == 1 && reread.Save().Saved &&
            Name(OpenStar(copy).GetDocument("items-extended"), 511) == "末条CSV复读", "扩展CSV末条没有真实保存");
        TestSupport.Assert(reread.UndoSaved().Success && Snapshot(copy).All(file => file.Value.SequenceEqual(after[file.Key])), "CSV已保存历史撤回失败");
        TestSupport.Assert(reread.UndoSaved().Success && Snapshot(copy).All(file => file.Value.SequenceEqual(before[file.Key])), "跨Star/Imsg历史没有恢复完整原字节");
        TestSupport.Assert(reread.RedoSaved().Success && Snapshot(copy).All(file => file.Value.SequenceEqual(after[file.Key])), "跨Star/Imsg历史重做失败");
    }

    private static void CheckWorkbookRoundtrip(string copy)
    {
        var workspace = new ProjectWorkspace(); workspace.Open(copy);
        var before = Snapshot(copy);
        var service = new PageWorkbookService(workspace);
        var path = Path.Combine(copy, "扩展宝物.xlsx");
        service.Export(BusinessPageKind.Items, path);
        TestSupport.Assert(service.PreviewImport(BusinessPageKind.Items, path).ChangedCells == 0 && !workspace.IsDirty,
            "扩展宝物XLSX原样回导产生修改");
        using (var package = SpreadsheetDocument.Open(path, true))
        {
            var part = package.WorkbookPart!;
            foreach (var key in new[] { "items-extended", "item-description-extended" })
            {
                var sheet = FindWorkbookSheet(part, key);
                var data = ((WorksheetPart)part.GetPartById(sheet.Id!)).Worksheet.GetFirstChild<SheetData>()!;
                var ids = data.Elements<Row>().Where(row => row.RowIndex?.Value >= 3)
                    .Select(row => int.Parse(row.Elements<Cell>().First().InnerText)).ToArray();
                TestSupport.Assert(ids.SequenceEqual(Enumerable.Range(256, 256)), $"扩展宝物XLSX未完整导出{key}的256–511记录");
            }
            var items = ((WorksheetPart)part.GetPartById(FindWorkbookSheet(part, "items-extended").Id!)).Worksheet.GetFirstChild<SheetData>()!;
            var header = items.Elements<Row>().Single(row => row.RowIndex?.Value == 1).Elements<Cell>().Single(cell => cell.InnerText == "名称");
            var column = new string(header.CellReference!.Value!.TakeWhile(char.IsLetter).ToArray());
            var last = items.Elements<Row>().Single(row => row.Elements<Cell>().First().InnerText == "511");
            var cell = last.Elements<Cell>().Single(value => value.CellReference == column + last.RowIndex!.Value);
            cell.RemoveAllChildren(); cell.DataType = CellValues.InlineString;
            cell.InlineString = new InlineString(new Text("末条工作簿复读"));
            foreach (var worksheet in part.WorksheetParts) worksheet.Worksheet.Save();
            part.Workbook.Save();
        }
        var preview = service.PreviewImport(BusinessPageKind.Items, path);
        TestSupport.Assert(preview.ChangedCells == 1 && preview.Changes[0].Id == "511" && !workspace.IsDirty &&
            Snapshot(copy).All(file => file.Value.SequenceEqual(before[file.Key])), "扩展宝物XLSX预检修改了草稿/磁盘或定位错末条");
        TestSupport.Assert(service.ApplyImport(preview) == 1 && Name(workspace.GetDocument("items-extended"), 511) == "末条工作簿复读",
            "扩展宝物XLSX未应用511名称");
        workspace.Commands.Undo();
        TestSupport.Assert(!workspace.IsDirty && Name(workspace.GetDocument("items-extended"), 511) == "新扩充1FF", "扩展宝物XLSX草稿没有整次撤回");
        workspace.Commands.Redo();
        TestSupport.Assert(workspace.Save().Saved, "扩展宝物XLSX没有保存到Star");
        var after = Snapshot(copy);
        var reopened = new ProjectWorkspace(); reopened.Open(copy);
        TestSupport.Assert(Name(reopened.GetDocument("items-extended"), 511) == "末条工作簿复读", "扩展宝物XLSX保存后默认重开不能复读511");
        var start = (511 - 104) * 25;
        foreach (var file in before)
            TestSupport.Assert(file.Value.Length == after[file.Key].Length && Enumerable.Range(0, file.Value.Length)
                .Where(index => file.Value[index] != after[file.Key][index])
                .All(index => file.Key == "Star.e5" && index >= start && index < start + 15), "扩展宝物XLSX改动末条名称之外字节：" + file.Key);
        TestSupport.Assert(reopened.UndoSaved().Success && Snapshot(copy).All(file => file.Value.SequenceEqual(before[file.Key])), "扩展宝物XLSX已保存历史未恢复完整原字节");
        TestSupport.Assert(reopened.RedoSaved().Success && Snapshot(copy).All(file => file.Value.SequenceEqual(after[file.Key])), "扩展宝物XLSX已保存历史重做失败");
    }

    private static Sheet FindWorkbookSheet(WorkbookPart part, string key)
    {
        var metadata = part.Workbook.Sheets!.Elements<Sheet>().Single(sheet => sheet.Name == "_DataManager");
        var json = string.Concat(((WorksheetPart)part.GetPartById(metadata.Id!)).Worksheet.Descendants<Text>().Select(text => text.Text));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var id = document.RootElement.GetProperty("Datasets").EnumerateArray()
            .Single(dataset => dataset.GetProperty("Key").GetString() == key).GetProperty("SheetId").GetUInt32();
        return part.Workbook.Sheets.Elements<Sheet>().Single(sheet => sheet.SheetId?.Value == id);
    }

    private static void RootRoundtrip(string copy)
    {
        var workspace = Open(copy, "ccz-6.5", "65-standard-v1");
        var items = workspace.GetDocument("items-extended");
        TestSupport.Assert(items.Definition.FileName == "Item.e5" && items.Definition.DataOffset == 0 && items.Definition.RecordCodecKey == "item-65-17-v1" && Name(items, 256) == "豆",
            "根Item扩展兼容性损坏");
        TestSupport.Assert(new ExtendDataService(workspace).GetItemOptions().Single(option => option.Id == 511).Name == Name(items, 511), "根Item的511名称目录缺失");
        CheckUi(workspace, "Item.e5", "Itemmsg.e5");
        var star = File.ReadAllBytes(Path.Combine(copy, "Star.e5"));
        workspace.ApplyCompositeTransaction("根Item末条复读", [new("items-extended", 255, "名称", "末条根表复读")]);
        TestSupport.Assert(workspace.Save().Saved && Name(Open(copy, "ccz-6.5", "65-standard-v1").GetDocument("items-extended"), 511) == "末条根表复读" &&
            File.ReadAllBytes(Path.Combine(copy, "Star.e5")).SequenceEqual(star), "根Item末条写回错误或误写Star");
    }

    private static void Check255LayoutStability(string copy)
    {
        var workspace = new ProjectWorkspace(); workspace.Open(copy);
        var revision = workspace.ActiveProfile.LayoutRevision;
        var original = Snapshot(copy);
        var originalName = Name(workspace.GetDocument("items-high"), 255);
        foreach (var replacement in new[] { string.Empty, "FF-End" })
        {
            workspace.ApplyCompositeTransaction("255名称不改变完整物品布局", [new("items-high", 151, "名称", replacement)]);
            var saved = workspace.Save();
            TestSupport.Assert(saved.Saved && saved.History != null, "255名称修改保存失败");
            // Each iteration leaves its previous save reverted. Target this
            // save explicitly rather than asking default redo to select the
            // oldest reverted record from an earlier iteration.
            var operationId = saved.History!.OperationId;
            var changed = Snapshot(copy);
            var reopened = new ProjectWorkspace(); reopened.Open(copy);
            TestSupport.Assert(reopened.ActiveProfile.LayoutRevision == revision && reopened.GetDocument("items-high").Data.Rows.Count == 152 &&
                reopened.GetDocument("item-description-high").Data.Rows.Count == 152 && new BusinessDataService(reopened).LoadItems().Records.Count == 512 &&
                Name(reopened.GetDocument("items-high"), 255) == replacement,
                "255名称清空/End后物理行消失、默认打开布局变化或总物品数改变");
            TestSupport.Assert(reopened.UndoSaved(operationId).Success && Snapshot(copy).All(file => file.Value.SequenceEqual(original[file.Key])) &&
                Name(reopened.GetDocument("items-high"), 255) == originalName, "255改名后的已保存历史无法完整撤回");
            TestSupport.Assert(reopened.RedoSaved(operationId).Success && Snapshot(copy).All(file => file.Value.SequenceEqual(changed[file.Key])) &&
                reopened.ActiveProfile.LayoutRevision == revision && Name(reopened.GetDocument("items-high"), 255) == replacement,
                "255改名后的已保存历史无法重做或改变布局身份");
            TestSupport.Assert(reopened.UndoSaved(operationId).Success, "255稳定性用例未恢复原始状态");
            workspace = new ProjectWorkspace(); workspace.Open(copy);
        }
    }

    private static void CheckShortStar(string copy)
    {
        var path = Path.Combine(copy, "Star.e5"); var original = File.ReadAllBytes(path);
        foreach (var length in new[] { 10199, 3799, 3775 })
        {
            File.WriteAllBytes(path, original[..length]);
            var workspace = OpenStar(copy);
            TestSupport.Assert(!workspace.Documents.ContainsKey("items-extended") &&
                workspace.GetDocument("items-high").Data.Rows.Count == (length >= 3800 ? 152 : 151) && new FileInfo(path).Length == length,
                "短Star未降级、丢失完整104–254高段或被自动补齐：" + length);
        }
    }

    private static void CheckShortDescriptions(string copy)
    {
        var path = Path.Combine(copy, "Imsg.e5"); File.WriteAllBytes(path, File.ReadAllBytes(path)[..440500]);
        var workspace = OpenStar(copy);
        TestSupport.Assert(workspace.Documents.ContainsKey("items-extended") && !workspace.Documents.ContainsKey("item-description-extended"), "短说明影响独立物品读取或读取越界");
    }

    private static void CheckMissingDescriptions(string copy)
    {
        File.Delete(Path.Combine(copy, "Imsg.e5"));
        var workspace = new ProjectWorkspace(); workspace.Open(copy);
        TestSupport.Assert(workspace.GetDocument("items-extended").Data.Rows.Count == 256 &&
            Name(workspace.GetDocument("items-extended"), 257) == "新扩充101" && !workspace.Documents.ContainsKey("item-description-extended"),
            "Imsg整文件缺失影响独立扩展物品读取或仍加载说明");
        CheckUi(workspace, "Star.e5", null);
    }

    private static void CheckUnknownConsumer(string copy)
    {
        MutateCode(copy, 0x4010D3, 0x90);
        TestSupport.Assert(!OpenStar(copy).Documents.ContainsKey("items-extended"), "缺少512消费证据仍按长度猜测扩展");
        var rootItem = new byte[6400]; EncodingService.GameEncoding.GetBytes("根表独立来源").CopyTo(rootItem, 0);
        File.WriteAllBytes(Path.Combine(copy, "Item.e5"), rootItem);
        var workspace = OpenStar(copy);
        TestSupport.Assert(workspace.GetDocument("items-extended").Definition.FileName == "Item.e5" &&
            Name(workspace.GetDocument("items-extended"), 256) == "根表独立来源", "未知Star消费者时根Item被Star尾部冒充");
    }

    private static void CheckUnverifiedDescriptionConsumer(string copy)
    {
        MutateCode(copy, 0x4143A5, 0x90);
        var workspace = OpenStar(copy);
        TestSupport.Assert(workspace.Documents.ContainsKey("items-extended") && !workspace.Documents.ContainsKey("item-description-extended"), "未验证说明消费者仍开放说明写入");
        CheckUi(workspace, "Star.e5", null);
    }

    private static void CheckDoubleSource(string copy)
    {
        File.WriteAllBytes(Path.Combine(copy, "Item.e5"), new byte[6400]);
        var selected = OpenStar(copy);
        TestSupport.Assert(selected.GetDocument("items-extended").Definition.FileName == "Star.e5" && selected.GetDocument("items-extended").Definition.IsWriteSupported &&
            selected.LastScan!.Diagnostics.Any(message => message.Contains("根Item.e5未被", StringComparison.Ordinal)), "无根Item消费者时没有明确选择Star");
        var path = Path.Combine(copy, "Ekd5.exe"); var bytes = File.ReadAllBytes(path);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        var section = pe + 24 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20));
        var position = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(section + 20)) + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(section + 16)) - 100));
        Convert.FromHexString("81E9000100006BC919030D1C004F0090909025FF000000B9C8000000F7E18B0D24004F00").CopyTo(bytes, position);
        File.WriteAllBytes(path, bytes);
        var conflict = OpenStar(copy);
        TestSupport.Assert(conflict.GetDocument("items-extended").Definition.Capability == TableCapability.ReadOnly &&
            conflict.LastScan!.Diagnostics.Any(message => message.Contains("双源冲突", StringComparison.Ordinal)), "双消费者冲突没有只读降级及来源诊断");
    }

    private static void CheckLayoutIsolation(string starCopy, string bases)
    {
        var csv = Path.Combine(starCopy, "隔离.csv");
        var star = OpenStar(starCopy);
        star.ExportCsv(star.GetDocument("items-extended"), csv);
        var metadata = File.ReadLines(csv).First();
        File.WriteAllText(csv, metadata + "\r\nID,名称\r\n256,布局交换\r\n", new UTF8Encoding(true));
        WithCopy(Path.Combine(bases, "复刻版6.6山贼"), rootCopy =>
        {
            File.WriteAllBytes(Path.Combine(rootCopy, "Item.e5"), new byte[6400]);
            var root = OpenStar(rootCopy);
            var document = root.GetDocument("items-extended");
            var before = Snapshot(rootCopy);
            var itemBefore = File.ReadAllBytes(Path.Combine(rootCopy, "Item.e5"));
            TestSupport.Assert(root.ImportCsv(document, csv).ChangedCells == 1 && document.Definition.FileName == "Item.e5" &&
                Name(document, 256) == "布局交换" && before.All(pair => File.ReadAllBytes(Path.Combine(rootCopy, pair.Key)).SequenceEqual(pair.Value)) &&
                File.ReadAllBytes(Path.Combine(rootCopy, "Item.e5")).SequenceEqual(itemBefore),
                "格式兼容的 Star 扩展 CSV 未按目标根 Item 布局进入草稿或提前写盘");
            root.Commands.Undo();
            TestSupport.Assert(!root.IsDirty && Name(document, 256) == string.Empty,
                "跨物理来源 CSV 撤回未恢复根 Item 草稿");
            var rootRevisions = LayoutRevisionCompatibility.GetLegacyRevisions(rootCopy, "ccz-6.6", "66-standard-v1")
                .Append(root.ActiveProfile.LayoutRevision);
            TestSupport.Assert(rootRevisions.All(revision => !LayoutRevisionCompatibility.IsCompatible(starCopy,
                "ccz-6.6", "66-standard-v1", revision, star.ActiveProfile.LayoutRevision)),
                "Star物理来源仍接受根Item当前或历史布局身份");
        });
        // The immediately preceding strategy release already used the same
        // Star geometry; adding its independent class table is a valid upgrade.
        var previousStar = StrategyEffectClassTests.PreviousLayoutRevision(star);
        var recognizedStar = LayoutRevisionCompatibility.GetLegacyRevisions(starCopy, "ccz-6.6", "66-standard-v1");
        TestSupport.Assert(recognizedStar.SequenceEqual(new[] { previousStar }) &&
            LayoutRevisionCompatibility.IsCompatible(starCopy, "ccz-6.6", "66-standard-v1", previousStar, star.ActiveProfile.LayoutRevision),
            "同源Star前策略布局未被精确识别或混入了其他来源身份");
    }

    private static void CheckMigration(string source, string target, bool sourceStar, bool emptyTarget)
    {
        void Seed(string root, bool star, byte effect, bool blank)
        {
            var path = Path.Combine(root, star ? "Star.e5" : "Item.e5"); var bytes = File.ReadAllBytes(path); var offset = star ? 3800 : 0;
            if (blank) bytes.AsSpan(offset, 25).Clear();
            else { bytes[offset + 0x11] = effect; bytes[offset + 0x12] = 2; }
            File.WriteAllBytes(path, bytes);
        }
        Seed(source, sourceStar, 45, false); Seed(target, !sourceStar, 46, emptyTarget);
        var service = new MigrationService();
        var plan = service.Preview(source, target, sourceStar ? "ccz-6.6" : "ccz-6.5", sourceStar ? "66-standard-v1" : "65-standard-v1",
            sourceStar ? "ccz-6.5" : "ccz-6.6", sourceStar ? "65-standard-v1" : "66-standard-v1");
        var choices = plan.Changes.Select((change, index) => (change, index))
            .Where(item => item.change.Id == 256 && (emptyTarget ? item.change.TableKey == "item-empty-slot-fill" && item.change.Kind == MigrationChangeKind.NativeStructureChanged :
                item.change.TableKey == "items-extended" && item.change.Kind == MigrationChangeKind.Changed && item.change.FieldName is "宝物类型" or "装备特效号"))
            .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
        TestSupport.Assert(choices.Count > 0, "扩展跨codec迁移没有生成经过类别转换的候选");
        var before = Snapshot(target); var itemBefore = File.Exists(Path.Combine(target, "Item.e5")) ? File.ReadAllBytes(Path.Combine(target, "Item.e5")) : null;
        var result = service.Apply(plan, choices);
        TestSupport.Assert(result.Success, "扩展跨codec迁移失败：" + string.Join("；", result.Messages));
        var workspace = sourceStar ? Open(target, "ccz-6.5", "65-standard-v1") : OpenStar(target);
        var document = workspace.GetDocument("items-extended"); var raw = File.ReadAllBytes(document.FilePath); var offset = checked((int)document.Definition.DataOffset);
        TestSupport.Assert(raw[offset + 0x11] == 45 && raw[offset + 0x12] == 2 &&
            Convert.ToInt32(document.Data.Rows[0][sourceStar ? "宝物类型" : "装备特效号"]) == 45,
            "根Item/Star扩展迁移交换了实际效果与类别字节");
        foreach (var file in before)
        {
            var after = File.ReadAllBytes(Path.Combine(target, file.Key));
            TestSupport.Assert(Enumerable.Range(0, after.Length).Where(index => after[index] != file.Value[index]).All(index =>
                file.Key.Equals(document.Definition.FileName, StringComparison.OrdinalIgnoreCase) && index >= offset && index < offset + 25),
                "扩展单行迁移改动了目标行外文件或字节");
        }
        if (itemBefore != null)
        {
            var after = File.ReadAllBytes(Path.Combine(target, "Item.e5"));
            TestSupport.Assert(Enumerable.Range(0, after.Length).Where(index => after[index] != itemBefore[index]).All(index => index < 25),
                "根Item迁移改动其他扩展行");
        }
    }

    private static void MutateCode(string copy, uint address, byte value)
    {
        var path = Path.Combine(copy, "Ekd5.exe"); var bytes = File.ReadAllBytes(path);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        var optional = pe + 24; var imageBase = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optional + 28));
        var sections = optional + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20));
        for (var index = 0; index < BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 6)); index++)
        {
            var entry = sections + index * 40; var rva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 12));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 16));
            if (address - imageBase < rva || address - imageBase >= rva + length) continue;
            bytes[checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 20)) + address - imageBase - rva))] = value;
            File.WriteAllBytes(path, bytes); return;
        }
        throw new InvalidDataException("测试指令无PE映射");
    }

    private static void CheckUi(ProjectWorkspace workspace, string dataFile, string? descriptionFile)
    {
        using var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000), ClientSize = new Size(1280, 720) };
        using var panel = new BusinessEditorPanel(workspace) { Dock = DockStyle.Fill }; host.Controls.Add(panel); host.Show();
        foreach (var id in new[] { 256, 511 })
        {
            panel.Bind(BusinessPageKind.Items, id); Application.DoEvents();
            var source = Descendants(panel).OfType<Label>().Single(label => label.Name == "ItemStorageSource");
            TestSupport.Assert(source.Text.Contains(dataFile, StringComparison.OrdinalIgnoreCase) &&
                (descriptionFile == null ? source.Text.Contains("说明未加载", StringComparison.Ordinal) : source.Text.Contains(descriptionFile, StringComparison.OrdinalIgnoreCase)),
                "宝物扩展UI没有正确标注实际物理来源/缺说明");
            var expected = Name(workspace.GetDocument("items-extended"), id);
            var editor = Descendants(panel).OfType<ByteTextEditor>().Single(control => !control.Editor.Multiline && control.Editor.Text == expected);
            TestSupport.Assert(!editor.Editor.ReadOnly, "宝物扩展UI未开放真实可写名称");
            // The real 18-province row 256 has a two-byte name followed by NUL
            // and preserved nonzero tail data. Exercise a valid edit within its
            // actual budget rather than asking the editor to overwrite that tail.
            var replacement = expected == "界" ? "米" : "界";
            editor.Editor.Text = replacement;
            TestSupport.Assert(panel.Flush() && Name(workspace.GetDocument("items-extended"), id) == replacement, "宝物扩展UI输入未进入真实工作区草稿");
            workspace.Commands.Undo();
            TestSupport.Assert(Name(workspace.GetDocument("items-extended"), id) == expected && !workspace.IsDirty, "宝物扩展UI草稿不能撤回");
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static ProjectWorkspace OpenStar(string root) => Open(root, "ccz-6.6", "66-standard-v1");
    private static ProjectWorkspace Open(string root, string profile, string variant) { var workspace = new ProjectWorkspace(); workspace.Open(root, profile, variant); return workspace; }
    private static int RowIndex(TableDocument document, int id) => id - document.Definition.BeginId;
    private static string Name(TableDocument document, int id) => Convert.ToString(document.Data.Rows[RowIndex(document, id)]["名称"]) ?? string.Empty;
    private static Dictionary<string, byte[]> Snapshot(string root) => CoreFiles.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)));
    private static void WithCopy(string source, Action<string> check)
    {
        var hashes = Directory.EnumerateFiles(source).Where(path => CoreFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("Item.e5", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("Itemmsg.e5", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, HashService.Sha256);
        var copy = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        try
        {
            foreach (var path in hashes.Keys.Where(path => !CoreFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
                File.Copy(path, Path.Combine(copy, Path.GetFileName(path)));
            check(copy);
        }
        finally
        {
            foreach (var pair in hashes) TestSupport.Assert(HashService.Sha256(pair.Key) == pair.Value, "测试修改真实基底：" + pair.Key);
            Directory.Delete(copy, true);
        }
    }
}
