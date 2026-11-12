using System.Data;
using System.Reflection;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class RecordPresentationTests
{
    // Presentation owns the left navigation label. Attribute searches are tested
    // separately through RecordQueryService and the three-condition UI tests.
    public static void Run()
    {
        var workspace = new ProjectWorkspace();
        Add(workspace, "persons", [F("名称"), F("头像", FieldKind.RawBytes), F("兵种", reference: "detailed-jobs"),
            F("默认武器", reference: "items"), F("暴击台词")],
            [[7, "刘备[试]'*", "3412", 3, 9, 0], [100, "曹操", "0000", 3, 255, 1]]);
        Add(workspace, "detailed-jobs", [F("名称")], [[3, "皇家近卫骑兵"]]);
        Add(workspace, "items-low", [F("名称")], [[9, "倚天长剑"]]);
        Add(workspace, "person-r-assign", [F("R形象ID")], [[7, 56123], [100, 0]]);
        Add(workspace, "person-s-assign", [F("S形象ID")], [[7, 45234], [100, 0]]);
        Add(workspace, "person-biography", [F("人物列传")], [[7, "桃园结义的故事"]], "persons");
        Add(workspace, "person-critical-quotes", [F("台词")], [[2, "天下归心！"], [21, "为了天下苍生！"], [22, new string('甲', 40) + "隐藏在摘要之外的尾文"]]);
        Add(workspace, "person-critical-special-map", [F("人物ID", reference: "persons")], [[2, 100]]);
        Add(workspace, "person-retreat-quotes", [F("台词")], [[7, "来日再战！"]]);
        Add(workspace, "job-series", [F("名称")], [[0, "骑兵"], [7, "弓骑兵"]]);
        var attributes = Add(workspace, "job-series-attributes", [F("兵种系 7")], [[0, 2], [1, 4]], "job-series");
        attributes.Data.Columns.Add("名称", typeof(string));
        attributes.Data.Rows[0]["名称"] = "错误继承的骑兵";
        Add(workspace, "strategies", [F("名称")], [[0, "火龙"]]);
        Add(workspace, "strategy-terrain-availability", [F("策略施展地形掩码")], [[0, 15]]);
        Add(workspace, "scenario-names", [F("战役名称")], [[7, "濮阳之战"]]);
        Add(workspace, "shops", [F("装备0", reference: "items")], [[7, 9], [127, 9]]);
        var nativeChild = Add(workspace, "native-child", [F("名称")], [[7, "子表原生名称"]], "persons");
        var finishing = Add(workspace, "finishing-moves", [F("名称")], [[0, "旧必杀技"]]);
        var finishingDescription = Add(workspace, "finishing-move-description", [F("说明")], [[0, "说明内容"]], "finishing-moves");
        Add(workspace, "equipment-type-names", [F("名称")], [[0, "项目自定剑"]]);
        Add(workspace, "equipment-type-display", [F("显示值")], [[0, 0]]);
        var service = new RecordPresentationService(workspace);
        var people = service.GetTableRecords(workspace.GetDocument("persons"));
        var person = people.Single(record => record.Id == 7);
        TestSupport.Assert(people.Select(record => record.Id).SequenceEqual([7, 100]), "导航必须保留非连续真实 ID");
        foreach (var query in new[] { "刘备", "[试]'*", "7", "7：刘备", "  刘备  " })
            TestSupport.Assert(person.Matches(query), $"人物物理表没有按左栏ID/名称标签匹配：{query}");
        foreach (var query in new[] { "皇家近卫骑兵", "倚天长剑", "56123", "45234", "4660", "3412", "3", "9", "桃园结义", "为了天下苍生", "来日再战" })
            TestSupport.Assert(!person.Matches(query), $"人物物理表查询误匹配了左栏标签以外的字段：{query}");
        TestSupport.Assert(!people.Single(record => record.Id == 100).Matches("56123") && !person.Matches("不存在") &&
            person.Matches("  "), "包含匹配、空查询或真实记录映射错误");
        var business = service.GetBusinessRecords(BusinessPageKind.Persons).Single(record => record.Id == 7);
        TestSupport.Assert(business.SearchText == business.Display && person.SearchText == person.Display &&
            business.Display == "7：刘备[试]'*" && business.Matches("7：刘备"), "业务和物理表查询标签必须与左栏完全一致");
        foreach (var query in new[] { "皇家近卫骑兵", "倚天长剑", "56123", "45234", "4660", "桃园结义", "为了天下苍生", "来日再战" })
            TestSupport.Assert(!business.Matches(query), $"人物业务查询误合并了关联字段：{query}");
        TestSupport.Assert(new RecordPresentation(22, "Alpha", "不得搜索的附加字段").Matches("ALPHA") &&
            !new RecordPresentation(22, "Alpha", "不得搜索的附加字段").Matches("附加字段"),
            "匹配必须忽略大小写且不得依赖附加SearchText");
        TestSupport.Assert(new RecordPresentation(23, string.Empty, "额外内容").Display == "23：未命名" &&
            new RecordPresentation(23, string.Empty, "额外内容").Matches("未命名"), "空名称必须匹配左栏显示的未命名占位");
        var biography = service.GetTableRecords(workspace.GetDocument("person-biography"))[0];
        TestSupport.Assert(biography.Matches("刘备") && !biography.Matches("桃园结义"), "副表只能匹配左栏关联名称，不能匹配正文");
        TestSupport.Assert(service.GetTableRecords(workspace.GetDocument("detailed-jobs"))[0].Matches("皇家近卫骑兵") &&
            service.GetBusinessRecords(BusinessPageKind.Items)[0].Matches("倚天长剑"), "独立兵种/宝物页应匹配其自身左栏名称");
        TestSupport.Assert(service.GetTableRecords(attributes)[0].Name == "移动声音" &&
            !service.GetTableRecords(attributes)[0].Matches("错误继承的骑兵"), "转置属性表不得继承兵种系行名称");
        TestSupport.Assert(service.GetTableRecords(workspace.GetDocument("strategy-terrain-availability"))[0].Name == "平原",
            "策略施展地形表应按地形轴显示，不能按策略ID显示");
        TestSupport.Assert(service.GetTableRecords(workspace.GetDocument("person-critical-quotes")).Single(record => record.Id == 2).Name.Contains("曹操") &&
            service.GetTableRecords(workspace.GetDocument("person-critical-quotes")).Single(record => record.Id == 21).Name.Contains("普通暴击类型 0") &&
            service.GetTableRecords(workspace.GetDocument("person-retreat-quotes"))[0].Name.Contains("来日再战"), "台词池须显示槽语义、实际绑定人物及文本摘要");
        TestSupport.Assert(service.GetTableRecords(workspace.GetDocument("person-retreat-quotes"))[0].Matches("来日再战"),
            "台词摘要已经显示在左栏名称时应可按标签匹配");
        var longQuote = service.GetTableRecords(workspace.GetDocument("person-critical-quotes")).Single(record => record.Id == 22);
        TestSupport.Assert(longQuote.Matches(new string('甲', 10)) && !longQuote.Matches("摘要之外的尾文"),
            "台词池查询应只匹配左栏可见摘要，不能匹配摘要之外的正文");
        TestSupport.Assert(service.GetBusinessRecords(BusinessPageKind.Shop).Single(record => record.Id == 7).Name == "濮阳之战" &&
            service.GetBusinessRecords(BusinessPageKind.Shop).Single(record => record.Id == 127).Name == string.Empty,
            "商店须按对应战役ID命名，未证实战役名不得错配");
        TestSupport.Assert(new BusinessDataService(workspace).GetIdNameOptions(BusinessPageKind.Shop).Single(option => option.Id == 7).Name == "濮阳之战",
            "业务导航和搜索必须使用同一战役名称来源");
        TestSupport.Assert(service.GetTableRecords(workspace.GetDocument("equipment-type-display"))[0].Name == "项目自定剑",
            "装备类型显示槽必须沿用项目原生类型名");

        typeof(ProjectWorkspace).GetMethod("AddParentReferenceNames", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(workspace, null);
        TestSupport.Assert(Convert.ToString(attributes.Data.Rows[0]["名称"]) == "移动声音" && attributes.Data.Columns["名称"]!.ReadOnly,
            "父表派生名称刷新必须纠正属性轴并保持只读");
        TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo, "只读搜索不得产生草稿或历史");
        TestSupport.Assert(Convert.ToString(nativeChild.Data.Rows[0]["名称"]) == "子表原生名称" && !nativeChild.Data.Columns["名称"]!.ReadOnly,
            "父表名称派生不得覆盖原生名称字段或改变其可写状态");
        workspace.RecordCellEdit(finishing, 0, "名称", "旧必杀技", "新必杀技");
        TestSupport.Assert(Convert.ToString(finishingDescription.Data.Rows[0]["名称"]) == "新必杀技", "通用父表改名未刷新派生子表名称");
        workspace.Commands.Undo();
        TestSupport.Assert(Convert.ToString(finishingDescription.Data.Rows[0]["名称"]) == "旧必杀技", "撤回父表改名未刷新派生子表名称");
        var personRow = workspace.GetDocument("persons").Data.Rows[0];
        personRow["名称"] = "新姓名";
        var refreshed = new RecordPresentationService(workspace).GetTableRecords(workspace.GetDocument("person-biography"))[0];
        TestSupport.Assert(refreshed.Name == "新姓名" && refreshed.Matches("新姓名"), "新搜索快照必须读取当前草稿姓名");

        var missing = new ProjectWorkspace();
        Add(missing, "person-critical-quotes", [F("台词")], [[21, "共享文本"]]);
        TestSupport.Assert(new RecordPresentationService(missing).GetBusinessRecords(BusinessPageKind.Persons).Count == 0 &&
            new BusinessDataService(missing).GetIdNameOptions(BusinessPageKind.Persons).Count == 0,
            "缺主表时不得将共享台词池行号冒充人物ID");
        Console.WriteLine("记录展示标签：ID/名称、附加字段隔离、字面包含、大小写、未命名占位、转置属性、地形、台词标签、战役和草稿名称验证通过；详情属性查询由独立查询服务验证。");
    }

    private static TableFieldDefinition F(string name, FieldKind kind = FieldKind.FixedString, string? reference = null)
        => new(name, 0, kind == FieldKind.RawBytes ? 2 : 40, kind, ReferenceTable: reference);

    private static TableDocument Add(ProjectWorkspace workspace, string key, TableFieldDefinition[] fields,
        object[][] records, string? parent = null)
    {
        var data = new DataTable(key);
        data.Columns.Add("ID", typeof(int));
        foreach (var field in fields) data.Columns.Add(field.Name, typeof(object));
        foreach (var record in records) data.Rows.Add(record);
        var document = new TableDocument
        {
            Definition = new(key, key, "test.e5", 0, records.Length, 256, 0, fields,
                key.StartsWith("item", StringComparison.Ordinal) ? "宝物" : "人物", ParentKey: parent),
            FilePath = "test.e5", Data = data, OriginalFileBytes = [], OriginalFileHash = string.Empty
        };
        document.CaptureOriginalValues();
        ((IDictionary<string, TableDocument>)workspace.Documents).Add(key, document);
        return document;
    }
}
