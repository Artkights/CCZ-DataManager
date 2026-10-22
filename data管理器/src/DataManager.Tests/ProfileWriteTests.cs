using System.Data;
using DataManager.Core.Editing;

namespace DataManager.Tests;

internal static class ProfileWriteTests
{
    public static void Run(string basePath)
    {
        RunLegacyItemWrite(basePath);
        var standardPath = Path.Combine(TestSupport.RepositoryRootFromBase(basePath), "基底", "曹操传6.6-V7.16 解压密码：66");
        if (Directory.Exists(standardPath)) RunStandardItemWrite(standardPath);
    }

    private static void RunLegacyItemWrite(string source)
    {
        var temp = TestSupport.CreateCoreCopy(source);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(temp);
            var document = workspace.GetDocument("items-low");
            var before = File.ReadAllBytes(document.FilePath);
            var rowOffset = checked((int)document.Definition.DataOffset);
            var iconBefore = before[rowOffset + 0x14];
            document.Data.Rows[0]["名称"] = "DM65";
            var save = workspace.Save();
            TestSupport.Assert(save.Saved && save.History?.ProfileId == "ccz-6.5", "6.5 物品写入或历史 Profile 失败");
            var after = File.ReadAllBytes(document.FilePath);
            TestSupport.Assert(after[rowOffset + 0x14] == iconBefore, "6.5 物品图标字节被改写");
            TestSupport.Assert(Differences(before, after).All(index => index >= rowOffset && index < rowOffset + 17), "6.5 物品名称修改越过 17 字节范围");

            var effects = workspace.GetDocument("class-effects");
            var effectBefore = File.ReadAllBytes(effects.FilePath);
            var effectOffset = checked((int)effects.Definition.DataOffset + 7);
            var oldValue = Convert.ToInt32(effects.Data.Rows[0]["特效值"]);
            effects.Data.Rows[0]["特效值"] = oldValue == 255 ? 254 : oldValue + 1;
            TestSupport.Assert(workspace.Save().Saved, "6.5 8 字节特效分配写入失败");
            var effectAfter = File.ReadAllBytes(effects.FilePath);
            TestSupport.Assert(Differences(effectBefore, effectAfter).SequenceEqual([effectOffset]), "6.5 特效值没有只写 row+7");
            RunJobSentinelWrite(workspace, "兵种", 6);
            RunAnimationWrite(workspace);
            RunIconWriteAndCsv(workspace, 0x14);
        }
        finally { Directory.Delete(temp, true); }
    }

    private static void RunStandardItemWrite(string source)
    {
        var temp = TestSupport.CreateCoreCopy(source);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(temp);
            var document = workspace.GetDocument("items-low");
            var definition = document.Definition;
            var before = File.ReadAllBytes(document.FilePath);
            var normalRow = Enumerable.Range(0, definition.RowCount).First(index => before[definition.DataOffset + index * definition.RowSize + 0x12] is not (2 or 3));
            var rowOffset = checked((int)(definition.DataOffset + normalRow * definition.RowSize));
            var oldEffect = Convert.ToInt32(document.Data.Rows[normalRow]["装备特效号"]);
            var newEffect = Enumerable.Range(0, 256).First(value => value != oldEffect && value is not (2 or 3));
            document.Data.Rows[normalRow]["名称"] = "DM66";
            document.Data.Rows[normalRow]["装备特效号"] = newEffect;
            var save = workspace.Save();
            TestSupport.Assert(save.Saved && save.History?.VariantId == "66-standard-v1", "标准 6.6 物品写入失败");
            var after = File.ReadAllBytes(document.FilePath);
            TestSupport.Assert(after[rowOffset + 0x0F] == before[rowOffset + 0x0F], "6.6 物品图标字节被改写");
            TestSupport.Assert(after[rowOffset + 0x10] == before[rowOffset + 0x10] && after[rowOffset + 0x14] == before[rowOffset + 0x14], "6.6 物品保留字节被改写");
            TestSupport.Assert(after[rowOffset + 0x12] == newEffect, "6.6 普通装备特效没有写入 row+0x12");
            TestSupport.Assert(Differences(before, after).All(index => (index >= rowOffset && index < rowOffset + 15) || index == rowOffset + 0x12), "6.6 物品修改写入了未授权字节");

            var markerDocument = workspace.GetDocument("items-high");
            var markerDefinition = markerDocument.Definition;
            var markerBefore = File.ReadAllBytes(markerDocument.FilePath);
            var markerRow = Enumerable.Range(0, markerDefinition.RowCount).First(index => markerBefore[markerDefinition.DataOffset + index * markerDefinition.RowSize + 0x12] is 2 or 3);
            var markerOffset = checked((int)(markerDefinition.DataOffset + markerRow * markerDefinition.RowSize));
            var oldMarkerEffect = Convert.ToInt32(markerDocument.Data.Rows[markerRow]["装备特效号"]);
            markerDocument.Data.Rows[markerRow]["装备特效号"] = oldMarkerEffect == 255 ? 254 : oldMarkerEffect + 1;
            var markerSave = workspace.Save();
            TestSupport.Assert(markerSave.Saved, "6.6 辅助/道具特效写入失败");
            var markerAfter = File.ReadAllBytes(markerDocument.FilePath);
            TestSupport.Assert(markerAfter[markerOffset + 0x11] != markerBefore[markerOffset + 0x11], "6.6 辅助/道具特效没有写入 row+0x11");
            TestSupport.Assert(markerAfter[markerOffset + 0x12] == markerBefore[markerOffset + 0x12], "6.6 辅助/道具类别标记被意外修改");

            var effects = workspace.GetDocument("class-effects");
            var effectBefore = File.ReadAllBytes(effects.FilePath);
            var effectOffset = checked((int)effects.Definition.DataOffset);
            var oldToken = Convert.ToInt32(effects.Data.Rows[0]["武将1"]);
            var newToken = oldToken == 65535 ? 1025 : 65535;
            effects.Data.Rows[0]["武将1"] = newToken;
            TestSupport.Assert(workspace.Save().Saved, "6.6 16 字节特效分配写入失败");
            var effectAfter = File.ReadAllBytes(effects.FilePath);
            TestSupport.Assert(BitConverter.ToUInt16(effectAfter, effectOffset) == newToken, "6.6 UInt16 人物 token 写入错误");
            TestSupport.Assert(Differences(effectBefore, effectAfter).All(index => index >= effectOffset && index < effectOffset + 2), "6.6 人物 token 写入越过 row+0..1");

            RunJobSentinelWrite(workspace, "兵种1", 12);
            RunAnimationWrite(workspace);
            RunIconWriteAndCsv(workspace, 0x0F);

            workspace.Open(temp, "ccz-6.6", "66-legacy-data-v1");
            var rejected = workspace.UndoSaved();
            TestSupport.Assert(!rejected.Success && rejected.Message.Contains("布局冲突", StringComparison.Ordinal), "跨布局历史重放必须拒绝");
        }
        finally { Directory.Delete(temp, true); }
    }

    private static void RunAnimationWrite(ProjectWorkspace workspace)
    {
        var small = workspace.GetDocument("strategy-small-animation");
        var big = workspace.GetDocument("strategy-big-animation");
        var before = File.ReadAllBytes(small.FilePath);
        foreach (var document in new[] { small, big })
        {
            for (var index = 0; index < 144; index++)
                TestSupport.Assert(Convert.ToInt32(document.Data.Rows[index]["动画ID"]) == before[document.Definition.DataOffset + index],
                    "策略动画单字节索引与原始 EXE 不一致");
            foreach (var row in new[] { 0, 143 })
                document.Data.Rows[row]["动画ID"] = (before[document.Definition.DataOffset + row] + 1) % 256;
        }
        TestSupport.Assert(workspace.Save().Saved, "策略动画边界行写入失败");
        var after = File.ReadAllBytes(small.FilePath);
        var allowed = new[] { (int)small.Definition.DataOffset, (int)small.Definition.DataOffset + 143, (int)big.Definition.DataOffset, (int)big.Definition.DataOffset + 143 };
        TestSupport.Assert(Differences(before, after).SequenceEqual(allowed), "策略动画写回覆盖了相邻表或其他行");
        var profile = workspace.ActiveProfile;
        workspace.Open(workspace.RootPath, profile.ProfileId, profile.VariantId);
        foreach (var key in new[] { "strategy-small-animation", "strategy-big-animation" })
        {
            var document = workspace.GetDocument(key);
            foreach (var row in new[] { 0, 143 })
                TestSupport.Assert(Convert.ToInt32(document.Data.Rows[row]["动画ID"]) == after[document.Definition.DataOffset + row], "策略动画保存后复读不一致");
        }
    }

    private static void RunIconWriteAndCsv(ProjectWorkspace workspace, int iconOffset)
    {
        foreach (var key in new[] { "items-low", "items-high" })
        {
            var document = workspace.GetDocument(key);
            var before = File.ReadAllBytes(document.FilePath);
            var rows = new[] { 0, document.Data.Rows.Count - 1 };
            var expected = rows.ToDictionary(row => row, row => (Convert.ToInt32(document.Data.Rows[row]["图标"]) + 1) % 256);
            foreach (var row in rows) document.Data.Rows[row]["图标"] = expected[row];
            TestSupport.Assert(workspace.Save().Saved, $"{key} 图标数值字段不能保存");
            var after = File.ReadAllBytes(document.FilePath);
            var addresses = rows.Select(row => checked((int)(document.Definition.DataOffset + row * document.Definition.RowSize + iconOffset))).ToArray();
            TestSupport.Assert(Differences(before, after).SequenceEqual(addresses), $"{key} 图标修改越过两个目标字节");
            foreach (var row in rows)
                TestSupport.Assert(after[addresses[Array.IndexOf(rows, row)]] == expected[row], "图标逻辑编号不应在写入时加一");
            var csv = Path.Combine(workspace.RootPath, key + "-图标往返.csv");
            workspace.ExportCsv(document, csv);
            TestSupport.Assert(workspace.UndoSaved().Success, "图标保存事务不能撤回");
            document = workspace.GetDocument(key);
            TestSupport.Assert(workspace.ImportCsv(document, csv).ChangedCells == rows.Length, "导出的图标CSV不能恢复原始逻辑编号");
            TestSupport.Assert(workspace.Save().Saved && File.ReadAllBytes(document.FilePath).SequenceEqual(after), "图标CSV回导写入与首次保存不一致");
        }
    }

    private static void RunJobSentinelWrite(ProjectWorkspace workspace, string fieldName, int fieldOffset)
    {
        var document = workspace.GetDocument("class-effects");
        var row = Enumerable.Range(0, document.Data.Rows.Count).First(index => Convert.ToInt32(document.Data.Rows[index][fieldName]) != 255);
        var before = File.ReadAllBytes(document.FilePath);
        var absolute = checked((int)(document.Definition.DataOffset + row * document.Definition.RowSize + fieldOffset));
        document.Data.Rows[row][fieldName] = 255;
        TestSupport.Assert(workspace.Save().Saved, "兵种特效的 255 空/不限哨兵必须能够写入");
        var after = File.ReadAllBytes(document.FilePath);
        TestSupport.Assert(after[absolute] == 255 && Differences(before, after).SequenceEqual([absolute]), "兵种特效哨兵写入越过目标字节");
    }

    private static IEnumerable<int> Differences(byte[] before, byte[] after)
    {
        for (var index = 0; index < Math.Min(before.Length, after.Length); index++)
        {
            if (before[index] != after[index]) yield return index;
        }
    }
}
