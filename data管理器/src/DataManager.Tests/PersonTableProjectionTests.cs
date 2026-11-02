using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Reflection;
using DataManager.App;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class PersonTableProjectionTests
{
    public static void Run(string source)
    {
        var root = TestSupport.CreateCoreCopy(source);
        using var form = new MainForm(false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Size = new Size(1500, 900)
        };
        try
        {
            form.Show(); form.LoadProjectForSmokeTest(root); form.ShowPhysicalTableForSmokeTest("persons"); Application.DoEvents();
            var grid = form.PhysicalGridForSmokeTest;
            var workspace = form.WorkspaceForSmokeTest;
            var persons = workspace.GetDocument("persons");
            var r = workspace.GetDocument("person-r-assign");
            var s = workspace.GetDocument("person-s-assign");
            TestSupport.Assert(ReferenceEquals(grid.DataSource, persons.Data) && !persons.Data.Columns.Contains("R形象编号"), "人物展示列污染物理数据集");
            TestSupport.Assert(!grid.Columns["Army"].Visible && grid.Columns["R形象编号"].DisplayIndex == grid.Columns["头像"].DisplayIndex + 1 &&
                grid.Columns["S形象编号"].DisplayIndex == grid.Columns["R形象编号"].DisplayIndex + 1, "人物表Army或R/S列位置不正确");
            var selector = (ComboBox)typeof(MainForm).GetField("_tableSelector", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            TestSupport.Assert(selector.Items.Cast<object>().All(item => !item.ToString()!.Contains("人物R形象指定", StringComparison.Ordinal) &&
                !item.ToString()!.Contains("人物S形象指定", StringComparison.Ordinal)), "人物R/S独立入口没有合并");

            var beforeR = Convert.ToInt32(r.Data.Rows[900]["R形象ID"], CultureInfo.InvariantCulture);
            var beforeR0 = r.Data.Rows[0]["R形象ID"];
            var beforeS = Convert.ToInt32(s.Data.Rows[900]["S形象ID"], CultureInfo.InvariantCulture);
            persons.Data.DefaultView.RowFilter = "ID=900 OR ID=1";
            grid.Sort(grid.Columns["ID"], ListSortDirection.Descending); Application.DoEvents();
            var row = grid.Rows.Cast<DataGridViewRow>().Single(candidate => Convert.ToInt32(((DataRowView)candidate.DataBoundItem!)["ID"]) == 900);
            var newR = beforeR == 65535 ? 65534 : beforeR + 1;
            row.Cells["R形象编号"].Value = newR;
            TestSupport.Assert(Convert.ToInt32(r.Data.Rows[900]["R形象ID"]) == newR && Equals(r.Data.Rows[0]["R形象ID"], beforeR0), "筛选/排序后R形象写入了错误人物");
            form.UndoDraftForSmokeTest();
            TestSupport.Assert(Convert.ToInt32(r.Data.Rows[900]["R形象ID"]) == beforeR, "合并列修改不能撤回");

            persons.Data.DefaultView.RowFilter = string.Empty; grid.Sort(grid.Columns["ID"], ListSortDirection.Ascending); Application.DoEvents();
            row = grid.Rows.Cast<DataGridViewRow>().Single(candidate => Convert.ToInt32(((DataRowView)candidate.DataBoundItem!)["ID"]) == 900);
            grid.CurrentCell = row.Cells["S形象编号"];
            TestSupport.Assert(grid.BeginEdit(false), "不能编辑合并后的S形象列");
            ((TextBox)grid.EditingControl!).Text = (beforeS == 65535 ? 65534 : beforeS + 1).ToString(CultureInfo.InvariantCulture);
            TestSupport.Assert(grid.EndEdit() && Convert.ToInt32(s.Data.Rows[900]["S形象ID"]) != beforeS, "原生表格编辑未提交到S源表");
            form.UndoDraftForSmokeTest();
            TestSupport.Assert(Convert.ToInt32(s.Data.Rows[900]["S形象ID"]) == beforeS && !workspace.IsDirty, "R/S撤回后仍有意外草稿");

            CheckBatchMapping(form, 900, beforeR, beforeS);
            CheckContentWidths(form);
            CheckUnavailableAssignments(form);
            Console.WriteLine("PERSON_TABLE_PROJECTION_TESTS_OK mapped-edit/sort/filter/undo/batch/content-width/fill/manual-width");
        }
        finally
        {
            while (form.WorkspaceForSmokeTest.Commands.CanUndo) form.WorkspaceForSmokeTest.Commands.Undo();
            form.BusinessEditorForSmokeTest.DiscardPendingChanges(); form.Dispose();
            Directory.Delete(root, true);
        }
    }

    private static void CheckBatchMapping(MainForm form, int id, int beforeR, int beforeS)
    {
        var workspace = form.WorkspaceForSmokeTest;
        var projected = (TableDocument)typeof(MainForm).GetMethod("PersonBatchView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, [workspace.GetDocument("persons")])!;
        var index = projected.Data.Rows.Cast<DataRow>().Select((row, ordinal) => (row, ordinal)).Single(pair => Convert.ToInt32(pair.row["ID"]) == id).ordinal;
        typeof(MainForm).GetMethod("ApplyPersonBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form,
            [projected, new[] { index }, new[] { "R形象编号", "S形象编号" }, "统一赋值", "42"]);
        TestSupport.Assert(Convert.ToInt32(workspace.GetDocument("person-r-assign").Data.Rows[id]["R形象ID"]) == 42 &&
            Convert.ToInt32(workspace.GetDocument("person-s-assign").Data.Rows[id]["S形象ID"]) == 42, "批量编辑没有写入两个真实源表");
        form.UndoDraftForSmokeTest();
        TestSupport.Assert(Convert.ToInt32(workspace.GetDocument("person-r-assign").Data.Rows[id]["R形象ID"]) == beforeR &&
            Convert.ToInt32(workspace.GetDocument("person-s-assign").Data.Rows[id]["S形象ID"]) == beforeS && !workspace.IsDirty, "批量R/S未用一次事务撤回");
    }

    private static void CheckContentWidths(MainForm form)
    {
        var workspace = form.WorkspaceForSmokeTest;
        var persons = workspace.GetDocument("persons");
        var jobs = workspace.GetDocument("detailed-jobs");
        var oldName = persons.Data.Rows[700]["名称"];
        var oldJobName = jobs.Data.Rows[0]["名称"];
        var longName = "第七百行的完整名称用于检验超出前二十四行的测量";
        var longJobName = "完整显示的超长兵种名称用于检验引用值而非数字";
        try
        {
            persons.Data.Rows[700]["名称"] = longName;
            jobs.Data.Rows[0]["名称"] = longJobName;
            form.ShowPhysicalTableForSmokeTest("persons"); Application.DoEvents();
            var grid = form.PhysicalGridForSmokeTest;
            TestSupport.Assert(grid.Columns["名称"].Width >= TextRenderer.MeasureText(longName, grid.Font).Width,
                "表格测宽遗漏第24行以后的完整名称");
            TestSupport.Assert(grid.Columns["兵种"].Width >= TextRenderer.MeasureText("0：" + longJobName, grid.Font).Width,
                "兵种列仍按原始数字而非引用显示名称测宽");
            grid.Columns["武力"].Width = 219;
            form.SelectModuleForSmokeTest("策略"); form.SelectModuleForSmokeTest("人物");
            TestSupport.Assert(grid.Columns["武力"].Width == 219, "人物合并表切页丢失手动列宽");
            form.ShowPhysicalTableForSmokeTest("person-biography"); Application.DoEvents();
            var usable = grid.ClientSize.Width - grid.RowHeadersWidth - SystemInformation.VerticalScrollBarWidth - 3;
            TestSupport.Assert(grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).Sum(column => column.Width) >= usable - 1,
                "窄表没有利用剩余可用宽度");
        }
        finally { persons.Data.Rows[700]["名称"] = oldName; jobs.Data.Rows[0]["名称"] = oldJobName; }
    }

    private static void CheckUnavailableAssignments(MainForm form)
    {
        var documents = (IDictionary<string, TableDocument>)form.WorkspaceForSmokeTest.Documents;
        var original = documents["person-r-assign"];
        try
        {
            documents.Remove("person-r-assign");
            form.ShowPhysicalTableForSmokeTest("persons"); Application.DoEvents();
            var grid = form.PhysicalGridForSmokeTest;
            TestSupport.Assert(grid.Columns["R形象编号"].ReadOnly && grid.Rows[0].Cells["R形象编号"].Value is DBNull,
                "缺少R指定时仍允许编辑或伪造零值");
            documents["person-r-assign"] = new TableDocument
            {
                Definition = original.Definition with { Capability = TableCapability.ReadOnly }, Data = original.Data,
                FilePath = original.FilePath, OriginalFileBytes = original.OriginalFileBytes, OriginalFileHash = original.OriginalFileHash
            };
            form.ShowPhysicalTableForSmokeTest("persons"); Application.DoEvents();
            TestSupport.Assert(grid.Columns["R形象编号"].ReadOnly, "只读R指定被合并列开放写入");
        }
        finally { documents["person-r-assign"] = original; }
    }
}
