using System.Text;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Csv;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class AccessoryGroupCsvTests
{
    public static void Run(string basePath)
    {
        var originalHash = HashService.Sha256(ProjectFileLocator.Resolve(basePath, "Ekd5.exe"));
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            var definition = workspace.Definitions.Single(table => table.Key == AccessoryJobGroupCsvService.TableKey);
            TestSupport.Assert(definition.Capability == TableCapability.Unavailable && !workspace.Documents.ContainsKey(definition.Key),
                "辅助分组占位表不应解码 EXE 头或生成物理文档");
            var business = new BusinessDataService(workspace);
            TestSupport.Assert(business.GetAvailableTableKeys(BusinessPageKind.JobSeries).Contains(definition.Key) && business.LoadJobSeries().Records.Count == 40 &&
                business.GetIdNameOptions(BusinessPageKind.JobSeries).Count == 40, "专用分组入口与兵种系实体列表应同时可用");
            var exports = business.ExportPageCsv(BusinessPageKind.JobSeries, Path.Combine(root, "physical-csv"));
            TestSupport.Assert(exports.All(item => item.TableKey != definition.Key) && !File.Exists(Path.Combine(root, "physical-csv", definition.Key + ".csv")),
                "物理 CSV 不得导出辅助分组占位偏移的 EXE 首字节");
            var denied = false;
            try { business.BeginTransaction(BusinessPageKind.JobSeries).Set(definition.Key, 0, "Raw", "4D"); }
            catch (InvalidOperationException) { denied = true; }
            TestSupport.Assert(denied, "辅助分组不能作为普通物理表草稿编辑");
            denied = false;
            try { business.ImportPageCsvAtomic(BusinessPageKind.JobSeries, new Dictionary<string, string> { [definition.Key] = "unused.csv" }); }
            catch (InvalidDataException ex) { denied = ex.Message.Contains("辅助分组子页", StringComparison.Ordinal); }
            TestSupport.Assert(denied, "批量物理 CSV 遇到专用分组时应给出专用入口提示，不能按占位表访问");
            using (var panel = new BusinessEditorPanel(workspace))
            {
                panel.Bind(BusinessPageKind.JobSeries, 0);
                var groupTab = panel.DetailTabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "辅助分组");
                var labels = Descendants(groupTab).OfType<Button>().Select(button => button.Text).ToArray();
                TestSupport.Assert(labels.Contains("导出 CSV") && labels.Contains("导入 CSV") && labels.Contains("校验保存") && labels.Contains("新增空白分组"),
                    "辅助分组页缺少独立 CSV 交换与校验提交入口");
            }

            var service = new AccessoryJobGroupService();
            var csv = new AccessoryJobGroupCsvService();
            var executable = ProjectFileLocator.Resolve(root, "Ekd5.exe");
            var before = File.ReadAllBytes(executable);
            var groups = service.Read(workspace).Groups.Select(group => group.JobSeriesIds).ToArray();
            var path = Path.Combine(root, "accessory-groups.csv");
            foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, EncodingService.GameEncoding })
            {
                csv.Export(workspace, path, groups, encoding);
                var imported = csv.ImportDraft(workspace, path);
                TestSupport.Assert(Equal(groups, imported), "辅助分组 CSV 不能以现有支持的字符编码原样导回");
            }
            csv.Export(workspace, path, groups);
            var chineseText = File.ReadAllText(path);
            TestSupport.Assert(chineseText.Split('\n')[1].TrimEnd('\r') == "分组编号,兵种系编号列表" &&
                AccessoryJobGroupCsvService.DefaultFileName == "兵种系-辅助装备分组.csv",
                "辅助分组 CSV 的列名和默认文件名应全部中文");
            File.WriteAllText(path, chineseText.Replace("分组编号,兵种系编号列表", "GroupID,JobSeriesIDs", StringComparison.Ordinal), new UTF8Encoding(true));
            TestSupport.Assert(Equal(groups, csv.ImportDraft(workspace, path)), "旧英文辅助分组表头不再兼容导入");
            TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo && workspace.GetHistory().Count == 0 &&
                File.ReadAllBytes(executable).SequenceEqual(before), "分组 CSV 导入/导出不应产生草稿、保存历史或磁盘写入");

            var profile = workspace.ActiveProfile;
            IReadOnlyList<string> Metadata(string? variant = null) => new[]
                { "#DataManagerCSV", "1", profile.ProfileId, variant ?? profile.VariantId, definition.Key, profile.LayoutRevision };
            void Reject(IReadOnlyList<IReadOnlyList<string>> records, string expectedMessage)
            {
                CsvService.WriteRecords(path, records);
                var rejected = false;
                try { csv.ImportDraft(workspace, path); }
                catch (InvalidDataException ex) { rejected = ex.Message.Contains(expectedMessage, StringComparison.Ordinal); }
                TestSupport.Assert(rejected && File.ReadAllBytes(executable).SequenceEqual(before) && !workspace.IsDirty,
                    "非法分组 CSV 没有无副作用地拒绝：" + expectedMessage);
            }
            var headers = new[] { "GroupID", "JobSeriesIDs" };
            Reject([Metadata("66-standard-v1"), headers, new[] { "0", "0,1" }], "不匹配");
            Reject([Metadata(), new[] { "GroupID", "Unknown" }, new[] { "0", "0,1" }], "列必须");
            Reject([Metadata(), headers, new[] { "0", "" }], "不能为空");
            Reject([Metadata(), headers, new[] { "0", "1,1" }], "重复兵种系");
            Reject([Metadata(), headers, new[] { "0", "40" }], "越界");
            Reject([Metadata(), headers, new[] { "0", "0" }, new[] { "0", "1" }], "重复 GroupID");
            Reject([Metadata(), headers, new[] { "1", "0,1" }], "连续编号");
            var oversized = new List<IReadOnlyList<string>> { Metadata(), headers };
            oversized.AddRange(Enumerable.Range(0, 8).Select(index => (IReadOnlyList<string>)new[] { index.ToString(), string.Join(',', Enumerable.Range(0, 40)) }));
            Reject(oversized, "超过安全区");

            csv.Export(workspace, path, [[0, 1], [2, 3]]);
            var draft = csv.ImportDraft(workspace, path);
            TestSupport.Assert(File.ReadAllBytes(executable).SequenceEqual(before), "导入分组 CSV 提前写入了 EXE");
            service.Write(workspace, draft);
            TestSupport.Assert(Equal(draft, service.Read(workspace).Groups.Select(group => group.JobSeriesIds).ToArray()) &&
                workspace.GetHistory().Last().OperationKind == "专用二进制保存", "CSV 分组没有通过工作区二进制事务保存复读");
            var after = File.ReadAllBytes(executable);
            TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(executable).SequenceEqual(before) &&
                workspace.RedoSaved().Success && File.ReadAllBytes(executable).SequenceEqual(after), "CSV 辅助分组保存历史不能撤回/重做");
            // Missing Data/Star/Imsg must not turn a native-only business key
            // into an attempted GetDocument of a fake physical table.
            var groupRange = service.Read(workspace);
            using (var stream = File.OpenWrite(executable)) stream.SetLength(groupRange.FileOffset + groupRange.WritableLength);
            foreach (var name in new[] { "Data.e5", "Star.e5", "Imsg.e5" }) File.Delete(Path.Combine(root, name));
            var incomplete = new ProjectWorkspace();
            incomplete.Open(root, "ccz-6.5", "65-standard-v1");
            var incompleteBusiness = new BusinessDataService(incomplete);
            TestSupport.Assert(incompleteBusiness.GetAvailableTableKeys(BusinessPageKind.JobSeries).Contains(definition.Key) &&
                incompleteBusiness.GetIdNameOptions(BusinessPageKind.JobSeries).Count == 0 && incompleteBusiness.LoadJobSeries().Records.Count == 0,
                "缺少实体表时专用分组入口不应被作为普通实体表枚举");
        }
        finally { Directory.Delete(root, true); }
        TestSupport.Assert(HashService.Sha256(ProjectFileLocator.Resolve(basePath, "Ekd5.exe")) == originalHash, "辅助分组 CSV 测试修改了原基底");
    }

    private static bool Equal(IReadOnlyList<IReadOnlyList<int>> left, IReadOnlyList<IReadOnlyList<int>> right)
        => left.Count == right.Count && left.Zip(right).All(pair => pair.First.SequenceEqual(pair.Second));

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
