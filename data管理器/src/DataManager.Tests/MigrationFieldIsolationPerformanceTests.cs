using System.Data;
using System.Diagnostics;
using System.Reflection;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Migration;
using DataManager.Core.Models;

namespace DataManager.Tests;

/// <summary>Exercises alias isolation without reading or modifying any sample project.</summary>
internal static class MigrationFieldIsolationPerformanceTests
{
    private delegate string? IsolationCall(ProjectWorkspace workspace, TableDocument document, int rowIndex,
        TableFieldDefinition field, object? value);

    private static readonly IsolationCall FindConflict = typeof(MigrationService).Assembly
        .GetType("DataManager.Core.Migration.MigrationFieldIsolation", throwOnError: true)!
        .GetMethod("FindConflict", BindingFlags.Public | BindingFlags.Static)!
        .CreateDelegate<IsolationCall>();

    public static void Run()
    {
        TestChangedBytesAndPolicies();
        TestDynamicItemFootprintChanges();
        TestIndexedFootprintsMatchReference();
        TestWideTableAllocationBudget();
    }

    private static void TestChangedBytesAndPolicies()
    {
        foreach (var policy in Enum.GetValues<FieldPolicy>())
        {
            var bytes = new byte[32];
            var selected = Field("选择字段", 0, 8, FieldKind.RawBytes);
            var source = Document("selected", 0, 1, 8, [selected], bytes);
            var alias = Document("alias", 0, 1, 8, [Field("别名字段", 0, 1, policy: policy)], bytes);
            var workspace = Workspace(source, alias);
            TestSupport.Assert(FindConflict(workspace, source, 0, selected, "0000000000000001") == null,
                "未改变的重叠字节不应阻止隔离迁移：" + policy);
            TestSupport.Assert(FindConflict(workspace, source, 0, selected, "0100000000000000")?.Contains("别名字段", StringComparison.Ordinal) == true,
                "漏掉物理别名字段：" + policy);
            TestSupport.Assert(FindConflict(workspace, source, 0, selected, "0000000000000000") == null,
                "无实际字节变化时不应产生冲突");
        }

        var shared = new byte[32];
        var field = Field("跨行字段", 5, 20, FieldKind.RawBytes);
        var selectedDocument = Document("selected", 0, 1, 32, [field], shared);
        var otherDocument = Document("rows", 0, 4, 8, [Field("后行别名", 3, 1)], shared);
        var project = Workspace(selectedDocument, otherDocument);
        var changed = new byte[20];
        changed[14] = 1; // physical byte 19 = row 2, offset 3
        var conflict = FindConflict(project, selectedDocument, 0, field, Convert.ToHexString(changed));
        TestSupport.Assert(conflict?.Contains("ID=2", StringComparison.Ordinal) == true,
            "跨行字段未检查后续行的实际变化字节");
        changed[14] = 0;
        changed[19] = 1; // physical byte 24 = row 3, offset 0, outside the alias
        TestSupport.Assert(FindConflict(project, selectedDocument, 0, field, Convert.ToHexString(changed)) == null,
            "字段间隙被误当成物理别名");
    }

    private static void TestDynamicItemFootprintChanges()
    {
        var bytes = new byte[0x15];
        var selected17 = Field("物理11", 0x11, 1);
        var selected18 = Field("物理12", 0x12, 1);
        var selected = Document("selected", 0, 1, bytes.Length, [selected17, selected18], bytes);
        var dynamicItem = Document("item-alias", 0, 1, bytes.Length, [Field("宝物类型", 0x11, 1)], bytes, "item-66-15-v1");
        var workspace = Workspace(selected, dynamicItem);

        Check(marker: false);
        bytes[0x12] = 2;
        // The engine classifies +0x11 < 26 as weapon/armor before
        // considering the category byte. A 2/3 byte alone is not a marker row.
        Check(marker: false);
        bytes[0x11] = 25;
        Check(marker: false);
        bytes[0x11] = 26;
        Check(marker: true);
        bytes[0x12] = 0;
        Check(marker: false);
        bytes[0x12] = 3;
        Check(marker: true);
        bytes[0x11] = 25;
        Check(marker: false);
        bytes[0x11] = 26;
        Check(marker: true);
        bytes[0x12] = 0;
        Check(marker: false);
        var replacement = (byte[])bytes.Clone();
        replacement[0x12] = 3;
        selected.OriginalFileBytes = replacement;
        dynamicItem.OriginalFileBytes = replacement;
        Check(marker: true);

        void Check(bool marker)
        {
            TestSupport.Assert((FindConflict(workspace, selected, 0, selected17, 9) != null) == !marker,
                "Item66 标记变化后 +0x11 写范围缓存过期");
            TestSupport.Assert((FindConflict(workspace, selected, 0, selected18, 9) != null) == marker,
                "Item66 标记变化后 +0x12 写范围缓存过期");
        }
    }

