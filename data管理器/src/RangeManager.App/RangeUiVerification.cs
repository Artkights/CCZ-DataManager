using System.Reflection;
using System.Text.Json;

namespace RangeManager.App;

/// <summary>Real WinForms layout checks. Draft edits are discarded; no Save is called.</summary>
public static class RangeUiVerification
{
    private sealed record Result(string Scenario, bool Passed, string Detail, int DeviceDpi);

    public static int Run(string root, string output)
    {
        root = Path.GetFullPath(root);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var results = new List<Result>();
        var sourceHashes = SourceFiles(root).ToDictionary(path => path, RangeSaveTransaction.Hash, StringComparer.OrdinalIgnoreCase);
        int actualDpi = 0;
        void Check(string name, Action action, int dpi)
        {
            try { action(); results.Add(new(name, true, "通过", dpi)); }
            catch (Exception ex) { results.Add(new(name, false, ex.ToString(), dpi)); }
        }
        static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }

        foreach (var window in new[] { new Size(1100, 650), new Size(1280, 800), new Size(1920, 1080) })
        foreach (float scale in new[] { 1f, 1.25f, 1.5f })
        {
            using var form = new MainForm(openDefaultBase: false)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false
            };
            using var font = new Font(form.Font.FontFamily, form.Font.SizeInPoints * scale);
            try
            {
                form.Font = font;
                form.Size = window;
                form.OpenBaseForTest(root);
                form.Show();
                form.Size = window;
                Application.DoEvents();
                actualDpi = form.DeviceDpi;
                foreach (int page in new[] { 0, 1, 2 })
                {
                    string name = $"{window.Width}x{window.Height}/字体{scale:P0}/{(page == 0 ? "攻击" : page == 1 ? "穿透" : "拓展")}";
                    Check(name, () =>
                    {
                        form.SelectPageForTest(page);
                        Application.DoEvents();
                        Assert(form.TabsForTest.TabPages.Count == 3 && form.TabsForTest.TabPages[2].Text == "拓展范围", "主界面拓展范围页签缺失");
                        if (page == 2)
                        {
                            var extension = form.ExtensionEditorForTest;
                            Assert(extension.GridForTest.Rows.Count >= 0x26, "拓展范围页没有显示完整攻击编号目录（至少应包含0x00..0x25）");
                            Assert(extension.GridForTest.Columns.Count == 5, "拓展范围页表列定义不完整");
                            var comboColumn = extension.GridForTest.Columns[2] as DataGridViewComboBoxColumn;
                            var options = extension.RangeValueOptionsForTest;
                            Assert(comboColumn != null && options.Count == RangeExtensionMapping.MaxRangeValue + 1 &&
                                options[0] == "0x00" && options[^1] == $"0x{RangeExtensionMapping.MaxRangeValue:X2}",
                                $"下一级范围值没有提供完整下拉选项（数量={options.Count}）");
                            Assert(extension.StatusForTest.Text.Contains("0x0041CEAB", StringComparison.Ordinal), "拓展范围页未显示真实映射地址");
                            Assert(!form.SessionForTest!.IsDirty, "只读浏览拓展范围页产生草稿");
                            // Regression for the reported WinForms
                            // SetCurrentCellAddressCore re-entry: edit row 0,
                            // finish the cell, then move directly to row 1.
                            // RefreshMapping is deferred until EndEdit has
                            // returned, so this sequence must not throw or
                            // jump back to a different row.
                            var grid = extension.GridForTest;
                            var session = form.SessionForTest;
                            byte[] original = session.Extension.Values.ToArray();
                            int ChangedValue(int row) => original[row] == 2 ? 3 : 2;
                            void StartEdit(int row, string text)
                            {
                                grid.CurrentCell = grid.Rows[row].Cells[2];
                                Assert(grid.BeginEdit(true), "拓展范围单元格无法进入编辑状态");
                                Assert(grid.EditingControl is ComboBox, "拓展范围未创建下拉编辑器");
                                ((ComboBox)grid.EditingControl!).Text = text;
                            }
                            void AssertCell(int row, int expected)
                            {
                                Assert(session.Extension.Values[row] == expected &&
                                    Convert.ToString(grid.Rows[row].Cells[2].Value) == $"0x{expected:X2}",
                                    $"拓展范围第{row}行表格与草稿不一致");
                                Assert(Convert.ToString(grid.Rows[row].Cells[4].Value)!
                                    .StartsWith(row == expected ? $"0x{row:X2} → ↻" : $"0x{row:X2} → 0x{expected:X2}", StringComparison.Ordinal),
                                    $"拓展范围第{row}行链路未同步");
                                Assert(grid.Rows[row].Cells[1].Value is not Bitmap image || image.Width == 64 && image.Height == 64,
                                    "范围值预览单元格类型不正确");
                                Assert(grid.Rows[row].Cells[3].Value is not Bitmap nextImage || nextImage.Width == 64 && nextImage.Height == 64,
                                    "下一级范围预览单元格类型不正确");
                            }
                            StartEdit(0, $"0x{ChangedValue(0):X2}");
                            // Moving the current cell while still editing
                            // exercises EndEdit inside SetCurrentCellAddressCore.
                            grid.CurrentCell = grid.Rows[1].Cells[2];
                            Application.DoEvents();
                            AssertCell(0, ChangedValue(0));
                            Assert(grid.CurrentCell?.RowIndex == 1 && grid.CurrentCell.ColumnIndex == 2,
                                "拓展范围刷新后当前单元格跳回原行");

                            int UndoCount() => ((System.Collections.ICollection)typeof(RangeEditSession)
                                .GetField("_undo", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .GetValue(session)!).Count;
                            int undoCountBeforeInvalid = UndoCount();
                            byte[] beforeInvalid = session.Extension.Values.ToArray();
                            // The production editor is a DropDownList, so an
                            // invalid byte cannot be typed into the control.
                            // Exercise the same validation boundary directly;
                            // the session must reject it without a draft or
                            // undo record, while the following valid edits use
                            // the actual owner-drawn combo.
                            Assert(!session.UpdateExtension(1, 0xFF, out _),
                                "保留哨兵 0xFF 不应被接受为下一级范围值");
                            Assert(session.Extension.Values.SequenceEqual(beforeInvalid) &&
                                UndoCount() == undoCountBeforeInvalid, "非法输入改变了草稿或创建了撤回记录");
                            StartEdit(1, $"0x{ChangedValue(1):X2}");
                            grid.CurrentCell = grid.Rows[2].Cells[2];
                            // Start another edit before the queued refresh
                            // runs; it must preserve the new editor as well.
                            Assert(grid.BeginEdit(true), "连续编辑下一行失败");
                            ((ComboBox)grid.EditingControl!).Text = $"0x{ChangedValue(2):X2}";
                            Application.DoEvents();
                            Assert(grid.IsCurrentCellInEditMode && grid.CurrentCell?.RowIndex == 2,
                                "上一次刷新打断了下一行编辑");
                            grid.CurrentCell = grid.Rows[3].Cells[2];
                            Application.DoEvents();
                            for (int row = 0; row < 3; row++) AssertCell(row, ChangedValue(row));
                            Assert(grid.CurrentCell?.RowIndex == 3, "连续编辑后选中行发生跳动");

                            session.Undo();
                            Application.DoEvents();
                            AssertCell(2, original[2]);
                            Assert(grid.CurrentCell?.RowIndex == 3, "撤回后选中行发生跳动");
                            session.Redo();
                            Application.DoEvents();
                            AssertCell(2, ChangedValue(2));
                            var restore = Descendants(extension).OfType<Button>().Single(button => button.Text == "恢复本表");
                            restore.PerformClick();
                            Application.DoEvents();
                            Assert(session.Extension.Values.SequenceEqual(original), "恢复本表后映射未还原");
                            for (int row = 0; row < 3; row++) AssertCell(row, original[row]);
                            Assert(grid.CurrentCell?.RowIndex == 3, "恢复本表后选中行发生跳动");
                            Assert(!session.IsDirty, "拓展范围回归后仍有未保存草稿");
                            grid.CurrentCell = grid.Rows[20].Cells[2];
                            grid.FirstDisplayedScrollingRowIndex = 12;
                            int firstVisible = grid.FirstDisplayedScrollingRowIndex;
                            Assert(session.UpdateExtension(3, ChangedValue(3), out _), "滚动保持回归无法修改映射");
                            Application.DoEvents();
                            Assert(grid.CurrentCell?.RowIndex == 20 && grid.CurrentCell.ColumnIndex == 2 &&
                                grid.FirstDisplayedScrollingRowIndex == firstVisible,
                                "表格刷新没有保留当前单元格和首个可见行");
                            session.Undo();
                            Application.DoEvents();
                            Assert(!session.IsDirty && grid.CurrentCell?.RowIndex == 20 &&
                                grid.FirstDisplayedScrollingRowIndex == firstVisible,
                                "滚动后撤回没有恢复草稿或保持位置");
                            return;
                        }
                        var editor = page == 0 ? form.AttackEditorForTest : form.PierceEditorForTest;
                        Assert(editor.SelectRangeForTest(1), "无法选择原生编号1");
                        Application.DoEvents();
                        Assert(editor.PreviewForTest.Columns >= 7 && editor.PreviewForTest.Rows >= 7, "自动网格少于7x7");
                        Assert(editor.PreviewForTest.Columns == editor.PreviewForTest.Rows &&
                            editor.PreviewForTest.GridLayout.Bounds.Width == editor.PreviewForTest.GridLayout.Bounds.Height,
                            "范围预览必须保持正方形网格");
                        if (page == 1)
                        {
                            var selfCentered = form.SessionForTest!.Bindings
                                .FirstOrDefault(b => b.Key.Kind == RangeKind.Pierce &&
                                    b.Center == RangeCenterMode.Self && b.Definition.Groups.Count > 0);
                            if (selfCentered != null)
                            {
                                Assert(editor.SelectRangeForTest(selfCentered.Key.Id), "无法选择自身中心穿透编号");
                                Application.DoEvents();
                                Assert(editor.PreviewForTest.TargetMarker == null,
                                    "自身中心穿透不应绘制主攻击目标外框");
                                var target = RangePreviewContext.Default.Target;
                                var targetCells = form.SessionForTest.Preview(selfCentered.Key, RangePreviewContext.Default);
                                if (targetCells.Contains(target))
                                {
                                    var targetRect = editor.PreviewForTest.GridLayout.CellRectangle(target);
                                    Assert(targetRect.IntersectsWith(editor.PreviewForTest.GridLayout.Bounds),
                                        "自身中心穿透目标格未保留在可选网格中");
                                }
                            }
                        }
                        Assert(editor.CellPixelsForTest.Value == 20 && editor.CellPixelsForTest.Minimum == 16, "默认/最小格像素不符");
                        Assert(editor.GeneratorsForTest.Visible && editor.GeneratorsForTest.ClientSize.Width > 100 &&
                            editor.GeneratorsForTest.ClientSize.Height >= editor.GeneratorsForTest.Font.Height * 2,
                            "可见生成元编辑框不足两行或被压缩");
                        Assert(editor.GroupSelectorForTest.Items.Count > 0 && editor.GroupSelectorForTest.SelectedIndex >= 0 &&
                            !string.IsNullOrWhiteSpace(editor.GroupSelectorForTest.Text), "编辑组没有有效文本");
                        foreach (var button in Descendants(editor).OfType<Button>().Where(b => b.Visible))
                        {
                            var text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                            Assert(button.ClientSize.Width >= text.Width + 4 && button.ClientSize.Height >= button.Font.Height,
                                $"按钮被裁剪：{button.Text}={button.ClientSize}, font={button.Font.Height}, text={text.Width}");
                            if (button.Text is "新增" or "删除" or "修改")
                            {
                                var singleLine = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine);
                                var preferred = button.GetPreferredSize(Size.Empty);
                                Assert(button.Width >= preferred.Width && button.ClientSize.Width >= singleLine.Width + 12 * button.DeviceDpi / 96 &&
                                    button.Height >= preferred.Height,
                                    $"按钮没有容纳原生首选尺寸/完整单行文字：{button.Text}={button.Size}, preferred={preferred}, text={singleLine}");
                            }
                        }
                        var viewport = (Panel)editor.PreviewForTest.Parent!;
                        Assert(viewport.ClientSize.Width > 200 && viewport.ClientSize.Height > 40, "画布视口被压缩到不可用");
                        var editViewport = Descendants(editor).OfType<Panel>().Single(p => p.Name == "RangeEditViewport");
                        Assert(editViewport.AutoScroll && viewport.ClientSize.Height >= editViewport.Height,
                            "底部编辑区没有滚动能力或占用了超过一半主体空间");
                        Assert(editor.PreviewForTest.Width >= editor.PreviewForTest.GetPreferredSize(Size.Empty).Width &&
                            editor.PreviewForTest.Height >= editor.PreviewForTest.GetPreferredSize(Size.Empty).Height,
                            "画布不足以完整容纳格点");
                        var gridRect = editor.PreviewForTest.GridLayout.Bounds;
                        Assert(gridRect.Bottom <= editor.PreviewForTest.Height - 12,
                            "画布网格未完整落在可视区域内");
                        Assert(!form.SessionForTest!.IsDirty, "只读切页/尺寸/字体变化产生草稿");
                        if (page == 1 && window == new Size(1100, 650) && Math.Abs(scale - 1f) < 0.001f)
                        {
                            var centerKey = new RangeKey(RangeKind.Pierce, 1);
                            if (form.SessionForTest!.CanChangeCenter(centerKey, out _))
                            {
                                var originalCenter = form.SessionForTest.Get(centerKey).Center;
                                var switchedCenter = originalCenter == RangeCenterMode.Self
                                    ? RangeCenterMode.Target : RangeCenterMode.Self;
                                Assert(form.SessionForTest.UpdateCenter(centerKey, switchedCenter, out _),
                                    "已有穿透编号中心切换失败");
                                Application.DoEvents();
                                Assert(form.SessionForTest.Get(centerKey).Center == switchedCenter &&
                                    editor.CenterSelectorForTest.SelectedIndex == (switchedCenter == RangeCenterMode.Self ? 1 : 0),
                                    "已有穿透编号中心切换未更新界面");
                                form.SessionForTest.Undo();
                                Application.DoEvents();
                                Assert(form.SessionForTest.Get(centerKey).Center == originalCenter,
                                    "已有穿透编号中心切换撤回失败");
                            }
                            var menu = (ContextMenuStrip)typeof(RangeEditorPanel)
                                .GetField("_pierceAddMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .GetValue(editor)!;
                            var add = (Button)typeof(RangeEditorPanel)
                                .GetField("_add", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .GetValue(editor)!;
                            Assert(menu.Items.Count == 4, "穿透新增菜单项数量不正确");
                            Assert(menu.Items.Cast<ToolStripItem>().Select(item => item.Text).SequenceEqual([
                                "目标中心 · 镜像", "目标中心 · 方向", "自身中心 · 镜像", "自身中心 · 方向"]),
                                "穿透新增菜单中心/几何选项不完整");
                            add.PerformClick();
                            Application.DoEvents();
                            Assert(menu.Visible && !menu.IsDisposed, "穿透新增菜单未能打开");
                            menu.Hide();
                            Application.DoEvents();
                            Assert(!menu.IsDisposed, "关闭穿透新增菜单时不应释放菜单对象");
                            add.PerformClick();
                            Application.DoEvents();
                            Assert(menu.Visible && !menu.IsDisposed, "穿透新增菜单关闭后不能再次打开");
                            menu.Hide();
                            Application.DoEvents();
                        }
                    }, form.DeviceDpi);
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(output, $"{window.Width}x{window.Height}-font{scale * 100:0}-{page}.png"));
                }

                Check($"{window.Width}x{window.Height}/字体{scale:P0}/大网格滚动与缩小恢复", () =>
                {
                    form.SelectPageForTest(0);
                    var editor = form.AttackEditorForTest;
                    Assert(editor.EditTextForTest(1, "20,0"), "半径20生成元输入失败");
                    Application.DoEvents();
                    Assert(editor.PreviewForTest.Columns >= 43 && editor.PreviewForTest.Rows >= 43, "半径20预览没有自动扩大到完整边界和留白");
                    var viewport = (Panel)editor.PreviewForTest.Parent!;
                    var preferred = editor.PreviewForTest.GetPreferredSize(Size.Empty);
                    Assert(viewport.AutoScroll && viewport.AutoScrollMinSize.Width >= preferred.Width && viewport.AutoScrollMinSize.Height >= preferred.Height,
                        "大网格缺少完整滚动范围");
                    editor.AutomaticGridForTest.Checked = false;
                    editor.GridColumnsForTest.Value = 7;
                    editor.GridRowsForTest.Value = 7;
                    Assert(editor.GridColumnsForTest.Value >= 43 && editor.GridRowsForTest.Value >= 43, "手动缩小裁剪了真实范围");
                    editor.CellPixelsForTest.Value = 16;
                    Assert(editor.PreviewForTest.CellPixelSize >= 16, "格像素被缩到16以下");
                    form.SessionForTest!.Restore(new(RangeKind.Attack, 1));
                }, form.DeviceDpi);
            }
            catch (Exception ex) { results.Add(new($"{window}/字体{scale:P0}/窗体初始化", false, ex.ToString(), form.DeviceDpi)); }
            finally { form.CloseForTest(); }
        }

