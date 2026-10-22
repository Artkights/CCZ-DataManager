using DataManager.Core.Domain;
using DataManager.Core.Editing;
using ImageManager.App;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private AccessoryGroupDraftService? _accessoryGroupDraft;
    private sealed record AccessoryGroupSnapshot(IReadOnlyList<IReadOnlyList<int>> Groups, int SelectedIndex);

    private static AccessoryGroupSnapshot CaptureAccessoryDraft(AccessoryGroupDraftService draft)
        => new(draft.Snapshot(), draft.SelectedIndex);

    private void RestoreAccessoryDraft(AccessoryGroupDraftService draft, AccessoryGroupSnapshot snapshot)
    {
        draft.Groups.Clear(); draft.Groups.AddRange(snapshot.Groups.Select(group => group.ToList()));
        draft.SelectedIndex = Math.Clamp(snapshot.SelectedIndex, 0, Math.Max(0, draft.Groups.Count - 1));
        if (ReferenceEquals(_accessoryGroupDraft, draft)) { _accessoryDraftRefresh?.Invoke(); NotifyPendingChanges(); }
    }

    private void RecordAccessoryEdit(string description, AccessoryGroupDraftService draft, AccessoryGroupSnapshot before)
    {
        var after = CaptureAccessoryDraft(draft);
        if (AccessoryGroupsSignature(before.Groups) == AccessoryGroupsSignature(after.Groups)) return;
        _workspace.Commands.Push(new DelegateEditCommand(description,
            () => RestoreAccessoryDraft(draft, before), () => RestoreAccessoryDraft(draft, after)));
        NotifyPendingChanges();
    }

    private static string AccessoryGroupsSignature(IEnumerable<IReadOnlyList<int>> groups)
        => string.Join(';', groups.Select(group => string.Join(',', group)));

    private static string AccessorySourceSignature(AccessoryJobGroupReadResult source)
        => $"{source.FileOffset}:{source.WritableLength}:{AccessoryGroupsSignature(source.Groups.Select(group => group.JobSeriesIds))}";

    private void AddAccessoryGroupEditor()
    {
        var page = NewTab("辅助分组");
        page.AutoScroll = false; page.Padding = new Padding(6);
        try
        {
            var service = new AccessoryJobGroupService();
            var csv = new AccessoryJobGroupCsvService();
            var source = service.Read(_workspace);
            var projectKey = $"{Path.GetFullPath(_workspace.RootPath)}|{_workspace.ActiveProfile.ProfileId}|{_workspace.ActiveProfile.VariantId}|{_workspace.ActiveProfile.LayoutRevision}";
            if (_accessoryGroupDraft == null || !_accessoryGroupDraft.ProjectKey.Equals(projectKey, StringComparison.OrdinalIgnoreCase))
                _accessoryGroupDraft = AccessoryGroupDraftService.For(_workspace);
            else if (!_accessoryGroupDraft.HasChanges && _accessoryGroupDraft.SourceSignature != AccessorySourceSignature(source))
                _accessoryGroupDraft.Reset(source);
            var draft = _accessoryGroupDraft;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4), Margin = Padding.Empty };
            Button ActionButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(100, 30), Margin = new Padding(0, 0, 8, 0) };
            var apply = ActionButton("校验保存");
            var add = ActionButton("新增空白分组");
            var importCsv = ActionButton("导入 CSV");
            var exportCsv = ActionButton("导出 CSV");
            bar.Controls.AddRange([apply, add, importCsv, exportCsv]);
            root.Controls.Add(bar, 0, 0); page.Controls.Add(root);
            var capacity = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(3, 5, 3, 1) };
            root.Controls.Add(capacity, 0, 2);
            int PayloadLength() => 1 + draft.Groups.Sum(group => group.Count + 1);
            void RefreshCapacity()
            {
                var used = PayloadLength(); var maximum = source.WritableLength - 1;
                var empty = draft.Groups.Count(group => group.Count == 0);
                capacity.Text = $"已用 {used} / {maximum} 字节 · 剩余 {maximum - used} 字节（另保留 1 字节边界）"
                    + (empty > 0 ? $" · {empty} 个空分组需填写或删除后才能保存" : draft.HasChanges ? " · 未保存草稿" : " · 已保存");
                capacity.ForeColor = empty > 0 || used > maximum ? Color.Firebrick : maximum - used <= 8 ? Color.DarkOrange : Color.DimGray;
                NotifyPendingChanges();
            }

            var body = Columns(36, 64); root.Controls.Add(body, 0, 1);
            var listGroup = new Panel { AccessibleName = "辅助分组", Dock = DockStyle.Fill, Padding = new Padding(6), Margin = new Padding(0, 2, 6, 0) };
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = false, AllowUserToResizeRows = false, AllowUserToResizeColumns = false,
                RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            SelectionAppearance.Configure(grid, preserveCurrentRow: true);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "分组编号", HeaderText = "分组编号", Width = 78, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "兵种系名称", HeaderText = "兵种系名称", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 100, SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, Padding = new Padding(3, 5, 3, 5) }
            });
            listGroup.Controls.Add(grid); body.Controls.Add(listGroup, 0, 0);

            var choicesGroup = new Panel { AccessibleName = "兵种系", Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(4, 2, 0, 0) };
            var choices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 10, Margin = Padding.Empty };
            for (var column = 0; column < 4; column++) choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            for (var row = 0; row < 10; row++) choices.RowStyles.Add(new RowStyle(SizeType.Percent, 10));
            choicesGroup.Controls.Add(choices); body.Controls.Add(choicesGroup, 1, 0);
            var names = Enumerable.Range(0, 40).Select(id => NameOf("job-series", id)).ToArray();
            string SeriesName(int id) => id >= 0 && id < names.Length && !string.IsNullOrWhiteSpace(names[id]) ? names[id] : "未命名";
            string Summary(IReadOnlyList<int> ids) => string.Join('、', ids.Select(SeriesName));
            var checkboxes = new CheckBox[40];
            var updating = false;

            void Report(string message, bool error = false)
            {
                _status.Text = message; _status.Visible = true;
                if (!grid.IsDisposed) _errors.SetError(grid, error ? message : string.Empty);
            }
            void LoadSelection()
            {
                if (updating) return;
                var selected = grid.CurrentCell?.RowIndex ?? -1;
                if (selected >= 0) draft.SelectedIndex = selected;
                updating = true;
                try
                {
                    choicesGroup.AccessibleName = selected >= 0 ? $"分组 {selected} 的兵种系" : "兵种系";
                    for (var id = 0; id < checkboxes.Length; id++)
                    {
                        checkboxes[id].Enabled = selected >= 0 && selected < draft.Groups.Count;
                        checkboxes[id].Checked = selected >= 0 && selected < draft.Groups.Count && draft.Groups[selected].Contains(id);
                    }
                }
                finally { updating = false; }
            }
            void RefreshRows(int selected)
            {
                updating = true;
                try
                {
                    grid.Rows.Clear();
                    for (var index = 0; index < draft.Groups.Count; index++) grid.Rows.Add(index, Summary(draft.Groups[index]));
                    if (grid.Rows.Count > 0)
                    {
                        selected = Math.Clamp(selected, 0, grid.Rows.Count - 1);
                        grid.CurrentCell = grid.Rows[selected].Cells[0]; grid.Rows[selected].Selected = true;
                        draft.SelectedIndex = selected;
                    }
                }
                finally { updating = false; }
                LoadSelection();
                RefreshCapacity();
            }
            for (var id = 0; id < checkboxes.Length; id++)
            {
                var jobSeriesId = id;
                var check = new CheckBox
                {
                    Name = $"辅助分组兵种系{id}", Text = SeriesName(id), Dock = DockStyle.Fill,
                    AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 1, 3, 1)
                };
                checkboxes[id] = check; choices.Controls.Add(check, id / 10, id % 10);
                check.CheckedChanged += (_, _) =>
                {
                    if (updating || grid.CurrentCell is not { RowIndex: >= 0 } cell || cell.RowIndex >= draft.Groups.Count) return;
                    var members = draft.Groups[cell.RowIndex];
                    if (check.Checked && !members.Contains(jobSeriesId) && PayloadLength() >= source.WritableLength - 1)
                    {
                        updating = true; try { check.Checked = false; } finally { updating = false; }
                        Report("辅助分组已达到安全字节容量，本次勾选已拒绝。", true); return;
                    }
                    var before = CaptureAccessoryDraft(draft);
                    if (check.Checked) { if (!members.Contains(jobSeriesId)) members.Add(jobSeriesId); }
                    else members.RemoveAll(member => member == jobSeriesId);
                    grid.Rows[cell.RowIndex].Cells[1].Value = Summary(members);
                    _errors.SetError(grid, string.Empty);
                    RefreshCapacity();
                    RecordAccessoryEdit($"编辑辅助分组 {cell.RowIndex} 的兵种系", draft, before);
                };
            }
            // SelectionChanged can fire before CurrentCell is moved; bind
            // checkboxes only after the new row is the actual current row.
            grid.CurrentCellChanged += (_, _) => LoadSelection();
            add.Click += (_, _) =>
            {
                if (PayloadLength() + 2 > source.WritableLength - 1) { Report("剩余容量不足以新增一个有效分组（至少需 2 字节），本次新增已拒绝。", true); return; }
                var before = CaptureAccessoryDraft(draft);
                draft.Groups.Add([]); RefreshRows(draft.Groups.Count - 1); _errors.SetError(grid, string.Empty);
                RecordAccessoryEdit("新增辅助分组", draft, before);
            };

            void DeleteSelected()
            {
                if (grid.CurrentCell is not { RowIndex: >= 0 } cell || cell.RowIndex >= draft.Groups.Count) return;
                var before = CaptureAccessoryDraft(draft);
                var index = cell.RowIndex; draft.Groups.RemoveAt(index); RefreshRows(index);
                RecordAccessoryEdit($"删除辅助分组 {index}", draft, before);
            }
            var context = new ContextMenuStrip();
            context.Items.Add("删除选中分组", null, (_, _) => DeleteSelected());
            context.Items.Add("重新载入已保存分组", null, (_, _) =>
            {
                if (draft.HasChanges && MessageBox.Show(this, "放弃尚未保存的辅助分组修改并重新读取？", "重新载入分组", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                try
                {
                    var before = CaptureAccessoryDraft(draft);
                    draft.Reset(service.Read(_workspace)); RefreshRows(draft.SelectedIndex); _errors.SetError(grid, string.Empty);
                    RecordAccessoryEdit("重新载入已保存辅助分组", draft, before);
                }
                catch (Exception ex) { Report(ex.Message, true); }
            });
            grid.ContextMenuStrip = context; grid.Disposed += (_, _) => context.Dispose();
            grid.KeyDown += (_, args) => { if (args.KeyCode == Keys.Delete) { DeleteSelected(); args.Handled = true; args.SuppressKeyPress = true; } };
            exportCsv.Click += (_, _) =>
            {
                using var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = AccessoryJobGroupCsvService.DefaultFileName };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { csv.Export(_workspace, dialog.FileName, draft.Snapshot()); Report("辅助分组 CSV 已导出。"); }
                catch (Exception ex) { Report(ex.Message, true); }
            };
            importCsv.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog { Filter = "CSV 文件|*.csv|所有文件|*.*" };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var imported = csv.ImportDraft(_workspace, dialog.FileName);
                    if (1 + imported.Sum(group => group.Count + 1) >= source.WritableLength)
                        throw new InvalidDataException($"导入分组超过安全负载容量 {source.WritableLength - 1} 字节，当前草稿已保留。");
                    var before = CaptureAccessoryDraft(draft);
                    draft.Groups.Clear(); draft.Groups.AddRange(imported.Select(group => group.ToList()));
                    RefreshRows(0); Report("CSV 已载入辅助分组草稿，请使用顶部保存或统一保存。");
                    RecordAccessoryEdit("导入辅助分组 CSV", draft, before);
                }
                catch (Exception ex) { Report(ex.Message, true); }
            };
            apply.Click += (_, _) =>
            {
                try
                {
                    _ = GetPreparedEdits();
                    SaveRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex) { Report(ex.Message, true); }
            };
            _entityRebinders.Add(() =>
            {
                for (var id = 0; id < names.Length; id++) { names[id] = NameOf("job-series", id); checkboxes[id].Text = SeriesName(id); }
                for (var index = 0; index < draft.Groups.Count; index++) grid.Rows[index].Cells[1].Value = Summary(draft.Groups[index]);
            });
            RefreshRows(draft.SelectedIndex);
            _accessoryDraftRefresh = () => { if (!grid.IsDisposed) RefreshRows(draft.SelectedIndex); };
        }
        catch (Exception ex) { page.Controls.Add(Notice(ex.Message)); }
    }
}
