namespace ImageManager.App;

/// <summary>A character assignment projected from the caller's current draft into Unit image numbers.</summary>
public sealed record SImageAssignmentReference(int PersonId, string PersonName, int SId,
    IReadOnlyList<int> PhysicalImageNumbers);

public static class SImageDeleteImpact
{
    public static IReadOnlyList<string> BuildWarnings(IEnumerable<int> selectedPhysicalNumbers,
        IReadOnlyList<SImageAssignmentReference>? references, int maximumPhysicalNumber)
    {
        var deleted = selectedPhysicalNumbers.Distinct().Order().ToArray();
        if (deleted.Length == 0) return [];
        var warnings = new List<string>
        {
            "删除的将领 S 物理图号：" + string.Join("、", deleted.Select(number => $"Unit #{number}")),
            "所选图号会从 Unit_mov/atk/spc 同步删除；后续物理图号前移，人物紧凑 S 指定值保持原值。"
        };
        if (references == null)
        {
            warnings.Add("人物引用未验证，无法列出受影响的已知人物。请结合人物指定表检查后续编号。");
            return warnings;
        }

        var affectedCount = 0;
        foreach (var reference in references.OrderBy(item => item.PersonId))
        {
            var impacts = new List<string>();
            foreach (var number in reference.PhysicalImageNumbers.Distinct().Order())
            {
                if (number < deleted[0] || number > maximumPhysicalNumber) continue;
                var position = Array.BinarySearch(deleted, number);
                if (position >= 0) impacts.Add($"Unit #{number} 被删除");
                else
                {
                    var shift = ~position;
                    if (shift > 0) impacts.Add($"Unit #{number} → #{number - shift}（前移 {shift}）");
                }
            }
            if (impacts.Count == 0) continue;
            affectedCount++;
            warnings.Add($"人物 ID {reference.PersonId} / {reference.PersonName} / 紧凑 S={reference.SId}：" +
                         string.Join("；", impacts));
        }
        warnings.Insert(2, $"当前草稿中受影响的已知人物：{affectedCount} 人（含所选图号与后续前移图号的引用）。");
        return warnings;
    }
}

internal sealed class SImageDeleteConfirmationDialog : Form
{
    internal SImageDeleteConfirmationDialog(IReadOnlyList<string> warnings)
    {
        Text = "确认删除将领 S 物理图号";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 640);
        MinimumSize = new Size(650, 420);
        var details = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, BackColor = SystemColors.Window,
            Text = string.Join(Environment.NewLine + Environment.NewLine, warnings),
            SelectionStart = 0, SelectionLength = 0
        };
        var explanation = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12),
            Text = "以下明细使用人物页当前草稿。确认后立即删除，人物指定值不会自动调整。"
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10), WrapContents = false
        };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var confirm = new Button { Text = "删除并前移编号", AutoSize = true, DialogResult = DialogResult.OK };
        actions.Controls.Add(cancel);
        actions.Controls.Add(confirm);
        Controls.Add(details);
        Controls.Add(explanation);
        Controls.Add(actions);
        CancelButton = cancel;
        AcceptButton = cancel;
    }
}
