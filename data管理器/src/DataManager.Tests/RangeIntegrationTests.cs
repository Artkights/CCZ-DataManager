using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using RangeManager.App;
using DataForm = DataManager.App.MainForm;

namespace DataManager.Tests;

/// <summary>Exercises the actual Data Manager host and one mixed project transaction on exact temporary copies.</summary>
internal static class RangeIntegrationTests
{
    private sealed record Check(string Name, string Detail);
    private static readonly List<Check> Checks = [];

    public static void Run(string path, string? output)
    {
        var repository = Directory.Exists(Path.Combine(path, "基底")) ? path : TestSupport.RepositoryRootFromBase(path);
        var sources = Directory.Exists(Path.Combine(path, "基底"))
            ? new[] { "洗隋", "加强版6.5未加密版", "复刻版6.6山贼" }
                .Select(name => Path.Combine(path, "基底", name)).ToArray()
            : [path];
        output ??= Path.Combine(repository, "data管理器", "验证", "2026-11-10-范围管理合并", "integration");
        Directory.CreateDirectory(output);
        Checks.Clear();
        var originalHashes = sources.SelectMany(SourceFiles).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(file => file, HashService.Sha256, StringComparer.OrdinalIgnoreCase);
        var passed = false;
        try
        {
            foreach (var source in sources) MixedRoundTrip(source, output);
            InvalidRangeRetainsMixedDraft(sources.FirstOrDefault(source => Path.GetFileName(source).Contains("6.5")) ?? sources[0]);
            ConflictingExeEditsAreRejected(sources.FirstOrDefault(source => Path.GetFileName(source).Contains("6.5")) ?? sources[0]);
            LateTransactionFailureRestoresRangeDraft(sources.FirstOrDefault(source => Path.GetFileName(source).Contains("6.5")) ?? sources[0]);
            CleanProjectSwitchReleasesSession(sources[0], sources[^1]);
            passed = true;
        }
        finally
        {
            var afterHashes = originalHashes.Keys.ToDictionary(file => file, HashService.Sha256, StringComparer.OrdinalIgnoreCase);
            var unchanged = originalHashes.All(pair => afterHashes[pair.Key] == pair.Value);
            File.WriteAllText(Path.Combine(output, "range-integration.json"), JsonSerializer.Serialize(new
            {
                Passed = passed && unchanged,
                RuntimeFormat = "Existing CCZRNG02 / CCZRNG03 footer version 2",
                OriginalSourcesUnchanged = unchanged,
                Checks,
                OriginalHashes = originalHashes,
                FinalHashes = afterHashes
            }, new JsonSerializerOptions { WriteIndented = true }));
            TestSupport.Assert(unchanged, "范围整合验收改变了真实基底文件");
        }
        Console.WriteLine($"RANGE_INTEGRATION_TESTS_OK bases={sources.Length} checks={Checks.Count}");
    }

