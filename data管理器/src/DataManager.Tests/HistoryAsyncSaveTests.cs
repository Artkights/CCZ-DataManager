using System.Reflection;
using Ccz.HistoryStorage;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.History;
using DataManager.Core.Models;
using RangeManager.App;

namespace DataManager.Tests;

internal static class HistoryAsyncSaveTests
{
    public static void Run(string basePath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var messageOwner = new Form(); _ = messageOwner.Handle;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                CoreAsyncLifecycle(basePath);
                MainWindowLifecycle(basePath);
                MainWindowRangeLifecycle(basePath);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("异步保存消息泵测试超时。");
        if (failure != null) throw new InvalidOperationException("异步保存测试失败。", failure);
        Console.WriteLine("HISTORY_ASYNC_SAVE_OK (STA context, frozen background payload, UI transaction/accept, reentry/close protection, late-ledger rollback, synchronous compatibility, actual top-button mixed range/business/Data save/reopen/undo/redo)");
    }

    private static void CoreAsyncLifecycle(string source)
    {
        using var owned = CreateOwnedCore(source);
        var root = owned.RootPath;
        try
        {
            var threadId = Environment.CurrentManagedThreadId;
            var workspace = new ProjectWorkspace(); workspace.Open(root);
            var participant = new ThreadBoundParticipant(root, threadId); workspace.RegisterDraftParticipant(participant);
            Edit(workspace);
            using var events = HistoryLedger.ObserveAppends(point => TestSupport.Assert(Environment.CurrentManagedThreadId == threadId,
                "持锁提交和历史追加没有返回原STA上下文"));
            var save = workspace.SaveAsync();
            TestSupport.Assert(!save.IsCompleted, "异步保存没有让出UI消息泵");
            TestSupport.Assert(!workspace.Save().Saved, "异步准备期间允许重入同步保存");
            var refused = false;
            try { workspace.Open(root); } catch (InvalidOperationException) { refused = true; }
            TestSupport.Assert(refused, "异步准备期间允许切换工作区");
            Pump(save);
            TestSupport.Assert(save.Result.Saved && participant.Accepted && !workspace.IsDirty, "异步保存没有接受全部草稿");
            TestSupport.Assert(workspace.UndoSaved().Success && workspace.RedoSaved().Success, "异步保存的同步撤回前进失效");

            Edit(workspace);
            var before = File.ReadAllBytes(Path.Combine(root, "Data.e5"));
            Task<SaveResult> failed;
            using (HistoryV3TestSupport.LockAtAppend("Commit"))
            {
                failed = workspace.SaveAsync(); Pump(failed);
                TestSupport.Assert(!failed.Result.Saved && workspace.IsDirty && File.ReadAllBytes(Path.Combine(root, "Data.e5")).SequenceEqual(before),
                    "异步保存的最后历史提交失败没有保留草稿并恢复文件");
            }
            workspace.History.Recover(root);
            TestSupport.Assert(workspace.Save().Saved, "异步失败恢复后同步Save不再兼容");
            owned.Complete();
        }
        catch (Exception error) { RetainFailure(owned, error); throw; }
    }

