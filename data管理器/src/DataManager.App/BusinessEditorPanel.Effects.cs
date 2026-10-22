using System.ComponentModel;
using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private static readonly IReadOnlyDictionary<string, string> ScenarioParameterLabels = new Dictionary<string, string>
    {
        ["EffectId"] = "特效", ["Slot"] = "槽位", ["TargetId"] = "人物/兵种", ["Value"] = "特效值",
        ["ItemId"] = "专属物品", ["WeaponId"] = "武器", ["ArmorId"] = "防具", ["AccessoryId"] = "辅助"
    };

    private void BuildEffectsPage()
    {
        var assignment = DetailPage("特效分配");
        var columns = Columns(66, 34); assignment.Controls.Add(columns);
        var left = Rows((SizeType.Absolute, 70), (SizeType.Percent, 100)); columns.Controls.Add(left, 0, 0);
        left.Controls.Add(FieldsBlock("", Fields("effect-names"), 1), 0, 0);
        left.Controls.Add(FieldsBlock("", Fields("class-effects"), 2), 0, 1);
        columns.Controls.Add(FieldsBlock("", Fields("effect-descriptions"), 1, textHeight: 300), 1, 0);

        var exclusive = DetailPage("人物专属与套装");
        var exclusiveRows = Rows((SizeType.Percent, 50), (SizeType.Percent, 50)); exclusive.Controls.Add(exclusiveRows);
        var personal = Columns(50, 50); var sets = Columns(50, 50);
        exclusiveRows.Controls.Add(personal, 0, 0); exclusiveRows.Controls.Add(sets, 0, 1);
        var fields = Fields("personal-effects").ToArray();
        for (var slot = 1; slot <= 2; slot++)
        {
            var number = slot;
            personal.Controls.Add(FieldsBlock("", fields.Where(f => f.Field.Name == $"武将{number}" ||
                f.Field.Name == $"装备{number}" || f.Field.Name == $"特效值{number}"), 1), slot - 1, 0);
        }
        for (var slot = 3; slot <= 4; slot++)
        {
            var number = slot;
            sets.Controls.Add(FieldsBlock("", fields.Where(f => f.Field.Name.StartsWith($"装备{number}-", StringComparison.Ordinal) ||
                f.Field.Name == $"特效值{number}").Select(f => f with { Label = f.Field.Name.EndsWith("-1", StringComparison.Ordinal) ? "武器" :
                    f.Field.Name.EndsWith("-2", StringComparison.Ordinal) ? "防具" : f.Field.Name.EndsWith("-3", StringComparison.Ordinal) ? "辅助" : "特效值" }), 1), slot - 3, 0);
        }
        BuildScenarioEffectsPage();
    }

    public IReadOnlyList<PreparedFileEdit> GetPreparedFileEdits() => ScenarioEffectAssignmentService.For(_workspace).PrepareFileEdits();
    public void AcceptPreparedFileEdits() => ScenarioEffectAssignmentService.For(_workspace).AcceptSaved();
    public void DiscardScenarioEdits() => ScenarioEffectAssignmentService.For(_workspace).Discard();

    private void BuildScenarioEffectsPage()
    {
        var service = ScenarioEffectAssignmentService.For(_workspace);
        var page = DetailPage("剧本分配");
        var layout = Rows((SizeType.Absolute, 36), (SizeType.Percent, 62), (SizeType.Percent, 38)); page.Controls.Add(layout);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var scan = new Button { Text = "扫描剧本", AutoSize = true };
        var all = new CheckBox { Text = "全部特效", AutoSize = true, Margin = new Padding(12, 7, 6, 0) };
        var filter = new TextBox { Width = 240, PlaceholderText = "筛选文件、备注或分配结果" };
        var progress = new Label { AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
        toolbar.Controls.AddRange([scan, all, filter, progress]); layout.Controls.Add(toolbar, 0, 0);
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle, AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "分配", FillWeight = 12 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Binding", HeaderText = "读取结果", FillWeight = 27 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "剧本位置", FillWeight = 30 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Remarks", HeaderText = "原备注", FillWeight = 31 });
        layout.Controls.Add(grid, 0, 1);
        var detailColumns = Columns(56, 44); layout.Controls.Add(detailColumns, 0, 2);
        var parameters = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle };
        parameters.Columns.Add(new DataGridViewTextBoxColumn { Name = "Parameter", HeaderText = "参数", ReadOnly = true, FillWeight = 24 });
        parameters.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "数值 / *变量", FillWeight = 30 });
        parameters.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "名称", ReadOnly = true, FillWeight = 46 });
        detailColumns.Controls.Add(parameters, 0, 0);
        var remarks = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle };
        detailColumns.Controls.Add(remarks, 1, 0);
        IReadOnlyList<ScenarioEffectAssignment> entries = service.Read(false);
        ScenarioEffectAssignment? selected = null; var loading = false; var committing = false; var busy = false;
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token; var projectRoot = _workspace.RootPath; var refreshQueued = 0;
        var uiThread = Environment.CurrentManagedThreadId; var scanUiGeneration = 0;

        bool ViewAlive() => !_releaseCompleted && !_disposingDetailPages && !IsDisposed && !Disposing && !page.IsDisposed && !page.Disposing &&
            !_tabs.IsDisposed && !_tabs.Disposing && page.Parent == _tabs && FindForm()?.Disposing != true && projectRoot == _workspace.RootPath;
        void ReportScanError(Exception error)
        {
            if (!ViewAlive()) return;
            // ErrorProvider creates a native window. The existing toolbar label is
            // sufficient for background errors, including handle-creation failures.
            try { progress.Text = "扫描失败：" + error.Message; }
            catch (Exception lifecycle) when (lifecycle is InvalidOperationException or Win32Exception) { }
        }
        bool PostUi(Action action)
        {
            if (!ViewAlive() || !IsHandleCreated) return false;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!ViewAlive() || Environment.CurrentManagedThreadId != uiThread) return;
                    try { action(); }
                    catch (Exception error) { ReportScanError(error); }
                }));
                return true;
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { return false; }
        }

        string ParameterName(ScenarioEffectAssignment entry, string field, string value)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return value.Contains('*') ? "变量引用" : "原始参数";
            if (field == "EffectId") return NameOf("effect-names", id);
            if (field is "ItemId" or "WeaponId" or "ArmorId" or "AccessoryId") return id == 255 ? "空物品槽" : OptionsFor("items").FirstOrDefault(o => o.Id == id)?.Name ?? "未确认";
            if (field == "TargetId")
            {
                if (entry.SubFunction == 10) return NameOf("persons", id);
                if (!entry.LayoutVerified || !int.TryParse(entry.Parameters.GetValueOrDefault("Slot"), out var slot)) return "槽位语义未确认";
                return slot < entry.CharacterSlots ? NameOf("persons", id) : NameOf("detailed-jobs", id);
            }
            if (field == "Slot" && entry.SubFunction == 5 && entry.LayoutVerified)
                return id < entry.CharacterSlots ? $"人物槽 {id + 1}" : $"兵种槽 {id - entry.CharacterSlots + 1}";
            return "";
        }
        string Binding(ScenarioEffectAssignment entry)
        {
            string Display(string key) => entry.Parameters.TryGetValue(key, out var value) ?
                $"{value} {ParameterName(entry, key, value)}" : key == "Value" ? "0" : "";
            var assignment = entry.SubFunction == 5 ? $"{Display("Slot")} → {Display("TargetId")}；值 {Display("Value")}" :
                entry.Kind == "人物专属" ? $"{Display("TargetId")} + {Display("ItemId")}；值 {Display("Value")}" :
                $"{Display("WeaponId")} / {Display("ArmorId")} / {Display("AccessoryId")}；值 {Display("Value")}";
            return $"{Display("EffectId")}；{assignment}";
        }
        bool CommitParameters()
        {
            if (!ViewAlive() || loading || committing || selected == null || !selected.IsWritable || parameters.IsDisposed) return true;
            committing = true;
            try
            {
                parameters.EndEdit();
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (DataGridViewRow row in parameters.Rows)
                    if (row.Tag is string field) values[field] = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture) ?? "";
                if (values.All(pair => pair.Value == selected.Parameters.GetValueOrDefault(pair.Key, pair.Key == "Value" ? "0" : ""))) return true;
                service.ApplyUpdate(new(selected.Key, values)); _errors.SetError(parameters, "");
                selected = service.Read(false).First(e => e.Key == selected.Key);
                entries = service.Read(false);
                if (grid.CurrentRow != null) { grid.CurrentRow.Tag = selected; grid.CurrentRow.Cells["Binding"].Value = Binding(selected); }
                OnEdited(); return true;
            }
            catch (Exception ex)
            {
                ShowEditorError(parameters, ex.Message, false); return false;
            }
            finally { committing = false; }
        }
        void LoadSelected()
        {
            if (!ViewAlive() || loading || grid.IsDisposed) return;
            var next = grid.CurrentRow?.Tag as ScenarioEffectAssignment;
            if (selected?.Key != next?.Key && !CommitParameters()) return;
            loading = true;
            try
            {
                selected = next == null ? null : service.Read(false).FirstOrDefault(e => e.Key == next.Key) ?? next;
                parameters.ReadOnly = selected?.IsWritable != true;
                parameters.Rows.Clear(); remarks.Text = selected == null ? "" : selected.Remarks +
                    (selected.Diagnostic.Length > 0 ? Environment.NewLine + Environment.NewLine + selected.Diagnostic : "");
                if (selected == null) return;
                foreach (var field in new[] { "EffectId", "Slot", "TargetId", "ItemId", "WeaponId", "ArmorId", "AccessoryId", "Value" })
                {
                    var exists = selected.Parameters.TryGetValue(field, out var value);
                    var optionalValue = field == "Value" && selected.SubFunction == 5 && selected.LayoutVerified && selected.CharacterSlots == 4;
                    if (!exists && !optionalValue) continue;
                    value ??= "0";
                    var row = parameters.Rows[parameters.Rows.Add(ScenarioParameterLabels[field], value, ParameterName(selected, field, value))]; row.Tag = field;
                }
            }
            finally { loading = false; }
        }
        void RefreshRows()
        {
            if (!ViewAlive() || grid.IsDisposed || Volatile.Read(ref busy)) return;
            if (Environment.CurrentManagedThreadId != uiThread) { QueueRefresh(); return; }
            var key = selected?.Key; loading = true;
            try
            {
                entries = service.Read(false); grid.Rows.Clear();
                foreach (var entry in entries)
                {
                    var effectMatches = int.TryParse(entry.Parameters.GetValueOrDefault("EffectId"), out var effect) && effect == _id;
                    if (!all.Checked && !effectMatches) continue;
                    var binding = Binding(entry);
                    if (filter.Text.Length > 0 && !($"{entry.RelativePath}\n{entry.Remarks}\n{binding}").Contains(filter.Text, StringComparison.CurrentCultureIgnoreCase)) continue;
                    var index = grid.Rows.Add(entry.Kind, binding, entry.SourceDisplay, entry.Remarks.Replace("\r", " ").Replace("\n", " "));
                    grid.Rows[index].Tag = entry;
                    if (entry.Key == key) grid.CurrentCell = grid.Rows[index].Cells[0];
                }
                progress.Text = $"{grid.Rows.Count} 条" + (service.Diagnostics.Count > 0 ? $"；{service.Diagnostics.Count} 个文件未解析" : "");
                _diagnosticTips.SetToolTip(progress, string.Join(Environment.NewLine, service.Diagnostics));
            }
            finally { loading = false; }
            LoadSelected();
        }
        void StartScan()
        {
            if (!ViewAlive() || !IsHandleCreated || Volatile.Read(ref busy) || !CommitParameters()) return;
            busy = true; scan.Enabled = false; progress.Text = "正在扫描…";
            var generation = ++scanUiGeneration;
            // VisibleChanged/HandleCreated may run before WinForms has installed
            // its SynchronizationContext. Never rely on an await continuation for UI.
            _ = Task.Run(() => service.Scan(token), token).ContinueWith(task =>
            {
                var error = task.Exception?.GetBaseException(); // Observe every task failure.
                Volatile.Write(ref busy, false);
                PostUi(() =>
                {
                    if (generation != scanUiGeneration) return;
                    scan.Enabled = true;
                    RefreshRows();
                    if (error != null && error is not OperationCanceledException) ReportScanError(error);
                    else if (!task.IsCanceled && !service.HasScanResult) QueueRefresh();
                });
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        void EnsureIndex()
        {
            if (!ViewAlive() || !IsHandleCreated || !Visible || _kind != BusinessPageKind.Effects) return;
            scan.Enabled = !Volatile.Read(ref busy);
            if (!service.HasScanResult) StartScan(); else RefreshRows();
        }
        void QueueRefresh()
        {
            if (!ViewAlive() || !IsHandleCreated || Interlocked.CompareExchange(ref refreshQueued, 1, 0) != 0) return;
            if (!PostUi(() =>
                {
                    Interlocked.Exchange(ref refreshQueued, 0);
                    selected = null;
                    EnsureIndex();
                })) Interlocked.Exchange(ref refreshQueued, 0);
        }
        scan.Click += (_, _) => StartScan();
        all.CheckedChanged += (_, _) => { if (CommitParameters()) RefreshRows(); };
        filter.TextChanged += (_, _) => { if (!parameters.IsCurrentCellInEditMode) RefreshRows(); };
        grid.SelectionChanged += (_, _) => LoadSelected();
        parameters.CellEndEdit += (_, _) => { if (CommitParameters() && selected != null)
            foreach (DataGridViewRow row in parameters.Rows) if (row.Tag is string field)
                row.Cells[2].Value = ParameterName(selected, field, Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture) ?? ""); };
        parameters.DataError += (_, e) => e.ThrowException = false;
        page.Enter += (_, _) => QueueRefresh();
        EventHandler moduleShown = (_, _) => QueueRefresh();
        EventHandler nativeChanged = (_, _) => QueueRefresh();
        VisibleChanged += moduleShown;
        HandleCreated += moduleShown;
        service.Changed += nativeChanged;
        page.Disposed += (_, _) =>
        {
            VisibleChanged -= moduleShown; HandleCreated -= moduleShown; service.Changed -= nativeChanged;
            scanUiGeneration++;
            cancellation.Cancel(); cancellation.Dispose();
        };
        _flushers.Add(CommitParameters);
        _entityRebinders.Add(() => { selected = null; if (ViewAlive() && Visible) { if (service.HasScanResult) RefreshRows(); else QueueRefresh(); } });
        _pendingChecks.Add(() => service.HasPendingChanges);
        QueueRefresh();
    }
}
