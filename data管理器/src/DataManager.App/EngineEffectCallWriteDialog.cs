using System.Globalization;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.App;

/// <summary>
/// Edits the known arguments of an unassigned/dynamic call.  Only arguments with
/// a proven direct or safe computed immediate are enabled; the write goes through
/// the same preview, backup and history transaction as effect-level writes.
/// </summary>
public sealed class EngineEffectCallWriteDialog : Form
{
    private readonly ProjectWorkspace _workspace;
    private readonly EngineEffectScanReport _report;
    private readonly EngineEffectCallSite _call;
    private readonly ComboBox _entryCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _functionEntryLabel = new();
    private readonly ComboBox _valueModeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _stackingCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _itemIdCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _personalIdCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly IReadOnlyList<EngineEffectChoiceItem> _itemChoices;
    private readonly IReadOnlyList<EngineEffectChoiceItem> _jobChoices;
    private readonly TextBox _evidenceBox = new();
    private readonly Button _previewButton = new() { Text = "写入预览", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };

    public EngineEffectCallWriteDialog(ProjectWorkspace workspace, EngineEffectScanReport report, EngineEffectCallSite call,
        IReadOnlyList<EngineEffectChoiceItem>? itemChoices = null, IReadOnlyList<EngineEffectChoiceItem>? jobChoices = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _call = call ?? throw new ArgumentNullException(nameof(call));
        var rows = itemChoices is { Count: > 0 } ? itemChoices : report.Effects.OrderBy(row => row.EffectId)
            .Select(row => new EngineEffectChoiceItem(row.EffectId, "未分配")).ToArray();
        _itemChoices = rows;
        _jobChoices = jobChoices is { Count: > 0 } ? jobChoices : rows;
        Text = $"写入调用点 0x{call.CallAddress:X8}";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Width = 820;
        Height = 620;
        MinimumSize = new Size(680, 480);
        BuildLayout();
        Populate();
    }

