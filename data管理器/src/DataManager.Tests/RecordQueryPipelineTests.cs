using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.Tests;

/// <summary>Exercises the actual shell pipeline, including asynchronous capture and navigation cancellation.</summary>
internal static class RecordQueryPipelineTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string source)
    {
        var copy = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        using var form = new MainForm(false)
        {
            StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000),
            ShowInTaskbar = false, Size = new(1280, 720)
        };
        try
        {
            CopySupplementalTables(source, copy);
            form.Show(); form.LoadProjectForSmokeTest(copy); TestSupport.PumpUi();
            EnsureLargeEffectFixture(form);
            var cache = WorkspaceReadCache.For(form.WorkspaceForSmokeTest);
            var (details, tables) = CheckEmptyActivation(form, cache);
            CheckStableExtensionNavigation(form);
            CheckSelectedColumnAndDraft(form, cache);
            CheckWidePhysicalTransitions(form);
            CheckLargeQueryCancellation(form, cache);
            Console.WriteLine($"RECORD_QUERY_PIPELINE_OK detailScopes={details} physicalTables={tables} " +
                "empty-zero-materialization/stable-extension-navigation/renamed-navigation-undo/" +
                "selected-single-column/duplicate-shared-column/real-ID/draft-undo/" +
                "wide-table-normal-history-scan-reload/large-capture-navigation-clear-edit-cancellation/" +
                "pending-input-protection/no-stale-overwrite/requery");
        }
        finally
        {
            form.Dispose();
            Directory.Delete(copy, true);
        }
    }

    private static void CopySupplementalTables(string source, string copy)
    {
        // CreateCoreCopy deliberately contains only the small core fixture. Keep every real
        // extension table in its original root/E5 location for this pipeline integration run.
        foreach (var directory in new[] { source, Path.Combine(source, "E5") }.Where(Directory.Exists))
        {
            var destination = Path.Combine(copy, Path.GetRelativePath(source, directory));
            Directory.CreateDirectory(destination);
            foreach (var path in Directory.EnumerateFiles(directory).Where(path =>
                         Path.GetExtension(path).Equals(".e5", StringComparison.OrdinalIgnoreCase)))
                File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), true);
        }
    }

    private static void EnsureLargeEffectFixture(MainForm form)
    {
        var workspace = form.WorkspaceForSmokeTest;
        if (workspace.Documents.ContainsKey("effect-archive")) return;
        var definition = workspace.Definitions.Single(table => table.Key == "effect-archive");
        var path = ProjectFileLocator.Resolve(workspace.RootPath, definition.FileName);
        if (File.Exists(path))
            throw new InvalidOperationException("实际拓展特效文件存在但未加载，不能用合成样本掩盖读取问题：" + path);
        // A missing optional archive is a valid original-base configuration. Exercise the large
        // async branch separately with the active profile's declared native geometry and codec.
        // Only the disposable copy receives this explicit synthetic archive.
        var bytes = new byte[checked((int)definition.EndOffsetExclusive)];
        for (var row = 0; row < definition.RowCount; row++)
            foreach (var field in definition.Fields.Where(field => field.Name.EndsWith("特效号", StringComparison.Ordinal) ||
                         field.Name.EndsWith("发生率", StringComparison.Ordinal)))
                if (field.Size == 1) bytes[checked((int)definition.DataOffset + row * definition.RowSize + field.Offset)] = 255;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        var document = new BinaryTableReader().Read(workspace.RootPath, definition);
        ((IDictionary<string, TableDocument>)workspace.Documents).Add(definition.Key, document);
        Console.WriteLine($"QUERY_PIPELINE_SYNTHETIC_EFFECT missing-optional-source rows={definition.RowCount} " +
            $"rowSize={definition.RowSize} fields={definition.Fields.Count} profile={workspace.ActiveProfile.ProfileId}; " +
            "async cancellation fixture only; original sample not modified");
    }

    private static (int Details, int Tables) CheckEmptyActivation(MainForm form, WorkspaceReadCache cache)
    {
        Assert(cache.Statistics.MaterializedColumns == 0, "打开项目的空查询已物化业务属性");
        var details = 0;
        var tables = 0;
        foreach (var module in new[] { "人物", "兵种系", "兵种", "宝物", "策略", "商店", "拓展数据" })
        {
            SelectBusiness(form, module);
            var editor = form.BusinessEditorForSmokeTest;
            if (module == "拓展数据")
            {
                var extend = editor.ExtendEditor ?? throw new InvalidOperationException("拓展数据宿主未创建。");
                foreach (var category in extend.DetailTabs.TabPages.Cast<TabPage>().ToArray())
                {
                    extend.DetailTabs.SelectedTab = category;
                    TestSupport.PumpUi();
                    Clear(form);
                    AssertEmptyInputs(form);
                    Assert(cache.Statistics.MaterializedColumns == 0, $"空查询进入拓展分类 {category.Text} 时物化了属性");
                    details++;
                }
            }
            else
            {
                foreach (var page in editor.DetailTabs.TabPages.Cast<TabPage>().ToArray())
                {
                    editor.DetailTabs.SelectedTab = page;
                    TestSupport.PumpUi();
                    Clear(form);
                    AssertEmptyInputs(form);
                    Assert(cache.Statistics.MaterializedColumns == 0, $"空查询进入 {module}/{page.Text} 时物化了属性");
                    details++;
                }
            }
        }

        foreach (var module in form.WorkspaceForSmokeTest.Modules)
        {
            SelectModule(form, module.Key);
            // Use the actual selector inventory: some displayed business projections intentionally
            // do not offer their underlying assignment tables as separate physical choices.
            var keys = Selector(form).Items.Cast<object>().Select(ChoiceKey)
                .Where(key => key != null && form.WorkspaceForSmokeTest.Documents.ContainsKey(key)).Cast<string>().ToArray();
            foreach (var key in keys)
            {
                SetTable(form, key);
                Clear(form);
                AssertEmptyInputs(form);
                var expected = cache.GetTableRecords(form.WorkspaceForSmokeTest.GetDocument(key));
                Assert(VisibleIds(form).SequenceEqual(expected.Select(record => record.Id).Order()),
                    $"空查询物理表 {key} 的导航没有保持真实 ID");
                Assert(cache.Statistics.MaterializedColumns == 0, $"空查询物理表 {key} 仍建立了属性快照");
                tables++;
            }
        }
        Assert(details > 7 && tables > 7, "空条件回归没有覆盖详情子页与物理表");
        Assert(!form.WorkspaceForSmokeTest.IsDirty, "空条件导航产生了草稿更改");
        return (details, tables);
    }

    private static void CheckSelectedColumnAndDraft(MainForm form, WorkspaceReadCache cache)
    {
        SelectModule(form, "人物"); SetTable(form, "persons"); Clear(form);
        var document = form.WorkspaceForSmokeTest.GetDocument("persons");
        var row = document.Data.Rows[42];
        var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
        var old = row["初始HP"];
        form.WorkspaceForSmokeTest.RecordCellEdit(document, 42, "初始HP", old, 60001);
        Invoke(form, "QueueQueryRefresh");
        TestSupport.PumpUi();
        var field = form.QueryScopeForSmokeTest.Fields.Single(candidate => candidate.Key.EndsWith(":初始HP", StringComparison.Ordinal));
        var before = cache.Statistics.MaterializedColumns;
        Assert(form.QueryConditionsForSmokeTest([new(field.Key, "60001"), new(field.Key, "600"), new(field.Key, "001")]),
            "指定属性三组查询未执行");
        Assert(cache.Statistics.MaterializedColumns == before + 1,
            "指定一个属性且重复三组时，应只物化一个共享列");
        Assert(VisibleIds(form).Contains(id) && form.PhysicalGridForSmokeTest.Rows.Cast<DataGridViewRow>()
            .Where(candidate => !candidate.IsNewRow).Any(candidate => candidate.DataBoundItem is DataRowView view &&
                Convert.ToInt32(view["ID"], CultureInfo.InvariantCulture) == id), "指定列未使用草稿及真实实体 ID");
        var after = cache.Statistics.MaterializedColumns;
        Assert(form.QueryConditionsForSmokeTest([new(field.Key, "60001"), new(field.Key, "60001")]), "热查询未执行");
        Assert(cache.Statistics.MaterializedColumns == after, "热查询或重复组重新读取了同一属性列");
        form.UndoDraftForSmokeTest(); TestSupport.PumpUi();
        Assert(!VisibleIds(form).Contains(id), "撤回属性编辑后仍显示旧草稿的查询匹配");
        Clear(form);
        Assert(VisibleIds(form).Contains(id), "清空未恢复撤回后的真实 ID");
    }

    private static void CheckStableExtensionNavigation(MainForm form)
    {
        SelectEffects(form); Clear(form);
        var extend = form.BusinessEditorForSmokeTest.ExtendEditor!;
        var original = extend.GetQueryNavigationRecords();
        Assert(original.Count > 0, "人物特效导航为空，无法验证切分类后的目录复用");
        var effects = extend.DetailTabs.SelectedTab;
        var strategies = extend.DetailTabs.TabPages.Cast<TabPage>()
            .Single(page => Equals(page.Tag, ExtendDataCategory.Strategies));
        extend.DetailTabs.SelectedTab = strategies;
        TestSupport.PumpUi();
        _ = extend.GetQueryNavigationRecords();
        extend.DetailTabs.SelectedTab = effects;
        TestSupport.PumpUi();
        Assert(ReferenceEquals(original, extend.GetQueryNavigationRecords()),
            "返回未变更的人物特效分类重新创建了导航目录，导致导航测宽缓存失效");

        var workspace = form.WorkspaceForSmokeTest;
        var persons = workspace.GetDocument("persons");
        var ids = original.Select(record => record.Id).ToHashSet();
        var row = persons.Data.Rows.Cast<DataRow>().First(candidate => ids.Contains(Convert.ToInt32(candidate["ID"], CultureInfo.InvariantCulture)));
        var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
        var oldName = Convert.ToString(row["名称"], CultureInfo.InvariantCulture) ?? string.Empty;
        var originalDisplayName = original.Single(record => record.Id == id).Name;
        var name = oldName == "QNavXYZ" ? "QNavABC" : "QNavXYZ";
        workspace.RecordCellEdit(persons, persons.Data.Rows.IndexOf(row), "名称", row["名称"], name);
        Invoke(form, "QueueQueryRefresh");
        TestSupport.PumpUi();
        var renamed = extend.GetQueryNavigationRecords();
        Assert(!ReferenceEquals(original, renamed) && renamed.Single(record => record.Id == id).Name == name &&
            form.EntityListForSmokeTest.Items.Cast<object>().Any(item =>
                item.ToString() == id.ToString(CultureInfo.InvariantCulture) + "：" + name),
            "人物名称草稿没有刷新拓展导航目录和实际左栏显示");

        form.UndoDraftForSmokeTest();
        TestSupport.PumpUi();
        var restored = extend.GetQueryNavigationRecords();
        Assert(!ReferenceEquals(renamed, restored) && restored.Single(record => record.Id == id).Name == originalDisplayName &&
            Convert.ToString(row["名称"], CultureInfo.InvariantCulture) == oldName && !workspace.IsDirty,
            "撤回人物名称后拓展导航仍保留过期名称，或留下意外草稿");
    }

    private static void CheckLargeQueryCancellation(MainForm form, WorkspaceReadCache cache)
    {
        var previousBudget = cache.BudgetBytes;
        // Prevent a previous all-properties capture from turning a cancellation scenario into a
        // fully synchronous hit. The normal production budget is restored after the scenarios.
        cache.BudgetBytes = 0;
        try
        {
            SelectEffects(form); Clear(form);
            var effects = form.WorkspaceForSmokeTest.GetDocument("effect-archive");
            var scope = form.QueryScopeForSmokeTest;
            Assert((long)effects.Data.Rows.Count * scope.Fields.Count > 20_000,
                "本基底的拓展特效规模不足以覆盖异步捕获分支");
            var originalIds = VisibleIds(form);
            var oldQuery = BeginLargeQuery(form);
            SelectBusiness(form, "人物");
            Assert(oldQuery.IsCancellationRequested, "切换业务页没有取消旧拓展属性捕获");
            var personsScope = form.QueryScopeForSmokeTest.Key;
            var personIds = VisibleIds(form);
            Pump(200);
            Assert(form.QueryScopeForSmokeTest.Key == personsScope && VisibleIds(form).SequenceEqual(personIds) &&
                !form.EmptyQueryForSmokeTest, "迟到的拓展查询结果覆盖了当前人物页");

            SelectEffects(form);
            // Reentering remembers its applied condition and may immediately resume that query.
            Clear(form);
            Assert(VisibleIds(form).SequenceEqual(originalIds), "清空未恢复拓展分类全体真实 ID");
            var clearQuery = BeginLargeQuery(form);
            Clear(form);
            Assert(clearQuery.IsCancellationRequested, "清空没有取消大范围属性捕获");
            Pump(200);
            Assert(VisibleIds(form).SequenceEqual(originalIds) && form.QueryInputsForSmokeTest.All(input => input.Text.Length == 0),
                "清空后的列表或输入被迟到查询结果覆盖");

            CheckPendingInputDuringCapture(form, originalIds);

            var editQuery = BeginLargeQuery(form);
            var row = effects.Data.Rows[0];
            var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
            var fieldName = effects.Data.Columns.Contains("槽250特效值") ? "槽250特效值" :
                effects.Definition.Fields.Last(field => field.Name.EndsWith("特效值", StringComparison.Ordinal)).Name;
            var old = row[fieldName];
            var value = Convert.ToInt32(old, CultureInfo.InvariantCulture) == 237 ? 238 : 237;
            form.WorkspaceForSmokeTest.RecordCellEdit(effects, 0, fieldName, old, value);
            // This is the same merged refresh requested by BusinessEditorPanel.Edited after a
            // successful UI flush. The draft mutation itself remains a real workspace edit.
            Invoke(form, "QueueQueryRefresh");
            Until(() => editQuery.IsCancellationRequested, "编辑没有取消旧修订的属性捕获");
            Clear(form);
            Pump(200);
            Assert(VisibleIds(form).SequenceEqual(originalIds) && Convert.ToInt32(row[fieldName], CultureInfo.InvariantCulture) == value,
                "取消旧修订查询时丢失草稿或被迟到结果覆盖");

            var field = form.QueryScopeForSmokeTest.Fields.Single(candidate => candidate.Key.EndsWith(":" + fieldName, StringComparison.Ordinal));
            Assert(form.QueryConditionsForSmokeTest([new(field.Key, value.ToString(CultureInfo.InvariantCulture))]),
                "取消后无法再次查询指定属性");
            var expected = effects.Data.Rows.Cast<DataRow>().Where(candidate => candidate.RowState != DataRowState.Deleted &&
                    Convert.ToString(candidate[fieldName], CultureInfo.InvariantCulture)!.Contains(value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
                .Select(candidate => Convert.ToInt32(candidate["ID"], CultureInfo.InvariantCulture)).Order().ToArray();
            Assert(VisibleIds(form).SequenceEqual(expected) && expected.Contains(id), "取消后再次查询未读取当前草稿和真实 ID");
            Clear(form);
        }
        finally { cache.BudgetBytes = previousBudget; }
    }

    private static void CheckWidePhysicalTransitions(MainForm form)
    {
        void SelectWide()
        {
            SelectModule(form, "拓展数据"); SetTable(form, "effect-archive"); Clear(form);
            var document = form.WorkspaceForSmokeTest.GetDocument("effect-archive");
            var grid = form.PhysicalGridForSmokeTest;
            Assert(document.Data.Columns.Count > 600 && !grid.AutoGenerateColumns &&
                grid.Columns.Count == document.Data.Columns.Count && ReferenceEquals(grid.DataSource, document.Data) &&
                grid.Columns.Cast<DataGridViewColumn>().Sum(column => (double)column.FillWeight) <= 65535,
                "1025 列拓展特效表未采用完整且有效的物理列绑定");
        }

        void CheckPersons()
        {
            SelectModule(form, "人物"); SetTable(form, "persons"); Clear(form);
            var grid = form.PhysicalGridForSmokeTest;
            Assert(grid.AutoGenerateColumns && grid.Columns.Contains("初始HP") &&
                grid.Columns.Contains("R形象编号") && grid.Columns.Contains("S形象编号") &&
                !grid.Columns.Contains("槽0特效值") &&
                ReferenceEquals(grid.DataSource, form.WorkspaceForSmokeTest.GetDocument("persons").Data),
                "离开宽物理表后普通人物列、形象投影或自动列生成未恢复");
        }

        SelectWide();
        SelectModule(form, "__scan");
        Assert(form.PhysicalGridForSmokeTest.AutoGenerateColumns &&
            form.PhysicalGridForSmokeTest.DataSource is DataTable { TableName: "扫描诊断" } &&
            form.PhysicalGridForSmokeTest.Columns.Contains("文件") &&
            !form.PhysicalGridForSmokeTest.Columns.Contains("槽0特效值"),
            "从宽物理表切至扫描诊断时仍保留手动宽表列");

        SelectWide();
        SelectModule(form, "__history");
        Until(() => form.PhysicalGridForSmokeTest.DataSource is DataTable { TableName: "历史记录" },
            "从宽物理表切至历史记录后未完成绑定");
        Assert(form.PhysicalGridForSmokeTest.AutoGenerateColumns &&
            form.PhysicalGridForSmokeTest.Columns.Contains("摘要") &&
            !form.PhysicalGridForSmokeTest.Columns.Contains("槽0特效值"),
            "从宽物理表切至历史记录时仍保留手动宽表列");
        CheckPersons();

        SelectWide();
        var oldTable = form.PhysicalGridForSmokeTest.DataSource;
        form.WorkspaceForSmokeTest.ReloadLoadedDocuments();
        Invoke(form, "RefreshCurrentView"); TestSupport.PumpUi();
        var reloaded = form.WorkspaceForSmokeTest.GetDocument("effect-archive");
        Assert(!ReferenceEquals(oldTable, reloaded.Data) && !form.PhysicalGridForSmokeTest.AutoGenerateColumns &&
            ReferenceEquals(form.PhysicalGridForSmokeTest.DataSource, reloaded.Data) &&
            form.PhysicalGridForSmokeTest.Columns.Count == reloaded.Data.Columns.Count,
            "重新读取宽物理表后没有通过宽列绑定器切换到替换后的文档");
        CheckPersons();
        Assert(!form.WorkspaceForSmokeTest.IsDirty, "宽表导航与重载产生了意外草稿");
    }

    private static void CheckPendingInputDuringCapture(MainForm form, int[] originalIds)
    {
        var extend = form.BusinessEditorForSmokeTest.ExtendEditor!;
        var editor = extend.Controls.Find("ExtendEffectValue0", true).OfType<TextBox>().Single();
        var originalText = editor.Text;
        var currentId = extend.CurrentId;
        var effects = form.WorkspaceForSmokeTest.GetDocument("effect-archive");
        var row = effects.Data.Rows.Cast<DataRow>().Single(candidate =>
            Convert.ToInt32(candidate["ID"], CultureInfo.InvariantCulture) == currentId);
        var originalValue = row["槽0特效值"];
        var wasDirty = form.WorkspaceForSmokeTest.IsDirty;

        // The query has yielded back to the UI. Its immutable result must not disable or
        // rebind an editor in which the user starts a new, still invalid, focused edit.
        _ = BeginLargeQuery(form);
        editor.Text = "invalid-query-pending";
        Assert(extend.HasPendingChanges, "异步查询期间新增的非法文本未标记为待编辑");
        Until(() => !form.QuerySummaryForSmokeTest.Contains("正在", StringComparison.Ordinal),
            "有未完成输入时，异步查询摘要没有结束运行状态");
        Assert(VisibleIds(form).SequenceEqual(originalIds) && !form.EmptyQueryForSmokeTest &&
            editor.Enabled && editor.Text == "invalid-query-pending" && extend.HasPendingChanges &&
            extend.CurrentId == currentId && Equals(row["槽0特效值"], originalValue) &&
            form.WorkspaceForSmokeTest.IsDirty == wasDirty,
            "异步零结果查询覆盖、禁用或提交了查询期间新增的未完成编辑");
        Assert(!(bool)Invoke(form, "ClearRecordQuery")!, "非法待编辑输入未阻止清空并重绑详情");
        Assert(editor.Text == "invalid-query-pending" && editor.Enabled && VisibleIds(form).SequenceEqual(originalIds),
            "清空验证失败后丢失文本、原结果或可修正的编辑区");
        editor.Text = originalText;
        Clear(form);
        Assert(VisibleIds(form).SequenceEqual(originalIds) && !extend.HasPendingChanges,
            "修正异步期间的非法输入后无法清空和恢复导航");
    }

    private static CancellationToken BeginLargeQuery(MainForm form)
    {
        TestSupport.PumpUi();
        for (var index = 0; index < 3; index++)
        {
            form.QueryFieldsForSmokeTest[index].SelectedIndex = 0;
            form.QueryInputsForSmokeTest[index].Text = index == 0 ? "__pipeline_no_literal_match__" : string.Empty;
        }
        Assert((bool)Invoke(form, "SubmitRecordQuery")!, "大范围查询无法提交");
        var cancellation = (CancellationTokenSource?)typeof(MainForm).GetField("_queryCancellation", PrivateInstance)!.GetValue(form);
        Assert(cancellation != null && form.QuerySummaryForSmokeTest.Contains("正在查询", StringComparison.Ordinal),
            "大范围查询没有进入可取消的异步捕获状态");
        return cancellation!.Token;
    }

    private static void SelectEffects(MainForm form)
    {
        SelectBusiness(form, "拓展数据");
        var extend = form.BusinessEditorForSmokeTest.ExtendEditor!;
        var effects = extend.DetailTabs.TabPages.Cast<TabPage>().Single(page => Equals(page.Tag, ExtendDataCategory.Effects));
        if (extend.DetailTabs.SelectedTab != effects) extend.DetailTabs.SelectedTab = effects;
        TestSupport.PumpUi();
    }

    private static void SelectBusiness(MainForm form, string module)
    { SelectModule(form, module); SetTable(form, "__business"); }

    private static void SelectModule(MainForm form, string module)
    {
        var tab = form.ModuleTabsForSmokeTest.TabPages.Cast<TabPage>().Single(page => Equals(page.Tag, module));
        if (form.ModuleTabsForSmokeTest.SelectedTab != tab) form.ModuleTabsForSmokeTest.SelectedTab = tab;
        TestSupport.PumpUi();
    }

    private static ComboBox Selector(MainForm form)
        => (ComboBox)typeof(MainForm).GetField("_tableSelector", PrivateInstance)!.GetValue(form)!;
    private static string? ChoiceKey(object choice) => choice.GetType().GetProperty("Key")?.GetValue(choice) as string;
    private static void SetTable(MainForm form, string key)
    {
        var selector = Selector(form);
        var choice = selector.Items.Cast<object>().Single(item => ChoiceKey(item) == key);
        if (!Equals(selector.SelectedItem, choice)) selector.SelectedItem = choice;
        TestSupport.PumpUi();
    }

    private static void Clear(MainForm form)
    {
        Assert((bool)Invoke(form, "ClearRecordQuery")!, "清空查询被拒绝");
        TestSupport.PumpUi();
    }
    private static object? Invoke(MainForm form, string method)
        => typeof(MainForm).GetMethod(method, PrivateInstance)!.Invoke(form, null);
    private static void AssertEmptyInputs(MainForm form)
        => Assert(form.QueryInputsForSmokeTest.All(input => input.Text.Length == 0) &&
            form.QueryFieldsForSmokeTest.All(combo => combo.SelectedIndex <= 0), "空查询导航出现了活动条件");
    private static int[] VisibleIds(MainForm form) => form.EntityListForSmokeTest.Items.Cast<object>()
        .Select(item => Convert.ToInt32(item.GetType().GetProperty("Id")!.GetValue(item), CultureInfo.InvariantCulture)).Order().ToArray();
    private static void Pump(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { TestSupport.PumpUi(); Thread.Sleep(1); }
    }
    private static void Until(Func<bool> completed, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!completed())
        {
            TestSupport.PumpUi(); Thread.Sleep(1);
            if (timer.ElapsedMilliseconds > 10000) throw new TimeoutException(message);
        }
    }
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);
}
