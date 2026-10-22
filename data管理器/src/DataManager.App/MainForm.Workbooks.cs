using DataManager.Core.Domain;
using DataManager.Core.Workbooks;

namespace DataManager.App;

public sealed partial class MainForm
{
    private bool _workbookBusy;

    private async void ImportCsv()
    {
        if (_workbookBusy) return;
        if (!_businessEditor.Visible || _businessKind is not { } kind) { ImportLegacyCsv(); return; }
        if (!PreparePendingEdits() || !_businessEditor.Flush()) return;
        var format = ChoosePageTransferFormat(import: true);
        if (format == DialogResult.Retry) { ImportLegacyCsv(); return; }
        if (format != DialogResult.OK) return;
        using var dialog = new OpenFileDialog { Filter = "业务页工作簿 (*.xlsx)|*.xlsx", Title = "导入当前业务页工作簿", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var service = new PageWorkbookService(_workspace);
            var preview = await RunWorkbookIo(() => service.PreviewImport(kind, dialog.FileName), "正在预检工作簿…");
            if (preview.ChangedCells == 0) { SetStatus("工作簿与当前草稿相同，没有需要导入的字段。"); return; }
            using var review = new WorkbookImportPreviewDialog(preview);
            if (review.ShowDialog(this) != DialogResult.OK) return;
            var count = service.ApplyImport(preview);
            RefreshSavedNames(); RefreshBusinessSelection(); RefreshDirtyState();
            SetStatus($"已将 {count} 个工作簿字段加入一条可撤销草稿；保存后写回游戏文件。");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "工作簿导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async void ExportCsv()
    {
        if (_workbookBusy) return;
        if (!_businessEditor.Visible || _businessKind is not { } kind) { ExportLegacyCsv(); return; }
        if (!PreparePendingEdits() || !_businessEditor.Flush()) return;
        var format = ChoosePageTransferFormat(import: false);
        if (format == DialogResult.Retry) { ExportLegacyCsv(); return; }
        if (format != DialogResult.OK) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "业务页工作簿 (*.xlsx)|*.xlsx", DefaultExt = "xlsx", AddExtension = true,
            FileName = string.Concat((_moduleTabs.SelectedTab?.Text ?? kind.ToString()).Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)) + ".xlsx",
            Title = "导出当前业务页工作簿"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var service = new PageWorkbookService(_workspace);
            await RunWorkbookIo(() => { service.Export(kind, dialog.FileName); return true; }, "正在导出工作簿…");
            SetStatus($"当前业务页已导出为一个工作簿：{dialog.FileName}");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "工作簿导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private DialogResult ChoosePageTransferFormat(bool import)
    {
        using var chooser = new Form
        {
            Text = import ? "导入格式" : "导出格式", StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(420, 160),
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.Controls.Add(new Label { Text = "一个业务页使用一个工作簿，子页对应工作表。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var workbook = new Button { Text = "Excel 工作簿 (.xlsx)", DialogResult = DialogResult.OK, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 4) };
        var legacy = new Button { Text = "兼容旧 CSV 目录", DialogResult = DialogResult.Retry, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0) };
        layout.Controls.Add(workbook, 0, 1); layout.Controls.Add(legacy, 0, 2); chooser.Controls.Add(layout);
        chooser.AcceptButton = workbook;
        return chooser.ShowDialog(this);
    }

    private async Task<T> RunWorkbookIo<T>(Func<T> operation, string status)
    {
        _workbookBusy = true; UseWaitCursor = true; SetStatus(status);
        Enabled = false;
        try { return await Task.Run(operation); }
        finally { Enabled = true; UseWaitCursor = false; _workbookBusy = false; }
    }

    internal async Task<int> ImportWorkbookForSmokeTest(BusinessPageKind kind, string path, bool apply)
    {
        if (!PreparePendingEdits() || !_businessEditor.Flush()) throw new InvalidOperationException("当前输入验证失败。");
        var service = new PageWorkbookService(_workspace);
        var preview = await RunWorkbookIo(() => service.PreviewImport(kind, path), "正在预检工作簿…");
        if (!apply) return preview.ChangedCells;
        var count = service.ApplyImport(preview);
        RefreshSavedNames(); RefreshBusinessSelection(); RefreshDirtyState(); return count;
    }

    internal async Task ExportWorkbookForSmokeTest(BusinessPageKind kind, string path)
    {
        if (!PreparePendingEdits() || !_businessEditor.Flush()) throw new InvalidOperationException("当前输入验证失败。");
        await RunWorkbookIo(() => { new PageWorkbookService(_workspace).Export(kind, path); return true; }, "正在导出工作簿…");
    }
}

internal sealed class WorkbookImportPreviewDialog : Form
{
    internal WorkbookImportPreviewDialog(PageWorkbookImportPreview preview)
    {
        Text = $"导入预览 · {preview.ChangedCells} 个字段"; StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1040, 640); MinimumSize = new Size(780, 420); ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        foreach (var (property, caption, weight) in new[]
        {
            ("Sheet", "工作表", 19f), ("Cell", "单元格", 9f), ("Id", "编号", 9f),
            ("Field", "字段", 19f), ("OldValue", "当前值", 22f), ("NewValue", "导入值", 22f)
        }) grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = caption, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.VirtualMode = true;
        grid.CellValueNeeded += (_, args) =>
        {
            if (args.RowIndex < 0 || args.RowIndex >= preview.Changes.Count) return;
            var change = preview.Changes[args.RowIndex];
            args.Value = args.ColumnIndex switch { 0 => change.Sheet, 1 => change.Cell, 2 => change.Id, 3 => change.Field, 4 => change.OldValue, _ => change.NewValue };
        };
        grid.RowCount = preview.ChangedCells;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        var confirm = new Button { Text = "确认加入草稿", DialogResult = DialogResult.OK, AutoSize = true, Height = 30 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true, Height = 30 };
        footer.Controls.Add(confirm); footer.Controls.Add(cancel);
        Controls.Add(grid); Controls.Add(footer); AcceptButton = confirm; CancelButton = cancel;
    }
}
