using System.Globalization;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Csv;
using DataManager.Core.Domain;
using DataManager.Core.Editing;

namespace DataManager.Tests;

internal static class CsvTests
{
    public static void Run(string basePath)
    {
        var temp = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(temp);
            var person = workspace.GetDocument("persons");
            var csv = Path.Combine(temp, "person.csv");
            File.WriteAllText(csv, "ID,名称,武力\r\n0,CSV测试,99\r\n", new UTF8Encoding(true));
            var result = workspace.ImportCsv(person, csv);
            TestSupport.Assert(result.ChangedCells == 2 && workspace.IsDirty, "CSV 导入没有形成预期草稿");
            TestSupport.Assert(workspace.Commands.Undo() != null && Convert.ToString(person.Data.Rows[0]["名称"], CultureInfo.InvariantCulture) != "CSV测试", "CSV 导入无法整体撤回");

            var before = Convert.ToInt32(person.Data.Rows[0]["武力"], CultureInfo.InvariantCulture);
            File.WriteAllText(csv, "ID,武力\r\n0,88\r\n0,77\r\n", new UTF8Encoding(true));
            var duplicateRejected = false;
            try { workspace.ImportCsv(person, csv); }
            catch (InvalidDataException) { duplicateRejected = true; }
            TestSupport.Assert(duplicateRejected && Convert.ToInt32(person.Data.Rows[0]["武力"], CultureInfo.InvariantCulture) == before, "重复 ID CSV 没有整体拒绝");

            var export = Path.Combine(temp, "person-export.csv");
            workspace.ExportCsv(person, export);
            var personCsvText = File.ReadAllText(export, new UTF8Encoding(true));
            var csvContext = CsvService.ReadContext(export);
            TestSupport.Assert(csvContext?.TableKey == "persons" && csvContext.ProfileId == "ccz-6.5" &&
                csvContext.VariantId == "65-standard-v1" && csvContext.LayoutRevision == workspace.ActiveProfile.LayoutRevision,
                "CSV 元数据读取应独立于文件名并保留完整布局身份");
            TestSupport.Assert(personCsvText.StartsWith("#DataManagerCSV,1,ccz-6.5,65-standard-v1,persons,", StringComparison.Ordinal), "CSV 导出缺少物理表元数据");
            TestSupport.Assert(personCsvText.Split('\n')[1].StartsWith("编号,", StringComparison.Ordinal), "CSV 导出缺少中文编号首列");
            TestSupport.Assert(personCsvText.Contains("级别", StringComparison.Ordinal) && personCsvText.Contains("部队原始值", StringComparison.Ordinal) &&
                personCsvText.Contains("初始气血", StringComparison.Ordinal) && personCsvText.Contains("初始策略值", StringComparison.Ordinal), "CSV 应以中文表头完整保留隐藏物理字段");
            TestSupport.Assert(workspace.ImportCsv(person, export).ChangedCells == 0, "人物 CSV 完整回导产生了无效变更");
            var oldColumnNames = personCsvText.Replace("编号,", "ID,", StringComparison.Ordinal)
                .Replace("部队原始值", "Army", StringComparison.Ordinal).Replace("初始气血", "初始HP", StringComparison.Ordinal)
                .Replace("初始策略值", "初始MP", StringComparison.Ordinal);
            File.WriteAllText(csv, oldColumnNames, new UTF8Encoding(true));
            TestSupport.Assert(workspace.ImportCsv(person, csv).ChangedCells == 0, "旧英文物理字段表头不再兼容导入");
            File.WriteAllText(csv, "编号,ID,名称\r\n0,0,不应写入\r\n", new UTF8Encoding(true));
            var duplicatedIdAliasRejected = false;
            try { workspace.ImportCsv(person, csv); }
            catch (InvalidDataException ex) { duplicatedIdAliasRejected = ex.Message.Contains("重复映射", StringComparison.Ordinal); }
            TestSupport.Assert(duplicatedIdAliasRejected && !workspace.IsDirty, "同一编号的中英文别名必须被整体拒绝");

            var business = new BusinessDataService(workspace);
            var exports = business.ExportPageCsv(BusinessPageKind.Persons, Path.Combine(temp, "人物导出"));
            TestSupport.Assert(exports.Single(item => item.TableKey == "persons").DisplayName == "人物" &&
                Path.GetFileName(exports.Single(item => item.TableKey == "persons").Path) == "人物.csv" &&
                exports.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == exports.Count,
                "业务 CSV 文件名和显示名应为中文且不得互相覆盖");
            var bundle = exports.ToDictionary(item => Path.GetFileName(item.Path), item => item.Path);
            TestSupport.Assert(business.ImportPageCsvAtomic(BusinessPageKind.Persons, bundle).ChangedCells == 0,
                "中文文件名 CSV 包不能原样导回对应物理表");
            var duplicateTableRejected = false;
            try { business.ImportPageCsvAtomic(BusinessPageKind.Persons, new Dictionary<string, string>
                { ["persons"] = export, ["人物.csv"] = export }); }
            catch (InvalidDataException ex) { duplicateTableRejected = ex.Message.Contains("重复指定", StringComparison.Ordinal); }
            TestSupport.Assert(duplicateTableRejected && !workspace.IsDirty, "同一物理表的中英文文件名不能重复导入");

            TestMetadataMismatch(workspace, person, csv, personCsvText);
            var readonlyLevel = Convert.ToInt32(person.Data.Columns.Contains("级别") ? person.Data.Rows[0]["级别"] :
                person.OriginalFileBytes[person.Definition.DataOffset + 27], CultureInfo.InvariantCulture);
            File.WriteAllText(csv, $"ID,名称,级别\r\n0,不应写入,{(readonlyLevel + 1) % 256}\r\n", new UTF8Encoding(true));
            var readonlyRejected = false;
            try { workspace.ImportCsv(person, csv); }
            catch (InvalidDataException ex) { readonlyRejected = ex.Message.Contains("只读/保留列", StringComparison.Ordinal); }
            TestSupport.Assert(readonlyRejected && Convert.ToString(person.Data.Rows[0]["名称"], CultureInfo.InvariantCulture) != "不应写入", "CSV 修改保留字段应整体拒绝");

            foreach (var encoding in new[] { Encoding.Unicode, Encoding.BigEndianUnicode, EncodingService.GameEncoding })
            {
                File.WriteAllText(csv, "编号,名称\r\n0,编码回导\r\n", encoding);
                TestSupport.Assert(CsvService.ReadContext(csv) == null, "旧无元数据 CSV 不应被误识别为带布局身份");
                TestSupport.Assert(workspace.ImportCsv(person, csv).ChangedCells == 1, "UTF-16/GBK CSV 没有正确导入");
                workspace.Commands.Undo();
                File.WriteAllText(csv, personCsvText, encoding);
                TestSupport.Assert(CsvService.ReadContext(csv) == csvContext && workspace.ImportCsv(person, csv).ChangedCells == 0,
                    "UTF-16/GBK 中文 CSV 元数据读取与原样回导失败");
            }
            foreach (var malformed in new[] { "#DataManagerCSV,1,ccz-6.5\r\n", "#DataManagerCSV,1,ccz-6.5,,persons,revision\r\n" })
            {
                File.WriteAllText(csv, malformed, new UTF8Encoding(true));
                var contextRejected = false;
                try { CsvService.ReadContext(csv); }
                catch (InvalidDataException ex) { contextRejected = ex.Message.Contains("元数据", StringComparison.Ordinal); }
                TestSupport.Assert(contextRejected, "CSV 首行元数据格式错误必须明确拒绝");
            }

            var items = workspace.GetDocument("items-low");
            var itemExport = Path.Combine(temp, "items-export.csv");
            workspace.ExportCsv(items, itemExport);
            var itemCsvText = File.ReadAllText(itemExport, new UTF8Encoding(true));
            TestSupport.Assert(!itemCsvText.Contains("物品分类", StringComparison.Ordinal) && !itemCsvText.Contains("可装备部队", StringComparison.Ordinal), "CSV 不应导出物理表派生列");

            foreach (var document in workspace.Documents.Values)
            {
                var roundTripPath = Path.Combine(temp, document.Definition.Key + "-roundtrip.csv");
                workspace.ExportCsv(document, roundTripPath);
                TestSupport.Assert(workspace.ImportCsv(document, roundTripPath).ChangedCells == 0,
                    $"物理表 CSV 原样回导失败：{document.Definition.Key}");
            }

            var biography = workspace.GetDocument("person-biography");
            var originalName = Convert.ToString(person.Data.Rows[0]["名称"], CultureInfo.InvariantCulture) ?? string.Empty;
            var originalBiography = Convert.ToString(biography.Data.Rows[0]["人物列传"], CultureInfo.InvariantCulture) ?? string.Empty;
            var validName = Path.Combine(temp, "bundle-person.csv");
            var invalidBiography = Path.Combine(temp, "bundle-biography-invalid.csv");
            File.WriteAllText(validName, "ID,名称\r\n0,批量回滚测试\r\n", new UTF8Encoding(true));
            File.WriteAllText(invalidBiography, "ID,不存在的列\r\n0,不应写入\r\n", new UTF8Encoding(true));
            var bundleRejected = false;
            try { workspace.ImportCsvBundle(new Dictionary<string, string> { ["persons"] = validName, ["person-biography"] = invalidBiography }); }
            catch (InvalidDataException) { bundleRejected = true; }
            TestSupport.Assert(bundleRejected, "CSV 批量导入应拒绝未知字段");
            TestSupport.Assert(Convert.ToString(person.Data.Rows[0]["名称"], CultureInfo.InvariantCulture) == originalName &&
                               Convert.ToString(biography.Data.Rows[0]["人物列传"], CultureInfo.InvariantCulture) == originalBiography,
                "CSV 批量导入失败后未回滚前序表修改");
        }
        finally { Directory.Delete(temp, true); }
        TestVariantAndExtendedRoundTrips(basePath);
    }

    private static void TestVariantAndExtendedRoundTrips(string basePath)
    {
        var standard66 = Path.Combine(TestSupport.RepositoryRootFromBase(basePath), "基底", "曹操传6.6-V7.16 解压密码：66");
        foreach (var (source, profile, variant) in new[]
        {
            (basePath, "ccz-6.5", "65-standard-v1"),
            (standard66, "ccz-6.6", "66-standard-v1"),
            (standard66, "ccz-6.6", "66-legacy-data-v1"),
            (standard66, "ccz-6.6", "66-shanzhai-legacy")
        })
        {
            if (!Directory.Exists(source)) continue;
            var root = TestSupport.CreateCoreCopy(source);
            try
            {
                var workspace = new ProjectWorkspace();
                workspace.Open(root, profile, variant);
                if (profile == "ccz-6.5")
                {
                    foreach (var key in new[] { "effect-archive", "effect-messages", "strategy-study-extended", "forge" })
                    {
                        var definition = workspace.Definitions.Single(item => item.Key == key);
                        File.WriteAllBytes(Path.Combine(root, definition.FileName), new byte[checked((int)definition.EndOffsetExclusive)]);
                    }
                    workspace.Open(root, profile, variant);
                }
                foreach (var document in workspace.Documents.Values)
                {
                    var path = Path.Combine(root, document.Definition.Key + ".csv");
                    workspace.ExportCsv(document, path);
                    TestSupport.Assert(workspace.ImportCsv(document, path).ChangedCells == 0,
                        $"{variant} 物理表 CSV 回导失败：{document.Definition.Key}");
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static void TestMetadataMismatch(ProjectWorkspace workspace, Core.Models.TableDocument document, string path, string exportedText)
    {
        var newline = exportedText.IndexOf('\n');
        var metadata = exportedText[..newline].TrimEnd('\r').Split(',');
        foreach (var (index, badValue, expectedMessage) in new[]
        {
            (1, "999", "schema"), (2, "ccz-6.6", "Profile"), (3, "other-variant", "Variant"),
            (4, "other-table", "TableKey"), (5, "other-layout", "布局修订")
        })
        {
            var invalid = (string[])metadata.Clone();
            invalid[index] = badValue;
            File.WriteAllText(path, string.Join(',', invalid) + exportedText[newline..], new UTF8Encoding(true));
            var rejected = false;
            try { workspace.ImportCsv(document, path); }
            catch (InvalidDataException ex) { rejected = ex.Message.Contains(expectedMessage, StringComparison.Ordinal); }
            TestSupport.Assert(rejected, $"CSV 元数据不匹配未明确拒绝：{expectedMessage}");
        }
    }
}
