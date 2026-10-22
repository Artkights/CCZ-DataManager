using System.ComponentModel;

namespace ImageManager.App;

/// <summary>Keep the current row for navigation without painting an unfocused editor as an active selection.</summary>
public static class SelectionAppearance
{
    public static void Configure(DataGridView grid, bool preserveCurrentRow = false)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.EnableHeadersVisualStyles = false;
        void UpdateColors(bool? focused = null)
        {
            if (grid.IsDisposed) return;
            var active = focused ?? grid.ContainsFocus;
            grid.DefaultCellStyle.SelectionBackColor = active || SystemInformation.HighContrast
                ? SystemColors.Highlight : Color.FromArgb(235, 238, 242);
            grid.DefaultCellStyle.SelectionForeColor = active || SystemInformation.HighContrast
                ? SystemColors.HighlightText : SystemColors.ControlText;
            var columnHeader = grid.ColumnHeadersDefaultCellStyle;
            columnHeader.SelectionBackColor = columnHeader.BackColor.IsEmpty ? SystemColors.Control : columnHeader.BackColor;
            columnHeader.SelectionForeColor = columnHeader.ForeColor.IsEmpty ? SystemColors.ControlText : columnHeader.ForeColor;
            var rowHeader = grid.RowHeadersDefaultCellStyle;
            rowHeader.SelectionBackColor = rowHeader.BackColor.IsEmpty ? SystemColors.Control : rowHeader.BackColor;
            rowHeader.SelectionForeColor = rowHeader.ForeColor.IsEmpty ? SystemColors.ControlText : rowHeader.ForeColor;
            grid.Invalidate();
        }
        void ClearInitialSelection()
        {
            if (preserveCurrentRow || grid.IsDisposed || grid.ContainsFocus || grid.IsCurrentCellInEditMode) return;
            grid.CurrentCell = null;
            grid.ClearSelection();
        }
        grid.Enter += (_, _) => UpdateColors(true);
        grid.Leave += (_, _) => UpdateColors(false);
        grid.DataBindingComplete += (_, args) =>
        {
            if (args.ListChangedType is ListChangedType.Reset or ListChangedType.PropertyDescriptorAdded)
                ClearInitialSelection();
            UpdateColors();
        };
        grid.HandleCreated += (_, _) => { ClearInitialSelection(); UpdateColors(); };
        grid.VisibleChanged += (_, _) =>
        {
            if (grid.Visible) { ClearInitialSelection(); UpdateColors(); }
        };
        ClearInitialSelection();
        UpdateColors();
    }
}
