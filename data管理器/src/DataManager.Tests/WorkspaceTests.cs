using System.Globalization;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class WorkspaceTests
{
    public static void Run(string basePath)
    {
        var temp = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace();
            var report = workspace.Open(temp);
            TestSupport.Assert(report.AllowsEditing && workspace.Documents.ContainsKey("persons") && workspace.Documents.ContainsKey("items-low"), "6.5 工作区没有加载核心表");
            var person = workspace.GetDocument("persons");
            TestSupport.Assert(person.Data.Columns.Contains("兵种") && !person.Data.Columns.Contains("职业"), "人物必须显示兵种且不能显示职业");
            TestSupport.Assert(!person.Data.Columns.Contains("级别") && !person.Data.Columns.Contains("经验"), "人物隐藏字段进入了界面数据源");
            TestSupport.Assert(person.Data.Columns.Contains("头像") && person.Definition.Fields.Single(field => field.Name == "头像").Kind == FieldKind.UInt16,
                "头像必须作为可写 UInt16 逻辑编号供下拉和迁移使用");
            TestSupport.Assert(!person.Data.Columns.Contains("个人/专属特效关联"), "人物不应再注入个人/专属特效派生列");
            var items = workspace.GetDocument("items-low");
            TestSupport.Assert(items.Data.Columns["物品分类"]!.ReadOnly && items.Data.Columns["可装备部队"]!.ReadOnly, "宝物派生列必须只读");
            TestSupport.Assert(workspace.GetDocument("detailed-job-growth").Data.Columns.Contains("名称"), "兵种子表缺少关联名称");

            var before = new[]
            {
                Convert.ToInt32(person.Data.Rows[0]["武力"], CultureInfo.InvariantCulture),
                Convert.ToInt32(person.Data.Rows[1]["统帅"], CultureInfo.InvariantCulture)
            };
            workspace.ApplyBatchMultiple(person, [0, 1], ["武力", "统帅"], "统一赋值", "88");
            TestSupport.Assert(Convert.ToInt32(person.Data.Rows[0]["武力"]) == 88 && Convert.ToInt32(person.Data.Rows[1]["统帅"]) == 88, "多行多字段批量修改失败");
            TestSupport.Assert(workspace.Commands.Undo() != null, "批量修改没有形成单条草稿命令");
            TestSupport.Assert(Convert.ToInt32(person.Data.Rows[0]["武力"]) == before[0] && Convert.ToInt32(person.Data.Rows[1]["统帅"]) == before[1], "批量撤回失败");
            var rejected = false;
            try { workspace.ApplyBatch(person, [0], "武力", "统一赋值", "300"); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            TestSupport.Assert(rejected, "批量修改必须拒绝越界值");

            var original = Convert.ToString(person.Data.Rows[0]["名称"], CultureInfo.InvariantCulture) ?? string.Empty;
            person.Data.Rows[0]["名称"] = original + "查找值";
            var replaced = workspace.ApplyFindReplace(person, [0], "名称", "查找值", "替换值");
            TestSupport.Assert(replaced == 1 && workspace.Commands.Undo() != null, "查找替换或撤回失败");
        }
        finally { Directory.Delete(temp, true); }
        PortraitRoundTrip(basePath);
    }

    private static void PortraitRoundTrip(string basePath)
    {
        var copy = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace(); workspace.Open(copy);
            var document = workspace.GetDocument("persons");
            var before = File.ReadAllBytes(document.FilePath);
            // A value over 255 proves that this is the complete two-byte ID,
            // rather than an icon byte or a partially preserved field.
            workspace.ApplyCompositeTransaction("头像编号回归", [new("persons", 17, "头像", 1000)]);
            TestSupport.Assert(workspace.Save().Saved, "头像编号保存失败");
            workspace.Open(copy);
            TestSupport.Assert(Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[17]["头像"]) == 1000, "头像UInt16写后复读不一致");
            var after = File.ReadAllBytes(document.FilePath);
            var offset = checked((int)document.Definition.DataOffset + 17 * document.Definition.RowSize + 13);
            TestSupport.Assert(after[offset] == 0xE8 && after[offset + 1] == 0x03 &&
                before.AsSpan(0, offset).SequenceEqual(after.AsSpan(0, offset)) &&
                before.AsSpan(offset + 2).SequenceEqual(after.AsSpan(offset + 2)), "头像写入影响了相邻台词或其他人物字节");
            TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(document.FilePath).SequenceEqual(before), "头像编号保存不能逐字节撤回");
        }
        finally { Directory.Delete(copy, true); }
    }
}