    private static void MainWindowLifecycle(string source)
    {
        using var owned = CreateOwnedCore(source);
        var root = owned.RootPath;
        try
        {
            using var form = new MainForm(); _ = form.Handle;
            form.LoadProjectForSmokeTest(root);
            var workspace = form.WorkspaceForSmokeTest;
            workspace.RegisterDraftParticipant(new ThreadBoundParticipant(root, Environment.CurrentManagedThreadId));
            Edit(workspace);
            var type = typeof(MainForm);
            var save = type.GetMethod("SaveProject", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var busy = type.GetField("_historyIoBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            save.Invoke(form, null);
            TestSupport.Assert((bool)busy.GetValue(form)! && !form.Enabled, "主窗保存没有在后台准备阶段保护输入");
            save.Invoke(form, null);
            var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
            type.GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(object), typeof(FormClosingEventArgs)], null)!.Invoke(form, [form, closing]);
            TestSupport.Assert(closing.Cancel, "正在保存时允许关闭主窗");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while ((bool)busy.GetValue(form)!)
            {
                Application.DoEvents(); Thread.Sleep(1);
                if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("主窗异步保存未完成。");
            }
            TestSupport.Assert(form.Enabled && !workspace.IsDirty && workspace.GetHistoryMetadata().Count == 1,
                "主窗异步保存未恢复输入、草稿未接受或重复保存");
            foreach (var redo in new[] { false, true })
            {
                type.GetMethod("RestoreHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [redo]);
                timer.Restart();
                while ((bool)busy.GetValue(form)!)
                {
                    Application.DoEvents(); Thread.Sleep(1);
                    if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("主窗异步恢复未完成。");
                }
                TestSupport.Assert(form.Enabled && workspace.GetHistoryMetadata().Single().Status ==
                    (redo ? HistoryStatus.Active : HistoryStatus.Reverted), "主窗后台载荷恢复未更新准确状态或未恢复输入");
            }
            owned.Complete();
        }
        catch (Exception error) { RetainFailure(owned, error); throw; }
    }

    private static void Edit(ProjectWorkspace workspace)
    {
        var row = workspace.GetDocument("persons").Data.Rows[0];
        row["武力"] = (Convert.ToInt32(row["武力"]) + 1) % 256;
    }

    private static void MainWindowRangeLifecycle(string source)
    {
        var sourceHashes = RangeFiles(source).ToDictionary(path => path, HashService.Sha256, StringComparer.OrdinalIgnoreCase);
        using var owned = CreateOwnedCore(source, restoreKnown65Snapshot: false);
        var root = owned.RootPath;
        try
        {
            foreach (var path in RangeFiles(source).Where(path => new[] { "Hitarea.e5", "Effarea.e5", "Spalet.e5" }
                .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
            {
                var target = Path.Combine(root, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }
            using var form = new MainForm(openDefaultProject: false)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                ShowInTaskbar = false, Size = new Size(1280, 800)
            };
            form.Show(); form.LoadProjectForSmokeTest(root); Application.DoEvents();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var workspace = form.WorkspaceForSmokeTest;
            var session = form.RangeSessionForSmokeTest ?? throw new InvalidOperationException("异步范围验收未创建真实范围参与者。");
            var before = ReadRangeFiles(root);
            var originalReferences = ReferenceFields.ToDictionary(field => field, field => Number(workspace, field.Table, field.Field));
            var originalPower = Number(workspace, "persons", "武力");
            var expectedPower = (originalPower + 1) % 256;

            // Build the same cached pickers before adding new draft IDs; the
            // selectors must receive the session revision without reopening.
            form.SelectModuleForSmokeTest("兵种"); Application.DoEvents();
            var jobs = form.BusinessEditorForSmokeTest;
            var jobAttack = RangePicker(jobs, "攻击范围");
            var jobPierce = RangePicker(jobs, "穿透");
            form.SelectModuleForSmokeTest("策略"); Application.DoEvents();
            var strategies = form.BusinessEditorForSmokeTest;
            var strategyAttack = RangePicker(strategies, "施法范围");
            var strategyPierce = RangePicker(strategies, "穿透范围");
            form.SelectModuleForSmokeTest("__ranges");
            var attack = session.Add(RangeKind.Attack);
            var pierce = session.Add(RangeKind.Pierce);
            TestSupport.Assert(session.UpdateText(attack, 0, "1,0;1,1", out var error), error);
            TestSupport.Assert(session.UpdateText(pierce, 0, "2,2;1,1;0,2", out error), error);
            var expectedAttack = session.Preview(attack, RangePreviewContext.Default).ToHashSet();
            var expectedPierce = session.Preview(pierce, RangePreviewContext.Default).ToHashSet();

            form.SelectModuleForSmokeTest("兵种"); Application.DoEvents();
            TestSupport.Assert(jobAttack.Combo.Options.Any(option => option.Id == attack.Id) &&
                jobPierce.Combo.Options.Any(option => option.Id == pierce.Id), "异步保存前新增范围没有立即进入已有兵种选择器。");
            jobAttack.Combo.Text = attack.Id.ToString();
            jobPierce.Combo.Text = pierce.Id.ToString();
            TestSupport.Assert(jobs.PreparePendingEdits(), "新增范围的兵种引用未通过真实业务输入验证。");
            form.SelectModuleForSmokeTest("策略"); Application.DoEvents();
            TestSupport.Assert(strategyAttack.Combo.Options.Any(option => option.Id == attack.Id) &&
                strategyPierce.Combo.Options.Any(option => option.Id == pierce.Id), "异步保存前新增范围没有立即进入已有策略选择器。");
            strategyAttack.Combo.Text = attack.Id.ToString();
            strategyPierce.Combo.Text = pierce.Id.ToString();
            TestSupport.Assert(strategies.HasPendingChanges, "顶部异步保存验收没有保留实际待提交业务输入。");
            workspace.ApplyCompositeTransaction("异步顶部保存同时修改普通DATA字段", [new("persons", 0, "武力", expectedPower)]);
            AssertRangeFiles(before, root, "异步顶部保存之前混合草稿提前写盘");

            var uiThread = Environment.CurrentManagedThreadId;
            var appendKinds = new List<string>();
            using (HistoryLedger.ObserveAppends(point =>
            {
                TestSupport.Assert(Environment.CurrentManagedThreadId == uiThread, "范围冻结参与者的事务追加没有返回UI线程。");
                appendKinds.Add(point.Kind);
            }))
            {
                var topSave = (Button)typeof(MainForm).GetField("_saveButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                TestSupport.Assert(topSave.Enabled && topSave.Visible, "实际顶部保存按钮不可操作。");
                // DoEvents starts and tears down a temporary message loop. Real
                // application runs keep this context throughout Application.Run.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                topSave.PerformClick();
                PumpMainWindow(form);
            }
            TestSupport.Assert(workspace.GetHistoryMetadata().Count == 1,
                "实际顶部按钮未创建混合历史；status=" + form.StatusForSmokeTest + "; appends=" + string.Join(',', appendKinds) +
                "; dirty=" + workspace.IsDirty + "; pending-business=" + strategies.HasPendingChanges +
                "; disposed=" + form.IsDisposed + "; enabled=" + form.Enabled + "; generation=" +
                typeof(MainForm).GetField("_historyIoGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form));
            var entry = workspace.GetHistoryMetadata().Single();
            TestSupport.Assert(entry.Status == HistoryStatus.Active && entry.Files.Count == 4 &&
                appendKinds.Contains("Prepare") && appendKinds.Contains("Commit"),
                "实际异步顶部保存没有将DATA、EXE和两个范围图标归入同一Prepare/Commit历史。");
            TestSupport.Assert(form.Enabled && !workspace.IsDirty && !session.IsDirty && !strategies.HasPendingChanges,
                "实际异步顶部保存没有接受范围参与者、普通字段或待提交业务输入。");
            var saved = ReadRangeFiles(root);
            foreach (var name in new[] { "Ekd5.exe", "Data.e5", "Hitarea.e5", "Effarea.e5" })
            {
                var key = saved.Keys.Single(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
                TestSupport.Assert(!before[key].SequenceEqual(saved[key]), "异步范围混合保存缺少实际写回：" + name);
            }
            form.LoadProjectForSmokeTest(root); Application.DoEvents();
            AssertSavedRangeState(form, attack, pierce, expectedAttack, expectedPierce, expectedPower);
            using (var reopened = new RangeEditSession(root))
            {
                TestSupport.Assert(reopened.Preview(attack, RangePreviewContext.Default).ToHashSet().SetEquals(expectedAttack) &&
                    reopened.Preview(pierce, RangePreviewContext.Default).ToHashSet().SetEquals(expectedPierce), "独立范围解析复读丢失异步保存的新范围。");
            }
            var reloaded = new ProjectWorkspace(); reloaded.Open(root);
            AssertRangeReferences(reloaded, attack.Id, pierce.Id);
            TestSupport.Assert(Number(reloaded, "persons", "武力") == expectedPower, "独立Data复读丢失同次普通字段修改。");
            using (var images = new E5RangeImages(root))
            using (var attackImage = images.Copy(true, attack.Id))
            using (var pierceImage = images.Copy(false, pierce.Id))
                TestSupport.Assert(attackImage != null && pierceImage != null, "异步保存的新增范围图标不能独立解码。");

            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            form.UndoSavedForSmokeTest(); PumpMainWindow(form);
            AssertRangeFiles(before, root, "实际主窗异步历史撤回未逐字节恢复混合基线");
            var undone = form.RangeSessionForSmokeTest!;
            TestSupport.Assert(!undone.Bindings.Any(binding => binding.Key == attack || binding.Key == pierce) &&
                Number(workspace, "persons", "武力") == originalPower &&
                originalReferences.All(pair => Number(workspace, pair.Key.Table, pair.Key.Field) == pair.Value) &&
                workspace.GetHistoryMetadata().Single().Status == HistoryStatus.Reverted,
                "实际主窗异步历史撤回未同步新ID目录、业务引用或普通DATA字段。");
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            form.RedoSavedForSmokeTest(); PumpMainWindow(form);
            AssertRangeFiles(saved, root, "实际主窗异步历史重做未逐字节恢复混合保存");
            AssertSavedRangeState(form, attack, pierce, expectedAttack, expectedPierce, expectedPower);
            TestSupport.Assert(workspace.GetHistoryMetadata().Single().Status == HistoryStatus.Active &&
                !workspace.Commands.CanUndo && !workspace.Commands.CanRedo && !workspace.IsDirty,
                "实际异步重做后历史状态、全局草稿栈或干净状态不正确。");
            Console.WriteLine($"HISTORY_ASYNC_RANGE_MIXED_OK attack={attack.Id} pierce={pierce.Id} files=4 refs=4 ordinary-Data=1 top-button/save/reopen/undo/redo=PASS");
            TestSupport.Assert(sourceHashes.All(pair => HashService.Sha256(pair.Key) == pair.Value), "异步范围验收改变了真实源基底文件。");
            owned.Complete();
        }
        catch (Exception error) { RetainFailure(owned, error); throw; }
        finally
        {
            TestSupport.Assert(sourceHashes.All(pair => HashService.Sha256(pair.Key) == pair.Value), "异步范围验收改变了真实源基底文件。");
        }
    }

    private static OwnedTemporaryWorkspace CreateOwnedCore(string source, bool restoreKnown65Snapshot = true)
    {
        var owned = OwnedTemporaryWorkspace.Create(Path.GetTempPath(), "DataManager.HistoryAsyncSaveTests", Path.GetFullPath(source));
        try
        {
            IReadOnlyDictionary<string, byte[]> bytes = Known65FixtureSnapshot.CoreNames.ToDictionary(name => name,
                name => File.ReadAllBytes(Path.Combine(source, name)), StringComparer.OrdinalIgnoreCase);
            if (restoreKnown65Snapshot) bytes = Known65FixtureSnapshot.Recover(source, bytes).Bytes;
            foreach (var pair in bytes) File.WriteAllBytes(Path.Combine(owned.RootPath, pair.Key), pair.Value);
            return owned;
        }
        catch (Exception error) { RetainFailure(owned, error); owned.Dispose(); throw; }
    }

    private static void RetainFailure(OwnedTemporaryWorkspace owned, Exception error)
    {
        File.WriteAllText(Path.Combine(owned.RootPath, "failure.txt"), error.ToString());
        Console.WriteLine("HISTORY_ASYNC_RETAINED_WORKSPACE " + owned.RootPath);
    }

    private static readonly (string Table, string Field)[] ReferenceFields =
    [ ("detailed-job-growth", "攻击范围"), ("detailed-job-penetration", "穿透"), ("strategies", "施法范围"), ("strategies", "穿透范围") ];

    private static void AssertSavedRangeState(MainForm form, RangeKey attack, RangeKey pierce,
        HashSet<CellOffset> expectedAttack, HashSet<CellOffset> expectedPierce, int power)
    {
        var session = form.RangeSessionForSmokeTest!;
        TestSupport.Assert(session.Preview(attack, RangePreviewContext.Default).ToHashSet().SetEquals(expectedAttack) &&
            session.Preview(pierce, RangePreviewContext.Default).ToHashSet().SetEquals(expectedPierce) && !session.IsDirty,
            "实际主窗重开或重做后范围参与者没有正确重载新几何。");
        AssertRangeReferences(form.WorkspaceForSmokeTest, attack.Id, pierce.Id);
        TestSupport.Assert(Number(form.WorkspaceForSmokeTest, "persons", "武力") == power, "实际主窗重开或重做后普通DATA字段不一致。");
    }

    private static void AssertRangeReferences(ProjectWorkspace workspace, int attack, int pierce)
        => TestSupport.Assert(ReferenceFields.All(field => Number(workspace, field.Table, field.Field) ==
            (field.Field is "攻击范围" or "施法范围" ? attack : pierce)), "异步保存后四项兵种/策略范围引用不一致。");

    private static int Number(ProjectWorkspace workspace, string table, string field)
        => Convert.ToInt32(workspace.GetDocument(table).Data.Rows[0][field]);

    private static RangePreviewPicker RangePicker(Control parent, string field)
        => Descendants(parent).OfType<RangePreviewPicker>().Single(picker => picker.FieldName == field);

    private static IEnumerable<Control> Descendants(Control parent)
        => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static IEnumerable<string> RangeFiles(string root)
        => new[] { "Ekd5.exe", "Data.e5", "Imsg.e5", "Star.e5", "Font.e5", "Hitarea.e5", "Effarea.e5", "Spalet.e5" }
            .SelectMany(name => new[] { Path.Combine(root, name), Path.Combine(root, "E5", name) }).Where(File.Exists);

    private static Dictionary<string, byte[]> ReadRangeFiles(string root)
        => RangeFiles(root).ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertRangeFiles(Dictionary<string, byte[]> expected, string root, string message)
    {
        foreach (var pair in expected)
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(root, pair.Key)).SequenceEqual(pair.Value), message + "：" + pair.Key);
    }

    private static void PumpMainWindow(MainForm form)
    {
        var busy = typeof(MainForm).GetField("_historyIoBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while ((bool)busy.GetValue(form)!)
        {
            Application.DoEvents(); Thread.Sleep(1);
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("实际主窗混合范围异步操作未完成。");
        }
        Application.DoEvents();
    }
    private static void Pump(Task task)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        { Application.DoEvents(); Thread.Sleep(1); if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("STA异步事务未完成。"); }
        task.GetAwaiter().GetResult();
    }
    private sealed class ThreadBoundParticipant : IWorkspaceDraftParticipant
    {
        private readonly int _thread;
        private readonly byte[] _candidate;
        private readonly string _hash;
        public bool HasPendingChanges => !Accepted;
        public bool Accepted { get; private set; }
        public ThreadBoundParticipant(string root, int thread)
        {
            _thread = thread;
            var bytes = new byte[2 * 1024 * 1024]; new Random(123).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(root, "history-async.payload"), bytes);
            _hash = HashService.Sha256(bytes); _candidate = [..bytes, 42];
        }
        public IReadOnlyList<PreparedFileEdit> PrepareFileEdits()
        { CheckThread(); return [new("history-async.payload", _hash, _candidate, "async-test", "frozen-large-payload")]; }
        public Action CaptureSaveRecovery() { CheckThread(); return CheckThread; }
        public void AcceptSaved() { CheckThread(); Accepted = true; }
        public void Discard() { CheckThread(); Accepted = true; }
        private void CheckThread() => TestSupport.Assert(Environment.CurrentManagedThreadId == _thread, "草稿参与者被送入worker");
    }
}