    private static void MixedRoundTrip(string source, string output)
    {
        var root = CreateFixture(source);
        using var form = NewForm();
        try
        {
            form.Show();
            form.LoadProjectForSmokeTest(root);
            Application.DoEvents();
            var workspace = form.WorkspaceForSmokeTest;
            var session = form.RangeSessionForSmokeTest ?? throw new InvalidOperationException("项目没有创建范围会话");
            var host = form.RangeHostForSmokeTest;
            var rangeTab = form.ModuleTabsForSmokeTest.TabPages.Cast<TabPage>().Single(page => page.Text == "范围管理");
            var rangeKey = Convert.ToString(rangeTab.Tag)!;
            form.SelectModuleForSmokeTest(rangeKey);
            Application.DoEvents();
            TestSupport.Assert(session.Game.RootPath == root && workspace.RootPath == root && host.Visible,
                "Data/范围会话没有共用当前临时项目，或范围页未显示");
            TestSupport.Assert(form.RangeNavigationCollapsedForSmokeTest, "范围页仍保留外层重复实体列表");
            var tabs = Descendants(host).OfType<TabControl>().Single();
            TestSupport.Assert(tabs.TabPages.Cast<TabPage>().Select(page => page.Text)
                .SequenceEqual(["攻击范围", "穿透范围", "拓展范围"]), "范围管理未保留原三子页");
            var attack = Descendants(host).OfType<RangeEditorPanel>().Single(panel => panel.SelectedKey?.Kind == RangeKind.Attack);
            var pierce = Descendants(host).OfType<RangeEditorPanel>().Single(panel => panel.SelectedKey?.Kind == RangeKind.Pierce);
            TestSupport.Assert(Descendants(host).OfType<RangeExtensionPanel>().Count() == 1 &&
                !Descendants(host).OfType<DataManager.App.ByteTextEditor>().Any(), "范围组件结构异常或意外加入了名称编辑功能");
            TestSupport.Assert(!workspace.IsDirty && !session.IsDirty, "只读进入整合范围页产生草稿");
            LayoutAndScreenshots(form, tabs, attack, pierce, output, session.Game.VersionText);
            Pass(source, "host", "主范围页、原三子页、单项目会话、1280/1920布局和原有控件");

            var attackKey = new RangeKey(RangeKind.Attack, 1);
            var pierceKey = new RangeKey(RangeKind.Pierce, 1);
            var attackOriginal = session.GetText(attackKey, 0);
            var pierceOriginal = session.GetText(pierceKey, 0);
            var attackText = AddGenerator(session.Get(attackKey));
            var pierceText = AddGenerator(session.Get(pierceKey));
            var mappingOriginal = session.Extension.Values[0x21];
            var mappingNew = mappingOriginal == 2 ? 3 : 2;
            var people = workspace.GetDocument("persons");
            var series = workspace.GetDocument("job-series");
            var powerOriginal = Convert.ToInt32(people.Data.Rows[0]["武力"]);
            var powerNew = (powerOriginal + 1) % 256;
            var seriesOriginal = Convert.ToString(series.Data.Rows[0]["名称"])!;
            const string seriesNew = "整合验收";
            var names = session.Bindings.ToDictionary(binding => binding.Key, binding => binding.Name);
            var nativeNames = CaptureNativeNames(session.Game);
            var filesBefore = ReadFixture(root);

            workspace.ApplyCompositeTransaction("范围整合：普通DATA草稿", [new("persons", 0, "武力", powerNew)]);
            workspace.ApplyCompositeTransaction("范围整合：普通EXE草稿", [new("job-series", 0, "名称", seriesNew)]);
            tabs.SelectedIndex = 0;
            TestSupport.Assert(attack.EditTextForTest(1, attackText), "攻击范围实际文本控件编辑失败");
            tabs.SelectedIndex = 1;
            TestSupport.Assert(pierce.EditTextForTest(1, pierceText), "穿透范围实际文本控件编辑失败");
            tabs.SelectedIndex = 2;
            TestSupport.Assert(session.UpdateExtension(0x21, mappingNew, out var error), error);
            Application.DoEvents();
            var attackExpected = session.Preview(attackKey, RangePreviewContext.Default).ToHashSet();
            var pierceExpected = session.Preview(pierceKey, RangePreviewContext.Default).ToHashSet();
            AssertFiles(filesBefore, root, "草稿编辑提前写盘");

            // Deliberately switch pages while undoing: there must be one chronological project stack.
            for (var step = 0; step < 5; step++)
            {
                form.SelectModuleForSmokeTest(step % 2 == 0 ? "人物" : rangeKey);
                form.UndoDraftForSmokeTest();
                Application.DoEvents();
                TestSupport.Assert(session.Extension.Values[0x21] == mappingOriginal, "全局撤回没有先撤回最后的范围映射操作");
                if (step >= 1) TestSupport.Assert(session.GetText(pierceKey, 0) == pierceOriginal, "穿透草稿未按全局顺序撤回");
                if (step >= 2) TestSupport.Assert(session.GetText(attackKey, 0) == attackOriginal, "攻击草稿未按全局顺序撤回");
                if (step >= 3) TestSupport.Assert(Convert.ToString(series.Data.Rows[0]["名称"]) == seriesOriginal, "普通EXE草稿未按全局顺序撤回");
            }
            TestSupport.Assert(Convert.ToInt32(people.Data.Rows[0]["武力"]) == powerOriginal && !workspace.IsDirty && !session.IsDirty,
                "五次全局撤回后未恢复干净基线");
            for (var step = 0; step < 5; step++)
            {
                form.SelectModuleForSmokeTest(step % 2 == 0 ? rangeKey : "人物");
                form.RedoDraftForSmokeTest();
                Application.DoEvents();
            }
            TestSupport.Assert(Convert.ToInt32(people.Data.Rows[0]["武力"]) == powerNew &&
                Convert.ToString(series.Data.Rows[0]["名称"]) == seriesNew &&
                session.Preview(attackKey, RangePreviewContext.Default).ToHashSet().SetEquals(attackExpected) &&
                session.Preview(pierceKey, RangePreviewContext.Default).ToHashSet().SetEquals(pierceExpected) &&
                session.Extension.Values[0x21] == mappingNew, "全局前进没有恢复全部五项混合草稿");
            AssertFiles(filesBefore, root, "撤回/前进写入了基底");
            Pass(source, "draft-order", "人物DATA、兵种系EXE、攻击、穿透、拓展映射五项按时间跨页撤回与前进");

            form.SelectModuleForSmokeTest("人物");
            var saved = form.SavePendingForSmokeTest();
            TestSupport.Assert(saved.Saved, "Data顶部混合保存失败：" + string.Join("；", saved.Messages));
            TestSupport.Assert(!workspace.IsDirty && !session.IsDirty, "保存成功后仍有范围或Data草稿");
            var filesSaved = ReadFixture(root);
            foreach (var file in new[] { "Ekd5.exe", "Data.e5" })
                TestSupport.Assert(!filesBefore[file].SequenceEqual(filesSaved[file]), "混合保存缺少实际写回：" + file);
            foreach (var file in new[] { FindRangeRelative(root, "Hitarea.e5"), FindRangeRelative(root, "Effarea.e5") })
                TestSupport.Assert(!filesBefore[file].SequenceEqual(filesSaved[file]), "几何保存未同步范围图标：" + file);
            TestSupport.Assert(saved.History != null && saved.History.Files.Count == 4, "混合文件未进入同一个保存历史");
            AssertRoundTrip(root, attackKey, pierceKey, attackExpected, pierceExpected, mappingNew, powerNew, seriesNew, names, nativeNames);
            AssertRuntimeVersionTwo(root);
            Pass(source, "mixed-save", "Data.e5 + EXE普通字段/范围 + Hitarea/Effarea 一次保存、原名称字节不变、原v2格式复读");

            TestSupport.RestoreSavedHistory(form, redo: false);
            AssertFiles(filesBefore, root, "保存历史撤回没有逐字节恢复混合文件");
            TestSupport.Assert(!session.IsDirty && session.GetText(attackKey, 0) == attackOriginal &&
                session.GetText(pierceKey, 0) == pierceOriginal && session.Extension.Values[0x21] == mappingOriginal,
                "历史撤回后范围会话没有随当前磁盘重新载入");
            TestSupport.RestoreSavedHistory(form, redo: true);
            AssertFiles(filesSaved, root, "保存历史前进没有逐字节恢复混合文件");
            TestSupport.Assert(session.Preview(attackKey, RangePreviewContext.Default).ToHashSet().SetEquals(attackExpected) &&
                session.Preview(pierceKey, RangePreviewContext.Default).ToHashSet().SetEquals(pierceExpected) &&
                session.Extension.Values[0x21] == mappingNew, "历史前进后范围会话未同步");
            TestSupport.Assert(workspace.UndoSaved().Success && session.Extension.Values[0x21] == mappingOriginal,
                "直接工作区历史撤回没有同步范围参与者");
            AssertFiles(filesBefore, root, "直接工作区历史撤回没有恢复混合文件");
            TestSupport.Assert(workspace.RedoSaved().Success && session.Extension.Values[0x21] == mappingNew,
                "直接工作区历史前进没有同步范围参与者");
            AssertFiles(filesSaved, root, "直接工作区历史前进没有恢复混合文件");
            TestSupport.Assert(!workspace.Commands.CanUndo && !workspace.Commands.CanRedo,
                "历史重载范围会话生成了额外全局草稿命令");
            Pass(source, "saved-history", "同一历史整批逐字节撤回/前进，范围会话随历史重载");

            // A subsequent ordinary EXE save detects stale workspace hashes after a range append.
            workspace.ApplyCompositeTransaction("范围保存后的普通EXE编辑", [new("job-series", 0, "名称", "再次验收")]);
            TestSupport.Assert(form.SavePendingForSmokeTest().Saved, "范围保存后普通EXE保存发生基线冲突");
            using var reopened = new RangeEditSession(root);
            TestSupport.Assert(reopened.Preview(attackKey, RangePreviewContext.Default).ToHashSet().SetEquals(attackExpected) &&
                reopened.Preview(pierceKey, RangePreviewContext.Default).ToHashSet().SetEquals(pierceExpected) &&
                reopened.Extension.Values[0x21] == mappingNew, "后续普通EXE保存破坏范围运行时");
            Pass(source, "subsequent-save", "范围保存后继续修改EXE，保存/重开保留运行时且无旧哈希冲突");
        }
        finally
        {
            form.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void InvalidRangeRetainsMixedDraft(string source)
    {
        var root = CreateFixture(source);
        using var form = NewForm();
        try
        {
            form.Show(); form.LoadProjectForSmokeTest(root); Application.DoEvents();
            var workspace = form.WorkspaceForSmokeTest;
            var session = form.RangeSessionForSmokeTest!;
            var before = ReadFixture(root);
            var value = Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[0]["武力"]);
            workspace.ApplyCompositeTransaction("非法范围时保留普通草稿", [new("persons", 0, "武力", (value + 1) % 256)]);
            var key = new RangeKey(RangeKind.Attack, 1);
            TestSupport.Assert(!session.UpdateText(key, 0, "invalid coordinate", out _), "非法坐标竟被接受");
            var result = form.SavePendingForSmokeTest();
            TestSupport.Assert(!result.Saved && workspace.IsDirty && session.IsDirty && session.GetTextError(key, 0) != null,
                "非法范围没有阻止混合保存，或失败后丢失草稿");
            AssertFiles(before, root, "非法范围导致部分普通数据写盘");
            form.UndoDraftForSmokeTest();
            form.UndoDraftForSmokeTest();
            TestSupport.Assert(!workspace.IsDirty && !session.IsDirty, "失败草稿不能通过全局撤回恢复");
            Pass(source, "invalid-atomicity", "非法范围阻止整批保存，普通草稿/错误文本保留，全局撤回可恢复");
        }
        finally { form.Dispose(); Directory.Delete(root, recursive: true); }
    }

    private static void ConflictingExeEditsAreRejected(string source)
    {
        var root = CreateFixture(source);
        using var form = NewForm();
        try
        {
            var exePath = Path.Combine(root, "Ekd5.exe");
            var overlay = Encoding.ASCII.GetBytes("RANGE_HOST_OVERLAY_20261110");
            File.WriteAllBytes(exePath, File.ReadAllBytes(exePath).Concat(overlay).ToArray());
            form.Show(); form.LoadProjectForSmokeTest(root); Application.DoEvents();
            var workspace = form.WorkspaceForSmokeTest;
            var session = form.RangeSessionForSmokeTest ?? throw new InvalidOperationException("含overlay的基底未打开范围会话");
            var key = new RangeKey(RangeKind.Attack, 1);
            var originalPower = Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[0]["武力"]);
            workspace.ApplyCompositeTransaction("冲突时保留普通DATA草稿", [new("persons", 0, "武力", (originalPower + 1) % 256)]);
            TestSupport.Assert(session.UpdateText(key, 0, AddGenerator(session.Get(key)), out var error), error);
            var baseline = ReadFixture(root);
            var pointer = session.Get(key).PointerSlots.First();
            var pointerOffset = session.Game.Exe.VaToOffset(pointer);

            void RejectPatch(long offset, string scenario)
            {
                var bytes = File.ReadAllBytes(exePath);
                var old = bytes[checked((int)offset)];
                var patch = new DataManager.Core.Models.PreparedBinaryEdit("Ekd5.exe", HashService.Sha256(bytes),
                    [new(offset, [old], [(byte)(old ^ 1)])], "整合冲突验收", scenario);
                var result = workspace.Save([patch]);
                TestSupport.Assert(!result.Saved && workspace.IsDirty && session.IsDirty,
                    $"{scenario}没有拒绝整批保存或丢失混合草稿：offset=0x{offset:X} saved={result.Saved} workspaceDirty={workspace.IsDirty} sessionDirty={session.IsDirty} messages={string.Join("；", result.Messages)}");
                AssertFiles(baseline, root, scenario + "导致部分写盘");
                Pass(source, scenario, string.Join("；", result.Messages));
            }

            RejectPatch(pointerOffset, "native-overlap-rejected");
            RejectPatch(baseline["Ekd5.exe"].Length - 1, "overlay-edit-rejected");
            var saved = form.SavePendingForSmokeTest();
            TestSupport.Assert(saved.Saved && File.ReadAllBytes(exePath).AsSpan()[^overlay.Length..].SequenceEqual(overlay),
                "取消冲突补丁后不能保存原草稿，或范围重建改变overlay");
            TestSupport.Assert(!workspace.Commands.CanUndo && !workspace.Commands.CanRedo && !session.IsDirty,
                "成功保存/接受范围会话产生了额外全局命令");

            // A normal patch into the previous JSON block must be rejected before the builder moves it.
            workspace.ApplyCompositeTransaction("旧扩展区冲突时保留普通DATA草稿", [new("persons", 0, "武力", (originalPower + 2) % 256)]);
            TestSupport.Assert(session.UpdateText(key, 0, AddGenerator(session.Get(key)), out error), error);
            TestSupport.Assert(session.IsDirty, "旧扩展区冲突验收没有创建实质范围草稿");
            baseline = ReadFixture(root);
            var pe = new PeFile(exePath);
            var section = pe.Sections.OrderBy(section => section.RawOffset).Last();
            var footer = checked((int)(section.RawOffset + section.RawSize) - 64);
            TestSupport.Assert(Encoding.ASCII.GetString(pe.Bytes, footer, 8) is "CCZRNG02" or "CCZRNG03",
                $"冲突后重试没有写入范围运行时：footer=0x{footer:X} magic={Convert.ToHexString(pe.Bytes.AsSpan(footer, 8))}");
            var metadata = section.RawOffset + BinaryPrimitives.ReadUInt32LittleEndian(pe.Bytes.AsSpan(footer + 16, 4));
            RejectPatch(metadata + 12, "old-managed-payload-edit-rejected");
            TestSupport.Assert(form.SavePendingForSmokeTest().Saved &&
                File.ReadAllBytes(exePath).AsSpan()[^overlay.Length..].SequenceEqual(overlay),
                "旧扩展区冲突后原草稿无法重试保存或overlay未保持");
            Pass(source, "conflict-retry", "原生重叠/overlay/旧扩展区补丁均拒绝；移除冲突后原草稿可重试，overlay字节保留");
        }
        finally { form.Dispose(); Directory.Delete(root, recursive: true); }
    }

