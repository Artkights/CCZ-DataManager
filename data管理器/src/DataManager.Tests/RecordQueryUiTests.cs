using System.Data;
using System.Reflection;
using DataManager.App;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class RecordQueryUiTests
{
    public static void Run(string source)
    {
        var copy = TestSupport.CreateCoreCopy(source);
        using var form = new MainForm(false) { StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000), ShowInTaskbar = false, Size = new Size(1280, 720) };
        try
        {
            form.Show(); form.LoadProjectForSmokeTest(copy); TestSupport.PumpUi();
            var workspace = form.WorkspaceForSmokeTest;
            var persons = workspace.GetDocument("persons");
            var id = Convert.ToInt32(persons.Data.Rows[42]["ID"]);
            var a = Convert.ToInt32(persons.Data.Rows[141]["ID"]);
            var b = Convert.ToInt32(persons.Data.Rows[142]["ID"]);
            var c = Convert.ToInt32(persons.Data.Rows[143]["ID"]);
            var biography = workspace.GetDocument("person-biography");
            var biographyField = biography.Definition.Fields.First(field => field.Kind == FieldKind.FixedString).Name;
            new BusinessDataService(workspace).BeginTransaction(BusinessPageKind.Persons)
                .SetById("persons", id, "名称", "查'[]*验")
                .SetById("persons", id, "初始HP", 60001)
                .SetById("person-biography", id, biographyField, "仅详情唯一词 BioNeedleXYZ")
                .SetById("persons", a, "名称", "筛曹操甲")
                .SetById("persons", b, "名称", "筛曹操乙")
                .SetById("persons", c, "名称", "筛孙权丙")
                .SetById("person-biography", a, biographyField, "跨字段甲词")
                .SetById("person-r-assign", a, "R形象ID", 12)
                .SetById("person-r-assign", b, "R形象ID", 22)
                .SetById("person-r-assign", c, "R形象ID", 3)
                .SetById("person-s-assign", a, "S形象ID", 3)
                .SetById("person-s-assign", b, "S形象ID", 12)
                .SetById("person-s-assign", c, "S形象ID", 22).Apply();
            TestSupport.PumpUi(); ClearQuery(form);
            CheckAttributeConditions(form, id, a, b);
            CheckExplicitExecution(form, id, a, b);
            CheckDetailPageState(form, id);

            TestSupport.Assert(form.QueryForSmokeTest("'[]*"), "字面关键词查询失败");
            AssertVisibleQueryIds(form, [id], "姓名特殊字符没有按字面匹配");
            TestSupport.Assert(form.BusinessEditorForSmokeTest.CurrentId == id, "查询没有绑定真实人物 ID");
            TestSupport.Assert(form.QuerySummaryForSmokeTest.Contains("匹配 1 /"), "没有查询结果数量");
            form.SelectModuleForSmokeTest("策略");
            TestSupport.Assert(form.QueryForSmokeTest("__没有匹配的测试内容__") && form.EmptyQueryForSmokeTest,
                "零结果没有显示空详情状态");
            form.SelectModuleForSmokeTest("人物");
            AssertVisibleQueryIds(form, [id], "返回业务页丢失独立查询状态");
            form.QuickLocateForSmokeTest(id + 1);
            TestSupport.Assert(form.BusinessEditorForSmokeTest.CurrentId == id + 1 && form.EntityListForSmokeTest.Items.Count == persons.Data.Rows.Count,
                "快速定位没有清除筛选并跳到筛选外记录");

            // Query accepts unblurred valid input before taking a current-draft snapshot.
            form.QuickLocateForSmokeTest(id);
            var nameEditor = Descendants(form.BusinessEditorForSmokeTest).OfType<ByteTextEditor>()
                .Single(editor => editor.Editor.Text == "查'[]*验");
            nameEditor.Editor.Focus(); nameEditor.Editor.Text = "未失焦查询";
            TestSupport.Assert(form.QueryForSmokeTest("未失焦查询") && SelectedId(form) == id &&
                Convert.ToString(persons.Data.Rows[42]["名称"]) == "未失焦查询", "查询丢失未失焦输入或读取了提交前名称");
            form.UndoDraftForSmokeTest(); TestSupport.PumpUi();
            TestSupport.Assert(form.EntityListForSmokeTest.Items.Count == 0 && form.EmptyQueryForSmokeTest, "撤回后没有重新计算查询");
            ClearQuery(form);

            CheckPhysicalDraftAndValidation(form, id);
            var checkedTables = CheckAllPhysicalTables(form);
            CheckImportUndoRedoAndSaveRefresh(form, copy, id);
            foreach (var size in new[] { new Size(1100, 650), new Size(1280, 720), new Size(1920, 1080) })
            {
                form.Size = size; TestSupport.PumpUi(); CheckQueryLayout(form, size.ToString());
            }
            CheckRemovedRememberedField(form, id);
            // Pending and applied conditions are memory-only and reset on project reopen.
            TestSupport.Assert(form.QueryForSmokeTest("__重开前零结果__"), "重开前测试条件失败");
            form.LoadProjectForSmokeTest(copy); TestSupport.PumpUi();
            TestSupport.Assert(form.QueryInputsForSmokeTest.All(input => input.Text.Length == 0) &&
                form.QueryFieldsForSmokeTest.All(field => field.SelectedIndex <= 0) && !form.EmptyQueryForSmokeTest,
                "重新打开项目仍保留旧查询条件");
            Console.WriteLine($"RECORD_QUERY_UI_TESTS_OK tables={checkedTables} three-group-AND/word-AND/whitespace/all-attributes/R-S/biography/literal/case/explicit-enter-button/clear/subpage-state/zero-tab-navigation/draft/undo/invalid-edit/external-filter/quick-locate/physical-ID/csv-import-undo-redo-save-refresh/missing-remembered-field-warning/reopen/layout");
        }
        finally { form.Dispose(); Directory.Delete(copy, true); }
    }

    private static void CheckAttributeConditions(MainForm form, int id, int a, int b)
    {
        TestSupport.Assert(form.QueryInputsForSmokeTest.Count == 3 && form.QueryFieldsForSmokeTest.Count == 3,
            "不是三组独立属性和普通文本输入框");
        TestSupport.Assert(form.QueryInputsForSmokeTest.All(input => input.Text.Length == 0) &&
            form.QueryFieldsForSmokeTest.All(field => field.SelectedIndex <= 0), "初始条件不是全部空白");
        var identity = FieldKey(form, "ID：名称");
        var r = FieldKey(form, "R形象编号");
        var s = FieldKey(form, "S形象编号");
        var biography = FieldKey(form, "列传");
        var rows = form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows.Cast<DataRow>();
        var assignments = form.WorkspaceForSmokeTest.GetDocument("person-r-assign").Data.Rows.Cast<DataRow>()
            .ToDictionary(row => Convert.ToInt32(row["ID"]), row => Convert.ToString(row["R形象ID"]) ?? string.Empty);
        var expected = rows.Where(row => $"{row["ID"]}：{row["名称"]}".Contains('1') &&
            assignments.GetValueOrDefault(Convert.ToInt32(row["ID"]), string.Empty).Contains('2'))
            .Select(row => Convert.ToInt32(row["ID"]));
        Apply(form, new RecordQueryCondition(identity, "1"), new RecordQueryCondition(r, "2"));
        AssertVisibleQueryIds(form, expected, "用户示例未同时约束 ID：名称和 R 形象编号");
        Apply(form, new RecordQueryCondition(identity, "筛曹 操"));
        AssertVisibleQueryIds(form, [a, b], "单框多词未执行 AND");
        Apply(form, new RecordQueryCondition(identity, "  筛曹　　操   "), new RecordQueryCondition(r, "2"), new RecordQueryCondition(s, "3"));
        AssertVisibleQueryIds(form, [a], "三组 AND、连续空格或全角空格处理错误");
        Apply(form, new RecordQueryCondition(identity, "筛曹 操"), new RecordQueryCondition(r, "1 2"));
        AssertVisibleQueryIds(form, [a], "数值未按文本包含或单属性多词匹配错误");
        Apply(form, new RecordQueryCondition(identity, "筛曹 操"), new RecordQueryCondition(s, "1 2"));
        AssertVisibleQueryIds(form, [b], "R/S 读取了同一个物理字段");
        Apply(form, new RecordQueryCondition(identity, "筛孙 权"), new RecordQueryCondition(r, "2"));
        AssertVisibleQueryIds(form, [], "R 编号错误命中了仅 S 编号匹配的记录");
        Apply(form, new RecordQueryCondition(null, "筛曹操甲 跨字段甲词"));
        AssertVisibleQueryIds(form, [a], "全部属性的多个词不能在不同属性内命中");
        Apply(form, new RecordQueryCondition(identity, "跨字段甲词"));
        AssertVisibleQueryIds(form, [], "明确选择身份后仍匹配了列传");
        Apply(form, new RecordQueryCondition(biography, "bioneedlexyz"));
        AssertVisibleQueryIds(form, [id], "列传正文未匹配或英文大小写未忽略");
        Apply(form, new RecordQueryCondition(identity, "'[]*"));
        AssertVisibleQueryIds(form, [id], "特殊字符被解释为模式或表达式");
        Apply(form, new RecordQueryCondition(identity, "筛曹"), new RecordQueryCondition(identity, "甲"), new RecordQueryCondition(s, " 　"));
        AssertVisibleQueryIds(form, [a], "重复属性条件或空输入组处理错误");
        Apply(form, new RecordQueryCondition(null, "仅详情唯一词"));
        AssertVisibleQueryIds(form, [id], "全部属性没有覆盖完整列传正文");
        Apply(form, new RecordQueryCondition(identity, " 　"), new RecordQueryCondition(r, ""), new RecordQueryCondition(s, "   "));
        TestSupport.Assert(form.EntityListForSmokeTest.Items.Count == rows.Count(), "全空输入条件没有恢复全体记录");
        ClearQuery(form);
    }

    private static void CheckExplicitExecution(MainForm form, int id, int a, int b)
    {
        Apply(form, new RecordQueryCondition(FieldKey(form, "ID：名称"), "'[]*"));
        var previousIds = VisibleQueryIds(form);
        var fields = form.QueryFieldsForSmokeTest;
        var inputs = form.QueryInputsForSmokeTest;
        SetField(fields[0], "ID：名称"); inputs[0].Text = "筛曹 操";
        SetField(fields[1], "R形象编号"); inputs[1].Text = "2";
        TestSupport.PumpUi();
        AssertVisibleQueryIds(form, previousIds, "修改属性或输入时提前应用条件");
        PressQueryEnter(inputs[2]);
        AssertVisibleQueryIds(form, [a, b], "任一输入框回车没有一次应用三组条件");
        inputs[0].Text = "'[]*"; fields[1].SelectedIndex = 0; inputs[1].Text = string.Empty;
        TestSupport.PumpUi();
        AssertVisibleQueryIds(form, [a, b], "文字修改提前改变已应用结果");
        ClickQuery(form);
        AssertVisibleQueryIds(form, [id], "查询按钮没有应用待执行输入");
        ClearQuery(form);
        TestSupport.Assert(inputs.All(input => input.Text.Length == 0) && fields.All(field => field.SelectedIndex <= 0) &&
            form.EntityListForSmokeTest.Items.Count == form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows.Count,
            "清空未同时重置三组属性、文字和结果");
    }

    private static void CheckDetailPageState(MainForm form, int personId)
    {
        form.SelectModuleForSmokeTest("策略"); ClearQuery(form);
        SelectDetailPage(form, "策略详情");
        var firstScope = form.QueryScopeForSmokeTest.Key;
        Apply(form, new RecordQueryCondition(FieldKey(form, "ID：名称"), "__详情条件__"));
        TestSupport.Assert(form.EmptyQueryForSmokeTest, "策略详情测试没有得到零结果");
        SelectDetailPage(form, "兵种学习等级");
        TestSupport.Assert(form.QueryScopeForSmokeTest.Key != firstScope &&
            form.QueryInputsForSmokeTest.All(input => input.Text.Length == 0) && !form.EmptyQueryForSmokeTest,
            "零结果不能切换详情子页或新子页继承了旧条件");
        var learningScope = form.QueryScopeForSmokeTest.Key;
        TestSupport.Assert(!form.QueryScopeForSmokeTest.Fields.Any(field => field.DisplayName is "R形象编号" or "列传"),
            "策略子页候选混入人物属性");
        Apply(form, new RecordQueryCondition(FieldKey(form, "ID：名称"), "1"));
        var appliedIds = VisibleQueryIds(form);
        form.QueryInputsForSmokeTest[1].Text = "尚未执行";
        SelectDetailPage(form, "策略详情");
        TestSupport.Assert(form.QueryScopeForSmokeTest.Key == firstScope && form.EmptyQueryForSmokeTest &&
            form.QueryInputsForSmokeTest[0].Text == "__详情条件__", "切回详情子页没有恢复已应用状态");
        SelectDetailPage(form, "兵种学习等级");
        TestSupport.Assert(form.QueryScopeForSmokeTest.Key == learningScope && form.QueryInputsForSmokeTest[1].Text == "尚未执行",
            "切回子页没有保存尚未执行的输入");
        AssertVisibleQueryIds(form, appliedIds, "恢复子页时错误应用了尚未执行的输入");
        ClearQuery(form); SelectDetailPage(form, "策略详情"); ClearQuery(form);
        form.SelectModuleForSmokeTest("人物"); ClearQuery(form); form.QuickLocateForSmokeTest(personId);
    }

    private static void CheckPhysicalDraftAndValidation(MainForm form, int id)
    {
        var persons = form.WorkspaceForSmokeTest.GetDocument("persons");
        form.ShowPhysicalTableForSmokeTest("persons");
        TestSupport.Assert(form.QueryForSmokeTest("'[]*"), "物理表查询失败");
        var grid = form.PhysicalGridForSmokeTest;
        TestSupport.Assert(grid.RowCount == 1 && Convert.ToInt32(((DataRowView)grid.Rows[0].DataBoundItem)["ID"]) == id && SelectedId(form) == id,
            "物理表筛选或导航的真实 ID 不一致");
        TestSupport.Assert(form.QueryForSmokeTest("仅详情唯一词") && grid.RowCount == 0, "物理表查询混入关联列传正文");
        TestSupport.Assert(form.QueryForSmokeTest("60001") && VisibleQueryIds(form).Contains(id), "物理表没有查询实际展示的初始 HP 列");
        Apply(form, new RecordQueryCondition(FieldKey(form, "ID：名称"), "60001"));
        AssertVisibleQueryIds(form, [], "物理表明确身份条件仍匹配初始 HP");
        form.QueryForSmokeTest("'[]*");
        var before = persons.Data.Rows[42]["武力"];
        grid.CurrentCell = grid.Rows[0].Cells["武力"];
        grid.BeginEdit(true); ((TextBox)grid.EditingControl!).Text = Convert.ToInt32(before) == 99 ? "98" : "99";
        TestSupport.Assert(form.QueryForSmokeTest(string.Empty), "当前表格编辑阻止正常查询");
        TestSupport.Assert(Convert.ToInt32(persons.Data.Rows[42]["武力"]) != Convert.ToInt32(before), "筛选后编辑没有提交真实源行");
        form.QuickLocateForSmokeTest(id + 1);
        TestSupport.Assert(SelectedId(form) == id + 1 && Convert.ToInt32(((DataRowView)grid.CurrentRow!.DataBoundItem)["ID"]) == id + 1,
            "物理表快速定位没有同步真实 ID");
        grid.CurrentCell = grid.CurrentRow!.Cells["武力"]; grid.BeginEdit(true);
        ((TextBox)grid.EditingControl!).Text = "9999999";
        var previousSummary = form.QuerySummaryForSmokeTest;
        var previousIds = VisibleQueryIds(form);
        var invalidQueryAccepted = form.QueryForSmokeTest("无效查询不得应用");
        TestSupport.Assert(!invalidQueryAccepted && form.QuerySummaryForSmokeTest == previousSummary && grid.IsCurrentCellInEditMode &&
            form.QueryInputsForSmokeTest[0].Text == "无效查询不得应用", "无效编辑没有保留旧结果、错误单元格和查询输入");
        AssertVisibleQueryIds(form, previousIds, "验证失败仍改变了结果");
        var hideEmpty = (CheckBox)typeof(MainForm).GetField("_hideEmpty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        hideEmpty.Checked = true;
        TestSupport.Assert(!hideEmpty.Checked && grid.IsCurrentCellInEditMode, "无效输入后隐藏空白行未恢复勾选状态");
        grid.CancelEdit(); form.QueryForSmokeTest("'[]*");
        grid.CurrentCell = grid.Rows[0].Cells["名称"];
        typeof(MainForm).GetMethod("GridKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [grid, new KeyEventArgs(Keys.Delete)]);
        TestSupport.PumpUi();
        TestSupport.Assert(form.EmptyQueryForSmokeTest, "清空字段后没有更新查询结果");
        form.UndoDraftForSmokeTest(); TestSupport.PumpUi();
        AssertVisibleQueryIds(form, [id], "撤回清空字段没有恢复查询记录");
        ClearQuery(form);

        persons.Data.DefaultView.RowFilter = $"[ID] >= {id} AND [ID] <= {id + 2}";
        form.QueryForSmokeTest("'[]*"); AssertVisibleQueryIds(form, [id], "属性查询没有与外部 RowFilter 交集");
        ClearQuery(form); AssertVisibleQueryIds(form, [id, id + 1, id + 2], "清空查询丢失外部 RowFilter");
        persons.Data.DefaultView.RowFilter = string.Empty; ClearQuery(form);
        hideEmpty.Checked = true; TestSupport.PumpUi(); var hiddenIds = VisibleQueryIds(form);
        form.QueryForSmokeTest("__隐藏空白行后零结果__"); ClearQuery(form);
        AssertVisibleQueryIds(form, hiddenIds, "清空查询没有继续遵守隐藏空白行");
        hideEmpty.Checked = false; TestSupport.PumpUi();
    }

    private static int CheckAllPhysicalTables(MainForm form)
    {
        var workspace = form.WorkspaceForSmokeTest;
        var checkedTables = 0;
        foreach (var module in workspace.Modules)
        {
            form.SelectModuleForSmokeTest(module.Key);
            foreach (var key in module.TableKeys.Where(workspace.Documents.ContainsKey).Where(key => key is not "person-r-assign" and not "person-s-assign"))
            {
                form.ShowPhysicalTableForSmokeTest(key); ClearQuery(form);
                var records = new RecordPresentationService(workspace).GetTableRecords(workspace.GetDocument(key));
                TestSupport.Assert(form.EntityListForSmokeTest.Items.Count == records.Count, key + " 导航记录数错误");
                if (records.Count > 0)
                {
                    TestSupport.Assert(form.EntityListForSmokeTest.Items[0].ToString()!.StartsWith(records[0].Id + "："), key + " 首 ID 错误");
                    TestSupport.Assert(form.EntityListForSmokeTest.Items[form.EntityListForSmokeTest.Items.Count - 1].ToString()!.StartsWith(records[^1].Id + "："), key + " 尾 ID 错误");
                }
                checkedTables++;
            }
        }
        return checkedTables;
    }

    private static void CheckImportUndoRedoAndSaveRefresh(MainForm form, string copy, int id)
    {
        ShowPersonBusiness(form); ClearQuery(form); form.QuickLocateForSmokeTest(id);
        const string importedName = "导入刷新";
        var identity = FieldKey(form, "ID：名称");
        Apply(form, new RecordQueryCondition(identity, importedName));
        AssertVisibleQueryIds(form, [], "导入前唯一名称已经存在，无法验证属性结果刷新");
        var csv = Path.Combine(copy, "查询导入刷新.csv");
        File.WriteAllText(csv, $"ID,名称\r\n{id},{importedName}\r\n", new System.Text.UTF8Encoding(true));
        var imported = form.ImportBusinessCsvForSmokeTest(new Dictionary<string, string> { ["persons"] = csv });
        TestSupport.PumpUi();
        TestSupport.Assert(imported.ChangedCells == 1, "无 profile 的合法 CSV 没有导入单个名称字段");
        AssertVisibleQueryIds(form, [id], "CSV 导入后已应用属性查询没有读取新草稿");
        TestSupport.Assert(form.QueryInputsForSmokeTest[0].Text == importedName,
            "CSV 导入后丢失已应用条件的输入文字");
        form.UndoDraftForSmokeTest(); TestSupport.PumpUi();
        AssertVisibleQueryIds(form, [], "撤回 CSV 导入后没有恢复零结果");
        typeof(MainForm).GetMethod("DraftRedo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        TestSupport.PumpUi();
        AssertVisibleQueryIds(form, [id], "重做 CSV 导入后没有重新命中名称");
        var saved = form.SavePendingForSmokeTest(); TestSupport.PumpUi();
        TestSupport.Assert(saved.Saved && !form.WorkspaceForSmokeTest.IsDirty,
            "CSV 查询草稿保存失败：" + string.Join("；", saved.Messages));
        AssertVisibleQueryIds(form, [id], "保存后属性快照没有保留当前查询结果");
        TestSupport.Assert(form.QueryInputsForSmokeTest[0].Text == importedName &&
            Convert.ToString(form.QueryFieldsForSmokeTest[0].SelectedItem) == "ID：名称",
            "保存后丢失已应用属性或关键词输入");
        Console.WriteLine("RECORD_QUERY_UI_IMPORT_REFRESH_OK metadata-free-csv=>one undo=>zero redo=>one save=>one inputs=retained");
    }

    private static void CheckRemovedRememberedField(MainForm form, int id)
    {
        ShowPersonBusiness(form); ClearQuery(form); form.QuickLocateForSmokeTest(id);
        var workspace = form.WorkspaceForSmokeTest;
        var rKey = FieldKey(form, "R形象编号");
        var rDocument = workspace.GetDocument("person-r-assign");
        var rValue = Convert.ToString(rDocument.Data.Rows.Cast<DataRow>().Single(row => Convert.ToInt32(row["ID"]) == id)["R形象ID"])!;
        Apply(form, new RecordQueryCondition(FieldKey(form, "ID：名称"), "导入刷新"), new RecordQueryCondition(rKey, rValue));
        AssertVisibleQueryIds(form, [id], "失效属性测试的原始已应用条件不正确");
        form.QueryInputsForSmokeTest[1].Text = "尚未执行的失效属性词";
        form.SelectModuleForSmokeTest("策略"); TestSupport.PumpUi();
        ((IDictionary<string, TableDocument>)workspace.Documents).Remove("person-r-assign");
        ShowPersonBusiness(form);
        typeof(MainForm).GetMethod("RefreshQueryNavigation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [true]);
        TestSupport.PumpUi();
        TestSupport.Assert(form.QueryScopeForSmokeTest.Fields.All(field => field.Key != rKey) &&
            form.QueryFieldsForSmokeTest[1].SelectedIndex <= 0 && form.QueryInputsForSmokeTest[1].Text.Length == 0,
            "失效的记忆属性没有同时清除待执行输入和属性选择");
        TestSupport.Assert(form.StatusForSmokeTest.Contains("已清除对应查询条件", StringComparison.Ordinal),
            "清除失效记忆属性没有给出状态提示：" + form.StatusForSmokeTest);
        AssertVisibleQueryIds(form, [id], "清除失效属性丢失了其余仍然有效的身份条件");
        TestSupport.Assert(form.QueryInputsForSmokeTest[0].Text == "导入刷新", "失效属性清除误删其他组的输入");
        Console.WriteLine("RECORD_QUERY_UI_REMOVED_FIELD_OK remembered-pending-and-applied-R=cleared identity=retained warning=shown");
    }

    private static void ShowPersonBusiness(MainForm form)
    {
        form.SelectModuleForSmokeTest("人物");
        var selector = (ComboBox)typeof(MainForm).GetField("_tableSelector", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var business = selector.Items.Cast<object>().Single(item =>
            Convert.ToString(item.GetType().GetProperty("Key")!.GetValue(item)) == "__business");
        if (!Equals(selector.SelectedItem, business)) selector.SelectedItem = business;
        TestSupport.PumpUi();
    }

    internal static void CheckQueryLayout(MainForm form, string context)
    {
        var controls = Descendants(form).Where(control => control.Visible &&
            (control.Name is "RecordQuery1" or "RecordQuery2" or "RecordQuery3" or
                "RecordQueryField1" or "RecordQueryField2" or "RecordQueryField3" or "ExecuteQuery" or "ClearQuery")).ToArray();
        TestSupport.Assert(controls.Length == 8, context + " 未完整显示三组筛选及查询/清空按钮");
        foreach (var control in controls)
        {
            TestSupport.Assert(control.Width >= 24 && control.Height >= control.Font.Height &&
                control.Parent!.ClientRectangle.Contains(control.Bounds), context + " 查询控件被父容器裁切：" + control.Name);
            var rectangle = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            TestSupport.Assert(form.ClientRectangle.Contains(rectangle), context + " 查询控件超出窗口：" + control.Name);
        }
        for (var i = 0; i < 3; i++)
            TestSupport.Assert(form.QueryInputsForSmokeTest[i].Parent == form.QueryFieldsForSmokeTest[i].Parent &&
                form.QueryInputsForSmokeTest[i].Top == form.QueryFieldsForSmokeTest[i].Top,
                context + " 属性和关键词没有按整组排列：" + (i + 1));
        for (var i = 0; i < controls.Length; i++)
            for (var j = i + 1; j < controls.Length; j++)
                TestSupport.Assert(!controls[i].RectangleToScreen(controls[i].ClientRectangle)
                    .IntersectsWith(controls[j].RectangleToScreen(controls[j].ClientRectangle)),
                    context + " 查询控件重叠：" + controls[i].Name + "/" + controls[j].Name);
    }

    private static void Apply(MainForm form, params RecordQueryCondition[] conditions)
        => TestSupport.Assert(form.QueryConditionsForSmokeTest(conditions), "三组查询未执行：" + string.Join(";", conditions.Select(condition => condition.Text)));
    private static string FieldKey(MainForm form, string displayName)
        => form.QueryScopeForSmokeTest.Fields.Single(field => field.DisplayName == displayName).Key;
    private static void SetField(ComboBox combo, string displayName)
        => combo.SelectedItem = combo.Items.Cast<object>().Single(item => item.ToString() == displayName ||
            Convert.ToString(item.GetType().GetProperty("DisplayName")?.GetValue(item)) == displayName);
    private static void SelectDetailPage(MainForm form, string text)
    {
        var tab = Descendants(form.BusinessEditorForSmokeTest).OfType<TabControl>()
            .Single(control => control.TabPages.Cast<TabPage>().Any(page => page.Text == text));
        TestSupport.Assert(tab.Enabled, "零结果停用了详情子页导航：" + text);
        tab.SelectedTab = tab.TabPages.Cast<TabPage>().Single(page => page.Text == text); TestSupport.PumpUi();
    }
    private static int SelectedId(MainForm form) => Convert.ToInt32(form.EntityListForSmokeTest.SelectedItem!.GetType()
        .GetProperty("Id")!.GetValue(form.EntityListForSmokeTest.SelectedItem));
    private static int[] VisibleQueryIds(MainForm form) => form.EntityListForSmokeTest.Items.Cast<object>()
        .Select(item => Convert.ToInt32(item.GetType().GetProperty("Id")!.GetValue(item))).Order().ToArray();
    private static void PressQueryEnter(TextBox input)
    {
        typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, [new KeyEventArgs(Keys.Enter)]);
        TestSupport.PumpUi();
    }
    private static void ClearQuery(MainForm form)
    {
        var button = (Button)typeof(MainForm).GetField("_clearQueryButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        button.PerformClick(); TestSupport.PumpUi();
    }
    private static void ClickQuery(MainForm form)
    {
        var button = (Button)typeof(MainForm).GetField("_queryButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        button.PerformClick(); TestSupport.PumpUi();
    }
    private static void AssertVisibleQueryIds(MainForm form, IEnumerable<int> expected, string message)
    {
        var expectedIds = expected.Order().ToArray(); var actualIds = VisibleQueryIds(form);
        TestSupport.Assert(actualIds.SequenceEqual(expectedIds),
            $"{message}；expected=[{string.Join(',', expectedIds)}] actual=[{string.Join(',', actualIds)}] inputs=[{string.Join(';', form.QueryInputsForSmokeTest.Select(input => input.Text))}] summary=[{form.QuerySummaryForSmokeTest}]");
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
