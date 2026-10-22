using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Scenarios;

namespace DataManager.Tests;

internal static class ScenarioEffectAssignmentTests
{
    public static void Run(string basePath)
    {
        VerifyParsing();
        VerifyRawPatchesAndJumps();
        VerifyBoundsAndConflicts();
        VerifyWorkspaceDraftAndSave(basePath);
        VerifyRealScenarios(basePath);
    }

    private static void VerifyParsing()
    {
        const string originalRemarks = "张飞失去士气辅攻天赋。\r\n备注尾部";
        var bytes = Document(Concat(
            Effect(5, "124\r\n3\r\n915\r\n" + originalRemarks),
            Effect(10, "1\r\n\t0 , 84 , 1 , 71 , 220 , -5 \r\n套装【原备注】\n尾\r\n"),
            Effect(10, "1\n0,84,1,71\n不完整"),
            Effect(5, "*15\n*3\n-*4\n*19\n变量备注"),
            Effect(6, "忽略其他子功能")));
        var entries = ScenarioEffectAssignmentService.ParseAssignments("RS/自定义系统.eex", bytes, 4);
        TestSupport.Assert(entries.Count == 4, "必须只扫描72/5和72/10，并包括非R_/S_命名的剧本。");
        TestSupport.Assert(entries[0].Kind == "人物" && entries[0].CharacterSlots == 4 && entries[0].Parameters["Slot"] == "3" &&
            !entries[0].Parameters.ContainsKey("Value") && entries[0].Remarks == originalRemarks && entries[0].IsWritable,
            "现代6.6缺失第四数值不能将槽3误认为兵种，备注须逐字保留。");
        TestSupport.Assert(entries[0].SourceDisplay.Contains("Scene 1 / Section 1 / Command 2", StringComparison.Ordinal),
            "剧本位置必须准确到Scene、Section和命令。");
        TestSupport.Assert(entries[1].Kind == "套装" && entries[1].Parameters["WeaponId"] == "1" && entries[1].Parameters["Value"] == "-5" &&
            entries[1].Remarks == "套装【原备注】\n尾\r\n", "套装带空白的逗号参数或原始备注读取错误。");
        TestSupport.Assert(!entries[2].IsWritable && entries[2].Diagnostic.Length > 0, "不完整套装参数必须只读。");
        TestSupport.Assert(entries[3].Parameters["TargetId"] == "-*4" && entries[3].Parameters["Value"] == "*19", "变量和负变量表达式被误读。");

        var three = Document(Effect(5, "124\n3\n915\n123\n原始备注"));
        var old = ScenarioEffectAssignmentService.ParseAssignments("RS/旧槽位.eex", three, 3).Single();
        TestSupport.Assert(old.Kind == "兵种" && !old.Parameters.ContainsKey("Value") && old.Remarks == "123\n原始备注",
            "旧3+1引擎第四行数字是备注，不可误读为现代Value。");
        var unknown = ScenarioEffectAssignmentService.ParseAssignments("RS/未知引擎.eex", three, 0, false).Single();
        TestSupport.Assert(!unknown.Parameters.ContainsKey("Value") && unknown.Remarks == "123\n原始备注" && !unknown.LayoutVerified,
            "未知引擎不可猜测第四行的语义。");
    }

