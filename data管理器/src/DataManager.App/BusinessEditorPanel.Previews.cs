using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Models;
using DataManager.Images;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private CancellationTokenSource _previewCancellation = new();
    private long _previewGeneration;
    private Control? _compactPreviewHost;
    private sealed class PreviewInput(Func<int?> read)
    {
        public Func<int?> Read { get; } = read;
        public int? LastValid { get; set; } = read();
    }
    private readonly Dictionary<(string Table, string Field), PreviewInput> _previewInputs = [];

    private void BeginPreviewBinding(bool preserveHost = false)
    {
        // The individual cards own cancellation. Rebinding another entity with
        // the same resource IDs keeps its images and animation position.
        if (!preserveHost)
        {
            _compactPreviewHost = null;
            _previewInputs.Clear();
        }
    }

    internal void InvalidateCompactPreviews()
    {
        if (_compactPreviewHost is not { IsDisposed: false } host) return;
        foreach (var card in PreviewCards(host)) card.InvalidateResource();
        RefreshCompactPreviews();
    }

    private static IEnumerable<ResourcePreviewCard> PreviewCards(Control host)
    {
        foreach (Control child in host.Controls)
        {
            if (child is ResourcePreviewCard card) yield return card;
            foreach (var descendant in PreviewCards(child)) yield return descendant;
        }
    }

    private int EntityNumber(string key, string field, int fallback = 0)
    {
        if (!_workspace.Documents.TryGetValue(key, out var document) || FindRow(document, _id) is not { } match) return fallback;
        var definition = document.Definition.Fields.FirstOrDefault(candidate => candidate.Name == field);
        return definition == null ? fallback : NumericValue(ReadFieldValue(document, match.Index, definition) ?? string.Empty, fallback, definition.Kind == FieldKind.RawBytes);
    }

    private int PreviewNumber(string key, string field, int fallback = 0)
        => !_binding && _previewInputs.TryGetValue((key, field), out var input) &&
            (input.Read() ?? input.LastValid) is { } number ? number : EntityNumber(key, field, fallback);

    private bool RegisterPreviewInput(string tableKey, string fieldName, Control editor, Func<int?> readInput)
    {
        if ((tableKey, fieldName) is not (("persons", "头像" or "兵种") or
            ("person-r-assign", "R形象ID") or ("person-s-assign", "S形象ID") or
            ("items-low" or "items-high" or "items-extended", "图标") or
            ("strategies", "策略图标") or ("strategy-small-animation" or "strategy-big-animation", "动画ID"))) return false;
        var input = new PreviewInput(readInput);
        _previewInputs[(tableKey, fieldName)] = input;
        // Preview valid input immediately. Text entry still uses the normal
        // Enter/validation transaction, so browsing images creates no edits.
        editor.TextChanged += (_, _) =>
        {
            if (editor.IsDisposed) return;
            var value = readInput();
            if (_binding) { input.LastValid = value; return; }
            // Keep the last valid value while typing an incomplete/invalid ID,
            // including when a different preview field changes meanwhile.
            if (value is not { } number) return;
            input.LastValid = number;
            RefreshCompactPreviews();
        };
        return true;
    }

    private Control CreateCompactPreviewSurface()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(3), Padding = Padding.Empty };
        _compactPreviewHost = panel;
        if (_kind == BusinessPageKind.Persons) BuildPersonPreviewColumns(panel);
        PopulateCompactPreviews(panel);
        return panel;
    }

    private void BuildPersonPreviewColumns(Control host)
    {
        const int count = 3;
        var table = new TableLayoutPanel
        {
            Name = "PersonPreviewColumns", Dock = DockStyle.Fill, ColumnCount = count, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        for (var column = 0; column < count; column++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / count));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.Controls.Add(table);
        var headerRows = new List<(TableLayoutPanel Form, Control Editor, Button Button)>();
        void FitHeaderHeight()
        {
            var needed = 56 * table.DeviceDpi / 96f;
            foreach (var row in headerRows)
            {
                var labelHeight = row.Form.Font.Height + 6 * table.DeviceDpi / 96;
                var editorHeight = row.Editor is ComboBox combo ? combo.PreferredHeight : row.Editor.PreferredSize.Height;
                var inputHeight = Math.Max(editorHeight, row.Button.GetPreferredSize(Size.Empty).Height) + row.Editor.Margin.Vertical + 6;
                row.Form.RowStyles[0].SizeType = SizeType.Absolute;
                row.Form.RowStyles[0].Height = labelHeight;
                row.Form.RowStyles[1].SizeType = SizeType.Absolute;
                row.Form.RowStyles[1].Height = inputHeight;
                needed = Math.Max(needed, labelHeight + inputHeight + 4);
            }
            table.RowStyles[0].Height = needed;
        }

        void AddHeader(FieldBinding? binding, int column, string category, string buttonText)
        {
            if (binding == null) return;
            var header = new Panel { Dock = DockStyle.Fill, Margin = new Padding(4, 0, 4, 2) };
            var form = new TableLayoutPanel
            {
                ColumnCount = 2, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty,
                Anchor = AnchorStyles.None
            };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            var label = new Label
            {
                Text = binding.Label ?? FieldLabel(binding.Document.Definition.Key, binding.Field.Name),
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty
            };
            var locationButton = new Button { Name = "LocatePerson" + category + "Image", Text = buttonText,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, Margin = new Padding(4, 0, 0, 0), Padding = Padding.Empty, TabStop = true };
            locationButton.Click += (_, _) =>
            {
                if (!Flush()) return;
                if (category == "S")
                    SImageLocationRequested?.Invoke(_id,
                        EntityNumber("person-s-assign", "S形象ID", -1), EntityNumber("persons", "兵种", -1));
                else PersonImageLocationRequested?.Invoke(category, _id,
                    EntityNumber(binding.Document.Definition.Key, binding.Field.Name, -1));
            };
            _diagnosticTips.SetToolTip(locationButton, category == "S"
                ? "按当前草稿指定定位实际 Unit 图号；三转人物或默认兵种可选择阶段/阵营。图片导入会即时保存。\n存档同步仍使用磁盘上已保存的人物指定。"
                : "按当前草稿编号定位图片设定中的对应条目。图片导入会即时保存。");
            var editor = CreateEditor(binding.Document, binding.Row, binding.RowIndex, binding.Field, binding.Label);
            editor.Dock = DockStyle.None; editor.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            editor.Margin = new Padding(0, 1, 0, 1);
            form.Controls.Add(label, 0, 0); form.Controls.Add(editor, 0, 1);
            form.SetColumnSpan(label, 2);
            form.Controls.Add(locationButton, 1, 1);
            headerRows.Add((form, editor, locationButton));
            editor.FontChanged += (_, _) => FitHeaderHeight();
            editor.SizeChanged += (_, _) => FitHeaderHeight();
            locationButton.FontChanged += (_, _) => FitHeaderHeight();
            header.Controls.Add(form); table.Controls.Add(header, column, 0);
            void ArrangeHeader()
            {
                var width = Math.Min(header.ClientSize.Width, Math.Max(210 * header.DeviceDpi / 96,
                    locationButton.GetPreferredSize(Size.Empty).Width + 96 * header.DeviceDpi / 96));
                form.Bounds = new Rectangle(Math.Max(0, (header.ClientSize.Width - width) / 2), 0, Math.Max(1, width), header.ClientSize.Height);
            }
            header.ClientSizeChanged += (_, _) => ArrangeHeader();
            header.DpiChangedAfterParent += (_, _) => ArrangeHeader();
            header.FontChanged += (_, _) => ArrangeHeader();
            ArrangeHeader();
        }

        AddHeader(Fields("persons").FirstOrDefault(binding => binding.Field.Name == "头像"), 0, "Face", "定位头像");
        AddHeader(Fields("person-r-assign").Select(binding => binding with { Label = "R形象编号" }).FirstOrDefault(), 1, "R", "定位 R");
        AddHeader(Fields("person-s-assign").Select(binding => binding with { Label = "S形象编号" }).FirstOrDefault(), 2, "S", "定位 S");
        table.FontChanged += (_, _) => FitHeaderHeight();
        table.DpiChangedAfterParent += (_, _) => FitHeaderHeight();
        table.HandleCreated += (_, _) => FitHeaderHeight();
        FitHeaderHeight();
    }

    private void RefreshCompactPreviews()
    {
        if (_compactPreviewHost is { IsDisposed: false } panel) PopulateCompactPreviews(panel);
    }

    private void PopulateCompactPreviews(Control host)
    {
        var root = _workspace.RootPath;
        var resourceRevision = _resources.Revision;
        var kind = _kind; var entityId = _id;
        var table = host.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
        if (table == null)
        {
            table = kind == BusinessPageKind.Persons
                ? Columns(1, 1, 1) : Rows(
                (kind is BusinessPageKind.Strategies or BusinessPageKind.DetailedJobs)
                    ? [(SizeType.Percent, 34), (SizeType.Percent, 33), (SizeType.Percent, 33)]
                    : [(SizeType.Percent, 100)]);
            host.Controls.Add(table);
        }
        var cards = table.Controls.OfType<ResourcePreviewCard>().ToArray();
        int cardIndex = 0;
        void Card(string key, string title, Func<CancellationToken, Task<ResourcePreviewAnimation>> loader, bool animate = false)
        {
            var index = cardIndex++;
            var card = index < cards.Length ? cards[index] : new ResourcePreviewCard(_diagnosticTips);
            if (card.Parent == null)
            {
                if (kind == BusinessPageKind.Persons)
                {
                    card.Margin = new Padding(8);
                    table.Controls.Add(card, index, 1);
                }
                else table.Controls.Add(card, 0, index);
            }
            card.Bind(root + "|" + resourceRevision.ToString(CultureInfo.InvariantCulture) + "|" + key, title, loader, animate);
        }
        async Task<ResourcePreviewAnimation> Still(string key, int id, CancellationToken cancellation)
        {
            var image = await _resources.DecodeFirstFrameAsync(root, key, id, new Size(512, 512), cancellation).ConfigureAwait(false);
            return new ResourcePreviewAnimation(image == null ? [] : [image], 160, $"{titleFor(key)} {id}");
        }
        static string titleFor(string key) => key switch { "Face" => "头像", "Pmapobj.e5" => "R形象", _ => "关联资源" };
        async Task<ResourcePreviewAnimation> Icon(bool strategy, int id, CancellationToken cancellation)
        {
            // Catalog discovery and archive decoding both stay off the UI thread.
            var kind = strategy ? ResourceBindingKind.StrategyIcon : ResourceBindingKind.ItemIcon;
            var icon = await _resources.DecodeBindingThumbnailAsync(root, kind, id, new Size(512, 512), cancellationToken: cancellation).ConfigureAwait(false);
            return new ResourcePreviewAnimation(icon == null ? [] : [icon], 160, $"图标 {id}");
        }
        switch (kind)
        {
            case BusinessPageKind.Persons:
                var face = PreviewNumber("persons", "头像");
                var job = PreviewNumber("persons", "兵种");
                var rId = PreviewNumber("person-r-assign", "R形象ID", -1);
                var sId = PreviewNumber("person-s-assign", "S形象ID", -1);
                var verifiedR = _workspace.Documents.TryGetValue("person-r-assign", out var r) && r.Definition.Capability == TableCapability.Editable;
                var verifiedS = _workspace.Documents.TryGetValue("person-s-assign", out var s) && s.Definition.Capability == TableCapability.Editable;
                Card($"Face:{face}", $"头像 {face}", cancellation => Still("Face", face, cancellation));
                Card($"R:{rId}:{verifiedR}", $"R {rId}", cancellation => verifiedR && rId >= 0 ? Still("Pmapobj.e5", rId, cancellation) : Task.FromResult(new ResourcePreviewAnimation([], 160, "R形象指定缺少布局证据")));
                Card($"S:{(verifiedS ? sId : 0)}:{(verifiedS && sId != 0 ? 0 : job)}:mov:0", verifiedS ? $"S {sId}" : "默认S", cancellation => _resources.DecodeCharacterSAsync(root, verifiedS ? sId : 0, job, 1, cancellation));
                break;
            case BusinessPageKind.DetailedJobs:
                for (var faction = 0; faction < 3; faction++)
                {
                    var slot = faction;
                    var name = new[] { "我军", "友军", "敌军" }[slot];
                    Card($"JobS:{entityId}:{slot}:mov:0", $"{name} · S_{entityId * 3 + slot + 1:000}",
                        cancellation => _resources.DecodeJobSFirstFrameAsync(root, entityId, slot, cancellation));
                }
                break;
            case BusinessPageKind.Items:
                var itemKey = new[] { "items-low", "items-high", "items-extended" }.FirstOrDefault(key => Fields(key).Any());
                var itemIcon = itemKey == null ? 0 : PreviewNumber(itemKey, "图标");
                Card($"ItemIcon:{itemIcon}", $"宝物图标 {itemIcon}", cancellation => Icon(false, itemIcon, cancellation));
                break;
            case BusinessPageKind.Strategies:
                var strategyIcon = PreviewNumber("strategies", "策略图标");
                var small = PreviewNumber("strategy-small-animation", "动画ID", 255);
                var large = PreviewNumber("strategy-big-animation", "动画ID", 255);
                Card($"StrategyIcon:{strategyIcon}", $"策略图标 {strategyIcon}", cancellation => Icon(true, strategyIcon, cancellation));
                Card($"Small:{small}", $"小动画 {small}", cancellation => _resources.DecodeStrategyAnimationAsync(root, false, small, cancellation), true);
                Card($"Large:{large}", $"大动画 {large}", cancellation => _resources.DecodeStrategyAnimationAsync(root, true, large, cancellation), true);
                break;
        }
    }
}
