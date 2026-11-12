using System.Text;

namespace TierManager.App;

public sealed class MainForm : Form
{
    private readonly ListView _tierList = new();
    private readonly DataGridView _grid = new();
    private readonly DataGridView _previewGrid = new();
    private readonly NumericUpDown _probe = new();
    private readonly Label _probeResult = new();
    private readonly TextBox _info = new();
    private readonly Label _maskHint = new();
    private readonly ToolStripLabel _baseInfo = new();
    private readonly ToolStripLabel _status = new();
    private readonly ToolStripButton _write = new("写入");
    private readonly ToolStripButton _discard = new("放弃修改");
    private readonly ToolStripButton _restore = new("恢复备份");
    private readonly Button _add = new();
    private readonly Button _remove = new();
    private readonly Button _up = new();
    private readonly Button _down = new();

    private SplitContainer? _split;
    private TierProfile? _profile;
    private readonly List<TierEntry> _rows = new();
    private bool _loading;
    private bool _splitterMoved;
    private bool _dirty;

    public MainForm(string? initialRoot = null)
    {
        Text = "档位管理器";
        Font = new Font("Microsoft YaHei", 9F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 720);
        Size = new Size(Math.Min(1180, area.Width), Math.Min(820, area.Height));
        MinimumSize = new Size(960, 620);
        KeyPreview = true;
        BuildUi();
        WindowState = FormWindowState.Maximized;
        FormClosing += OnClosing;
        if (!string.IsNullOrWhiteSpace(initialRoot))
            Shown += (_, _) => LoadBase(initialRoot);
    }

