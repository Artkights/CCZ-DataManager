using System.Globalization;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

/// <summary>Exercises the real strategy extension bit editor against isolated fixture copies.</summary>
internal static class StrategyExtensionUiTests
{
    internal static void Run(string basePath)
    {
        var repository = TestSupport.RepositoryRootFromBase(basePath);
        foreach (var name in new[] { "洗隋", "加强版6.5未加密版", "复刻版6.6山贼" })
        {
            var source = Path.Combine(repository, "基底", name);
            var originalHashes = Known65FixtureSnapshot.CoreNames.ToDictionary(file => file,
                file => HashService.Sha256(ProjectFileLocator.Resolve(source, file)));
            var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
            MainForm? form = null;
            try
            {
                var before = Snapshot(root);
                form = new MainForm(false)
                {
                    StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                    ShowInTaskbar = false, MaximumSize = new Size(4096, 2160)
                };
                form.Show();
                form.LoadProjectForSmokeTest(root);
                form.SelectModuleForSmokeTest("策略");
                Settle(form);
                var panel = form.BusinessEditorForSmokeTest;
                var workspace = form.WorkspaceForSmokeTest;
                switch (workspace.ActiveProfile.ProfileId)
                {
                    case "ccz-6.6": Assert66(form, panel, workspace, root, before); break;
                    case "ccz-6.5": Assert65(panel, workspace); break;
                    case "ccz-6.4": Assert64(panel, workspace); break;
                    default: throw new InvalidOperationException("未预期的策略基底画像：" + workspace.ActiveProfile.ProfileId);
                }
                AssertSemanticTables();
                TestSupport.Assert(!workspace.IsDirty && !panel.HasPendingChanges,
                    "策略扩展位只读检查留下了草稿或待提交输入。");
                Console.WriteLine($"STRATEGY_EXTENSION_FIXTURE_OK {name} profile={workspace.ActiveProfile.ProfileId}");
            }
            finally { form?.Dispose(); Directory.Delete(root, true); }
            foreach (var pair in originalHashes)
                TestSupport.Assert(HashService.Sha256(ProjectFileLocator.Resolve(source, pair.Key)) == pair.Value,
                    "策略扩展位测试改变了原始基底：" + name + "/" + pair.Key);
        }
        Console.WriteLine("STRATEGY_EXTENSION_TESTS_OK three bases bit0-bit1 reserved single-byte rebind");
    }

