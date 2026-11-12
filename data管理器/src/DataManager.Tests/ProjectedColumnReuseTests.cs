using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.History;

namespace DataManager.Tests;

internal static class ProjectedColumnReuseTests
{
    public static void Run(string source)
    {
        using var owned = OwnedTemporaryWorkspace.Create(Path.GetTempPath(), "DataManager.ProjectedColumnReuseTests", Path.GetFullPath(source));
        using var history = ProjectHistoryPaths.UseBaseDirectory(Path.Combine(owned.RootPath, "history"));
        foreach (var name in Known65FixtureSnapshot.CoreNames)
            File.Copy(Path.Combine(source, name), Path.Combine(owned.RootPath, name));
        using var form = new MainForm(false); _ = form.Handle;
        form.LoadProjectForSmokeTest(owned.RootPath);
        form.SelectModuleForSmokeTest("人物"); form.ShowPhysicalTableForSmokeTest("persons");
        var grid = form.PhysicalGridForSmokeTest;
        var r = grid.Columns["R形象编号"]; var s = grid.Columns["S形象编号"];
        TestSupport.Assert(r != null && s != null, "投影列未创建");
        for (var cycle = 0; cycle < 30; cycle++)
        {
            form.ShowPhysicalTableForSmokeTest("__business"); form.ShowPhysicalTableForSmokeTest("persons");
            TestSupport.Assert(ReferenceEquals(r, grid.Columns["R形象编号"]) && ReferenceEquals(s, grid.Columns["S形象编号"]),
                "同物理表返回时重建了投影列");
        }
        var workspace = form.WorkspaceForSmokeTest;
        var document = workspace.GetDocument("person-r-assign");
        var before = Convert.ToInt32(document.Data.Rows[0]["R形象ID"]);
        var changed = (before + 1) % 256;
        workspace.ApplyCompositeTransaction("投影复用回归", [new("person-r-assign", 0, "R形象ID", changed)]);
        TestSupport.Assert(Convert.ToInt32(grid.Rows[0].Cells[r!.Index].Value) == changed, "复用投影没有读取当前草稿");
        workspace.Commands.Undo();
        TestSupport.Assert(Convert.ToInt32(grid.Rows[0].Cells[r.Index].Value) == before, "复用投影撤回后显示过时");
        TestSupport.Assert(!workspace.IsDirty, "只读切页留下草稿");
        owned.Complete();
        Console.WriteLine("PROJECTED_COLUMN_REUSE_OK returns=30 same-columns live-draft undo clean-state");
    }
}