        using (var form = new MainForm(openDefaultBase: false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false
        })
        {
            try
            {
                form.OpenBaseForTest(root);
                form.Show();
                Application.DoEvents();
                Check("整组悬停预览不改变草稿", () =>
                {
                    var editor = form.AttackEditorForTest;
                    Assert(editor.SelectRangeForTest(1), "无法选择攻击编号1");
                    var grid = editor.PreviewForTest;
                    var center = grid.CellCenterForTest(new(2, 1));
                    typeof(ShapeGridControl).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(grid, [new MouseEventArgs(MouseButtons.None, 0, center.X, center.Y, 0)]);
                    var expected = ShapeGeometry.ExpandMirror([new CellOffset(2, 1)]).ToHashSet();
                    Assert(grid.HoverCellsForTest.ToHashSet().SetEquals(expected), "悬停未高亮完整镜像组");
                    Assert(!form.SessionForTest!.IsDirty, "悬停改变了草稿");
                }, form.DeviceDpi);
            }
            finally { form.CloseForTest(); }
        }

        Check("真实基底文件哈希保持一致", () =>
            Assert(sourceHashes.All(pair => RangeSaveTransaction.Hash(pair.Key) == pair.Value), "UI验收改变了真实基底文件"), actualDpi);
        bool passed = results.All(r => r.Passed);
        File.WriteAllText(Path.Combine(output, "ui-verification.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, Checks = results.Count, DeviceDpi = actualDpi,
            ScaleMeaning = "FontScale为额外字体放大模拟，不等于真实显示器DPI切换；DeviceDpi为宿主实测。",
            SourceHashes = sourceHashes, Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        yield return Path.Combine(root, "Ekd5.exe");
        foreach (string name in new[] { "Hitarea.e5", "Effarea.e5", "Spalet.e5" })
        {
            string path = Path.Combine(root, "E5", name);
            if (!File.Exists(path)) path = Path.Combine(root, name);
            if (File.Exists(path)) yield return path;
        }
    }
}
