using DataManager.Core.Editing;
using DataManager.Core.Migration;

namespace DataManager.Tests;

internal static class ItemMigrationTests
{
    public static void Run(string basePath)
    {
        CrossVersion(basePath, false);
        CrossVersion(basePath, true);
    }

    private static void CrossVersion(string basePath, bool source66)
    {
        var source = TestSupport.CreateCoreCopy(basePath); var target = TestSupport.CreateCoreCopy(basePath);
        try
        {
            foreach (var root in new[] { source, target }) File.WriteAllText(Path.Combine(root, "System.ini"), "DefID=70\r\nAssID=80\r\n");
            Seed(source, source66, [(0, 4, 5), (90, 44, 2), (109, 45, 2), (110, 46, 3), (111, 47, 2)]);
            Seed(target, !source66, [(0, 6, 7), (90, 16, 2), (109, 17, 2), (110, 18, 2), (111, 19, 4)]);
            var targetBefore = new Dictionary<string, byte[]> { ["Data.e5"] = File.ReadAllBytes(Path.Combine(target, "Data.e5")), ["Star.e5"] = File.ReadAllBytes(Path.Combine(target, "Star.e5")) };
            var service = new MigrationService();
            var plan = service.Preview(source, target, source66 ? "ccz-6.6" : "ccz-6.5", source66 ? "66-standard-v1" : "65-standard-v1",
                source66 ? "ccz-6.5" : "ccz-6.6", source66 ? "65-standard-v1" : "66-standard-v1");
            TestSupport.Assert(plan.Changes.Any(change => change.TableKey == "items-high" && change.Id == 111 && change.Kind == MigrationChangeKind.Incompatible && change.Reason!.Contains("类别", StringComparison.Ordinal)), "未报告辅助与未知类别间的不可映射行");
            TestSupport.Assert(!plan.WritableChanges.Any(change => change.TableKey == "items-high" && change.Id == 111), "不支持的物品类别转换产生了部分可写字段");
            var effectField = source66 ? "宝物类型" : "装备特效号";
            TestSupport.Assert(plan.WritableChanges.Any(change => change.TableKey == "items-high" && change.Id == 109 && change.FieldName == effectField && Convert.ToInt32(change.SourceValue) == 45), "跨版本实际效果没有映射到目标字段");
            var choices = plan.Changes.Select((change, index) => (change, index)).Where(item => item.change.Kind == MigrationChangeKind.Changed &&
                item.change.TableKey is "items-low" or "items-high" && item.change.Id is 0 or 90 or 109 or 110 && item.change.FieldName is "宝物类型" or "装备特效号")
                .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            var result = service.Apply(plan, choices);
            TestSupport.Assert(result.Success, $"{(source66 ? "66→65" : "65→66")}物品迁移失败：" + string.Join("；", result.Messages));
            var workspace = Open(target, !source66);
            foreach (var (id, effect, marker) in new[] { (0, 4, 5), (90, 44, 2), (109, 45, 2), (110, 46, 3), (111, 19, 4) })
            {
                var document = workspace.GetDocument(id < 104 ? "items-low" : "items-high");
                var rowIndex = id - document.Definition.BeginId;
                var offset = checked((int)document.Definition.DataOffset + rowIndex * document.Definition.RowSize);
                var bytes = File.ReadAllBytes(document.FilePath);
                TestSupport.Assert(bytes[offset + 0x11] == effect && bytes[offset + 0x12] == marker, $"物品{id}的原始效果/类别标记颠倒或被乱写");
                var row = document.Data.Rows[rowIndex];
                var dynamicMarker = !source66 && id != 0 && id != 111;
                TestSupport.Assert(Convert.ToInt32(row["宝物类型"]) == (dynamicMarker ? marker : effect) &&
                    Convert.ToInt32(row["装备特效号"]) == (dynamicMarker ? effect : marker), $"物品{id}经目标Codec复读后语义错误");
                if (id == 111) TestSupport.Assert(targetBefore[document.Definition.FileName].AsSpan(offset, document.Definition.RowSize).SequenceEqual(bytes.AsSpan(offset, document.Definition.RowSize)), "类别转换被拒绝的整行仍被改动");
            }
        }
        finally { Directory.Delete(source, true); Directory.Delete(target, true); }
    }

    private static ProjectWorkspace Open(string root, bool is66)
    {
        var workspace = new ProjectWorkspace(); workspace.Open(root, is66 ? "ccz-6.6" : "ccz-6.5", is66 ? "66-standard-v1" : "65-standard-v1"); return workspace;
    }

    private static void Seed(string root, bool is66, (int Id, int Effect, int Marker)[] rows)
    {
        var workspace = Open(root, is66);
        foreach (var group in rows.GroupBy(row => row.Id < 104 ? "items-low" : "items-high"))
        {
            var document = workspace.GetDocument(group.Key); var bytes = File.ReadAllBytes(document.FilePath);
            foreach (var row in group)
            {
                var offset = checked((int)document.Definition.DataOffset + (row.Id - document.Definition.BeginId) * document.Definition.RowSize);
                bytes[offset + 0x11] = checked((byte)row.Effect); bytes[offset + 0x12] = checked((byte)row.Marker);
            }
            File.WriteAllBytes(document.FilePath, bytes);
        }
    }
}
