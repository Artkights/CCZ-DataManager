using Ccz.ManagerUi;

namespace DataManager.App;

public sealed partial class MainForm
{
    private ResponsiveHeaderPanel? _responsiveHeader;
    internal long HeaderLayoutPassesForSmokeTest => _responsiveHeader?.LayoutPasses ?? 0;

    private Control BuildResponsiveQuerySettingsStrip()
    {
        var settings = new ResponsiveSettingsPanel { Name = "ResponsiveSettingsRow" };
        settings.AddSetting("SettingsVersionGroup", "版本", _profileSelector, 120, 20);
        settings.AddSetting("SettingsVariantGroup", "布局", _variantSelector, 155, 25);
        _hideEmpty.Name = "SettingsHideEmpty";
        _hideEmpty.AutoSize = true;
        _hideEmpty.Margin = Padding.Empty;
        settings.AddNatural(_hideEmpty);
        settings.AddSetting("SettingsQuickLocateGroup", "快速定位", _quickLookup, 145, 25);
        settings.AddSetting("SettingsTableGroup", "表", _tableSelector, 210, 30);
        _tableSelector.DropDownStyle = ComboBoxStyle.DropDownList;

        var pairs = new ResponsiveQueryPair[3];
        for (var index = 0; index < pairs.Length; index++)
        {
            pairs[index] = new ResponsiveQueryPair(_queryFields[index], _queryInputs[index])
                { Name = $"RecordQueryPair{index + 1}" };
            _shellTips.SetToolTip(_queryFields[index], "选择当前详情子页的属性；全部属性包含 ID：名称及该子页的所有记录属性。");
            _shellTips.SetToolTip(_queryInputs[index], "空格分词，框内各词和三组条件全部满足。按回车或点击查询后应用。");
            _queryFields[index].SelectedIndexChanged += (_, _) => RememberQueryInputs();
            _queryFields[index].FontChanged += (sender, _) => UpdateQueryDropDownHeight((ComboBox)sender!);
            _queryFields[index].DpiChangedAfterParent += (sender, _) => UpdateQueryDropDownHeight((ComboBox)sender!);
            _queryFields[index].DropDown += (sender, _) => UpdateQueryDropDownHeight((ComboBox)sender!);
            _queryInputs[index].TextChanged += (_, _) => RememberQueryInputs();
            _queryInputs[index].KeyDown += (_, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                args.Handled = args.SuppressKeyPress = true;
                SubmitRecordQuery();
            };
        }
        var actions = new ResponsiveQueryActions(_queryButton, _clearQueryButton, _querySummary)
            { Name = "RecordQueryActions" };
        var queries = new ResponsiveQueryPanel(pairs, actions) { Name = "ResponsiveQueryRows" };
        _queryGroup.AutoSize = false;
        _queryGroup.Dock = DockStyle.None;
        _queryGroup.Padding = _queryGroup.Margin = Padding.Empty;
        _queryGroup.WrapContents = false;
        _queryGroup.Controls.Add(queries);
        _queryButton.Click += (_, _) => SubmitRecordQuery();
        _clearQueryButton.Click += (_, _) => ClearRecordQuery();

        _responsiveHeader = new ResponsiveHeaderPanel(settings, _queryGroup, queries)
        {
            Name = "SettingsAndQuery", Dock = DockStyle.Top,
            BackColor = Color.FromArgb(245, 247, 250), Margin = Padding.Empty
        };
        return _responsiveHeader;
    }

    /// <summary>
    /// Uses stable native parents and assigns only changed bounds. A queued pass
    /// handles inherited font/DPI changes after the native preferred heights settle.
    /// </summary>
    private sealed class ResponsiveHeaderPanel : Panel
    {
        private readonly ResponsiveSettingsPanel _settings;
        private readonly FlowLayoutPanel _queryWrapper;
        private readonly ResponsiveQueryPanel _queries;
        private readonly LayoutScheduler _scheduler;
        private bool _arranging;
        public long LayoutPasses { get; private set; }

