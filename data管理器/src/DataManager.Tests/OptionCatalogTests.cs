using DataManager.Core.Domain;

namespace DataManager.Tests;

internal static class OptionCatalogTests
{
    public static void Run()
    {
        var service = new OptionCatalogService();
        var knownAttack = new Dictionary<int, string> { [0] = "四格", [10] = "全屏" };
        var attackRequest = new OptionCatalogRequest(
            "ccz-6.5", "65-standard-v1", "6.5", "ekd-hash", "detailed-job-growth", "攻击范围",
            OptionCatalogKind.LegacyDataAttackRange, 0, 255, 255,
            [12, 20, 21, 26, 32], knownAttack, PreviewEntryCount: 33);
        var attack = service.Build(attackRequest);
        var attackIds = attack.Options.Select(option => option.Id).ToHashSet();
        TestSupport.Assert(new[] { 12, 20, 21, 26, 32 }.All(attackIds.Contains),
            "兵种旧攻击字段没有保留当前基底的扩展范围值。");
        TestSupport.Assert(attack.Options.Single(option => option.Id == 32).PreviewAvailable,
            "Hitarea 33 项资源没有把字段值 32 标为可预览。");
        TestSupport.Assert(attack.Options.Single(option => option.Id == 31).Status == OptionStatus.PreviewAvailable,
            "真实资源域候选没有标记为可预览状态。");
        var noImage = attack.Options.Single(option => option.Id == 255);
        TestSupport.Assert(noImage.Status == OptionStatus.NoPreview && !noImage.PreviewAvailable,
            "超出 Hitarea 条目数的当前值没有保留为无图状态。");

        var pierce = service.Build(new OptionCatalogRequest(
            "ccz-6.5", "65-standard-v1", "6.5", "ekd-hash", "strategies", "穿透范围",
            OptionCatalogKind.E5PierceRange, 0, 255, 11, [7],
            new Dictionary<int, string> { [0] = "不穿透" }, PreviewEntryCount: 12));
        TestSupport.Assert(pierce.Options.Any(option => option.Id == 11 && option.PreviewAvailable),
            "Effarea 12 项资源没有把最后一个字段值 11 作为候选。");

        var nativeRequest = new OptionCatalogRequest(
            "ccz-6.5", "65-standard-v1", "6.5", "ekd-hash", "detailed-job-penetration", "穿透",
            OptionCatalogKind.NativeJobShape, 0, 255, 11, [9, 10],
            new Dictionary<int, string> { [0] = "不穿透" }, NativeMaximum: 11);
        var native = service.Build(nativeRequest);
        TestSupport.Assert(Enumerable.Range(0, 12).All(id => native.Options.Any(option => option.Id == id)),
            "Ekd5 6.5 原生穿透契约没有覆盖 0..0x0B。");

        TestSupport.Assert(NativeRangeDomain.PierceMaximum("ccz-6.4") == 0x09,
            "6.4 原生穿透契约上限错误。");
        TestSupport.Assert(NativeRangeDomain.PierceMaximum("ccz-6.5") == 0x0B,
            "6.5 原生穿透契约上限错误。");
        TestSupport.Assert(NativeRangeDomain.PierceMaximum("ccz-6.6") == 0x0A,
            "6.6 原生穿透契约上限错误。");

        var nativeRoot = Path.Combine(Directory.GetCurrentDirectory(), "基底", "加强版6.5未加密版");
        if (File.Exists(Path.Combine(nativeRoot, "Ekd5.exe")))
        {
            var nativeSnapshot = NativeRangeDomainReader.Read(nativeRoot, "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(nativeSnapshot.JobPierceValues.Count > 0 && nativeSnapshot.LogicalAttackIds.Contains(0x25),
                "共享原生范围读取器没有读取 Ekd5 兵种穿透表或 0x4D1400 逻辑域。");
        }

        var outside = service.Build(nativeRequest with { CurrentValue = 12, ActualValues = [12] });
        TestSupport.Assert(outside.Options.Single(option => option.Id == 12).Status == OptionStatus.OutsideNativeContract,
            "超出原生穿透契约的基底值没有保留并标记诊断状态。");

        var unavailable = service.Build(attackRequest with { PreviewEntryCount = null, ResourceAvailable = false });
        TestSupport.Assert(unavailable.Options.All(option => option.Status != OptionStatus.PreviewAvailable) &&
            unavailable.Options.Any(option => option.Status == OptionStatus.ResourceUnavailable),
            "资源不可用时错误地把旧静态候选标成了可预览。");
        TestSupport.Assert(ConsumableEffectCatalog.GetAll([0xFE]).Any(entry => entry.Id == 0xFE && !entry.Confirmed),
            "消耗品特效目录没有保留当前基底中的未知字节值。");
        Console.WriteLine("OPTION_CATALOG_TESTS_OK");
    }
}

