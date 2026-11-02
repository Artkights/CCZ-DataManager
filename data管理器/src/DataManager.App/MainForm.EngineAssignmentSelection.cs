using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly Dictionary<string, int?> _enginePreparedAssignments = new(StringComparer.Ordinal);
    internal Func<string, string, bool>? AssignmentConfirmationForSmokeTest { get; set; }
    internal Func<string, IReadOnlyList<EngineEffectJobSlot>, int?>? AssignmentSlotChoiceForSmokeTest { get; set; }
    internal Func<string, int?, int?>? AssignmentValueForSmokeTest { get; set; }

    private string PreparationKey(bool isJob, EngineEffectAssignmentEffect effect)
        => $"{isJob}:{(isJob ? _engineSelectedJob : _engineSelectedItem)}:{effect.Key}:" +
           string.Join(',', effect.ItemSelectors) + ":" + string.Join(',', effect.JobSlots.Select(slot => $"{slot.JobId}/{slot.Value}"));

    private IReadOnlyList<string> CurrentItemOwners(EngineEffectAssignmentEffect effect)
    {
        if (_engineAssignmentDraft is not { } draft) return [];
        return draft.Catalog.Items.Where(item =>
        {
            var selector = draft.ItemSelectorTargets.GetValueOrDefault(item.Id, item.Selector);
            return item.SelectorFileOffset >= 0 && (!item.NeedsSpecialType || draft.SpecialItemIds.Contains(item.Id)) &&
                selector != 255 && effect.ItemSelectors.Contains(selector);
        }).Select(item => item.Display).ToArray();
    }

    private bool PrepareEnginePoolSelection(bool isJob, EngineAssignmentDisplayRow row)
    {
        if (_engineAssignmentDraft is not { } draft || !row.Selectable) return false;
        var effect = _engineAssignmentService.GetEffects(draft).FirstOrDefault(effect => effect.Key == row.Key);
        if (effect == null) return false;
        var key = PreparationKey(isJob, effect);
        if (_enginePreparedAssignments.ContainsKey(key)) return true;
        int? replacement = null;
        if (!isJob)
        {
            var owners = CurrentItemOwners(effect);
            if (owners.Count > 0 && !ConfirmEngineAssignment(
                    "特效已被分配",
                    "该特效已分配给：" + string.Join("、", owners) + Environment.NewLine +
                    "继续后，双击将把该特效的装备渠道转移到当前宝物的实际特效号。是否继续？" + Environment.NewLine +
                    "本次选择不会新增草稿。")) return false;
        }
        else if (effect.IsJobPoolFull)
        {
            if (AssignmentSlotChoiceForSmokeTest != null) replacement = AssignmentSlotChoiceForSmokeTest(effect.Name, effect.JobSlots);
            else
            {
                using var dialog = new EngineAssignmentSlotDialog(effect.Name, effect.JobSlots, AssignmentJobName);
                if (dialog.ShowDialog(this) == DialogResult.OK) replacement = dialog.ReplacementSlot;
            }
            if (replacement is not { } selectedSlot || selectedSlot < 0 || selectedSlot >= effect.JobSlots.Count) return false;
        }
        _enginePreparedAssignments[key] = replacement;
        return true;
    }

    private bool ConfirmEngineAssignment(string title, string text)
        => AssignmentConfirmationForSmokeTest?.Invoke(title, text) ??
           MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    private string AssignmentJobName(int id)
        => _engineAssignmentCatalog?.Jobs.FirstOrDefault(job => job.Id == id)?.Display ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private bool ConfirmEngineSlotRemoval(string effectName, EngineEffectJobSlot slot, int selectedJobId)
    {
        var text = $"将卸下 {effectName} 的来源槽 {slot.Slot + 1}：{AssignmentJobName(slot.JobId)}，N={slot.Value}。\r\n" +
                   (slot.JobId == selectedJobId ? "当前兵种直接命中此槽。" : "当前兵种通过继承命中此槽。") + "\r\n" +
                   $"匹配范围：{slot.MatchScope}\r\n" +
                   $"当前受影响兵种：{string.Join("、", slot.AffectedJobIds.Select(AssignmentJobName))}\r\n" +
                   $"同族模式下可能影响：{string.Join("、", slot.PotentialAffectedJobIds.Select(AssignmentJobName))}\r\n" +
                   "继续将整个来源槽加入卸下草稿。";
        return ConfirmEngineAssignment("卸下兵种来源槽", text);
    }

    private int? RequestEngineAssignmentValue(string name, int? current)
    {
        if (AssignmentValueForSmokeTest != null) return AssignmentValueForSmokeTest(name, current);
        using var dialog = new EngineAssignmentValueDialog(name, current);
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Value : null;
    }

    internal static EngineAssignmentItemValueInfo DescribeItemValue(EngineEffectAssignmentEffect effect, EngineEffectItemEntity? item)
    {
        var raw = item?.Value is { } value ? "N=" + value : "N 未确认";
        var writable = item?.ValueFileOffset is >= 0 && item.Value != null;
        if (effect.Kind is EngineEffectAssignmentKind.NativeHp or EngineEffectAssignmentKind.NativeMp)
            return new(raw, "原生数值路径读取宝物共享 N；" + item?.ValueSource, writable);
        var modes = effect.BindingPoints.Select(point => point.ValueMode).Distinct().ToArray();
        var numeric = modes.Contains(0);
        var boolean = modes.Any(mode => mode is not null and not 0);
        var unknown = modes.Length == 0 || modes.Contains(null);
        if (!numeric && boolean && !unknown)
            return new("开关／不读取装备数值", "模式 " + string.Join('/', modes) + "：开关／不读取装备数值；装备命中时返回100。该效果不能修改宝物共享 N。", false);
        if (numeric && boolean)
            return new("混合：" + raw + "／开关", "包含数值模式0与开关模式：仅模式0读取宝物共享 N，其他已知非零模式不读取 N；" + item?.ValueSource + (unknown ? "；另有未解析模式。" : string.Empty), writable && !unknown);
        if (unknown)
            return new(numeric ? "混合：" + raw + "／模式未知" : "装备值模式未确认", "存在未解析的装备值模式；仍可编辑已定位的宝物共享 N，具体读取方式未解析。", writable);
        return new(raw, "数值模式0读取宝物共享 N，并按等级与效果规则换算；" + item?.ValueSource, writable);
    }
}

internal sealed record EngineAssignmentItemValueInfo(string Display, string Explanation, bool Editable);
