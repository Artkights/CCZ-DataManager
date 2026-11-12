using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DataManager.App;
using DataManager.Core.Domain;

namespace DataManager.Tests;

/// <summary>
/// Diagnostic benchmark on the real three shell ComboBoxes. It never applies a query or edits
/// a game record. Semantically invalid fast variants remain explicitly excluded from recommendations.
/// </summary>
internal static class QueryChoiceBindingPerformanceTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int WmSetRedraw = 0x000B;
    private const int CbInitStorage = 0x0161;
    private const uint RdwInvalidate = 0x0001, RdwErase = 0x0004, RdwAllChildren = 0x0080;

    private sealed record ChoiceSet(string Name, object[] Items, Array TypedItems, int StorageBytes, int DropDownWidth);
    private sealed record Variant(string Name, bool DataSource = false, bool PrivateBindingContext = false,
        bool BeginUpdate = true, bool HeaderRedraw = false, bool ChoiceRedraw = false, bool InitStorage = false,
        bool? IntegralHeight = null, DrawMode? Drawing = null, FlatStyle? Style = null, int? DropDownHeight = null,
        bool HideDuringReplace = false, int? MaxDropDownItems = null);
    private sealed record ReplaceTiming(double ClearMs, double AddMs, double WidthMs, double SelectionMs, double EndUpdateMs);
    private sealed record Sample(int Iteration, string ChoiceSet, int Count, double BindingMs,
        double ReadyMs, int SelectionEvents, int DrawEvents, int HandleCreations, ReplaceTiming Steps);
    private sealed record Distribution(double P50Ms, double P95Ms, double MaximumMs);
    private sealed record Result(string Variant, bool SemanticallyValid, string[] Failures,
        double SetupMs, Distribution Binding, Distribution Ready, IReadOnlyList<Sample> Samples,
        object EffectiveProperties);
    private sealed record OriginalChoiceState(object[] Items, int SelectedIndex, string Text,
        int DropDownWidth, BindingContext? BindingContext, bool IntegralHeight, DrawMode DrawMode,
        FlatStyle FlatStyle, int DropDownHeight, int ItemHeight, int MaxDropDownItems);

    public static void Run(string source, string output, int iterations = 30)
    {
        if (iterations < 30) throw new ArgumentOutOfRangeException(nameof(iterations), "每个方案至少需要 30 次预热后的切换。");
        Directory.CreateDirectory(output);
        var copy = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        using var form = new MainForm(false)
        {
            StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000),
            ShowInTaskbar = false, Size = new(1280, 720)
        };
        try
        {
            form.Show(); form.LoadProjectForSmokeTest(copy); TestSupport.PumpUi();
            var (sets, sourceScopes) = CaptureChoiceSets(form);
            var combos = form.QueryFieldsForSmokeTest.ToArray();
            var original = combos.Select(combo => new OriginalChoiceState(combo.Items.Cast<object>().ToArray(),
                combo.SelectedIndex, combo.Text, combo.DropDownWidth, combo.BindingContext,
                combo.IntegralHeight, combo.DrawMode, combo.FlatStyle, combo.DropDownHeight,
                combo.ItemHeight, combo.MaxDropDownItems)).ToArray();
            var inputTexts = form.QueryInputsForSmokeTest.Select(input => input.Text).ToArray();
            var header = Descendants(form).Single(control => control.Name == "SettingsAndQuery");
            var guard = typeof(MainForm).GetField("_bindingQueryInputs", PrivateInstance)!;
            var oldGuard = (bool)guard.GetValue(form)!;
            var hadUndo = form.WorkspaceForSmokeTest.Commands.CanUndo;
            var initialQuerySummary = form.QuerySummaryForSmokeTest;
            var results = new List<Result>();
            Variant[] variants =
            [
                new("items-begin-update"),
                new("items-no-begin-update", BeginUpdate: false),
                new("items-header-redraw", HeaderRedraw: true),
                new("items-header-and-choices-redraw", BeginUpdate: false, HeaderRedraw: true, ChoiceRedraw: true),
                new("items-init-storage", InitStorage: true),
                new("items-init-storage-header-redraw", HeaderRedraw: true, InitStorage: true),
                new("data-source-shared-context", DataSource: true, BeginUpdate: false),
                new("data-source-private-context", DataSource: true, PrivateBindingContext: true, BeginUpdate: false),
                new("data-source-private-context-header-redraw", DataSource: true, PrivateBindingContext: true,
                    BeginUpdate: false, HeaderRedraw: true),
                new("data-source-private-context-begin-update", DataSource: true, PrivateBindingContext: true),
                new("items-integral-height-false", IntegralHeight: false),
                new("items-fixed-dropdown-height", IntegralHeight: false, DropDownHeight: 360),
                new("items-owner-draw-fixed", Drawing: DrawMode.OwnerDrawFixed),
                new("items-owner-draw-fixed-height", IntegralHeight: false,
                    Drawing: DrawMode.OwnerDrawFixed, DropDownHeight: 360),
                new("items-flat-system", Style: FlatStyle.System),
                new("items-flat-flat", Style: FlatStyle.Flat),
                new("items-hidden-during-replace", HideDuringReplace: true),
                new("items-max-dropdown-one", MaxDropDownItems: 1)
            ];
            // Keep the original ten variants available; this optional process-local filter lets
            // a diagnostic rerun isolate native item insertion without repeating bound-list cases.
            var variantFilter = Environment.GetEnvironmentVariable("CCZ_QUERY_CHOICE_VARIANTS")?.Trim() ?? "all";
            if (variantFilter.Equals("native", StringComparison.OrdinalIgnoreCase))
                variants = variants.Where(variant => variant.Name == "items-begin-update" ||
                    variant.IntegralHeight.HasValue || variant.Drawing.HasValue || variant.Style.HasValue ||
                    variant.HideDuringReplace || variant.MaxDropDownItems.HasValue).ToArray();
            else if (!variantFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var requested = variantFilter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                variants = variants.Where(variant => variant.Name == "items-begin-update" || requested.Contains(variant.Name)).ToArray();
                TestSupport.Assert(variants.Length > 1, "CCZ_QUERY_CHOICE_VARIANTS 没有匹配任何候选方案。");
            }
            guard.SetValue(form, true);
            try
            {
                foreach (var variant in variants)
                {
                    var setup = Stopwatch.StartNew();
                    ResetCombos(combos, original);
                    if (variant.PrivateBindingContext)
                        foreach (var combo in combos) combo.BindingContext = new BindingContext();
                    var drawEvents = 0;
                    DrawItemEventHandler drawItem = (sender, args) =>
                    {
                        drawEvents++;
                        DrawChoice((ComboBox)sender!, args);
                    };
                    foreach (var combo in combos)
                    {
                        if (variant.IntegralHeight is { } integralHeight) combo.IntegralHeight = integralHeight;
                        if (variant.Style is { } flatStyle) combo.FlatStyle = flatStyle;
                        if (variant.Drawing is { } drawMode)
                        {
                            combo.DrawItem += drawItem;
                            combo.DrawMode = drawMode;
                            combo.ItemHeight = Math.Max(original[0].ItemHeight,
                                TextRenderer.MeasureText("国 Ag", combo.Font).Height + 2);
                        }
                        if (variant.DropDownHeight is { } dropDownHeight)
                            combo.DropDownHeight = (int)Math.Ceiling(dropDownHeight * combo.DeviceDpi / 96d);
                        if (variant.MaxDropDownItems is { } maxDropDownItems) combo.MaxDropDownItems = maxDropDownItems;
                        _ = combo.Handle;
                    }
                    TestSupport.PumpUi();
                    var setupMs = setup.Elapsed.TotalMilliseconds;
                    var effectiveProperties = combos.Select(combo => new
                    {
                        combo.Name, combo.IntegralHeight, Drawing = combo.DrawMode.ToString(),
                        Style = combo.FlatStyle.ToString(), combo.DropDownHeight, combo.ItemHeight,
                        combo.MaxDropDownItems, combo.Sorted, combo.DropDownStyle
                    }).ToArray();
                    var failures = new HashSet<string>(StringComparer.Ordinal);
                    var selectionEvents = 0;
                    var handleCreations = 0;
                    EventHandler selectionChanged = (_, _) => selectionEvents++;
                    EventHandler handleCreated = (_, _) => handleCreations++;
                    foreach (var combo in combos) combo.SelectedIndexChanged += selectionChanged;
                    foreach (var combo in combos) combo.HandleCreated += handleCreated;
                    var samples = new List<Sample>();
                    try
                    {
                        for (var iteration = -8; iteration < iterations; iteration++)
                        {
                            var set = sets[(iteration + 8) & 1];
                            selectionEvents = 0;
                            drawEvents = 0;
                            handleCreations = 0;
                            var clock = Stopwatch.StartNew();
                            var steps = Replace(combos, header, set, variant);
                            var bindingMs = clock.Elapsed.TotalMilliseconds;
                            // The second number includes queued layout and the final paint. A redraw
                            // variant cannot appear faster merely by leaving its work in the queue.
                            TestSupport.PumpUi();
                            form.Update();
                            TestSupport.PumpUi();
                            var readyMs = clock.Elapsed.TotalMilliseconds;
                            Validate(form, combos, set, inputTexts, failures);
                            if (iteration >= 0) samples.Add(new(iteration + 1, set.Name, set.Items.Length,
                                bindingMs, readyMs, selectionEvents, drawEvents, handleCreations, steps));
                        }
                        if (form.WorkspaceForSmokeTest.IsDirty) failures.Add("绑定候选产生了游戏数据草稿。");
                        if (form.WorkspaceForSmokeTest.Commands.CanUndo != hadUndo) failures.Add("绑定候选改变了撤回历史。");
                        if (form.QuerySummaryForSmokeTest != initialQuerySummary) failures.Add("候选绑定意外应用了查询条件。");
                    }
                    finally
                    {
                        foreach (var combo in combos)
                        {
                            combo.SelectedIndexChanged -= selectionChanged;
                            combo.HandleCreated -= handleCreated;
                            combo.DrawItem -= drawItem;
                        }
                    }
                    results.Add(new(variant.Name, failures.Count == 0, failures.ToArray(), setupMs,
                        Describe(samples.Select(sample => sample.BindingMs)), Describe(samples.Select(sample => sample.ReadyMs)),
                        samples, effectiveProperties));
                    Console.WriteLine($"QUERY_CHOICE_BINDING {variant.Name} valid={failures.Count == 0} " +
                        $"bindP50={results[^1].Binding.P50Ms:F2} bindP95={results[^1].Binding.P95Ms:F2} " +
                        $"readyP95={results[^1].Ready.P95Ms:F2}");
                }
            }
            finally
            {
                ResetCombos(combos, original);
                guard.SetValue(form, oldGuard);
                TestSupport.PumpUi();
            }
            TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty && form.WorkspaceForSmokeTest.Commands.CanUndo == hadUndo,
                "候选绑定微基准产生了编辑副作用。");
            TestSupport.Assert(form.QueryInputsForSmokeTest.Select(input => input.Text).SequenceEqual(inputTexts),
                "候选绑定微基准改变了待执行关键词。");
            var report = new
            {
                Schema = "ccz.query-choice-binding.v2", Source = Path.GetFullPath(source), Iterations = iterations,
                VariantFilter = variantFilter,
                WarmupTransitions = 8, ActualDpi = form.DeviceDpi, Window = form.Size.ToString(),
                Font = new { combos[0].Font.Name, combos[0].Font.SizeInPoints, Style = combos[0].Font.Style.ToString() },
                ChoiceRuntimeType = sets[0].Items[0].GetType().FullName,
                SourceScopes = sourceScopes,
                ChoiceSets = sets.Select(set => new { set.Name, Count = set.Items.Length,
                    Labels = set.Items.Select(item => item.ToString()).ToArray() }),
                Results = results,
                RecommendedCandidates = results.Where(result => result.SemanticallyValid).OrderBy(result => result.Ready.P95Ms)
                    .Select(result => result.Variant).ToArray(),
                Notes = "Uses the real three MainForm query ComboBoxes, actual private QueryFieldChoice instances, cached arrays and cached widths. " +
                    "Selected indexes are independently 1/2/3, then the first is changed again to detect shared CurrencyManager coupling. " +
                    "BindingMs measures all three controls; ReadyMs additionally drains layout and paint. " +
                    "Steps reports cumulative Clear/Add(or DataSource)/width/selection/EndUpdate times for those three controls; " +
                    "handle recreation and owner drawing events are counted per transition. Native-style properties change only during setup and are restored. " +
                    "Fixture creation, text measurement, validation and JSON output are outside measured intervals. " +
                    "DataSource variants with shared selection are reported invalid even if fast. No query is applied and no source file is modified."
            };
            File.WriteAllText(Path.Combine(output, "query-choice-binding.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            TestSupport.Assert(results.Single(result => result.Variant == "items-begin-update").SemanticallyValid,
                "基准 Items 方案不满足原候选类型、独立选择和文字语义。");
            Console.WriteLine($"QUERY_CHOICE_BINDING_OK iterations={iterations} variants={results.Count} output={Path.GetFullPath(output)}");
        }
        finally { form.Dispose(); Directory.Delete(copy, true); }
    }

    private static (ChoiceSet[] Sets, object[] Sources) CaptureChoiceSets(MainForm form)
    {
        var pool = new List<object>();
        var sources = new List<object>();
        foreach (var module in new[] { "人物", "策略", "兵种" })
        {
            var tab = form.ModuleTabsForSmokeTest.TabPages.Cast<TabPage>().Single(page => Equals(page.Tag, module));
            if (form.ModuleTabsForSmokeTest.SelectedTab != tab) form.ModuleTabsForSmokeTest.SelectedTab = tab;
            TestSupport.PumpUi();
            Capture();
            if (module == "人物")
            {
                form.ShowPhysicalTableForSmokeTest("persons"); TestSupport.PumpUi(); Capture();
            }
        }
        // Stay on a normal physical view, preserving the shell's real parents, dimensions and font.
        var persons = form.ModuleTabsForSmokeTest.TabPages.Cast<TabPage>().Single(page => Equals(page.Tag, "人物"));
        if (form.ModuleTabsForSmokeTest.SelectedTab != persons) form.ModuleTabsForSmokeTest.SelectedTab = persons;
        form.ShowPhysicalTableForSmokeTest("persons"); TestSupport.PumpUi();
        var distinct = pool.DistinctBy(Key).ToArray();
        TestSupport.Assert(distinct.Length >= 61, "实际查询范围候选不足 61 项，不能覆盖约 30/60 项的切换微基准。");
        return ([Make("small-31", distinct.Take(31).ToArray()), Make("large-61", distinct.Take(61).ToArray())], sources.ToArray());

        void Capture()
        {
            var choices = form.QueryFieldsForSmokeTest[0].Items.Cast<object>().ToArray();
            pool.AddRange(choices);
            sources.Add(new { Scope = form.QueryScopeForSmokeTest.Key, Count = choices.Length });
        }
        ChoiceSet Make(string name, object[] choices)
        {
            var typed = Array.CreateInstance(choices[0].GetType(), choices.Length);
            for (var index = 0; index < choices.Length; index++) typed.SetValue(choices[index], index);
            var storage = choices.Sum(item => ((item.ToString()?.Length ?? 0) + 1) * sizeof(char));
            var width = Math.Min(800, choices.Max(item => TextRenderer.MeasureText(item.ToString(), form.QueryFieldsForSmokeTest[0].Font).Width) + 32);
            return new(name, choices, typed, storage, width);
        }
    }

    private static ReplaceTiming Replace(ComboBox[] combos, Control header, ChoiceSet set, Variant variant)
    {
        var clearMs = 0d;
        var addMs = 0d;
        var widthMs = 0d;
        var selectionMs = 0d;
        var endUpdateMs = 0d;
        var originalVisibility = combos.Select(combo => combo.Visible).ToArray();
        if (variant.HeaderRedraw) SendMessage(header.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        if (variant.ChoiceRedraw)
            foreach (var combo in combos) SendMessage(combo.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            for (var index = 0; index < combos.Length; index++)
            {
                var combo = combos[index];
                if (variant.HideDuringReplace) combo.Visible = false;
                if (variant.BeginUpdate) combo.BeginUpdate();
                try
                {
                    if (variant.InitStorage) SendMessage(combo.Handle, CbInitStorage, (IntPtr)set.Items.Length, (IntPtr)set.StorageBytes);
                    var step = Stopwatch.GetTimestamp();
                    if (variant.DataSource)
                    {
                        combo.DataSource = set.TypedItems;
                        addMs += Stopwatch.GetElapsedTime(step).TotalMilliseconds;
                    }
                    else
                    {
                        combo.Items.Clear();
                        clearMs += Stopwatch.GetElapsedTime(step).TotalMilliseconds;
                        step = Stopwatch.GetTimestamp();
                        combo.Items.AddRange(set.Items);
                        addMs += Stopwatch.GetElapsedTime(step).TotalMilliseconds;
                    }
                    step = Stopwatch.GetTimestamp();
                    combo.DropDownWidth = Math.Max(combo.Width, set.DropDownWidth);
                    widthMs += Stopwatch.GetElapsedTime(step).TotalMilliseconds;
                    step = Stopwatch.GetTimestamp();
                    combo.SelectedIndex = index + 1;
                    selectionMs += Stopwatch.GetElapsedTime(step).TotalMilliseconds;
                }
                finally
                {
                    var step = Stopwatch.GetTimestamp();
                    if (variant.BeginUpdate) combo.EndUpdate();
                    endUpdateMs += Stopwatch.GetElapsedTime(step).TotalMilliseconds;
                    if (variant.HideDuringReplace) combo.Visible = originalVisibility[index];
                }
            }
            // A late change of the first group must preserve the independent selections of groups
            // two and three; merely checking all three at index zero would miss bound-list coupling.
            var finalSelection = Stopwatch.GetTimestamp();
            combos[0].SelectedIndex = 4;
            selectionMs += Stopwatch.GetElapsedTime(finalSelection).TotalMilliseconds;
        }
        finally
        {
            if (variant.ChoiceRedraw)
                foreach (var combo in combos) SendMessage(combo.Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
            if (variant.HeaderRedraw) SendMessage(header.Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
            if (variant.HeaderRedraw || variant.ChoiceRedraw)
                RedrawWindow(header.Handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwErase | RdwAllChildren);
        }
        return new(clearMs, addMs, widthMs, selectionMs, endUpdateMs);
    }

    private static void DrawChoice(ComboBox combo, DrawItemEventArgs args)
    {
        args.DrawBackground();
        var text = args.Index >= 0 && args.Index < combo.Items.Count
            ? combo.GetItemText(combo.Items[args.Index]) : combo.Text;
        var foreground = !combo.Enabled ? SystemColors.GrayText :
            (args.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : combo.ForeColor;
        var bounds = Rectangle.Inflate(args.Bounds, -2, 0);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        if (combo.RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;
        TextRenderer.DrawText(args.Graphics, text, args.Font ?? combo.Font, bounds, foreground, flags);
        args.DrawFocusRectangle();
    }

    private static void Validate(MainForm form, ComboBox[] combos, ChoiceSet expected, string[] inputs, HashSet<string> failures)
    {
        for (var index = 0; index < combos.Length; index++)
        {
            var combo = combos[index];
            var selected = index == 0 ? 4 : index + 1;
            if (combo.Items.Count != expected.Items.Length || !combo.Items.Cast<object>().SequenceEqual(expected.Items))
                failures.Add($"第 {index + 1} 组候选内容或次序改变。");
            if (combo.Items.Cast<object>().Any(item => item.GetType() != expected.Items[0].GetType()))
                failures.Add($"第 {index + 1} 组 Items 实际类型改变。");
            if (combo.SelectedIndex != selected || !Equals(combo.SelectedItem, expected.Items[selected]))
                failures.Add($"第 {index + 1} 组选择联动或 SelectedIndex 不符：期望 {selected}，实际 {combo.SelectedIndex}。");
            if (combo.Text != expected.Items[selected].ToString()) failures.Add($"第 {index + 1} 组显示文字改变。");
            if (!combo.Visible) failures.Add($"第 {index + 1} 组重绘恢复后不可见。");
        }
        if (!form.QueryInputsForSmokeTest.Select(input => input.Text).SequenceEqual(inputs)) failures.Add("待执行的关键词输入改变。");
    }

    private static void ResetCombos(ComboBox[] combos, OriginalChoiceState[] original)
    {
        for (var index = 0; index < combos.Length; index++)
        {
            var combo = combos[index];
            combo.BeginUpdate();
            try
            {
                combo.DataSource = null;
                combo.BindingContext = original[index].BindingContext;
                combo.FlatStyle = original[index].FlatStyle;
                combo.DrawMode = original[index].DrawMode;
                combo.ItemHeight = original[index].ItemHeight;
                combo.MaxDropDownItems = original[index].MaxDropDownItems;
                // DropDownHeight itself switches IntegralHeight off in WinForms.
                combo.DropDownHeight = original[index].DropDownHeight;
                combo.IntegralHeight = original[index].IntegralHeight;
                combo.DisplayMember = string.Empty; combo.ValueMember = string.Empty;
                combo.Items.Clear(); combo.Items.AddRange(original[index].Items);
                combo.DropDownWidth = original[index].DropDownWidth;
                combo.SelectedIndex = original[index].SelectedIndex;
                if (combo.DropDownStyle != ComboBoxStyle.DropDownList) combo.Text = original[index].Text;
            }
            finally { combo.EndUpdate(); }
        }
    }

    private static string Key(object choice)
        => Convert.ToString(choice.GetType().GetProperty("Key")!.GetValue(choice), CultureInfo.InvariantCulture) ?? string.Empty;
    private static Distribution Describe(IEnumerable<double> samples)
    {
        var values = samples.Order().ToArray();
        double At(double percentile) => values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)];
        return new(At(.5), At(.95), values[^1]);
    }
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control control in root.Controls) { yield return control; foreach (var child in Descendants(control)) yield return child; } }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRectangle, IntPtr updateRegion, uint flags);
}