    private static void CleanProjectSwitchReleasesSession(string firstSource, string nextSource)
    {
        var first = CreateFixture(firstSource);
        var next = CreateFixture(nextSource);
        using var form = NewForm();
        try
        {
            var firstBytes = ReadFixture(first);
            var nextBytes = ReadFixture(next);
            form.Show(); form.LoadProjectForSmokeTest(first); Application.DoEvents();
            var previous = form.RangeSessionForSmokeTest!;
            TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty && !previous.IsDirty,
                "首次打开项目产生意外草稿");
            form.LoadProjectForSmokeTest(next); Application.DoEvents();
            var workspace = form.WorkspaceForSmokeTest;
            var current = form.RangeSessionForSmokeTest!;
            TestSupport.Assert(!ReferenceEquals(previous, current) && current.Game.RootPath == next &&
                workspace.RootPath == next && !workspace.Commands.CanUndo && !workspace.Commands.CanRedo && !workspace.IsDirty,
                "切换项目没有替换范围会话、清理旧命令或绑定当前路径");
            var key = new RangeKey(RangeKind.Attack, 1);
            TestSupport.Assert(form.RangeHostForSmokeTest.AttackEditor.EditTextForTest(key.Id, AddGenerator(current.Get(key))) &&
                current.IsDirty && workspace.Commands.CanUndo,
                "新项目范围页没有接入新的全局草稿命令");
            form.UndoDraftForSmokeTest();
            TestSupport.Assert(!current.IsDirty && !workspace.IsDirty && workspace.Commands.CanRedo,
                "新项目全局撤回触及旧会话或不能清理新草稿");
            form.LoadProjectForSmokeTest(next); Application.DoEvents();
            TestSupport.Assert(!ReferenceEquals(current, form.RangeSessionForSmokeTest) &&
                !form.WorkspaceForSmokeTest.Commands.CanUndo && !form.WorkspaceForSmokeTest.Commands.CanRedo &&
                !form.WorkspaceForSmokeTest.IsDirty,
                "重新加载clean项目没有更换会话或清理旧redo命令");
            AssertFiles(firstBytes, first, "项目切换改变前一项目磁盘文件");
            AssertFiles(nextBytes, next, "项目切换/clean重载改变后一项目磁盘文件");
            Pass(nextSource, "clean-project-switch", "同窗切换两临时项目，范围会话/参与者重新绑定、旧命令清空；新项目撤回及clean重载均无串扰");
        }
        finally { form.Dispose(); Directory.Delete(first, recursive: true); Directory.Delete(next, recursive: true); }
    }

    private sealed class RejectAfterRangeAccepted : IWorkspaceDraftParticipant
    {
        public bool HasPendingChanges => true;
        public void AcceptSaved() => throw new IOException("范围整合验收：提交末段故障注入");
        public void Discard() { }
    }

    private static void LateTransactionFailureRestoresRangeDraft(string source)
    {
        var root = CreateFixture(source);
        using var form = NewForm();
        try
        {
            form.Show(); form.LoadProjectForSmokeTest(root); Application.DoEvents();
            var workspace = form.WorkspaceForSmokeTest;
            var session = form.RangeSessionForSmokeTest!;
            var before = ReadFixture(root);
            var value = Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[0]["武力"]);
            workspace.ApplyCompositeTransaction("提交末段故障时保留普通DATA草稿", [new("persons", 0, "武力", (value + 1) % 256)]);
            var key = new RangeKey(RangeKind.Attack, 1);
            TestSupport.Assert(session.UpdateText(key, 0, AddGenerator(session.Get(key)), out var error), error);
            var expected = session.Preview(key, RangePreviewContext.Default).ToHashSet();
            var failing = new RejectAfterRangeAccepted();
            workspace.RegisterDraftParticipant(failing);
            var result = form.SavePendingForSmokeTest();
            TestSupport.Assert(!result.Saved && workspace.IsDirty && session.IsDirty &&
                session.Preview(key, RangePreviewContext.Default).ToHashSet().SetEquals(expected),
                "提交末段失败没有恢复普通/范围草稿");
            AssertFiles(before, root, "提交末段失败没有恢复混合磁盘文件");
            workspace.UnregisterDraftParticipant(failing);
            TestSupport.Assert(form.SavePendingForSmokeTest().Saved && !workspace.IsDirty && !session.IsDirty,
                "移除故障后保留的混合草稿无法重试保存");
            using var reopened = new RangeEditSession(root);
            TestSupport.Assert(reopened.Preview(key, RangePreviewContext.Default).ToHashSet().SetEquals(expected),
                "提交末段故障后重试保存的范围不能复读");
            Pass(source, "late-transaction-rollback", "Range已接受候选后注入提交末段异常；全部文件恢复、混合草稿保留、移除故障后重试成功");
        }
        finally { form.Dispose(); Directory.Delete(root, recursive: true); }
    }

    private static void LayoutAndScreenshots(DataForm form, TabControl tabs, RangeEditorPanel attack,
        RangeEditorPanel pierce, string output, string version)
    {
        foreach (var size in new[] { new Size(1280, 800), new Size(1920, 1080) })
        {
            form.Size = size;
            for (var page = 0; page < 3; page++)
            {
                tabs.SelectedIndex = page; Application.DoEvents();
                if (page < 2)
                {
                    var editor = page == 0 ? attack : pierce;
                    TestSupport.Assert(editor.Visible && editor.PreviewForTest.Width >= 180 &&
                        editor.PreviewForTest.Height >= 120 && editor.GeneratorsForTest.Width >= 200,
                        $"{version} {size} 范围编辑区或预览不可用");
                }
                if (version == "6.5")
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(output, $"host-{version}-{size.Width}x{size.Height}-page{page}.png"), ImageFormat.Png);
                }
                foreach (var button in Descendants(form.RangeHostForSmokeTest).OfType<Button>().Where(button => button.Visible &&
                    button.Text is "新增" or "删除" or "修改" or "应用生成元" or "恢复本项" or "恢复本表"))
                    AssertFullyVisible(button, $"{version} {size} page={page}");
            }
        }
    }

    private static void AssertRoundTrip(string root, RangeKey attackKey, RangeKey pierceKey,
        HashSet<CellOffset> attackExpected, HashSet<CellOffset> pierceExpected, int mapping, int power, string name,
        Dictionary<RangeKey, string> names, Dictionary<uint, byte[]> nativeNames)
    {
        using var session = new RangeEditSession(root);
        TestSupport.Assert(session.Preview(attackKey, RangePreviewContext.Default).ToHashSet().SetEquals(attackExpected) &&
            session.Preview(pierceKey, RangePreviewContext.Default).ToHashSet().SetEquals(pierceExpected) &&
            session.Extension.Values[0x21] == mapping, "保存重开后的EXE几何或拓展映射不一致");
        TestSupport.Assert(session.Bindings.All(binding => names[binding.Key] == binding.Name), "仅合并操作改变了范围名称");
        foreach (var pair in nativeNames)
            TestSupport.Assert(session.Game.Exe.Bytes.AsSpan(session.Game.Exe.VaToOffset(pair.Key), pair.Value.Length).SequenceEqual(pair.Value),
                $"仅合并操作改变了原生名称槽0x{pair.Key:X8}");
        var workspace = new ProjectWorkspace(); workspace.Open(root);
        TestSupport.Assert(Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[0]["武力"]) == power &&
            Convert.ToString(workspace.GetDocument("job-series").Data.Rows[0]["名称"]) == name,
            "混合保存丢失普通DATA/EXE字段");
        using var images = new E5RangeImages(root);
        using var attack = images.Copy(true, attackKey.Id);
        using var pierce = images.Copy(false, pierceKey.Id);
        TestSupport.Assert(attack != null && pierce != null && images.CanReadAttack && images.CanReadPierce,
            "范围E5同步输出无法重开");
        if (images.AttackIsIndexed) TestSupport.Assert(images.DecodedLength(true, attackKey.Id) == 4096, "索引Hitarea格式被改变");
        if (images.PierceIsIndexed) TestSupport.Assert(images.DecodedLength(false, pierceKey.Id) == 4096, "索引Effarea格式被改变");
    }

    private static void AssertRuntimeVersionTwo(string root)
    {
        var pe = new PeFile(Path.Combine(root, "Ekd5.exe"));
        var section = pe.Sections.OrderBy(section => section.RawOffset).Last();
        var footer = checked((int)(section.RawOffset + section.RawSize) - 64);
        var magic = Encoding.ASCII.GetString(pe.Bytes, footer, 8);
        TestSupport.Assert(magic is "CCZRNG02" or "CCZRNG03" &&
            BinaryPrimitives.ReadUInt32LittleEndian(pe.Bytes.AsSpan(footer + 8, 4)) == 2,
            "整合保存没有沿用现有范围v2格式");
        var metadata = checked((int)(section.RawOffset + BinaryPrimitives.ReadUInt32LittleEndian(pe.Bytes.AsSpan(footer + 16, 4))));
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pe.Bytes.AsSpan(footer + 20, 4)));
        using var json = JsonDocument.Parse(pe.Bytes.AsMemory(metadata, length));
        TestSupport.Assert(json.RootElement.GetProperty("Format").GetInt32() == 2 &&
            json.RootElement.GetProperty("Mapping").GetProperty("Format").GetInt32() == 2 &&
            json.RootElement.GetProperty("Mapping").GetProperty("Contract").GetString() == "CCZRNG03-v2",
            "范围/映射元数据格式出现未授权升级");
    }

    private static Dictionary<uint, byte[]> CaptureNativeNames(GameBase game)
    {
        var table = game.Version == EngineVersion.V65 ? 0x40CEE9u : 0x40620Au;
        var count = game.Version == EngineVersion.V64 ? 10 : 12;
        var result = new Dictionary<uint, byte[]> { [table] = game.Exe.Bytes.AsSpan(game.Exe.VaToOffset(table), count * 4).ToArray() };
        foreach (var id in Enumerable.Range(119, 9).Concat(Enumerable.Range(157, 8)))
        {
            var address = 0x4CC600u + (uint)(id * 16);
            result[address] = game.Exe.Bytes.AsSpan(game.Exe.VaToOffset(address), 16).ToArray();
        }
        return result;
    }

    private static string AddGenerator(RangeBinding binding)
    {
        var group = binding.Definition.Groups[0];
        var existing = group.Generators.Select(cell => group.Geometry == RangeGeometryKind.Mirror
            ? ShapeGeometry.ToGenerator(cell)! : cell).ToHashSet();
        var next = Enumerable.Range(2, 8).Select(x => new CellOffset(x, 0))
            .Select(cell => group.Geometry == RangeGeometryKind.Mirror ? ShapeGeometry.ToGenerator(cell)! : cell)
            .First(cell => !existing.Contains(cell));
        return string.Join("; ", group.Generators.Append(next).Select(cell => $"{cell.Dx},{cell.Dy}"));
    }

    private static DataForm NewForm() => new(openDefaultProject: false)
    {
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-32000, -32000),
        ShowInTaskbar = false
    };

    private static string CreateFixture(string source)
    {
        var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        foreach (var name in new[] { "Hitarea.e5", "Effarea.e5", "Spalet.e5" })
        {
            var path = new[] { Path.Combine(source, "E5", name), Path.Combine(source, name) }.FirstOrDefault(File.Exists);
            if (path == null) continue;
            var target = Path.Combine(root, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
        return root;
    }

    private static IEnumerable<string> SourceFiles(string root)
        => new[] { "Ekd5.exe", "Data.e5", "Imsg.e5", "Star.e5", "Font.e5", "Hitarea.e5", "Effarea.e5", "Spalet.e5" }
            .SelectMany(name => new[] { Path.Combine(root, name), Path.Combine(root, "E5", name) }).Where(File.Exists);

    private static Dictionary<string, byte[]> ReadFixture(string root)
        => SourceFiles(root).ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertFiles(Dictionary<string, byte[]> expected, string root, string message)
    {
        foreach (var pair in expected)
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(root, pair.Key)).SequenceEqual(pair.Value), message + "：" + pair.Key);
    }

    private static string FindRangeRelative(string root, string name)
        => File.Exists(Path.Combine(root, "E5", name)) ? Path.Combine("E5", name) : name;

    private static IEnumerable<Control> Descendants(Control root)
        => root.Controls.Cast<Control>().SelectMany(control => new[] { control }.Concat(Descendants(control)));

    private static void AssertFullyVisible(Control control, string scenario)
    {
        var rectangle = control.RectangleToScreen(new Rectangle(Point.Empty, control.Size));
        for (var ancestor = control.Parent; ancestor != null; ancestor = ancestor.Parent)
            TestSupport.Assert(ancestor.RectangleToScreen(ancestor.ClientRectangle).Contains(rectangle),
                $"{scenario} {control.Text} 被祖先{ancestor.GetType().Name}/{ancestor.Name}裁剪：control={rectangle} ancestor={ancestor.RectangleToScreen(ancestor.ClientRectangle)}");
    }

    private static void Pass(string source, string name, string detail)
    {
        var scenario = Path.GetFileName(source) + "/" + name;
        Checks.Add(new(scenario, detail));
        Console.WriteLine("PASS " + scenario + ": " + detail);
    }
}