    private void BuildLayout()
    {
        var summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(10, 6, 10, 4),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"调用点 0x{_call.CallAddress:X8}  入口 {_call.EntryName}  " +
                   $"桩类型 {EngineEffectDetailPanel.PatternTextForDialog(_call.Pattern)}  " +
                   $"状态 {EngineEffectDetailPanel.StatusText(_call.Status)}\r\n" +
                   "只读字段来自反汇编证据；仅“可写”字段会进入预览。"
        };

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 116,
            Padding = new Padding(10, 4, 10, 4),
            ColumnCount = 4,
            RowCount = 3
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < 3; row++) fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));

        _entryCombo.Dock = DockStyle.Fill;
        _entryCombo.Margin = new Padding(0, 2, 8, 2);
        _functionEntryLabel.Dock = DockStyle.Fill;
        _functionEntryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _functionEntryLabel.ForeColor = Color.DimGray;
        _functionEntryLabel.Margin = new Padding(0, 2, 0, 2);
        _valueModeCombo.Dock = DockStyle.Fill;
        _valueModeCombo.Margin = new Padding(0, 2, 8, 2);
        _stackingCombo.Dock = DockStyle.Fill;
        _stackingCombo.Margin = new Padding(0, 2, 0, 2);
        _itemIdCombo.Dock = DockStyle.Fill;
        _itemIdCombo.Margin = new Padding(0, 2, 8, 2);
        _personalIdCombo.Dock = DockStyle.Fill;
        _personalIdCombo.Margin = new Padding(0, 2, 0, 2);

        fields.Controls.Add(FieldLabel("判定入口"), 0, 0);
        fields.Controls.Add(_entryCombo, 1, 0);
        fields.Controls.Add(FieldLabel("函数入口"), 2, 0);
        fields.Controls.Add(_functionEntryLabel, 3, 0);
        fields.Controls.Add(FieldLabel("效果值模式"), 0, 1);
        fields.Controls.Add(_valueModeCombo, 1, 1);
        fields.Controls.Add(FieldLabel("叠加方式"), 2, 1);
        fields.Controls.Add(_stackingCombo, 3, 1);
        fields.Controls.Add(FieldLabel("装备特效号"), 0, 2);
        fields.Controls.Add(_itemIdCombo, 1, 2);
        fields.Controls.Add(FieldLabel("兵种特效号"), 2, 2);
        fields.Controls.Add(_personalIdCombo, 3, 2);

        _evidenceBox.Dock = DockStyle.Fill;
        _evidenceBox.Multiline = true;
        _evidenceBox.ReadOnly = true;
        _evidenceBox.ScrollBars = ScrollBars.Both;
        _evidenceBox.WordWrap = false;
        _evidenceBox.BackColor = Color.White;
        _evidenceBox.Font = new Font(FontFamily.GenericMonospace, Math.Max(8.5f, Font.Size - 0.5f));
        _evidenceBox.Text = _call.DisassemblyText;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8),
            BackColor = Color.FromArgb(245, 247, 250)
        };
        _cancelButton.Margin = new Padding(8, 0, 0, 0);
        _previewButton.Margin = new Padding(8, 0, 0, 0);
        _previewButton.Click += (_, _) => PreviewAndWrite();
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_previewButton);

        Controls.Add(_evidenceBox);
        Controls.Add(fields);
        Controls.Add(summary);
        Controls.Add(buttons);
        AcceptButton = _previewButton;
        CancelButton = _cancelButton;
    }

    private void Populate()
    {
        if (_call.Pattern == EngineEffectCallPattern.WrapperDerived)
        {
            _entryCombo.Items.Add(new EntryChoice(_call.EntryTarget,
                $"0x{_call.EntryTarget:X8} 调用桩（派生，只读）"));
            _entryCombo.SelectedIndex = 0;
            _entryCombo.Enabled = false;
        }
        else
        {
            foreach (var target in EngineEffectScanService.KnownEntryTargets)
                _entryCombo.Items.Add(new EntryChoice(target, $"0x{target:X8} {EngineEffectScanService.EntryNames[target]}"));
        }
        for (var value = 0; value <= byte.MaxValue; value++)
            _valueModeCombo.Items.Add(new ModeChoice(value, value switch
            {
                0 => "0 读数值",
                1 => "1 返回开关",
                _ => $"{value.ToString(CultureInfo.InvariantCulture)} 未验证"
            }));
        for (var value = 0; value <= 3; value++)
            _stackingCombo.Items.Add(new StackingChoice(value, $"{value.ToString(CultureInfo.InvariantCulture)} {EngineEffectDetailPanel.StackingText(value)}"));

        _functionEntryLabel.Text = _call.FunctionEntry is { } entry
            ? $"0x{entry:X8}（+0x{_call.FunctionEntryDistance:X}）" : "—";
        if (_call.Pattern != EngineEffectCallPattern.WrapperDerived)
        {
            SelectEntry(_call.EntryTarget);
            _entryCombo.Enabled = _call.Pattern != EngineEffectCallPattern.EngineInternal;
        }

        SetCombo(_valueModeCombo, _call.ValueMode, _call.ValuePush.WritePlan);
        SetCombo(_stackingCombo, _call.StackingMode, _call.StackingPush.WritePlan);
        EngineEffectChoiceCombo.Fill(_itemIdCombo, _itemChoices);
        EngineEffectChoiceCombo.Fill(_personalIdCombo, _jobChoices);
        EngineEffectChoiceCombo.Select(_itemIdCombo, _call.ItemEffectId);
        EngineEffectChoiceCombo.Select(_personalIdCombo, _call.PersonalEffectId);
        _itemIdCombo.Enabled = _call.ItemPush.WritePlan.IsWritable;
        _personalIdCombo.Enabled = _call.PersonalPush.WritePlan.IsWritable;

        var tips = new ToolTip();
        tips.SetToolTip(_valueModeCombo, ArgumentTip(_call.ValuePush));
        tips.SetToolTip(_stackingCombo, ArgumentTip(_call.StackingPush));
        tips.SetToolTip(_itemIdCombo, ArgumentTip(_call.ItemPush));
        tips.SetToolTip(_personalIdCombo, ArgumentTip(_call.PersonalPush));
        tips.SetToolTip(_entryCombo, "判定入口只允许三个已验证目标；写入会改写 E8 rel32。");
        _previewButton.Enabled = _call.Pattern is not (EngineEffectCallPattern.EngineInternal
            or EngineEffectCallPattern.TableDerived or EngineEffectCallPattern.LoopDerived
            or EngineEffectCallPattern.NestedDerived);
    }

    private void PreviewAndWrite()
    {
        var edit = new EngineEffectCallEdit
        {
            Identity = _call.Identity,
            CallAddress = _call.CallAddress,
            ValueMode = SelectedValue(_valueModeCombo) is { } mode && mode != _call.ValueMode ? mode : null,
            StackingMode = SelectedValue(_stackingCombo) is { } stacking && stacking != _call.StackingMode ? stacking : null,
            ItemEffectId = _itemIdCombo.Enabled && EngineEffectChoiceCombo.Selected(_itemIdCombo) is { } itemId &&
                    itemId != (_call.ItemEffectId ?? 0)
                ? itemId : null,
            PersonalEffectId = _personalIdCombo.Enabled && EngineEffectChoiceCombo.Selected(_personalIdCombo) is { } personalId &&
                    personalId != (_call.PersonalEffectId ?? 0)
                ? personalId : null,
            EntryTarget = _entryCombo.SelectedItem is EntryChoice choice && choice.Target != _call.EntryTarget
                ? choice.Target : null
        };
        if (edit.ValueMode is null && edit.StackingMode is null && edit.ItemEffectId is null &&
            edit.PersonalEffectId is null && edit.EntryTarget is null)
        {
            MessageBox.Show(this, "没有修改。", "写入调用点", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var request = new EngineEffectWriteRequest
        {
            EffectId = -1,
            NewCalls = new Dictionary<uint, EngineEffectCallEdit> { [_call.CallAddress] = edit }
        };
        var service = new EngineEffectWriteService();
        EngineEffectWritePreview preview;
        try
        {
            preview = service.BuildPreview(_workspace, _report, request);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "写入调用点预览", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using (var dialog = new EngineEffectWritePreviewDialog(preview))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
        }
        EngineEffectWriteResult result;
        try
        {
            result = service.Commit(_workspace, preview);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "写入调用点失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (!result.Success)
        {
            MessageBox.Show(this, result.Message, "写入调用点失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        MessageBox.Show(this, result.Message, "写入调用点完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void SetCombo(ComboBox combo, int? current, EngineEffectArgumentWritePlan plan)
    {
        combo.SelectedIndex = current is >= 0 and <= byte.MaxValue ? current.Value : -1;
        combo.Enabled = plan.IsWritable;
    }

    private static int? SelectedValue(ComboBox combo) => combo.SelectedIndex;

    private void SelectEntry(uint target)
    {
        for (var index = 0; index < _entryCombo.Items.Count; index++)
            if (_entryCombo.Items[index] is EntryChoice choice && choice.Target == target)
            {
                _entryCombo.SelectedIndex = index;
                return;
            }
        _entryCombo.SelectedIndex = -1;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = false,
        Margin = new Padding(0, 0, 6, 0)
    };

    private static string ArgumentTip(EngineEffectPushArgument argument)
        => argument.WritePlan.IsWritable
            ? $"可写；定义指令 0x{argument.WritePlan.Definition?.Address:X8}，立即数 {argument.WritePlan.ImmediateLength} 字节。"
            : $"只读：{argument.WritePlan.BlockReason ?? "来源未证明"}";

    private sealed record EntryChoice(uint Target, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record ModeChoice(int Value, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record StackingChoice(int Value, string Display)
    {
        public override string ToString() => Display;
    }
}
