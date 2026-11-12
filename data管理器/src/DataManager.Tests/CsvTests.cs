using System.Data;
using System.Globalization;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Csv;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class CsvTests
{
    public static void Run(string basePath)
    {
        var temp = TestSupport.CreateCoreCopy(basePath);
        try
        {
            TestPhysicalFormatCompatibility(temp);
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

            foreach (var invalidFormat in new[]
            {
                "ID,名称,武力\r\n0,不应写入,4294967296\r\n",
                "ID,名称\r\n2147483647,不应写入\r\n",
                "ID,名称\r\n0,不应写入,多余列\r\n",
                "ID,名称\r\n0,\"未闭合\r\n"
            })
            {
                File.WriteAllText(csv, invalidFormat, new UTF8Encoding(true));
                var invalidRejected = false;
                try { workspace.ImportCsv(person, csv); }
                catch (InvalidDataException) { invalidRejected = true; }
                TestSupport.Assert(invalidRejected && !workspace.IsDirty &&
                    Convert.ToInt32(person.Data.Rows[0]["武力"], CultureInfo.InvariantCulture) == before,
                    "取消来源身份限制后仍须整体拒绝不可编码数值、无效目标、错误列数和坏引号");
            }

            foreach (var emptyTextRecord in new[] { "\"\"", "   ", "" })
            {
                File.WriteAllText(csv, $"名称\r\n{emptyTextRecord}\r\n第二人\r\n", new UTF8Encoding(true));
                var sequential = workspace.ImportCsv(person, csv);
                TestSupport.Assert(sequential.ImportedRows == 2 &&
                    Convert.ToString(person.Data.Rows[0]["名称"], CultureInfo.InvariantCulture) == (emptyTextRecord == "   " ? "   " : "") &&
                    Convert.ToString(person.Data.Rows[1]["名称"], CultureInfo.InvariantCulture) == "第二人",
                    "无编号 CSV 的空文本记录必须保留行位，不能将后续文本错写到前一人物");
                workspace.Commands.Undo();
            }
            File.WriteAllText(csv, "名称\r\n\"\"", new UTF8Encoding(true));
            TestSupport.Assert(workspace.ImportCsv(person, csv).ImportedRows == 1 &&
                Convert.ToString(person.Data.Rows[0]["名称"], CultureInfo.InvariantCulture) == "", "文件末尾无换行的空引号文本也须清空对应行");
            workspace.Commands.Undo();
            File.WriteAllText(csv, "ID,名称\r\n\r\n1,有ID回导\r\n", new UTF8Encoding(true));
            TestSupport.Assert(workspace.ImportCsv(person, csv).ImportedRows == 1 &&
                Convert.ToString(person.Data.Rows[1]["名称"], CultureInfo.InvariantCulture) == "有ID回导", "有编号 CSV 仍应跳过无目标的空行");
            workspace.Commands.Undo();

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

            TestSourceMetadataDoesNotRestrictImport(workspace, person, csv, personCsvText);
            var readonlyLevel = Convert.ToInt32(person.Data.Columns.Contains("级别") ? person.Data.Rows[0]["级别"] :
                person.OriginalFileBytes[person.Definition.DataOffset + 27], CultureInfo.InvariantCulture);
            File.WriteAllText(csv, "ID,名称,级别\r\n0,可写列回导,来源布局的任意文本\r\n", new UTF8Encoding(true));
            var preservedResult = workspace.ImportCsv(person, csv);
            var preservedLevel = Convert.ToInt32(person.Data.Columns.Contains("级别") ? person.Data.Rows[0]["级别"] :
                person.OriginalFileBytes[person.Definition.DataOffset + 27], CultureInfo.InvariantCulture);
            TestSupport.Assert(preservedResult.ChangedCells == 1 && preservedResult.SkippedReadOnlyCells == 1 && preservedLevel == readonlyLevel &&
                Convert.ToString(person.Data.Rows[0]["名称"], CultureInfo.InvariantCulture) == "可写列回导", "CSV 应忽略来源保留字段差异并导入可写列");
            workspace.Commands.Undo();

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
            foreach (var sourceHint in new[] { "#DataManagerCSV", "#DataManagerCSV,999,,,persons", "#DataManagerCSV,1,ccz-6.5,,persons,revision" })
            {
                File.WriteAllText(csv, sourceHint + "\r\n名称\r\n按序回导\r\n", new UTF8Encoding(true));
                TestSupport.Assert(workspace.ImportCsv(person, csv).ChangedCells == 1 &&
                    Convert.ToString(person.Data.Rows[0]["名称"], CultureInfo.InvariantCulture) == "按序回导", "来源提示不完整或无编号时应按实际字段和行序回导");
                workspace.Commands.Undo();
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

    private static void TestPhysicalFormatCompatibility(string directory)
    {
        var path = Path.Combine(directory, "physical-format-compatibility.csv");
        var numericFields = new[]
        {
            new TableFieldDefinition("ByteValue", 0, 1, FieldKind.UInt8, Minimum: 2, Maximum: 10),
            new TableFieldDefinition("WordValue", 1, 2, FieldKind.UInt16, Minimum: 2, Maximum: 10),
            new TableFieldDefinition("DwordValue", 3, 4, FieldKind.UInt32, Minimum: 2, Maximum: 10)
        };
        var data = new DataTable();
        data.Columns.Add("ID", typeof(int));
        data.Columns.Add("ByteValue", typeof(int));
        data.Columns.Add("WordValue", typeof(int));
        data.Columns.Add("DwordValue", typeof(long));
        data.Rows.Add(0, 5, 5, 5L);
        var definition = new TableDefinition("physical-format-test", "物理格式测试", "test.bin", 0, 1, 7, 0, numericFields, "test");
        var document = new TableDocument { Definition = definition, FilePath = path, Data = data, OriginalFileBytes = [], OriginalFileHash = "" };
        var csv = new CsvService();
        File.WriteAllText(path, "ID,ByteValue,WordValue,DwordValue\r\n0,255,65535,4294967295\r\n", new UTF8Encoding(true));
        TestSupport.Assert(csv.Import(document, path).ChangedCells == 3 && (int)data.Rows[0]["ByteValue"] == byte.MaxValue &&
            (int)data.Rows[0]["WordValue"] == ushort.MaxValue && (long)data.Rows[0]["DwordValue"] == uint.MaxValue,
            "CSV 数值应接受超出语义范围但符合目标 UInt8/16/32 容量的值");
        File.WriteAllText(path, "ID,ByteValue,WordValue,DwordValue\r\n0,0,0,0\r\n", new UTF8Encoding(true));
        TestSupport.Assert(csv.Import(document, path).ChangedCells == 3, "CSV 物理无符号字段应允许语义最小值以下的零");
        foreach (var (column, invalid) in new[] { ("ByteValue", "256"), ("WordValue", "65536"), ("DwordValue", "4294967296"), ("ByteValue", "-1") })
        {
            File.WriteAllText(path, $"ID,{column}\r\n0,{invalid}\r\n", new UTF8Encoding(true));
            var rejected = false;
            try { csv.Import(document, path); }
            catch (InvalidDataException) { rejected = true; }
            TestSupport.Assert(rejected && Convert.ToInt64(data.Rows[0][column], CultureInfo.InvariantCulture) == 0,
                "取消语义范围后仍须拒绝超出目标物理容量的数值：" + column);
        }

        var ignoredData = new DataTable();
        ignoredData.Columns.Add("ID", typeof(int));
        ignoredData.Columns.Add("名称", typeof(string));
        ignoredData.Columns.Add("锁定列", typeof(string));
        ignoredData.Rows.Add(0, "原名", "原锁定值");
        ignoredData.Columns["锁定列"]!.ReadOnly = true;
        var ignoredFields = new[]
        {
            new TableFieldDefinition("名称", 0, 16, FieldKind.FixedString),
            new TableFieldDefinition("来源Raw", 16, 2, FieldKind.RawBytes, FieldPolicy.Preserve),
            new TableFieldDefinition("只读编号", 18, 2, FieldKind.UInt16, FieldPolicy.ReadOnly),
            new TableFieldDefinition("不可见列", 20, 1, FieldKind.UInt8),
            new TableFieldDefinition("锁定列", 21, 8, FieldKind.FixedString)
        };
        var ignoredDefinition = new TableDefinition("ignored-source-test", "来源忽略测试", "test.bin", 0, 1, 29, 0, ignoredFields, "test");
        var ignoredDocument = new TableDocument { Definition = ignoredDefinition, FilePath = path, Data = ignoredData, OriginalFileBytes = [], OriginalFileHash = "" };
        File.WriteAllText(path, "ID,名称,来源Raw,只读编号,不可见列,锁定列\r\n0,新名,不同长度且非十六进制,不是数字,也不是数字,来源中超长的锁定文本内容\r\n", new UTF8Encoding(true));
        var ignored = csv.Import(ignoredDocument, path);
        TestSupport.Assert(ignored.ChangedCells == 1 && ignored.SkippedReadOnlyCells == 4 &&
            (string)ignoredData.Rows[0]["名称"] == "新名" && (string)ignoredData.Rows[0]["锁定列"] == "原锁定值",
            "只读/保留/不可写来源列须在读取与解析前忽略，不能阻止可写列导入");
        var readonlyDocument = new TableDocument { Definition = ignoredDefinition with { Capability = TableCapability.ReadOnly },
            FilePath = path, Data = ignoredData, OriginalFileBytes = [], OriginalFileHash = "" };
        File.WriteAllText(path, "ID,名称\r\n0,超过目标文本长度但整表不可写所以忽略\r\n", new UTF8Encoding(true));
        var readonlyResult = csv.Import(readonlyDocument, path);
        TestSupport.Assert(readonlyResult.ChangedCells == 0 && readonlyResult.SkippedReadOnlyCells == 1 &&
            (string)ignoredData.Rows[0]["名称"] == "新名", "不可写整表的来源字段也须直接忽略");
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

    private static void TestSourceMetadataDoesNotRestrictImport(ProjectWorkspace workspace, Core.Models.TableDocument document, string path, string exportedText)
    {
        var newline = exportedText.IndexOf('\n');
        var metadata = exportedText[..newline].TrimEnd('\r').Split(',');
        foreach (var (index, sourceValue) in new[]
        {
            (1, "999"), (2, "ccz-6.6"), (3, "other-variant"),
            (4, "other-table"), (5, "other-layout")
        })
        {
            var source = (string[])metadata.Clone();
            source[index] = sourceValue;
            File.WriteAllText(path, string.Join(',', source) + exportedText[newline..], new UTF8Encoding(true));
            TestSupport.Assert(workspace.ImportCsv(document, path).ChangedCells == 0 && !workspace.IsDirty,
                $"CSV 来源提示不应限制格式兼容的回导：{index}");
        }
    }
}
