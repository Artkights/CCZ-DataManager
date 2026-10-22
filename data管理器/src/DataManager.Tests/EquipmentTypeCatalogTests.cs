using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class EquipmentTypeCatalogTests
{
    public static void Run(string basePath)
    {
        var repository = TestSupport.RepositoryRootFromBase(basePath);
        foreach (var fixture in new[]
        {
            (Path: basePath, Profile: "ccz-6.5", Variant: "65-standard-v1"),
            (Path: Path.Combine(repository, "基底", "复刻版6.6山贼 国庆"), Profile: "ccz-6.6", Variant: "66-standard-v1")
        }.Where(fixture => Directory.Exists(fixture.Path)))
            RunProject(fixture.Path, fixture.Profile, fixture.Variant);
        TestSupport.Assert(EquipmentTypeCatalogService.BuildTables(new byte[0x90000]).All(table => table.Capability == TableCapability.Unavailable),
            "无 PE/消费者证据的文件不能开放装备类型固定地址写入。");
    }

    private static void RunProject(string basePath, string profile, string variant)
    {
        var root = TestSupport.CreateCoreCopy(basePath, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace(); workspace.Open(root, profile, variant);
            var catalog = new EquipmentTypeCatalogService();
            var names = workspace.GetDocument(EquipmentTypeCatalogService.NamesTableKey);
            var display = workspace.GetDocument(EquipmentTypeCatalogService.DisplayTableKey);
            TestSupport.Assert(names.Definition.IsWriteSupported && display.Definition.IsWriteSupported &&
                names.Data.Rows.Count == 15 && display.Data.Rows.Count == 13, $"{profile} 装备类型表没有按本项目布局开放。");
            var entries = catalog.Read(workspace);
            TestSupport.Assert(entries.Count == 15 && entries.Count(entry => entry.HasDisplayToggle) == 13,
                "辅助/道具不应伪造原生显示开关。");
            var before = File.ReadAllBytes(Path.Combine(root, "Ekd5.exe"));
            var originalName = entries[0].Name;
            var originalDisplay = Convert.ToInt32(display.Data.Rows[0]["显示值"], CultureInfo.InvariantCulture);
            var nextDisplay = EquipmentTypeCatalogService.DisplayValue(0, !entries[0].IsVisible);
            workspace.ApplyCompositeTransaction("测试装备类型草稿",
                [new(names.Definition.Key, 0, "名称", "测器"), new(display.Definition.Key, 0, "显示值", nextDisplay)]);
            TestSupport.Assert(catalog.GetTypeDisplayName(workspace, 0) == "普通测器" && catalog.GetTypeDisplayName(workspace, 1) == "特殊测器",
                "普通/特殊标签未从名称草稿同步读取。");
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(root, "Ekd5.exe")).SequenceEqual(before), "目录编辑在统一保存前写入了磁盘。");
            workspace.Commands.Undo();
            TestSupport.Assert(catalog.Read(workspace)[0].Name == originalName && Convert.ToInt32(display.Data.Rows[0]["显示值"]) == originalDisplay,
                "装备类型名称/开关不能一次撤回。");
            workspace.Commands.Redo();
            foreach (var invalid in new[] { "超出两字", "😀" })
                ExpectFailure(() => workspace.ApplyCompositeTransaction("无效类型名", [new(names.Definition.Key, 1, "名称", invalid)]), "非法 GBK 名称被接受。");
            ExpectFailure(() => workspace.ApplyCompositeTransaction("无效显示值", [new(display.Definition.Key, 1, "显示值", 42)]), "非标准显示值被接受。");
            var save = workspace.Save();
            TestSupport.Assert(save.Saved, string.Join("；", save.Messages));
            var after = File.ReadAllBytes(Path.Combine(root, "Ekd5.exe"));
            var allowed = Enumerable.Range((int)EquipmentTypeCatalogService.NameTableOffset, 4)
                .Append((int)EquipmentTypeCatalogService.DisplayTableOffset).ToHashSet();
            TestSupport.Assert(Enumerable.Range(0, before.Length).Where(index => before[index] != after[index]).All(allowed.Contains),
                "装备类型写回改动了名称前4字节和显示字节以外的位置。");
            TestSupport.Assert(after[(int)EquipmentTypeCatalogService.NameTableOffset + 4] == before[(int)EquipmentTypeCatalogService.NameTableOffset + 4],
                "装备类型第5字节被改写。");
            var reopened = new ProjectWorkspace(); reopened.Open(root, profile, variant);
            TestSupport.Assert(catalog.Read(reopened)[0].Name == "测器" && catalog.Read(reopened)[0].DisplayValue == nextDisplay,
                "保存重开后装备类型目录或布局写入能力丢失。");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ExpectFailure(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
}