    private static void VerifyRawPatchesAndJumps()
    {
        var conditional = Concat(U16(1), U16(2), U16(5), EncodingService.GameEncoding.GetBytes("条件备注"), [0],
            Block(Concat(Effect(5, "22\n4\n12\n10000\n嵌套备注"), U16(3))));
        var bytes = Document(Concat(Jump(), Effect(5, "1\r\n0\r\n15\r\n2\r\n主备注\r\n"), conditional, Jump(), U16(3)), Concat(Effect(10, "0\n0,18,6,10,1\n另一Scene"), U16(3)));
        var before = EexStructure.Parse(bytes);
        var firstEffect = before.Commands.First(c => c.Id == 0x72);
        var jumps = before.Commands.Where(c => c.Id == 0x76).ToArray();
        SetJump(bytes, jumps[0], before.Commands.First(c => c.Scene == 2).Offset);
        SetJump(bytes, jumps[1], firstEffect.Offset);
        before = EexStructure.Parse(bytes);
        var mainText = firstEffect.Parameters.Single(p => p.Tag == 5);
        var nested = before.Commands.Where(c => c.Id == 0x72).Skip(1).First().Parameters.Single(p => p.Tag == 5);
        var mainToken = IndexOf(bytes, Encoding.ASCII.GetBytes("15"), mainText.Offset);
        var nestedToken = IndexOf(bytes, Encoding.ASCII.GetBytes("10000"), nested.Offset);
        var patches = new[]
        {
            new ScenarioTokenReplacement(mainToken, Encoding.ASCII.GetBytes("15"), Encoding.ASCII.GetBytes("*12345")),
            new ScenarioTokenReplacement(nestedToken, Encoding.ASCII.GetBytes("10000"), Encoding.ASCII.GetBytes("-2"))
        };
        var after = new ScenarioParameterPatchBuilder().Build(bytes, patches);
        var reparsed = EexStructure.Parse(after);
        TestSupport.Assert(after.Length == bytes.Length + 1 && reparsed.SceneOffsets[1] == before.SceneOffsets[1] + 1,
            "变长参数未同步更新后续Scene偏移。");
        TestSupport.Assert(before.Lengths.Count == reparsed.Lengths.Count && before.Lengths.Count >= 5,
            "未覆盖Section、Body及SubEvent嵌套长度。");
        foreach (var command in before.Commands.Where(c => c.Id == 0x76))
        {
            var oldTarget = command.Offset + 8 + command.Parameters.Single(p => p.Tag == 4).Number!.Value;
            var oldOrdinal = before.Commands.Single(c => c.Offset == oldTarget).Ordinal;
            var next = reparsed.Commands.Single(c => c.Ordinal == command.Ordinal);
            var nextTarget = next.Offset + 8 + next.Parameters.Single(p => p.Tag == 4).Number!.Value;
            TestSupport.Assert(reparsed.Commands.Single(c => c.Offset == nextTarget).Ordinal == oldOrdinal,
                "变长参数改变了0x76前向或后向跳转的目标命令。");
        }
        AssertOnlyAuthorizedBytesChanged(bytes, after, before, patches);
        TestSupport.Assert(after.AsSpan(after.Length - 6).SequenceEqual(new byte[] { 0xFF, 0x81, 0x31, 0xD0, 0xAB, 0xCD }),
            "文件尾部未解释的原始字节不能被重建或丢弃。");
    }

    private static void VerifyBoundsAndConflicts()
    {
        var bytes = Document(Concat(Jump(), Effect(5, "1\n0\n15\n2\n备注"), U16(3)));
        var structure = EexStructure.Parse(bytes); var command = structure.Commands.Single(c => c.Id == 0x72);
        var parameter = command.Parameters.Single(p => p.Tag == 5);
        var patch = new ScenarioTokenReplacement(parameter.Offset, [49], [49, 48]);
        ExpectFailure(() => new ScenarioParameterPatchBuilder().Build(bytes, [patch with { ExpectedBytes = [50] }]), "补丁未拒绝原始字节冲突。");
        ExpectFailure(() => new ScenarioParameterPatchBuilder().Build(bytes, [patch with { NewBytes = Encoding.ASCII.GetBytes("1\n其他文本") }]), "补丁接受了非数字内容。");
        ExpectFailure(() => new ScenarioParameterPatchBuilder().Build(bytes, [patch, patch]), "补丁未拒绝重叠写入。");
        ExpectFailure(() => new ScenarioParameterPatchBuilder().Build(bytes, [patch with { Offset = 0 }]), "补丁允许在72文本外写入。");
        var jump = structure.Commands.Single(c => c.Id == 0x76);
        SetJump(bytes, jump, command.Offset + 12);
        ExpectFailure(() => new ScenarioParameterPatchBuilder().Build(bytes, [patch]), "受变长影响且未解析的跳转目标必须拒绝写回。");

        var text = "1\n0\n15\n2\n" + new string('A', 65_500);
        var large = Document(Effect(5, text)); var largeStructure = EexStructure.Parse(large);
        var location = largeStructure.Commands.Single(c => c.Id == 0x72).Parameters.Single(p => p.Tag == 5).Offset;
        ExpectFailure(() => new ScenarioParameterPatchBuilder().Build(large,
            [new(location, [49], Encoding.ASCII.GetBytes(new string('1', 40)))]), "补丁超过UInt16块长仍被接受。");
    }

