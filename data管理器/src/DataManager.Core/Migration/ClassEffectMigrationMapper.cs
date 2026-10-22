using System.Data;
using System.Globalization;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Migration;

/// <summary>Maps logical assignment slots across the shared-value and per-slot-value layouts.</summary>
internal static class ClassEffectMigrationMapper
{
    private static readonly string[] LegacyPersons = ["一号武将", "二号武将", "三号武将"];

    public static bool TryMap(ProjectWorkspace targetWorkspace, TableDocument source, TableDocument target,
        DataRow sourceRow, DataRow targetRow, List<MigrationChange> changes)
    {
        if (source.Definition.Key != "class-effects" || source.Definition.RowSize == target.Definition.RowSize) return false;
        var id = Number(sourceRow, "ID");
        if (source.Definition.RowSize == 8 && target.Definition.RowSize == 16)
        {
            var value = Number(sourceRow, "特效值");
            for (var slot = 0; slot < LegacyPersons.Length; slot++)
            {
                var person = Number(sourceRow, LegacyPersons[slot]);
                Add($"武将{slot + 1}", person, $"8 → 16 字节：{LegacyPersons[slot]}映射到武将{slot + 1}，目标第4武将槽保持原值");
                if (person < 1024) Add($"武将{slot + 1}特效值", value, "8 → 16 字节：共享特效值映射到有效武将槽");
            }
            var job = Number(sourceRow, "兵种");
            Add("兵种1", job, "8 → 16 字节：兵种映射到兵种1，目标第2兵种槽保持原值");
            if (job < 255) Add("兵种1特效值", value, "8 → 16 字节：共享特效值映射到有效兵种槽");
            return true;
        }
        if (source.Definition.RowSize == 16 && target.Definition.RowSize == 8)
        {
            var blockers = new List<string>();
            if (Number(sourceRow, "武将4") < 1024) blockers.Add($"第4武将槽 ID={Number(sourceRow, "武将4")} 不能由8字节布局表达");
            if (Number(sourceRow, "兵种2") < 255) blockers.Add($"第2兵种槽 ID={Number(sourceRow, "兵种2")} 不能由8字节布局表达");
            var activeValues = Enumerable.Range(1, 3).Where(slot => Number(sourceRow, $"武将{slot}") < 1024)
                .Select(slot => ($"武将{slot}", Value: Number(sourceRow, $"武将{slot}特效值"))).ToList();
            if (Number(sourceRow, "兵种1") < 255) activeValues.Add(("兵种1", Number(sourceRow, "兵种1特效值")));
            if (activeValues.Select(item => item.Value).Distinct().Count() > 1)
                blockers.Add($"有效槽特效值不一致（{string.Join("，", activeValues.Select(item => $"{item.Item1}={item.Value}"))}），8字节布局只有一个共享特效值");
            if (blockers.Count > 0)
            {
                changes.Add(new MigrationChange("class-effects", source.Definition.DisplayName, "整行分配", id,
                    Describe16(sourceRow), Describe8(targetRow), MigrationChangeKind.Incompatible,
                    $"16 → 8 字节整行不可映射：{string.Join("；", blockers)}。本行保持目标原值，避免丢失源分配。"));
                return true;
            }
            for (var slot = 0; slot < LegacyPersons.Length; slot++)
                Add(LegacyPersons[slot], Number(sourceRow, $"武将{slot + 1}"), $"16 → 8 字节：武将{slot + 1}映射到{LegacyPersons[slot]}");
            Add("兵种", Number(sourceRow, "兵种1"), "16 → 8 字节：兵种1映射到兵种");
            if (activeValues.Count > 0) Add("特效值", activeValues[0].Value, "16 → 8 字节：有效槽特效值一致，合并为共享特效值");
            return true;
        }
        changes.Add(new MigrationChange("class-effects", source.Definition.DisplayName, "布局", id,
            source.Definition.RowSize, target.Definition.RowSize, MigrationChangeKind.Incompatible, "兵种特效分配没有可用的布局转换器"));
        return true;

        void Add(string fieldName, int value, string reason)
        {
            var current = Number(targetRow, fieldName);
            if (current == value) return;
            if (source.Definition.Capability != TableCapability.Editable || target.Definition.Capability != TableCapability.Editable)
            {
                changes.Add(new MigrationChange("class-effects", source.Definition.DisplayName, fieldName, id,
                    value, current, MigrationChangeKind.Incompatible, "源或目标兵种特效分配表缺少可写布局证据"));
                return;
            }
            try { targetWorkspace.ValidateCellValue(target, target.Data.Rows.IndexOf(targetRow), fieldName, value); }
            catch (Exception ex)
            {
                changes.Add(new MigrationChange("class-effects", source.Definition.DisplayName, fieldName, id,
                    value, current, MigrationChangeKind.Incompatible, $"目标字段无法接受逻辑映射值：{ex.Message}"));
                return;
            }
            changes.Add(new MigrationChange("class-effects", source.Definition.DisplayName, fieldName, id,
                value, current, MigrationChangeKind.Changed, reason));
        }
    }

    private static int Number(DataRow row, string field) => Convert.ToInt32(row[field], CultureInfo.InvariantCulture);
    private static string Describe16(DataRow row) => string.Join("，", Enumerable.Range(1, 4)
        .Select(slot => $"武将{slot}={Number(row, $"武将{slot}")}/{Number(row, $"武将{slot}特效值")}")) +
        $"，兵种1={Number(row, "兵种1")}/{Number(row, "兵种1特效值")}，兵种2={Number(row, "兵种2")}/{Number(row, "兵种2特效值")}";
    private static string Describe8(DataRow row) => string.Join("，", LegacyPersons.Select(field => $"{field}={Number(row, field)}")) +
        $"，兵种={Number(row, "兵种")}，共享值={Number(row, "特效值")}";
}