        public ResponsiveHeaderPanel(ResponsiveSettingsPanel settings, FlowLayoutPanel queryWrapper, ResponsiveQueryPanel queries)
        {
            _settings = settings; _queryWrapper = queryWrapper; _queries = queries;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Controls.Add(settings); Controls.Add(queryWrapper);
            _scheduler = new LayoutScheduler(this, PerformLayout);
            settings.ContentChanged += (_, _) => _scheduler.Request();
            queryWrapper.VisibleChanged += (_, _) => _scheduler.Request();
            queries.ContentChanged += (_, _) => _scheduler.Request();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            if (_arranging || IsDisposed || Disposing) return;
            using var timing = RuntimeTiming.Measure("Header.Layout");
            _arranging = true;
            SuspendLayout();
            try
            {
                var inset = HeaderPixels(this, 8);
                var vertical = HeaderPixels(this, 2);
                var width = Math.Max(0, ClientSize.Width - inset * 2);
                var settingsHeight = _settings.ArrangeForWidth(width);
                HeaderBounds(_settings, inset, vertical, width, settingsHeight);
                var queryHeight = 0;
                if (_queryWrapper.Visible)
                {
                    queryHeight = _queries.ArrangeForWidth(width);
                    HeaderBounds(_queryWrapper, inset, vertical + settingsHeight, width, queryHeight);
                    HeaderBounds(_queries, 0, 0, width, queryHeight);
                }
                var height = Math.Max(1, vertical * 2 + settingsHeight + queryHeight);
                if (Height != height) Height = height;
                LayoutPasses++;
            }
            finally { ResumeLayout(false); _arranging = false; }
        }
    }

    private static int HeaderPixels(Control owner, int logical)
        => Math.Max(1, (int)Math.Round(logical * owner.DeviceDpi / 96d));

    private static void HeaderBounds(Control control, int x, int y, int width, int height)
    {
        var rectangle = new Rectangle(x, y, Math.Max(0, width), Math.Max(0, height));
        if (control.Bounds == rectangle) return;
        using var timing = RuntimeTiming.Measure("Header.BoundsWrite");
        control.Bounds = rectangle;
    }

    // Keep the normal preferred-size behavior while diagnostics distinguish
    // native ComboBox measurement from layout caused by an item/selection change.
    // RuntimeTiming creates no clock or measurement when its sink is disabled.
    private static Size HeaderPreferredSize(Control control)
    {
        using var timing = RuntimeTiming.Measure(control switch
        {
            ComboBox => "Header.ComboPreferredSize", Button => "Header.ButtonPreferredSize",
            Label => "Header.LabelPreferredSize", _ => "Header.OtherPreferredSize"
        });
        return control.GetPreferredSize(Size.Empty);
    }

    private sealed class ResponsiveSettingGroup : Panel
    {
        private readonly Label _caption;
        private readonly int _minimumEditorDip;
        public Control Editor { get; }
        public int Weight { get; }
        public bool HasVisibleEditor => Editor.Visible;

        public ResponsiveSettingGroup(string caption, Control editor, int minimumEditorDip, int weight)
        {
            _minimumEditorDip = minimumEditorDip; Weight = weight; Editor = editor;
            Margin = Padding.Empty;
            _caption = new Label { Text = caption, AutoSize = false, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft };
            editor.Dock = DockStyle.None; editor.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            editor.Margin = Padding.Empty;
            Controls.Add(_caption); Controls.Add(editor);
        }

        private Size CaptionSize => TextRenderer.MeasureText(_caption.Text, _caption.Font,
            Size.Empty, TextFormatFlags.SingleLine);

        public int MinimumLayoutWidth => CaptionSize.Width + HeaderPixels(this, 4) + HeaderPixels(this, _minimumEditorDip);
        public int PreferredLayoutHeight => Math.Max(CaptionSize.Height, HeaderPreferredSize(Editor).Height) + HeaderPixels(this, 6);

        protected override void OnLayout(LayoutEventArgs levent)
        {
            using var timing = RuntimeTiming.Measure("Header.SettingGroupLayout");
            var caption = CaptionSize;
            var editorHeight = HeaderPreferredSize(Editor).Height;
            var gap = HeaderPixels(this, 4);
            var top = HeaderPixels(this, 3);
            var contentHeight = Math.Max(caption.Height, editorHeight);
            HeaderBounds(_caption, 0, top + (contentHeight - caption.Height) / 2, caption.Width, caption.Height);
            HeaderBounds(Editor, caption.Width + gap, top, ClientSize.Width - caption.Width - gap, editorHeight);
        }
    }

