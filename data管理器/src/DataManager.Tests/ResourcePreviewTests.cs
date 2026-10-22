using System.Globalization;
using System.Diagnostics;
using System.Drawing.Imaging;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Images;
using ImageManager.App;
using ImageManager.Core;
using IdNameOption = DataManager.App.IdNameOption;

namespace DataManager.Tests;

internal static class ResourcePreviewTests
{
    public static void Run(string basePath)
    {
        CheckResourceSelectionCommit();
        TestProject(basePath);
        var standard66 = Path.Combine(TestSupport.RepositoryRootFromBase(basePath), "基底", "曹操传6.6-V7.16 解压密码：66");
        if (Directory.Exists(standard66)) TestProject(standard66);
    }

    private static void CheckResourceSelectionCommit()
    {
        using var form = new Form
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            Size = new Size(400, 200), ShowInTaskbar = false
        };
        using var combo = new SearchableIdComboBox { Width = 240 };
        combo.SetPreviewLoader((_, _) => Task.FromResult<Bitmap?>(null));
        combo.SetOptions([new IdNameOption(12, "已选图片"), new IdNameOption(120, "先前输入")]);
        form.Controls.Add(combo); form.Show(); Application.DoEvents();
        combo.Text = "120";
        combo.SelectedItem = combo.Items.Cast<IdNameOption>().Single(option => option.Id == 12);
        var notifications = 0;
        combo.SelectionChangeCommitted += (_, _) =>
        {
            notifications++;
            TestSupport.Assert(combo.SelectedItem is IdNameOption selected && selected.Id == 12 &&
                combo.Text == selected.Display && combo.TryGetSelectedId(out var id) && id == selected.Id,
                "资源下拉提交事件读取了上次输入的文字/ID，而非当前选中项");
        };
        typeof(SearchableIdComboBox).GetMethod("OnSelectionChangeCommitted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(combo, [EventArgs.Empty]);
        TestSupport.Assert(notifications == 1, "资源下拉选择没有且仅有一次提交通知");
    }

    private static void TestProject(string source)
    {
        using var adapter = new ResourceCatalogAdapter();
        var catalog = adapter.Scan(source);
        if (!catalog.Resources.Any(item => item.Key == "SComposite") || !catalog.Resources.Any(item => item.Key == "Face")) return;
        var checkedPaths = catalog.Resources.Where(item => item.Key is "Face" or "Fb.e5" or "Pmapobj.e5" or "SComposite" or "Item.e5" or "Itemicon.dll" or "Mtem.e5" or "Mgcicon.dll" or "Meff.e5" || item.Key.StartsWith("Mcall:", StringComparison.Ordinal))
            .SelectMany(item => item.Paths).Append(Path.Combine(source, "Data.e5")).Append(Path.Combine(source, "Ekd5.exe"))
            .Append(Path.Combine(source, "Imsg.e5")).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, HashService.Sha256);
        var workspace = new ProjectWorkspace();
        workspace.Open(source);
        foreach (var path in workspace.Documents.Values.Select(document => document.FilePath).Distinct(StringComparer.OrdinalIgnoreCase))
            checkedPaths.TryAdd(path, HashService.Sha256(path));
        using (var defaultS = adapter.DecodeCharacterS(source, 0, 0))
            TestSupport.Assert(defaultS.Frames.Count == 1 && defaultS.Frames[0].Size == new Size(48, 48) &&
                defaultS.Mapping.Contains("#1", StringComparison.Ordinal), "默认兵种 S000 必须仅解码对应兵种mov第0帧");
        using (var specialS = adapter.DecodeCharacterS(source, 1, 0, 2))
            TestSupport.Assert(specialS.Frames.Count == 1 && specialS.Frames[0].Size == new Size(48, 48) &&
                specialS.Mapping.Contains("#241", StringComparison.Ordinal),
                "人物紧凑 S001 必须仅预览 Unit_mov 图号241的第0帧，不能随转数切换图片");
        var hasVerifiedR = workspace.Documents.TryGetValue("person-r-assign", out var rAssign) && rAssign.Definition.Capability == TableCapability.Editable;
        if (hasVerifiedR)
        {
            var rFrames = adapter.DecodeFrames(source, "Pmapobj.e5", ReadNumber(rAssign!, 0, "R形象ID"));
            try { TestSupport.Assert(rFrames.Count == 40 && rFrames.All(frame => frame.Size == new Size(48, 64)), "人物 R 字段没有映射到正反面各 20 帧形象"); }
            finally { foreach (var frame in rFrames) frame.Dispose(); }
        }
        using (var absent = adapter.DecodeStrategyAnimation(source, true, 255))
            TestSupport.Assert(absent.Frames.Count == 0 && absent.Mapping.Contains("无大动画", StringComparison.Ordinal), "255 大动画哨兵不应读取资源");
        var strategies = workspace.GetDocument("strategies");
        var small = workspace.GetDocument("strategy-small-animation");
        var large = workspace.GetDocument("strategy-big-animation");
        var strategyIndex = Enumerable.Range(0, strategies.Data.Rows.Count)
            .First(index => ReadNumber(small, index, "动画ID") != 255 && ReadNumber(large, index, "动画ID") is >= 100 and < 255);
        using (var animation = adapter.DecodeStrategyAnimation(source, false, ReadNumber(small, strategyIndex, "动画ID")))
            TestSupport.Assert(animation.Frames.Count > 1 && animation.IntervalMs > 0 && animation.Mapping.Contains("Meff", StringComparison.Ordinal),
                "策略小动画字段没有映射到 Meff 多帧动画");
        using (var animation = adapter.DecodeStrategyAnimation(source, true, ReadNumber(large, strategyIndex, "动画ID")))
            TestSupport.Assert(animation.Frames.Count > 1 && animation.IntervalMs > 0 && animation.Mapping.Contains("Mcall", StringComparison.Ordinal),
                "策略大动画字段没有映射到 Mcall 多帧动画");

