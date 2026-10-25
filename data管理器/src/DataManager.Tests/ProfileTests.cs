using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class ProfileTests
{
    public static void Run()
    {
        var registry = new ProfileRegistry();
        TestSupport.Assert(registry.Profiles.Count == 3, "必须公开 6.4/6.5/6.6 三个 Profile");
        TestSupport.Assert(registry.Profiles.Select(profile => profile.Version).SequenceEqual(["6.4", "6.5", "6.6"]), "Profile 显示顺序或版本错误");

        var profile65 = registry.Resolve("ccz-6.5", "65-standard-v1");
        var standard66 = registry.Resolve("ccz-6.6", "66-standard-v1");
        var legacy66 = registry.Resolve("ccz-6.6", "66-legacy-data-v1");
        TestSupport.Assert(profile65.Tables.Count >= 40 && standard66.Tables.Count >= 40 && legacy66.Tables.Count >= 40, "Profile 表集合不完整");
        TestSupport.Assert(profile65.Tables.Any(table => table.Key == "person-critical-quotes") && profile65.Tables.Any(table => table.Key == "shops"), "新增人物台词/商店表缺失");
        foreach (var key in new[] { "item-acquisition-low", "item-acquisition-high" })
        {
            var table = profile65.Tables.Single(item => item.Key == key);
            TestSupport.Assert(table.Capability == Core.Models.TableCapability.Unavailable &&
                table.VerificationLevel == Core.Models.VerificationLevel.InheritedUnverified &&
                table.CapabilityEvidence.Contains("Enable=false", StringComparison.Ordinal),
                "旧 HexTable.xml 已禁用的物品获取说明不能凭文件长度或继承布局开放");
            TestSupport.Assert(standard66.Tables.Single(item => item.Key == key).Capability == Core.Models.TableCapability.Unavailable,
                "6.6 不得继承未验证的物品获取说明区域");
        }
        TestSupport.Assert(profile65.Tables.Single(table => table.Key == "items-low").RecordCodecKey == "item-65-17-v1", "6.5 物品 codec 错误");
        TestSupport.Assert(standard66.Tables.Single(table => table.Key == "items-low").RecordCodecKey == "item-66-15-v1", "标准 6.6 物品 codec 错误");
        TestSupport.Assert(legacy66.Tables.Single(table => table.Key == "items-low").Fields.Single(field => field.Name == "名称").Size == 17, "兼容 6.6 应保留 17 字节物品名");
        var terrain65 = profile65.Tables.Single(table => table.Key == "strategy-terrain-availability");
        TestSupport.Assert(terrain65.DataOffset == 0x1FECC && terrain65.RowCount == 30 && terrain65.RowSize == 1 && terrain65.IsWriteSupported,
            "6.5 策略施展地形必须使用人工确认的 30 项可写表");
        var terrain66 = standard66.Tables.Single(table => table.Key == "strategy-terrain-availability");
        TestSupport.Assert(terrain66.Capability == Core.Models.TableCapability.Unavailable && !terrain66.IsWriteSupported,
            "6.6 策略施展地形在缺证时必须标记不可用，禁止读取继承偏移");
        foreach (var profile in new[] { profile65, standard66, legacy66, registry.Resolve("ccz-6.6", "66-shanzhai-legacy") })
        {
            var smallAnimation = profile.Tables.Single(table => table.Key == "strategy-small-animation");
            var bigAnimation = profile.Tables.Single(table => table.Key == "strategy-big-animation");
            TestSupport.Assert(smallAnimation.RowCount == 144 && smallAnimation.RowSize == 1 && bigAnimation.RowCount == 144 && bigAnimation.RowSize == 1 &&
                smallAnimation.EndOffsetExclusive == bigAnimation.DataOffset && smallAnimation.Fields[0].Kind == Core.Models.FieldKind.UInt8 &&
                bigAnimation.Fields[0].Kind == Core.Models.FieldKind.UInt8,
                "策略小/大动画必须是相邻但不重叠的 144 项单字节表");
        }
        foreach (var variant in new[] { standard66, legacy66, registry.Resolve("ccz-6.6", "66-shanzhai-legacy") })
        {
            foreach (var key in new[] { "person-r-assign", "person-s-assign" })
                TestSupport.Assert(variant.Tables.Single(table => table.Key == key).Capability == Core.Models.TableCapability.Unavailable,
                    "6.6 R/S 缺证时不得读取继承偏移");
            foreach (var key in new[] { "item-effect-names-low", "item-effect-names-high" })
                TestSupport.Assert(variant.Tables.Single(table => table.Key == key).Capability == Core.Models.TableCapability.Unavailable,
                    "6.6 不得读取继承的 6.5 变长名称池偏移");
        }
        TestSupport.Assert(profile65.LayoutRevision != standard66.LayoutRevision && standard66.LayoutRevision != legacy66.LayoutRevision, "不同布局必须具有不同 LayoutRevision");
    }
}
