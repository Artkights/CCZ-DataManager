using System.Data;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class RecordQueryCacheTests
{
    public static void Run()
    {
        CheckDemandAndDependencyInvalidation();
        CheckIndexesSchemaAndReplacement();
        CheckReferenceDirectories();
        CheckCancellationAndCaptureRevision();
        CheckZeroBudgetCaptureWorkingSet();
        CheckBudgetAndReset();
        Console.WriteLine("查询读取缓存：空条件零属性物化、按列读取、共享目录、精细失效、不可变快照、取消/修订隔离及有界会话释放通过。");
    }

    private static void CheckDemandAndDependencyInvalidation()
    {
        var workspace = new ProjectWorkspace();
        var persons = Add(workspace, "persons", [F("名称"), F("武力"), F("备注")],
            [[12, "曹操", "95", "旧备注"], [2, "曹仁", "82", "备注"]]);
        var cache = WorkspaceReadCache.For(workspace);
        var service = new RecordQueryService(workspace);
        var forceReads = 0;
        var noteReads = 0;
        var force = new RecordQueryField("force", "武力", id =>
        {
            forceReads++;
            return [Convert.ToString(cache.FindRow(persons, id)!["武力"])!];
        }) { Dependencies = [new("persons", "武力")] };
        var note = new RecordQueryField("note", "备注", id =>
        {
            noteReads++;
            return [Convert.ToString(cache.FindRow(persons, id)!["备注"])!];
        }) { Dependencies = [new("persons", "备注")] };
        var scope = new RecordQueryScope("test:demand", [service.IdentityField(), force, note]);
        var records = cache.GetNavigationRecords(BusinessPageKind.Persons);
        var empty = service.BuildSnapshotForConditions(scope, records, RecordQueryState.EmptyConditions());
        Assert(forceReads == 0 && noteReads == 0 && cache.Statistics.MaterializedColumns == 0,
            "空条件不能读取或物化业务属性");
        Assert(empty.Filter([]).Count == 2 && empty.Filter([new(RecordQueryService.IdentityKey, "曹 操")]).Single().Id == 12,
            "仅身份快照必须保留真实 ID 和原有分词匹配");

        var selected = service.BuildSnapshot(scope, records, [force.Key, force.Key]);
        Assert(forceReads == 2 && noteReads == 0, "指定属性仅读取一次所需列，重复条件必须共享物化列");
        Assert(selected.Filter([new(force.Key, "9"), new(force.Key, "5")]).Single().Id == 12,
            "指定列快照的重复条件必须保持 AND");
        _ = service.BuildSnapshot(scope, records, [force.Key]);
        Assert(forceReads == 2, "同修订热查询应命中属性列缓存");
        var navigationBuilds = cache.Statistics.NavigationBuilds;
        var indexBuilds = cache.Statistics.RowIndexBuilds;
        var schemaVersion = cache.GetSchemaVersion([new("persons")]);
        persons.Data.Rows[0]["备注"] = "改过备注";
        _ = cache.GetNavigationRecords(BusinessPageKind.Persons);
        _ = service.BuildSnapshot(scope, records, [force.Key]);
        Assert(forceReads == 2 && cache.Statistics.NavigationBuilds == navigationBuilds &&
            cache.Statistics.RowIndexBuilds == indexBuilds, "无关列编辑不应使属性、导航名称或 ID 索引失效");
        Assert(cache.GetSchemaVersion([new("persons")]) == schemaVersion,
            "值编辑不能触发布局/属性描述的结构版本变化");

        persons.Data.Rows[0]["武力"] = "100";
        var changed = service.BuildSnapshot(scope, records, [force.Key]);
        Assert(forceReads == 4 && changed.Filter([new(force.Key, "100")]).Single().Id == 12 &&
            selected.Filter([new(force.Key, "95")]).Single().Id == 12, "相关列需刷新，旧快照必须保持不可变");
        Assert(cache.Statistics.RowIndexBuilds == indexBuilds, "普通属性编辑不能重建 ID 索引");

        persons.Data.Rows[0]["名称"] = "草稿曹公";
        var renamed = cache.GetNavigationRecords(BusinessPageKind.Persons);
        Assert(renamed.Single(record => record.Id == 12).Name == "草稿曹公" && records.Single(record => record.Id == 12).Name == "曹操",
            "名称目录必须读取当前草稿，旧导航记录不得原地改变");
        _ = service.BuildSnapshot(scope, renamed, [force.Key]);
        Assert(forceReads == 4, "名称变化不应重建不依赖名称的属性列");
        persons.Data.RejectChanges();
        var restored = service.BuildSnapshot(scope, cache.GetNavigationRecords(BusinessPageKind.Persons), [force.Key]);
        Assert(restored.Filter([new(force.Key, "95")]).Single().Id == 12 &&
            cache.GetTableRowName(persons, 12) == "曹操", "RejectChanges 需恢复查询属性和引用名称");

        var all = service.BuildSnapshotForConditions(scope, cache.GetTableRecords(persons), [new(null, "曹 备注")]);
        Assert(noteReads == 2 && all.Filter([new(null, "曹 备注")]).Count == 2,
            "全部属性按需读取，单框多词可命中不同属性");
        Assert(all.Filter([new("removed", "曹")]).Count == 0, "失效属性不能变成全部属性");
        WorkspaceReadCache.Reset(workspace);
    }

    private static void CheckIndexesSchemaAndReplacement()
    {
        var workspace = new ProjectWorkspace();
        var persons = Add(workspace, "persons", [F("名称"), F("武力")], [[11, "曹操", "95"]]);
        var cache = WorkspaceReadCache.For(workspace);
        var service = new RecordQueryService(workspace);
        var force = service.PhysicalField(["persons"], "武力")!;
        var scope = new RecordQueryScope("test:structure", [force]);
        var initialVersion = cache.GetVersion([new("persons", "武力")]);
        Assert(cache.FindRow(persons, 11) != null, "首次 ID 索引无法读取实际记录");
        persons.Data.Rows[0]["ID"] = 91;
        Assert(cache.FindRow(persons, 11) == null && cache.FindRow(persons, 91) != null &&
            cache.GetVersion([new("persons", "武力")]) != initialVersion,
            "ID 编辑必须使 ID 索引及相关属性记录映射失效");
        persons.Data.Rows.Add(40, "新增", "77");
        Assert(cache.GetTableRecords(persons).Select(record => record.Id).SequenceEqual([40, 91]),
            "新增记录必须沿真实 ID 重建导航");
        persons.Data.Rows[1].Delete();
        Assert(cache.FindRow(persons, 40) == null, "删除记录不能留在 ID 索引中");
        persons.Data.RejectChanges();
        Assert(cache.FindRow(persons, 11) != null && cache.FindRow(persons, 91) == null,
            "结构撤回不能复用旧 ID 索引");

        var beforeSchema = cache.GetSchemaVersion([new("persons")]);
        persons.Data.Columns["武力"]!.ColumnName = "不存在的列";
        Assert(cache.GetSchemaVersion([new("persons")]) != beforeSchema && service.PhysicalField(["persons"], "武力") == null,
            "列改名需刷新描述并移除不再存在的候选");
        persons.Data.Columns["不存在的列"]!.ColumnName = "武力";
        Assert(service.PhysicalField(["persons"], "武力") != null, "恢复列后属性描述未刷新");
        var version = cache.GetVersion([new("persons", "武力")]);
        ((IDictionary<string, TableDocument>)workspace.Documents).Remove("persons");
        var replacement = Add(workspace, "persons", [F("名称"), F("武力")], [[11, "重开曹公", "66"]]);
        Assert(cache.GetVersion([new("persons", "武力")]) != version &&
            ReferenceEquals(cache.FindRow(replacement, 11), replacement.Data.Rows[0]), "同键同修订的新文档必须使用实例身份失效");
        var reopened = service.BuildSnapshot(scope, cache.GetTableRecords(replacement), [force.Key]);
        Assert(reopened.Filter([new(force.Key, "66")]).Single().Name == "重开曹公", "属性读取不能保留替换前的文档");
        WorkspaceReadCache.Reset(workspace);
    }

    private static void CheckReferenceDirectories()
    {
        var workspace = new ProjectWorkspace();
        var persons = Add(workspace, "persons", [F("名称"), F("兵种", "detailed-jobs")], [[8, "曹操", "3"]]);
        var jobs = Add(workspace, "detailed-jobs", [F("名称"), F("备注")], [[3, "皇家骑兵", "无关"]]);
        var cache = WorkspaceReadCache.For(workspace);
        var service = new RecordQueryService(workspace);
        var field = service.PhysicalField(["persons"], "兵种")!;
        var scope = new RecordQueryScope("test:references", [field]);
        var records = cache.GetTableRecords(persons);
        var old = service.BuildSnapshot(scope, records, [field.Key]);
        Assert(old.Filter([new(field.Key, "3 皇家")]).Single().Id == 8, "引用属性应同时匹配编号和名称");
        var columns = cache.Statistics.MaterializedColumns;
        jobs.Data.Rows[0]["备注"] = "新备注";
        _ = service.BuildSnapshot(scope, records, [field.Key]);
        Assert(cache.Statistics.MaterializedColumns == columns, "引用表无关列不应使属性列失效");
        jobs.Data.Rows[0]["名称"] = "精锐骑兵";
        var current = service.BuildSnapshot(scope, records, [field.Key]);
        Assert(current.Filter([new(field.Key, "精锐")]).Count == 1 && current.Filter([new(field.Key, "皇家")]).Count == 0 &&
            old.Filter([new(field.Key, "皇家")]).Count == 1, "引用名称编辑应刷新关联列且保留旧快照");
        WorkspaceReadCache.Reset(workspace);
    }

    private static void CheckCancellationAndCaptureRevision()
    {
        var workspace = new ProjectWorkspace();
        var document = Add(workspace, "persons", [F("名称"), F("武力")], [[1, "曹操", "95"], [2, "曹仁", "82"]]);
        var cache = WorkspaceReadCache.For(workspace);
        var service = new RecordQueryService(workspace);
        var reads = 0;
        var slow = new RecordQueryField("slow", "延迟读取", id =>
        {
            if (++reads == 1) Thread.Sleep(12); // Force a capture slice, independent of machine speed.
            return [Convert.ToString(cache.FindRow(document, id)!["武力"])!];
        }) { Dependencies = [new("persons", "武力")] };
        var scope = new RecordQueryScope("test:cancel", [slow]);
        var records = cache.GetTableRecords(document);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        ExpectCancelled(() => service.BuildSnapshot(scope, records, [slow.Key], cancelled.Token));
        Assert(reads == 0, "预取消查询不得开始读取属性");
        using var during = new CancellationTokenSource();
        ExpectCancelled(() => service.BuildSnapshotAsync(scope, records, [slow.Key], _ =>
        {
            during.Cancel(); return Task.CompletedTask;
        }, during.Token).GetAwaiter().GetResult());
        Assert(cache.Statistics.MaterializedColumns == 0, "取消的半列不能发布到缓存");

        reads = 0;
        var changedAtYield = false;
        ExpectCancelled(() => service.BuildSnapshotAsync(scope, records, [slow.Key], _ =>
        {
            changedAtYield = true;
            document.Data.Rows[0]["武力"] = "100";
            return Task.CompletedTask;
        }).GetAwaiter().GetResult());
        Assert(changedAtYield && cache.Statistics.MaterializedColumns == 0,
            "UI 分批捕获时的草稿变化必须取消旧捕获，不能发布混合修订");
        var completed = service.BuildSnapshot(scope, records, [slow.Key]);
        Assert(completed.Filter([new(slow.Key, "100")]).Single().Id == 1,
            "取消后新查询必须读取新草稿");
        ExpectCancelled(() => completed.Filter([new(null, "曹")], cancelled.Token));
        WorkspaceReadCache.Reset(workspace);
    }

    private static void CheckZeroBudgetCaptureWorkingSet()
    {
        var workspace = new ProjectWorkspace();
        const int recordCount = 128, fieldCount = 64;
        var definitions = Enumerable.Range(0, fieldCount).Select(index => F("列" + index)).ToArray();
        var rows = Enumerable.Range(0, recordCount).Select(index =>
            new object[] { 1000 + index * 3 }.Concat(Enumerable.Range(0, fieldCount)
                .Select(field => (object)(index == 17 && field == 63 ? "草稿[17].*" : ((index + field) % 97).ToString())))
                .ToArray()).ToArray();
        var document = Add(workspace, "persons", definitions, rows);
        var cache = WorkspaceReadCache.For(workspace);
        cache.BudgetBytes = 0;
        var service = new RecordQueryService(workspace);
        var scope = service.TableScope(document);
        var records = cache.GetTableRecords(document);
        var before = cache.Statistics.RowIndexBuilds;
        var snapshot = service.BuildSnapshot(scope, records);
        Assert(cache.Statistics.RowIndexBuilds == before + 1,
            "零缓存预算的全属性捕获应每表仅构建一次 ID 索引，不能逐格重新扫描整张表");
        Assert(cache.Statistics.ResidentBytes == 0 && cache.Statistics.CapturePinnedRowBytes == 0 &&
            cache.Statistics.PeakCapturePinnedRowBytes >= 128L + recordCount * 56L,
            "零预算捕获需分别统计临时行目录与常驻缓存，并在完成后释放临时工作集");
        Assert(snapshot.Filter([new(null, "草稿[17].*")]).Single().Id == 1051,
            "批量物理读取不能把行位置当成真实 ID，也不能改变字面匹配");

        var physical = scope.Fields.Single(field => field.Key.EndsWith(":列63", StringComparison.Ordinal));
        document.Data.Rows[17]["列63"] = "新草稿";
        var updated = service.BuildSnapshot(scope, records, [physical.Key]);
        Assert(updated.Filter([new(physical.Key, "新草稿")]).Single().Id == 1051 &&
            snapshot.Filter([new(physical.Key, "草稿[17].*")]).Single().Id == 1051,
            "临时列访问器不得跨捕获保留过期草稿，旧快照需保持独立");

        var firstRead = true;
        var delayed = physical with { Key = "zero-budget-delayed", ReadValues = id =>
        {
            var values = physical.ReadValues(id);
            if (firstRead) { firstRead = false; Thread.Sleep(12); }
            return values;
        } };
        using var cancellation = new CancellationTokenSource();
        ExpectCancelled(() => service.BuildSnapshotAsync(new("test:zero-budget-cancel", [delayed]), records,
            null, _ =>
            {
                Assert(cache.Statistics.CapturePinnedRowBytes > 0 && cache.Statistics.ResidentBytes == 0,
                    "UI 分批捕获时应保留当前行目录，并继续遵守常驻缓存预算");
                cancellation.Cancel(); return Task.CompletedTask;
            }, cancellation.Token).GetAwaiter().GetResult());
        Assert(cache.Statistics.CapturePinnedRowBytes == 0, "取消捕获后仍钉住临时行目录");

        var throwing = physical with { Key = "zero-budget-throw", ReadValues = id =>
        {
            _ = physical.ReadValues(id);
            throw new InvalidOperationException("expected capture reader failure");
        } };
        try { service.BuildSnapshot(new("test:zero-budget-throw", [throwing]), records); }
        catch (InvalidOperationException error) when (error.Message == "expected capture reader failure")
        {
            Assert(cache.Statistics.CapturePinnedRowBytes == 0, "属性读取异常后仍钉住临时行目录");
            Console.WriteLine($"RECORD_QUERY_ZERO_BUDGET_OK rows={recordCount} fields={fieldCount} " +
                $"initial-index-builds=1 cache-resident={cache.Statistics.ResidentBytes} " +
                $"capture-pinned={cache.Statistics.CapturePinnedRowBytes} peak-capture-pinned={cache.Statistics.PeakCapturePinnedRowBytes}");
            WorkspaceReadCache.Reset(workspace);
            return;
        }
        throw new InvalidOperationException("零预算异常清理测试没有触发属性读取异常。");
    }

    private static void CheckBudgetAndReset()
    {
        var workspace = new ProjectWorkspace();
        var document = Add(workspace, "persons", [F("名称")], [[1, "曹操"], [2, "曹仁"]]);
        var cache = WorkspaceReadCache.For(workspace);
        _ = cache.FindRow(document, 1);
        _ = cache.GetTableRecords(document);
        Assert(cache.Statistics.ResidentBytes > 0, "缓存预算测试未建立真实条目");
        cache.BudgetBytes = 1;
        Assert(cache.Statistics.ResidentBytes <= 1 && cache.Statistics.Entries == 0 && cache.Statistics.Evictions > 0,
            "预算缩小时必须驱逐冷数据，不能保留超预算条目");
        _ = cache.GetTableRecords(document);
        Assert(cache.Statistics.ResidentBytes <= cache.BudgetBytes, "单个超大条目不能突破缓存预算");
        WorkspaceReadCache.Reset(workspace);
        var fresh = WorkspaceReadCache.For(workspace);
        Assert(!ReferenceEquals(cache, fresh) && fresh.Statistics.Entries == 0 && cache.Statistics.ResidentBytes == 0,
            "项目会话重置必须释放旧缓存并建立独立新会话");
        WorkspaceReadCache.Reset(workspace);
    }

    private static void ExpectCancelled(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("预期查询捕获取消，但操作完成了。");
    }

    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);
    private static TableFieldDefinition F(string name, string? reference = null)
        => new(name, 0, 80, FieldKind.FixedString, ReferenceTable: reference);

    private static TableDocument Add(ProjectWorkspace workspace, string key, TableFieldDefinition[] fields, object[][] rows)
    {
        var data = new DataTable(key);
        data.Columns.Add("ID", typeof(int));
        foreach (var field in fields) data.Columns.Add(field.Name, typeof(object));
        foreach (var row in rows) data.Rows.Add(row);
        var document = new TableDocument
        {
            Definition = new(key, key, "test.e5", 0, rows.Length, 256, 0, fields, "人物"),
            FilePath = "test.e5", Data = data, OriginalFileBytes = [], OriginalFileHash = string.Empty
        };
        document.CaptureOriginalValues();
        ((IDictionary<string, TableDocument>)workspace.Documents).Add(key, document);
        return document;
    }
}
