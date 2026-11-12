using System.Globalization;
using System.Text.Json.Nodes;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Workbooks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace DataManager.Tests;

/// <summary>Strategy workbook validation, warning context and exact prior-layout compatibility.</summary>
internal static class StrategyWorkbookTests
{
    internal static void Run(string basePath)
    {
        var repository = TestSupport.RepositoryRootFromBase(basePath);
        foreach (var name in new[] { "洗隋", "加强版6.5未加密版", "复刻版6.6山贼" })
        {
            var source = Path.Combine(repository, "基底", name);
            var originalHashes = Known65FixtureSnapshot.CoreNames.ToDictionary(file => file,
                file => HashService.Sha256(ProjectFileLocator.Resolve(source, file)));
            var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
            try
            {
                var workspace = new ProjectWorkspace(); workspace.Open(root);
                var service = new PageWorkbookService(workspace);
                var effects = workspace.GetDocument(StrategyEffectClassService.TableKey);
                var strategies = workspace.GetDocument("strategies");
                var originalEffect = Convert.ToInt32(effects.Data.Rows[0][StrategyEffectClassService.FieldName]);
                var originalMp = Convert.ToInt32(strategies.Data.Rows[0]["策略消耗"]);
                var nextMp = (originalMp + 1) % 256;
                var before = Known65FixtureSnapshot.CoreNames.ToDictionary(file => file,
                    file => HashService.Sha256(ProjectFileLocator.Resolve(root, file)));
                var normal = Path.Combine(root, "策略效果回导.xlsx");
                service.Export(BusinessPageKind.Strategies, normal);
                TestSupport.Assert(service.PreviewImport(BusinessPageKind.Strategies, normal).ChangedCells == 0 && !workspace.IsDirty,
                    "新增效果类工作表原样回导不是无操作。");
                Edit(normal, StrategyEffectClassService.TableKey, StrategyEffectClassService.FieldName, "9");
                var preview = service.PreviewImport(BusinessPageKind.Strategies, normal);
                TestSupport.Assert(preview.ChangedCells == 1 && preview.Warnings.Count == 1 &&
                    preview.Warnings[0].Contains("策略ID 0", StringComparison.Ordinal) && !workspace.IsDirty,
                    "跨族效果工作簿没有可确认的警告，或预览修改了草稿。");
                TestSupport.Assert(service.ApplyImport(preview) == 1 &&
                    workspace.Documents.Values.SelectMany(document => document.EnumerateChanges()).Count() == 1 &&
                    Convert.ToInt32(effects.Data.Rows[0][StrategyEffectClassService.FieldName]) == 9,
                    "效果工作簿没有只应用一个字段草稿。");
                AssertHashes(root, before);
                workspace.Commands.Undo();
                TestSupport.Assert(!workspace.IsDirty && Convert.ToInt32(effects.Data.Rows[0][StrategyEffectClassService.FieldName]) == originalEffect,
                    "效果工作簿不能整次撤回。");

                var coordinated = Path.Combine(root, "效果与对象联合回导.xlsx");
                service.Export(BusinessPageKind.Strategies, coordinated);
                Edit(coordinated, StrategyEffectClassService.TableKey, StrategyEffectClassService.FieldName, "13");
                Edit(coordinated, "strategies", "施展对象", "1");
                preview = service.PreviewImport(BusinessPageKind.Strategies, coordinated);
                TestSupport.Assert(preview.ChangedCells == 2 && preview.Warnings.Count == 1 &&
                    preview.Warnings[0].Contains("效果类别不同", StringComparison.Ordinal) &&
                    !preview.Warnings[0].Contains("施展对象", StringComparison.Ordinal),
                    "工作簿效果警告没有结合本次同时提交的目标侧字段。");
                TestSupport.Assert(!workspace.IsDirty, "联合效果预览提前提交了字段。");

                foreach (var invalid in new[] { "42", "48", "144", "255", "非数字" })
                {
                    var bad = Path.Combine(root, "非法策略效果.xlsx");
                    service.Export(BusinessPageKind.Strategies, bad);
                    Edit(bad, "strategies", "策略消耗", nextMp.ToString(CultureInfo.InvariantCulture));
                    Edit(bad, StrategyEffectClassService.TableKey, StrategyEffectClassService.FieldName, invalid);
                    Reject(() => service.PreviewImport(BusinessPageKind.Strategies, bad), "实际值", invalid, "ID=0", StrategyEffectClassService.FieldName);
                    TestSupport.Assert(!workspace.IsDirty &&
                        Convert.ToInt32(strategies.Data.Rows[0]["策略消耗"]) == originalMp &&
                        Convert.ToInt32(effects.Data.Rows[0][StrategyEffectClassService.FieldName]) == originalEffect,
                        "非法效果类回导预检留下前序合法字段的部分草稿。");
                    AssertHashes(root, before);
                }

                var old = Path.Combine(root, "上一版策略工作簿.xlsx");
                service.Export(BusinessPageKind.Strategies, old);
                RemoveEffectDataset(old, StrategyEffectClassTests.PreviousLayoutRevision(workspace));
                TestSupport.Assert(service.PreviewImport(BusinessPageKind.Strategies, old).ChangedCells == 0,
                    "精确上一版工作簿缺少新增效果数据集时不能原样回导。");
                Edit(old, "strategies", "策略消耗", nextMp.ToString(CultureInfo.InvariantCulture));
                preview = service.PreviewImport(BusinessPageKind.Strategies, old);
                TestSupport.Assert(preview.ChangedCells == 1 && preview.Warnings.Count == 0 && service.ApplyImport(preview) == 1 &&
                    Convert.ToInt32(effects.Data.Rows[0][StrategyEffectClassService.FieldName]) == originalEffect,
                    "上一版策略工作簿改变了新效果类或不能回导既有字段。");
                workspace.Commands.Undo(); TestSupport.Assert(!workspace.IsDirty, "上一版策略工作簿撤回后仍有草稿。");
                var missing = Path.Combine(root, "当前版缺效果数据集.xlsx");
                service.Export(BusinessPageKind.Strategies, missing);
                RemoveEffectDataset(missing, workspace.ActiveProfile.LayoutRevision);
                Reject(() => service.PreviewImport(BusinessPageKind.Strategies, missing), "数据集");
                TestSupport.Assert(!workspace.IsDirty, "拒绝当前版缺失数据集时留下草稿。");
                AssertHashes(root, before);
                Console.WriteLine($"STRATEGY_WORKBOOK_FIXTURE_OK {name} warnings atomic-invalid pending-target previous-layout");
            }
            finally { Directory.Delete(root, true); }
            AssertHashes(source, originalHashes);
        }
        Console.WriteLine("STRATEGY_WORKBOOK_TESTS_OK originals-unchanged");
    }

