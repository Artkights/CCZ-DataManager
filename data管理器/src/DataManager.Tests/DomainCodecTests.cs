using DataManager.Core.Domain;
using DataManager.Core.Editing;

namespace DataManager.Tests;

internal static class DomainCodecTests
{
    public static void Run(string basePath)
    {
        var bytes = AccessoryJobGroupService.Serialize([[0, 3, 39], [1, 2]]);
        TestSupport.Assert(bytes.SequenceEqual(new byte[] { 0xFF, 0, 3, 39, 0xFF, 1, 2, 0xFF }), "辅助装备分组编码错误");
        var parsed = AccessoryJobGroupService.Parse(bytes);
        TestSupport.Assert(parsed.Count == 2 && parsed[0].JobSeriesIds.SequenceEqual([0, 3, 39]), "辅助装备分组解码错误");
        var rejected = false;
        try { _ = AccessoryJobGroupService.Serialize([[1, 1]]); }
        catch (InvalidDataException) { rejected = true; }
        TestSupport.Assert(rejected, "辅助装备分组重复 ID 未拒绝");

        var copy = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var names = new EffectNamePoolService().Read(workspace, EffectNameChannel.PersonalJob);
            TestSupport.Assert(names.Count == 255 && names.All(item => item.OriginalBytes.Length == 16), "人物/兵种特效名称池读取不完整");
            var items = new EffectNamePoolService().Read(workspace, EffectNameChannel.Item);
            TestSupport.Assert(items.Count > 0 && items.All(item => !item.IsWritable), "6.5 宝物特效变长名称池必须只读解析");
            TestSupport.Assert(items.Single(item => item.EffectId == 0x1B).Name == "每回合恢复MP", "6.5 名称池零填充被错误当作特效 ID 槽");
        }
        finally { Directory.Delete(copy, true); }
    }
}
