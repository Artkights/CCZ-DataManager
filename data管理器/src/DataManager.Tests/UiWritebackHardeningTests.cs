using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Reflection;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class UiWritebackHardeningTests
{
    public static void Run(string basePath)
    {
        var copy = TestSupport.CreateCoreCopy(basePath);
        using var form = new MainForm(openDefaultProject: false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false
        };
        var fontScale = float.TryParse(Environment.GetEnvironmentVariable("DATAMANAGER_UI_FONT_SCALE"), CultureInfo.InvariantCulture, out var requestedScale) ? requestedScale : 1f;
        if (fontScale != 1f) form.Font = new Font(form.Font.FontFamily, form.Font.Size * fontScale, form.Font.Style);
        try
        {
            form.Show(); form.LoadProjectForSmokeTest(copy); Application.DoEvents();
            var editor = form.BusinessEditorForSmokeTest;
            var workspace = form.WorkspaceForSmokeTest;
            var name = ((ByteTextEditor)ForLabel(editor, "名称")).Editor;
            name.SelectAll(); TestSupport.Assert(name.InputGuard.TryReplaceSelection("保存验证"), "姓名有效输入被拒绝");
            TestSupport.Assert(editor.HasPendingChanges && !workspace.IsDirty, "未失焦文本未被识别为待保存输入");
            name.Focus();
            TestSupport.Assert(editor.TryUndoFocusedText() && !editor.HasPendingChanges, "聚焦业务文本撤销未使用控件本地历史");
            TestSupport.Assert(editor.TryUndoFocusedText(redo: true) && name.Text == "保存验证" && editor.HasPendingChanges, "聚焦业务文本重做未恢复待保存值");
            var saved = form.SavePendingForSmokeTest();
            TestSupport.Assert(saved.Saved && !editor.HasPendingChanges && Convert.ToString(workspace.GetDocument("persons").Data.Rows[0]["名称"]) == "保存验证", "顶部保存未收集当前文本框");

            var biography = Descendants(editor).Single(group => group.Name == "PersonBiography");
            var biographyBox = Descendants(biography).OfType<ByteLimitedTextBox>().Single();
            var originalBiography = biographyBox.Text; var capacity = biographyBox.InputGuard.Validation!.Capacity;
            biographyBox.SelectAll();
            TestSupport.Assert(biographyBox.InputGuard.TryReplaceSelection(new string('汉', capacity / 2) + (capacity % 2 == 0 ? "" : "A")), "列传满容量输入失败");
            biographyBox.Select(biographyBox.TextLength, 0);
            TestSupport.Assert(!biographyBox.InputGuard.TryReplaceSelection("A") && biographyBox.InputGuard.Validation?.RemainingBytes == 0, "列传满容量后额外输入未被整次拒绝");
            var hpHost = ForLabel(editor, "初始HP"); var hp = Descendants(hpHost).OfType<NumericUpDown>().Single();
            var mpHost = ForLabel(editor, "初始MP"); var mp = Descendants(mpHost).OfType<NumericUpDown>().Single();
            var oldHp = hp.Value; var oldMp = mp.Value; hp.Value = 501; mp.Value = 251;
            TestSupport.Assert(Descendants(hpHost).OfType<Label>().Any(label => label.Text.Contains("500")) && Descendants(mpHost).OfType<Label>().Any(label => label.Text.Contains("250")),
                "HP/MP超游戏读取范围时没有动态提示");
            SaveSnapshots(form, "人物-字节容量", fontScale);
            hp.Value = oldHp; mp.Value = oldMp;
            biographyBox.SelectAll(); TestSupport.Assert(biographyBox.InputGuard.TryReplaceSelection(originalBiography), "列传测试草稿不能恢复");
            CheckBiographyTextRoundTrip(form);

            var quoteGroup = Descendants(editor).Single(group => group.Name == "PersonQuotes");
            var mode = Descendants(quoteGroup).OfType<ComboBox>().Single(combo => combo.Items.Cast<object>().Any(item => Equals(item, "普通暴击")));
            mode.SelectedIndex = 0; Application.DoEvents();
            var type = Descendants(quoteGroup).OfType<NumericUpDown>().Single();
            var oldType = (int)type.Value;
            var quoteBox = ((ByteTextEditor)ForLabel(quoteGroup, "暴击 1")).Editor;
            quoteBox.SelectAll(); TestSupport.Assert(quoteBox.InputGuard.TryReplaceSelection("切换前台词"), "有效台词输入失败");
            type.Value = oldType == 25 ? 24 : oldType + 1; Application.DoEvents();
            var pool = workspace.GetDocument("person-critical-quotes");
            TestSupport.Assert(Convert.ToString(pool.Data.Rows.Cast<DataRow>().Single(row => Convert.ToInt32(row["ID"]) == 21 + oldType * 3)["台词"]) == "切换前台词",
                "切换台词类型时丢失了原类型尚未应用的文本");
            type.Value = oldType; Application.DoEvents();
            TestSupport.Assert(quoteBox.Text == "切换前台词", "切回原类型没有重新加载保留的台词草稿");
            TestSupport.Assert(form.SavePendingForSmokeTest().Saved, "台词切换后的统一保存失败");
            var occupied = new PersonQuoteService(workspace).GetSpecialSlots().First(value => !value.IsEmpty && value.PersonId != 0);
            mode.SelectedIndex = 1; Application.DoEvents();
            var specialSelector = Descendants(quoteGroup).OfType<SearchableIdComboBox>().Single();
            specialSelector.SetBoundValue(occupied.Display);
            typeof(SearchableIdComboBox).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(specialSelector, [new KeyEventArgs(Keys.Enter)]);
            var occupiedText = quoteBox.Text;
            quoteBox.SelectAll(); TestSupport.Assert(quoteBox.InputGuard.TryReplaceSelection("未确认覆盖台词"), "冲突槽文本测试输入失败");
            mode.SelectedIndex = 0; Application.DoEvents();
            TestSupport.Assert(mode.SelectedIndex == 1 && quoteBox.Text == "未确认覆盖台词" && !workspace.IsDirty
                && Convert.ToString(pool.Data.Rows.Cast<DataRow>().Single(row => Convert.ToInt32(row["ID"]) == occupied.Id)["台词"]) == occupiedText,
                "未确认覆盖就通过切换选择写入了他人特殊槽文本");
            quoteBox.SelectAll(); TestSupport.Assert(quoteBox.InputGuard.TryReplaceSelection(occupiedText), "冲突槽草稿恢复失败");
            mode.SelectedIndex = 0; TestSupport.Assert(editor.Flush() && !workspace.IsDirty, "冲突槽撤销文本后未能安全切回");

            form.ShowPhysicalTableForSmokeTest("persons"); Application.DoEvents();
            var grid = form.PhysicalGridForSmokeTest; var persons = workspace.GetDocument("persons");
            var values = persons.Data.Rows.Cast<DataRow>().Select(row => Convert.ToInt32(row["武力"], CultureInfo.InvariantCulture)).ToArray();
            var idColumn = grid.Columns.Cast<DataGridViewColumn>().Single(column => column.DataPropertyName == "ID");
            var powerColumn = grid.Columns.Cast<DataGridViewColumn>().Single(column => column.DataPropertyName == "武力");
            grid.Sort(idColumn, ListSortDirection.Descending);
            var nameColumn = grid.Columns.Cast<DataGridViewColumn>().Single(column => column.DataPropertyName == "名称");
            grid.CurrentCell = grid.Rows[3].Cells[nameColumn.Index];
            TestSupport.Assert(grid.BeginEdit(true) && grid.EditingControl is TextBox, "物理表文本不能开始编辑");
            var physicalText = (TextBox)grid.EditingControl!;
            physicalText.SelectAll(); physicalText.SelectedText = "姓名输入验证";
            TestSupport.Assert(physicalText.Text == "姓名输入验证", "物理表满字节输入失败");
            physicalText.Select(physicalText.TextLength, 0); physicalText.SelectedText = "A";
            TestSupport.Assert(physicalText.Text == "姓名输入验证", "物理表超限替换未被阻止");
            SaveSnapshots(form, "人物-物理表字节容量", fontScale);
            grid.CancelEdit(); TestSupport.Assert(!workspace.IsDirty, "取消物理表文本编辑仍留下意外变更");
            grid.CurrentCell = grid.Rows[3].Cells[powerColumn.Index];
            var physicalRow = persons.Data.Rows.IndexOf(((DataRowView)grid.Rows[3].DataBoundItem).Row);
            var replacement = values[physicalRow] == 255 ? 254 : values[physicalRow] + 1;
            TestSupport.Assert(grid.BeginEdit(true) && grid.EditingControl is TextBox, "物理表不能开始编辑");
            ((TextBox)grid.EditingControl!).Text = replacement.ToString(CultureInfo.InvariantCulture);
            TestSupport.Assert(form.SavePendingForSmokeTest().Saved, "排序后单格编辑保存失败");
            for (var index = 0; index < values.Length; index++)
                TestSupport.Assert(Convert.ToInt32(persons.Data.Rows[index]["武力"], CultureInfo.InvariantCulture) == (index == physicalRow ? replacement : values[index]),
                    $"排序后写回错误人物物理行 {index}");
            CheckFilteredPhysicalCommands(form);

            form.SelectModuleForSmokeTest("兵种系"); Application.DoEvents();
            editor = form.BusinessEditorForSmokeTest;
            var page = editor.DetailTabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "辅助分组");
            editor.DetailTabs.SelectedTab = page; Application.DoEvents();
            var groupGrid = Descendants(page).OfType<DataGridView>().Single();
            groupGrid.CurrentCell = groupGrid.Rows[0].Cells[0]; Application.DoEvents();
            var before = new AccessoryJobGroupService().Read(workspace);
            var check = Descendants(page).OfType<CheckBox>().First(box => !box.Checked);
            var newMember = int.Parse(check.Name["辅助分组兵种系".Length..], CultureInfo.InvariantCulture);
            check.Checked = true;
            TestSupport.Assert(editor.HasPendingChanges && workspace.IsDirty, "辅助分组草稿没有纳入工作区未保存状态");
            var executableBeforeUndo = File.ReadAllBytes(Path.Combine(workspace.RootPath, "Ekd5.exe"));
            TestSupport.Assert(workspace.Commands.CanUndo && workspace.Commands.Undo() != null && !editor.HasPendingChanges && !check.Checked,
                "辅助分组勾选没有进入统一撤回栈");
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(workspace.RootPath, "Ekd5.exe")).SequenceEqual(executableBeforeUndo), "辅助分组草稿撤回意外写入文件");
            TestSupport.Assert(workspace.Commands.Redo() != null && editor.HasPendingChanges && check.Checked, "辅助分组草稿不能统一重做");
            var originalGroupCount = groupGrid.RowCount;
            Descendants(page).OfType<Button>().Single(button => button.Text == "新增空白分组").PerformClick();
            TestSupport.Assert(groupGrid.RowCount == originalGroupCount + 1, "辅助分组新增草稿失败");
            workspace.Commands.Undo(); TestSupport.Assert(groupGrid.RowCount == originalGroupCount, "辅助分组新增不能撤回");
            workspace.Commands.Redo(); TestSupport.Assert(groupGrid.RowCount == originalGroupCount + 1, "辅助分组新增不能重做");
            ((ToolStripMenuItem)groupGrid.ContextMenuStrip!.Items[0]).PerformClick();
            TestSupport.Assert(groupGrid.RowCount == originalGroupCount, "辅助分组删除草稿失败");
            workspace.Commands.Undo(); TestSupport.Assert(groupGrid.RowCount == originalGroupCount + 1, "辅助分组删除不能撤回");
            workspace.Commands.Undo(); TestSupport.Assert(groupGrid.RowCount == originalGroupCount, "连续撤回未恢复新增前分组");
            SaveSnapshots(form, "兵种系-辅助分组", fontScale);
            form.SelectModuleForSmokeTest("人物"); Application.DoEvents();
            TestSupport.Assert(Equals(form.ModuleTabsForSmokeTest.SelectedTab?.Tag, "人物") && form.PhysicalGridForSmokeTest.Visible
                && !editor.Visible && editor.HasPendingChanges,
                "切换模块没有恢复人物物理页并保留隐藏辅助分组页面的草稿");
            TestSupport.Assert(form.SavePendingForSmokeTest().Saved && !editor.HasPendingChanges && !workspace.IsDirty,
                "从其他模块统一保存未收集隐藏辅助分组草稿");
            var reread = new AccessoryJobGroupService().Read(workspace.RootPath);
            TestSupport.Assert(reread.Groups[0].JobSeriesIds.SequenceEqual(before.Groups[0].JobSeriesIds.Append(newMember)), "顶部保存后辅助分组复读不一致");
            form.SelectModuleForSmokeTest("兵种系"); Application.DoEvents();
            TestSupport.Assert(ReferenceEquals(editor, form.BusinessEditorForSmokeTest) && editor.DetailTabs.SelectedTab == page && !editor.HasPendingChanges,
                "保存后返回辅助分组未复用页面或留下过期草稿");
            Console.WriteLine("UI_HIDDEN_ACCESSORY_DRAFT_SAVE_OK");
            form.Close(); Console.WriteLine("UI_WRITEBACK_HARDENING_TESTS_OK");
        }
        finally
        {
            form.BusinessEditorForSmokeTest.DiscardPendingChanges();
            while (form.WorkspaceForSmokeTest.Commands.CanUndo) form.WorkspaceForSmokeTest.Commands.Undo();
            form.WorkspaceForSmokeTest.DiscardNativeDrafts();
            form.Close(); Directory.Delete(copy, true);
        }
    }

    private static void CheckFilteredPhysicalCommands(MainForm form)
    {
        var workspace = form.WorkspaceForSmokeTest;
        var document = workspace.GetDocument("persons");
        var grid = form.PhysicalGridForSmokeTest;
        var originalPower = document.Data.Rows.Cast<DataRow>().Select(row => row["武力"]).ToArray();
        var originalNames = document.Data.Rows.Cast<DataRow>().Select(row => row["名称"]).ToArray();
        var originalBytes = File.ReadAllBytes(document.FilePath);
        var oldFilter = document.Data.DefaultView.RowFilter;
        var oldSort = document.Data.DefaultView.Sort;
        var idColumn = grid.Columns.Cast<DataGridViewColumn>().Single(column => column.DataPropertyName == "ID");
        var powerColumn = grid.Columns.Cast<DataGridViewColumn>().Single(column => column.DataPropertyName == "武力");
        var physicalRowMethod = typeof(MainForm).GetMethod("PhysicalRowIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int Physical(DataGridViewRow row) => (int)physicalRowMethod.Invoke(form, [row.Index])!;
        void AssertColumn(string column, object[] expected, string operation)
        {
            for (var index = 0; index < expected.Length; index++)
                TestSupport.Assert(Equals(document.Data.Rows[index][column], expected[index]), $"{operation}错误修改了物理行 {index} / {column}");
        }
        void SuppressGridEvents(Action action)
        {
            var field = typeof(MainForm).GetField("_suppressGridEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var previous = field.GetValue(form);
            field.SetValue(form, true);
            try { action(); }
            finally { field.SetValue(form, previous); }
            Application.DoEvents();
        }
        try
        {
            document.Data.DefaultView.RowFilter = "[ID] IN (3, 7, 11, 17)";
            grid.Sort(idColumn, ListSortDirection.Descending); Application.DoEvents();
            TestSupport.Assert(grid.Rows.Count == 4, "筛选后的真实物理表未显示四行");
            foreach (DataGridViewRow row in grid.Rows)
                TestSupport.Assert(Physical(row) == document.Data.Rows.IndexOf(((DataRowView)row.DataBoundItem).Row), "显示行到物理行映射与数据绑定对象不一致");
            var deleteRow = grid.Rows.Cast<DataGridViewRow>().First(row => Convert.ToInt32(((DataRowView)row.DataBoundItem).Row["武力"], CultureInfo.InvariantCulture) != 0);
            var deletePhysical = Physical(deleteRow);
            grid.CurrentCell = deleteRow.Cells[powerColumn.Index];
            var deleteArgs = new KeyEventArgs(Keys.Delete);
            typeof(MainForm).GetMethod("GridKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [grid, deleteArgs]);
            var deletedPower = originalPower.ToArray(); deletedPower[deletePhysical] = 0;
            AssertColumn("武力", deletedPower, "筛选排序后真实Delete入口");
            AssertColumn("名称", originalNames, "筛选排序后真实Delete入口");
            TestSupport.Assert(deleteArgs.Handled && deleteArgs.SuppressKeyPress && workspace.IsDirty, "真实Delete入口未按预期记录草稿");
            form.UndoDraftForSmokeTest(); AssertColumn("武力", originalPower, "Delete撤回");

            grid.ClearSelection(); grid.Rows[0].Selected = true; grid.Rows[2].Selected = true;
            var selected = grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index).Select(Physical).Where(index => index >= 0).Distinct().ToArray();
            var expectedSelected = new[] { document.Data.Rows.IndexOf(((DataRowView)grid.Rows[0].DataBoundItem).Row), document.Data.Rows.IndexOf(((DataRowView)grid.Rows[2].DataBoundItem).Row) };
            TestSupport.Assert(selected.SequenceEqual(expectedSelected), "排序筛选后批量选行顺序未映射到真实记录");
            // Reuse the dialog entry's actual selection mapping without opening a modal dialog in the smoke test.
            SuppressGridEvents(() => workspace.ApplyBatchMultiple(document, selected, ["武力"], "统一赋值", "173"));
            var batchPower = originalPower.ToArray(); foreach (var row in selected) batchPower[row] = 173;
            AssertColumn("武力", batchPower, "排序筛选后批量选行"); AssertColumn("名称", originalNames, "排序筛选后批量选行");
            form.UndoDraftForSmokeTest(); AssertColumn("武力", originalPower, "批量选行撤回");
            typeof(MainForm).GetMethod("DraftRedo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            AssertColumn("武力", batchPower, "批量选行重做");
            form.UndoDraftForSmokeTest(); AssertColumn("武力", originalPower, "批量选行重做后撤回");

            var visible = grid.Rows.Cast<DataGridViewRow>().Where(row => row.Visible).Select(Physical).Where(index => index >= 0).ToArray();
            TestSupport.Assert(visible.Length == 4 && visible.Distinct().Count() == 4, "查找替换可见行范围不正确");
            var hidden = Enumerable.Range(0, document.Data.Rows.Count).First(index => !visible.Contains(index));
            SuppressGridEvents(() => workspace.ApplyBatchMultiple(document, visible.Append(hidden).ToArray(), ["名称"], "统一赋值", "映射甲"));
            var seededNames = originalNames.ToArray(); foreach (var row in visible.Append(hidden)) seededNames[row] = "映射甲";
            var changed = -1;
            // Reuse FindReplaceInCurrentTable's visible-row mapping; hidden records must remain untouched.
            SuppressGridEvents(() => changed = workspace.ApplyFindReplace(document, visible, "名称", "映射", "查换"));
            var replacedNames = seededNames.ToArray(); foreach (var row in visible) replacedNames[row] = "查换甲";
            TestSupport.Assert(changed == visible.Length, "排序筛选后查找替换数量错误");
            AssertColumn("名称", replacedNames, "排序筛选后查找替换"); AssertColumn("武力", originalPower, "排序筛选后查找替换");
            form.UndoDraftForSmokeTest(); AssertColumn("名称", seededNames, "查找替换撤回");
            typeof(MainForm).GetMethod("DraftRedo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            AssertColumn("名称", replacedNames, "查找替换重做");
            form.UndoDraftForSmokeTest(); form.UndoDraftForSmokeTest();
            AssertColumn("名称", originalNames, "查找替换与测试预置连续撤回");
            TestSupport.Assert(!workspace.IsDirty && File.ReadAllBytes(document.FilePath).SequenceEqual(originalBytes), "物理表草稿映射/撤回测试意外修改磁盘或留下草稿");
        }
        finally
        {
            document.Data.DefaultView.RowFilter = oldFilter;
            document.Data.DefaultView.Sort = oldSort;
            Application.DoEvents();
        }
        Console.WriteLine("UI_SORT_FILTER_COMMAND_MAPPING_OK Delete selected-batch visible-find-replace undo redo");
    }

    private static void CheckBiographyTextRoundTrip(MainForm form)
    {
        var workspace = form.WorkspaceForSmokeTest;
        var document = workspace.GetDocument("person-biography");
        var field = document.Definition.Fields.Single(field => field.Kind == FieldKind.FixedString);
        var rowIndex = document.Data.Rows.Cast<DataRow>().Select((row, index) => (row, index))
            .Single(value => Convert.ToInt32(value.row["ID"], CultureInfo.InvariantCulture) == 0).index;
        foreach (var (name, value) in new[]
        {
            ("独立CR", "保存甲\r乙  "),
            ("独立LF", "保存甲\n乙  "),
            ("CRLF", "保存甲\r\n乙  "),
            ("控制码01及1A", "保存甲\u0001乙\u001A  "),
            ("混合格式", "保存甲\r乙\n丙\r\n丁\u0001戊\u001A  ")
        })
        {
            var biography = Descendants(form.BusinessEditorForSmokeTest).Single(group => group.Name == "PersonBiography");
            var box = Descendants(biography).OfType<ByteLimitedTextBox>().Single();
            box.Focus(); box.SelectAll();
            TestSupport.Assert(box.InputGuard.TryReplaceSelection(value) && box.Text == value, name + "真实列传控件输入被归一化或截断");
            TestSupport.Assert(form.SavePendingForSmokeTest().Saved, name + "真实列传控件保存失败");
            var reloadedBox = Descendants(form.BusinessEditorForSmokeTest).Single(group => group.Name == "PersonBiography")
                .Controls.Cast<Control>().SelectMany(control => Descendants(control).Prepend(control)).OfType<ByteLimitedTextBox>().Single();
            TestSupport.Assert(reloadedBox.Text == value && !reloadedBox.InputGuard.HasPendingChanges, name + "保存后重绑定改变文本");
            var reread = new ProjectWorkspace(); reread.Open(workspace.RootPath);
            TestSupport.Assert(Convert.ToString(reread.GetDocument("person-biography").Data.Rows[rowIndex][field.Name], CultureInfo.InvariantCulture) == value,
                name + "保存后从磁盘重新打开文本不一致");
            var raw = File.ReadAllBytes(document.FilePath);
            var offset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize + field.Offset));
            var payload = EncodingService.GameEncoding.GetBytes(value);
            TestSupport.Assert(raw.AsSpan(offset, payload.Length).SequenceEqual(payload) && raw[offset + payload.Length] == 0,
                name + "保存后的实际字节、尾空格或终止符不一致");
        }
        Console.WriteLine("UI_MULTILINE_DISK_ROUNDTRIP_OK CR LF CRLF trailing-spaces 01 1A");
    }

    private static Control ForLabel(Control root, string caption)
    {
        var label = Descendants(root).OfType<Label>().Single(label => label.Text == caption);
        var layout = (TableLayoutPanel)label.Parent!;
        return layout.GetControlFromPosition(1, layout.GetPositionFromControl(label).Row)!;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void SaveSnapshots(MainForm form, string prefix, float fontScale)
    {
        var directory = Environment.GetEnvironmentVariable("DATAMANAGER_HARDENING_SCREENSHOTS")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "验证", "写回完善截图"));
        Directory.CreateDirectory(directory);
        foreach (var size in new[] { new Size(1280, 720), new Size(1500, 900) })
        {
            form.Size = size; form.PerformLayout(); Application.DoEvents(); Application.DoEvents();
            foreach (var text in Descendants(form.BusinessEditorForSmokeTest).OfType<ByteTextEditor>().Where(text => text.Visible))
                TestSupport.Assert(text.Editor.Height >= text.Editor.Font.Height + 3, $"{prefix}/{size} 下字节计数遮住文本输入");
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            var scaling = fontScale == 1f ? string.Empty : $"-字体{fontScale * 100:0}%模拟";
            bitmap.Save(Path.Combine(directory, $"{prefix}-{size.Width}x{size.Height}-{form.DeviceDpi}dpi{scaling}.png"));
        }
    }
}

