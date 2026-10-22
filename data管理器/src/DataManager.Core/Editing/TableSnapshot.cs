using System.Data;

namespace DataManager.Core.Editing;

public sealed class TableSnapshot
{
    private string[] _columns = [];
    private object?[][] _rows = [];

    public static TableSnapshot Capture(DataTable table)
    {
        // ItemArray copies a row in one pass. A dictionary entry for every
        // scalar costs far more memory than the entire 1 MB extension file.
        return new TableSnapshot
        {
            _columns = table.Columns.Cast<DataColumn>().Select(column => CanRestore(column) ? column.ColumnName : string.Empty).ToArray(),
            _rows = table.Rows.Cast<DataRow>().Select(row => row.ItemArray).ToArray()
        };
    }

    public void Restore(DataTable table)
    {
        var columns = _columns.Select((name, ordinal) => (Column: name.Length == 0 ? null : table.Columns[name], Ordinal: ordinal))
            .Where(item => item.Column != null && CanRestore(item.Column)).ToArray();
        for (var rowIndex = 0; rowIndex < Math.Min(_rows.Length, table.Rows.Count); rowIndex++)
        {
            var row = table.Rows[rowIndex];
            foreach (var item in columns)
            {
                var value = _rows[rowIndex][item.Ordinal] ?? DBNull.Value;
                if (!Equals(row[item.Column!], value)) row[item.Column!] = value;
            }
        }
    }

    // Read-only labels and expression columns are not part of an editable draft.
    private static bool CanRestore(DataColumn column)
        => column.ColumnName != "ID" && !column.ReadOnly && !column.ExtendedProperties.Contains("Derived");
}
