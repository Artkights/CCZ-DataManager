using System.Data;
using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class RecordQueryServiceTests
{
    public static void Run(string basePath)
    {
        CheckConditionsAndScope();
        CheckReferencesSegmentsAndDrafts();
        CheckRealWorkspace(basePath);
        Console.WriteLine("属性查询 Core：三组与多词 AND、全部属性、真实 ID、子页范围、引用名称、物理列、草稿/撤回/重做、快照与真实基底验证通过。");
    }

    private static void CheckConditionsAndScope()
    {
        var workspace = new ProjectWorkspace();
        var persons = Add(workspace, "persons",
            [F("名称"), F("武力", FieldKind.UInt8), F("智力", FieldKind.UInt8),
             F("保留数据", policy: FieldPolicy.Preserve), F("头像", FieldKind.RawBytes), F("未加载字段")],
            [[21, "曹仁Beta", 82, 71, "保留内容", "RawSecret", ""],
             [11, "曹操Alpha[试]'*", 95, 75, "保留内容", "RawSecret", ""],
             [7, "刘备", 78, 73, "保留内容", "RawSecret", ""],
             [41, DBNull.Value, DBNull.Value, DBNull.Value, "保留内容", "RawSecret", ""]]);
        persons.Data.Columns.Remove("未加载字段");
        persons.Data.Columns.Add("派生诊断", typeof(string));
        persons.Data.Rows[0]["派生诊断"] = "DerivedSecret";
        Add(workspace, "person-r-assign", [F("R形象ID", FieldKind.UInt16)], [[7, 99451], [11, 12], [21, 20], [41, 1]]);
        Add(workspace, "person-s-assign", [F("S形象ID", FieldKind.UInt16)], [[41, 0], [21, 123], [11, 80], [7, 2]]);
        Add(workspace, "person-biography", [F("人物列传")], [[11, "青梅煮酒的完整列传正文"]], "persons");
        var service = new RecordQueryService(workspace);
        var r = service.PhysicalField(["person-r-assign"], "R形象ID", "R形象编号")!;
        var s = service.PhysicalField(["person-s-assign"], "S形象ID", "S形象编号")!;
        var force = service.PhysicalField(["persons"], "武力")!;
        var intelligence = service.PhysicalField(["persons"], "智力")!;
        var basicScope = new RecordQueryScope("persons:basic", [service.IdentityField(), r, s, force, intelligence]);
        var records = new RecordPresentationService(workspace).GetBusinessRecords(BusinessPageKind.Persons)
            .Select(record => record with { SearchText = "不得查询的 SearchText" }).Reverse().ToArray();
        var snapshot = service.BuildSnapshot(basicScope, records);

        CheckIds(snapshot.Filter([C(RecordQueryService.IdentityKey, "1"), C(r.Key, "2")]), 21, 11);
        CheckIds(snapshot.Filter([C(RecordQueryService.IdentityKey, "1"), C(r.Key, "2"), C(s.Key, "8")]), 11);
        CheckIds(snapshot.Filter([C(RecordQueryService.IdentityKey, "曹 操")]), 11);
        CheckIds(snapshot.Filter([C(null, "  曹\t\u300095\r\n  ")]), 11);
        CheckIds(snapshot.Filter([C(null, "ALPHA")]), 11);
        CheckIds(snapshot.Filter([C(null, "[试]'*")]), 11);
        CheckIds(snapshot.Filter([C(null, "*")]), 11);
        CheckIds(snapshot.Filter([C(r.Key, "2 0"), C(r.Key, "20")]), 21);
        CheckIds(snapshot.Filter([C(null, "未命名")]), 41);
        TestSupport.Assert(snapshot.Filter([C("missing", "1")]).Count == 0,
            "失效属性不得默默扩展成全部属性");
        TestSupport.Assert(snapshot.Filter([C(null, "不得查询")]).Count == 0,
            "属性快照不得依赖旧 SearchText 附加字段");
        foreach (var word in new[] { "青梅煮酒", "保留内容", "RawSecret", "DerivedSecret" })
            TestSupport.Assert(snapshot.Filter([C(null, word)]).Count == 0, $"当前子页混入其他子页或诊断数据：{word}");
        TestSupport.Assert(ReferenceEquals(snapshot.Filter([]), snapshot.Records) &&
            snapshot.Filter([C(null, "  \u3000\t\r\n"), C("missing", "")]).Select(record => record.Id).SequenceEqual(records.Select(record => record.Id)),
            "空组必须忽略并保留记录顺序");

        var biography = service.PhysicalField(["person-biography"], "人物列传", "列传")!;
        var biographySnapshot = service.BuildSnapshot(new("persons:biography", [service.IdentityField(), biography]), records);
        CheckIds(biographySnapshot.Filter([C(null, "青梅 煮酒")]), 11);
        TestSupport.Assert(biographySnapshot.Filter([C(null, "99451")]).Count == 0,
            "列传子页不得继承基本信息页的 R 编号");

        var physicalScope = service.TableScope(persons);
        var physical = service.BuildSnapshot(physicalScope, records);
        TestSupport.Assert(physicalScope.Fields.All(field => field.Key != r.Key) &&
            physical.Filter([C(null, "99451")]).Count == 0 && physical.Filter([C(null, "青梅")]).Count == 0,
            "物理表模式只能查询当前表实际列");
        foreach (var word in new[] { "保留内容", "RawSecret", "DerivedSecret" })
            TestSupport.Assert(physical.Filter([C(null, word)]).Count == 0, $"物理查询混入非展示属性：{word}");
        TestSupport.Assert(service.PhysicalField(["persons"], "未加载字段") == null &&
            service.PhysicalField(["persons"], "保留数据") == null &&
            service.PhysicalField(["persons"], "头像") == null && service.PhysicalField(["missing"], "名称") == null,
            "缺少列、缺少表和原始保留字节不应提供属性候选");

        // Separate values must remain separate: joining them would incorrectly make 'bc' searchable.
        var boundary = service.BuildSnapshot(new("test:boundaries",
            [new("parts", "分段", _ => ["ab", "cd"])]), [new(0, "记录", "bc")]);
        TestSupport.Assert(boundary.Filter([C("parts", "bc")]).Count == 0 && boundary.Filter([C(null, "bc")]).Count == 0,
            "不同属性值边界不能拼出不存在的字面文本");
        TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo,
            "构建查询或执行条件不能写入草稿或操作历史");
    }

    private static void CheckReferencesSegmentsAndDrafts()
    {
        var workspace = new ProjectWorkspace();
        var persons = Add(workspace, "persons", [F("名称"), F("兵种", FieldKind.UInt8, "detailed-jobs"),
            F("默认武器", FieldKind.UInt16, "items")], [[11, "曹操", 3, 300], [7, "刘备", 3, 9]]);
        var jobs = Add(workspace, "detailed-jobs", [F("名称")], [[3, "皇家近卫骑兵"]]);
        Add(workspace, "items-low", [F("名称")], [[9, "倚天剑"]]);
        Add(workspace, "items-high", [F("名称")], [[111, "玄铁甲"]]);
        Add(workspace, "items-extended", [F("名称")], [[300, "扩展青龙剑"]]);
        var service = new RecordQueryService(workspace);
        var job = service.PhysicalField(["persons"], "兵种")!;
        var weapon = service.PhysicalField(["persons"], "默认武器")!;
        var scope = new RecordQueryScope("persons:basic", [service.IdentityField(), job, weapon]);
        IReadOnlyList<RecordPresentation> Records() => new RecordPresentationService(workspace).GetBusinessRecords(BusinessPageKind.Persons);
        var snapshot = service.BuildSnapshot(scope, Records());
        CheckIds(snapshot.Filter([C(job.Key, "3 皇家")]), 7, 11);
        CheckIds(snapshot.Filter([C(weapon.Key, "300 青龙")]), 11);
        CheckIds(snapshot.Filter([C(weapon.Key, "9 倚天")]), 7);

        var itemName = service.PhysicalField(["items-low", "items-high", "items-extended"], "名称", stableKey: "items:name")!;
        var itemRecords = new RecordPresentationService(workspace).GetBusinessRecords(BusinessPageKind.Items);
        var itemSnapshot = service.BuildSnapshot(new("items:basic", [service.IdentityField(), itemName]), itemRecords);
        CheckIds(itemSnapshot.Filter([C(itemName.Key, "青龙")]), 300);
        CheckIds(itemSnapshot.Filter([C(itemName.Key, "玄铁")]), 111);

        workspace.RecordCellEdit(jobs, 0, "名称", "皇家近卫骑兵", "草稿精锐骑兵");
        workspace.RecordCellEdit(persons, 0, "名称", "曹操", "草稿曹公");
        var edited = service.BuildSnapshot(scope, Records());
        CheckIds(edited.Filter([C(RecordQueryService.IdentityKey, "草稿曹公"), C(job.Key, "精锐")]), 11);
        TestSupport.Assert(snapshot.Filter([C(job.Key, "精锐")]).Count == 0 && edited.Filter([C(job.Key, "皇家")]).Count == 0,
            "新快照必须读取草稿，旧快照必须保持物化内容");
        workspace.Commands.Undo();
        workspace.Commands.Undo();
        CheckIds(service.BuildSnapshot(scope, Records()).Filter([C(RecordQueryService.IdentityKey, "曹操"), C(job.Key, "皇家")]), 11);
        workspace.Commands.Redo();
        workspace.Commands.Redo();
        CheckIds(service.BuildSnapshot(scope, Records()).Filter([C(RecordQueryService.IdentityKey, "草稿曹公"), C(job.Key, "精锐")]), 11);

        // A new document can have the same key and revision: a cache keyed only by key/revision is stale.
        ((IDictionary<string, TableDocument>)workspace.Documents).Remove("detailed-jobs");
        Add(workspace, "detailed-jobs", [F("名称")], [[3, "重新载入的兵种"]]);
        CheckIds(service.BuildSnapshot(scope, Records()).Filter([C(job.Key, "重新载入")]), 7, 11);
        TestSupport.Assert(job.ReadValues(11).Contains("重新载入的兵种"), "独立属性读取也必须读取当前文档实例");
    }

    private static void CheckRealWorkspace(string basePath)
    {
        var copy = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var service = new RecordQueryService(workspace);
            var presentation = new RecordPresentationService(workspace);
            var records = presentation.GetBusinessRecords(BusinessPageKind.Persons);
            TestSupport.Assert(records.Count != 0, "真实基底缺少人物查询样本");
            var r = service.PhysicalField(["person-r-assign"], "R形象ID", "R形象编号");
            var s = service.PhysicalField(["person-s-assign"], "S形象ID", "S形象编号");
            TestSupport.Assert(r != null && s != null, "真实基底缺少 R/S 分配字段");
            var scope = new RecordQueryScope("persons:basic", [service.IdentityField(), r!, s!]);
            var snapshot = service.BuildSnapshot(scope, records);
            foreach (var (key, field) in new[] { ("person-r-assign", r!), ("person-s-assign", s!) })
            {
                var document = workspace.GetDocument(key);
                var fieldName = key == "person-r-assign" ? "R形象ID" : "S形象ID";
                var row = document.Data.Rows.Cast<DataRow>().First(candidate =>
                    records.Any(record => record.Id == Convert.ToInt32(candidate["ID"], CultureInfo.InvariantCulture)));
                var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
                var value = Convert.ToString(row[fieldName], CultureInfo.InvariantCulture)!;
                TestSupport.Assert(snapshot.Filter([C(field.Key, value)]).Any(record => record.Id == id),
                    $"真实 {fieldName} 未使用实际分配字段/真实人物 ID");
            }
            var documentScope = service.TableScope(workspace.GetDocument("persons"));
            var physical = service.BuildSnapshot(documentScope, records);
            TestSupport.Assert(physical.Filter([C(null, records[0].Display)]).Any(record => record.Id == records[0].Id),
                "真实物理表身份查询未沿用显示标签");
            TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo,
                "真实基底只读查询不得产生修改");
        }
        finally { Directory.Delete(copy, true); }
    }

    private static RecordQueryCondition C(string? field, string text) => new(field, text);
    private static void CheckIds(IReadOnlyList<RecordPresentation> actual, params int[] expected)
        => TestSupport.Assert(actual.Select(record => record.Id).SequenceEqual(expected),
            $"属性查询 ID 不符：期望 [{string.Join(",", expected)}]，实际 [{string.Join(",", actual.Select(record => record.Id))}]");
    private static TableFieldDefinition F(string name, FieldKind kind = FieldKind.FixedString,
        string? reference = null, FieldPolicy policy = FieldPolicy.Editable)
        => new(name, 0, kind == FieldKind.RawBytes ? 2 : 80, kind, Policy: policy, ReferenceTable: reference);
    private static TableDocument Add(ProjectWorkspace workspace, string key, TableFieldDefinition[] fields,
        object[][] rows, string? parent = null)
    {
        var data = new DataTable(key);
        data.Columns.Add("ID", typeof(int));
        foreach (var field in fields) data.Columns.Add(field.Name, typeof(object));
        foreach (var row in rows) data.Rows.Add(row);
        var document = new TableDocument
        {
            Definition = new(key, key, "test.e5", 0, rows.Length, 256, 0, fields,
                key.StartsWith("item", StringComparison.Ordinal) ? "宝物" : "人物", ParentKey: parent),
            FilePath = "test.e5", Data = data, OriginalFileBytes = [], OriginalFileHash = string.Empty
        };
        document.CaptureOriginalValues();
        ((IDictionary<string, TableDocument>)workspace.Documents).Add(key, document);
        return document;
    }
}
