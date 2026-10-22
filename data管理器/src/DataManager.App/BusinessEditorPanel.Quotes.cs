using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private void BuildQuoteEditor(Control host, int personId)
    {
        var service = new PersonQuoteService(_workspace);
        PersonQuoteMapping mapping;
        try { mapping = service.Resolve(personId); }
        catch (Exception ex) { host.Controls.Add(Notice(ex.Message)); return; }
        var writable = _workspace.Documents.TryGetValue("person-critical-special-map", out var map) && map.Definition.IsWriteSupported;
        var slots = service.GetSpecialSlots();
        var initialSlot = slots.FirstOrDefault(slot => slot.PersonId == personId)?.Id ?? slots.FirstOrDefault(slot => slot.IsEmpty)?.Id ?? 0;
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Enabled = writable, Margin = new Padding(2, 2, 4, 2) };
        mode.Items.AddRange(["普通暴击", "特殊暴击"]); mode.SelectedIndex = mapping.IsSpecial ? 1 : 0;
        var type = new NumericUpDown { Minimum = 0, Maximum = Math.Max(25, mapping.CriticalType), Value = mapping.CriticalType, Dock = DockStyle.Fill, Enabled = writable && !mapping.IsSpecial };
        var slot = new SearchableIdComboBox { Dock = DockStyle.Fill, Enabled = writable && mapping.IsSpecial };
        slot.SetOptions(slots.Select(value => new IdNameOption(value.Id, value.IsEmpty ? "空槽" : $"{value.PersonId}：{value.PersonName}")));
        slot.SetBoundValue(slots.FirstOrDefault(value => value.Id == initialSlot)?.Display ?? initialSlot.ToString(CultureInfo.InvariantCulture));
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9,
            Padding = new Padding(4), Margin = Padding.Empty,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 9; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var arrangeQuotes = AttachScrollableContent(host, layout, () => new Size(0,
            (mapping.IsSpecial ? 2 : 4) * Math.Max(layout.Font.Height * 2 + 28, 56 * layout.DeviceDpi / 96) +
            4 * Math.Max(layout.Font.Height + 12, 26 * layout.DeviceDpi / 96)));
        Label AddQuoteRow(int row, string caption, Control editor)
        {
            var label = new Label
            {
                Text = caption, Dock = DockStyle.Fill, AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 4, 2)
            };
            editor.Margin = new Padding(2, 2, 4, 2);
            layout.Controls.Add(label, 0, row); layout.Controls.Add(editor, 1, row);
            return label;
        }
        AddQuoteRow(0, "暴击模式", mode);
        var assignment = new Panel { Dock = DockStyle.Fill, Height = type.PreferredHeight + 4, Margin = Padding.Empty };
        assignment.Controls.Add(type); assignment.Controls.Add(slot);
        var assignmentLabel = AddQuoteRow(1, mapping.IsSpecial ? "特殊槽位" : "普通类型", assignment);
        _diagnosticTips.SetToolTip(type, "普通暴击类型 0～25；同类型人物共享三句台词。");
        _diagnosticTips.SetToolTip(slot, "特殊槽 0～20；槽中保存人物 ID，绑定后优先使用该槽台词。");
        var state = new Label { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = mapping.IsSpecial ? $"特殊槽 {initialSlot}" : "同类型人物共享三句台词" };
        _diagnosticTips.SetToolTip(state, mapping.Evidence);
        AddQuoteRow(2, "关联状态", state);
        // AutoSize labels need an explicit wrapping width inside a percentage
        // column; otherwise a long read-only reason can widen the whole page.
        layout.SizeChanged += (_, _) => state.MaximumSize = new Size(Math.Max(40, layout.ClientSize.Width - (int)layout.ColumnStyles[0].Width - 14), 0);
        var overwrite = new CheckBox { Text = "确认覆盖被占用的特殊槽", AutoSize = true, Visible = false };
        var conflictLabel = AddQuoteRow(3, "冲突处理", overwrite); conflictLabel.Visible = false;
        TextFieldConstraint Constraint(string key, int id)
        {
            var document = _workspace.GetDocument(key);
            var field = document.Definition.Fields.First(field => field.Name == "台词");
            return FindRow(document, id) is { } row ? ConstraintForRow(document, row.Index, field) : TextFieldConstraints.Get(document.Definition, field);
        }
        ByteTextEditor QuoteEditor() => new() { Dock = DockStyle.Fill, Editor = { Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true } };
        var textEditors = Enumerable.Range(0, 3).Select(_ => QuoteEditor()).ToArray();
        var texts = textEditors.Select(editor => editor.Editor).ToArray();
        for (var index = 0; index < texts.Length; index++) { texts[index].ReadOnly = !writable; AddQuoteRow(index + 4, $"暴击 {index + 1}", textEditors[index]); }
        var retreatEditor = QuoteEditor(); var retreat = retreatEditor.Editor;
        retreat.ReadOnly = mapping.Retreat == null;
        if (mapping.Retreat != null) retreatEditor.Bind(Constraint(mapping.Retreat.TableKey, mapping.Retreat.Id), mapping.Retreat.Text);
        AddQuoteRow(7, "撤退台词", retreatEditor);
        PersonQuoteAssignmentPreview? preview = null;
        var changing = false;
        string Signature() => $"{mode.SelectedIndex}|{type.Value}|{slot.Text}|{overwrite.Checked}|{string.Join('\u001f', texts.Select(text => text.Text))}|{retreat.Text}";
        string SelectionSignature() => $"{mode.SelectedIndex}|{type.Value}|{(mode.SelectedIndex == 1 && slot.TryGetSelectedId(out var selectedSlot) ? selectedSlot : -1)}";
        var loadedSelection = string.Empty;
        string lastSubmitted = string.Empty;
        IReadOnlyList<PersonQuoteRow> loadedRows = [];
        var loadedMode = mode.SelectedIndex; var loadedType = type.Value; var loadedSlotText = slot.Text;
        void RefreshQuotes(bool initial = false)
        {
            if (changing) return;
            changing = true;
            try
            {
                preview = null;
                var special = mode.SelectedIndex == 1;
                type.Enabled = writable && !special; slot.Enabled = writable && special;
                type.Visible = !special; slot.Visible = special;
                assignmentLabel.Text = special ? "特殊槽位" : "普通类型";
                IReadOnlyList<PersonQuoteRow> quotes = mapping.Critical;
                if (writable)
                {
                    if (!slot.TryGetSelectedId(out var slotId)) slotId = initialSlot;
                    preview = service.PreviewAssignment(personId, special ? PersonCriticalQuoteMode.Special : PersonCriticalQuoteMode.Ordinary, (int)type.Value, special ? slotId : null);
                    quotes = preview.Critical;
                    state.Text = special ? $"特殊槽 {slotId} → 人物 {personId}" : "同类型人物共享三句台词";
                    _diagnosticTips.SetToolTip(state, preview.Evidence);
                    overwrite.Visible = preview.RequiresOverwrite;
                    conflictLabel.Visible = preview.RequiresOverwrite;
                    overwrite.Checked = false;
                    if (preview.RequiresOverwrite) overwrite.Text = $"覆盖人物 {preview.ConflictingPersonId} 的特殊槽";
                }
                else state.Text = "台词映射只读（悬停查看原因）";
                for (var index = 0; index < texts.Length; index++)
                {
                    if (index < quotes.Count) textEditors[index].Bind(Constraint(quotes[index].TableKey, quotes[index].Id), quotes[index].Text);
                    texts[index].Enabled = index < quotes.Count;
                    textEditors[index].Visible = index < quotes.Count;
                    var position = layout.GetPositionFromControl(textEditors[index]);
                    layout.GetControlFromPosition(0, position.Row)!.Visible = index < quotes.Count;
                    layout.RowStyles[position.Row].SizeType = index < quotes.Count ? SizeType.Percent : SizeType.Absolute;
                    layout.RowStyles[position.Row].Height = index < quotes.Count ? 1 : 0;
                    _diagnosticTips.SetToolTip(texts[index], index < quotes.Count ? $"文本行 {quotes[index].Id}" : "特殊模式只有一句台词");
                }
                layout.RowStyles[7].SizeType = SizeType.Percent;
                layout.RowStyles[7].Height = 1.15f;
                arrangeQuotes();
                loadedSelection = SelectionSignature();
                loadedRows = quotes;
                loadedMode = mode.SelectedIndex; loadedType = type.Value; loadedSlotText = slot.Text;
                if (initial) lastSubmitted = Signature();
            }
            catch (Exception ex) { state.Text = ex.Message; _diagnosticTips.SetToolTip(state, ex.Message); }
            finally { changing = false; }
        }
        RefreshQuotes(true);
        bool ValidateTexts()
        {
            foreach (var text in texts.Take(loadedRows.Count).Append(retreat).Where(box => !box.ReadOnly && box.InputGuard.HasPendingChanges))
                if (!text.InputGuard.Prepare()) { ShowEditorError(text, text.InputGuard.RejectionMessage ?? "台词无法保存。"); return false; }
            return true;
        }
        bool ChangeSelection()
        {
            if (_binding || changing) return true;
            if (!ValidateTexts()) { RestoreSelection(); return false; }
            try
            {
                if (preview?.RequiresOverwrite == true && texts.Take(loadedRows.Count).Any(text => text.InputGuard.HasPendingChanges) && !overwrite.Checked)
                    throw new InvalidOperationException($"特殊暴击槽 {preview.SpecialSlot} 已由人物 {preview.ConflictingPersonId} 占用；保留该槽文本前须显式选择覆盖。");
                // Selector events arrive after the selector changes. Preserve the
                // text in its previously loaded physical rows before loading new rows.
                var edits = new List<CompositeCellEdit>();
                for (var index = 0; index < loadedRows.Count; index++)
                    if (texts[index].InputGuard.HasPendingChanges && FindRow(_workspace.GetDocument(loadedRows[index].TableKey), loadedRows[index].Id) is { } row)
                        edits.Add(new(loadedRows[index].TableKey, row.Index, "台词", texts[index].Text));
                if (mapping.Retreat != null && retreat.InputGuard.HasPendingChanges && FindRow(_workspace.GetDocument(mapping.Retreat.TableKey), mapping.Retreat.Id) is { } retreatRow)
                    edits.Add(new(mapping.Retreat.TableKey, retreatRow.Index, "台词", retreat.Text));
                if (edits.Count > 0 && _workspace.ApplyCompositeTransaction($"人物 {personId} 台词文本", edits).Applied) OnEdited();
                retreat.InputGuard.MarkSubmitted();
                RefreshQuotes(); NotifyPendingChanges(); return true;
            }
            catch (Exception ex) { RestoreSelection(); ShowEditorError(assignment, ex.Message); return false; }
        }
        void RestoreSelection()
        {
            changing = true;
            try { mode.SelectedIndex = loadedMode; type.Value = loadedType; slot.SetBoundValue(loadedSlotText); }
            finally { changing = false; }
        }
        bool SaveQuotes()
        {
            // A typed slot may be submitted by a toolbar action without Enter.
            // Load that slot before applying so text from the previous slot
            // cannot overwrite the newly entered slot.
            if (!_binding && !changing && SelectionSignature() != loadedSelection && !ChangeSelection()) return false;
            if (_binding || changing || Signature() == lastSubmitted) return true;
            if (!ValidateTexts()) return false;
            try
            {
                bool applied;
                if (writable)
                {
                    if (!slot.TryGetSelectedId(out var slotId)) throw new InvalidDataException("请输入特殊槽编号 0～20。");
                    var special = mode.SelectedIndex == 1;
                    var count = special ? 1 : 3;
                    applied = service.ApplyAssignment(personId, special ? PersonCriticalQuoteMode.Special : PersonCriticalQuoteMode.Ordinary,
                        (int)type.Value, special ? slotId : null, texts.Take(count).Select(box => box.Text).ToArray(),
                        mapping.Retreat == null ? null : retreat.Text, overwrite.Checked).Applied;
                }
                else if (mapping.Retreat != null)
                {
                    var document = _workspace.GetDocument(mapping.Retreat.TableKey);
                    if (FindRow(document, mapping.Retreat.Id) is not { } row) return false;
                    applied = _workspace.ApplyCompositeTransaction($"人物 {personId} 撤退台词", [new(mapping.Retreat.TableKey, row.Index, "台词", retreat.Text)]).Applied;
                }
                else return true;
                _errors.SetError(retreat, string.Empty); lastSubmitted = Signature();
                foreach (var text in texts.Append(retreat)) text.InputGuard.MarkSubmitted();
                if (applied) OnEdited();
                NotifyPendingChanges();
                return true;
            }
            catch (Exception ex) { ShowEditorError(overwrite.Visible ? overwrite : assignment, ex.Message); return false; }
        }
        mode.SelectedIndexChanged += (_, _) => ChangeSelection();
        type.ValueChanged += (_, _) => ChangeSelection();
        slot.SelectionChangeCommitted += (_, _) => ChangeSelection();
        slot.UnknownValueSubmitted += (_, _) => ChangeSelection();
        slot.Validating += (_, args) => { if (SelectionSignature() != loadedSelection) args.Cancel = !ChangeSelection(); };
        foreach (var text in texts.Append(retreat))
        {
            text.InputGuard.StateChanged += (_, _) => { if (!changing) NotifyPendingChanges(); };
            text.InputGuard.InputRejected += (_, _) => ShowEditorError(text, text.InputGuard.RejectionMessage ?? "本次输入已拒绝。", false);
        }
        slot.TextChanged += (_, _) => { if (!changing) NotifyPendingChanges(); };
        _pendingChecks.Add(() => Signature() != lastSubmitted);
        var apply = new Button { Text = "应用台词", AutoSize = true, Anchor = AnchorStyles.Left, MinimumSize = new Size(92, 28), Enabled = writable || mapping.Retreat != null };
        apply.Click += (_, _) => SaveQuotes(); AddQuoteRow(8, string.Empty, apply);
        void FitQuoteLabels()
        {
            var measured = layout.Controls.OfType<Label>().Where(label => layout.GetPositionFromControl(label).Column == 0)
                .Select(label => TextRenderer.MeasureText(label.Text, layout.Font, Size.Empty, TextFormatFlags.SingleLine).Width + 8)
                .DefaultIfEmpty(72).Max();
            layout.ColumnStyles[0].Width = Math.Max(72 * layout.DeviceDpi / 96f, measured);
            state.MaximumSize = new Size(Math.Max(40, layout.ClientSize.Width - (int)layout.ColumnStyles[0].Width - 14), 0);
        }
        layout.FontChanged += (_, _) => FitQuoteLabels();
        layout.HandleCreated += (_, _) => FitQuoteLabels();
        layout.VisibleChanged += (_, _) => { if (layout.Visible) FitQuoteLabels(); };
        FitQuoteLabels();
        _flushers.Add(SaveQuotes);
        _entityRebinders.Add(() =>
        {
            changing = true;
            try
            {
                personId = _id; mapping = service.Resolve(personId);
                slots = service.GetSpecialSlots();
                initialSlot = slots.FirstOrDefault(value => value.PersonId == personId)?.Id ?? slots.FirstOrDefault(value => value.IsEmpty)?.Id ?? 0;
                mode.SelectedIndex = mapping.IsSpecial ? 1 : 0;
                type.Maximum = Math.Max(25, mapping.CriticalType); type.Value = mapping.CriticalType;
                slot.SetOptions(slots.Select(value => new IdNameOption(value.Id, value.IsEmpty ? "空槽" : $"{value.PersonId}：{value.PersonName}")));
                slot.SetBoundValue(slots.FirstOrDefault(value => value.Id == initialSlot)?.Display ?? initialSlot.ToString(CultureInfo.InvariantCulture));
                if (mapping.Retreat != null) retreatEditor.Bind(Constraint(mapping.Retreat.TableKey, mapping.Retreat.Id), mapping.Retreat.Text);
                retreat.ReadOnly = mapping.Retreat == null;
            }
            finally { changing = false; }
            RefreshQuotes(true);
        });
    }
}
