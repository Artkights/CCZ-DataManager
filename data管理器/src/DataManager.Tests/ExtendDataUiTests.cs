using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using AppIdNameOption = DataManager.App.IdNameOption;

namespace DataManager.Tests;

internal static class ExtendDataUiTests
{
    public static void Run(string basePath, string? screenshotDirectory = null)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        ResourcePreviewCardTests.Run();
        var copy = TestSupport.CreateCoreCopy(basePath, restoreKnown65Snapshot: false);
        using var form = new MainForm(openDefaultProject: false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, MaximumSize = new Size(4096, 2160), ClientSize = new Size(1280, 720)
        };
        try
        {
            PrepareExtensionFixture(basePath, copy);
            Console.WriteLine("EXTEND_UI_STAGE fixture " + copy);
            form.Show(); Console.WriteLine("EXTEND_UI_STAGE shown");
            form.LoadProjectForSmokeTest(copy); Console.WriteLine("EXTEND_UI_STAGE loaded");
            form.SelectModuleForSmokeTest("拓展数据"); Console.WriteLine("EXTEND_UI_STAGE selected");
            Pump();
            Console.WriteLine("EXTEND_UI_STAGE opened");
            var editor = form.BusinessEditorForSmokeTest.ExtendEditor ?? throw new InvalidOperationException("未创建拓展编辑器");
            var workspace = form.WorkspaceForSmokeTest;
            var service = new ExtendDataService(workspace);
            TestSupport.Assert(editor.GetEntityOptions().Count == 1024 && form.EntityListForSmokeTest.Items.Count == 1024, "人物特效列表容量错误");
            TestSupport.Assert(editor.DetailTabs.TabPages.Cast<TabPage>().Select(page => page.Text).SequenceEqual(["人物特效", "特效成长", "策略学习", "锻造配方"]), "拓展分类文案/顺序不完整");
            TestSupport.Assert(!workspace.IsDirty && !editor.HasPendingChanges, "仅绑定拓展数据产生了草稿");
            TestSupport.Assert(editor.DetailTabs.TabPages.Cast<TabPage>().Skip(1).All(page => page.Controls.Count == 0), "未访问的拓展分类提前创建了控件");

            var original = service.GetRaw(ExtendDataCategory.Effects, 0);
            var value = Find<TextBox>(editor, "ExtendEffectValue0");
            var effectsScroll = Find<Panel>(editor, "ExtendDataScroll");
            var fixedExperience = Find<TextBox>(editor, "ExtendEffectExperience0");
            TestSupport.Assert(Find<TextBox>(editor, "ExtendRawEvidence").TextLength == 0, "折叠的字节诊断仍被提前格式化");
            value.Text = "999";
            Console.WriteLine("EXTEND_UI_STAGE invalid-start");
            TestSupport.Assert(editor.HasPendingChanges && !editor.Flush(), "无效特效参数未阻止保存");
            Console.WriteLine("EXTEND_UI_STAGE invalid-flush");
            TestSupport.Assert(!editor.BindEntity(1) && editor.CurrentId == 0 && value.Text == "999", "无效参数在切人物时丢失");
            Console.WriteLine("EXTEND_UI_STAGE invalid-bind");
            Find<ComboBox>(editor, "ExtendPage").SelectedIndex = 1; Pump();
            Console.WriteLine("EXTEND_UI_STAGE invalid-page");
            TestSupport.Assert(Find<ComboBox>(editor, "ExtendPage").SelectedIndex == 0 && value.Text == "999", "无效参数未阻止切页");
            editor.DetailTabs.SelectedIndex = 1; Pump();
            TestSupport.Assert(editor.Category == ExtendDataCategory.Effects && value.Text == "999", "无效参数未阻止切分类");
            TestSupport.Assert(service.GetRaw(ExtendDataCategory.Effects, 0).AsSpan().SequenceEqual(original), "无效参数改变了数据");
            Console.WriteLine("EXTEND_UI_STAGE invalid-complete");

            value.Text = "8";
            Console.WriteLine("EXTEND_UI_STAGE legal-text");
            TestSupport.Assert(editor.Flush(), "合法参数不能提交到草稿");
            Console.WriteLine("EXTEND_UI_STAGE legal-flush");
            var changed = service.GetRaw(ExtendDataCategory.Effects, 0);
            TestSupport.Assert(changed[1] == 8 && changed[2] == 9 && changed[3] == 13 && changed[5] == 222 && changed[6] == 111 && changed[7] == 0,
                "效果值编辑清洗了经验/回合或未修改的空槽");
            TestSupport.Assert(changed.Where((value, index) => value != original[index]).Count() == 1, "单参数编辑改变了额外字节");
            TestSupport.Assert(workspace.Commands.Undo() != null, "特效编辑未注册撤销");
            Console.WriteLine("EXTEND_UI_STAGE legal-undo");
            editor.BindEntity(0);
            TestSupport.Assert(service.GetRaw(ExtendDataCategory.Effects, 0).AsSpan().SequenceEqual(original), "特效撤销不能恢复全部原字节");
            Console.WriteLine("EXTEND_UI_STAGE effect-undo");

            var effectCombo = Find<SearchableIdComboBox>(editor, "ExtendEffectId0");
            TestSupport.Assert(effectCombo.Options.Count == 256 && effectCombo.Options.Single(option => option.Id == 255).Name == "无", "特效下拉缺少完整 ID/无选项");
            SelectOption(effectCombo, 27);
            TestSupport.Assert(editor.Flush() && service.ReadInt(ExtendDataCategory.Effects, 0, "槽0特效号") == 27, "下拉未按 ID 提交");
            TestSupport.Assert(service.GetRaw(ExtendDataCategory.Effects, 0)[2] == 9 && service.GetRaw(ExtendDataCategory.Effects, 0)[3] == 13, "切特效错误重置既有经验/回合");
            Find<CheckBox>(editor, "ExtendEffectAdvanced").Checked = true; Pump();
            TestSupport.Assert(ReferenceEquals(value, Find<TextBox>(editor, "ExtendEffectValue0")) && ReferenceEquals(fixedExperience, Find<TextBox>(editor, "ExtendEffectExperience0")),
                "高级显示开关重建了已有字段控件");
            TestSupport.Assert(Find<TextBox>(editor, "ExtendEffectExperience0").Text == "9", "高级字段没有显示真实经验");
            Find<ComboBox>(editor, "ExtendPage").SelectedIndex = 25; Pump();
            TestSupport.Assert(Descendants(editor.DetailTabs.SelectedTab!).OfType<SearchableIdComboBox>().Count(combo => combo.Visible && combo.Name.StartsWith("ExtendEffectId", StringComparison.Ordinal)) == 6,
                "第26页未限制为最后6个真实槽");
            TestSupport.Assert(ReferenceEquals(value, Find<TextBox>(editor, "ExtendEffectValue0")), "槽位分页重建了固定字段控件");
            TestSupport.Assert(!editor.HasPendingChanges, "切页意外修改原始值");
            Snapshot(form, screenshotDirectory, "effects-last-page");
            Find<ComboBox>(editor, "ExtendPage").SelectedIndex = 0; Pump();
            Find<Button>(editor, "ExtendCompact").PerformClick(); Pump();
            TestSupport.Assert(service.GetEffects(0)[1].EffectId == 26 && service.GetEffects(0)[1].Value == 44 && service.GetEffects(0)[1].Experience == 5 && service.GetEffects(0)[1].RemainingTurns == 3,
                "整理槽位没有整条移动重复特效");
            workspace.Commands.Undo(); editor.BindEntity(0);
            TestSupport.Assert(service.GetEffects(0)[2].EffectId == 26, "整理槽位无法一次撤回");
            Find<Button>(editor, "ExtendEffectClear0").PerformClick(); Pump();
            TestSupport.Assert(service.GetRaw(ExtendDataCategory.Effects, 0).AsSpan(0, 4).SequenceEqual(new byte[] { 255, 0, 0, 255 }), "主动清空没有写入标准4字节空槽");
            workspace.Commands.Undo(); editor.BindEntity(0);

            editor.SearchText = "12";
            effectsScroll.AutoScrollPosition = new Point(0, 40); Pump();
            var rememberedEffectsScroll = effectsScroll.AutoScrollPosition;
            editor.DetailTabs.SelectedIndex = 1; Pump();
            TestSupport.Assert(editor.Category == ExtendDataCategory.Growth && form.EntityListForSmokeTest.Items.Count == 256, "成长分类仍显示人物列表");
            var enabled = Find<CheckBox>(editor, "ExtendGrowthEnabled");
            TestSupport.Assert(enabled.Checked && enabled.Text.Contains("2", StringComparison.Ordinal), "非标准非零成长标记没有按启用显示并保留原值");
            TestSupport.Assert(editor.Flush() && service.ReadInt(ExtendDataCategory.Growth, 0, "Enabled") == 2, "绑定成长复选框清洗了非1标记");
            Find<TextBox>(editor, "ExtendGrowth4").Text = "33";
            TestSupport.Assert(editor.Flush() && service.ReadInt(ExtendDataCategory.Growth, 0, "Param4") == 33 && service.ReadInt(ExtendDataCategory.Growth, 0, "Enabled") == 2,
                "成长参数编辑错误规范化启用标记");
            editor.BindEntity(255);
            TestSupport.Assert(!Find<CheckBox>(editor, "ExtendGrowthEnabled").Enabled && !Find<TextBox>(editor, "ExtendGrowth1").Enabled, "成长255保留记录允许编辑");
            editor.BindEntity(0);
            TestSupport.Assert(Find<CheckBox>(editor, "ExtendGrowthEnabled").Enabled && Find<TextBox>(editor, "ExtendGrowth1").Enabled, "从保留记录返回后控件没有恢复可编辑");

            Console.WriteLine("EXTEND_UI_STAGE effect-complete");
            Snapshot(form, screenshotDirectory, "growth");
            Console.WriteLine("EXTEND_UI_STAGE growth-complete");

            editor.DetailTabs.SelectedIndex = 2; Pump();
            TestSupport.Assert(form.EntityListForSmokeTest.Items.Count == 1024 && Find<ComboBox>(editor, "ExtendPage").Items.Count == 4, "策略分页/主体容量错误");
            Find<ComboBox>(editor, "ExtendPage").SelectedIndex = 3; Pump();
            SelectOption(Find<SearchableIdComboBox>(editor, "ExtendStrategyId7"), 5);
            TestSupport.Assert(editor.Flush() && service.ReadInt(ExtendDataCategory.Strategies, 0, "策略31ID") == 5 && service.ReadInt(ExtendDataCategory.Strategies, 0, "策略31次数") == 255,
                "策略末槽新增未使用默认无限次数");
            Find<TextBox>(editor, "ExtendStrategyCount7").Text = "17";
            TestSupport.Assert(editor.Flush(), "策略次数无法编辑");
            editor.BindEntity(1); editor.BindEntity(0);
            TestSupport.Assert(Find<ComboBox>(editor, "ExtendPage").SelectedIndex == 3 && Find<TextBox>(editor, "ExtendStrategyCount7").Text == "17", "策略换人后丢失页码或草稿");
            Snapshot(form, screenshotDirectory, "strategies");
            Console.WriteLine("EXTEND_UI_STAGE strategy-complete");

            editor.DetailTabs.SelectedIndex = 3; Pump();
            TestSupport.Assert(form.EntityListForSmokeTest.Items.Count == 256, "真实16KiB锻造没有显示256配方");
            var forgeOriginal = service.GetRaw(ExtendDataCategory.Forge, 0);
            TestSupport.Assert(Find<TextBox>(editor, "ExtendForgePrice").Text == uint.MaxValue.ToString(CultureInfo.InvariantCulture), "UInt32锻造价格溢出/截断");
            TestSupport.Assert(Find<CheckBox>(editor, "ExtendForgeExact0").Checked && Find<TextBox>(editor, "ExtendForgeAmount0").Text == "3", "装备精确等级没有分离高位");
            TestSupport.Assert(!Find<CheckBox>(editor, "ExtendForgeExact1").Enabled && Find<TextBox>(editor, "ExtendForgeAmount1").Text == "2", "消耗品材料数量语义错误");
            TestSupport.Assert(Find<TextBox>(editor, "ExtendForgeAmount2").Text == "200", "产物数量被截成低7位");
            TestSupport.Assert(editor.Flush() && service.GetRaw(ExtendDataCategory.Forge, 0).AsSpan().SequenceEqual(forgeOriginal), "打开锻造清洗了未知/异常字段");
            var forgeName = Find<ByteLimitedTextBox>(editor, "ExtendForgeName");
            TestSupport.Assert(forgeName.Text == "测试配方", "锻造名称控件未绑定真实文本");
            TestSupport.Assert(forgeName.InputGuard.Validation?.Capacity == 29, "锻造名称未提供GBK字节上限");
            forgeName.SelectAll(); TestSupport.Assert(!forgeName.InputGuard.TryReplaceSelection(new string('汉', 15)), "锻造超29字节名称未拒绝整次输入");
            TestSupport.Assert(!editor.HasPendingChanges, "拒绝名称输入后留下不完整草稿");
            Find<TextBox>(editor, "ExtendForgePrice").Text = "4294967294";
            TestSupport.Assert(editor.Flush(), "UInt32价格编辑失败");
            var forgeChanged = service.GetRaw(ExtendDataCategory.Forge, 0);
            TestSupport.Assert(forgeChanged[60] == 2 && forgeChanged[35] == 130 && forgeChanged[38] == 200 && forgeChanged[62] == 0xAA && forgeChanged[63] == 0xBB,
                "锻造无关编辑改变启用/异常高位/产物或尾部保留字节");
            TestSupport.Assert(forgeChanged.Where((value, index) => value != forgeOriginal[index]).Count() == 1, "锻造价格编辑改变额外字节");
            SelectOption(Find<SearchableIdComboBox>(editor, "ExtendForgeItem0"), 256);
            TestSupport.Assert(editor.Flush() && service.GetForgeSlots(0)[0].ItemId == 256 && service.GetForgeSlots(0)[0].RawAmount == 3,
                "装备材料改为消耗品时遗留了仅装备允许的精确等级标记");
            workspace.Commands.Undo(); editor.BindEntity(0);
            Snapshot(form, screenshotDirectory, "forge");
            Console.WriteLine("EXTEND_UI_STAGE forge-complete");

            var lookup = (SearchableIdComboBox)typeof(MainForm).GetField("_quickLookup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            lookup.Text = "12";
            typeof(ComboBox).GetMethod("OnTextUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lookup, [EventArgs.Empty]);
            Pump();
            TestSupport.Assert(editor.SearchText == "12" && form.EntityListForSmokeTest.Items.Count < 256, "快捷搜索没有过滤锻造配方列表");
            editor.DetailTabs.SelectedIndex = 0; Pump();
            TestSupport.Assert(ReferenceEquals(value, Find<TextBox>(editor, "ExtendEffectValue0")) && ReferenceEquals(effectsScroll, Find<Panel>(editor, "ExtendDataScroll")),
                "切换分类销毁了已访问分类的字段或滚动容器");
            TestSupport.Assert(effectsScroll.AutoScrollPosition == rememberedEffectsScroll, "切换分类丢失了原滚动位置");
            TestSupport.Assert(editor.SearchText == "12" && lookup.Text == "12" && form.EntityListForSmokeTest.Items.Count < 1024, "分类独立搜索状态未恢复");
            editor.SearchText = string.Empty;
            editor.BindEntity(17); Find<ComboBox>(editor, "ExtendPage").SelectedIndex = 12; Pump();
            form.SelectModuleForSmokeTest("人物"); form.SelectModuleForSmokeTest("拓展数据"); Pump();
            TestSupport.Assert(editor.CurrentId == 17 && Find<ComboBox>(editor, "ExtendPage").SelectedIndex == 12, "跨模块重进丢失当前人物/页码");
            CheckLayout(editor, form, screenshotDirectory);
            CheckReload(editor, workspace, service);

            var sourceHash = HashService.Sha256(File.ReadAllBytes(Path.Combine(copy, "Effect.e5")));
            TestSupport.Assert(sourceHash == HashService.Sha256(service.GetDocument(ExtendDataCategory.Effects).OriginalFileBytes), "UI草稿操作意外写入文件");
            Console.WriteLine("EXTEND_DATA_UI_TESTS_OK");
        }
        finally
        {
            Console.WriteLine("EXTEND_UI_STAGE cleanup");
            var workspace = form.WorkspaceForSmokeTest;
            while (workspace.Commands.CanUndo) workspace.Commands.Undo();
            form.BusinessEditorForSmokeTest.DiscardPendingChanges();
            form.Close();
            Directory.Delete(copy, true);
        }
    }

    private static void CheckReload(ExtendDataEditorPanel editor, ProjectWorkspace workspace, ExtendDataService service)
    {
        editor.DetailTabs.SelectedIndex = 0; editor.BindEntity(0); Pump();
        Find<ComboBox>(editor, "ExtendPage").SelectedIndex = 0; Pump();
        var value = Find<TextBox>(editor, "ExtendEffectValue0");
        value.Text = "199";
        editor.RefreshFromWorkspace();
        TestSupport.Assert(value.Text == service.ReadInt(ExtendDataCategory.Effects, 0, "槽0特效值").ToString(CultureInfo.InvariantCulture) && !editor.HasPendingChanges,
            "工作区刷新提交或保留了过期输入");
        var evidence = Find<TextBox>(editor, "ExtendRawEvidence");
        Find<CheckBox>(editor, "ExtendEvidenceToggle").Checked = true;
        TestSupport.Assert(evidence.Text.Contains("原始：", StringComparison.Ordinal), "展开字节诊断没有延迟生成内容");
        Find<CheckBox>(editor, "ExtendEvidenceToggle").Checked = false;
        workspace.ReloadLoadedDocuments();
        editor.RefreshFromWorkspace();
        TestSupport.Assert(!ReferenceEquals(value, Find<TextBox>(editor, "ExtendEffectValue0")) && value.IsDisposed && !editor.HasPendingChanges,
            "重新载入文件后继续持有旧表字段绑定");
        editor.DetailTabs.SelectedIndex = 1; Pump();
        TestSupport.Assert(Find<TextBox>(editor, "ExtendGrowth1").Text == service.ReadInt(ExtendDataCategory.Growth, editor.CurrentId, "Param1").ToString(CultureInfo.InvariantCulture),
            "重新载入后隐藏分类显示了旧字段值");
        editor.ResetProject();
        TestSupport.Assert(editor.CurrentId == 0 && editor.DetailTabs.SelectedIndex == 0 && editor.DetailTabs.TabPages.Cast<TabPage>().Skip(1).All(page => page.Controls.Count == 0),
            "切换项目没有清除其他分类的旧控件和状态");
    }

    private static void PrepareExtensionFixture(string source, string target)
    {
        foreach (var name in new[] { "Effect.e5", "Effectmsg.e5", "MeffStudy.e5", "Forge.e5" })
        {
            var path = ProjectFileLocator.Resolve(source, name);
            if (File.Exists(path)) File.Copy(path, Path.Combine(target, name));
        }
        var effectPath = Path.Combine(target, "Effect.e5");
        var effects = File.Exists(effectPath) ? File.ReadAllBytes(effectPath) : new byte[1024 * 1024];
        for (var slot = 0; slot < 256; slot++) new byte[] { 255, 0, 0, 255 }.CopyTo(effects, slot * 4);
        new byte[] { 26, 7, 9, 13, 255, 222, 111, 0, 26, 44, 5, 3 }.CopyTo(effects, 0);
        File.WriteAllBytes(effectPath, effects);
        var growthPath = Path.Combine(target, "Effectmsg.e5");
        var growth = File.Exists(growthPath) ? File.ReadAllBytes(growthPath) : new byte[2048];
        growth[0] = 2; File.WriteAllBytes(growthPath, growth);
        var strategyPath = Path.Combine(target, "MeffStudy.e5");
        var strategies = File.Exists(strategyPath) ? File.ReadAllBytes(strategyPath) : new byte[65536];
        for (var slot = 0; slot < 32; slot++) new byte[] { 255, 0 }.CopyTo(strategies, slot * 2);
        File.WriteAllBytes(strategyPath, strategies);
        var forgePath = Path.Combine(target, "Forge.e5");
        var forge = File.Exists(forgePath) ? File.ReadAllBytes(forgePath) : new byte[16384];
        forge.AsSpan(0, 64).Clear();
        EncodingService.GameEncoding.GetBytes("测试配方").CopyTo(forge, 0);
        for (var slot = 0; slot < 8; slot++) BinaryPrimitives.WriteUInt16LittleEndian(forge.AsSpan(30 + slot * 3, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(forge.AsSpan(30, 2), 0); forge[32] = 131;
        BinaryPrimitives.WriteUInt16LittleEndian(forge.AsSpan(33, 2), 256); forge[35] = 130;
        BinaryPrimitives.WriteUInt16LittleEndian(forge.AsSpan(36, 2), 1); forge[38] = 200;
        BinaryPrimitives.WriteUInt32LittleEndian(forge.AsSpan(54, 4), uint.MaxValue);
        forge[58] = 2; forge[60] = 2; forge[62] = 0xAA; forge[63] = 0xBB;
        File.WriteAllBytes(forgePath, forge);
        var rootItems = new byte[256 * 25];
        EncodingService.GameEncoding.GetBytes("测试消耗品").CopyTo(rootItems, 0);
        File.WriteAllBytes(Path.Combine(target, "Item.e5"), rootItems);
    }

    private static void CheckLayout(ExtendDataEditorPanel editor, MainForm form, string? screenshots)
    {
        foreach (var (size, scale) in new[] { (new Size(1280, 720), 1f), (new Size(1920, 1080), 1f), (new Size(1280, 720), 1.25f), (new Size(1280, 720), 1.5f) })
        {
            var defaultFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            form.Font = new Font(defaultFont.FontFamily, defaultFont.Size * scale);
            form.ClientSize = size; form.PerformLayout(); Pump();
            TestSupport.Assert(form.ClientSize == size, $"请求窗口 {size}，实际 {form.ClientSize}，不能作为此分辨率验收");
            foreach (var category in new[] { 0, 1, 2, 3 })
            {
                editor.DetailTabs.SelectedIndex = category; Pump();
                var scroll = Find<Panel>(editor, "ExtendDataScroll");
                TestSupport.Assert(scroll.AutoScroll && scroll.ClientSize.Width > 100 && scroll.ClientSize.Height > 100, "小窗口/大字体未保留可滚动编辑区域");
                var body = Find<TableLayoutPanel>(editor, "ExtendDataBody");
                foreach (var textEditor in Descendants(editor.DetailTabs.SelectedTab!).OfType<ByteTextEditor>())
                {
                    var preferred = textEditor.GetPreferredSize(new Size(textEditor.Width, 0));
                    TestSupport.Assert(textEditor.Height <= preferred.Height + 2,
                        $"字节说明被自动布局推到输入框远处：{textEditor.Size}/{preferred}");
                    var hint = Descendants(textEditor).OfType<Label>().Single(label => label.Text == ByteTextEditor.EncodingHint);
                    TestSupport.Assert(hint.Top >= textEditor.Editor.Bottom && hint.Bottom <= textEditor.ClientSize.Height,
                        "字节换算说明遮住输入框或超出编辑区域");
                }
                TestSupport.Assert(body.Width <= scroll.ClientSize.Width, $"编辑内容超出可见宽度：{body.Width}/{scroll.ClientSize.Width}");
                foreach (var grid in body.Controls.OfType<TableLayoutPanel>())
                    TestSupport.Assert(grid.Right <= body.ClientSize.Width, $"编辑表格超出主体右侧：{grid.Right}/{body.ClientSize.Width}");
                foreach (var input in Descendants(editor).Where(control => control.Visible && control.Enabled && control is TextBox or ComboBox))
                {
                    TestSupport.Assert(input.Width >= 25 && input.Height >= 18, $"{scale}倍字体输入框塌缩：{input.Name} {input.Size}");
                    if (input.Parent is TableLayoutPanel grid)
                        TestSupport.Assert(input.Right <= grid.ClientSize.Width, $"输入框超出表格右侧：{input.Name} {input.Right}/{grid.ClientSize.Width}");
                }
                Snapshot(form, screenshots, $"layout-{category}-{size.Width}x{size.Height}-{scale.ToString("0.00", CultureInfo.InvariantCulture)}");
            }
        }
    }

    private static void Snapshot(Form form, string? directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        foreach (var box in Descendants(form).OfType<TextBoxBase>()) { box.SelectionStart = box.TextLength; box.SelectionLength = 0; }
        foreach (var combo in Descendants(form).OfType<ComboBox>()) { combo.DroppedDown = false; combo.SelectionStart = combo.Text.Length; combo.SelectionLength = 0; }
        form.Select();
        Pump();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }
    private static T Find<T>(Control control, string name) where T : Control
        => Descendants(control is ExtendDataEditorPanel editor ? editor.DetailTabs.SelectedTab! : control).OfType<T>().Single(item => item.Name == name);
    private static void SelectOption(SearchableIdComboBox combo, int id)
    {
        combo.SelectedItem = combo.Items.Cast<AppIdNameOption>().Single(option => option.Id == id);
        typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(combo, [EventArgs.Empty]);
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }
    private static void Pump() { Application.DoEvents(); Application.DoEvents(); }
}