    private static void TestIndexedFootprintsMatchReference()
    {
        var random = new Random(17825);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var bytes = new byte[256];
            var sourceField = Field("待迁移字段", random.Next(0, 24), 32, FieldKind.RawBytes);
            var source = Document("source", 8, 3, 64, [sourceField], bytes);
            var fields = Enumerable.Range(0, 36).Select(index =>
            {
                var kind = (FieldKind)(index % 5);
                var length = kind switch { FieldKind.UInt8 => 1, FieldKind.UInt16 => 2, FieldKind.UInt32 => 4, _ => random.Next(1, 6) };
                return Field("别名" + index, random.Next(0, 32 - length), length, kind, (FieldPolicy)(index % 3));
            }).ToArray();
            var alias = Document("alias", random.Next(0, 32), 6, 32, fields, bytes);
            var workspace = Workspace(source, alias);
            var changes = new byte[32];
            for (var change = 0; change < random.Next(1, 6); change++) changes[random.Next(changes.Length)] = 1;
            var row = random.Next(3);
            var value = Convert.ToHexString(changes);
            var indexed = FindConflict(workspace, source, row, sourceField, value);
            var reference = FindConflictReference(workspace, source, row, sourceField, value);
            TestSupport.Assert((indexed != null) == (reference != null), "区间索引与原始逐字节隔离结果不同：" + iteration);
            TestSupport.Assert(bytes.All(value => value == 0), "隔离预览修改了原始快照");
        }
    }

    private static void TestWideTableAllocationBudget()
    {
        const int calls = 10_000;
        var bytes = new byte[2050];
        var field = Field("空隙字段", 2049, 1);
        var source = Document("source", 0, 1, bytes.Length, [field], bytes);
        var aliasFields = Enumerable.Range(0, 2048).Select(index => Field("列" + index, index, 1)).ToArray();
        var alias = Document("wide-alias", 0, 1, bytes.Length, aliasFields, bytes);
        var workspace = Workspace(source, alias);
        TestSupport.Assert(FindConflict(workspace, source, 0, field, 1) == null, "宽表空隙不应产生冲突");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < calls; index++)
            if (FindConflict(workspace, source, 0, field, 1) != null) throw new InvalidOperationException("宽表索引误报");
        timer.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestSupport.Assert(allocated < 8L * 1024 * 1024,
            $"隔离校验重新编码了别名表：{calls} 次分配 {allocated:N0} 字节，超过 8 MiB 上界");
        Console.WriteLine($"MIGRATION_ISOLATION_INDEX calls={calls} aliasFields={aliasFields.Length} elapsedMs={timer.Elapsed.TotalMilliseconds:F1} allocatedBytes={allocated}");
    }

    private static TableFieldDefinition Field(string name, int offset, int size, FieldKind kind = FieldKind.UInt8,
        FieldPolicy policy = FieldPolicy.Editable)
        => new(name, offset, size, kind, policy, 0, kind switch { FieldKind.UInt16 => ushort.MaxValue, FieldKind.UInt32 => uint.MaxValue, _ => byte.MaxValue });

    private static TableDocument Document(string key, int offset, int rows, int rowSize,
        IReadOnlyList<TableFieldDefinition> fields, byte[] bytes, string codec = "fixed-record-v1")
        => new()
        {
            Definition = new TableDefinition(key, key, "IsolationTest.bin", offset, rows, rowSize, 0, fields, "tests", RecordCodecKey: codec),
            FilePath = Path.Combine(Path.GetTempPath(), "DataManagerIsolationSynthetic.bin"),
            Data = new DataTable(),
            OriginalFileBytes = bytes,
            OriginalFileHash = string.Empty
        };

    private static ProjectWorkspace Workspace(params TableDocument[] documents)
    {
        var workspace = new ProjectWorkspace();
        var loaded = (Dictionary<string, TableDocument>)typeof(ProjectWorkspace)
            .GetField("_documents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
        foreach (var document in documents) loaded.Add(document.Definition.Key, document);
        return workspace;
    }

    private static string? FindConflictReference(ProjectWorkspace workspace, TableDocument document, int rowIndex,
        TableFieldDefinition field, object? value)
    {
        var codecs = new RecordCodecRegistry();
        var start = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        var row = document.OriginalFileBytes.AsSpan(start, document.Definition.RowSize);
        foreach (var patch in codecs.Get(document.Definition.RecordCodecKey).EncodeChangedField(row, field, value))
        {
            var changed = Enumerable.Range(0, patch.Bytes.Length).Where(index => document.OriginalFileBytes[start + patch.Offset + index] != patch.Bytes[index])
                .Select(index => (long)start + patch.Offset + index).ToHashSet();
            if (changed.Count == 0) continue;
            foreach (var other in workspace.Documents.Values.Where(candidate => candidate.Definition.Key != document.Definition.Key &&
                candidate.FilePath.Equals(document.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                var definition = other.Definition;
                if (changed.Max() < definition.DataOffset || changed.Min() >= definition.EndOffsetExclusive) continue;
                var first = Math.Max(0, (changed.Min() - definition.DataOffset) / definition.RowSize);
                var last = Math.Min(definition.RowCount - 1L, (changed.Max() - definition.DataOffset) / definition.RowSize);
                for (var index = first; index <= last; index++)
                {
                    var otherStart = checked((int)(definition.DataOffset + index * definition.RowSize));
                    var otherRow = other.OriginalFileBytes.AsSpan(otherStart, definition.RowSize);
                    var codec = codecs.Get(definition.RecordCodecKey);
                    foreach (var otherField in definition.Fields)
                    foreach (var otherPatch in codec.EncodeChangedField(otherRow, otherField, codec.Decode(otherRow, otherField)))
                        if (changed.Any(offset => offset >= otherStart + otherPatch.Offset && offset < otherStart + otherPatch.Offset + otherPatch.Bytes.Length)) return "conflict";
                }
            }
        }
        return null;
    }
}
