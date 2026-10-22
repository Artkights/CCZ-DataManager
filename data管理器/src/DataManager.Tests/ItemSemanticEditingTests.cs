using System.Data;
using System.Globalization;
using DataManager.App;
using DataManager.Core.Csv;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

/// <summary>
/// Regression for the item editor's category-dependent aliases. The real-base
/// expectations below are independent byte observations, not generated from
/// ItemSemanticResolver. Every write is made against a temporary core copy.
/// </summary>
internal static class ItemSemanticEditingTests
{
    private sealed record RealAccessory(int Id, string Name, int EffectValue, int SuitableJob, string JobName);

    private static readonly RealAccessory[] RealAccessories =
    [
        new(154, "天毒珠", 1, 6, "侠客"),
        new(156, "太平要术", 5, 0, "君主"),
        new(157, "天毒珠（仿）", 60, 6, "侠客"),
        new(149, "左氏春秋", 22, 255, "所有兵种"),
        new(139, "凌风赤兔", 7, 27, "上将")
    ];

    public static void Run(string repository)
    {
        var realBase = Path.Combine(repository, "基底", "复刻版6.6山贼 国庆");
        var standard65 = Path.Combine(repository, "基底", "加强版6.5未加密版");
        TestSupport.Assert(Directory.Exists(realBase), "缺少山贼国庆真实回归基底：" + realBase);
        TestSupport.Assert(Directory.Exists(standard65), "缺少6.5合成回归所需基底：" + standard65);
        RunRealAccessories(realBase, Path.Combine(repository, "data管理器", "验证", "2026-10-20-宝物读写修复"));
        RunSynthetic(standard65, "ccz-6.5", "65-standard-v1");
        RunSynthetic(realBase, "ccz-6.6", "66-shanzhai-legacy");
        Console.WriteLine("ITEM_SEMANTIC_EDITING_TESTS_OK");
    }