    private static void Edit(string path, string key, string field, string value)
    {
        using var package = SpreadsheetDocument.Open(path, true);
        var part = package.WorkbookPart!;
        var manifest = Manifest(part);
        var id = manifest["Datasets"]!.AsArray().Single(node => node!["Key"]!.GetValue<string>() == key)!["SheetId"]!.GetValue<uint>();
        var sheet = part.Workbook.Sheets!.Elements<Sheet>().Single(candidate => candidate.SheetId?.Value == id);
        var data = ((WorksheetPart)part.GetPartById(sheet.Id!)).Worksheet.GetFirstChild<SheetData>()!;
        var header = data.Elements<Row>().Single(row => row.RowIndex?.Value == 1).Elements<Cell>().Single(cell => cell.InnerText == field);
        var letters = new string(header.CellReference!.Value!.TakeWhile(char.IsLetter).ToArray());
        var target = data.Elements<Row>().Single(row => row.RowIndex?.Value == 3).Elements<Cell>().Single(cell => cell.CellReference == letters + "3");
        target.RemoveAllChildren(); target.DataType = CellValues.InlineString;
        target.InlineString = new InlineString(new Text(value));
        foreach (var worksheet in part.WorksheetParts) worksheet.Worksheet.Save();
        part.Workbook.Save();
    }

    private static void RemoveEffectDataset(string path, string layout)
    {
        using var package = SpreadsheetDocument.Open(path, true);
        var part = package.WorkbookPart!; var manifest = Manifest(part);
        var registrations = manifest["Datasets"]!.AsArray();
        var entry = registrations.Single(node => node!["Key"]!.GetValue<string>() == StrategyEffectClassService.TableKey)!;
        foreach (var key in new[] { "SheetId", "BaselineSheetId" })
        {
            var id = entry[key]!.GetValue<uint>();
            var sheet = part.Workbook.Sheets!.Elements<Sheet>().Single(candidate => candidate.SheetId?.Value == id);
            var worksheet = (WorksheetPart)part.GetPartById(sheet.Id!);
            sheet.Remove(); part.DeletePart(worksheet);
        }
        registrations.Remove(entry); manifest["Layout"] = layout;
        var meta = part.Workbook.Sheets!.Elements<Sheet>().Single(sheet => sheet.Name == "_DataManager");
        var metadata = (WorksheetPart)part.GetPartById(meta.Id!);
        metadata.Worksheet.GetFirstChild<SheetData>()!.Remove();
        metadata.Worksheet.AppendChild(new SheetData(new Row(new Cell
        {
            CellReference = "A1", DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(manifest.ToJsonString()))
        }) { RowIndex = 1 }));
        metadata.Worksheet.Save(); part.Workbook.Save();
    }

    private static JsonObject Manifest(WorkbookPart part)
    {
        var meta = part.Workbook.Sheets!.Elements<Sheet>().Single(sheet => sheet.Name == "_DataManager");
        var json = string.Concat(((WorksheetPart)part.GetPartById(meta.Id!)).Worksheet.Descendants<Text>().Select(text => text.Text));
        return JsonNode.Parse(json)!.AsObject();
    }

    private static void Reject(Action action, params string[] reasons)
    {
        try { action(); }
        catch (InvalidDataException error)
        {
            foreach (var reason in reasons)
                TestSupport.Assert(error.Message.Contains(reason, StringComparison.Ordinal), "导入错误缺少定位信息“" + reason + "”：" + error.Message);
            return;
        }
        throw new InvalidOperationException("非法策略工作簿未被拒绝。");
    }

    private static void AssertHashes(string root, IReadOnlyDictionary<string, string> expected)
    {
        foreach (var pair in expected)
            TestSupport.Assert(HashService.Sha256(ProjectFileLocator.Resolve(root, pair.Key)) == pair.Value,
                "策略工作簿预览/导入改写了游戏磁盘文件：" + pair.Key);
    }
}