    private static void Assert66(MainForm form, BusinessEditorPanel panel, ProjectWorkspace workspace,
        string root, IReadOnlyDictionary<string, byte[]> before)
    {
        var extension = workspace.GetDocument(StrategyExtensionSemantics.TableKey);
        TestSupport.Assert(extension.Definition is
        {
            DataOffset: 0xA2CE0, RowCount: 144, RowSize: 1, Capability: TableCapability.Editable
        }, "6.6 策略扩展位没有按 Ekd5 0xA2CE0 的 144×1 可写表加载。");
        var business = new BusinessDataService(workspace);
        TestSupport.Assert(business.GetAvailableTableKeys(BusinessPageKind.Strategies)
            .Contains(StrategyExtensionSemantics.TableKey), "6.6 策略页丢失了策略扩展位表。");

        // Seed reserved bit7 only in the isolated copy's draft. This keeps the
        // executable fingerprint intact while proving the editor preserves bits
        // it does not display.
        var seeded = workspace.ApplyCompositeTransaction("预置策略扩展保留位",
            [new(StrategyExtensionSemantics.TableKey, 0, "内容", 0x83)]);
        TestSupport.Assert(seeded.Applied && Value(extension, 0) == 0x83,
            "无法在副本草稿中预置策略扩展位 0x83。");

        panel.Bind(BusinessPageKind.Strategies, 0); Settle(form);
        var checks = ExtensionChecks(panel);
        TestSupport.Assert(checks.Select(check => check.Name).SequenceEqual(new[]
                { "Flag_strategy-extension_0", "Flag_strategy-extension_1" }) &&
            checks.Select(check => check.Text).SequenceEqual(new[] { "策略模仿", "辅助穿透" }) &&
            checks.All(check => check.Checked),
            "6.6 策略 0 没有显示两个选中的 bit0/bit1 复选框。");

        checks[0].Checked = false; Settle(form);
        var changes = Changes(workspace);
        TestSupport.Assert(changes.Length == 1 && changes[0].Table.Key == StrategyExtensionSemantics.TableKey &&
            changes[0].RowIndex == 0 && changes[0].Field.Name == "内容" && Value(extension, 0) == 0x82,
            "取消 bit0 没有只生成 0x82 的单字段草稿。");

        // Rebinding another strategy must not commit its checkbox states as a
        // transient draft; the existing row-0 draft must remain the only change.
        panel.Bind(BusinessPageKind.Strategies, 104); Settle(form);
        changes = Changes(workspace);
        TestSupport.Assert(changes.Length == 1 && changes[0].RowIndex == 0 && Value(extension, 0) == 0x82,
            "实体切换把策略 104 的中间复选框状态写成了瞬态草稿。");
        checks = ExtensionChecks(panel);
        TestSupport.Assert(!checks[0].Checked && checks[1].Checked,
            "策略 104（原值 0x02）没有显示为仅 bit1 选中。");

        panel.Bind(BusinessPageKind.Strategies, 0); Settle(form);
        checks = ExtensionChecks(panel);
        TestSupport.Assert(!checks[0].Checked && checks[1].Checked && Value(extension, 0) == 0x82,
            "返回策略 0 后丢失了 0x82 草稿或复选框状态。");

        var saved = workspace.Save();
        TestSupport.Assert(saved.Saved && saved.ChangedFields == 1 && saved.ChangedBytes == 1,
            "策略扩展位首次保存没有按单字段单字节提交：" + string.Join("；", saved.Messages));
        AssertOnlyOffsetChanged(root, before, 0xA2CE0, 0x82);

        checks = ExtensionChecks(panel);
        checks[1].Checked = false; Settle(form);
        TestSupport.Assert(Value(extension, 0) == 0x80, "取消 bit1 没有保留 bit7 并写为 0x80。");
        var savedAgain = workspace.Save();
        TestSupport.Assert(savedAgain.Saved && savedAgain.ChangedBytes == 1,
            "策略扩展位第二次保存没有按单字节提交：" + string.Join("；", savedAgain.Messages));
        AssertOnlyOffsetChanged(root, before, 0xA2CE0, 0x80);
    }

    private static void Assert65(BusinessEditorPanel panel, ProjectWorkspace workspace)
    {
        var business = new BusinessDataService(workspace);
        TestSupport.Assert(workspace.Documents.ContainsKey(StrategyExtensionSemantics.TableKey) &&
            !business.GetAvailableTableKeys(BusinessPageKind.Strategies).Contains(StrategyExtensionSemantics.TableKey),
            "6.5 应保留扩展位物理文档，但不得放进策略业务页。");
        panel.Bind(BusinessPageKind.Strategies, 0);
        TestSupport.Assert(ExtensionChecks(panel).Length == 0, "6.5 策略页暴露了逐策略扩展位。");
        TestSupport.Assert(Descendants(panel.DetailTabs.SelectedTab!).OfType<CheckBox>()
            .Count(check => check.Name.StartsWith("Flag_strategy-learning_", StringComparison.Ordinal)) == 8,
            "6.5 策略页的 AI 类别掩码八位复选框不完整。");
    }

    private static void Assert64(BusinessEditorPanel panel, ProjectWorkspace workspace)
    {
        TestSupport.Assert(!workspace.Documents.ContainsKey(StrategyExtensionSemantics.TableKey),
            "6.4 加载了未验证的策略扩展区域。");
        panel.Bind(BusinessPageKind.Strategies, 0);
        TestSupport.Assert(ExtensionChecks(panel).Length == 0, "6.4 策略页暴露了逐策略扩展位。");
    }

