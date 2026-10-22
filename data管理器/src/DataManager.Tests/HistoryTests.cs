using System.Globalization;
using DataManager.Core.Editing;

namespace DataManager.Tests;

internal static class HistoryTests
{
    public static void Run(string basePath)
    {
        var temp = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(temp);
            var person = workspace.GetDocument("persons");
            var biography = workspace.GetDocument("person-biography");
            var dataBefore = File.ReadAllBytes(person.FilePath);
            var imsgBefore = File.ReadAllBytes(biography.FilePath);
            var ability = Convert.ToInt32(person.Data.Rows[0]["武力"], CultureInfo.InvariantCulture);
            person.Data.Rows[0]["武力"] = ability == 255 ? 254 : ability + 1;
            biography.Data.Rows[0]["人物列传"] = "历史 2.0 烟测";
            var save = workspace.Save();
            TestSupport.Assert(save.Saved && save.History is { SchemaVersion: "2.0", ProfileId: "ccz-6.5" }, "历史 2.0 身份没有登记");
            TestSupport.Assert(save.History!.Files.Count >= 2 && save.History.LayoutRevision == workspace.ActiveProfile.LayoutRevision, "多文件历史或布局修订缺失");
            var dataAfter = File.ReadAllBytes(person.FilePath);
            var imsgAfter = File.ReadAllBytes(biography.FilePath);
            TestSupport.Assert(dataAfter[396 + 27] == dataBefore[396 + 27] && dataAfter[396 + 28] == dataBefore[396 + 28], "人物级别/经验字节被改写");
            TestSupport.Assert(workspace.UndoSaved().Success, "同布局历史撤回失败");
            TestSupport.Assert(File.ReadAllBytes(person.FilePath).SequenceEqual(dataBefore) && File.ReadAllBytes(biography.FilePath).SequenceEqual(imsgBefore), "历史撤回没有整体恢复文件");
            TestSupport.Assert(workspace.RedoSaved().Success, "同布局历史前进失败");
            TestSupport.Assert(File.ReadAllBytes(person.FilePath).SequenceEqual(dataAfter) && File.ReadAllBytes(biography.FilePath).SequenceEqual(imsgAfter), "历史前进没有整体重放文件");

            var strategy = workspace.GetDocument("strategies");
            var iconOffset = checked((int)strategy.Definition.DataOffset + 16);
            var iconBefore = File.ReadAllBytes(strategy.FilePath)[iconOffset];
            var type = Convert.ToInt32(strategy.Data.Rows[0]["策略类型"], CultureInfo.InvariantCulture);
            strategy.Data.Rows[0]["策略类型"] = type == 255 ? 254 : type + 1;
            TestSupport.Assert(workspace.Save().Saved && File.ReadAllBytes(strategy.FilePath)[iconOffset] == iconBefore, "策略资源原始字节被改写");
            TestSupport.Assert(!Directory.EnumerateFiles(temp, "*.bak", SearchOption.AllDirectories).Any(), "工具生成了额外备份文件");
        }
        finally { Directory.Delete(temp, true); }
    }
}
