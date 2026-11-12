using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using RangeManager.App;

namespace DataManager.Tests;

/// <summary>Uses the actual host, its cached business selectors and exact temporary game files.</summary>
internal static class WorkspaceRangeCatalogTests
{
    private sealed record Check(string Name, string Detail);
    private static readonly List<Check> Checks = [];

    public static void Run(string path, string? output)
    {
        var repository = Directory.Exists(Path.Combine(path, "基底")) ? path : TestSupport.RepositoryRootFromBase(path);
        var sources = Directory.Exists(Path.Combine(path, "基底"))
            ? new[] { "洗隋", "加强版6.5未加密版", "复刻版6.6山贼" }.Select(name => Path.Combine(path, "基底", name)).ToArray()
            : [path];
        output ??= Path.Combine(repository, "data管理器", "验证", "2026-11-10-范围候选即时同步", "integration");
        Directory.CreateDirectory(output);
        Checks.Clear();
        var original = sources.SelectMany(SourceFiles).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(file => file, HashService.Sha256, StringComparer.OrdinalIgnoreCase);
        var passed = false;
        try
        {
            foreach (var source in sources) CheckHost(source, output);
            passed = true;
        }
        finally
        {
            var after = original.Keys.ToDictionary(file => file, HashService.Sha256, StringComparer.OrdinalIgnoreCase);
            var unchanged = original.All(pair => after[pair.Key] == pair.Value);
            File.WriteAllText(Path.Combine(output, "range-catalog-integration.json"), JsonSerializer.Serialize(new
            {
                Passed = passed && unchanged,
                OriginalSourcesUnchanged = unchanged,
                Checks,
                OriginalHashes = original,
                FinalHashes = after
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            TestSupport.Assert(unchanged, "范围候选验收改变了真实源基底");
        }
        Console.WriteLine($"RANGE_CATALOG_INTEGRATION_TESTS_OK bases={sources.Length} checks={Checks.Count}");
    }

    private static void CheckHost(string source, string output)
    {
        var root = CreateFixture(source);
        var other = CreateFixture(source);
        var form = new MainForm(openDefaultProject: false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Size = new Size(1280, 800)
        };
        try
        {
            var before = Files(root);
            form.Show(); form.LoadProjectForSmokeTest(root); Application.DoEvents();
            var workspace = form.WorkspaceForSmokeTest;
            var session = form.RangeSessionForSmokeTest ?? throw new InvalidOperationException("实际宿主没有范围会话");
            var catalog = Catalog(form);
            TestSupport.Assert(catalog.HasSession, "当前项目的共享目录没有绑定范围会话");
            var originalIds = session.Bindings.GroupBy(binding => binding.Key.Kind)
                .ToDictionary(group => group.Key, group => group.Select(binding => binding.Key.Id).Order().ToArray());

            // Build both selectors first; an add must refresh these same controls without reopening the project.
            form.SelectModuleForSmokeTest("兵种"); Application.DoEvents();
            var jobs = form.BusinessEditorForSmokeTest;
            var jobAttack = Picker(jobs, "攻击范围");
            var jobPierce = Picker(jobs, "穿透");
            form.SelectModuleForSmokeTest("策略"); Application.DoEvents();
            var strategies = form.BusinessEditorForSmokeTest;
            var strategyAttack = Picker(strategies, "施法范围");
            var strategyPierce = Picker(strategies, "穿透范围");
            AssertCatalog(form, jobs, strategies, "初始目录");
            TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo, "只读创建业务候选产生草稿");
            Pass(source, "shared-initial-domain", "兵种和策略预建选择器共用当前会话精确范围目录，兼容实际原值");

            form.SelectModuleForSmokeTest("__ranges");
            var revision = catalog.GetSnapshot(false).Revision;
            var attack = session.Add(RangeKind.Attack);
            var pierce = session.Add(RangeKind.Pierce);
            var sparse = session.Add(RangeKind.Pierce);
            TestSupport.Assert(session.UpdateText(attack, 0, "1,0;1,1", out var error), error);
            TestSupport.Assert(session.UpdateText(pierce, 0, "1,0", out error), error);
            TestSupport.Assert(session.UpdateText(sparse, 0, "2,2;1,1;0,2", out error), error);
            Application.DoEvents();
            TestSupport.Assert(catalog.GetSnapshot(false).Revision > revision,
                "新增及几何编辑没有递增目录版本");
            AssertCatalog(form, jobs, strategies, "新增未保存");
            TestSupport.Assert(jobAttack.Combo.Options.Any(option => option.Id == attack.Id) &&
                jobPierce.Combo.Options.Any(option => option.Id == sparse.Id) &&
                strategyPierce.Combo.Options.Any(option => option.Id == sparse.Id),
                "缓存业务页激活后没有立即收到新增草稿ID");
            TestSupport.Assert(ReferenceEquals(jobAttack, Picker(jobs, "攻击范围")) &&
                ReferenceEquals(strategyPierce, Picker(strategies, "穿透范围")), "新增目录重建了已有业务选择器");
            if (session.Game.Version == EngineVersion.V66)
                TestSupport.Assert(!catalog.GetSnapshot(false).Ids.Contains(0x0B), "6.6稀疏穿透目录错误填补了不存在的0x0B");
            AssertFiles(before, root, "新增草稿或目录刷新提前写盘");
            Pass(source, "draft-ids-and-sparse-hole", $"未保存攻击{attack.Id}和穿透{pierce.Id}/{sparse.Id}立即进入两业务页；6.6保留0x0B空洞");

            // The same ID stays typed while geometry changes. Both its image and revision must change.
            form.SelectModuleForSmokeTest("兵种");
            form.SelectModuleForSmokeTest("兵种"); Application.DoEvents();
            var originalInput = jobPierce.Combo.Text;
            jobPierce.Combo.Text = sparse.Id.ToString();
            var imageBefore = ExpectedImageHash(catalog, false, sparse.Id);
            WaitFor(() => ImageHash(jobPierce) == imageBefore, "新增未保存穿透没有通过业务页预览当前草稿");
            TestSupport.Assert(session.UpdateText(sparse, 0, "1,0;1,1", out error), error);
            var imageAfter = ExpectedImageHash(catalog, false, sparse.Id);
            TestSupport.Assert(imageAfter != imageBefore, "预览验收没有产生不同图形");
            WaitFor(() => ImageHash(jobPierce) == imageAfter, "同ID修改后业务预览仍显示旧几何");
            TestSupport.Assert(jobPierce.Combo.Text == sparse.Id.ToString(), "预览刷新覆盖正在输入的范围编号");
            jobPierce.Combo.SetBoundValue(originalInput);
            TestSupport.Assert(jobs.PreparePendingEdits(), "恢复预览输入后业务字段不能提交");
            revision = catalog.GetSnapshot(false).Revision;
            form.UndoDraftForSmokeTest(); Application.DoEvents();
            TestSupport.Assert(catalog.GetSnapshot(false).Revision > revision &&
                ExpectedImageHash(catalog, false, sparse.Id) == imageBefore,
                "全局撤回没有恢复同ID几何或目录版本倒退");
            revision = catalog.GetSnapshot(false).Revision;
            form.RedoDraftForSmokeTest(); Application.DoEvents();
            TestSupport.Assert(catalog.GetSnapshot(false).Revision > revision &&
                ExpectedImageHash(catalog, false, sparse.Id) == imageAfter,
                "全局前进没有恢复同ID几何或目录版本倒退");
            AssertFiles(before, root, "预览/撤回/前进提前写盘");
            Pass(source, "draft-preview-and-revision", "未保存几何可预览；同ID改形自动刷新且保留输入，全局撤回/前进使用递增版本");

            session.Delete(sparse); Application.DoEvents();
            TestSupport.Assert(catalog.GetSnapshot(false).Ids.Contains(sparse.Id) &&
                session.Preview(sparse, RangePreviewContext.Default).Count == 0,
                "清空生成元错误删除了目录ID或空几何没有生效");
            var emptyHash = ExpectedImageHash(catalog, false, sparse.Id);
            TestSupport.Assert(emptyHash != imageAfter, "清空生成元没有刷新为空范围预览");
            session.Restore(sparse); Application.DoEvents();
            TestSupport.Assert(!catalog.GetSnapshot(false).Ids.Contains(sparse.Id), "恢复未保存新增范围没有移除其ID");
            form.UndoDraftForSmokeTest(); Application.DoEvents();
            TestSupport.Assert(catalog.GetSnapshot(false).Ids.Contains(sparse.Id) &&
                session.Preview(sparse, RangePreviewContext.Default).Count == 0, "撤回恢复没有还原空范围目录项");
            form.RedoDraftForSmokeTest(); Application.DoEvents();
            TestSupport.Assert(!catalog.GetSnapshot(false).Ids.Contains(sparse.Id), "前进恢复没有再次移除新增项");
            AssertCatalog(form, jobs, strategies, "清空与恢复后");
            TestSupport.Assert(session.UpdateText(pierce, 0, string.Empty, out error), error);
            sparse = session.Add(RangeKind.Pierce);
            TestSupport.Assert(session.UpdateText(sparse, 0, "2,2;1,1;0,2", out error), error);
            AssertCatalog(form, jobs, strategies, "重建新增项");
            Pass(source, "clear-restore-undo-redo", "空范围保留ID与空预览；恢复未保存新增项移除ID，撤回/前进同步两业务页");

            // One typed field and one native dropdown selection in each business page.
            form.SelectModuleForSmokeTest("兵种");
            var growthOriginal = Number(workspace, "detailed-job-growth", "攻击范围");
            jobAttack.Combo.Text = attack.Id.ToString();
            TestSupport.Assert(Number(workspace, "detailed-job-growth", "攻击范围") == growthOriginal && jobs.HasPendingChanges,
                "纯键入编号提前提交普通字段或丢失待提交输入");
            SelectDropdown(jobPierce.Combo, sparse.Id);
            TestSupport.Assert(Number(workspace, "detailed-job-penetration", "穿透") == sparse.Id,
                "兵种原生下拉选择事件未提交新增ID");
            if (session.Game.Version == EngineVersion.V66)
            {
                ShowPicker(jobPierce);
                Capture(form, output, "range-catalog-6.6-job-new-13.png");
            }
            TestSupport.Assert(jobs.PreparePendingEdits(), "兵种新增范围字段未通过输入验证");
            form.SelectModuleForSmokeTest("策略");
            SelectDropdown(strategyAttack.Combo, attack.Id);
            strategyPierce.Combo.Text = sparse.Id.ToString();
            var expectedAttack = session.Preview(attack, RangePreviewContext.Default).ToHashSet();
            var expectedPierce = session.Preview(sparse, RangePreviewContext.Default).ToHashSet();
            AssertFiles(before, root, "选择业务范围字段提前写盘");
            var saved = form.SavePendingForSmokeTest();
            TestSupport.Assert(saved.Saved && saved.History?.Files.Count == 4,
                "新增范围与四普通字段没有联合保存：" + string.Join("；", saved.Messages));
            TestSupport.Assert(!workspace.IsDirty && !session.IsDirty, "联合保存后仍残留草稿");
            var savedBytes = Files(root);
            AssertReferences(workspace, attack.Id, sparse.Id);
            using (var reopened = new RangeEditSession(root))
            {
                TestSupport.Assert(reopened.Preview(attack, RangePreviewContext.Default).ToHashSet().SetEquals(expectedAttack) &&
                    reopened.Preview(sparse, RangePreviewContext.Default).ToHashSet().SetEquals(expectedPierce) &&
                    reopened.Preview(pierce, RangePreviewContext.Default).Count == 0,
                    "保存重开丢失新几何或有效空范围");
            }
            var reopenedWorkspace = new ProjectWorkspace(); reopenedWorkspace.Open(root);
            AssertReferences(reopenedWorkspace, attack.Id, sparse.Id);
            AssertCatalog(form, jobs, strategies, "保存后");
            Pass(source, "typed-dropdown-joint-save", "十进制输入/实际下拉提交四字段，DATA+EXE+Hitarea+Effarea同事务保存，重开含稀疏/空范围");

            TestSupport.RestoreSavedHistory(form, redo: false);
            AssertFiles(before, root, "历史撤回没有逐字节恢复联合文件");
            TestSupport.Assert(catalog.GetSnapshot(true).Ids.SequenceEqual(originalIds[RangeKind.Attack]) &&
                catalog.GetSnapshot(false).Ids.SequenceEqual(originalIds[RangeKind.Pierce]),
                "历史撤回后目录没有回到原生精确集合");
            AssertCatalog(form, jobs, strategies, "历史撤回后");
            TestSupport.RestoreSavedHistory(form, redo: true);
            AssertFiles(savedBytes, root, "历史前进没有逐字节恢复联合文件");
            AssertCatalog(form, jobs, strategies, "历史前进后");
            AssertReferences(workspace, attack.Id, sparse.Id);
            TestSupport.Assert(catalog.GetSnapshot(false).Ids.Contains(sparse.Id), "历史前进丢失managed目录");
            Pass(source, "saved-history-catalog", "保存历史整批撤回/前进逐字节恢复，目录及业务候选随范围会话同步");

            // A stale task must not make the next clean project's catalogue contain the previous IDs.
            var oldPreview = catalog.DecodePreviewAsync(false, sparse.Id, CancellationToken.None);
            form.LoadProjectForSmokeTest(other); Application.DoEvents();
            var next = form.RangeSessionForSmokeTest!;
            TestSupport.Assert(!ReferenceEquals(session, next) && ReferenceEquals(catalog, Catalog(form)) &&
                catalog.GetSnapshot(false).RootPath == other &&
                !catalog.GetSnapshot(false).Ids.Contains(sparse.Id), "切换项目保留旧会话目录或替换了共享实例");
            try { using var late = Await(oldPreview); }
            catch (OperationCanceledException) { }
            form.SelectModuleForSmokeTest("兵种"); Application.DoEvents();
            var nextJobs = form.BusinessEditorForSmokeTest;
            form.SelectModuleForSmokeTest("策略"); Application.DoEvents();
            var nextStrategies = form.BusinessEditorForSmokeTest;
            AssertCatalog(form, nextJobs, nextStrategies, "项目切换后");
            TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty && !form.WorkspaceForSmokeTest.Commands.CanUndo &&
                !form.WorkspaceForSmokeTest.Commands.CanRedo, "项目切换后旧命令或草稿串入新项目");
            AssertFiles(savedBytes, root, "切换项目改变已保存前一项目");
            Pass(source, "project-switch", "同共享目录实例切换clean项目，旧ID/命令清除，迟到预览不能污染新目录");

            if (next.Game.Version == EngineVersion.V66)
                Capture(form, output, "range-catalog-6.6-clean-strategies.png");
            CheckSaveFailureLocation(source, form, other);
        }
        finally { form.Dispose(); Directory.Delete(root, true); Directory.Delete(other, true); }
    }

    private static void AssertCatalog(MainForm form, BusinessEditorPanel jobs, BusinessEditorPanel strategies, string scenario)
    {
        Application.DoEvents();
        var session = form.RangeSessionForSmokeTest!;
        var catalog = Catalog(form);
        foreach (var attack in new[] { true, false })
        {
            var kind = attack ? RangeKind.Attack : RangeKind.Pierce;
            var ids = session.Bindings.Where(binding => binding.Key.Kind == kind).Select(binding => binding.Key.Id).Order().ToArray();
            TestSupport.Assert(catalog.GetSnapshot(attack).Ids.SequenceEqual(ids), scenario + "共享目录不是当前会话精确ID集合");
            foreach (var target in attack
                ? new[] { (jobs, "detailed-job-growth", "攻击范围"), (strategies, "strategies", "施法范围") }
                : new[] { (jobs, "detailed-job-penetration", "穿透"), (strategies, "strategies", "穿透范围") })
            {
                // Hidden pages defer control work. Exercise the real user
                // path: activate the cached page, then inspect the same picker.
                form.SelectModuleForSmokeTest(ReferenceEquals(target.Item1, jobs) ? "兵种" : "策略");
                Application.DoEvents();
                var actual = Picker(target.Item1, target.Item3).Combo.Options.Select(option => option.Id).Order().ToArray();
                var expected = ids.Concat(form.WorkspaceForSmokeTest.GetDocument(target.Item2).Data.Rows.Cast<System.Data.DataRow>()
                    .Select(row => Convert.ToInt32(row[target.Item3]))).Distinct().Order().ToArray();
                TestSupport.Assert(actual.SequenceEqual(expected),
                    $"{scenario} {target.Item2}/{target.Item3}候选与实际目录/兼容原值不一致：expected={string.Join(',', expected)} actual={string.Join(',', actual)}");
            }
        }
    }

    private static WorkspaceRangeCatalog Catalog(MainForm form)
        => (WorkspaceRangeCatalog)(typeof(MainForm).GetField("_rangeCatalog", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form)
            ?? throw new InvalidOperationException("宿主没有共享WorkspaceRangeCatalog"));

    private static RangePreviewPicker Picker(Control panel, string field)
        => Descendants(panel).OfType<RangePreviewPicker>().Single(picker => picker.FieldName == field);

    private static void SelectDropdown(SearchableIdComboBox combo, int id)
    {
        combo.SetBoundValue(string.Empty, filterOptions: true);
        combo.SelectedItem = combo.Items.Cast<object>().OfType<DataManager.App.IdNameOption>().Single(option => option.Id == id);
        typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(combo, [EventArgs.Empty]);
        Application.DoEvents();
        TestSupport.Assert(combo.TryGetSelectedId(out var selected) && selected == id, "下拉选择丢失ID");
    }

    private static void CheckSaveFailureLocation(string source, MainForm form, string root)
    {
        var before = Files(root);
        var session = form.RangeSessionForSmokeTest!;
        var workspace = form.WorkspaceForSmokeTest;
        var key = session.Add(RangeKind.Pierce);
        const string overflow = "1,2;1,3;1,4;1,5;1,6;1,7;1,8";
        TestSupport.Assert(session.UpdateText(key, 0, overflow, out var error), error);
        TestSupport.Assert(session.Preview(key, RangePreviewContext.Default).Count == 28, "超限测试未产生28个额外坐标");
        var original = Number(workspace, "persons", "武力");
        var edited = (original + 1) % 256;
        workspace.ApplyCompositeTransaction("保存失败定位验收的普通DATA草稿", [new("persons", 0, "武力", edited)]);
        var host = form.RangeHostForSmokeTest!;
        form.SelectModuleForSmokeTest("__ranges"); host.Tabs.SelectedIndex = 0;
        form.SelectModuleForSmokeTest("策略"); Application.DoEvents();
        var rejected = form.SavePendingForSmokeTest(); Application.DoEvents();
        TestSupport.Assert(!rejected.Saved && workspace.IsDirty && session.IsDirty &&
            Number(workspace, "persons", "武力") == edited && session.GetText(key, 0) == overflow,
            "超24坐标保存失败丢失了普通或范围草稿");
        AssertFiles(before, root, "超24坐标保存失败仍改写磁盘");
        TestSupport.Assert(rejected.Messages.Any(message => message.Contains("28", StringComparison.Ordinal) &&
            message.Contains("24", StringComparison.Ordinal)) && session.LastValidationFailureKey == key &&
            session.LastValidationFailure?.GroupIndex == null,
            "超限错误没有保留准确失败ID或错误消息");
        AssertFailureFocus(form, key, overflow);
        Pass(source, "save-overflow-focus", "28坐标保存被原子拒绝，普通/范围草稿保留；从策略页自动定位穿透ID及生成元修改栏");

        TestSupport.Assert(session.UpdateText(key, 0, "2,2;1,1;0,2", out error), error);
        TestSupport.Assert(session.LastValidationFailure == null, "修复超限后仍保留过时失败定位");
        TestSupport.Assert(!session.UpdateText(key, 0, "invalid,coordinate", out error), "无效文本测试未拒绝坐标");
        form.SelectModuleForSmokeTest("策略");
        rejected = form.SavePendingForSmokeTest(); Application.DoEvents();
        TestSupport.Assert(!rejected.Saved && session.LastValidationFailure?.GroupIndex == 0,
            "无效组文本保存失败没有记住准确分组");
        AssertFailureFocus(form, key, "invalid,coordinate");
        TestSupport.Assert(host.PierceEditor.GroupSelectorForTest.SelectedIndex == 0, "无效文本没有定位第1组");
        AssertFiles(before, root, "无效组文本保存失败仍改写磁盘");
        TestSupport.Assert(session.UpdateText(key, 0, "2,2;1,1;0,2", out error), error);
        var saved = form.SavePendingForSmokeTest(); Application.DoEvents();
        TestSupport.Assert(saved.Saved && !workspace.IsDirty && !session.IsDirty && session.LastValidationFailure == null,
            "修复范围草稿后不能成功保存或未清除失败定位：" + string.Join("；", saved.Messages));
        using var reopened = new RangeEditSession(root);
        var reopenedWorkspace = new ProjectWorkspace(); reopenedWorkspace.Open(root);
        TestSupport.Assert(reopened.Preview(key, RangePreviewContext.Default).Count == 12 &&
            Number(reopenedWorkspace, "persons", "武力") == edited, "修复后联合保存无法复读普通与范围修改");
        Pass(source, "save-group-focus-repair", "无效文本定位准确分组；修复为12坐标后清除旧错误并联合保存，重开保留普通与范围修改");
    }

    private static void AssertFailureFocus(MainForm form, RangeKey key, string text)
    {
        var host = form.RangeHostForSmokeTest!;
        TestSupport.Assert(Equals(form.ModuleTabsForSmokeTest.SelectedTab?.Tag, "__ranges") &&
            host.Tabs.SelectedIndex == 1 && host.PierceEditor.SelectedKey == key &&
            host.PierceEditor.GeneratorsForTest.Text == text &&
            host.PierceEditor.GeneratorsForTest.Focused &&
            !string.IsNullOrWhiteSpace(host.PierceEditor.ValidationMessage),
            "顶部保存失败没有切换到准确穿透页/范围/文本或没有聚焦修改栏");
    }

    private static void ShowPicker(Control picker)
    {
        for (Control? current = picker; current != null; current = current.Parent)
            if (current is TabPage page && page.Parent is TabControl tabs) tabs.SelectedTab = page;
        Application.DoEvents();
        for (Control? current = picker.Parent; current != null; current = current.Parent)
            if (current is ScrollableControl scroll && scroll.AutoScroll) scroll.ScrollControlIntoView(picker);
        Application.DoEvents();
    }

    private static void Capture(MainForm form, string output, string name)
    {
        Application.DoEvents();
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        image.Save(Path.Combine(output, name), ImageFormat.Png);
    }

    private static string ExpectedImageHash(WorkspaceRangeCatalog catalog, bool attack, int id)
    {
        using var image = Await(catalog.DecodePreviewAsync(attack, id, CancellationToken.None));
        TestSupport.Assert(image != null, "当前范围草稿不能生成预览：" + id);
        return BitmapHash(image!);
    }

    private static string? ImageHash(RangePreviewPicker picker)
    {
        var image = typeof(RangePreviewSurface).GetField("_image", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker.PreviewBox) as Bitmap;
        return image == null ? null : BitmapHash(image);
    }

    private static string BitmapHash(Bitmap bitmap)
    {
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                BitConverter.TryWriteBytes(pixels.AsSpan((y * bitmap.Width + x) * 4, 4), bitmap.GetPixel(x, y).ToArgb());
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static T Await<T>(Task<T> task)
    {
        WaitFor(() => task.IsCompleted, "范围预览异步请求没有完成");
        return task.GetAwaiter().GetResult();
    }

    private static void WaitFor(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        do { Application.DoEvents(); if (condition()) return; Thread.Sleep(10); } while (timer.Elapsed < TimeSpan.FromSeconds(12));
        throw new InvalidOperationException(message);
    }

    private static int Number(ProjectWorkspace workspace, string table, string field)
        => Convert.ToInt32(workspace.GetDocument(table).Data.Rows[0][field]);

    private static void AssertReferences(ProjectWorkspace workspace, int attack, int pierce)
    {
        TestSupport.Assert(Number(workspace, "detailed-job-growth", "攻击范围") == attack &&
            Number(workspace, "detailed-job-penetration", "穿透") == pierce &&
            Number(workspace, "strategies", "施法范围") == attack &&
            Number(workspace, "strategies", "穿透范围") == pierce, "兵种/策略四个范围引用值不一致");
    }

    private static string CreateFixture(string source)
    {
        var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        foreach (var name in new[] { "Hitarea.e5", "Effarea.e5", "Spalet.e5" })
        {
            var file = new[] { Path.Combine(source, "E5", name), Path.Combine(source, name) }.FirstOrDefault(File.Exists);
            if (file == null) continue;
            var target = Path.Combine(root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        return root;
    }

    private static IEnumerable<string> SourceFiles(string root)
        => new[] { "Ekd5.exe", "Data.e5", "Imsg.e5", "Star.e5", "Font.e5", "Hitarea.e5", "Effarea.e5", "Spalet.e5" }
            .SelectMany(name => new[] { Path.Combine(root, name), Path.Combine(root, "E5", name) }).Where(File.Exists);

    private static Dictionary<string, byte[]> Files(string root)
        => SourceFiles(root).ToDictionary(file => Path.GetRelativePath(root, file), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertFiles(Dictionary<string, byte[]> expected, string root, string message)
    {
        foreach (var file in expected)
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(root, file.Key)).SequenceEqual(file.Value), message + "：" + file.Key);
    }

    private static IEnumerable<Control> Descendants(Control control)
        => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static void Pass(string source, string name, string detail)
    {
        Checks.Add(new Check(Path.GetFileName(source) + "/" + name, detail));
        Console.WriteLine("PASS " + Checks[^1].Name + ": " + detail);
    }
}