        CheckResourceOptionUiLifecycle(workspace, adapter);
        using var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1280, 720) };
        using var panel = new BusinessEditorPanel(workspace, adapter);
        var edits = 0;
        panel.Edited += (_, _) => edits++;
        host.Controls.Add(panel);
        host.Show();
        foreach (var (kind, id, minimumImages) in new[]
        {
            (BusinessPageKind.Persons, 0, hasVerifiedR ? 3 : 2), (BusinessPageKind.DetailedJobs, 0, 3),
            (BusinessPageKind.Items, 0, 1), (BusinessPageKind.Strategies, strategyIndex, 3)
        })
        {
            panel.Bind(kind, id);
            WaitFor(() => LoadedPictures(panel).Length >= minimumImages,
                $"{workspace.ActiveProfile.VariantId} {kind} 关联预览失败；{string.Join("；", Descendants(panel).OfType<Label>().Select(label => label.Text))}");
            if (kind == BusinessPageKind.Persons)
            {
                CheckPersonPickers(panel, workspace, adapter, id);
                CheckPersonFrames(panel, workspace, adapter, id);
            }
            if (kind == BusinessPageKind.Items)
            {
                CheckResourcePicker(panel, "图标", adapter.GetIdOptions(source, ResourceBindingKind.ItemIcon),
                    ReadById(workspace, ItemTable(workspace, id), id, "图标"));
                CheckItemFrame(panel, workspace, adapter, id);
            }
            foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
            {
                host.Size = size;
                Application.DoEvents();
                CheckPictureLayout(panel, $"{workspace.ActiveProfile.VariantId}/{kind}/{size.Width}");
                if (kind == BusinessPageKind.Items && size.Width == 1920)
                    TestSupport.Assert(LoadedPictures(panel).Any(picture => picture.Width > picture.Image!.Width * 4 ||
                        picture.Height > picture.Image!.Height * 4), "宝物预览仍受固定放大倍数限制");
                SaveSnapshot(host, workspace.ActiveProfile.VariantId, kind.ToString());
            }
            AssertNoDraft(panel, workspace, edits, $"{workspace.ActiveProfile.VariantId}/{kind}选项加载、下拉开合和预览");
        }
        CheckJobFirstFrames(panel, workspace, adapter);
        CheckRapidEntitySwitch(panel, workspace, adapter, () => edits);
        CheckPersonEditRefresh(panel, workspace, adapter);
        var closedPictures = Descendants(panel).OfType<PictureBox>().ToArray();
        host.Close();
        TestSupport.Assert(closedPictures.Length > 0 && closedPictures.All(picture => picture.IsDisposed && picture.Image == null),
            "关闭业务页后关联预览控件或图片引用未释放");
        foreach (var (path, hash) in checkedPaths)
            TestSupport.Assert(HashService.Sha256(path) == hash, $"关联预览不应修改资源或项目文件：{path}");
        CheckDraftSReferences(source);
    }

    private static void CheckResourceOptionUiLifecycle(ProjectWorkspace workspace, ResourceCatalogAdapter adapter)
    {
        var previousContext = SynchronizationContext.Current;
        var autoInstall = WindowsFormsSynchronizationContext.AutoInstall;
        WindowsFormsSynchronizationContext.AutoInstall = false;
        SynchronizationContext.SetSynchronizationContext(null);
        try { CheckResourceOptionsWithoutSynchronizationContext(workspace, adapter); }
        finally
        {
            WindowsFormsSynchronizationContext.AutoInstall = autoInstall;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void CheckResourceOptionsWithoutSynchronizationContext(ProjectWorkspace workspace, ResourceCatalogAdapter adapter)
    {
        var ownerThread = Environment.CurrentManagedThreadId;
        var violations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var observed = new HashSet<Control>();
        var closed = false;
        var handlesCreated = 0;
        void Track(Control control)
        {
            if (Environment.CurrentManagedThreadId != ownerThread)
            {
                violations.Enqueue("后台线程修改了资源控件树");
                return;
            }
            if (!observed.Add(control)) return;
            control.ControlAdded += (_, args) => { if (args.Control != null) Track(args.Control); };
            if (control is SearchableIdComboBox combo)
            {
                combo.HandleCreated += (_, _) =>
                {
                    Interlocked.Increment(ref handlesCreated);
                    if (Environment.CurrentManagedThreadId != ownerThread) violations.Enqueue("SearchableIdComboBox.HandleCreated不在UI线程");
                    if (closed) violations.Enqueue("关闭后重新创建了资源下拉框句柄");
                };
                combo.TextChanged += (_, _) =>
                {
                    if (Environment.CurrentManagedThreadId != ownerThread) violations.Enqueue("SearchableIdComboBox.TextChanged不在UI线程");
                    if (closed) violations.Enqueue("关闭后仍在修改资源下拉框文字");
                };
                combo.Invalidated += (_, _) =>
                {
                    if (Environment.CurrentManagedThreadId != ownerThread) violations.Enqueue("SearchableIdComboBox.Invalidated不在UI线程");
                };
            }
            foreach (Control child in control.Controls) Track(child);
        }
        using (var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1280, 720) })
        using (var panel = new BusinessEditorPanel(workspace, adapter))
        {
            Track(panel);
            host.Controls.Add(panel);
            panel.Bind(BusinessPageKind.Persons, 0);
            var combo = (SearchableIdComboBox)LabeledEditor(panel, "头像");
            foreach (var control in Descendants(panel)) Track(control);
            var expected = adapter.GetIdOptions(workspace.RootPath, ResourceBindingKind.Portrait);
            combo.SetOptions(expected.Take(1).Select(option => new IdNameOption(option.Id, option.Name)));
            // Native ComboBox property setters may legitimately create their
            // parent on the binding thread. Explicitly return to the HWND gap
            // so this case also covers completion before the panel is shown.
            if (panel.IsHandleCreated)
                typeof(Control).GetMethod("DestroyHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(panel, null);
            // Let the background reader finish before the owner has any HWND.
            // Its completion must stay pending until the UI creates that HWND.
            Thread.Sleep(100);
            TestSupport.Assert(!panel.IsHandleCreated && SynchronizationContext.Current is not WindowsFormsSynchronizationContext && violations.IsEmpty,
                $"资源列表后台完成时创建了UI句柄，或无同步上下文回归条件未建立：panelHandle={panel.IsHandleCreated}; context={SynchronizationContext.Current?.GetType().Name ?? "null"}; combos={observed.OfType<SearchableIdComboBox>().Count()}; " + string.Join("；", violations));
            host.Show();
            panel.CreateControl();
            _ = combo.Handle; // Force the observed target HWND on its owning UI thread.
            typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(combo, null);
            WaitFor(() => combo.Options.Select(option => option.Id).SequenceEqual(expected.Select(option => option.Id)),
                "建句柄前完成的资源列表没有在UI线程显示");
            TestSupport.Assert(handlesCreated > 0, "资源下拉框句柄线程断言没有观察到真实目标控件");

            // A completion already posted to the old owner HWND must neither
            // block the replacement HWND nor consume its newly posted queue.
            combo.SetOptions(expected.Take(1).Select(option => new IdNameOption(option.Id, option.Name)));
            adapter.Invalidate(workspace.RootPath);
            panel.Bind(BusinessPageKind.Persons, 0);
            Thread.Sleep(100);
            typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(panel, null);
            WaitFor(() => combo.Options.Select(option => option.Id).SequenceEqual(expected.Select(option => option.Id)),
                "重建业务页句柄后资源选项队列没有恢复");

            // Force a fresh result while preserving an input made after the
            // request began. Rebinding the entity must also keep the latest ID.
            adapter.Invalidate(workspace.RootPath);
            panel.Bind(BusinessPageKind.Persons, 1);
            combo = (SearchableIdComboBox)LabeledEditor(panel, "头像");
            var boundText = combo.Text;
            TestSupport.Assert(expected.Count > 1, "资源选项样本不足以验证异步回填");
            combo.SetOptions(expected.Take(1).Select(option => new IdNameOption(option.Id, option.Name)));
            if (combo.Enabled) combo.Focus();
            combo.Text = "1";
            combo.SelectionStart = 0;
            combo.SelectionLength = 1;
            var preserveCaret = combo.Focused;
            WaitFor(() => combo.Options.Select(option => option.Id).SequenceEqual(expected.Select(option => option.Id)),
                "无同步上下文时资源列表没有回到UI线程");
            TestSupport.Assert(combo.Text == "1" && (!preserveCaret || combo.SelectionStart == 0 && combo.SelectionLength == 1),
                "异步资源列表覆盖了正在输入的编号或光标选择");
            combo.SetBoundValue(boundText);
            TestSupport.Assert(combo.TryGetSelectedId(out var id) && id == ReadById(workspace, "persons", 1, "头像"),
                "切换人物后异步资源列表恢复了旧人物编号");
            TestSupport.Assert(panel.Flush() && !workspace.IsDirty, "资源列表生命周期验证产生了意外草稿");
            host.Close();
            closed = true;
            Pump(TimeSpan.FromMilliseconds(100));
        }

        // Leave completions queued, dispose one page tree through rebinding,
        // then close its replacement before pumping any completion messages.
        for (var iteration = 0; iteration < 4; iteration++)
        {
            closed = false;
            using var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1280, 720) };
            using var panel = new BusinessEditorPanel(workspace, adapter);
            Track(panel);
            host.Controls.Add(panel); host.Show();
            adapter.Invalidate(workspace.RootPath);
            panel.Bind(BusinessPageKind.Persons, 0);
            var oldCombos = Descendants(panel).OfType<SearchableIdComboBox>().ToArray();
            foreach (var oldCombo in oldCombos) Track(oldCombo);
            panel.Bind(BusinessPageKind.Items, 0);
            var closingCombos = Descendants(panel).OfType<SearchableIdComboBox>().ToArray();
            foreach (var closingCombo in closingCombos) Track(closingCombo);
            var allCombos = oldCombos.Concat(closingCombos).ToArray();
            host.Close();
            closed = true;
            var closedOptions = allCombos.Select(combo => combo.Options.ToArray()).ToArray();
            Pump(TimeSpan.FromMilliseconds(200));
            TestSupport.Assert(allCombos.All(combo => combo.IsDisposed) &&
                allCombos.Select((combo, index) => combo.Options.SequenceEqual(closedOptions[index])).All(unchanged => unchanged),
                "页面重建或窗口关闭后仍有资源结果回填旧下拉框");
        }
        TestSupport.Assert(violations.IsEmpty, "资源异步UI生命周期违规：" + string.Join("；", violations));
    }

    private static void CheckJobFirstFrames(BusinessEditorPanel panel, ProjectWorkspace workspace, ResourceCatalogAdapter adapter)
    {
        var profile = new ProjectProfileService().Detect(workspace.RootPath);
        var archive = E5Archive.Open(profile.TryGetResource("Unit_mov.e5")!);
        // Explicit boundaries cover the three promotions, the next series,
        // and the switch to the twenty series with a single promotion.
        foreach (var (job, physical) in new[] { (0, 1), (1, 4), (2, 7), (3, 10), (59, 178), (60, 181), (79, 238) })
        {
            panel.Bind(BusinessPageKind.DetailedJobs, job);
            var cards = Descendants(panel).OfType<ResourcePreviewCard>().ToArray();
            TestSupport.Assert(cards.Length == 3, "兵种页没有同时显示我军、友军、敌军三个静态预览");
            WaitFor(() => cards.All(card => card.HasLoadedResource), $"兵种{job}的三阵营第0帧没有完成加载");
            var ordered = cards.OrderBy(card => card.PointToScreen(Point.Empty).Y).ToArray();
            var expectedTitles = new[] { $"我军 · S_{physical:000}", $"友军 · S_{physical + 1:000}", $"敌军 · S_{physical + 2:000}" };
            TestSupport.Assert(ordered.Select(card => card.Text).SequenceEqual(expectedTitles),
                $"兵种{job}的卡片没有按我/友/敌军从上到下标明物理图号");
            for (var faction = 0; faction < 3; faction++)
            {
                using var expected = ReadMovementFrame(profile, archive, physical + faction);
                AssertSamePixels(PreviewPicture(panel, expectedTitles[faction]).Image!, expected,
                    $"兵种{job}阵营{faction}没有对应Unit_mov #{physical + faction}第0帧");
                using var preview = adapter.DecodeJobSFirstFrame(workspace.RootPath, job, faction);
                TestSupport.Assert(preview.Frames.Count == 1 && preview.Frames[0].Size == new Size(48, 48),
                    $"兵种{job}阵营{faction}预览仍在加载完整动作或错误尺寸");
                AssertSamePixels(preview.Frames[0], expected, "兵种预览适配层的动作/帧映射错误");
            }
            Pump(TimeSpan.FromMilliseconds(200));
            for (var faction = 0; faction < 3; faction++)
            {
                using var expected = ReadMovementFrame(profile, archive, physical + faction);
                AssertSamePixels(PreviewPicture(panel, expectedTitles[faction]).Image!, expected,
                    $"兵种{job}阵营{faction}预览没有保持mov第0帧");
            }
        }
    }

    private static void CheckPersonPickers(BusinessEditorPanel panel, ProjectWorkspace workspace, ResourceCatalogAdapter adapter, int id)
    {
        var job = ReadById(workspace, "persons", id, "兵种");
        foreach (var (key, field, label, kind) in new[]
        {
            ("persons", "头像", "头像", ResourceBindingKind.Portrait),
            ("person-r-assign", "R形象ID", "R形象编号", ResourceBindingKind.CharacterR),
            ("person-s-assign", "S形象ID", "S形象编号", ResourceBindingKind.CharacterS)
        })
        {
            if (!HasEntity(workspace, key, id)) continue;
            CheckResourcePicker(panel, label, adapter.GetIdOptions(workspace.RootPath, kind, job, job <= 59 ? job % 3 + 1 : 1),
                ReadById(workspace, key, id, field));
        }
    }

    private static void CheckResourcePicker(Control panel, string label, IReadOnlyList<ResourceIdOption> expected, int selectedId)
    {
        var editor = LabeledEditor(panel, label);
        TestSupport.Assert(editor is SearchableIdComboBox, $"{label}没有使用可输入资源下拉框，而是{editor.GetType().Name}");
        var combo = (SearchableIdComboBox)editor;
        TestSupport.Assert(combo.DrawMode == DrawMode.OwnerDrawVariable && combo.DropDownStyle == ComboBoxStyle.DropDown,
            $"{label}没有启用资源缩略图OwnerDraw或数字输入");
        TestSupport.Assert(expected.Count > 0 && expected[0].Id == 0, $"{label}真实资源编号没有从0开始");
        WaitFor(() => combo.Options.Select(option => option.Id).SequenceEqual(expected.Select(option => option.Id)),
            $"{label}异步资源选项没有完整加载");
        TestSupport.Assert(combo.Options[0].Display.StartsWith("0：", StringComparison.Ordinal) &&
            combo.Options.All(option => !string.IsNullOrWhiteSpace(option.Name)), $"{label}资源选项没有显示ID和名称");
        TestSupport.Assert(combo.TryGetSelectedId(out var value) && value == selectedId,
            $"{label}异步加载把当前绑定值{selectedId}改成了{combo.Text}");
        // Opening and painting the native dropdown requests visible thumbnails.
        // Neither option loading nor popup navigation should submit an edit.
        if (combo.Enabled)
        {
            combo.DroppedDown = true;
            Pump(TimeSpan.FromMilliseconds(100));
            combo.DroppedDown = false;
            TestSupport.Assert(combo.TryGetSelectedId(out value) && value == selectedId, $"{label}打开下拉时丢失当前值");
        }
    }

    private static void CheckRapidEntitySwitch(BusinessEditorPanel panel, ProjectWorkspace workspace, ResourceCatalogAdapter adapter, Func<int> edits)
    {
        PictureBox[] previousKind = [];
        foreach (var kind in new[] { BusinessPageKind.Persons, BusinessPageKind.Items })
        {
            var key = kind == BusinessPageKind.Persons ? "persons" : "items-low";
            var document = workspace.GetDocument(key);
            TestSupport.Assert(document.Data.Rows.Count > 1, "真实样本不足以验证实体切换");
            var first = document.Definition.BeginId;
            var final = first + 1;
            panel.Bind(kind, first);
            TestSupport.Assert(previousKind.All(picture => picture.IsDisposed && picture.Image == null), "更换页面结构后旧预览没有释放");
            var persistentCards = Descendants(panel).OfType<ResourcePreviewCard>().ToArray();
            var persistentPictures = Descendants(panel).OfType<PictureBox>().ToArray();
            // Keep callbacks queued while rebinding the same control tree.
            // Viewports persist; old requests must not repaint a newer binding.
            for (var iteration = 0; iteration < 8; iteration++)
            {
                panel.Bind(kind, iteration % 2 == 0 ? final : first);
                TestSupport.Assert(persistentCards.SequenceEqual(Descendants(panel).OfType<ResourcePreviewCard>()) &&
                    persistentPictures.SequenceEqual(Descendants(panel).OfType<PictureBox>()), $"快速切换实体重建了关联预览卡片或图片框：{workspace.ActiveProfile.VariantId}/{kind}/{iteration} cards={persistentCards.Length}/{Descendants(panel).OfType<ResourcePreviewCard>().Count()} pictures={persistentPictures.Length}/{Descendants(panel).OfType<PictureBox>().Count()}");
            }
            panel.Bind(kind, final);
            var count = kind == BusinessPageKind.Persons &&
                workspace.Documents.TryGetValue("person-r-assign", out var r) && r.Definition.Capability == TableCapability.Editable ? 3 :
                kind == BusinessPageKind.Persons ? 2 : 1;
            WaitFor(() => persistentCards.All(card => card.HasLoadedResource) && LoadedPictures(panel).Length >= count,
                "快速切换后的最终实体没有完成关联预览");
            Pump(TimeSpan.FromMilliseconds(300));
            TestSupport.Assert(panel.CurrentKind == kind && panel.CurrentId == final &&
                persistentPictures.All(picture => !picture.IsDisposed), "快速切换后的实体或持久预览状态错误");
            if (kind == BusinessPageKind.Persons)
            {
                CheckPersonPickers(panel, workspace, adapter, final);
                CheckPersonFrames(panel, workspace, adapter, final);
            }
            else
            {
                CheckResourcePicker(panel, "图标", adapter.GetIdOptions(workspace.RootPath, ResourceBindingKind.ItemIcon),
                    ReadById(workspace, ItemTable(workspace, final), final, "图标"));
                CheckItemFrame(panel, workspace, adapter, final);
            }
            CheckPictureLayout(panel, "快速切换后的" + kind);
            AssertNoDraft(panel, workspace, edits(), "快速切换实体");
            SaveSnapshot(panel.FindForm()!, workspace.ActiveProfile.VariantId, kind + "-切换后");
            if (kind == BusinessPageKind.Items)
            {
                var oldImage = persistentPictures.Single().Image;
                adapter.Invalidate(workspace.RootPath);
                panel.Bind(kind, final);
                WaitFor(() => persistentCards.All(card => card.HasLoadedResource) && !ReferenceEquals(oldImage, persistentPictures.Single().Image),
                    "资源缓存失效后相同编号仍使用旧关联图片");
                CheckItemFrame(panel, workspace, adapter, final);
            }
            previousKind = persistentPictures;
        }
    }

    private static void CheckPersonFrames(Control panel, ProjectWorkspace workspace, ResourceCatalogAdapter adapter, int id)
    {
        CheckStill("Face", ReadById(workspace, "persons", id, "头像"), "头像 ");
        if (workspace.ActiveProfile.ProfileId == "ccz-6.6")
            CheckStill("Fb.e5", ResourceCatalogAdapter.HalfBodyImageNumber(ReadById(workspace, "persons", id, "头像")), "半身像 ");
        if (workspace.Documents.TryGetValue("person-r-assign", out var r) && r.Definition.Capability == TableCapability.Editable)
            CheckStill("Pmapobj.e5", ReadById(workspace, "person-r-assign", id, "R形象ID"), "R ");
        var sVerified = workspace.Documents.TryGetValue("person-s-assign", out var s) && s.Definition.Capability == TableCapability.Editable;
        var job = ReadById(workspace, "persons", id, "兵种");
        var sId = sVerified ? ReadById(workspace, "person-s-assign", id, "S形象ID") : 0;
        // Derive the expected source independently of the adapter under test.
        var physical = sId == 0 ? job * 3 + 1 : sId <= 32 ? 241 + (sId - 1) * 3 : sId + 304;
        var profile = new ProjectProfileService().Detect(workspace.RootPath);
        var archive = E5Archive.Open(profile.TryGetResource("Unit_mov.e5")!);
        using var expectedS = ReadMovementFrame(profile, archive, physical);
        AssertSamePixels(PreviewPicture(panel, sVerified ? "S " : "默认S").Image!, expectedS,
            "人物S预览没有显示最终实体紧凑编号第一转的mov第0帧");

        void CheckStill(string key, int logicalId, string prefix)
        {
            var frames = adapter.DecodeFrames(workspace.RootPath, key, logicalId);
            try
            {
                TestSupport.Assert(frames.Count > 0, $"人物{id}的{key}真实资源缺少第一帧");
                AssertSamePixels(PreviewPicture(panel, prefix).Image!, frames[0], $"人物{key}预览不是当前编号{logicalId}第一帧");
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
    }

    private static void CheckPersonEditRefresh(BusinessEditorPanel panel, ProjectWorkspace workspace, ResourceCatalogAdapter adapter)
    {
        const int personId = 0;
        TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo && !workspace.Commands.CanRedo,
            "人物即时预览验证开始前存在意外草稿");
        panel.Bind(BusinessPageKind.Persons, personId);
        CheckPersonPickers(panel, workspace, adapter, personId);
        WaitFor(() => Descendants(panel).OfType<ResourcePreviewCard>().All(card => card.HasLoadedResource),
            "人物即时预览验证的初始图片没有完成加载");
        var profile = new ProjectProfileService().Detect(workspace.RootPath);
        var job = ReadById(workspace, "persons", personId, "兵种");
        var tested = new List<string>();
        foreach (var (key, field, label, kind) in new[]
        {
            ("persons", "头像", "头像", ResourceBindingKind.Portrait),
            ("person-r-assign", "R形象ID", "R形象编号", ResourceBindingKind.CharacterR),
            ("person-s-assign", "S形象ID", "S形象编号", ResourceBindingKind.CharacterS)
        })
        {
            if (!HasEntity(workspace, key, personId)) continue;
            var document = workspace.GetDocument(key);
            var combo = LabeledEditor(panel, label) as SearchableIdComboBox;
            if (combo is not { Enabled: true }) continue;
            var row = document.Data.Rows[personId - document.Definition.BeginId];
            var original = Convert.ToInt32(row[field], CultureInfo.InvariantCulture);
            var next = combo.Options.First(option => option.Id != original).Id;
            var expected = new List<(string Prefix, Bitmap Image)>();
            try
            {
                if (kind == ResourceBindingKind.CharacterS)
                {
                    var physical = next == 0 ? job * 3 + 1 : next <= 32 ? 241 + (next - 1) * 3 : next + 304;
                    expected.Add(("S ", ReadMovementFrame(profile, E5Archive.Open(profile.TryGetResource("Unit_mov.e5")!), physical)));
                }
                else
                {
                    expected.Add((kind == ResourceBindingKind.Portrait ? "头像 " : "R ",
                        FirstFrame(kind == ResourceBindingKind.Portrait ? "Face" : "Pmapobj.e5", next)));
                    if (kind == ResourceBindingKind.Portrait && workspace.ActiveProfile.ProfileId == "ccz-6.6")
                        expected.Add(("半身像 ", FirstFrame("Fb.e5", ResourceCatalogAdapter.HalfBodyImageNumber(next))));
                }
                var cards = Descendants(panel).OfType<ResourcePreviewCard>().ToArray();
                var pictures = expected.Select(item => PreviewPicture(panel, item.Prefix)).ToArray();
                var oldImages = pictures.Select(picture => picture.Image).ToArray();
                // A complete typed ID must repaint before Enter, validation,
                // entity rebind, Flush, or save, without changing the draft.
                combo.Text = next.ToString(CultureInfo.InvariantCulture);
                WaitFor(() => expected.Select((item, index) =>
                    cards.Single(card => card.Text.StartsWith(item.Prefix, StringComparison.Ordinal)).HasLoadedResource &&
                    pictures[index].Image != null && !ReferenceEquals(pictures[index].Image, oldImages[index])).All(loaded => loaded),
                    $"{label}键入合法编号后必须直接刷新预览，不能依赖回车、切换人物或保存");
                TestSupport.Assert(Convert.ToInt32(row[field], CultureInfo.InvariantCulture) == original && !workspace.IsDirty &&
                    !workspace.Commands.CanUndo && panel.HasPendingChanges, $"{label}仅预览输入就提前修改了人物草稿");
                TestSupport.Assert(panel.CurrentKind == BusinessPageKind.Persons && panel.CurrentId == personId &&
                    cards.SequenceEqual(Descendants(panel).OfType<ResourcePreviewCard>()) &&
                    expected.Select((item, index) => ReferenceEquals(pictures[index], PreviewPicture(panel, item.Prefix))).All(same => same),
                    $"{label}即时刷新重建了人物或预览控件");
                for (var index = 0; index < expected.Count; index++)
                    AssertSamePixels(pictures[index].Image!, expected[index].Image, $"{label}键入后的{expected[index].Prefix.Trim()}像素不是新编号{next}");
                var typedImages = pictures.Select(picture => picture.Image).ToArray();
                foreach (var invalid in new[] { string.Empty, "-1", "2147483647", "查找" })
                {
                    combo.Text = invalid;
                    Pump(TimeSpan.FromMilliseconds(30));
                    TestSupport.Assert(pictures.Select((picture, index) => ReferenceEquals(picture.Image, typedImages[index])).All(same => same) &&
                        Convert.ToInt32(row[field], CultureInfo.InvariantCulture) == original && !workspace.IsDirty,
                        $"{label}的中间/无效输入触发了错误预览或草稿写入：{invalid}");
                }
                combo.Text = next.ToString(CultureInfo.InvariantCulture);
                typeof(SearchableIdComboBox).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(combo, [new KeyEventArgs(Keys.Enter)]);
                TestSupport.Assert(Convert.ToInt32(row[field], CultureInfo.InvariantCulture) == next && workspace.IsDirty &&
                    workspace.Commands.CanUndo && !panel.HasPendingChanges, $"{label}回车没有立即提交当前人物的草稿");
                combo.SelectedItem = combo.Items.Cast<IdNameOption>().Single(option => option.Id == original);
                typeof(SearchableIdComboBox).GetMethod("OnSelectionChangeCommitted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(combo, [EventArgs.Empty]);
                TestSupport.Assert(Convert.ToInt32(row[field], CultureInfo.InvariantCulture) == original && !panel.HasPendingChanges,
                    $"{label}选择下拉项没有立即更新编号");
                WaitFor(() => cards.All(card => card.HasLoadedResource) &&
                    pictures.Select((picture, index) => picture.Image != null && !ReferenceEquals(picture.Image, typedImages[index])).All(loaded => loaded),
                    $"{label}选择下拉项后没有立即刷新预览");
                CheckPersonFrames(panel, workspace, adapter, personId);
                tested.Add(label);
            }
            finally
            {
                foreach (var item in expected) item.Image.Dispose();
                while (workspace.Commands.CanUndo) workspace.Commands.Undo();
                workspace.Commands.Clear();
                panel.Bind(BusinessPageKind.Persons, personId);
                TestSupport.Assert(Convert.ToInt32(row[field], CultureInfo.InvariantCulture) == original && !workspace.IsDirty && !panel.HasPendingChanges,
                    $"{label}即时预览验证没有恢复原始人物草稿");
                WaitFor(() => Descendants(panel).OfType<ResourcePreviewCard>().All(card => card.HasLoadedResource),
                    $"{label}撤回后预览没有恢复");
            }
        }
        TestSupport.Assert(tested.Contains("头像"), "真实人物样本没有验证可编辑头像的即时刷新");
        CheckPersonFrames(panel, workspace, adapter, personId);
        Console.WriteLine($"PERSON_EDIT_PREVIEW_REFRESH_OK {workspace.ActiveProfile.VariantId}: {string.Join('/', tested)}; 6.6头像同步半身像；未切换人物、未保存");

        Bitmap FirstFrame(string resource, int imageId)
        {
            var frames = adapter.DecodeFrames(workspace.RootPath, resource, imageId);
            try
            {
                TestSupport.Assert(frames.Count > 0, $"即时预览样本缺少{resource}编号{imageId}");
                return new Bitmap(frames[0]);
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
    }

    private static Bitmap ReadMovementFrame(ProjectProfile profile, E5Archive archive, int physical)
    {
        var bytes = archive.ReadDecoded(physical);
        using var strip = ImageRendering.IsStandardImage(bytes)
            ? ImageRendering.DecodeStandardImage(bytes)
            : ImageRendering.DecodeIndexed(bytes, 48, 48 * 11, new PaletteService().Load(profile));
        return ImageRendering.Crop(strip, new Rectangle(0, 0, 48, 48));
    }

    private static void CheckDraftSReferences(string source)
    {
        using var form = new MainForm(openDefaultProject: false);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var workspace = (ProjectWorkspace)typeof(MainForm).GetField("_workspace", flags)!.GetValue(form)!;
        workspace.Open(source);
        if (!workspace.Documents.TryGetValue("person-s-assign", out var s) ||
            s.Definition.Capability != TableCapability.Editable) return;
        var persons = workspace.GetDocument("persons");
        if (persons.Definition.Capability != TableCapability.Editable) return;
        TestSupport.Assert(s.Data.Rows.Count > 1 && persons.Data.Rows.Count > 1, "人物引用夹具没有足够人物行");
        // Change only the in-memory draft. A unique baseline prevents old
        // assignments from masking a projection that accidentally reads disk.
        foreach (System.Data.DataRow row in s.Data.Rows) row["S形象ID"] = 33;
        s.Data.Rows[0]["S形象ID"] = 0;
        persons.Data.Rows[0]["兵种"] = 79;
        s.Data.Rows[1]["S形象ID"] = 1;
        var method = typeof(MainForm).GetMethod("GetVerifiedUsedImageIds", flags)!;
        var used = method.Invoke(form, ["S"]) as IReadOnlySet<int>;
        TestSupport.Assert(used != null && used.SetEquals(new[] { 238, 239, 240, 241, 242, 243, 337 }),
            $"主界面S引用没有从当前草稿展开默认兵种三阵营及人物三转物理图号：used={(used == null ? "null" : string.Join(',', used.Order()))}；人物表={persons.Definition.Capability}/{persons.Definition.VerificationLevel}/{persons.Definition.CapabilityEvidence}");
        var referencesMethod = typeof(MainForm).GetMethod("GetCurrentSAssignmentReferences", flags)!;
        var references = referencesMethod.Invoke(form, null) as IReadOnlyList<SImageAssignmentReference>;
        var personId = s.Data.Columns.Contains("ID") ? Convert.ToInt32(s.Data.Rows[1]["ID"]) : s.Definition.BeginId + 1;
        var first = references?.Single(reference => reference.PersonId == personId);
        TestSupport.Assert(first != null && first.SId == 1 && first.PhysicalImageNumbers.SequenceEqual(new[] { 241, 242, 243 }),
            "删除明细回调没有从当前草稿展开S1的全部三转引用");
        var warnings = SImageDeleteImpact.BuildWarnings([242], references, 556);
        var firstWarning = warnings.SingleOrDefault(warning => warning.StartsWith($"人物 ID {personId} /", StringComparison.Ordinal));
        TestSupport.Assert(firstWarning != null && firstWarning.Contains(first!.PersonName, StringComparison.Ordinal) &&
            firstWarning.Contains("紧凑 S=1", StringComparison.Ordinal) && firstWarning.Contains("Unit #242 被删除", StringComparison.Ordinal) &&
            firstWarning.Contains("Unit #243 → #242", StringComparison.Ordinal),
            "删除242的明细缺少当前人物ID/姓名/S1，或没有区分直接删除和后续前移");
        TestSupport.Assert(warnings.Any(warning => warning.Contains("紧凑 S=33", StringComparison.Ordinal) &&
            warning.Contains("Unit #337 → #336", StringComparison.Ordinal)), "删除242没有列出后续紧凑S33引用前移");
        var baseline = references!.Single(reference => reference.SId == 0);
        TestSupport.Assert(!warnings.Any(warning => warning.StartsWith($"人物 ID {baseline.PersonId} /", StringComparison.Ordinal)),
            "删除将领S错误列出了未受影响的默认兵种S人物");
        s.Data.Rows[1]["S形象ID"] = 2;
        used = method.Invoke(form, ["S"]) as IReadOnlySet<int>;
        TestSupport.Assert(used != null && used.SetEquals(new[] { 238, 239, 240, 244, 245, 246, 337 }) && workspace.IsDirty,
            "未保存的人物紧凑S修改没有立即更新物理引用集合，或引用枚举提交了草稿");
        references = referencesMethod.Invoke(form, null) as IReadOnlyList<SImageAssignmentReference>;
        first = references?.Single(reference => reference.PersonId == personId);
        TestSupport.Assert(first != null && first.SId == 2 && first.PhysicalImageNumbers.SequenceEqual(new[] { 244, 245, 246 }),
            "未保存的S1→S2没有立即更新删除明细人物引用");
    }

    private static void CheckItemFrame(Control panel, ProjectWorkspace workspace, ResourceCatalogAdapter adapter, int id)
    {
        var icon = ReadById(workspace, ItemTable(workspace, id), id, "图标");
        using var expected = adapter.DecodeBindingThumbnailAsync(workspace.RootPath, ResourceBindingKind.ItemIcon, icon, new Size(512, 512)).GetAwaiter().GetResult();
        TestSupport.Assert(expected != null, $"宝物{id}图标{icon}真实资源没有可预览大图");
        AssertSamePixels(PreviewPicture(panel, "宝物图标 ").Image!, expected!, $"宝物{id}没有预览当前图标{icon}大图，或旧图回填");
    }

    private static void AssertSamePixels(Image actual, Bitmap expected, string message)
    {
        TestSupport.Assert(actual is Bitmap && actual.Size == expected.Size, message + "（图像尺寸）");
        var bitmap = (Bitmap)actual;
        for (var y = 0; y < expected.Height; y++)
            for (var x = 0; x < expected.Width; x++)
                TestSupport.Assert(bitmap.GetPixel(x, y).ToArgb() == expected.GetPixel(x, y).ToArgb(), message + $"（{x},{y}）");
    }

    private static void CheckPictureLayout(Control panel, string context)
    {
        var pictures = LoadedPictures(panel);
        TestSupport.Assert(pictures.Length > 0, context + "没有可验证的预览图片");
        foreach (var picture in pictures)
        {
            var image = picture.Image!;
            var viewport = picture.Parent!.ClientSize;
            TestSupport.Assert(picture.SizeMode == PictureBoxSizeMode.Zoom && picture.Width > 0 && picture.Height > 0 &&
                viewport.Width > 0 && viewport.Height > 0, context + "预览框没有有效的等比显示区域");
            TestSupport.Assert(Math.Abs((long)picture.Width * image.Height - (long)picture.Height * image.Width) <= Math.Max(image.Width, image.Height),
                context + "预览框比例与原图不符");
            TestSupport.Assert(picture.Left >= 0 && picture.Top >= 0 && picture.Right <= viewport.Width + 1 && picture.Bottom <= viewport.Height + 1,
                context + "预览框超出可用区域，完整图像可能裁切");
            TestSupport.Assert(Math.Abs(picture.Width - viewport.Width) <= 1 || Math.Abs(picture.Height - viewport.Height) <= 1,
                context + "预览未充分利用可用区域，可能仍有固定放大上限");
            TestSupport.Assert(Math.Abs(picture.Left * 2 + picture.Width - viewport.Width) <= 1 &&
                Math.Abs(picture.Top * 2 + picture.Height - viewport.Height) <= 1, context + "预览未居中");
        }
    }

    private static PictureBox PreviewPicture(Control panel, string titlePrefix)
        => Descendants(panel).OfType<ResourcePreviewCard>().Single(group => group.Text.StartsWith(titlePrefix, StringComparison.Ordinal))
            .Controls.Cast<Control>().SelectMany(control => Descendants(control).Prepend(control)).OfType<PictureBox>().Single();

    private static Control LabeledEditor(Control panel, string label)
    {
        var matched = Descendants(panel).OfType<Label>().SingleOrDefault(control => control.Text == label);
        TestSupport.Assert(matched?.Parent is TableLayoutPanel, $"没有找到{label}字段标签");
        var layout = (TableLayoutPanel)matched!.Parent!;
        var position = layout.GetPositionFromControl(matched);
        var adjacent = layout.GetControlFromPosition(position.Column + 1, position.Row);
        return (adjacent == null || adjacent is Label ? layout.GetControlFromPosition(position.Column, position.Row + 1) : adjacent)
            ?? throw new InvalidOperationException("字段没有编辑控件：" + label);
    }

    private static PictureBox[] LoadedPictures(Control panel) => Descendants(panel).OfType<PictureBox>()
        .Where(picture => !picture.IsDisposed && picture.Image != null).ToArray();

    private static void AssertNoDraft(BusinessEditorPanel panel, ProjectWorkspace workspace, int edits, string action)
    {
        TestSupport.Assert(panel.Flush(), action + "后字段无法正常验证");
        TestSupport.Assert(edits == 0 && !workspace.IsDirty && !workspace.Commands.CanUndo && !workspace.Commands.CanRedo,
            action + $"产生了意外编辑事件或可撤回草稿：edits={edits}, dirty={workspace.IsDirty}, undo={workspace.Commands.CanUndo}, redo={workspace.Commands.CanRedo}");
    }

    private static void WaitFor(Func<bool> predicate, string failure)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            Application.DoEvents();
            if (predicate()) return;
            Thread.Sleep(10);
        } while (watch.Elapsed < TimeSpan.FromSeconds(15));
        throw new InvalidOperationException(failure);
    }

    private static void Pump(TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(10); } while (watch.Elapsed < duration);
    }

    private static void SaveSnapshot(Form host, string variant, string module)
    {
        var directory = Environment.GetEnvironmentVariable("DATAMANAGER_UI_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var safe = string.Concat($"关联预览-{variant}-{module}".Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        using var bitmap = new Bitmap(host.Width, host.Height);
        host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, host.Size));
        bitmap.Save(Path.Combine(directory, $"{safe}-{host.Width}x{host.Height}.png"), ImageFormat.Png);
    }

    private static string ItemTable(ProjectWorkspace workspace, int id)
        => new[] { "items-low", "items-high" }.First(key => HasEntity(workspace, key, id));

    private static bool HasEntity(ProjectWorkspace workspace, string key, int id)
        => workspace.Documents.TryGetValue(key, out var document) && id >= document.Definition.BeginId &&
            id < document.Definition.BeginId + document.Data.Rows.Count;

    private static int ReadById(ProjectWorkspace workspace, string key, int id, string field)
    {
        var document = workspace.GetDocument(key);
        return ReadNumber(document, id - document.Definition.BeginId, field);
    }

    private static int ReadNumber(TableDocument document, int rowIndex, string fieldName)
    {
        var field = document.Definition.Fields.Single(field => field.Name == fieldName);
        var offset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        if (field.Kind == FieldKind.RawBytes)
        {
            TestSupport.Assert(field.Size is > 0 and <= 4, "预览数值字段的原始字节宽度不受支持");
            var number = 0;
            for (var index = 0; index < field.Size; index++)
                number |= document.OriginalFileBytes[offset + field.Offset + index] << (index * 8);
            return number;
        }
        return Convert.ToInt32(new RecordCodecRegistry().Get(document.Definition.RecordCodecKey)
            .Decode(document.OriginalFileBytes.AsSpan(offset, document.Definition.RowSize), field), CultureInfo.InvariantCulture);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