    private static void AssertSemanticTables()
    {
        TestSupport.Assert(StrategyExtensionSemantics.Penetration65.SequenceEqual(new byte[]
            { 0x01, 0x02, 0x03, 0x06, 0x07, 0x04, 0x06, 0x05, 0x08 }), "6.5 策略穿透表常量错误。");
        TestSupport.Assert(StrategyExtensionSemantics.Penetration66.SequenceEqual(new byte[]
            { 0x01, 0x02, 0x03, 0x08, 0x07, 0x05, 0x06, 0x05, 0x06, 0x02, 0x0A }), "6.6 策略穿透表常量错误。");
        TestSupport.Assert(StrategyExtensionSemantics.AttackRange.Length == 33 &&
            StrategyExtensionSemantics.AttackRange[0] == 0x01 &&
            StrategyExtensionSemantics.AttackRange[32] == 0x24, "0x41CEAB 范围表常量错误。");
        TestSupport.Assert(StrategyExtensionSemantics.PreserveReserved(0x83, 0x02) == 0x82 &&
            StrategyExtensionSemantics.PreserveReserved(0xFC, 0x03) == 0xFF,
            "策略扩展位保留位写回算法错误。");
        TestSupport.Assert(StrategyExtensionSemantics.SupportsPerStrategyFlags("ccz-6.6") &&
            !StrategyExtensionSemantics.SupportsPerStrategyFlags("ccz-6.5") &&
            !StrategyExtensionSemantics.SupportsPerStrategyFlags("ccz-6.4"),
            "逐策略扩展位画像能力判断错误。");
        TestSupport.Assert(StrategyExtensionSemantics.MapPenetration("ccz-6.5", 8) == 0x08 &&
            StrategyExtensionSemantics.MapPenetration("ccz-6.6", 10) == 0x0A &&
            StrategyExtensionSemantics.MapPenetration("ccz-6.4", 0) == null,
            "策略穿透映射查询错误。");
        TestSupport.Assert(StrategyExtensionSemantics.MapAttackRange(32) == 0x24 &&
            StrategyExtensionSemantics.MapAttackRange(33) == null, "范围映射查询错误。");
    }

    private static CheckBox[] ExtensionChecks(BusinessEditorPanel panel)
    {
        var section = Descendants(panel.DetailTabs.SelectedTab!)
            .FirstOrDefault(control => control.Name == "Flags_strategy-extension");
        return section == null ? [] : Descendants(section).OfType<CheckBox>().ToArray();
    }

    private static int Value(TableDocument document, int row)
        => Convert.ToInt32(document.Data.Rows[row]["内容"], CultureInfo.InvariantCulture);

    private static TableCellChange[] Changes(ProjectWorkspace workspace)
        => workspace.Documents.Values.SelectMany(document => document.EnumerateChanges()).ToArray();

    private static Dictionary<string, byte[]> Snapshot(string root)
        => Known65FixtureSnapshot.CoreNames.ToDictionary(name => name,
            name => File.ReadAllBytes(ProjectFileLocator.Resolve(root, name)));

    private static void AssertOnlyOffsetChanged(string root, IReadOnlyDictionary<string, byte[]> before,
        int offset, byte expected)
    {
        foreach (var pair in before)
        {
            var after = File.ReadAllBytes(ProjectFileLocator.Resolve(root, pair.Key));
            TestSupport.Assert(after.Length == pair.Value.Length, "策略扩展位保存改变了文件长度：" + pair.Key);
            var offsets = Enumerable.Range(0, after.Length).Where(index => after[index] != pair.Value[index]).ToArray();
            TestSupport.Assert(pair.Key.Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase)
                    ? offsets.SequenceEqual([offset]) && after[offset] == expected
                    : offsets.Length == 0,
                "策略扩展位保存改变了目标单字节以外的数据：" + pair.Key);
        }
    }

    private static void Settle(MainForm form) { Application.DoEvents(); form.PerformLayout(); Application.DoEvents(); }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