    private static void VerifyWorkspaceDraftAndSave(string basePath)
    {
        var modernBase = Path.Combine(TestSupport.RepositoryRootFromBase(basePath), "基底", "复刻版6.6山贼 国庆");
        var useModern = Directory.Exists(modernBase);
        var root = TestSupport.CreateCoreCopy(useModern ? modernBase : basePath, restoreKnown65Snapshot: false);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "RS"));
            var relative = Path.Combine("RS", "特技系统.eex"); var path = Path.Combine(root, relative);
            var bytes = Document(Concat(Effect(5, "124\r\n3\r\n915\r\n原备注。\r\n"), Effect(10, "1\n0,1,2\n不完整")));
            File.WriteAllBytes(path, bytes);
            TestSupport.Assert(useModern, "RS工作区变长写回测试需要已确认现代槽位的6.6山贼基底。");
            var workspace = new ProjectWorkspace(); workspace.Open(root, "ccz-6.6", "66-standard-v1");
            var service = ScenarioEffectAssignmentService.For(workspace); var entries = service.Scan();
            var entry = entries.Single(e => e.SubFunction == 5);
            TestSupport.Assert(entry.CharacterSlots == 4 && entry.LayoutVerified && entry.IsWritable, "测试引擎槽位证据未被确认。");
            ExpectFailure(() => service.ApplyUpdate(new(entries.Single(e => e.SubFunction == 10).Key, new Dictionary<string, string> { ["EffectId"] = "2" })),
                "域服务允许修改不完整的只读套装记录。");
            service.ApplyUpdate(new(entry.Key, new Dictionary<string, string> { ["Value"] = "9", ["TargetId"] = "*12345" }));
            TestSupport.Assert(service.HasPendingChanges && workspace.IsDirty && File.ReadAllBytes(path).SequenceEqual(bytes),
                "RS草稿没有参与脏状态，或在统一保存前已经修改原文件。");
            var readDraft = service.Read().Single(e => e.Key == entry.Key);
            TestSupport.Assert(readDraft.Parameters["Value"] == "9" && readDraft.Parameters["TargetId"] == "*12345", "扫描服务未返回当前草稿。");
            workspace.Commands.Undo();
            TestSupport.Assert(!service.HasPendingChanges && !workspace.IsDirty, "RS草稿未能一次撤销。");
            workspace.Commands.Redo();
            TestSupport.Assert(service.HasPendingChanges && service.Read().Single(e => e.Key == entry.Key).Parameters["Value"] == "9", "RS草稿重做失败。");
            var candidate = service.PrepareFileEdits().Single().CandidateBytes;
            TestSupport.Assert(candidate.AsSpan().IndexOf(EncodingService.GameEncoding.GetBytes("124\r\n3\r\n*12345\r\n9\r\n原备注。\r\n")) >= 0,
                "缺失Value插入时更改了原备注或原始换行。");
            var persons = workspace.GetDocument("persons"); var originalData = File.ReadAllBytes(Path.Combine(root, "Data.e5"));
            var oldValue = Convert.ToInt32(persons.Data.Rows[0]["武力"]);
            var names = workspace.GetDocument("effect-names"); var descriptions = workspace.GetDocument("effect-descriptions");
            TestSupport.Assert(names.Definition.IsWriteSupported, "双消费者已确认的个人/兵种特效名称没有开放写入。");
            var nameField = names.Definition.Fields.Single(field => field.Name == "名称");
            var descriptionField = descriptions.Definition.Fields.Single(field => field.Kind == DataManager.Core.Models.FieldKind.FixedString);
            var exePath = ProjectFileLocator.Resolve(root, "Ekd5.exe"); var originalExe = File.ReadAllBytes(exePath);
            var imsgPath = ProjectFileLocator.Resolve(root, "Imsg.e5"); var originalImsg = File.ReadAllBytes(imsgPath);
            var description = new string('文', 100);
            workspace.ApplyCompositeTransaction("人物、特效文本与剧本混合保存", [new("persons", 0, "武力", (oldValue + 1) % 256),
                new("effect-names", 124, nameField.Name, "测试特效"), new("effect-descriptions", 124, descriptionField.Name, description)]);
            TestSupport.Assert(new EffectNamePoolService().BuildLookup(workspace, EffectNameChannel.PersonalJob)[124] == "测试特效",
                "特效名称预览没有读取当前名称草稿。");
            ExpectFailure(() => workspace.ApplyCompositeTransaction("超长特效名称", [new("effect-names", 125, nameField.Name, new string('文', 8))]),
                "特效名称未保留16字节槽内的NUL。");
            EventHandler failAfterAccept = (_, _) =>
            {
                if (!service.HasPendingChanges) throw new InvalidOperationException("模拟接受原生草稿后的视图通知失败。");
            };
            service.Changed += failAfterAccept;
            var acceptanceFailure = workspace.Save();
            service.Changed -= failAfterAccept;
            TestSupport.Assert(!acceptanceFailure.Saved && service.HasPendingChanges && workspace.IsDirty &&
                service.Read().Single(e => e.Key == entry.Key).Parameters["Value"] == "9" &&
                Convert.ToString(names.Data.Rows[124]["名称"]) == "测试特效" && File.ReadAllBytes(path).SequenceEqual(bytes) &&
                File.ReadAllBytes(Path.Combine(root, "Data.e5")).SequenceEqual(originalData) &&
                File.ReadAllBytes(exePath).SequenceEqual(originalExe) && File.ReadAllBytes(imsgPath).SequenceEqual(originalImsg),
                "AcceptSaved之后失败没有恢复全部物理文件及人物/特效/RS草稿：" + string.Join("；", acceptanceFailure.Messages));
            var save = workspace.Save();
            TestSupport.Assert(save.Saved && !workspace.IsDirty && File.ReadAllBytes(path).SequenceEqual(candidate),
                "RS变长候选未进入同一统一保存事务：" + string.Join("；", save.Messages));
            var savedData = File.ReadAllBytes(Path.Combine(root, "Data.e5"));
            TestSupport.Assert(!savedData.SequenceEqual(originalData), "混合事务漏存了人物Data。");
            var savedExe = File.ReadAllBytes(exePath); var savedImsg = File.ReadAllBytes(imsgPath);
            var nameOffset = checked((int)names.Definition.DataOffset + 124 * names.Definition.RowSize);
            TestSupport.Assert(savedExe.AsSpan(nameOffset, nameField.Size).Contains((byte)0) &&
                savedExe.Where((value, index) => value != originalExe[index]).Count() <= nameField.Size,
                "特效名称保存越过了当前名称槽或丢失NUL。");
            TestSupport.Assert(savedImsg.AsSpan(checked((int)descriptions.Definition.DataOffset + 124 * descriptions.Definition.RowSize), 200)
                .SequenceEqual(EncodingService.GameEncoding.GetBytes(description)), "满200字节特效说明没有完整写回。");
            var undo = workspace.History.Undo(root, workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId, workspace.ActiveProfile.LayoutRevision);
            TestSupport.Assert(undo.Success && File.ReadAllBytes(path).SequenceEqual(bytes) && File.ReadAllBytes(Path.Combine(root, "Data.e5")).SequenceEqual(originalData),
                "统一历史不能同时撤销变长RS和固定长度Data：" + undo.Message);
            TestSupport.Assert(File.ReadAllBytes(exePath).SequenceEqual(originalExe) && File.ReadAllBytes(imsgPath).SequenceEqual(originalImsg),
                "统一历史漏撤了特效名称或说明。");
            var redo = workspace.History.Redo(root, workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId, workspace.ActiveProfile.LayoutRevision);
            TestSupport.Assert(redo.Success && File.ReadAllBytes(path).SequenceEqual(candidate) && File.ReadAllBytes(Path.Combine(root, "Data.e5")).SequenceEqual(savedData),
                "统一历史不能同时重做变长RS和固定长度Data。");
            TestSupport.Assert(File.ReadAllBytes(exePath).SequenceEqual(savedExe) && File.ReadAllBytes(imsgPath).SequenceEqual(savedImsg),
                "统一历史漏重做了特效名称或说明。");
            VerifyUiLifecycle(workspace, service);
            service.Scan(); entry = service.Read().Single(e => e.SubFunction == 5);
            service.ApplyUpdate(new(entry.Key, new Dictionary<string, string> { ["Value"] = "11" }));
            var external = File.ReadAllBytes(path); external[^1] ^= 1; File.WriteAllBytes(path, external);
            ExpectFailure(() => service.PrepareFileEdits(), "RS保存未检查外部文件冲突。");
            ExpectFailure(() => service.Scan(), "重新扫描覆盖了外部冲突前的RS草稿基线。");
            TestSupport.Assert(service.HasPendingChanges, "外部冲突后原始RS草稿丢失。");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifyUiLifecycle(ProjectWorkspace workspace, ScenarioEffectAssignmentService service)
    {
        var previousContext = SynchronizationContext.Current;
        var autoInstall = WindowsFormsSynchronizationContext.AutoInstall;
        WindowsFormsSynchronizationContext.AutoInstall = false;
        SynchronizationContext.SetSynchronizationContext(null);
        try { VerifyUiWithoutSynchronizationContext(workspace, service); }
        finally
        {
            WindowsFormsSynchronizationContext.AutoInstall = autoInstall;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void VerifyUiWithoutSynchronizationContext(ProjectWorkspace workspace, ScenarioEffectAssignmentService service)
    {
        service.Discard();
        using var form = new Form { Width = 1280, Height = 900, ShowInTaskbar = false, Opacity = 0 };
        using var panel = new BusinessEditorPanel(workspace);
        var ownerThread = Environment.CurrentManagedThreadId;
        var wrongThreads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Track(Control control)
        {
            control.HandleCreated += (_, _) => { if (Environment.CurrentManagedThreadId != ownerThread) wrongThreads.Enqueue(control.GetType().Name); };
            control.ControlAdded += (_, args) => { if (args.Control != null) Track(args.Control); };
            if (control is DataGridView table)
                table.RowsAdded += (_, _) => { if (Environment.CurrentManagedThreadId != ownerThread) wrongThreads.Enqueue("DataGridView.RowsAdded"); };
            foreach (Control child in control.Controls) Track(child);
        }
        Track(panel);
        form.Controls.Add(panel); panel.Bind(BusinessPageKind.Effects, 124); form.Show();
        WaitFor(() => service.HasScanResult, "首次进入特效模块未在隐藏的剧本tab后台建立索引。");
        TestSupport.Assert(panel.DetailTabs.SelectedIndex == 0, "首次扫描不应强制切换当前特效子页。");
        var scanButton = Descendants(panel).OfType<Button>().Single(button => button.Text == "扫描剧本");
        SynchronizationContext.SetSynchronizationContext(null);
        scanButton.PerformClick();
        TestSupport.Assert(SynchronizationContext.Current == null, "无同步上下文启动扫描的回归条件未建立。");
        WaitFor(() => scanButton.Enabled, "没有同步上下文时扫描完成没有回到UI线程。");
        var indexed = service.Read(false).First();
        panel.Bind(BusinessPageKind.Effects, 125);
        Application.DoEvents();
        TestSupport.Assert(ReferenceEquals(indexed, service.Read(false).First()), "切换特效重复扫描了既有剧本索引。");
        panel.Bind(BusinessPageKind.Effects, 124);
        panel.DetailTabs.SelectedIndex = 2;
        var page = panel.DetailTabs.SelectedTab!;
        var grid = Descendants(page).OfType<DataGridView>().Single(g => g.Columns.Contains("Binding"));
        WaitFor(() => grid.Rows.Count == 1, "剧本子页没有显示缓存中的当前特效分配。");
        var selected = service.Read(false).Single(e => e.SubFunction == 5);
        service.ApplyUpdate(new(selected.Key, new Dictionary<string, string> { ["Value"] = "10" }));
        WaitFor(() => Convert.ToString(grid.Rows[0].Cells["Binding"].Value)?.Contains("值 10", StringComparison.Ordinal) == true,
            "外部草稿/工作簿修改后剧本页没有响应服务刷新。");
        TestSupport.Assert(workspace.Save().Saved, "UI生命周期测试的RS保存失败。");
        WaitFor(() => service.HasScanResult && grid.Rows.Count == 1 &&
            Convert.ToString(grid.Rows[0].Cells["Binding"].Value)?.Contains("值 10", StringComparison.Ordinal) == true,
            "保存使索引失效后，当前特效模块没有重新扫描。");
        TestSupport.Assert(workspace.UndoSaved().Success, "UI生命周期测试不能历史撤销。");
        panel.Bind(BusinessPageKind.Effects, 124);
        WaitFor(() => service.HasScanResult && service.Read(false).Single(e => e.SubFunction == 5).Parameters["Value"] == "9",
            "历史撤销后剧本索引没有读取已恢复参数。");
        TestSupport.Assert(workspace.RedoSaved().Success, "UI生命周期测试不能历史重做。");
        panel.Bind(BusinessPageKind.Effects, 124);
        WaitFor(() => service.HasScanResult && service.Read(false).Single(e => e.SubFunction == 5).Parameters["Value"] == "10",
            "历史重做后剧本索引没有读取已恢复参数。");
        TestSupport.Assert(wrongThreads.IsEmpty, "后台线程创建了UI句柄或更新了结果表：" + string.Join(",", wrongThreads));
        form.Close();
        Application.DoEvents();

        // Exercise queued scan completions while old pages and their parent
        // handles are destroyed or rebuilt, without a WinForms async context.
        for (var iteration = 0; iteration < 6; iteration++)
        {
            service.Discard();
            using var closing = new Form { Width = 800, Height = 600, ShowInTaskbar = false, Opacity = 0 };
            using var oldPanel = new BusinessEditorPanel(workspace);
            Track(oldPanel);
            closing.Controls.Add(oldPanel); oldPanel.Bind(BusinessPageKind.Effects, 124);
            closing.Show(); Application.DoEvents();
            oldPanel.DiscardPendingChanges();
            oldPanel.Bind(BusinessPageKind.Effects, 125);
            service.Discard(); // Queue a native refresh immediately before disposal.
            closing.Close(); Application.DoEvents();
        }
        WaitFor(() => !service.HasPendingChanges, "销毁特效页意外生成了RS草稿。");
        TestSupport.Assert(wrongThreads.IsEmpty, "销毁或重建页面期间后台创建了UI句柄：" + string.Join(",", wrongThreads));
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void WaitFor(Func<bool> predicate, string message)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(8))
        {
            Application.DoEvents();
            if (predicate()) return;
            Thread.Sleep(10);
        }
        throw new InvalidOperationException(message);
    }

    private static void VerifyRealScenarios(string basePath)
    {
        var repository = TestSupport.RepositoryRootFromBase(basePath);
        foreach (var (name, slots) in new[] { ("加强版6.5未加密版", 3), ("复刻版6.6山贼 国庆", 4), ("曹操传6.6-V10.1 【解压密码：66】", 4) })
        {
            var rs = Path.Combine(repository, "基底", name, "RS");
            if (!Directory.Exists(rs)) continue;
            var total = 0; var files = 0; var failed = 0; var changed = false;
            foreach (var path in Directory.EnumerateFiles(rs, "*.eex"))
            {
                var bytes = File.ReadAllBytes(path);
                IReadOnlyList<ScenarioEffectAssignment> entries;
                try { entries = ScenarioEffectAssignmentService.ParseAssignments(Path.GetRelativePath(repository, path), bytes, slots); }
                catch (InvalidDataException ex) { Console.WriteLine($"RS_REAL_DIAGNOSTIC {name}/{Path.GetFileName(path)} {ex.Message}"); failed++; continue; }
                total += entries.Count; files++;
                if (!changed)
                {
                    var entry = entries.FirstOrDefault(e => e.IsWritable && e.Parameters.ContainsKey("EffectId"));
                    if (entry != null)
                    {
                        var command = EexStructure.Parse(bytes).Commands.Single(c => c.Ordinal == entry.CommandOrdinal);
                        var text = command.Parameters.Single(p => p.Tag == 5);
                        var value = entry.Parameters["EffectId"];
                        var token = IndexOf(bytes, Encoding.ASCII.GetBytes(value), text.Offset);
                        var after = new ScenarioParameterPatchBuilder().Build(bytes, [new(token, Encoding.ASCII.GetBytes(value), Encoding.ASCII.GetBytes("*12345"))]);
                        var reread = ScenarioEffectAssignmentService.ParseAssignments("RS/复读.eex", after, slots).Single(e => e.CommandOrdinal == entry.CommandOrdinal);
                        TestSupport.Assert(reread.Parameters["EffectId"] == "*12345" && reread.Remarks == entry.Remarks,
                            "真实剧本变长补丁未复读一致：" + path);
                        changed = true;
                    }
                }
            }
            Console.WriteLine($"RS_REAL_SCAN {name} files={files} failed={failed} assignments={total} variable_patch={changed}");
            TestSupport.Assert(files > 0, "真实RS测试未读取任何剧本。");
        }
    }

    private static void AssertOnlyAuthorizedBytesChanged(byte[] before, byte[] after, EexStructure structure, ScenarioTokenReplacement[] patches)
    {
        var ignored = new HashSet<int>();
        void Ignore(int start, int count) { foreach (var offset in Enumerable.Range(start, count)) ignored.Add(offset); }
        Ignore(10, structure.SceneOffsets.Count * 4);
        foreach (var length in structure.Lengths) Ignore(length.Offset, 2);
        foreach (var command in structure.Commands.Where(c => c.Id == 0x76)) Ignore(command.Parameters.Single(p => p.Tag == 4).Offset, 4);
        foreach (var patch in patches) Ignore(patch.Offset, patch.ExpectedBytes.Length);
        for (var index = 0; index < before.Length; index++)
        {
            if (ignored.Contains(index)) continue;
            var shift = patches.Where(p => p.Offset + p.ExpectedBytes.Length <= index).Sum(p => p.NewBytes.Length - p.ExpectedBytes.Length);
            TestSupport.Assert(before[index] == after[index + shift], $"0x{index:X}的非参数原始字节发生改变。");
        }
    }

    private static byte[] Document(params byte[][] bodies)
    {
        var scenes = bodies.Select(body => Concat(U16(1), Block(Concat(U16(0), Block(body))))).ToArray();
        var bytes = new byte[10 + 4 * scenes.Length + scenes.Sum(scene => scene.Length) + 6];
        new byte[] { 69, 69, 88, 0, 1, 2, 3, 4, 5, 6 }.CopyTo(bytes, 0);
        var cursor = 10 + 4 * scenes.Length;
        for (var index = 0; index < scenes.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10 + index * 4), cursor);
            scenes[index].CopyTo(bytes, cursor); cursor += scenes[index].Length;
        }
        new byte[] { 0xFF, 0x81, 0x31, 0xD0, 0xAB, 0xCD }.CopyTo(bytes, cursor);
        return bytes;
    }
    private static byte[] Effect(int subFunction, string text) => Concat(U16(0x72), U16(4), I32(subFunction), U16(5), EncodingService.GameEncoding.GetBytes(text), [0]);
    private static byte[] Jump() => Concat(U16(0x76), U16(4), I32(0));
    private static byte[] Block(byte[] body) => Concat(U16(body.Length), body);
    private static byte[] U16(int value) { var bytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value)); return bytes; }
    private static byte[] I32(int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return bytes; }
    private static byte[] Concat(params byte[][] arrays) => arrays.SelectMany(a => a).ToArray();
    private static int IndexOf(byte[] source, byte[] token, int start)
    {
        var found = source.AsSpan(start).IndexOf(token);
        return found < 0 ? throw new InvalidDataException("测试原始数字不存在。") : start + found;
    }
    private static void SetJump(byte[] bytes, EexCommand command, int target)
        => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(command.Parameters.Single(p => p.Tag == 4).Offset), target - command.Offset - 8);
    private static void ExpectFailure(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or OverflowException) { return; }
        throw new InvalidOperationException(message);
    }
}