    private sealed class ResponsiveSettingsPanel : Panel
    {
        private sealed record Entry(Control Owner, Control VisibilitySource, int Weight);
        private readonly List<Entry> _entries = [];
        public event EventHandler? ContentChanged;

        public void AddSetting(string name, string caption, Control editor, int minimumEditorDip, int weight)
        {
            var group = new ResponsiveSettingGroup(caption, editor, minimumEditorDip, weight) { Name = name };
            _entries.Add(new(group, editor, weight)); Controls.Add(group);
            editor.VisibleChanged += (_, _) => ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AddNatural(Control control)
        {
            _entries.Add(new(control, control, 0)); Controls.Add(control);
            control.VisibleChanged += (_, _) => ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private static int MinimumWidth(Entry entry) => entry.Owner is ResponsiveSettingGroup group
            ? group.MinimumLayoutWidth : HeaderPreferredSize(entry.Owner).Width;
        private static int EntryHeight(Entry entry) => entry.Owner is ResponsiveSettingGroup group
            ? group.PreferredLayoutHeight : HeaderPreferredSize(entry.Owner).Height;

        public int ArrangeForWidth(int width)
        {
            using var timing = RuntimeTiming.Measure("Header.SettingsArrange");
            var gap = HeaderPixels(this, 8);
            var visible = _entries.Where(entry => entry.VisibilitySource.Visible).ToArray();
            foreach (var entry in _entries.Except(visible)) HeaderBounds(entry.Owner, 0, 0, 0, 0);
            var y = 0;
            for (var first = 0; first < visible.Length;)
            {
                var end = first;
                var occupied = 0;
                while (end < visible.Length)
                {
                    var next = MinimumWidth(visible[end]);
                    if (end > first && occupied + gap + next > width) break;
                    occupied += (end == first ? 0 : gap) + next;
                    end++;
                }
                var row = visible[first..end];
                var rowHeight = row.Max(EntryHeight);
                var totalWeight = row.Sum(entry => entry.Weight);
                var remaining = Math.Max(0, width - occupied);
                var allocated = 0;
                var x = 0;
                var lastElastic = Array.FindLastIndex(row, entry => entry.Weight > 0);
                for (var index = 0; index < row.Length; index++)
                {
                    var entry = row[index];
                    var extra = totalWeight == 0 || entry.Weight == 0 ? 0 : index == lastElastic
                        ? remaining - allocated : remaining * entry.Weight / totalWeight;
                    allocated += extra;
                    var entryWidth = Math.Min(Math.Max(0, width - x), MinimumWidth(entry) + extra);
                    var height = EntryHeight(entry);
                    HeaderBounds(entry.Owner, x, y + (rowHeight - height) / 2, entryWidth, height);
                    x += entryWidth + gap;
                }
                y += rowHeight;
                first = end;
            }
            return y;
        }
    }

    private sealed class ResponsiveQueryPair : Panel
    {
        private readonly ComboBox _field;
        private readonly TextBox _input;
        public ResponsiveQueryPair(ComboBox field, TextBox input)
        {
            _field = field; _input = input;
            Margin = Padding.Empty;
            field.Dock = input.Dock = DockStyle.None;
            field.Margin = input.Margin = Padding.Empty;
            Controls.Add(field); Controls.Add(input);
        }

        private int MinimumFieldWidth => Math.Max(HeaderPixels(this, 112),
            TextRenderer.MeasureText("全部属性", _field.Font, Size.Empty, TextFormatFlags.SingleLine).Width + SystemInformation.VerticalScrollBarWidth + HeaderPixels(this, 8));
        private int MinimumInputWidth => Math.Max(HeaderPixels(this, 128),
            TextRenderer.MeasureText("关键词（空格分词）", _input.Font, Size.Empty, TextFormatFlags.SingleLine).Width + HeaderPixels(this, 8));
        public int MinimumLayoutWidth => MinimumFieldWidth + HeaderPixels(this, 4) + MinimumInputWidth;
        public int PreferredLayoutHeight => Math.Max(HeaderPreferredSize(_field).Height, _input.PreferredHeight) + HeaderPixels(this, 6);

        protected override void OnLayout(LayoutEventArgs levent)
        {
            using var timing = RuntimeTiming.Measure("Header.QueryPairLayout");
            var gap = HeaderPixels(this, 4);
            var available = Math.Max(0, ClientSize.Width - gap);
            var fieldWidth = (int)Math.Round(available * .4);
            if (available >= MinimumFieldWidth + MinimumInputWidth)
                fieldWidth = Math.Clamp(fieldWidth, MinimumFieldWidth, available - MinimumInputWidth);
            var top = HeaderPixels(this, 3);
            HeaderBounds(_field, 0, top, fieldWidth, HeaderPreferredSize(_field).Height);
            HeaderBounds(_input, fieldWidth + gap, top, available - fieldWidth, _input.PreferredHeight);
        }
    }

    private sealed class ResponsiveQueryActions : Panel
    {
        private readonly Control[] _actions;
        public event EventHandler? ContentChanged;
        public ResponsiveQueryActions(Button query, Button clear, Label summary)
        {
            Margin = Padding.Empty;
            _actions = [query, clear, summary];
            foreach (var action in _actions)
            {
                action.Margin = Padding.Empty; action.Dock = DockStyle.None;
                Controls.Add(action);
            }
            summary.TextChanged += (_, _) => ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        public int PreferredLayoutWidth => _actions.Where(action => action.Visible).Sum(action => HeaderPreferredSize(action).Width)
            + Math.Max(0, _actions.Count(action => action.Visible) - 1) * HeaderPixels(this, 6);
        public int PreferredLayoutHeight => _actions.Where(action => action.Visible).Select(action => HeaderPreferredSize(action).Height)
            .DefaultIfEmpty(0).Max() + HeaderPixels(this, 6);

        protected override void OnLayout(LayoutEventArgs levent)
        {
            using var timing = RuntimeTiming.Measure("Header.QueryActionsLayout");
            var gap = HeaderPixels(this, 6);
            var contentHeight = PreferredLayoutHeight - HeaderPixels(this, 6);
            var x = 0;
            foreach (var action in _actions.Where(action => action.Visible))
            {
                var size = HeaderPreferredSize(action);
                HeaderBounds(action, x, HeaderPixels(this, 3) + (contentHeight - size.Height) / 2, size.Width, size.Height);
                x += size.Width + gap;
            }
        }
    }

    private sealed class ResponsiveQueryPanel : Panel
    {
        private readonly ResponsiveQueryPair[] _pairs;
        private readonly ResponsiveQueryActions _actions;
        public event EventHandler? ContentChanged;
        public ResponsiveQueryPanel(ResponsiveQueryPair[] pairs, ResponsiveQueryActions actions)
        {
            _pairs = pairs; _actions = actions; Margin = Padding.Empty;
            Controls.AddRange(pairs); Controls.Add(actions);
            actions.ContentChanged += (_, _) => ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        public int ArrangeForWidth(int width)
        {
            using var timing = RuntimeTiming.Measure("Header.QueryArrange");
            var gap = HeaderPixels(this, 8);
            var minimum = _pairs.Max(pair => pair.MinimumLayoutWidth);
            var actionWidth = Math.Min(width, _actions.PreferredLayoutWidth);
            var rowHeight = _pairs.Max(pair => pair.PreferredLayoutHeight);
            var oneLine = width >= minimum * 3 + actionWidth + gap * 3;
            var columns = oneLine ? 3 : Math.Clamp((width + gap) / Math.Max(1, minimum + gap), 1, 3);
            var y = 0;
            for (var first = 0; first < _pairs.Length; first += columns)
            {
                var count = Math.Min(columns, _pairs.Length - first);
                var rowWidth = oneLine ? width - actionWidth - gap : width;
                var available = Math.Max(0, rowWidth - gap * (count - 1));
                var x = 0;
                for (var index = 0; index < count; index++)
                {
                    var end = available * (index + 1) / count;
                    var start = available * index / count;
                    HeaderBounds(_pairs[first + index], x, y, end - start, rowHeight);
                    x += end - start + gap;
                }
                y += rowHeight;
            }
            var actionsHeight = _actions.PreferredLayoutHeight;
            if (oneLine)
            {
                HeaderBounds(_actions, width - actionWidth, 0, actionWidth, actionsHeight);
                return Math.Max(y, actionsHeight);
            }
            HeaderBounds(_actions, width - actionWidth, y, actionWidth, actionsHeight);
            return y + actionsHeight;
        }
    }
}
