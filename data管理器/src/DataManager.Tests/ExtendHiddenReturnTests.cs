using System.Data;
using System.Diagnostics;
using System.Reflection;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class ExtendHiddenReturnTests
{
    public static void Run()
    {
        var workspace = CreateWorkspace(out var effects, out var persons);
        using var panel = new BusinessEditorPanel(workspace);
        using var parent = new Panel { Dock = DockStyle.Fill };
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Size = new Size(1000, 700) };
        parent.Controls.Add(panel); form.Controls.Add(parent); form.Show();
        panel.Bind(BusinessPageKind.ExtendData, 0); Drain();
        var editor = panel.ExtendEditor!;
        var ordinaryTabs = (TabControl)Field("_tabs").GetValue(panel)!;
        var untouchedPage = new TabPage("同类返回不得清理普通页集合");
        ordinaryTabs.TabPages.Add(untouchedPage);
        var visibleChanges = 0;
        editor.VisibleChanged += (_, _) => visibleChanges++;
        ordinaryTabs.VisibleChanged += (_, _) => visibleChanges++;
        var visibleLayouts = 0;
        panel.Layout += (_, args) =>
        { if (args.AffectedProperty == "Visible" && (ReferenceEquals(args.AffectedControl, editor) || ReferenceEquals(args.AffectedControl, ordinaryTabs))) visibleLayouts++; };
        var value = Find<TextBox>(editor, "ExtendEffectValue0");
        var id = Find<SearchableIdComboBox>(editor, "ExtendEffectId0");
        TestSupport.Assert(value.Text == "7" && editor.CurrentId == 0 && !editor.HasPendingChanges, "内存拓展夹具没有建立真实字段绑定");
        value.SelectionStart = 0; value.SelectionLength = 1;
        var selection = (value.SelectionStart, value.SelectionLength);
        var controls = Descendants(editor).ToArray();
        parent.Visible = false; panel.DeactivateView(); Drain();
        TestSupport.Assert(!editor.Visible, "未建立所属父级隐藏的拓展页状态");
        visibleChanges = visibleLayouts = 0;
        panel.Bind(BusinessPageKind.ExtendData, 0); Drain();
        TestSupport.Assert(visibleChanges == 0 && visibleLayouts == 0 && !untouchedPage.IsDisposed && ordinaryTabs.TabPages.Contains(untouchedPage) && controls.SequenceEqual(Descendants(editor)) &&
            ReferenceEquals(value, Find<TextBox>(editor, "ExtendEffectValue0")) &&
            ReferenceEquals(id, Find<SearchableIdComboBox>(editor, "ExtendEffectId0")) &&
            selection == (value.SelectionStart, value.SelectionLength),
            $"隐藏拓展页同ID返回翻转内部显隐或重建字段：visible-changes={visibleChanges}, visible-layouts={visibleLayouts}, ordinary-page-disposed={untouchedPage.IsDisposed}");
        parent.Visible = true; panel.ActivateView(); Drain();
        TestSupport.Assert(editor.Visible && !((Control)Field("_tabs").GetValue(panel)!).Visible,
            "拓展缓存返回没有恢复正确内容宿主");

        parent.Visible = false; panel.DeactivateView(); Drain(); visibleChanges = visibleLayouts = 0;
        panel.Bind(BusinessPageKind.ExtendData, 1); Drain();
        TestSupport.Assert(editor.CurrentId == 1 && value.Text == "8" && visibleChanges == 0 && visibleLayouts == 0 &&
            ReferenceEquals(value, Find<TextBox>(editor, "ExtendEffectValue0")), "隐藏拓展页改变实体ID没有刷新原字段");
        var beforeRevision = effects.Revision;
        effects.Data.Rows[1]["槽0特效值"] = 21;
        persons.Data.Rows[1]["名称"] = "名称修订";
        panel.Bind(BusinessPageKind.ExtendData, 1); Drain();
        TestSupport.Assert(effects.Revision > beforeRevision && value.Text == "21" &&
            Find<Label>(editor, "ExtendEntityTitle").Text == "1：名称修订" && visibleChanges == 0,
            "隐藏拓展页没有读取新文档版本或名称版本");
        value.Text = "-"; value.SelectionStart = 1; value.SelectionLength = 0;
        TestSupport.Assert(editor.HasPendingChanges, "未建立拓展未完成输入");
        panel.Bind(BusinessPageKind.ExtendData, 0); Drain();
        TestSupport.Assert(editor.CurrentId == 1 && value.Text == "-" && value.SelectionStart == 1 &&
            editor.HasPendingChanges && !editor.Flush() && visibleChanges == 0 &&
            Convert.ToInt32(effects.Data.Rows[1]["槽0特效值"]) == 21,
            "隐藏拓展页缓存返回绕过校验或丢弃未完成输入");
        parent.Visible = true; panel.ActivateView(); Drain();
        TestSupport.Assert(value.Text == "-" && value.SelectionStart == 1, "重新显示拓展页丢失未完成输入");
        editor.DiscardPendingChanges();
        panel.Bind(BusinessPageKind.Shop, 0); Drain();
        TestSupport.Assert(panel.CurrentKind == BusinessPageKind.Shop && !editor.Visible &&
            ((Control)Field("_tabs").GetValue(panel)!).Visible, "跨模块切换没有隐藏拓展页并显示普通页");
        panel.Bind(BusinessPageKind.ExtendData, 0); Drain();
        TestSupport.Assert(panel.CurrentKind == BusinessPageKind.ExtendData && editor.Visible && editor.CurrentId == 0 &&
            value.Text == "7" && !((Control)Field("_tabs").GetValue(panel)!).Visible,
            "跨模块返回拓展页没有恢复正常绑定");
        Console.WriteLine("EXTEND_HIDDEN_RETURN_OK same-id=zero-visibility-layout controls/selection=retained ordinary-tabs=untouched changed-id/revision/names=refreshed partial-input=validated kind-switch=preserved fixture=memory");
    }

    internal static ProjectWorkspace CreateWorkspace(out TableDocument effects, out TableDocument persons)
    {
        var workspace = new ProjectWorkspace();
        typeof(ProjectWorkspace).GetProperty(nameof(ProjectWorkspace.RootPath))!.SetValue(workspace,
            Path.Combine(Path.GetTempPath(), "ccz-memory-extend-" + Guid.NewGuid().ToString("N")));
        var definition = workspace.Definitions.Single(table => table.Key == "effect-archive") with { RowCount = 2, DataOffset = 0 };
        var bytes = new byte[definition.RowSize * 2];
        for (var row = 0; row < 2; row++)
        {
            for (var slot = 0; slot < 256; slot++)
            { bytes[row * definition.RowSize + slot * 4] = 255; bytes[row * definition.RowSize + slot * 4 + 3] = 255; }
            bytes[row * definition.RowSize] = 1; bytes[row * definition.RowSize + 1] = (byte)(7 + row);
        }
        var data = new DataTable(); data.Columns.Add("ID", typeof(int));
        foreach (var field in definition.Fields) data.Columns.Add(field.Name, typeof(int));
        for (var row = 0; row < 2; row++)
        {
            var values = new object[definition.Fields.Count + 1]; values[0] = row;
            for (var column = 0; column < definition.Fields.Count; column++)
                values[column + 1] = BinaryValueCodec.Decode(bytes.AsSpan(row * definition.RowSize, definition.RowSize), definition.Fields[column])!;
            data.Rows.Add(values);
        }
        effects = new() { Definition = definition, Data = data, OriginalFileBytes = bytes, OriginalFileHash = string.Empty,
            FilePath = Path.Combine(workspace.RootPath, definition.FileName) };
        effects.CaptureOriginalValues();
        var names = new DataTable(); names.Columns.Add("ID", typeof(int)); names.Columns.Add("名称", typeof(string));
        names.Rows.Add(0, "内存人物零"); names.Rows.Add(1, "内存人物一");
        persons = new() { Definition = new("persons", "内存人物", "Persons.synthetic", 0, 2, 12, 0,
                [new("名称", 0, 12, FieldKind.FixedString)], "synthetic"), Data = names, OriginalFileBytes = new byte[24],
            OriginalFileHash = string.Empty, FilePath = Path.Combine(workspace.RootPath, "Persons.synthetic") };
        persons.CaptureOriginalValues();
        var documents = (Dictionary<string, TableDocument>)typeof(ProjectWorkspace).GetField("_documents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
        documents.Add(effects.Definition.Key, effects); documents.Add(persons.Definition.Key, persons);
        return workspace;
    }

    private static FieldInfo Field(string name) => typeof(BusinessEditorPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Find<T>(Control root, string name) where T : Control => Descendants(root).OfType<T>().Single(control => control.Name == name);
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    private static void Drain()
    { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < 40) { Application.DoEvents(); Thread.Sleep(1); } }
}
