using System.Runtime.InteropServices;
using DataManager.Core.Binary;

namespace DataManager.App;

/// <summary>Applies the writer's GBK byte constraint to an existing WinForms edit control.</summary>
public sealed class ByteTextInputGuard : NativeWindow, IDisposable
{
    private const int WmChar = 0x102, WmPaste = 0x302, WmUndo = 0x304, EmUndo = 0xC7,
        EmReplaceSel = 0xC2, WmImeStart = 0x10D, WmImeEnd = 0x10E, WmImeComposition = 0x10F,
        GcsResultString = 0x800, EmFirstVisibleLine = 0xCE, EmLineScroll = 0xB6;
    private readonly TextBoxBase _box;
    private readonly Stack<Snapshot> _undo = new(), _redo = new();
    private TextFieldConstraint? _constraint;
    private Snapshot _accepted = new(string.Empty, 0, 0, 0);
    private Snapshot? _compositionStart;
    private string _submitted = string.Empty;
    private bool _changing, _composing, _disposed;
    private int _messageDepth;

    private sealed record Snapshot(string Text, int Start, int Length, int FirstLine);

    public ByteTextInputGuard(TextBoxBase box)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        box.HandleCreated += HandleCreated;
        box.HandleDestroyed += HandleDestroyed;
        box.TextChanged += TextChanged;
        box.Disposed += BoxDisposed;
        _submitted = box.Text; _accepted = Capture();
        if (box.IsHandleCreated) AssignHandle(box.Handle);
    }

    public event EventHandler? StateChanged;
    public event EventHandler? InputRejected;
    public string? RejectionMessage { get; private set; }
    public TextValidationResult? Validation => _constraint?.Evaluate(_box.Text);
    public bool HasPendingChanges => !string.Equals(_submitted, _box.Text, StringComparison.Ordinal);
    public bool IsComposing => _composing;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Bind(TextFieldConstraint constraint, string? text)
    {
        _constraint = constraint ?? throw new ArgumentNullException(nameof(constraint));
        _changing = true;
        try { _box.Text = text ?? string.Empty; _box.Select(_box.TextLength, 0); }
        finally { _changing = false; }
        _composing = false; _compositionStart = null;
        _submitted = _box.Text; _accepted = Capture();
        _undo.Clear(); _redo.Clear(); RejectionMessage = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkSubmitted()
    {
        _submitted = _box.Text; _accepted = Capture(); _undo.Clear(); _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Prepare()
    {
        if (_composing && _box.IsHandleCreated)
        {
            var context = ImmGetContext(_box.Handle);
            if (context != IntPtr.Zero)
            {
                try { ImmNotifyIME(context, 0x15, 1, 0); } // NI_COMPOSITIONSTR / CPS_COMPLETE
                finally { ImmReleaseContext(_box.Handle, context); }
            }
            if (_composing) FinishComposition();
        }
        if (!HasPendingChanges) return true;
        var valid = Validation;
        if (valid is { CanEncode: false }) { Reject(valid.Error ?? "文本无法写入当前字段。"); return false; }
        return true;
    }

    /// <summary>The entire replacement is accepted or rejected, including clipboard pastes.</summary>
    public bool TryReplaceSelection(string? replacement)
    {
        if (_box.ReadOnly || !_box.Enabled) return false;
        replacement ??= string.Empty;
        var before = Capture();
        var candidate = Replace(before, replacement);
        if (!_box.Multiline && replacement.IndexOfAny(['\r', '\n']) >= 0)
        { Reject("此单行字段不能接收换行文本。"); return false; }
        if (!CanAccept(candidate, before.Text, out var error)) { Reject(error); return false; }
        _box.SelectedText = replacement;
        return string.Equals(_box.Text, candidate, StringComparison.Ordinal);
    }

    public bool Undo(bool redo = false)
    {
        if (_composing || _box.ReadOnly) return false;
        var source = redo ? _redo : _undo; var destination = redo ? _undo : _redo;
        if (source.Count == 0) return false;
        destination.Push(Capture());
        var target = source.Pop(); Restore(target); _accepted = Capture(); RejectionMessage = null;
        StateChanged?.Invoke(this, EventArgs.Empty); return true;
    }

    private void HandleCreated(object? sender, EventArgs e) { if (!_disposed) AssignHandle(_box.Handle); }
    private void HandleDestroyed(object? sender, EventArgs e) { if (Handle != IntPtr.Zero) ReleaseHandle(); }
    private void BoxDisposed(object? sender, EventArgs e) => Dispose();

    protected override void WndProc(ref Message m)
    {
        if (_changing || _constraint == null) { base.WndProc(ref m); return; }
        if (m.Msg == 0xC6) { m.Result = CanUndo ? (IntPtr)1 : IntPtr.Zero; return; } // EM_CANUNDO, including the native context menu
        if (m.Msg == WmUndo || m.Msg == EmUndo) { m.Result = Undo() ? (IntPtr)1 : IntPtr.Zero; return; }
        if (m.Msg == WmImeStart)
        {
            _compositionStart = Capture(); _composing = true;
            base.WndProc(ref m); return;
        }
        if (m.Msg == WmImeComposition && ((long)m.LParam & GcsResultString) != 0 && _compositionStart != null)
        {
            var context = ImmGetContext(_box.Handle);
            if (context != IntPtr.Zero)
            {
                try
                {
                    var bytes = ImmGetCompositionStringW(context, GcsResultString, null, 0);
                    if (bytes >= 0)
                    {
                        var buffer = new byte[bytes]; ImmGetCompositionStringW(context, GcsResultString, buffer, buffer.Length);
                        var result = System.Text.Encoding.Unicode.GetString(buffer);
                        if (!CanAccept(Replace(_compositionStart, result), _compositionStart.Text, out var error))
                        {
                            var previous = _compositionStart;
                            ImmNotifyIME(context, 0x15, 4, 0); // CPS_CANCEL, consume the complete invalid result
                            _composing = false; _compositionStart = null; Restore(previous); _accepted = Capture();
                            Reject(error); m.Result = IntPtr.Zero; return;
                        }
                    }
                }
                finally { ImmReleaseContext(_box.Handle, context); }
            }
        }
        if (m.Msg == WmImeEnd)
        {
            base.WndProc(ref m); FinishComposition(); return;
        }
        if (m.Msg == WmPaste && !_composing)
        {
            try { TryReplaceSelection(Clipboard.GetText(TextDataFormat.UnicodeText)); }
            catch (ExternalException) { Reject("暂时无法读取剪贴板，请重试粘贴。"); }
            m.Result = IntPtr.Zero; return;
        }
        if (m.Msg == WmChar && !_composing && !_box.ReadOnly)
        {
            var character = (char)(int)m.WParam;
            var textBox = _box as TextBox;
            var insertion = character == '\r' && _box.Multiline ? "\r\n"
                : character == '\t' && textBox?.AcceptsTab == true ? "\t"
                : !char.IsControl(character) ? character.ToString() : null;
            if (insertion != null && !CanAccept(Replace(Capture(), insertion), _box.Text, out var error))
            { Reject(error); m.Result = IntPtr.Zero; return; }
        }
        if (m.Msg == EmReplaceSel && !_composing && !_box.ReadOnly)
        {
            var insertion = Marshal.PtrToStringUni(m.LParam) ?? string.Empty;
            if (!_box.Multiline && insertion.IndexOfAny(['\r', '\n']) >= 0)
            { Reject("此单行字段不能接收换行文本。"); m.Result = IntPtr.Zero; return; }
            if (!CanAccept(Replace(Capture(), insertion), _box.Text, out var error))
            { Reject(error); m.Result = IntPtr.Zero; return; }
        }
        // Capture queries send edit messages themselves. Only surround messages
        // which can mutate text, otherwise a first-visible-line query recurses.
        if (m.Msg is not (WmChar or EmReplaceSel or 0x100 or 0x300 or 0x303 or 0xC))
        { base.WndProc(ref m); return; }
        var before = Capture();
        _messageDepth++;
        try { base.WndProc(ref m); }
        finally
        {
            _messageDepth--;
            if (_messageDepth == 0 && !_composing && !_changing && _box.Text != _accepted.Text)
                AcceptOrRestore(before);
        }
    }

    private void FinishComposition()
    {
        var before = _compositionStart ?? _accepted;
        _composing = false; _compositionStart = null;
        AcceptOrRestore(before);
    }

    private void TextChanged(object? sender, EventArgs e)
    {
        if (_changing || _composing || _messageDepth > 0) return;
        AcceptOrRestore(_accepted);
    }

    private void AcceptOrRestore(Snapshot before)
    {
        if (_box.Text == _accepted.Text) return;
        if (!CanAccept(_box.Text, before.Text, out var error))
        { Restore(before); _accepted = Capture(); Reject(error); return; }
        _undo.Push(before); _redo.Clear(); _accepted = Capture(); RejectionMessage = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanAccept(string candidate, string previous, out string error)
    {
        var result = _constraint?.Evaluate(candidate);
        error = result?.Error ?? "输入超过该字段的字节容量。";
        if (result == null || result.CanEncode) return true;
        // Existing malformed text is displayed intact; permit reducing it so it can be repaired.
        return _constraint!.Evaluate(previous).CanEncode == false && candidate.Length < previous.Length
            && IsDeletion(previous, candidate);
    }

    private static bool IsDeletion(string previous, string candidate)
    {
        var position = 0;
        foreach (var character in previous)
            if (position < candidate.Length && candidate[position] == character) position++;
        return position == candidate.Length;
    }

    private static string Replace(Snapshot before, string insertion)
        => before.Text.Remove(before.Start, before.Length).Insert(before.Start, insertion);

    private Snapshot Capture() => new(_box.Text, _box.SelectionStart, _box.SelectionLength,
        _box.IsHandleCreated ? (int)SendMessage(_box.Handle, EmFirstVisibleLine, IntPtr.Zero, IntPtr.Zero) : 0);

    private void Restore(Snapshot snapshot)
    {
        _changing = true;
        try
        {
            _box.Text = snapshot.Text;
            _box.Select(Math.Min(snapshot.Start, _box.TextLength), Math.Min(snapshot.Length, _box.TextLength - Math.Min(snapshot.Start, _box.TextLength)));
            if (_box.IsHandleCreated && _box.Multiline)
            {
                var current = (int)SendMessage(_box.Handle, EmFirstVisibleLine, IntPtr.Zero, IntPtr.Zero);
                SendMessage(_box.Handle, EmLineScroll, IntPtr.Zero, (IntPtr)(snapshot.FirstLine - current));
            }
        }
        finally { _changing = false; }
    }

    private void Reject(string error)
    {
        RejectionMessage = error + " 本次输入已整次拒绝。";
        InputRejected?.Invoke(this, EventArgs.Empty); StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _box.HandleCreated -= HandleCreated; _box.HandleDestroyed -= HandleDestroyed;
        _box.TextChanged -= TextChanged; _box.Disposed -= BoxDisposed;
        if (Handle != IntPtr.Zero) ReleaseHandle();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr window);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr window, IntPtr context);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionStringW(IntPtr context, int index, byte[]? buffer, int length);
    [DllImport("imm32.dll")] private static extern bool ImmNotifyIME(IntPtr context, int action, int index, int value);
}

public sealed class ByteLimitedTextBox : TextBox
{
    public ByteTextInputGuard InputGuard { get; }
    public ByteLimitedTextBox() { InputGuard = new ByteTextInputGuard(this); AllowDrop = false; MaxLength = 0; }
    public void SetBoundText(TextFieldConstraint constraint, string? text) => InputGuard.Bind(constraint, text);
}

/// <summary>A regular textbox with an always-visible byte budget beneath it.</summary>
public sealed class ByteTextEditor : UserControl
{
    public const string EncodingHint = "1个汉字2字节，1个字母/数字1字节";
    private readonly Label _budget = new() { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _encodingHint = new() { Dock = DockStyle.Bottom, Text = EncodingHint, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    private bool _compactBudget;
    private bool _arrangingBudget;
    private string _evidence = string.Empty;
    public ByteLimitedTextBox Editor { get; } = new() { Dock = DockStyle.Fill };
    public bool CompactBudget
    {
        get => _compactBudget;
        set
        {
            _compactBudget = value;
            _budget.Dock = value ? DockStyle.Right : DockStyle.Bottom;
            _budget.Width = TextRenderer.MeasureText("剩余 999 字节", Font).Width + 4;
            PerformLayout(); RefreshBudget();
        }
    }
    public ByteTextEditor()
    {
        Controls.Add(Editor); Controls.Add(_budget); Controls.Add(_encodingHint); Height = 48; Margin = Padding.Empty;
        Editor.InputGuard.StateChanged += (_, _) => RefreshBudget();
        FontChanged += (_, _) => { if (_compactBudget) CompactBudget = true; else PerformLayout(); };
    }
    public void Bind(TextFieldConstraint constraint, string? text) { _evidence = constraint.Evidence; Editor.SetBoundText(constraint, text); RefreshBudget(); }
    private void RefreshBudget()
    {
        var validation = Editor.InputGuard.Validation;
        if (validation == null) return;
        var count = validation.ByteCount?.ToString() ?? "?";
        var fullBudget = $"已用 {count} / {validation.Capacity} 字节 · 剩余 {validation.RemainingBytes} 字节";
        var preservedOriginal = !validation.CanEncode && !Editor.InputGuard.HasPendingChanges;
        _budget.Text = _compactBudget ? validation.CanEncode ? $"剩余 {validation.RemainingBytes} 字节" : preservedOriginal ? "原文异常" : "不可保存"
            : validation.CanEncode ? fullBudget
            : preservedOriginal ? $"原文 {count} / {validation.Capacity} 字节 · 未修改会保留，编辑时请修复"
            : $"无法保存：{validation.Error}";
        if (_compactBudget) _budget.Width = TextRenderer.MeasureText(_budget.Text, _budget.Font).Width + 4;
        if (!_compactBudget && validation.CanEncode && Editor.InputGuard.RejectionMessage != null) _budget.Text += " · 本次输入已拒绝";
        _budget.ForeColor = !validation.CanEncode ? Color.Firebrick
            : validation.RemainingBytes <= Math.Max(2, validation.Capacity / 10) ? Color.DarkOrange : Color.DimGray;
        var detail = string.Join(Environment.NewLine, new[] { fullBudget, EncodingHint, Editor.InputGuard.RejectionMessage, validation.Error, validation.Warning,
            preservedOriginal ? "原始文本存在长度或编码异常；未修改会保留原始字节，编辑时须修复。" : null, _evidence }.Where(text => !string.IsNullOrWhiteSpace(text)));
        _tips.SetToolTip(_budget, detail); _tips.SetToolTip(_encodingHint, detail); _tips.SetToolTip(Editor, detail);
        PerformLayout();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (!_arrangingBudget)
        {
            _arrangingBudget = true;
            try
            {
                var width = Math.Max(1, ClientSize.Width);
                int WrappedHeight(Label label) => Math.Max(Font.Height + 4,
                    TextRenderer.MeasureText(label.Text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 4);
                _encodingHint.Height = WrappedHeight(_encodingHint);
                _budget.Height = _compactBudget ? Editor.PreferredHeight : WrappedHeight(_budget);
                MinimumSize = new Size(_compactBudget ? TextRenderer.MeasureText("夏侯惇", Font).Width + _budget.Width + 12 : 0,
                    Editor.PreferredHeight + _encodingHint.Height + (_compactBudget ? 0 : _budget.Height));
                if (_compactBudget && Dock == DockStyle.None) Height = MinimumSize.Height;
            }
            finally { _arrangingBudget = false; }
        }
        base.OnLayout(e);
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = Math.Max(1, proposedSize.Width is > 0 and < int.MaxValue ? proposedSize.Width : Width);
        int WrappedHeight(Label label) => Math.Max(Font.Height + 4,
            TextRenderer.MeasureText(label.Text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 4);
        return new Size(Math.Max(MinimumSize.Width, width), Editor.PreferredHeight + WrappedHeight(_encodingHint) +
            (_compactBudget ? 0 : WrappedHeight(_budget)));
    }
    protected override void Dispose(bool disposing) { if (disposing) _tips.Dispose(); base.Dispose(disposing); }
}