    private void BuildUi()
    {
        var strip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Font = Font,
            Padding = new Padding(6, 4, 6, 4)
        };
        var choose = new ToolStripButton("选择基底文件夹");
        choose.Click += (_, _) => ChooseBase();
        var rescan = new ToolStripButton("重新扫描");
        rescan.Click += (_, _) => { if (_profile != null) LoadBase(_profile.RootPath); };
        _write.Click += (_, _) => WriteRows();
        _discard.Click += (_, _) => Discard();
        _restore.Click += (_, _) => RestoreBackup();
        _write.Enabled = false;
        _discard.Enabled = false;
        _restore.Enabled = false;
        _baseInfo.AutoSize = true;
        _baseInfo.Text = "尚未选择基底。";
        _status.AutoSize = true;
        _status.ForeColor = Color.DimGray;
        _status.Text = "请选择包含 Ekd5.exe 的基底文件夹。";
        strip.Items.AddRange(new ToolStripItem[]
        {
            choose, rescan, new ToolStripSeparator(), _write, _discard, _restore,
            new ToolStripSeparator(), _baseInfo, new ToolStripSeparator(), _status
        });

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        _split = split;
        split.SplitterMoved += (_, _) => _splitterMoved = true;
        Shown += (_, _) => FitSplitter();
        Resize += (_, _) => FitSplitter();

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(4)
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 70));

        var infoGroup = new GroupBox
        {
            Text = "基底信息",
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(10)
        };
        _info.Dock = DockStyle.Fill;
        _info.Multiline = true;
        _info.ReadOnly = true;
        _info.BorderStyle = BorderStyle.None;
        _info.ScrollBars = ScrollBars.Both;
        _info.WordWrap = false;
        _info.TabStop = false;
        _info.BackColor = SystemColors.Control;
        _info.ForeColor = Color.FromArgb(64, 64, 64);
        _info.Text = "尚未扫描基底。\r\n\r\n点击工具栏“选择基底文件夹”，\r\n工具会静态扫描 Ekd5.exe，\r\n识别档位函数、阈值与显示字母。";
        infoGroup.Controls.Add(_info);

        var listGroup = new GroupBox
        {
            Text = "档位一览（点击可定位到编辑表）",
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(8)
        };
        _tierList.Dock = DockStyle.Fill;
        _tierList.View = View.Details;
        _tierList.FullRowSelect = true;
        _tierList.HideSelection = false;
        _tierList.MultiSelect = false;
        _tierList.Columns.Add("档位", 80);
        _tierList.Columns.Add("阈值", 120);
        _tierList.Columns.Add("字母", 120);
        _tierList.SelectedIndexChanged += (_, _) => OnListSelectionChanged();
        listGroup.Controls.Add(_tierList);

        left.Controls.Add(infoGroup, 0, 0);
        left.Controls.Add(listGroup, 0, 1);
        split.Panel1.Controls.Add(left);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4)
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 14));

        var editGroup = new GroupBox
        {
            Text = "档位编辑（第一行是基准档，没有阈值；字母最多 2 个 GBK 字节，即 1 个汉字）",
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(8)
        };
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "档位", FillWeight = 16, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "属性值阈值", FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "显示字母", FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "覆盖属性值区间", FillWeight = 32, ReadOnly = true });
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, _) => OnGridEdited();
        _grid.SelectionChanged += (_, _) => OnGridSelectionChanged();

        var editButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 4)
        };
        _add.Text = "添加档位";
        _add.AutoSize = true;
        _add.MinimumSize = new Size(96, 30);
        _add.Enabled = false;
        _add.Click += (_, _) => AddTier();
        _remove.Text = "删除选中";
        _remove.AutoSize = true;
        _remove.MinimumSize = new Size(96, 30);
        _remove.Enabled = false;
        _remove.Click += (_, _) => RemoveTier();
        _up.Text = "字母上移";
        _up.AutoSize = true;
        _up.MinimumSize = new Size(96, 30);
        _up.Enabled = false;
        _up.Click += (_, _) => MoveLabel(-1);
        _down.Text = "字母下移";
        _down.AutoSize = true;
        _down.MinimumSize = new Size(96, 30);
        _down.Enabled = false;
        _down.Click += (_, _) => MoveLabel(1);
        var moveHint = new Label
        {
            Text = "上移/下移只交换显示字母，不改变阈值顺序。",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(10, 11, 0, 0)
        };
        editButtons.Controls.AddRange([_add, _remove, _up, _down, moveHint]);
        editGroup.Controls.Add(_grid);
        editGroup.Controls.Add(editButtons);

        var previewGroup = new GroupBox
        {
            Text = "显示与属性值预览",
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(8)
        };
        var previewLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var probePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 0)
        };
        probePanel.Controls.Add(new Label { Text = "属性值：", AutoSize = true, Margin = new Padding(0, 7, 0, 0) });
        _probe.Width = 80;
        _probe.Minimum = 0;
        _probe.Maximum = 255;
        _probe.Value = 0;
        _probe.Margin = new Padding(0, 3, 12, 0);
        _probe.ValueChanged += (_, _) => UpdateProbe();
        _probeResult.AutoSize = true;
        _probeResult.Font = new Font(Font, FontStyle.Bold);
        _probeResult.ForeColor = Color.FromArgb(0, 90, 158);
        _probeResult.Margin = new Padding(0, 7, 0, 0);
        probePanel.Controls.Add(_probe);
        probePanel.Controls.Add(_probeResult);

        _previewGrid.Dock = DockStyle.Fill;
        _previewGrid.ReadOnly = true;
        _previewGrid.AllowUserToAddRows = false;
        _previewGrid.AllowUserToDeleteRows = false;
        _previewGrid.AllowUserToResizeRows = false;
        _previewGrid.RowHeadersVisible = false;
        _previewGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "档位", FillWeight = 18 });
        _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "属性值区间", FillWeight = 42 });
        _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "显示字母", FillWeight = 18 });
        _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明", FillWeight = 32 });

        _maskHint.Dock = DockStyle.Fill;
        _maskHint.ForeColor = Color.DimGray;
        _maskHint.Text = "预览按属性值计算属性档位；人物最终档位 = floor((兵种档位 + 属性档位) / 2)。";
        _maskHint.AutoEllipsis = true;

        previewLayout.Controls.Add(probePanel, 0, 0);
        previewLayout.Controls.Add(_previewGrid, 0, 1);
        previewLayout.Controls.Add(_maskHint, 0, 2);
        previewGroup.Controls.Add(previewLayout);

        var hintGroup = new GroupBox
        {
            Text = "写入说明",
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(8)
        };
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Text = "显示字母最多 2 个 GBK 字节（1 个汉字）：游戏用 8 字节栈缓冲区拼接“旧字母↑新字母”，箭头占 2 字节，超长会写坏栈导致闪退。\r\n" +
                   "只修改 Ekd5.exe：档位函数、显示字母表、档位表指针和全部调用点一起重定位到安全代码洞，写入前自动备份为 Ekd5.exe.bak-tier-*。\r\n" +
                   "首次写入后会留下 TIER1 标记，之后可继续增删改；本工具是静态补丁，未在游戏中实机验证，修改前请退出游戏并保留备份。"
        };
        hintGroup.Controls.Add(hint);

        right.Controls.Add(editGroup, 0, 0);
        right.Controls.Add(previewGroup, 0, 1);
        right.Controls.Add(hintGroup, 0, 2);
        split.Panel2.Controls.Add(right);

        Controls.Add(split);
        Controls.Add(strip);
        KeyDown += OnKeyDown;
    }

    private void FitSplitter()
    {
        if (_splitterMoved || _split == null || _split.Width <= 0) return;
        try { _split.SplitterDistance = Math.Clamp((int)(_split.Width * 0.25), 280, 420); } catch { }
    }

    private void ChooseBase()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择包含 Ekd5.exe 的基底文件夹" };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadBase(dialog.SelectedPath);
    }

    private void LoadBase(string root)
    {
        try
        {
            var profile = TierScanner.Load(root);
            _profile = profile;
            _dirty = false;
            _baseInfo.Text = $"已识别：{profile.KindText} ｜ {new DirectoryInfo(profile.RootPath).Name}";
            _info.Text = BuildInfo(profile);
            _rows.Clear();
            foreach (var entry in profile.Entries) _rows.Add(Clone(entry));
            RebuildGrid(0);
            RefreshTierList(0);
            _maskHint.Text = profile.Kind == TierPathKind.Mask
                ? "6.5 掩码路径：任一条属性命中“强化成长”位时，该属性档位 +2；预览显示的是未命中掩码时的基础属性档位。"
                : "预览按属性值计算属性档位；人物最终档位 = floor((兵种档位 + 属性档位) / 2)，需结合具体兵种。";
            _probe.Value = 0;
            UpdatePreview();
            _write.Enabled = profile.CanWrite;
            _discard.Enabled = profile.CanWrite;
            _add.Enabled = profile.CanWrite;
            _remove.Enabled = profile.CanWrite;
            _up.Enabled = profile.CanWrite;
            _down.Enabled = profile.CanWrite;
            SetStatus(profile.CanWrite
                ? "扫描完成，可以增删改；Ctrl+S 写入。"
                : "只读：" + profile.ReadOnlyReason, !profile.CanWrite);
            _restore.Enabled = true;
        }
        catch (Exception ex)
        {
            _profile = null;
            _rows.Clear();
            _grid.Rows.Clear();
            _tierList.Items.Clear();
            _previewGrid.Rows.Clear();
            _write.Enabled = false;
            _discard.Enabled = false;
            _add.Enabled = false;
            _remove.Enabled = false;
            _up.Enabled = false;
            _down.Enabled = false;
            _baseInfo.Text = "扫描失败。";
            _info.Text = "扫描失败：\r\n" + ex.Message;
            SetStatus("扫描失败：" + ex.Message, true);
            MessageBox.Show(this, ex.Message, "扫描失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string BuildInfo(TierProfile profile)
    {
        var sites = profile.CallSites.Count == 0
            ? "—"
            : string.Join("、", profile.CallSites.Take(6).Select(va => $"0x{va:X8}"))
              + (profile.CallSites.Count > 6 ? $" 等 {profile.CallSites.Count} 处" : "");
        var lines = new List<string>
        {
            $"路径：{profile.RootPath}",
            $"引擎路径：{profile.KindText}",
            $"档位数量：{profile.TierCount} 档（基准返回 {profile.TierBase}，连续编号）",
            $"档位函数：0x{profile.TierFuncVa:X8}" +
                (profile.HasMarker ? $"（原始 0x{profile.OriginalTierFuncVa:X8}，已由本工具重定位）" : ""),
            $"显示指针：0x{profile.DisplayPointerVa:X8} → 表 0x{profile.DisplayTableVa:X8}（索引 +{profile.DisplayIndexOffset}）",
            $"调用点：{sites}",
            $"代码洞：0x{profile.CaveVa:X8} / {profile.CaveLength} 字节",
            $"阈值：{(profile.Thresholds.Count == 0 ? "—" : string.Join(" / ", profile.Thresholds))}",
            $"字母：{string.Join(" / ", profile.Labels)}",
            $"补丁标记：{(profile.HasMarker ? "TIER1（可重复编辑）" : "原始基底")}",
            $"写入：{(profile.CanWrite ? "可写入，自动备份" : profile.ReadOnlyReason)}"
        };
        return string.Join("\r\n", lines);
    }

    private static TierEntry Clone(TierEntry entry) => new()
    {
        Ordinal = entry.Ordinal,
        Threshold = entry.Threshold,
        Label = entry.Label,
        OriginalReturn = entry.OriginalReturn
    };

    private void RebuildGrid(int selectRow)
    {
        _loading = true;
        _grid.Rows.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            string threshold = i == 0 ? "—" : _rows[i].Threshold?.ToString() ?? "";
            int index = _grid.Rows.Add(_rows[i].Ordinal, threshold, _rows[i].Label, RangeText(i));
            _grid.Rows[index].Cells[1].ReadOnly = i == 0;
            if (i == 0) _grid.Rows[index].DefaultCellStyle.BackColor = Color.FromArgb(244, 244, 244);
        }
        _loading = false;
        if (_grid.Rows.Count > 0)
        {
            int row = Math.Clamp(selectRow, 0, _grid.Rows.Count - 1);
            _grid.CurrentCell = _grid.Rows[row].Cells[2];
            _grid.Rows[row].Selected = true;
        }
    }

    private string RangeText(int index)
    {
        if (index == 0)
            return _rows.Count > 1 && _rows[1].Threshold is { } first ? $"0 – {first - 1}" : "—";
        int lower = _rows[index].Threshold ?? 0;
        if (index == _rows.Count - 1) return $"{lower} – 255";
        return _rows[index + 1].Threshold is { } next ? $"{lower} – {next - 1}" : $"{lower} – 255";
    }

    private void RefreshTierList(int selectRow)
    {
        int keep = selectRow;
        if (keep < 0 && _tierList.SelectedIndices.Count > 0) keep = _tierList.SelectedIndices[0];
        _loading = true;
        _tierList.BeginUpdate();
        _tierList.Items.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            var item = new ListViewItem(_rows[i].Ordinal.ToString());
            item.SubItems.Add(i == 0 ? "—" : _rows[i].Threshold?.ToString() ?? "");
            item.SubItems.Add(_rows[i].Label);
            _tierList.Items.Add(item);
        }
        _tierList.EndUpdate();
        if (_tierList.Items.Count > 0)
        {
            int row = Math.Clamp(keep, 0, _tierList.Items.Count - 1);
            _tierList.Items[row].Selected = true;
            _tierList.Items[row].Focused = true;
        }
        _loading = false;
    }

    private void OnListSelectionChanged()
    {
        if (_loading || _grid.Rows.Count == 0 || _tierList.SelectedIndices.Count == 0) return;
        int index = Math.Clamp(_tierList.SelectedIndices[0], 0, _grid.Rows.Count - 1);
        _loading = true;
        _grid.CurrentCell = _grid.Rows[index].Cells[2];
        _grid.Rows[index].Selected = true;
        _loading = false;
    }

    private void OnGridSelectionChanged()
    {
        if (_loading || _grid.CurrentRow == null || _tierList.Items.Count == 0) return;
        int index = Math.Clamp(_grid.CurrentRow.Index, 0, _tierList.Items.Count - 1);
        _loading = true;
        _tierList.Items[index].Selected = true;
        _tierList.Items[index].Focused = true;
        _loading = false;
    }

    private void OnGridEdited()
    {
        if (_loading || _profile == null) return;
        if (!TryReadGrid(out string error))
        {
            _write.Enabled = false;
            SetStatus("输入有误：" + error, true);
            return;
        }
        _dirty = true;
        _write.Enabled = _profile.CanWrite;
        RefreshTierList(-1);
        UpdatePreview();
        SetStatus("有未保存的修改。按 Ctrl+S 或点“写入”。", false);
    }

    private bool TryReadGrid(out string error)
    {
        error = "";
        if (_profile == null)
        {
            error = "尚未扫描基底。";
            return false;
        }
        if (_grid.Rows.Count is < 2 or > 12)
        {
            error = $"档位数量必须在 2..12 之间（当前 {_grid.Rows.Count}）。";
            return false;
        }
        var gbk = Encoding.GetEncoding("GBK");
        var result = new List<TierEntry>();
        int? previous = null;
        for (int i = 0; i < _grid.Rows.Count; i++)
        {
            string label = (_grid.Rows[i].Cells[2].Value?.ToString() ?? "").Trim();
            if (label.Length == 0)
            {
                error = $"第 {i + 1} 行显示字母不能为空。";
                return false;
            }
            if (gbk.GetByteCount(label) > TierProfile.MaxLabelBytes)
            {
                error = $"第 {i + 1} 行显示字母超过 {TierProfile.MaxLabelBytes} 个 GBK 字节（游戏显示缓冲区只有 8 字节）。";
                return false;
            }
            byte? threshold = null;
            if (i > 0)
            {
                string text = (_grid.Rows[i].Cells[1].Value?.ToString() ?? "").Trim();
                if (!byte.TryParse(text, out byte value))
                {
                    error = $"第 {i + 1} 行阈值必须是 0..255 的整数。";
                    return false;
                }
                if (previous is { } last && value <= last)
                {
                    error = $"第 {i + 1} 行阈值必须大于上一行（当前 {value} ≤ {last}）。";
                    return false;
                }
                threshold = value;
                previous = value;
            }
            result.Add(new TierEntry
            {
                Ordinal = _profile.TierBase + i,
                Threshold = threshold,
                Label = label
            });
        }
        _rows.Clear();
        _rows.AddRange(result);
        return true;
    }

    private void AddTier()
    {
        if (_profile == null) return;
        if (!TryReadGrid(out string error))
        {
            MessageBox.Show(this, error, "无法添加", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_rows.Count >= 12)
        {
            MessageBox.Show(this, "最多支持 12 档。", "无法添加", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int insertAt = _grid.CurrentRow?.Index ?? _rows.Count - 1;
        insertAt = Math.Clamp(insertAt, 1, _rows.Count);
        byte? threshold;
        if (insertAt >= _rows.Count)
        {
            int last = _rows[^1].Threshold ?? 0;
            if (last >= 250)
            {
                MessageBox.Show(this, "最后一个阈值已经接近 255，没有空间追加新档位。请先调低阈值。",
                    "无法添加", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            threshold = (byte)Math.Min(255, last + 5);
        }
        else
        {
            int lower = _rows[insertAt - 1].Threshold ?? 0;
            int upper = _rows[insertAt].Threshold ?? 255;
            if (upper - lower >= 2)
            {
                threshold = (byte)(lower + (upper - lower) / 2);
            }
            else
            {
                int last = _rows[^1].Threshold ?? 0;
                if (last >= 250)
                {
                    MessageBox.Show(this, "相邻阈值之间没有空隙，末尾也没有空间。请先调整阈值。",
                        "无法添加", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                insertAt = _rows.Count;
                threshold = (byte)Math.Min(255, last + 5);
            }
        }
        _rows.Insert(insertAt, new TierEntry { Threshold = threshold, Label = "?" });
        NormalizeOrdinals();
        RebuildGrid(insertAt);
        RefreshTierList(insertAt);
        MarkDirty();
    }

    private void RemoveTier()
    {
        if (_profile == null) return;
        if (!TryReadGrid(out string error))
        {
            MessageBox.Show(this, error, "无法删除", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        int index = _grid.CurrentRow?.Index ?? -1;
        if (index <= 0)
        {
            MessageBox.Show(this, "基准档（第一行）不能删除。", "无法删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_rows.Count <= 2)
        {
            MessageBox.Show(this, "至少保留 2 档。", "无法删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _rows.RemoveAt(index);
        NormalizeOrdinals();
        int select = Math.Min(index, _rows.Count - 1);
        RebuildGrid(select);
        RefreshTierList(select);
        MarkDirty();
    }

    private void MoveLabel(int delta)
    {
        if (_profile == null) return;
        if (!TryReadGrid(out string error))
        {
            MessageBox.Show(this, error, "无法移动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        int index = _grid.CurrentRow?.Index ?? -1;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _rows.Count) return;
        (_rows[index].Label, _rows[target].Label) = (_rows[target].Label, _rows[index].Label);
        RebuildGrid(target);
        RefreshTierList(target);
        MarkDirty();
    }

    private void NormalizeOrdinals()
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Ordinal = _profile!.TierBase + i;
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdatePreview();
        SetStatus("有未保存的修改。按 Ctrl+S 或点“写入”。", false);
    }

    private void UpdatePreview()
    {
        _previewGrid.Rows.Clear();
        if (_profile == null) return;
        _previewGrid.SuspendLayout();
        for (int i = 0; i < _rows.Count; i++)
        {
            string note = i == 0 ? "基准档（属性值低于第一阈值）" : i == _rows.Count - 1 ? "最高档" : "达到阈值即进入";
            int index = _previewGrid.Rows.Add(_rows[i].Ordinal, RangeText(i), _rows[i].Label, note);
            if (i == 0) _previewGrid.Rows[index].DefaultCellStyle.BackColor = Color.FromArgb(244, 244, 244);
        }
        _previewGrid.ResumeLayout();
        UpdateProbe();
    }

    private void UpdateProbe()
    {
        if (_rows.Count == 0)
        {
            _probeResult.Text = "";
            return;
        }
        int value = (int)_probe.Value;
        int index = 0;
        for (int i = 1; i < _rows.Count; i++)
            if (_rows[i].Threshold is { } threshold && value >= threshold) index = i;
        _probeResult.Text = $"属性档位：{_rows[index].Ordinal}    显示字母：{_rows[index].Label}";
    }

    private void WriteRows()
    {
        if (_profile == null) return;
        _grid.EndEdit();
        _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        if (!TryReadGrid(out string error))
        {
            MessageBox.Show(this, error, "无法写入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_profile.CanWrite)
        {
            MessageBox.Show(this, _profile.ReadOnlyReason, "只读", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_dirty)
        {
            SetStatus("没有需要写入的修改。", false);
            return;
        }
        string thresholds = string.Join(" / ", _rows.Skip(1).Select(row => row.Threshold?.ToString() ?? ""));
        string labels = string.Join(" / ", _rows.Select(row => row.Label));
        var confirm = MessageBox.Show(this,
            $"将修改：{_profile.ExePath}\r\n\r\n档位：{_rows.Count} 档\r\n阈值：{thresholds}\r\n字母：{labels}\r\n\r\n" +
            "写入前会自动备份为 Ekd5.exe.bak-tier-*。\r\n" +
            "这是静态补丁，尚未实机验证；请先退出游戏。\r\n\r\n继续写入？",
            "确认写入", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        var result = TierWriter.Apply(_profile, _rows);
        MessageBox.Show(this, result.Message, result.Success ? "写入成功" : "写入失败",
            MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        if (result.Success)
        {
            _dirty = false;
            LoadBase(_profile.RootPath);
        }
        SetStatus(result.Message, !result.Success);
    }

    private void Discard()
    {
        if (_profile == null) return;
        _rows.Clear();
        foreach (var entry in _profile.Entries) _rows.Add(Clone(entry));
        _dirty = false;
        RebuildGrid(0);
        RefreshTierList(0);
        UpdatePreview();
        SetStatus("已放弃未写入的修改。", false);
    }

    private void RestoreBackup()
    {
        if (_profile == null) return;
        string directory = Path.GetDirectoryName(_profile.ExePath)!;
        var backups = Directory.GetFiles(directory, "Ekd5.exe.bak-tier-*")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .ToArray();
        if (backups.Length == 0)
        {
            MessageBox.Show(this, "这个基底目录里没有 Ekd5.exe.bak-tier-* 备份。", "恢复备份",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new Form
        {
            Text = "恢复备份",
            Font = Font,
            Width = 720,
            Height = 430,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedDialog
        };
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        foreach (string backup in backups) list.Items.Add(Path.GetFileName(backup));
        list.SelectedIndex = 0;
        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Text = "选择要恢复的 Ekd5.exe 备份。恢复前会先为当前文件再保存一份备份。",
            ForeColor = Color.DimGray,
            Padding = new Padding(8, 6, 8, 0)
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8, 7, 8, 0)
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
        var ok = new Button { Text = "恢复选中", DialogResult = DialogResult.OK, Width = 100, Height = 28 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        dialog.Controls.Add(list);
        dialog.Controls.Add(hint);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0) return;

        string selected = backups[list.SelectedIndex];
        if (MessageBox.Show(this,
                $"要用下面的备份覆盖当前 Ekd5.exe 吗？\r\n\r\n{Path.GetFileName(selected)}\r\n\r\n当前文件会先另存为新的备份。",
                "确认恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string safety = _profile.ExePath + ".bak-tier-" + stamp + "-before-restore";
            File.Copy(_profile.ExePath, safety, overwrite: false);
            File.Copy(selected, _profile.ExePath, overwrite: true);
            string root = _profile.RootPath;
            LoadBase(root);
            SetStatus("已恢复备份：" + Path.GetFileName(selected) + "（当前文件另存为 " + Path.GetFileName(safety) + "）", false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("恢复失败：" + ex.Message, true);
        }
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.S)
        {
            WriteRows();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_dirty) return;
        var result = MessageBox.Show(this, "有未写入的档位修改，确定关闭吗？", "档位管理器",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes) e.Cancel = true;
    }
}