    private static void RunRealAccessories(string source, string evidenceDirectory)
    {
        var copy = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        try
        {
            var workspace = Open(copy, "ccz-6.6", "66-shanzhai-legacy");
            var document = workspace.GetDocument("items-high");
            TestSupport.Assert(document.Definition.RowSize == 25, "真实宝物表不再是25字节布局");
            using var host = Host();
            using var panel = new BusinessEditorPanel(workspace);
            host.Controls.Add(panel); host.Show();

            // Revisit same-category rows in a different order: the compact
            // editor reuses controls and must replace every previous value.
            foreach (var example in RealAccessories.Concat(RealAccessories.Reverse()))
            {
                var rowIndex = RowIndex(document, example.Id);
                var offset = RowOffset(document, rowIndex);
                var bytes = document.OriginalFileBytes;
                TestSupport.Assert(bytes[offset + 0x15] == example.EffectValue &&
                    bytes[offset + 0x17] == example.SuitableJob && bytes[offset + 0x13] == 255 && bytes[offset + 0x18] == 1,
                    $"真实样本{example.Id}的独立字节证据已变化，须复核基底而非放宽断言");
                TestSupport.Assert(Convert.ToString(document.Data.Rows[rowIndex]["名称"], CultureInfo.InvariantCulture) == example.Name,
                    $"真实样本{example.Id}名称不符");
                Bind(panel, example.Id);
                AssertAccessoryUi(panel, example.EffectValue, example.SuitableJob, example.JobName, 255, true);
                var snapshot = ItemSemanticResolver.Resolve(document, rowIndex);
                TestSupport.Assert(snapshot.Category == ItemCategory.Accessory && snapshot.EffectValue == example.EffectValue &&
                    snapshot.SuitableJob == example.SuitableJob && snapshot.GalleryValue == 1 &&
                    snapshot.ActualEffectNumber == bytes[offset + 0x11], $"真实样本{example.Id}语义解析错误");
            }
            TestSupport.Assert(!workspace.IsDirty && !panel.HasPendingChanges, "只切换真实宝物记录生成了修改草稿");

            Bind(panel, 154);
            Directory.CreateDirectory(evidenceDirectory);
            using (var bitmap = new Bitmap(panel.Width, panel.Height))
            {
                panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(evidenceDirectory, "宝物154-天毒珠.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            var before = CoreBytes(copy);
            var row154 = RowIndex(document, 154);
            var physical154 = RowOffset(document, row154);
            var effectId = NextByte(document.OriginalFileBytes[physical154 + 0x11]);
            SetCombo(panel, Details(panel), effectId, "实际特效号", "实际特效号（类型字节）");
            SetNumber(panel, Details(panel), 9, "特殊效果值");
            SetCombo(panel, Details(panel), 27, "适用兵种", "适合兵种");
            SetNumber(panel, Details(panel), 99, "价格（/100）");
            SetGallery(panel, false);
            SetNumber(panel, Tab(panel, "原始字段"), 77, "保留值（+0x16）");
            var draft = ItemSemanticResolver.Resolve(document, row154);
            TestSupport.Assert(draft.ActualEffectNumber == effectId && draft.EffectValue == 9 && draft.SuitableJob == 27 &&
                draft.ReservedValue == 77 && draft.GalleryValue == 0, "真实辅助编辑后Resolver未读取当前草稿");
            AssertSnapshotByte(draft, 0x13, 99, "价格草稿");
            TestSupport.Assert(document.OriginalFileBytes[physical154 + 0x15] == 1 &&
                File.ReadAllBytes(document.FilePath).SequenceEqual(before["Star.e5"]), "编辑草稿提前改写了基线或文件");

            Bind(panel, 156); AssertAccessoryUi(panel, 5, 0, "君主", 255, true);
            Bind(panel, 154); AssertAccessoryUi(panel, 9, 27, "上将", 99, false);
            TestSupport.Assert(Number(Tab(panel, "原始字段"), "保留值（+0x16）").Value == 77, "返回记录后保留字节草稿丢失");
            SaveOnly(workspace, before, document, row154,
                new Dictionary<int, byte> { [0x11] = (byte)effectId, [0x13] = 99, [0x15] = 9, [0x16] = 77, [0x17] = 27, [0x18] = 0 });
            AssertCsvPhysicalCompatibility(workspace, document, row154, "装备特效号");
            host.Close();
        }
        finally { Directory.Delete(copy, true); }
    }

    private static void RunSynthetic(string source, string profile, string variant)
    {
        var copy = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: profile == "ccz-6.5");
        try
        {
            File.WriteAllText(Path.Combine(copy, "System.ini"), "DefID=70\r\nAssID=109\r\n");
            SeedSynthetic(copy, profile, variant);
            var workspace = Open(copy, profile, variant);
            var document = workspace.GetDocument("items-high");
            using var host = Host();
            using var panel = new BusinessEditorPanel(workspace);
            host.Controls.Add(panel); host.Show();

            // All three categories are in items-high, so table identity alone
            // cannot decide whether the existing form layout is reusable.
            foreach (var id in new[] { 109, 150, 104, 109, 104, 150, 109 })
            {
                Bind(panel, id);
                if (id == 109) AssertAccessoryUi(panel, 20, 7, null, 255, true);
                else if (id == 104) AssertOrdinaryUi(panel);
                else AssertConsumableUi(panel, 60, 1, false);
            }
            TestSupport.Assert(!workspace.IsDirty && !panel.HasPendingChanges, $"{variant}跨同表类别切换生成了修改");
            var rowIndex = RowIndex(document, 109);
            var effectField = variant == "65-standard-v1" ? "宝物类型" : "装备特效号";
            var before = CoreBytes(copy);
            SetCombo(panel, Details(panel), 0x4B, "实际特效号", "实际特效号（类型字节）");
            SetNumber(panel, Details(panel), 21, "特殊效果值");
            SetCombo(panel, Details(panel), 255, "适用兵种", "适合兵种");
            SetNumber(panel, Details(panel), 7, "价格（/100）");
            SetGallery(panel, false);
            SetNumber(panel, Tab(panel, "原始字段"), 42, "保留值（+0x16）");
            TestSupport.Assert(Convert.ToInt32(document.Data.Rows[rowIndex][effectField], CultureInfo.InvariantCulture) == 0x4B,
                $"{variant}实际特效UI没有绑定正确逻辑字段");
            var draft = ItemSemanticResolver.Resolve(document, rowIndex);
            TestSupport.Assert(draft.ActualEffectNumber == 0x4B && draft.EffectValue == 21 && draft.SuitableJob == 255 &&
                draft.ReservedValue == 42 && draft.GalleryValue == 0 && draft.InitialAttribute == null && draft.GrowthValue == null,
                $"{variant}辅助草稿被按普通装备解释或仍读取旧基线");
            AssertSnapshotByte(draft, 0x13, 7, variant + "价格草稿");
            SaveOnly(workspace, before, document, rowIndex,
                new Dictionary<int, byte> { [0x11] = 0x4B, [0x13] = 7, [0x15] = 21, [0x16] = 42, [0x17] = 255, [0x18] = 0 });
            AssertCsvPhysicalCompatibility(workspace, document, rowIndex, effectField);

            Bind(panel, 150);
            before = CoreBytes(copy);
            var consumableRow = RowIndex(document, 150);
            SetCombo(panel, Details(panel), 0x3D, "实际特效号", "实际特效号（类型字节）");
            SetNumber(panel, Details(panel), 0x34, "效果参数（低字节）");
            SetNumber(panel, Details(panel), 0x12, "效果参数（高字节）");
            SetGallery(panel, true);
            SetNumber(panel, Tab(panel, "原始字段"), 33, "保留值（+0x17）");
            var consumableDraft = ItemSemanticResolver.Resolve(document, consumableRow);
            TestSupport.Assert(consumableDraft.Category == ItemCategory.Consumable && consumableDraft.ActualEffectNumber == 0x3D &&
                consumableDraft.InitialAttribute == null && consumableDraft.GrowthValue == null && consumableDraft.EffectValue == null &&
                consumableDraft.ReservedValue == 33 && consumableDraft.GalleryValue == 1, $"{variant}消耗品草稿语义错误");
            AssertSnapshotByte(consumableDraft, 0x15, 0x34, variant + "参数低字节");
            AssertSnapshotByte(consumableDraft, 0x16, 0x12, variant + "参数高字节");
            TestSupport.Assert(Descendants(Details(panel)).OfType<Label>().Any(label => label.Text.Contains("装备升级", StringComparison.Ordinal)),
                $"{variant}消耗品特效变更后仍显示旧摘要/装备特效名");
            SaveOnly(workspace, before, document, consumableRow,
                new Dictionary<int, byte> { [0x11] = 0x3D, [0x15] = 0x34, [0x16] = 0x12, [0x17] = 33, [0x18] = 1 });
            host.Close();
        }
        finally { Directory.Delete(copy, true); }
    }

    private static void SeedSynthetic(string copy, string profile, string variant)
    {
        var workspace = Open(copy, profile, variant);
        var document = workspace.GetDocument("items-high");
        var bytes = File.ReadAllBytes(document.FilePath);
        foreach (var (id, values) in new[]
        {
            (104, new byte[] { 20, 0x40, 12, 2, 40, 8, 3, 1 }),
            (109, new byte[] { 0x4A, 2, 255, 3, 20, 10, 7, 1 }),
            (150, new byte[] { 0x1A, 3, 2, 4, 60, 1, 195, 0 })
        })
            values.CopyTo(bytes, RowOffset(document, RowIndex(document, id)) + 0x11);
        File.WriteAllBytes(document.FilePath, bytes);
    }

    private static void AssertAccessoryUi(BusinessEditorPanel panel, int effectValue, int suitable, string? jobName, int price, bool gallery)
    {
        var detail = Details(panel);
        TestSupport.Assert(Number(detail, "特殊效果值").Value == effectValue, $"辅助{panel.CurrentId}特殊效果值显示错误");
        var combo = Combo(detail, "适用兵种", "适合兵种");
        TestSupport.Assert(combo.Enabled && combo.TryGetSelectedId(out var id) && id == suitable,
            $"辅助{panel.CurrentId}适用兵种没有可编辑地显示原始+17：{combo.Text}");
        if (jobName != null) TestSupport.Assert(combo.Text.Contains(jobName, StringComparison.Ordinal), $"辅助{panel.CurrentId}适用兵种名称错误：{combo.Text}");
        TestSupport.Assert(Number(detail, "价格（/100）").Value == price && Gallery(panel).Checked == gallery && Gallery(panel).Enabled,
            $"辅助{panel.CurrentId}价格或图鉴显示错误");
        TestSupport.Assert(!HasLabel(detail, "初始能力", "初始属性", "能力成长", "装备特效值"), "辅助页出现普通装备成长字段或保留字节");
        TestSupport.Assert(Descendants(detail).OfType<Label>().Any(label => label.Text.Contains("255表示不可买卖", StringComparison.Ordinal)),
            "宝物价格缺少255不可买卖提示");
        TestSupport.Assert(Number(Tab(panel, "原始字段"), "保留值（+0x16）").Enabled, "辅助+16保留字节没有可编辑数字框");
    }

    private static void AssertOrdinaryUi(BusinessEditorPanel panel)
    {
        var detail = Details(panel);
        TestSupport.Assert(Number(detail, "初始属性").Value == 40 && Number(detail, "特殊效果值").Value == 8 &&
            Number(detail, "能力成长").Value == 3 && Gallery(panel).Checked, "普通防具字段与辅助布局混淆");
        TestSupport.Assert(!HasLabel(detail, "适用兵种", "适合兵种", "效果参数（低字节）", "效果参数（高字节）"),
            "切回普通防具后残留辅助/消耗品字段");
        TestSupport.Assert(!panel.DetailTabs.TabPages.Cast<TabPage>().Any(tab => tab.Text == "原始字段"), "普通防具残留其他类别的原始字段页");
    }

    private static void AssertConsumableUi(BusinessEditorPanel panel, int low, int high, bool gallery)
    {
        var detail = Details(panel);
        TestSupport.Assert(Number(detail, "效果参数（低字节）").Value == low && Number(detail, "效果参数（高字节）").Value == high &&
            Gallery(panel).Checked == gallery, "消耗品参数低高字节或图鉴显示错误");
        TestSupport.Assert(!HasLabel(detail, "初始能力", "初始属性", "能力成长", "适用兵种", "适合兵种", "特殊效果值"),
            "消耗品页残留装备/辅助字段");
        var actual = Combo(detail, "实际特效号", "实际特效号（类型字节）");
        TestSupport.Assert(actual.TryGetSelectedId(out var id) && id == 0x1A && actual.Text.Contains("恢复生命", StringComparison.Ordinal),
            "消耗品效果号没有使用独立的道具映射");
        TestSupport.Assert(Number(Tab(panel, "原始字段"), "保留值（+0x17）").Value == 195, "消耗品+17保留值被重解释或丢失");
    }

    private static void AssertCsvPhysicalCompatibility(ProjectWorkspace workspace, TableDocument document, int rowIndex, string effectField)
    {
        var path = Path.Combine(workspace.RootPath, "item-semantic-roundtrip.csv");
        workspace.ExportCsv(document, path);
        var records = CsvService.ReadRecords(path, document.Definition.Fields.Select(field => field.Name));
        var header = records.First(record => record.Contains("初始能力"));
        TestSupport.Assert(header.Contains("装备特效值") && header.Contains("能力成长") && header.Contains("宝物图鉴") &&
            !header.Contains("特殊效果值") && !header.Contains("适用兵种"), "宝物UI别名破坏了原始CSV字段名");
        TestSupport.Assert(workspace.ImportCsv(document, path).ChangedCells == 0, "宝物物理CSV原样回导产生了修改");
        var before = CoreBytes(workspace.RootPath);
        var id = document.Definition.BeginId + rowIndex;
        CsvService.WriteRecords(path,
        [
            new[] { "ID", "初始能力", "装备特效值", "能力成长", "价格（/100）", "宝物图鉴", effectField },
            new[] { id.ToString(CultureInfo.InvariantCulture), "31", "43", "6", "18", "1", "76" }
        ]);
        TestSupport.Assert(workspace.ImportCsv(document, path).ChangedCells == 6, "原始物理字段CSV没有完整导入6个宝物字节");
        var draft = ItemSemanticResolver.Resolve(document, rowIndex);
        TestSupport.Assert(draft.EffectValue == 31 && draft.ReservedValue == 43 && draft.SuitableJob == 6 &&
            draft.GalleryValue == 1 && draft.ActualEffectNumber == 76, "CSV导入后宝物语义仍读取旧基线");
        SaveOnly(workspace, before, document, rowIndex,
            new Dictionary<int, byte> { [0x11] = 76, [0x13] = 18, [0x15] = 31, [0x16] = 43, [0x17] = 6, [0x18] = 1 });
        workspace.ExportCsv(document, path);
        TestSupport.Assert(workspace.ImportCsv(document, path).ChangedCells == 0, "宝物修改后的CSV不能无损往返");
    }

    private static void SaveOnly(ProjectWorkspace workspace, IReadOnlyDictionary<string, byte[]> before, TableDocument document,
        int rowIndex, IReadOnlyDictionary<int, byte> updates)
    {
        var offset = RowOffset(document, rowIndex);
        var expected = before.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        var fileName = Path.GetFileName(document.FilePath);
        foreach (var update in updates) expected[fileName][offset + update.Key] = update.Value;
        var result = workspace.Save();
        TestSupport.Assert(result.Saved, "宝物草稿保存失败：" + string.Join("；", result.Messages));
        foreach (var pair in expected)
        {
            var actual = File.ReadAllBytes(Path.Combine(workspace.RootPath, pair.Key));
            TestSupport.Assert(actual.SequenceEqual(pair.Value), $"保存宝物时{pair.Key}没有严格保留目标字节以外的数据");
        }
        var saved = ItemSemanticResolver.Resolve(document, rowIndex);
        foreach (var update in updates) AssertSnapshotByte(saved, update.Key, update.Value, "保存复读");
        TestSupport.Assert(!workspace.IsDirty, "宝物保存后工作区仍为脏状态");
    }

    private static ProjectWorkspace Open(string root, string profile, string variant)
    {
        var workspace = new ProjectWorkspace(); workspace.Open(root, profile, variant);
        TestSupport.Assert(workspace.GetDocument("items-high").Definition.IsWriteSupported, $"{variant}宝物表不可写");
        return workspace;
    }

    private static Form Host() => new()
    {
        StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
        ShowInTaskbar = false, Size = new Size(1280, 850)
    };

    private static IReadOnlyDictionary<string, byte[]> CoreBytes(string root)
        => Known65FixtureSnapshot.CoreNames.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)), StringComparer.OrdinalIgnoreCase);
    private static int RowIndex(TableDocument document, int id)
        => document.Data.Rows.Cast<DataRow>().Select((row, index) => (row, index))
            .Single(pair => Convert.ToInt32(pair.row["ID"], CultureInfo.InvariantCulture) == id).index;
    private static int RowOffset(TableDocument document, int rowIndex)
        => checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
    private static int NextByte(byte value) => value == 255 ? 254 : value + 1;
    private static void AssertSnapshotByte(ItemSemanticSnapshot snapshot, int offset, int expected, string context)
        => TestSupport.Assert(Convert.FromHexString(snapshot.RawSummary)[offset] == expected, $"{context}未包含当前+0x{offset:X2}={expected}");

    private static void Bind(BusinessEditorPanel panel, int id)
    {
        TestSupport.Assert(panel.Flush(), "切换宝物前不能提交有效输入");
        panel.Bind(BusinessPageKind.Items, id); Application.DoEvents();
        panel.DetailTabs.SelectedTab = Details(panel); Application.DoEvents();
    }
    private static TabPage Details(BusinessEditorPanel panel) => Tab(panel, "宝物详情");
    private static TabPage Tab(BusinessEditorPanel panel, string name)
        => panel.DetailTabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == name);
    private static bool HasLabel(Control root, params string[] names)
        => Descendants(root).OfType<Label>().Any(label => names.Contains(label.Text, StringComparer.Ordinal));
    private static Control ForLabel(Control root, params string[] names)
    {
        foreach (var label in Descendants(root).OfType<Label>().Where(label => names.Contains(label.Text, StringComparer.Ordinal)))
        {
            if (label.Parent is not TableLayoutPanel table) continue;
            var position = table.GetPositionFromControl(label);
            if (table.GetControlFromPosition(position.Column + 1, position.Row) is { } editor) return editor;
        }
        throw new InvalidOperationException("找不到宝物字段编辑器：" + string.Join(" / ", names));
    }
    private static NumericUpDown Number(Control root, params string[] names)
        => SelfAndChildren(ForLabel(root, names)).OfType<NumericUpDown>().Single();
    private static SearchableIdComboBox Combo(Control root, params string[] names)
        => SelfAndChildren(ForLabel(root, names)).OfType<SearchableIdComboBox>().Single();
    private static CheckBox Gallery(BusinessEditorPanel panel)
        => SelfAndChildren(ForLabel(Details(panel), "宝物图鉴")).OfType<CheckBox>().Single();
    private static void SetNumber(BusinessEditorPanel panel, Control root, int value, params string[] names)
    {
        var number = Number(root, names);
        TestSupport.Assert(number.Enabled, "宝物数字编辑器不可编辑：" + string.Join("/", names));
        number.Value = value; TestSupport.Assert(panel.Flush(), "宝物数字编辑未提交"); Application.DoEvents();
    }
    private static void SetCombo(BusinessEditorPanel panel, Control root, int value, params string[] names)
    {
        var combo = Combo(root, names);
        TestSupport.Assert(combo.Enabled, "宝物下拉编辑器不可编辑：" + string.Join("/", names));
        combo.SelectedIndex = -1; combo.Text = value.ToString(CultureInfo.InvariantCulture);
        TestSupport.Assert(panel.Flush(), "宝物下拉值未提交：" + string.Join("/", names)); Application.DoEvents();
    }
    private static void SetGallery(BusinessEditorPanel panel, bool value)
    {
        var gallery = Gallery(panel); TestSupport.Assert(gallery.Enabled, "图鉴勾选框不可编辑");
        gallery.Checked = value; TestSupport.Assert(panel.Flush(), "图鉴修改未提交"); Application.DoEvents();
    }
    private static IEnumerable<Control> SelfAndChildren(Control root) => new[] { root }.Concat(Descendants(root));
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }
}
