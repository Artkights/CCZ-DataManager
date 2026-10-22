using System.Runtime.CompilerServices;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Migration;

/// <summary>Rejects changes to bytes which also represent another loaded physical field.</summary>
internal static class MigrationFieldIsolation
{
    private static readonly RecordCodecRegistry Codecs = new();
    private static readonly ConditionalWeakTable<TableDocument, TableFootprintIndex> Footprints = new();

    public static string? FindConflict(ProjectWorkspace workspace, TableDocument document, int rowIndex,
        TableFieldDefinition field, object? value)
    {
        var start = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        var row = document.OriginalFileBytes.AsSpan(start, document.Definition.RowSize);
        // The requested value still passes through the actual codec. Only the
        // footprint of the other fields is indexed; no value or write is cached.
        var patches = Codecs.Get(document.Definition.RecordCodecKey).EncodeChangedField(row, field, value);
        foreach (var patch in patches)
        {
            var index = 0;
            while (index < patch.Bytes.Length)
            {
                while (index < patch.Bytes.Length && document.OriginalFileBytes[start + patch.Offset + index] == patch.Bytes[index]) index++;
                var changedStart = index;
                while (index < patch.Bytes.Length && document.OriginalFileBytes[start + patch.Offset + index] != patch.Bytes[index]) index++;
                if (changedStart == index) continue;
                var conflict = FindChangedRangeConflict(workspace, document,
                    (long)start + patch.Offset + changedStart, (long)start + patch.Offset + index);
                if (conflict != null) return conflict;
            }
        }
        return null;
    }

    private static string? FindChangedRangeConflict(ProjectWorkspace workspace, TableDocument document, long changedStart, long changedEnd)
    {
        foreach (var other in workspace.Documents.Values)
        {
            if (other.Definition.Key == document.Definition.Key ||
                !other.FilePath.Equals(document.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
            var definition = other.Definition;
            var overlapStart = Math.Max(changedStart, definition.DataOffset);
            var overlapEnd = Math.Min(changedEnd, definition.EndOffsetExclusive);
            if (overlapStart >= overlapEnd) continue;
            var first = (overlapStart - definition.DataOffset) / definition.RowSize;
            var last = (overlapEnd - 1 - definition.DataOffset) / definition.RowSize;
            var footprints = Footprints.GetValue(other, candidate => new TableFootprintIndex(candidate.Definition));
            for (var index = first; index <= last; index++)
            {
                var otherStart = checked((int)(definition.DataOffset + index * definition.RowSize));
                var otherRow = other.OriginalFileBytes.AsSpan(otherStart, definition.RowSize);
                var otherField = footprints.Find(otherRow, overlapStart - otherStart, overlapEnd - otherStart);
                if (otherField == null) continue;
                return $"目标实际写入字节同时属于 {definition.DisplayName}（{definition.Key}）ID={definition.BeginId + index} 字段 {otherField.Name}；未经物理别名语义验证，本项不能单独迁移。";
            }
        }
        return null;
    }

    private sealed class TableFootprintIndex(TableDefinition definition)
    {
        private readonly IRecordCodec _codec = Codecs.Get(definition.RecordCodecKey);
        private readonly Dictionary<int, FieldIntervalIndex> _layouts = [];

        public TableFieldDefinition? Find(ReadOnlySpan<byte> row, long start, long end)
        {
            // Fixed codecs have one layout, Item66 has two, selected from the
            // current ORIGINAL marker byte. Replacing a snapshot therefore
            // cannot leave a stale row layout in this cache.
            var layout = RecordCodecWriteFootprints.GetLayoutKey(_codec, row);
            FieldIntervalIndex intervals;
            if (layout is { } key)
            {
                lock (_layouts)
                {
                    if (!_layouts.TryGetValue(key, out intervals!))
                        _layouts.Add(key, intervals = Build(row));
                }
            }
            else intervals = Build(row);
            return intervals.Find(start, end);
        }

        private FieldIntervalIndex Build(ReadOnlySpan<byte> row)
        {
            // ReadOnly and Preserve fields are physical aliases too, even
            // when absent from an editable DataTable.
            var ranges = new List<FieldInterval>();
            foreach (var field in definition.Fields)
            foreach (var range in RecordCodecWriteFootprints.Get(_codec, row, field))
                if (range.Length > 0) ranges.Add(new FieldInterval(range.Offset, checked(range.Offset + range.Length), field));
            return new FieldIntervalIndex(ranges);
        }
    }

    private sealed record FieldInterval(int Start, int End, TableFieldDefinition Field);

    private sealed class FieldIntervalIndex
    {
        private readonly FieldInterval[] _ranges;
        private readonly int[] _maximumEnd;

        public FieldIntervalIndex(IEnumerable<FieldInterval> ranges)
        {
            _ranges = ranges.OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
            _maximumEnd = new int[_ranges.Length];
            var maximum = 0;
            for (var index = 0; index < _ranges.Length; index++)
                _maximumEnd[index] = maximum = Math.Max(maximum, _ranges[index].End);
        }

        public TableFieldDefinition? Find(long start, long end)
        {
            var low = 0;
            var high = _ranges.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_ranges[middle].Start < end) low = middle + 1;
                else high = middle;
            }
            for (var index = low - 1; index >= 0 && _maximumEnd[index] > start; index--)
                if (_ranges[index].End > start) return _ranges[index].Field;
            return null;
        }
    }
}
