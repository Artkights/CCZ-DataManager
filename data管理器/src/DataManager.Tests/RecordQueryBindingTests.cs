using System.Data;
using System.Globalization;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

/// <summary>Build each real business page once, then query all entities through its declared readers.</summary>
internal static class RecordQueryBindingTests
{
    public static void Run(string basePath)
    {
        var copy = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace(); workspace.Open(copy);
            using (var panel = new BusinessEditorPanel(workspace))
            using (var host = Host(panel))
            {
                CheckSharedQuotes(workspace, panel);
                CheckTransposedAttributes(workspace, panel);
                CheckLearningMappings(workspace, panel);
                CheckItemSegmentsAndPermissions(workspace, panel);
                CheckMissingOptionalFields(workspace, panel);
            }
            CheckCompleteExtendSchema();
            Console.WriteLine("RECORD_QUERY_BINDING_TESTS_OK shared/special-quotes/retreat/transposed-attributes/enums/job-learning/strategy-learning-names/items-low-high-256-511/item-semantics/permissions/optional-fields/extend-complete-schema/paging/raw-and-reference-names");
        }
        finally { Directory.Delete(copy, true); }
    }

    private static void CheckSharedQuotes(ProjectWorkspace workspace, BusinessEditorPanel panel)
    {
        var persons = workspace.GetDocument("persons");
        foreach (DataRow row in persons.Data.Rows) row["暴击台词"] = 0;
        foreach (DataRow row in workspace.GetDocument("person-critical-special-map").Data.Rows)
            row["人物ID"] = PersonQuoteService.EmptySpecialPersonId;
        Row(persons, 141)["暴击台词"] = 2;
        Row(persons, 142)["暴击台词"] = 2;
        Row(workspace.GetDocument("person-critical-special-map"), 3)["人物ID"] = 143;
        Row(workspace.GetDocument("person-critical-quotes"), 27)["台词"] = "共享槽甲词";
        Row(workspace.GetDocument("person-critical-quotes"), 3)["台词"] = "专属槽乙词";
        Row(workspace.GetDocument("person-retreat-quotes"), 3)["台词"] = "撤退映射丙词";
        panel.Bind(BusinessPageKind.Persons, 141); Select(panel, "人物详情");
        var scope = panel.CurrentQueryScope;
        var snapshot = Snapshot(workspace, scope, BusinessPageKind.Persons);
        var critical = Field(scope, "暴击台词");
        var retreat = Field(scope, "撤退台词");
        AssertIds(snapshot, critical.Key, "共享槽甲词", 141, 142);
        AssertIds(snapshot, critical.Key, "专属槽乙词", 143);
        AssertIds(snapshot, retreat.Key, "撤退映射丙词", 3);
        TestSupport.Assert(!critical.ReadValues(27).Contains("共享槽甲词") &&
            !critical.ReadValues(3).Contains("专属槽乙词") && retreat.ReadValues(141).All(string.IsNullOrEmpty),
            "共享台词槽号或不适用的撤退行被当成人物 ID");
        AssertIds(snapshot, Field(scope, "特殊槽位").Key, "3", 143);
        AssertIds(snapshot, Field(scope, "暴击 1").Key, "共享槽甲词", 141, 142);
        Console.WriteLine("RECORD_QUERY_BINDING_QUOTES_OK ordinary-slot27=>persons141,142 special-slot3=>person143 retreat-row3=>person3");
    }

    private static void CheckTransposedAttributes(ProjectWorkspace workspace, BusinessEditorPanel panel)
    {
        var attributes = workspace.GetDocument("job-series-attributes");
        foreach (var column in attributes.Data.Columns.Cast<DataColumn>().Where(column => column.ColumnName.StartsWith("兵种系 ", StringComparison.Ordinal)))
            foreach (var rowIndex in new[] { 0, 3, 5 }) attributes.Data.Rows[rowIndex][column] = 0;
        attributes.Data.Rows[0]["兵种系 5"] = 4;
        attributes.Data.Rows[0]["兵种系 9"] = 2;
        attributes.Data.Rows[3]["兵种系 5"] = 1;
        attributes.Data.Rows[5]["兵种系 5"] = 2;
        attributes.Data.Rows[5]["兵种系 9"] = 1;
        panel.Bind(BusinessPageKind.JobSeries, 5); Select(panel, "兵种属性");
        var scope = panel.CurrentQueryScope;
        var snapshot = Snapshot(workspace, scope, BusinessPageKind.JobSeries);
        var sound = Field(scope, "移动声音");
        AssertIds(snapshot, sound.Key, "引擎声", 5);
        AssertIds(snapshot, sound.Key, "脚步声", 9);
        AssertIds(snapshot, sound.Key, "4", 5);
        AssertIds(snapshot, Field(scope, "远程兵种").Key, "是", 5);
        AssertIds(snapshot, Field(scope, "兵种类型").Key, "武将", 5);
        AssertIds(snapshot, Field(scope, "兵种类型").Key, "文官", 9);
        Select(panel, "辅助分组");
        TestSupport.Assert(panel.CurrentQueryScope.Fields.Count == 1 && panel.CurrentQueryScope.Fields[0].Key == RecordQueryService.IdentityKey,
            "独立的全局分组配置混入了实体属性");
        Console.WriteLine("RECORD_QUERY_BINDING_MATRIX_OK attribute-row0/3/5=>series-column5/9 raw-and-enum global-config=identity-only");
    }

    private static void CheckLearningMappings(ProjectWorkspace workspace, BusinessEditorPanel panel)
    {
        var strategies = workspace.GetDocument("strategies");
        var learning = strategies.Definition.Fields.Where(field => field.Name.StartsWith("兵种", StringComparison.Ordinal) &&
            field.Name.EndsWith("学习等级", StringComparison.Ordinal)).ToArray();
        foreach (DataRow row in strategies.Data.Rows)
            foreach (var field in learning) row[field.Name] = 0;
        Row(strategies, 4)["名称"] = "映射策略甲";
        Row(strategies, 19)["名称"] = "映射策略乙";
        Row(strategies, 4)["兵种07学习等级"] = 21;
        Row(strategies, 19)["兵种11学习等级"] = 37;
        var jobs = workspace.GetDocument("detailed-jobs");
        Row(jobs, 7)["名称"] = "映射兵甲";
        Row(jobs, 11)["名称"] = "映射兵乙";
        panel.Bind(BusinessPageKind.DetailedJobs, 7); Select(panel, "兵种详情");
        var scope = panel.CurrentQueryScope;
        var learned = Field(scope, "习得策略");
        var snapshot = Snapshot(workspace, scope, BusinessPageKind.DetailedJobs);
        AssertIds(snapshot, learned.Key, "映射策略甲", 7);
        AssertIds(snapshot, learned.Key, "映射策略乙", 11);
        AssertIds(snapshot, learned.Key, "21", 7);
        AssertIds(snapshot, learned.Key, "37", 11);
        TestSupport.Assert(learned.ReadValues(4).All(value => !value.Contains("映射策略甲", StringComparison.Ordinal)),
            "习得策略把策略 ID 当作兵种 ID");
        panel.Bind(BusinessPageKind.Strategies, 4); Select(panel, "兵种学习等级");
        scope = panel.CurrentQueryScope;
        snapshot = Snapshot(workspace, scope, BusinessPageKind.Strategies);
        var job7 = Field(scope, "7：映射兵甲");
        var job11 = Field(scope, "11：映射兵乙");
        AssertIds(snapshot, job7.Key, "21", 4);
        AssertIds(snapshot, job7.Key, "映射兵甲", 4);
        AssertIds(snapshot, job11.Key, "37", 19);
        AssertIds(snapshot, job11.Key, "映射兵乙", 19);
        TestSupport.Assert(job7.ReadValues(19).All(value => !value.Contains("映射兵甲", StringComparison.Ordinal)),
            "未学习该策略的兵种名称错误进入查询值");
        Select(panel, "策略详情");
        TestSupport.Assert(!panel.CurrentQueryScope.Fields.Any(field => field.Key == job7.Key), "学习矩阵属性越过详情子页范围");
        Console.WriteLine("RECORD_QUERY_BINDING_LEARNING_OK strategy4=>job7/level21 strategy19=>job11/level37 inverse-name-mapping=correct");
    }

    private static void CheckItemSegmentsAndPermissions(ProjectWorkspace workspace, BusinessEditorPanel panel)
    {
        var low = workspace.GetDocument("items-low");
        var high = workspace.GetDocument("items-high");
        var lowId = Convert.ToInt32(low.Data.Rows[0]["ID"], CultureInfo.InvariantCulture);
        var highId = Convert.ToInt32(high.Data.Rows[0]["ID"], CultureInfo.InvariantCulture);
        // Two noncontiguous real IDs deliberately differ from both physical row ordinals.
        var extended = CloneItemRows(workspace, low, [256, 511]);
        var ids = new[] { lowId, highId, 256, 511 };
        var rows = new[] { Row(low, lowId), Row(high, highId), Row(extended, 256), Row(extended, 511) };
        for (var index = 0; index < rows.Length; index++)
        {
            rows[index]["名称"] = new[] { "甲", "乙", "丙", "丁" }[index];
            rows[index]["宝物类型"] = index % 2;
            rows[index]["装备特效号"] = 0;
            rows[index]["初始能力"] = 71 + index;
            rows[index]["装备特效值"] = 211 + index;
            rows[index]["能力成长"] = 31 + index;
        }
        var growth = workspace.GetDocument("detailed-job-growth");
        Row(growth, 7)["普通剑"] = 1; Row(growth, 7)["特殊剑"] = 0;
        Row(growth, 11)["普通剑"] = 0; Row(growth, 11)["特殊剑"] = 1;
        panel.Bind(BusinessPageKind.Items, lowId); Select(panel, "宝物详情");
        var scope = panel.CurrentQueryScope;
        var records = new RecordPresentationService(workspace).GetBusinessRecords(BusinessPageKind.Items)
            .Where(record => ids.Contains(record.Id)).ToArray();
        var snapshot = new RecordQueryService(workspace).BuildSnapshot(scope, records);
        TestSupport.Assert(scope.Fields.Count(field => field.DisplayName == "初始属性") == 1,
            "跨低段、高段、扩展的同一业务属性没有合并为一个候选");
        AssertIds(snapshot, Field(scope, "初始属性").Key, "71", lowId);
        AssertIds(snapshot, Field(scope, "初始属性").Key, "72", highId);
        AssertIds(snapshot, Field(scope, "初始属性").Key, "73", 256);
        AssertIds(snapshot, Field(scope, "初始属性").Key, "74", 511);
        AssertIds(snapshot, Field(scope, "特殊效果值").Key, "213", 256);
        AssertIds(snapshot, Field(scope, "能力成长").Key, "34", 511);
        AssertIds(snapshot, RecordQueryService.IdentityKey, "256", 256);
        AssertIds(snapshot, RecordQueryService.IdentityKey, "511", 511);
        Select(panel, "可装备部队");
        scope = panel.CurrentQueryScope;
        snapshot = new RecordQueryService(workspace).BuildSnapshot(scope, records);
        var job7 = Field(scope, "7：映射兵甲");
        var job11 = Field(scope, "11：映射兵乙");
        AssertIds(snapshot, job7.Key, "1", lowId, 256);
        AssertIds(snapshot, job7.Key, "是", lowId, 256);
        AssertIds(snapshot, job7.Key, "0", highId, 511);
        AssertIds(snapshot, job11.Key, "是", highId, 511);
        AssertIds(snapshot, job11.Key, "否", lowId, 256);
        TestSupport.Assert(!scope.Fields.Any(field => field.DisplayName == "初始属性"), "装备许可子页混入宝物详情属性");
        Console.WriteLine($"RECORD_QUERY_BINDING_ITEMS_OK low={lowId} high={highId} extended=256,511 semantic-fields=merged permissions=type-slot-not-item-ID");
    }

    private static void CheckMissingOptionalFields(ProjectWorkspace workspace, BusinessEditorPanel panel)
    {
        panel.Bind(BusinessPageKind.Persons, 141); Select(panel, "人物详情");
        TestSupport.Assert(panel.CurrentQueryScope.Fields.Any(field => field.DisplayName == "R形象编号") &&
            panel.CurrentQueryScope.Fields.Any(field => field.DisplayName == "列传"), "可选字段消失测试缺少原始候选");
        var documents = (IDictionary<string, TableDocument>)workspace.Documents;
        documents.Remove("person-r-assign"); documents.Remove("person-biography");
        panel.Bind(BusinessPageKind.Persons, 141); Select(panel, "人物详情");
        var scope = panel.CurrentQueryScope;
        TestSupport.Assert(scope.Fields.All(field => field.DisplayName is not "R形象编号" and not "列传") &&
            scope.Fields.Any(field => field.DisplayName == "S形象编号") && scope.Fields.Any(field => field.Key == RecordQueryService.IdentityKey),
            "缺失可选表仍提供旧候选，或误删仍然存在的属性");
        Console.WriteLine("RECORD_QUERY_BINDING_MISSING_OK removed-R/biography=absent S/identity=retained");
    }

    private static void CheckCompleteExtendSchema()
    {
        var workspace = ExtendHiddenReturnTests.CreateWorkspace(out var effects, out _);
        effects.Data.Rows[0]["槽250特效号"] = 17;
        effects.Data.Rows[0]["槽250特效值"] = 237;
        effects.Data.Rows[1]["槽250特效号"] = 18;
        effects.Data.Rows[1]["槽250特效值"] = 238;
        var study = AddSchema(workspace, "strategy-study-extended", 2, bytes =>
        {
            for (var index = 0; index < bytes.Length; index += 2) bytes[index] = 255;
        });
        study.Data.Rows[0]["策略31ID"] = 17; study.Data.Rows[0]["策略31次数"] = 191;
        study.Data.Rows[1]["策略31ID"] = 18; study.Data.Rows[1]["策略31次数"] = 192;
        AddNames(workspace, "strategies", [[17, "末页策略甲"], [18, "末页策略乙"]]);
        using var panel = new BusinessEditorPanel(workspace);
        using var host = Host(panel);
        panel.Bind(BusinessPageKind.ExtendData, 0); TestSupport.PumpUi();
        var editor = panel.ExtendEditor!;
        var pager = Find<ComboBox>(editor, "ExtendPage");
        TestSupport.Assert(pager.SelectedIndex == 0 && pager.Items.Count == 26, "拓展特效未处于真实26页的第一页");
        var scope = panel.CurrentQueryScope;
        AssertCompleteSchema(scope, effects);
        var records = editor.GetEntityOptions().Select(option => new RecordPresentation(option.Id, option.Name, string.Empty)).ToArray();
        var snapshot = new RecordQueryService(workspace).BuildSnapshot(scope, records);
        var lastEffectValue = Field(scope, "槽250特效值");
        AssertIds(snapshot, lastEffectValue.Key, "237", 0);
        AssertIds(snapshot, lastEffectValue.Key, "238", 1);
        AssertIds(snapshot, null, "237", 0);
        var keys = scope.Fields.Select(field => field.Key).ToArray();
        pager.SelectedIndex = 25; TestSupport.PumpUi();
        TestSupport.Assert(panel.CurrentQueryScope.Fields.Select(field => field.Key).SequenceEqual(keys), "内部翻页改变了完整查询 schema");
        AssertIds(new RecordQueryService(workspace).BuildSnapshot(panel.CurrentQueryScope, records), lastEffectValue.Key, "237", 0);

        editor.DetailTabs.SelectedTab = editor.DetailTabs.TabPages.Cast<TabPage>().Single(page => Equals(page.Tag, ExtendDataCategory.Strategies));
        TestSupport.PumpUi(); scope = panel.CurrentQueryScope;
        AssertCompleteSchema(scope, study);
        TestSupport.Assert(Find<ComboBox>(editor, "ExtendPage", visibleOnly: true).SelectedIndex == 0 &&
            scope.Key != "extend:Effects", "拓展策略范围未与特效范围分离");
        records = editor.GetEntityOptions().Select(option => new RecordPresentation(option.Id, option.Name, string.Empty)).ToArray();
        snapshot = new RecordQueryService(workspace).BuildSnapshot(scope, records);
        AssertIds(snapshot, Field(scope, "策略31次数").Key, "191", 0);
        AssertIds(snapshot, Field(scope, "策略31ID").Key, "末页策略甲", 0);
        AssertIds(snapshot, Field(scope, "策略31ID").Key, "17", 0);
        TestSupport.Assert(!scope.Fields.Any(field => field.Key == lastEffectValue.Key), "拓展分类混入了其他类别的槽属性");
        Find<ComboBox>(editor, "ExtendPage", visibleOnly: true).SelectedIndex = 3; TestSupport.PumpUi();
        AssertIds(new RecordQueryService(workspace).BuildSnapshot(panel.CurrentQueryScope, records), Field(scope, "策略31ID").Key, "末页策略乙", 1);
        Console.WriteLine("RECORD_QUERY_BINDING_EXTEND_OK effects=full-256-slots/26-pages strategies=full-32-slots/4-pages last-page-values-queryable-on-first-page");
    }

    private static void AssertCompleteSchema(RecordQueryScope scope, TableDocument document)
    {
        var expected = document.Definition.Fields.Count(field => field.Kind != FieldKind.RawBytes && field.Policy != FieldPolicy.Preserve &&
            document.Data.Columns.Contains(field.Name));
        TestSupport.Assert(scope.Fields.Count == expected + 1 && scope.Fields.Select(field => field.Key).Distinct().Count() == scope.Fields.Count,
            $"拓展查询未提供完整 schema 或出现重复属性：{scope.Key}, actual={scope.Fields.Count}, expected={expected + 1}");
    }

    private static TableDocument CloneItemRows(ProjectWorkspace workspace, TableDocument source, int[] ids)
    {
        var data = source.Data.Clone();
        foreach (var id in ids)
        {
            var values = source.Data.Rows[0].ItemArray; values[data.Columns["ID"]!.Ordinal] = id;
            data.Rows.Add(values);
        }
        var document = new TableDocument
        {
            Definition = source.Definition with { Key = "items-extended", DisplayName = "合成扩展宝物", FileName = "SyntheticItem.e5",
                BeginId = 256, DataOffset = 0, RowCount = ids.Length }, Data = data,
            OriginalFileBytes = new byte[source.Definition.RowSize * ids.Length], OriginalFileHash = string.Empty,
            FilePath = Path.Combine(workspace.RootPath, "SyntheticItem.e5")
        };
        document.CaptureOriginalValues();
        ((IDictionary<string, TableDocument>)workspace.Documents)["items-extended"] = document;
        return document;
    }

    private static TableDocument AddSchema(ProjectWorkspace workspace, string key, int count, Action<byte[]> initialize)
    {
        var definition = workspace.Definitions.Single(table => table.Key == key) with { DataOffset = 0, RowCount = count };
        var bytes = new byte[definition.RowSize * count]; initialize(bytes);
        var data = new DataTable(key); data.Columns.Add("ID", typeof(int));
        foreach (var field in definition.Fields) data.Columns.Add(field.Name, typeof(object));
        for (var row = 0; row < count; row++)
        {
            var values = new object?[definition.Fields.Count + 1]; values[0] = definition.BeginId + row;
            for (var index = 0; index < definition.Fields.Count; index++)
                values[index + 1] = BinaryValueCodec.Decode(bytes.AsSpan(row * definition.RowSize, definition.RowSize), definition.Fields[index]);
            data.Rows.Add(values);
        }
        var document = new TableDocument { Definition = definition, Data = data, OriginalFileBytes = bytes,
            OriginalFileHash = string.Empty, FilePath = Path.Combine(workspace.RootPath, definition.FileName) };
        document.CaptureOriginalValues(); ((IDictionary<string, TableDocument>)workspace.Documents).Add(key, document);
        return document;
    }

    private static void AddNames(ProjectWorkspace workspace, string key, object[][] rows)
    {
        var data = new DataTable(key); data.Columns.Add("ID", typeof(int)); data.Columns.Add("名称", typeof(string));
        foreach (var row in rows) data.Rows.Add(row);
        var document = new TableDocument { Definition = new(key, key, key + ".synthetic", 0, rows.Length, 24, 0,
            [new("名称", 0, 24, FieldKind.FixedString)], "synthetic"), Data = data,
            OriginalFileBytes = new byte[24 * rows.Length], OriginalFileHash = string.Empty,
            FilePath = Path.Combine(workspace.RootPath, key + ".synthetic") };
        document.CaptureOriginalValues(); ((IDictionary<string, TableDocument>)workspace.Documents).Add(key, document);
    }

    private static Form Host(BusinessEditorPanel panel)
    {
        var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Size = new Size(1280, 720) };
        form.Controls.Add(panel); form.Show(); TestSupport.PumpUi(); return form;
    }
    private static DataRow Row(TableDocument document, int id)
        => document.Data.Rows.Cast<DataRow>().Single(row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture) == id);
    private static void Select(BusinessEditorPanel panel, string page)
    {
        TestSupport.Assert(panel.SelectQueryScopeForSmokeTest(page), "查询子页不存在：" + page); TestSupport.PumpUi();
    }
    private static RecordQueryField Field(RecordQueryScope scope, string display)
        => scope.Fields.Single(field => field.DisplayName == display);
    private static RecordQuerySnapshot Snapshot(ProjectWorkspace workspace, RecordQueryScope scope, BusinessPageKind kind)
        => new RecordQueryService(workspace).BuildSnapshot(scope, new RecordPresentationService(workspace).GetBusinessRecords(kind));
    private static void AssertIds(RecordQuerySnapshot snapshot, string? field, string text, params int[] expected)
    {
        var actual = snapshot.Filter([new(field, text)]).Select(record => record.Id).Order().ToArray();
        TestSupport.Assert(actual.SequenceEqual(expected.Order()),
            $"业务映射查询不符：field=[{field}] text=[{text}] expected=[{string.Join(',', expected.Order())}] actual=[{string.Join(',', actual)}]");
    }
    private static T Find<T>(Control root, string name, bool visibleOnly = false) where T : Control
        => Descendants(root).OfType<T>().Single(control => control.Name == name && (!visibleOnly || control.Visible));
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
}
