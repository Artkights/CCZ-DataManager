using System.Data;
using System.Globalization;
using System.ComponentModel;
using DataManager.Core.Domain;
using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly LookupIndexCache _referenceLookup = new();
    private readonly Dictionary<(TableDocument Document, string Field), ReferenceOptionsEntry> _referenceOptions = [];
    private string? _itemEffectNameIdentity;
    private Dictionary<int, string>? _itemEffectNames;

    private void ResetReferenceChoices()
    {
        foreach (var entry in _referenceOptions.Values) entry.Dispose();
        _referenceOptions.Clear();
        _referenceLookup.Prune([]);
        _itemEffectNameIdentity = null; _itemEffectNames = null;
    }

    private void ConfigureReferenceColumns(TableDocument document)
    {
        var current = _workspace.Documents.Values.ToHashSet();
        _referenceLookup.Prune(current);
        foreach (var key in _referenceOptions.Keys.Where(key => !current.Contains(key.Document)).ToArray())
        {
            _referenceOptions[key].Dispose(); _referenceOptions.Remove(key);
        }
        foreach (var field in document.Definition.Fields.Where(field => field.Policy == FieldPolicy.Editable && !string.IsNullOrWhiteSpace(field.ReferenceTable)))
        {
            if (!_grid.Columns.Contains(field.Name)) continue;
            var oldColumn = _grid.Columns[field.Name];
            var choices = GetReferenceChoices(document, field);
            if (oldColumn is DataGridViewComboBoxColumn existing)
            {
                if (!ReferenceEquals(existing.DataSource, choices)) existing.DataSource = choices;
                continue;
            }
            var displayIndex = oldColumn.DisplayIndex;
            var combo = new DataGridViewComboBoxColumn
            {
                CellTemplate = document.Definition.Key == "persons" && PersonEquipmentService.CategoryForField(field.Name).HasValue
                    ? new EquipmentReferenceCell() : new DataGridViewComboBoxCell(),
                Name = field.Name, HeaderText = oldColumn.HeaderText, DataPropertyName = field.Name,
                DataSource = choices, ValueMember = nameof(ReferenceChoice.Value), DisplayMember = nameof(ReferenceChoice.Display),
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton, FlatStyle = FlatStyle.Flat,
                SortMode = DataGridViewColumnSortMode.Automatic, ToolTipText = $"引用表：{field.ReferenceTable}",
                Width = oldColumn.Width, ReadOnly = oldColumn.ReadOnly
            };
            _grid.Columns.Remove(oldColumn); _grid.Columns.Add(combo);
            combo.DisplayIndex = displayIndex;
        }
    }

    private ReferenceChoice[] GetReferenceChoices(TableDocument document, TableFieldDefinition field)
    {
        if (document.Definition.Key == "persons" && PersonEquipmentService.CategoryForField(field.Name).HasValue)
            return PersonEquipmentService.GetOptions(_workspace.Documents, field.Name, field.Minimum, field.Maximum)
                .Select(option => new ReferenceChoice(option.Id, option.Display)).ToArray();
        var key = (document, field.Name);
        if (!_referenceOptions.TryGetValue(key, out var entry))
            _referenceOptions[key] = entry = new ReferenceOptionsEntry(document.Data, field);
        var nativeItemNames = field.Name == "装备特效号" && document.Definition.Module == "宝物";
        var dependencies = nativeItemNames ? [] : ResolveReferenceDocuments(field.ReferenceTable!)
            .Where(referenced => referenced.Data.Columns.Contains("名称"))
            .Select(_referenceLookup.Options).ToArray();
        Dictionary<int, string>? nativeNames = null;
        var nativeIdentity = string.Empty;
        if (nativeItemNames)
        {
            // Both the fixed 6.6 slots and packed 6.5 pools live in the
            // executable; the service resolves each pool's native layout.
            var paths = _workspace.Definitions.Where(definition => definition.Key.StartsWith("item-effect-names", StringComparison.Ordinal))
                .Select(definition => ProjectFileLocator.Resolve(_workspace.RootPath, definition.FileName))
                .Append(ProjectFileLocator.Resolve(_workspace.RootPath, "Ekd5.exe"))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            nativeIdentity = _workspace.RootPath + "|" + _workspace.ActiveProfile.ProfileId + "|" + _workspace.ActiveProfile.VariantId + "|" +
                string.Join('|', paths.Select(path =>
                {
                    var info = new FileInfo(path);
                    return $"{path}:{(info.Exists ? info.Length : -1)}:{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
                }));
            if (_itemEffectNames == null || _itemEffectNameIdentity != nativeIdentity)
            {
                _itemEffectNames = new EffectNamePoolService().BuildLookup(_workspace, EffectNameChannel.Item)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                _itemEffectNames[255] = "无";
                _itemEffectNameIdentity = nativeIdentity;
            }
            nativeNames = _itemEffectNames;
        }
        var actualValues = entry.ActualValues();
        if (entry.Choices != null && entry.NativeIdentity == nativeIdentity && ReferenceEquals(entry.Values, actualValues) &&
            entry.Dependencies.Length == dependencies.Length && entry.Dependencies.Zip(dependencies).All(pair => ReferenceEquals(pair.First, pair.Second)))
            return entry.Choices;
        var names = nativeNames ?? dependencies.SelectMany(options => options)
            .GroupBy(option => option.Id).ToDictionary(group => group.Key, group => group.Last().Name);
        var values = field.Maximum - field.Minimum <= 4096
            ? Enumerable.Range(checked((int)field.Minimum), checked((int)(field.Maximum - field.Minimum + 1)))
            : names.Keys.Concat(actualValues).Concat([1024, 1025, 65535])
                .Where(value => value >= field.Minimum && value <= field.Maximum).Distinct().Order();
        var choices = values.Select(value => new ReferenceChoice(value,
            names.TryGetValue(value, out var name) && !string.IsNullOrWhiteSpace(name) ? $"{value}：{name}"
                : value is 255 or 1024 or 1025 or 65535 ? $"{value}：空值/保留" : $"{value}：未确认")).ToArray();
        if (entry.Choices == null || !entry.Choices.SequenceEqual(choices)) entry.Choices = choices;
        entry.Dependencies = dependencies; entry.NativeIdentity = nativeIdentity; entry.Values = actualValues;
        return entry.Choices;
    }

    private sealed class EquipmentReferenceCell : DataGridViewComboBoxCell
    {
        public override void InitializeEditingControl(int rowIndex, object? initialFormattedValue, DataGridViewCellStyle cellStyle)
        {
            base.InitializeEditingControl(rowIndex, initialFormattedValue, cellStyle);
            if (DataGridView?.EditingControl is not DataGridViewComboBoxEditingControl editor || Value == null || Value == DBNull.Value ||
                DataSource is not ReferenceChoice[] choices) return;
            var id = Convert.ToInt32(Value, CultureInfo.InvariantCulture);
            if (choices.Any(choice => choice.Value == id)) return;
            editor.DropDownStyle = ComboBoxStyle.DropDown;
            editor.Text = $"{id}：原值（类别不匹配）";
            editor.EditingControlValueChanged = false;
        }

        protected override object? GetFormattedValue(object? value, int rowIndex, ref DataGridViewCellStyle cellStyle,
            TypeConverter? valueTypeConverter, TypeConverter? formattedValueTypeConverter, DataGridViewDataErrorContexts context)
        {
            if (value != null && value != DBNull.Value && DataSource is ReferenceChoice[] choices &&
                Convert.ToInt32(value, CultureInfo.InvariantCulture) is var id && choices.All(choice => choice.Value != id))
                return $"{id}：原值（类别不匹配）";
            return base.GetFormattedValue(value, rowIndex, ref cellStyle, valueTypeConverter, formattedValueTypeConverter, context);
        }
    }

    private sealed class ReferenceOptionsEntry : IDisposable
    {
        private readonly DataTable _table;
        private readonly string _field;
        private readonly bool _usesActualValues;
        private int[]? _actualValues;
        public ReferenceChoice[]? Choices;
        public IReadOnlyList<IdNameOption>[] Dependencies = [];
        public string NativeIdentity = string.Empty;
        public int[]? Values;

        public ReferenceOptionsEntry(DataTable table, TableFieldDefinition field)
        {
            _table = table; _field = field.Name;
            _usesActualValues = field.Maximum - field.Minimum > 4096;
            if (!_usesActualValues) return;
            table.ColumnChanged += ColumnChanged; table.RowChanged += RowChanged; table.RowDeleted += RowChanged; table.TableCleared += Cleared;
        }

        public int[] ActualValues() => !_usesActualValues ? Array.Empty<int>() : _actualValues ??=
            _table.Rows.Cast<DataRow>().Where(row => row.RowState != DataRowState.Deleted)
                .Select(row => Convert.ToInt32(row[_field], CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
        private void ColumnChanged(object? sender, DataColumnChangeEventArgs args) { if (args.Column?.ColumnName == _field) _actualValues = null; }
        private void RowChanged(object? sender, DataRowChangeEventArgs args) { if (args.Action is DataRowAction.Add or DataRowAction.Delete or DataRowAction.Rollback) _actualValues = null; }
        private void Cleared(object? sender, DataTableClearEventArgs args) => _actualValues = null;
        public void Dispose()
        {
            if (!_usesActualValues) return;
            _table.ColumnChanged -= ColumnChanged; _table.RowChanged -= RowChanged; _table.RowDeleted -= RowChanged; _table.TableCleared -= Cleared;
        }
    }
}
