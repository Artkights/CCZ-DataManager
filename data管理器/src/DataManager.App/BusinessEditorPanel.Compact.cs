using System.Data;
using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private readonly List<Action> _entityRebinders = [];
    private BusinessPageKind _compactLayoutKind;
    private string? _compactLayoutSignature;
    private IReadOnlyList<string> _compactTableKeys = [];
    private static readonly string[] JobAttributeNames =
        ["移动声音", "移动速度", "攻击声音", "远程兵种", "攻击延迟", "兵种类型", "策略伤害", "参与围攻"];
    private sealed record FieldBinding(TableDocument Document, DataRow Row, int RowIndex, TableFieldDefinition Field, string? Label = null);

    private string CompactLayoutSignature(BusinessPageKind kind, int id)
    {
        var signature = string.Join('|', _compactTableKeys.Where(_workspace.Documents.ContainsKey).Select(key => _workspace.GetDocument(key))
            .Where(document => HasCompactLayoutSource(kind, id, document))
            .Select(document => $"{document.Definition.Key}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(document)}"));
        if (kind == BusinessPageKind.Items)
        {
            foreach (var key in new[] { "items-low", "items-high", "items-extended" })
            {
                if (!_workspace.Documents.TryGetValue(key, out var document) || FindRow(document, id) is not { } match) continue;
                var semantics = ItemTypeService.Resolve(document, match.Index);
                signature += $"|{semantics.Category}:{semantics.IsCategoryMarker}";
                break;
            }
        }
        return signature;
    }

    private bool HasCompactLayoutSource(BusinessPageKind kind, int id, TableDocument document)
    {
        var key = document.Definition.Key;
        // These documents supply shared names, global masks or transposed
        // associations. Their row IDs do not identify the current entity.
        if (key is "person-critical-quotes" or "person-retreat-quotes" or "person-critical-special-map" or
            "strategy-terrain-availability" or "equipment-type-names" or "equipment-type-display" || key.StartsWith("item-effect-names", StringComparison.Ordinal) ||
            kind == BusinessPageKind.Items && key == "detailed-job-growth" ||
            kind == BusinessPageKind.DetailedJobs && key == "strategies") return true;
        // Job attributes have eight rows and one field per job series.
        if (kind == BusinessPageKind.JobSeries && key == "job-series-attributes")
            return document.Definition.Fields.Any(field => field.Name == $"兵种系 {id}");
        return FindRow(document, id) != null;
    }

    private bool TryRebindCompactEntity(BusinessPageKind kind, int id)
    {
        if (_compactLayoutSignature == null || _compactLayoutKind != kind ||
            CompactLayoutSignature(kind, id) != _compactLayoutSignature) return false;
        _kind = kind; _id = id;
        BeginPreviewBinding(true);
        _errors.Clear();
        _status.Visible = false;
        _title.Text = $"{id}：{EntityName(kind, id)}";
        _status.Text = "编辑后进入草稿；保存时统一提交。";
        foreach (var rebind in _entityRebinders.ToArray()) rebind();
        _accessoryDraftRefresh?.Invoke();
        RefreshEquipmentTypeBindings();
        _refreshItemPermissions?.Invoke();
        RefreshCompactPreviews();
        return true;
    }

    private FieldBinding? ResolveEditorBinding(string key, string fieldName, int originalPhysicalRow)
    {
        if (!_workspace.Documents.TryGetValue(key, out var document)) return null;
        if (key == "job-series-attributes")
        {
            var attribute = document.Definition.Fields.FirstOrDefault(field => field.Name == $"兵种系 {_id}");
            return attribute == null || originalPhysicalRow >= document.Data.Rows.Count ? null
                : new(document, document.Data.Rows[originalPhysicalRow], originalPhysicalRow, attribute);
        }
        if (key == "strategy-terrain-availability")
            return originalPhysicalRow >= document.Data.Rows.Count ? null
                : new(document, document.Data.Rows[originalPhysicalRow], originalPhysicalRow, document.Definition.Fields[0]);
        var match = FindRow(document, _id);
        var selectedField = document.Definition.Fields.FirstOrDefault(field => field.Name == fieldName);
        return match == null || selectedField == null ? null : new(document, match.Value.Row, match.Value.Index, selectedField);
    }

    private string EntityName(BusinessPageKind kind, int id)
    {
        var keys = kind switch
        {
            BusinessPageKind.Persons => new[] { "persons" },
            BusinessPageKind.JobSeries => ["job-series"],
            BusinessPageKind.DetailedJobs => ["detailed-jobs"],
            BusinessPageKind.Items => ["items-low", "items-high", "items-extended"],
            BusinessPageKind.Strategies => ["strategies"],
            BusinessPageKind.Shop => ["scenario-names"],
            _ => Array.Empty<string>()
        };
        return keys.Where(_workspace.Documents.ContainsKey).SelectMany(key => _lookup.Options(_workspace.GetDocument(key)))
            .FirstOrDefault(option => option.Id == id)?.Name ?? string.Empty;
    }

    private bool BuildCompactEntity(BusinessPageKind kind, int id)
    {
        switch (kind)
        {
            case BusinessPageKind.Persons: BuildPersonPage(); return true;
            case BusinessPageKind.JobSeries: BuildJobSeriesPage(); return true;
            case BusinessPageKind.DetailedJobs: BuildJobPage(); return true;
            case BusinessPageKind.Items: BuildItemPage(); return true;
            case BusinessPageKind.Strategies: BuildStrategyPage(); return true;
            case BusinessPageKind.Shop: BuildShopPage(); return true;
            default: return false;
        }
    }

    private IEnumerable<FieldBinding> Fields(string key)
    {
        if (!_workspace.Documents.TryGetValue(key, out var document) || FindRow(document, _id) is not { } match) yield break;
        foreach (var field in document.Definition.Fields)
            yield return new(document, match.Row, match.Index, field);
    }

    private TabPage DetailPage(string title)
    {
        var page = NewTab(title);
        page.AutoScroll = false;
        page.Padding = new Padding(4);
        return page;
    }

    private static TableLayoutPanel Columns(params float[] widths)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = widths.Length, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        foreach (var width in widths) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return table;
    }

    private static TableLayoutPanel Rows(params (SizeType Type, float Height)[] heights)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length, Margin = Padding.Empty, Padding = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (type, height) in heights) table.RowStyles.Add(new RowStyle(type, height));
        return table;
    }

    private static Control SectionContainer(string title)
        => new Panel { Dock = DockStyle.Fill, AccessibleName = title,
            Padding = new Padding(6, 3, 6, 4), Margin = new Padding(3) };

    private static Action AttachScrollableContent(Control host, Control content, Func<Size> minimumSize)
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true,
            BorderStyle = BorderStyle.None, Padding = Padding.Empty, Margin = Padding.Empty };
        content.Dock = DockStyle.None; content.Location = Point.Empty;
        viewport.Controls.Add(content); host.Controls.Add(viewport);
        var arranging = false;
        var queued = false;
        var handleGeneration = 0;
        bool IsReleasing()
        {
            if (content.IsDisposed || content.Disposing || viewport.IsDisposed || viewport.Disposing) return true;
            for (Control? ancestor = host; ancestor != null; ancestor = ancestor.Parent)
                if (ancestor.IsDisposed || ancestor.Disposing ||
                    ancestor is BusinessEditorPanel panel && (panel._disposingDetailPages || panel._releaseCompleted)) return true;
            return false;
        }
        void Arrange()
        {
            if (arranging || IsReleasing()) return;
            arranging = true;
            try
            {
                var minimum = minimumSize();
                // Solve both scrollbars from the borderless viewport's full
                // bounds. Using its current ClientSize makes the content grow
                // and shrink with a scrollbar while Windows is changing that
                // scrollbar, which can keep posting layout work indefinitely.
                var vertical = false;
                var horizontal = false;
                for (var pass = 0; pass < 3; pass++)
                {
                    horizontal = minimum.Width > Math.Max(0, viewport.Width - (vertical ? SystemInformation.VerticalScrollBarWidth : 0));
                    vertical = minimum.Height > Math.Max(0, viewport.Height - (horizontal ? SystemInformation.HorizontalScrollBarHeight : 0));
                }
                var size = new Size(Math.Max(minimum.Width, viewport.Width - (vertical ? SystemInformation.VerticalScrollBarWidth : 0)),
                    Math.Max(minimum.Height, viewport.Height - (horizontal ? SystemInformation.HorizontalScrollBarHeight : 0)));
                if (viewport.AutoScrollMinSize != minimum || content.Size != size)
                {
                    viewport.SuspendLayout();
                    try { viewport.AutoScrollMinSize = minimum; content.Size = size; }
                    finally { viewport.ResumeLayout(true); }
                }
            }
            finally { arranging = false; }
        }
        void QueueArrange()
        {
            if (queued || IsReleasing() || !viewport.IsHandleCreated) return;
            queued = true;
            var generation = handleGeneration;
            viewport.BeginInvoke(() =>
            {
                if (generation != handleGeneration) return;
                queued = false;
                if (IsReleasing() || !viewport.IsHandleCreated) return;
                Arrange();
            });
        }
        viewport.HandleDestroyed += (_, _) => { handleGeneration++; queued = false; };
        viewport.SizeChanged += (_, _) => QueueArrange();
        viewport.DpiChangedAfterParent += (_, _) => { Arrange(); QueueArrange(); };
        content.FontChanged += (_, _) => { Arrange(); QueueArrange(); };
        content.HandleCreated += (_, _) => { Arrange(); QueueArrange(); };
        viewport.VisibleChanged += (_, _) => { if (viewport.Visible) { Arrange(); QueueArrange(); } };
        Arrange();
        return Arrange;
    }

    private static int[] FieldRowMinimums(TableLayoutPanel table) => Enumerable.Range(0, table.RowCount)
        .Select(row => Math.Max(Math.Max(table.Font.Height + 14, 30 * table.DeviceDpi / 96),
            table.Controls.Cast<Control>().Where(control => table.GetRow(control) == row)
                .Select(control => control.MinimumSize.Height + control.Margin.Vertical).DefaultIfEmpty(0).Max())).ToArray();

    private static void FitFieldRows(TableLayoutPanel table)
    {
        var minimums = FieldRowMinimums(table);
        var spare = Math.Max(0, table.ClientSize.Height - minimums.Sum());
        while (table.RowStyles.Count < minimums.Length) table.RowStyles.Add(new RowStyle());
        for (var row = 0; row < minimums.Length; row++)
        {
            table.RowStyles[row].SizeType = SizeType.Absolute;
            table.RowStyles[row].Height = minimums[row] + spare / minimums.Length + (row < spare % minimums.Length ? 1 : 0);
        }
    }

    private Control FieldsBlock(string title, IEnumerable<FieldBinding> source, int columns = 2, int labelWidth = 88, int textHeight = 64)
    {
        var group = SectionContainer(title);
        var fields = source.ToArray();
        var body = Columns(Enumerable.Repeat(100f / columns, columns).ToArray());
        var columnMinimums = new Size[columns];
        var arrange = AttachScrollableContent(group, body, () => new Size(columnMinimums.Sum(size => size.Width), columnMinimums.Select(size => size.Height).DefaultIfEmpty(0).Max()));
        var perColumn = Math.Max(1, (fields.Length + columns - 1) / columns);
        for (var column = 0; column < columns; column++)
        {
            var columnIndex = column;
            var columnFields = fields.Skip(column * perColumn).Take(perColumn).ToArray();
            var labels = columnFields.Select(binding => binding.Label ?? ItemFieldLabel(binding.Document, binding.RowIndex, binding.Field.Name)
                ?? FieldLabel(binding.Document.Definition.Key, binding.Field.Name)).ToArray();
            var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = perColumn,
                Margin = new Padding(4, 0, 8, 0), Padding = Padding.Empty };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var editorFitters = new List<Action>();
            (Font Font, int Dpi)? measuredFor = null;
            var fitting = false;
            void FitLabelColumn()
            {
                if (fitting) return;
                fitting = true;
                try
                {
                // Controls are built before inheriting the MainForm font/DPI.
                // Measure again after parenting so HP/MP and four-character
                // captions do not wrap into a second line at 120/144 DPI.
                if (measuredFor is not { } previous || !previous.Font.Equals(form.Font) || previous.Dpi != form.DeviceDpi)
                {
                    var measured = labels.Select(label => TextRenderer.MeasureText(label, form.Font, Size.Empty,
                            TextFormatFlags.SingleLine).Width + 8).DefaultIfEmpty(labelWidth).Max();
                    var width = Math.Max(labelWidth * form.DeviceDpi / 96f, measured);
                    if (form.ColumnStyles[0].Width != width) form.ColumnStyles[0].Width = width;
                    measuredFor = (form.Font, form.DeviceDpi);
                }
                foreach (var fit in editorFitters) fit();
                var editorMinimum = TextRenderer.MeasureText("255：空物品槽", form.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + SystemInformation.VerticalScrollBarWidth + 12;
                columnMinimums[columnIndex] = new Size((int)form.ColumnStyles[0].Width + editorMinimum + form.Margin.Horizontal + 8,
                    FieldRowMinimums(form).Sum());
                FitFieldRows(form);
                arrange();
                }
                finally { fitting = false; }
            }
            form.SizeChanged += (_, _) => FitLabelColumn();
            form.FontChanged += (_, _) => FitLabelColumn();
            form.HandleCreated += (_, _) => FitLabelColumn();
            form.VisibleChanged += (_, _) => { if (form.Visible) FitLabelColumn(); };
            body.Controls.Add(form, column, 0);
            for (var index = 0; index < perColumn; index++)
            {
                form.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / perColumn));
                if (index >= columnFields.Length) continue;
                var binding = columnFields[index];
                var editor = CreateEditor(binding.Document, binding.Row, binding.RowIndex, binding.Field);
                var text = editor is ByteTextEditor byteEditor ? byteEditor.Editor : editor as TextBox;
                if (text is { Multiline: true })
                {
                    if (binding.Field.Name.Contains("名称", StringComparison.Ordinal)) { text.Multiline = false; text.ScrollBars = ScrollBars.None; if (editor is ByteTextEditor bytes) bytes.CompactBudget = true; else editor.Height = 23; }
                    else { editor.Height = textHeight; editor.Dock = DockStyle.Fill; }
                }
                // Keep short numeric fields short, while distributing complete rows
                // over the available height instead of stacking them at the top.
                if (text is not { Multiline: true })
                {
                    editor.Dock = DockStyle.None;
                    editor.Anchor = AnchorStyles.Left;
                    var preferredWidth = editor is NumericUpDown ? 104 : editor is CheckBox ? editor.PreferredSize.Width
                        : editor is FlowLayoutPanel ? int.MaxValue : 300;
                    if (editor is FlowLayoutPanel) editor.AutoSize = false;
                    void FitEditor()
                    {
                        var available = Math.Max(1, form.ClientSize.Width - (int)form.ColumnStyles[0].Width - editor.Margin.Horizontal);
                        editor.Width = Math.Min(preferredWidth, available);
                        if (editor is FlowLayoutPanel flags)
                            flags.Height = Math.Max(flags.Font.Height + 8, flags.GetPreferredSize(new Size(editor.Width, 0)).Height);
                    }
                    form.SizeChanged += (_, _) => FitEditor();
                    editorFitters.Add(FitEditor);
                    FitEditor();
                }
                var label = new Label { Text = labels[index], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                    AutoSize = false, Margin = new Padding(0, 0, 5, 0) };
                editor.Margin = new Padding(0, 3, 3, 3);
                form.Controls.Add(label, 0, index); form.Controls.Add(editor, 1, index);
                if (editor is ByteTextEditor) editor.SizeChanged += (_, _) => FitLabelColumn();
                if (!binding.Document.Definition.IsWriteSupported)
                    _diagnosticTips.SetToolTip(editor, "只读：" + binding.Document.Definition.CapabilityEvidence);
            }
            FitLabelColumn();
        }
        return group;
    }

    /// <summary>
    /// Builds a small row-major form.  FieldsBlock is deliberately column-major
    /// for dense legacy pages; the compact job and strategy forms need paired
    /// labels in a predictable left-to-right order instead.
    /// </summary>
    private Control FieldsGridBlock(string title, IReadOnlyList<(FieldBinding? Left, FieldBinding? Right)> rows,
        int labelWidth = 88, int textHeight = 64)
    {
        var group = SectionContainer(title);
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = Math.Max(1, rows.Count),
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < Math.Max(1, rows.Count); row++)
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / Math.Max(1, rows.Count)));
        var arrangeRows = AttachScrollableContent(group, body, () => new Size((int)(body.ColumnStyles[0].Width + body.ColumnStyles[2].Width) +
            2 * (TextRenderer.MeasureText("255：九宫", body.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + SystemInformation.VerticalScrollBarWidth + 12),
            FieldRowMinimums(body).Sum()));
        body.SizeChanged += (_, _) => FitFieldRows(body);

        var leftLabels = rows.Select(row => row.Left == null ? string.Empty : row.Left.Label ??
            ItemFieldLabel(row.Left.Document, row.Left.RowIndex, row.Left.Field.Name) ??
            FieldLabel(row.Left.Document.Definition.Key, row.Left.Field.Name)).ToArray();
        var rightLabels = rows.Select(row => row.Right == null ? string.Empty : row.Right.Label ??
            ItemFieldLabel(row.Right.Document, row.Right.RowIndex, row.Right.Field.Name) ??
            FieldLabel(row.Right.Document.Definition.Key, row.Right.Field.Name)).ToArray();

        void FitLabels()
        {
            var leftWidth = leftLabels.Where(label => label.Length > 0)
                .Select(label => TextRenderer.MeasureText(label, body.Font, Size.Empty, TextFormatFlags.SingleLine).Width + 8)
                .DefaultIfEmpty(labelWidth).Max();
            var rightWidth = rightLabels.Where(label => label.Length > 0)
                .Select(label => TextRenderer.MeasureText(label, body.Font, Size.Empty, TextFormatFlags.SingleLine).Width + 8)
                .DefaultIfEmpty(labelWidth).Max();
            body.ColumnStyles[0].Width = Math.Max(labelWidth * body.DeviceDpi / 96f, leftWidth);
            body.ColumnStyles[2].Width = Math.Max(labelWidth * body.DeviceDpi / 96f, rightWidth);
            FitFieldRows(body);
        }

        void AddField(FieldBinding? binding, string labelText, int labelColumn, int editorColumn, int row)
        {
            if (binding == null) return;
            var editor = CreateEditor(binding.Document, binding.Row, binding.RowIndex, binding.Field);
            var text = editor is ByteTextEditor byteEditor ? byteEditor.Editor : editor as TextBox;
            if (text is { Multiline: true })
            {
                if (binding.Field.Name.Contains("名称", StringComparison.Ordinal))
                {
                    text.Multiline = false;
                    text.ScrollBars = ScrollBars.None;
                    if (editor is ByteTextEditor bytes) bytes.CompactBudget = true;
                    else editor.Height = 23;
                }
                else
                {
                    editor.Height = textHeight;
                }
            }
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 3, 3, 3);
            var label = new Label
            {
                Text = labelText,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false,
                Margin = new Padding(0, 0, 5, 0)
            };
            body.Controls.Add(label, labelColumn, row);
            body.Controls.Add(editor, editorColumn, row);
            if (editor is ByteTextEditor) editor.SizeChanged += (_, _) => { FitFieldRows(body); arrangeRows(); };
            if (!binding.Document.Definition.IsWriteSupported)
                _diagnosticTips.SetToolTip(editor, "只读：" + binding.Document.Definition.CapabilityEvidence);
        }

        for (var row = 0; row < rows.Count; row++)
        {
            AddField(rows[row].Left, leftLabels[row], 0, 1, row);
            if (rows[row].Right == null && rows[row].Left != null)
            {
                // The first job row is the name field.  Keep its label in the
                // first column, then let the editor occupy the complete
                // remaining row so the name is never squeezed into a single
                // half-width cell at 125%/150% DPI.
                var editor = body.GetControlFromPosition(1, row);
                if (editor != null) body.SetColumnSpan(editor, 3);
            }
            else
            {
                AddField(rows[row].Right, rightLabels[row], 2, 3, row);
            }
        }
        body.FontChanged += (_, _) => FitLabels();
        FitFieldRows(body);
        body.HandleCreated += (_, _) => FitLabels();
        body.VisibleChanged += (_, _) => { if (body.Visible) FitLabels(); };
        FitLabels();
        return group;
    }

    private void BuildPersonPage()
    {
        var page = DetailPage("人物详情");
        var root = Rows((SizeType.Percent, 43), (SizeType.Percent, 57));
        root.Name = "PersonPageContent";
        AttachScrollableContent(page, root, () => new Size(0,
            Math.Max(450 * root.DeviceDpi / 96, (root.Font.Height + 14) * 15)));
        var top = Columns(58, 42);
        root.Controls.Add(top, 0, 0);
        var hidden = new HashSet<string>(["Army", "暴击台词", "撤退台词", "级别", "经验", "头像"], StringComparer.Ordinal);
        var fieldOrder = new[] { "名称", "武力", "统帅", "智力", "敏捷", "士气", "兵种", "初始HP", "初始MP", "默认武器", "默认防具", "默认辅助" };
        // The column-major form puts the class beside the name on its first
        // row, followed by initial HP/MP and the three equipment slots.
        var basic = FieldsBlock(string.Empty, Fields("persons").Where(binding => !hidden.Contains(binding.Field.Name) && binding.Field.Kind != FieldKind.RawBytes)
            .OrderBy(binding => Array.IndexOf(fieldOrder, binding.Field.Name) is var order && order >= 0 ? order : int.MaxValue), 2, 0);
        basic.Name = "PersonBasicFields";
        top.Controls.Add(basic, 0, 0);
        var previews = CreateCompactPreviewSurface();
        top.Controls.Add(previews, 1, 0);
        var lower = Columns(45, 55);
        root.Controls.Add(lower, 0, 1);
        var biography = FieldsBlock(string.Empty, Fields("person-biography").Select(binding => binding with { Label = "列传" }), 1, 42, 125);
        biography.Name = "PersonBiography";
        lower.Controls.Add(biography, 0, 0);
        var quotes = SectionContainer(string.Empty);
        quotes.Name = "PersonQuotes";
        lower.Controls.Add(quotes, 1, 0);
        BuildQuoteEditor(quotes, _id);
    }

    private void BuildJobSeriesPage()
    {
        var page = DetailPage("兵种属性");
        var body = Columns(20, 20, 20, 20, 20);
        body.Name = "JobSeriesColumns";
        page.Controls.Add(body);
        var counter = Fields("job-series-counter").ToArray();
        for (var part = 0; part < 2; part++)
        {
            var start = part * 20;
            var matrix = NumericEntityMatrix(string.Empty,
                counter.Skip(start).Take(20).Select((binding, index) => binding with { Label = $"{start + index}：{NameOf("job-series", start + index)}" }), "兵种系", "克制");
            matrix.Name = $"JobSeriesCounter{start}";
            body.Controls.Add(matrix, part, 0);
        }
        var attributes = new List<FieldBinding>();
        if (_workspace.Documents.TryGetValue("job-series-attributes", out var document))
        {
            var field = document.Definition.Fields.FirstOrDefault(candidate => candidate.Name == $"兵种系 {_id}");
            if (field != null)
                for (var row = 0; row < Math.Min(JobAttributeNames.Length, document.Data.Rows.Count); row++)
                    attributes.Add(new(document, document.Data.Rows[row], row, field, JobAttributeNames[row]));
        }
        var attributeGroup = NumericEntityMatrix(string.Empty, Fields("job-series").Concat(attributes), "属性", "设定");
        attributeGroup.Name = "JobSeriesAttributes";
        var terrain = TerrainValueMatrix();
        body.Controls.Add(terrain, 2, 0);
        body.SetColumnSpan(terrain, 2);
        body.Controls.Add(attributeGroup, 4, 0);
        var terrainColumns = (TableLayoutPanel)terrain;
        FitJobSeriesPage(page, body, [body.GetControlFromPosition(0, 0)!, body.GetControlFromPosition(1, 0)!,
            terrainColumns.GetControlFromPosition(0, 0)!, terrainColumns.GetControlFromPosition(1, 0)!, attributeGroup]);
        // The group editor is global. Reuse its validated dedicated binary transaction.
        AddAccessoryGroupsOnly();
    }

    private void BuildJobPage()
    {
        var page = DetailPage("兵种详情");
        var columns = Columns(68, 32);
        AttachScrollableContent(page, columns, () => new Size(0, (columns.Font.Height + 14) * 17));
        var leftBody = Columns(80, 20);
        columns.Controls.Add(leftBody, 0, 0);
        var left = Rows((SizeType.Percent, 45), (SizeType.Percent, 16), (SizeType.Percent, 39));
        leftBody.Controls.Add(left, 0, 0);
        var top = Columns(100); left.Controls.Add(top, 0, 0);
        var fields = Fields("detailed-jobs").Concat(Fields("detailed-job-growth").Where(binding => !EquipmentColumns.Contains(binding.Field.Name) && binding.Field.Kind != FieldKind.RawBytes))
            .Concat(Fields("detailed-job-penetration"));
        var byName = fields.GroupBy(binding => binding.Field.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        FieldBinding? JobField(string name, string? label = null)
            => byName.TryGetValue(name, out var binding) ? binding with { Label = label } : null;
        var jobRows = new (FieldBinding? Left, FieldBinding? Right)[]
        {
            (JobField("名称"), null),
            (JobField("攻击成长"), JobField("防御成长")),
            (JobField("精神成长"), JobField("爆发成长")),
            (JobField("士气成长"), JobField("移动力成长", "移动力")),
            (JobField("HP成长"), JobField("MP成长")),
            (JobField("攻击范围"), JobField("穿透", "穿透范围"))
        };
        top.Controls.Add(FieldsGridBlock(string.Empty, jobRows, 68), 0, 0);
        leftBody.Controls.Add(CreateCompactPreviewSurface(), 1, 0);
        left.Controls.Add(FieldsBlock(string.Empty, Fields("detailed-job-description").Select(binding => binding with { Label = "介绍" }), 1, 42, 34), 0, 1);
        left.Controls.Add(EquipmentChecks(), 0, 2);
        var learning = SectionContainer("习得策略");
        columns.Controls.Add(learning, 1, 0);
        AddJobLearningTab(_id, learning);
    }

    private Control EquipmentChecks()
    {
        var fields = Fields("detailed-job-growth").Where(binding => EquipmentColumns.Contains(binding.Field.Name)).ToDictionary(binding => binding.Field.Name, StringComparer.Ordinal);
        var group = SectionContainer(string.Empty);
        var table = new TableLayoutPanel { Name = "EquipmentPermissions", Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 8, Padding = new Padding(7, 2, 7, 4) };
        for (var column = 0; column < 4; column++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        var rowHeight = Math.Max(20, Font.Height + 3);
        for (var row = 0; row < 8; row++) table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 8));
        for (var index = 0; index < EquipmentColumns.Count; index++)
        {
            var storageColumn = EquipmentColumns[index];
            if (!fields.TryGetValue(storageColumn, out var binding)) continue;
            var check = (CheckBox)CreateEditor(binding.Document, binding.Row, binding.RowIndex, binding.Field);
            check.Name = storageColumn; check.Margin = new Padding(2, 0, 2, 0);
            void RefreshName() => check.Text = EquipmentPermissionDisplayName(storageColumn);
            RefreshName(); RegisterEquipmentTypeBinding(check, RefreshName); _entityRebinders.Add(RefreshName);
            check.AutoSize = false; check.AutoEllipsis = true; check.Height = rowHeight; check.Dock = DockStyle.Fill;
            _diagnosticTips.SetToolTip(check, check.Text);
            var pair = index / 2;
            var row = pair < 10 ? pair / 2 : 6 + (pair - 10) / 2;
            var column = ((pair < 10 ? pair : pair - 10) % 2) * 2 + index % 2;
            table.Controls.Add(check, column, row);
        }
        AttachScrollableContent(group, table, () => new Size(
            4 * Math.Max(90, table.Controls.OfType<CheckBox>().Select(check => check.PreferredSize.Width + check.Margin.Horizontal).DefaultIfEmpty(90).Max()) + table.Padding.Horizontal,
            8 * Math.Max(table.Font.Height + 10, 24 * table.DeviceDpi / 96) + table.Padding.Vertical));
        return group;
    }

    private void BuildItemPage()
    {
        var page = DetailPage("宝物详情");
        var root = Columns(72, 28); page.Controls.Add(root);
        var left = Rows((SizeType.Percent, 68), (SizeType.Percent, 32)); root.Controls.Add(left, 0, 0);
        var itemFields = Fields("items-low").Concat(Fields("items-high")).Concat(Fields("items-extended")).ToArray();
        var item = itemFields.FirstOrDefault();
        var semanticSnapshot = item == null ? null : ItemSemanticResolver.Resolve(item.Document, item.RowIndex);
        var semantics = semanticSnapshot?.Semantics;
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string> categoryHidden = semantics?.Category switch
        {
            ItemCategory.Accessory => new[] { "装备特效值" },
            ItemCategory.Consumable => new[] { "能力成长" },
            _ => Array.Empty<string>()
        };
        foreach (var fieldName in categoryHidden) hidden.Add(fieldName);
        var visibleItemFields = itemFields
            .Where(binding => binding.Field.Policy != FieldPolicy.Preserve && !hidden.Contains(binding.Field.Name));
        left.Controls.Add(FieldsBlock("宝物设定", visibleItemFields, 2, 0), 0, 0);
        left.Controls.Add(FieldsBlock(string.Empty, Fields("item-description-low").Concat(Fields("item-description-high")).Concat(Fields("item-description-extended")).Select(binding => binding with { Label = "介绍" }), 1, 42, 112), 0, 1);
        var right = Rows((SizeType.Percent, 35), (SizeType.Percent, 65));
        right.Controls.Add(CreateCompactPreviewSurface(), 0, 0);
        right.Controls.Add(BuildEquipmentTypeEditor(), 0, 1);
        root.Controls.Add(right, 1, 0);
        if (itemFields.Any(binding => hidden.Contains(binding.Field.Name)))
        {
            var rawPage = DetailPage("原始字段");
            rawPage.Controls.Add(FieldsBlock("保留字节", itemFields.Where(binding => hidden.Contains(binding.Field.Name))
                .Select(binding => binding with { Label = $"保留值（+0x{binding.Field.Offset:X2}）" }), 2, 115));
            rawPage.Controls.Add(new Label { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8),
                Text = "这些字节没有已确认的业务含义；可直接编辑，未修改时保留原值。" });
        }
        BuildItemPermissions();
    }

    private Control CreateItemSemanticSummary(FieldBinding item, ItemSemanticSnapshot snapshot)
    {
        var group = SectionContainer("类别与字段说明");
        var label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        group.Controls.Add(label);
        void Refresh()
        {
            var binding = ResolveEditorBinding(item.Document.Definition.Key, item.Field.Name, item.RowIndex);
            if (binding == null) return;
            var current = ItemSemanticResolver.Resolve(binding.Document, binding.RowIndex);
            label.Text = current.Category switch
            {
                ItemCategory.Accessory => "辅助装备：特殊效果值和适用兵种独立编辑，图鉴可勾选。保留值可在“原始字段”页编辑。",
                ItemCategory.Consumable => $"道具特效 0x{current.ActualEffectNumber:X2}：{ConsumableEffectCatalog.Resolve(current.ActualEffectNumber).Name}；参数由低、高字节组合，图鉴可勾选。",
                ItemCategory.Weapon or ItemCategory.Armor => $"{current.Semantics.CategoryName}：初始属性、特殊效果值、能力成长和图鉴均可编辑。装备许可在“可装备部队”页设置。",
                _ => "当前类别未确认；物理字段仍可编辑，保存保留未修改的原始字节。"
            };
        }
        _entityRebinders.Add(Refresh);
        _itemSemanticRefresh = Refresh;
        Refresh();
        return group;
    }

    private void BuildItemPermissions()
    {
        if (!_workspace.Documents.TryGetValue("detailed-job-growth", out var growth)) return;
        FieldBinding? CurrentItem() => Fields("items-low").Concat(Fields("items-high")).Concat(Fields("items-extended")).FirstOrDefault();
        if (CurrentItem() == null) return;
        var page = DetailPage("可装备部队");
        var message = new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(4), AutoEllipsis = true };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 20, Padding = new Padding(4) };
        for (var column = 0; column < 4; column++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (var row = 0; row < 20; row++) table.RowStyles.Add(new RowStyle(SizeType.Percent, 5));
        page.Controls.Add(table); page.Controls.Add(message);
        var checks = new List<(CheckBox Check, int RowIndex)>();
        foreach (var (row, index) in growth.Data.Rows.Cast<DataRow>().Select((row, index) => (row, index)).Take(80))
        {
            var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
            var check = new CheckBox { Text = $"{id}：{NameOf("detailed-jobs", id)}", AutoSize = false, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 0), AutoEllipsis = true };
            table.Controls.Add(check, index / 20, index % 20); checks.Add((check, index));
            check.CheckedChanged += (_, _) =>
            {
                if (_binding) return;
                var item = CurrentItem(); if (item == null) return;
                var semantics = ItemTypeService.Resolve(item.Document, item.RowIndex);
                if (semantics.PermissionColumn is not { } column || !growth.Data.Columns.Contains(column)) return;
                try
                {
                    if (_workspace.ApplyCompositeTransaction($"编辑宝物 {_id} 可装备部队", [new("detailed-job-growth", index, column, check.Checked ? 1 : 0)]).Applied) OnEdited();
                }
                catch (Exception ex) { ShowEditorError(check, ex.Message); }
            };
        }
        void Refresh()
        {
            var previous = _binding; _binding = true;
            try
            {
                var item = CurrentItem(); if (item == null) return;
                var semantics = ItemTypeService.Resolve(item.Document, item.RowIndex);
                var hasPermission = semantics.PermissionColumn is { } permissionColumn && growth.Data.Columns.Contains(permissionColumn);
                message.Text = semantics.PermissionColumn is { } column
                    ? hasPermission ? $"{EquipmentPermissionDisplayName(column)}：同类型宝物共享装备许可" : "当前引擎尚无此装备许可字段的独立证据，保留原字节。"
                    : $"{semantics.CategoryName}不使用兵种装备许可";
                foreach (var (check, rowIndex) in checks)
                {
                    check.Enabled = hasPermission && growth.Definition.IsWriteSupported;
                    check.Checked = hasPermission && Convert.ToInt32(growth.Data.Rows[rowIndex][semantics.PermissionColumn!], CultureInfo.InvariantCulture) != 0;
                }
            }
            finally { _binding = previous; }
        }
        Refresh(); _refreshItemPermissions = Refresh;
    }

    private void BuildStrategyPage()
    {
        var page = DetailPage("策略详情");
        var columns = Columns(76, 24);
        AttachScrollableContent(page, columns, () => new Size(0, (columns.Font.Height + 14) * 18));
        var left = Rows((SizeType.Percent, 60), (SizeType.Percent, 22), (SizeType.Percent, 18)); columns.Controls.Add(left, 0, 0);
        var keys = _business.GetAvailableTableKeys(BusinessPageKind.Strategies)
            .Where(key => key is not ("strategy-description" or "strategy-terrain-availability"));
        var fields = keys.SelectMany(Fields).Where(binding => !IsLearningField(binding.Field.Name))
            .Select(binding => binding.Field.Name == "动画ID" ? binding with { Label = binding.Document.Definition.Key == "strategy-big-animation" ? "大动画" : "小动画" } : binding);
        var allFields = fields.ToArray();
        var settings = allFields.Where(binding => GetFlags(binding.Document.Definition.Key, binding.Field.Name) == null).ToArray();
        var rangeFields = settings.Where(binding => binding.Field.Name is "施法范围" or "穿透范围").ToArray();
        var otherSettings = settings.Where(binding => binding.Field.Name is not ("施法范围" or "穿透范围")).ToArray();
        var strategyTop = Rows((SizeType.Percent, 24), (SizeType.Percent, 76));
        var rangePair = new (FieldBinding? Left, FieldBinding? Right)[]
        {
            (rangeFields.FirstOrDefault(binding => binding.Field.Name == "施法范围"),
             rangeFields.FirstOrDefault(binding => binding.Field.Name == "穿透范围"))
        };
        var settingRows = otherSettings
            .Select((binding, index) => (binding, index))
            .GroupBy(item => item.index / 2)
            .Select(group =>
            {
                var pair = group.Select(item => item.binding).ToArray();
                return (pair.ElementAtOrDefault(0), pair.ElementAtOrDefault(1));
            })
            .ToArray();
        strategyTop.Controls.Add(FieldsGridBlock("施展范围", rangePair, 88), 0, 0);
        strategyTop.Controls.Add(FieldsGridBlock("策略设定", settingRows, 88), 0, 1);
        left.Controls.Add(strategyTop, 0, 0);
        left.Controls.Add(FieldsBlock("效果与拓展", allFields.Where(binding => GetFlags(binding.Document.Definition.Key, binding.Field.Name) != null), 1, 88), 0, 1);
        left.Controls.Add(FieldsBlock("策略说明", Fields("strategy-description").Select(binding => binding with { Label = "说明" }), 1, 42, 90), 0, 2);
        columns.Controls.Add(CreateCompactPreviewSurface(), 1, 0);
        var learning = DetailPage("兵种学习等级");
        var learningColumns = Columns(25, 25, 25, 25); learning.Controls.Add(learningColumns);
        var learningFields = Fields("strategies").Where(binding => IsLearningField(binding.Field.Name)).Select(binding =>
        {
            var digits = new string(binding.Field.Name.Where(char.IsDigit).ToArray());
            var id = int.Parse(digits, CultureInfo.InvariantCulture);
            return binding with { Label = $"{id}：{NameOf("detailed-jobs", id)}" };
        }).ToArray();
        for (var column = 0; column < 4; column++)
            learningColumns.Controls.Add(EntityMatrix($"兵种 {column * 20}–{column * 20 + 19}（0 为不学习）", learningFields.Skip(column * 20).Take(20)), column, 0);
        FitLearningPage(learning, learningColumns);
        BuildTerrainPage();
    }

    private void BuildTerrainPage()
    {
        var page = DetailPage("施展地形");
        if (!_workspace.Documents.TryGetValue("strategy-terrain-availability", out var terrain))
        { page.Controls.Add(Notice("当前基底没有已确认的施展地形布局。")); return; }
        var rows = new List<FieldBinding>();
        for (var row = 0; row < terrain.Data.Rows.Count; row++)
            rows.Add(new(terrain, terrain.Data.Rows[row], row, terrain.Definition.Fields[0], TerrainLabel(row)));
        page.Controls.Add(FieldsBlock("全局施展地形（火、水、风、地策略共享）", rows, 3, 72));
    }

    private void BuildShopPage()
    {
        var page = DetailPage("商店详情");
        var root = Rows((SizeType.Percent, 19), (SizeType.Percent, 81)); page.Controls.Add(root);
        root.Controls.Add(FieldsBlock(string.Empty, Fields("scenario-names").Concat(Fields("shops").Where(binding => binding.Field.ReferenceTable == "persons")), 3, 104), 0, 0);
        var slots = Fields("shops").Where(binding => binding.Field.ReferenceTable != "persons").ToArray();
        root.Controls.Add(FieldsBlock(string.Empty, slots, 4, 64), 0, 1);
    }

    private static string TerrainName(int index) => index < TerrainNames.Length ? TerrainNames[index] : "未确认";
    private static string TerrainLabel(int index) => $"{index}：{TerrainName(index)}";
    private static IReadOnlyList<IdNameOption>? JobAttributeOptions(int attribute) => attribute switch
    {
        0 => [new(0, "马蹄声"), new(1, "车轮声"), new(2, "脚步声"), new(3, "无声"), new(4, "引擎声"), new(5, "预设1"), new(6, "预设2")],
        1 => [new(0, "快速"), new(1, "慢速")],
        2 => [new(0, "剑"), new(1, "其他"), new(2, "爆炸1"), new(3, "爆炸2"), new(4, "火箭筒"), new(5, "枪声（连射）"), new(6, "枪声（点射）"), new(7, "火焰喷射"), new(8, "毒气喷射")],
        3 => [new(0, "否"), new(1, "是")],
        4 => [new(0, "无延迟"), new(1, "有延迟")],
        5 => [new(0, "全能"), new(1, "文官"), new(2, "武将")],
        7 => [new(0, "不参与"), new(1, "参与")],
        _ => null
    };

    private IReadOnlyList<IdNameOption> ItemEffectOptions()
    {
        try { return new EffectNamePoolService().Read(_workspace, EffectNameChannel.Item).Where(item => item.EffectId != 255)
            .Select(item => new IdNameOption(item.EffectId, item.Name)).Append(new IdNameOption(255, "无")).ToArray(); }
        catch { return [new(255, "无")]; }
    }
}
