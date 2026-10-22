using System.Runtime.InteropServices;
using System.Text;
using DataManager.App;
using DataManager.Core.Binary;

namespace DataManager.Tests;

internal static class ByteTextInputTests
{
    public static void Run()
    {
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        using var box = new ByteLimitedTextBox { Multiline = true, Dock = DockStyle.Fill };
        form.Controls.Add(box); form.Show(); Application.DoEvents();
        var guard = box.InputGuard;
        var limit = new TextFieldConstraint(6, 6, false, "测试槽");
        guard.Bind(limit, "汉A"); box.Select(1, 1);
        TestSupport.Assert(guard.TryReplaceSelection("汉") && box.Text == "汉汉" && guard.Validation?.ByteCount == 4, "GBK字节输入/选择替换错误");
        box.Select(1, 1); var previousSelection = (box.SelectionStart, box.SelectionLength);
        TestSupport.Assert(!guard.TryReplaceSelection("测试汉字") && box.Text == "汉汉" && (box.SelectionStart, box.SelectionLength) == previousSelection,
            "超长整次替换被截断或破坏了选区");
        TestSupport.Assert(SendMessage(box.Handle, 0xC6, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero, "拒绝输入导致原生右键撤销菜单不可用");
        TestSupport.Assert(guard.Undo() && box.Text == "汉A" && box.SelectionStart == 1 && box.SelectionLength == 1,
            "拒绝输入破坏了先前编辑的撤销/选区");
        TestSupport.Assert(guard.Undo(redo: true) && box.Text == "汉汉", "重做不能恢复有效编辑");
        box.Select(0, box.TextLength);
        TestSupport.Assert(!guard.TryReplaceSelection("🙂") && box.Text == "汉汉", "GBK无法表示的字符被问号代写");
        guard.Bind(limit, "汉汉汉"); box.Select(box.TextLength, 0);
        SendMessage(box.Handle, 0x102, (IntPtr)'A', IntPtr.Zero);
        TestSupport.Assert(box.Text == "汉汉汉" && guard.Validation?.RemainingBytes == 0, "满字节后额外键入未被阻止");
        box.Select(1, 1); box.SelectedText = "AB";
        TestSupport.Assert(box.Text == "汉AB汉" && guard.HasPendingChanges, "等字节原生替换未生效");
        SendMessage(box.Handle, 0x304, IntPtr.Zero, IntPtr.Zero);
        TestSupport.Assert(box.Text == "汉汉汉", "原生撤销未保留有效历史");
        guard.Bind(limit, "汉A"); box.Select(1, 1);
        SendMessage(box.Handle, 0x10D, IntPtr.Zero, IntPtr.Zero);
        box.Text = "测试汉字";
        SendMessage(box.Handle, 0x10E, IntPtr.Zero, IntPtr.Zero);
        TestSupport.Assert(box.Text == "汉A" && box.SelectionStart == 1 && box.SelectionLength == 1, "IME提交后兜底未整次还原超限输入");
        guard.Bind(limit, "汉汉汉汉");
        TestSupport.Assert(box.Text == "汉汉汉汉" && !guard.HasPendingChanges && guard.Prepare(), "加载存量超预算文本时被截断或阻止其他字段保存");
        box.Select(3, 1); TestSupport.Assert(guard.TryReplaceSelection("") && box.Text == "汉汉汉", "存量异常文本不能通过删除修复");
        guard.MarkSubmitted(); TestSupport.Assert(!guard.HasPendingChanges && guard.Prepare(), "提交后草稿状态错误");
        CheckMultilineExactText(box);
        box.Multiline = false; guard.Bind(limit, "原文"); box.SelectAll();
        TestSupport.Assert(!guard.TryReplaceSelection("A\r\nB") && box.Text == "原文", "单行字段粘贴换行文本被隐式截断");
        form.Close(); Console.WriteLine("BYTE_TEXT_INPUT_TESTS_OK");
    }

    private static void CheckMultilineExactText(ByteLimitedTextBox box)
    {
        box.Multiline = true;
        var guard = box.InputGuard;
        var limit = new TextFieldConstraint(200, 200, false, "多行控件原样保存测试槽");
        foreach (var (name, value) in new[]
        {
            ("独立CR", "甲\r乙  "),
            ("独立LF", "甲\n乙  "),
            ("CRLF", "甲\r\n乙  "),
            ("控制码01及1A", "甲\u0001乙\u001A  "),
            ("混合换行及控制码", "甲\r乙\n丙\r\n丁\u0001戊\u001A  ")
        })
        {
            guard.Bind(limit, value);
            AssertNativeText(box, value, name + "/Bind");
            TestSupport.Assert(!guard.HasPendingChanges && guard.Prepare(), name + "绑定后产生意外草稿");
            box.Select(1, 0);
            TestSupport.Assert(guard.TryReplaceSelection("插"), name + "已有文本中的选区插入失败");
            AssertNativeText(box, value.Insert(1, "插"), name + "/保留原文插入");
            TestSupport.Assert(guard.Undo(), name + "插入后无法撤回");
            AssertNativeText(box, value, name + "/撤回");
            guard.Bind(limit, "L[R]T  "); box.Select(1, 3);
            TestSupport.Assert(guard.TryReplaceSelection(value), name + "整次多行选区替换失败");
            AssertNativeText(box, "L" + value + "T  ", name + "/guard选区替换");
            TestSupport.Assert(guard.Prepare(), name + "替换后无法准备保存");
            guard.Bind(limit, "Q#Z"); box.Select(1, 1); box.SelectedText = value;
            AssertNativeText(box, "Q" + value + "Z", name + "/原生SelectedText");
            guard.MarkSubmitted();
            TestSupport.Assert(!guard.HasPendingChanges && guard.Prepare(), name + "提交后状态错误");
        }
        Console.WriteLine("MULTILINE_NATIVE_TEXT_PRESERVATION_OK CR LF CRLF trailing-spaces 01 1A");
    }

    private static void AssertNativeText(TextBox box, string expected, string phase)
    {
        var native = new StringBuilder(GetWindowTextLength(box.Handle) + 1);
        GetWindowText(box.Handle, native, native.Capacity);
        var actual = box.Text;
        TestSupport.Assert(actual == expected && native.ToString() == expected,
            $"{phase}改变文本：expected={Convert.ToHexString(EncodingService.GameEncoding.GetBytes(expected))}，managed={Convert.ToHexString(EncodingService.GameEncoding.GetBytes(actual))}，native={Convert.ToHexString(EncodingService.GameEncoding.GetBytes(native.ToString()))}");
        TestSupport.Assert(box is not ByteLimitedTextBox limited || limited.InputGuard.Validation?.ByteCount == EncodingService.GameEncoding.GetByteCount(expected),
            phase + "字节计数与原始文本不一致");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
