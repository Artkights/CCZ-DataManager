using System.Data;
using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class MainForm
{
    private sealed record ProjectedField(string TableKey, string FieldName, string Caption);
    private sealed record GridFieldSource(TableDocument Document, DataRow Row, int RowIndex, TableFieldDefinition Field);
    private static readonly ProjectedField[] PersonProjectedFields =
    [
        new("person-r-assign", "R形象ID", "R形象编号"),
        new("person-s-assign", "S形象ID", "S形象编号")
    ];

    private void ConfigureProjectedColumns(TableDocument document)
    {
        foreach (var column in _grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Tag is ProjectedField projected &&
                     (document.Definition.Key != "persons" || !PersonProjectedFields.Contains(projected))).ToArray())
            _grid.Columns.Remove(column);
        if (document.Definition.Key != "persons") return;
        if (_grid.Columns["Army"] is { } army) army.Visible = false;
        var position = (_grid.Columns["头像"]?.DisplayIndex ?? 1) + 1;
        foreach (var projected in PersonProjectedFields)
        {
            var available = _workspace.Documents.TryGetValue(projected.TableKey, out var source) &&
                source.Definition.Capability == TableCapability.Editable &&
                source.Definition.Fields.Any(field => field.Name == projected.FieldName && field.Policy == FieldPolicy.Editable);
            var toolTip = available ? $"按人物 ID 编辑 {source!.Definition.DisplayName}，保存时统一写入。" : "当前布局未验证此形象指定，暂不可编辑。";
            if (_grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(column => Equals(column.Tag, projected)) is { } existing)
            {
                // Returning from a cached business/range page keeps the same
                // physical table. Reuse its cells and native UIA providers;
                // removing an unchanged column disconnects every row provider.
                existing.ReadOnly = !available;
                existing.ToolTipText = toolTip;
                if (existing.DisplayIndex != position) existing.DisplayIndex = position;
                position++;
                continue;
            }
            var column = new DataGridViewColumn(new PersonAppearanceCell(this, projected))
            {
                Name = projected.Caption, HeaderText = projected.Caption, Tag = projected,
                ValueType = typeof(int), ReadOnly = !available, SortMode = DataGridViewColumnSortMode.NotSortable,
                ToolTipText = toolTip,
                DefaultCellStyle = new DataGridViewCellStyle { NullValue = "不可用" }
            };
            _grid.Columns.Add(column);
            column.DisplayIndex = position++;
        }
    }

    private GridFieldSource? ResolveGridField(int rowIndex, DataGridViewColumn column)
    {
        if (_currentDocument == null || rowIndex < 0 || rowIndex >= _grid.Rows.Count ||
            _grid.Rows[rowIndex].DataBoundItem is not DataRowView view) return null;
        if (column.Tag is ProjectedField projected)
            return ResolveProjectedField(projected, Convert.ToInt32(view.Row["ID"], CultureInfo.InvariantCulture));
        var field = _currentDocument.Definition.Fields.FirstOrDefault(field => field.Name == column.DataPropertyName);
        var physical = _currentDocument.Data.Rows.IndexOf(view.Row);
        return field == null || physical < 0 ? null : new(_currentDocument, view.Row, physical, field);
    }

    private GridFieldSource? ResolveProjectedField(ProjectedField projected, int id)
    {
        if (!_workspace.Documents.TryGetValue(projected.TableKey, out var source)) return null;
        var field = source.Definition.Fields.FirstOrDefault(field => field.Name == projected.FieldName);
        if (field == null || source.Definition.Capability == TableCapability.Unavailable) return null;
        var ordinal = id - source.Definition.BeginId;
        if ((uint)ordinal < (uint)source.Data.Rows.Count && Convert.ToInt32(source.Data.Rows[ordinal]["ID"], CultureInfo.InvariantCulture) == id)
            return new(source, source.Data.Rows[ordinal], ordinal, field);
        var match = source.Data.Rows.Cast<DataRow>().Select((row, index) => (Row: row, Index: index))
            .FirstOrDefault(pair => Convert.ToInt32(pair.Row["ID"], CultureInfo.InvariantCulture) == id);
        return match.Row == null ? null : new(source, match.Row, match.Index, field);
    }

    private bool SetProjectedValue(int rowIndex, ProjectedField projected, object? value)
    {
        if (_suppressGridEvents || _currentDocument?.Definition.Key != "persons" ||
            rowIndex < 0 || rowIndex >= _grid.Rows.Count || _grid.Rows[rowIndex].DataBoundItem is not DataRowView view) return false;
        var id = Convert.ToInt32(view.Row["ID"], CultureInfo.InvariantCulture);
        var source = ResolveProjectedField(projected, id) ?? throw new InvalidOperationException("当前人物的形象指定不可用。");
        if (source.Document.Definition.Capability != TableCapability.Editable || source.Field.Policy != FieldPolicy.Editable)
            throw new InvalidOperationException("当前形象指定未通过布局验证，不可编辑。");
        _workspace.ValidateCellValue(source.Document, source.RowIndex, source.Field.Name, value);
        var transaction = new BusinessDataService(_workspace).BeginTransaction(BusinessPageKind.Persons, $"人物 {id} {projected.Caption}");
        transaction.SetById(projected.TableKey, id, projected.FieldName, value).Apply();
        RefreshDirtyState();
        QueueQueryRefresh();
        return true;
    }

    private sealed class PersonAppearanceCell : DataGridViewTextBoxCell
    {
        private MainForm? _owner;
        private ProjectedField? _projected;
        public PersonAppearanceCell() { }
        public PersonAppearanceCell(MainForm owner, ProjectedField projected) { _owner = owner; _projected = projected; }
        public override object Clone()
        {
            var cell = (PersonAppearanceCell)base.Clone();
            cell._owner = _owner; cell._projected = _projected;
            return cell;
        }

        protected override object? GetValue(int rowIndex)
        {
            if (_owner == null || _projected == null || rowIndex < 0 || DataGridView == null || rowIndex >= DataGridView.RowCount ||
                DataGridView.Rows[rowIndex].DataBoundItem is not DataRowView view) return DBNull.Value;
            var source = _owner.ResolveProjectedField(_projected, Convert.ToInt32(view.Row["ID"], CultureInfo.InvariantCulture));
            return source == null ? DBNull.Value : source.Row[source.Field.Name];
        }

        protected override bool SetValue(int rowIndex, object? value)
        {
            if (_owner == null || _projected == null || !_owner.SetProjectedValue(rowIndex, _projected, value)) return false;
            return base.SetValue(rowIndex, value);
        }
    }

    private TableDocument PersonBatchView(TableDocument document)
    {
        var data = document.Data.Copy();
        var fields = document.Definition.Fields.ToList();
        if (data.Columns["Army"] is { } army) army.ReadOnly = true;
        foreach (var projected in PersonProjectedFields)
        {
            var column = data.Columns.Add(projected.Caption, typeof(int));
            var sourceField = _workspace.Documents.TryGetValue(projected.TableKey, out var source)
                ? source.Definition.Fields.FirstOrDefault(field => field.Name == projected.FieldName) : null;
            var field = sourceField == null ? null : sourceField with { Name = projected.Caption };
            if (field != null) { fields.Add(field); column.ExtendedProperties["Field"] = field; }
            foreach (DataRow row in data.Rows)
            {
                var binding = ResolveProjectedField(projected, Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture));
                row[column] = binding?.Row[binding.Field.Name] ?? DBNull.Value;
            }
            column.ReadOnly = source == null || source.Definition.Capability != TableCapability.Editable || field?.Policy != FieldPolicy.Editable;
        }
        return new TableDocument
        {
            Definition = document.Definition with { Fields = fields }, Data = data,
            FilePath = document.FilePath, OriginalFileBytes = document.OriginalFileBytes, OriginalFileHash = document.OriginalFileHash
        };
    }

    private void ApplyPersonBatch(TableDocument view, IReadOnlyList<int> rows, IReadOnlyList<string> columns, string operation, string input)
    {
        var transaction = new BusinessDataService(_workspace).BeginTransaction(BusinessPageKind.Persons, $"批量修改人物（{rows.Count} 行、{columns.Count} 字段）");
        foreach (var name in columns)
        {
            var column = view.Data.Columns[name] ?? throw new InvalidOperationException($"字段不存在：{name}");
            if (column.ReadOnly) throw new InvalidOperationException($"字段只读：{name}");
            var projected = PersonProjectedFields.FirstOrDefault(field => field.Caption == name);
            var field = column.ExtendedProperties["Field"] as TableFieldDefinition;
            foreach (var rowIndex in rows.Distinct())
            {
                var current = view.Data.Rows[rowIndex][column];
                var value = operation == "将首行复制到选中行" ? view.Data.Rows[rows[0]][column] : ComputePersonBatchValue(current, column.DataType, field, operation, input);
                var id = Convert.ToInt32(view.Data.Rows[rowIndex]["ID"], CultureInfo.InvariantCulture);
                transaction.SetById(projected?.TableKey ?? "persons", id, projected?.FieldName ?? name, value);
            }
        }
        transaction.Apply();
    }

    private static object ComputePersonBatchValue(object? current, Type type, TableFieldDefinition? field, string operation, string input)
    {
        if (operation == "查找替换")
        {
            var parts = input.Split("=>", 2, StringSplitOptions.None);
            if (parts.Length != 2 || parts[0].Length == 0) throw new FormatException("查找替换的格式为：查找内容=>替换内容。");
            var text = (Convert.ToString(current, CultureInfo.InvariantCulture) ?? string.Empty).Replace(parts[0], parts[1], StringComparison.Ordinal);
            return Convert.ChangeType(text, type, CultureInfo.InvariantCulture)!;
        }
        if (type == typeof(string))
        {
            if (operation != "统一赋值") throw new InvalidOperationException("文本字段只支持统一赋值或查找替换。");
            return input;
        }
        if (field == null || field.Kind is not (FieldKind.UInt8 or FieldKind.UInt16 or FieldKind.UInt32))
            throw new InvalidOperationException("当前字段不是可批量计算的数值字段。");
        var number = BinaryValueCodec.ParseInteger64(input, long.MinValue, long.MaxValue, field.Name);
        var old = Convert.ToInt64(current, CultureInfo.InvariantCulture);
        var target = operation switch
        {
            "统一赋值" => number, "数值增加" => checked(old + number), "数值减少" => checked(old - number),
            _ => throw new InvalidOperationException($"不支持的批量操作：{operation}")
        };
        return BinaryValueCodec.ParseInteger64(target.ToString(CultureInfo.InvariantCulture), field.Minimum, field.Maximum, field.Name);
    }
}
