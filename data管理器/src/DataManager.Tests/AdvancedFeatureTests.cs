using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Migration;

namespace DataManager.Tests;

internal static class AdvancedFeatureTests
{
    public static void Run(string basePath)
    {
        TestIdSearch();
        TestCompositePageAndMigration(basePath);
        TestMigrationRejectsStalePlan(basePath);
        MigrationResourceTests.Run(basePath);
        WorkspaceBinaryTransactionTests.Run(basePath);
        ClassEffectMigrationTests.Run(basePath);
        MigrationIsolationTests.Run(basePath);
        MigrationSelectionContractTests.Run(basePath);
    }

    private static void TestIdSearch()
    {
        var options = new[] { new IdNameOption(12, "甲"), new IdNameOption(120, "乙"), new IdNameOption(312, "丙") };
        var filtered = IdNameSearch.Filter(options, "12", includeUnknown: false);
        TestSupport.Assert(filtered.Select(item => item.Id).SequenceEqual([12, 120, 312]), "ID 包含搜索规则错误");
        var unknown = IdNameSearch.Filter(options, "999", includeUnknown: true);
        TestSupport.Assert(unknown.Count == 1 && unknown[0].Id == 999, "未知数字 ID 未保留");
    }

    private static void TestCompositePageAndMigration(string basePath)
    {
        var sourceRoot = TestSupport.CreateCoreCopy(basePath);
        var targetRoot = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var sourceWorkspace = new ProjectWorkspace();
            sourceWorkspace.Open(sourceRoot);
            var person = sourceWorkspace.GetDocument("persons");
            var original = Convert.ToInt32(person.Data.Rows[0]["武力"], CultureInfo.InvariantCulture);
            var pages = new BusinessDataService(sourceWorkspace);
            TestSupport.Assert(pages.GetAvailableTableKeys(BusinessPageKind.JobSeries).Contains("job-series-accessory-groups"), "兵种系页未暴露辅助装备分组表");
            TestSupport.Assert(pages.GetAvailableTableKeys(BusinessPageKind.Shop).Contains("shops"), "商店页未暴露 shops 表");
            TestSupport.Assert(pages.GetAvailableTableKeys(BusinessPageKind.Strategies).Contains("strategy-terrain-availability"), "策略页未暴露策略施展地形表");
            TestSupport.Assert(pages.LoadDetailedJobs().Records.All(record => record.PhysicalFields.All(field => field.TableKey != "strategies")),
                "兵种页不得按同 ID 误关联策略实体，学习等级应由兵种列反转解析");
            TestSupport.Assert(pages.LoadItems().Records.All(record => record.PhysicalFields.All(field => field.TableKey != "detailed-job-growth")),
                "宝物页不得按同 ID 误关联兵种实体，装备许可应由装备类型解析");
            var tx = pages.BeginTransaction(BusinessPageKind.Persons, "测试人物事务");
            tx.SetById("persons", 0, "武力", original + 1).Apply();
            TestSupport.Assert(Convert.ToInt32(person.Data.Rows[0]["武力"], CultureInfo.InvariantCulture) == original + 1, "跨表事务未应用");
            var save = sourceWorkspace.Save();
            TestSupport.Assert(save.Saved, "迁移测试源项目保存失败");

            var plan = new MigrationService().Preview(sourceRoot, targetRoot);
            TestSupport.Assert(plan.WritableChanges.Any(change => change.TableKey == "persons" && change.FieldName == "武力"), "迁移预览未发现人物字段差异");
            var result = new MigrationService().Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success && result.AppliedChanges > 0, "迁移应用失败");

            var targetWorkspace = new ProjectWorkspace();
            targetWorkspace.Open(targetRoot);
            var migrated = Convert.ToInt32(targetWorkspace.GetDocument("persons").Data.Rows[0]["武力"], CultureInfo.InvariantCulture);
            TestSupport.Assert(migrated == original + 1, "迁移后目标字段未复读");
        }
        finally
        {
            Directory.Delete(sourceRoot, true);
            Directory.Delete(targetRoot, true);
        }
    }

    private static void TestMigrationRejectsStalePlan(string basePath)
    {
        var sourceRoot = TestSupport.CreateCoreCopy(basePath);
        var targetRoot = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var service = new MigrationService();
            var plan = service.Preview(sourceRoot, targetRoot);
            var sourceData = Path.Combine(sourceRoot, "Data.e5");
            var bytes = File.ReadAllBytes(sourceData);
            bytes[0] ^= 0x01;
            File.WriteAllBytes(sourceData, bytes);
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(!result.Success && result.Messages.Any(message => message.Contains("预览后发生变化", StringComparison.Ordinal)), "迁移应拒绝套用过期预览");
        }
        finally
        {
            Directory.Delete(sourceRoot, true);
            Directory.Delete(targetRoot, true);
        }
    }
}
