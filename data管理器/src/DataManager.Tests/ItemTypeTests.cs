using System.Data;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class ItemTypeTests
{
    public static void Run()
    {
        foreach (var (profile, variant, dynamic) in new[]
        {
            ("ccz-6.4", "64-xisui-v1", false),
            ("ccz-6.5", "65-standard-v1", false), ("ccz-6.6", "66-standard-v1", true),
            ("ccz-6.6", "66-legacy-data-v1", false), ("ccz-6.6", "66-shanzhai-legacy", true)
        })
        {
            var gun = Create(profile, variant, 3, 2, 255);
            TestSupport.Assert(ItemTypeService.Resolve(gun, 0).PermissionColumn == "普通枪", $"{variant} 普通枪类型2未直接对应槽2");
            TestSupport.Assert(ItemTypeService.GetOptions(ItemTypeService.Resolve(gun, 0)).Single(option => option.Id == 2).Name == "普通枪", "普通类型2名称错误");
            var armor = Create(profile, variant, 70, 20, 255);
            TestSupport.Assert(ItemTypeService.Resolve(armor, 0).PermissionColumn == "普通铠甲", $"{variant} 类型20不得按十位数映射");
            var auxiliary = Create(profile, variant, 109, 74, 2);
            var auxiliaryType = ItemTypeService.Resolve(auxiliary, 0);
            TestSupport.Assert(auxiliaryType.Category == ItemCategory.Accessory && auxiliaryType.PermissionSlot == null,
                $"{variant} 辅助效果码74被误认为普通装备许可");
            var options = ItemTypeService.GetOptions(auxiliaryType);
            TestSupport.Assert(!options.Any(option => option.Name.Contains("枪", StringComparison.Ordinal)), $"{variant} 辅助选项包含枪类误映射");
            TestSupport.Assert(auxiliaryType.IsCategoryMarker == dynamic, $"{variant} 使用了错误的动态类型编码");
            var consumable = Create(profile, variant, 150, 46, 3);
            var consumableSemantics = ItemTypeService.Resolve(consumable, 0);
            TestSupport.Assert(consumableSemantics.Category == ItemCategory.Consumable &&
                consumableSemantics.PermissionSlot == null, $"{variant} 道具效果码46误关联普通装备许可");
            TestSupport.Assert(consumableSemantics.Layout.ConsumableParameterOffset == 0x15 &&
                consumableSemantics.Layout.GrowthOffset == null && consumableSemantics.Layout.SuitableJobOffset == null,
                $"{variant} 消耗品布局没有隐藏装备成长/适合兵种字段");
            if (dynamic)
            {
                auxiliary.Data.Rows[0]["宝物类型"] = 3;
                TestSupport.Assert(ItemTypeService.Resolve(auxiliary, 0).Category == ItemCategory.Consumable,
                    "动态类型未按当前草稿标记更新");
                TestSupport.Assert(options.Select(option => option.Id).SequenceEqual([2, 3]) && options[0].Name == "辅助装备",
                    "动态辅助行应只提供2辅助、3道具两个类别");
            }
            else
            {
                // The fixed 17-byte codec must retain its original write semantics.
                var field = auxiliary.Definition.Fields.Single(field => field.Name == "宝物类型");
                TestSupport.Assert(new RecordCodecRegistry().Get(auxiliary.Definition.RecordCodecKey)
                    .EncodeChangedField(auxiliary.OriginalFileBytes, field, 8).Single().Offset == 0x11,
                    "17字节兼容布局的类型写入被错误交换到类别标记字节");
            }
            foreach (var id in new[] { 0, 69, 70, 103, 104, 108, 109, 180, 254 })
            {
                TestSupport.Assert(ItemTypeService.Resolve(Create(profile, variant, id, 0, 255), 0).Category == ItemCategory.Weapon,
                    $"{variant} 类型0的普通剑被编号{id}错误分段");
                TestSupport.Assert(ItemTypeService.Resolve(Create(profile, variant, id, 25, 255), 0).Category == ItemCategory.Armor,
                    $"{variant} 类型25的特殊袍服被编号{id}错误分段");
                TestSupport.Assert(ItemTypeService.Resolve(Create(profile, variant, id, 0, 2), 0).Category == ItemCategory.Weapon,
                    $"{variant} 类型0武器被效果2或编号{id}抢占类别");
                TestSupport.Assert(ItemTypeService.Resolve(Create(profile, variant, id, 20, 3), 0).Category == ItemCategory.Armor,
                    $"{variant} 类型20防具被效果3或编号{id}抢占类别");
                TestSupport.Assert(ItemTypeService.Resolve(Create(profile, variant, id, 74, 2), 0).Category == ItemCategory.Accessory,
                    $"{variant} 辅助类别被编号{id}错误分段");
                TestSupport.Assert(ItemTypeService.Resolve(Create(profile, variant, id, 46, 3), 0).Category == ItemCategory.Consumable,
                    $"{variant} 道具类别被编号{id}错误分段");
                TestSupport.Assert(ItemTypeService.Resolve(Create(profile, variant, id, 26, 255), 0).Category == ItemCategory.Unknown,
                    $"{variant} 未知类型26被编号{id}推断为装备");
                foreach (var (marker, expected) in new[] { (0, ItemCategory.Weapon), (1, ItemCategory.Armor) })
                {
                    var fallback = ItemTypeService.Resolve(Create(profile, variant, id, 70, (byte)marker), 0);
                    TestSupport.Assert(fallback.Category == expected && fallback.PermissionSlot == null,
                        $"{variant} 类型>=26时没有按引擎fallback类别{marker}分类，或伪造装备许可槽");
                }
            }
        }
        var accessoryLayout = ItemTypeService.GetLayout(ItemCategory.Accessory);
        TestSupport.Assert(accessoryLayout.SuitableJobOffset == 0x17 && accessoryLayout.EffectValueOffset == 0x15 &&
            accessoryLayout.GrowthOffset == null, "辅助装备语义布局错误");
        TestSupport.Assert(ConsumableEffectCatalog.Resolve(0x1A).Name == "恢复生命" &&
            ConsumableEffectCatalog.Resolve(0x28).Name == "恢复SP" &&
            ConsumableEffectCatalog.Resolve(0x2A).Name == "HP上升" &&
            ConsumableEffectCatalog.Resolve(0x2B).Name == "MP上升" &&
            ConsumableEffectCatalog.Resolve(0x31).Name == "单挑" &&
            ConsumableEffectCatalog.Resolve(0x3D).Name == "装备升级" &&
            !ConsumableEffectCatalog.Resolve(0x40).Confirmed,
            "消耗品特效目录的用户映射或未知编号边界错误");
        TestSupport.Assert(new[] { 0x2C, 0x30, 0x35, 0x39 }.All(id =>
            ConsumableEffectCatalog.Resolve(id).Name == $"{id:X2}道具号") &&
            ConsumableEffectCatalog.GetAll().Select(entry => entry.Id).SequenceEqual(Enumerable.Range(0x1A, 0x24)),
            "消耗品特效目录丢失道具号标签或连续 ID");
    }

    public static void RunProject(string basePath)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            File.WriteAllText(Path.Combine(root, "System.ini"), "DefID=70 ; 防具\r\nAssID=109 ; 辅助\r\n");
            foreach (var (profile, variant) in new[] { ("ccz-6.5", "65-standard-v1"), ("ccz-6.6", "66-standard-v1"), ("ccz-6.6", "66-legacy-data-v1") })
            {
                var workspace = new ProjectWorkspace(); workspace.Open(root, profile, variant);
                using var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
                using var panel = new BusinessEditorPanel(workspace); host.Controls.Add(panel); host.Show();
                panel.Bind(BusinessPageKind.Items, 109); Application.DoEvents();
                var dynamic = variant == "66-standard-v1";
                var types = Editor(panel, dynamic ? "物品类别" : "实际特效号（类型字节）");
                TestSupport.Assert(dynamic ? types.Text == "2：辅助装备" : types.Text.StartsWith("74", StringComparison.Ordinal),
                    $"{variant} 辅助页类型显示错误：{types.Text}");
                if (!dynamic)
                    TestSupport.Assert(Editor(panel, "类别标记（特效字节）").Text == "2：辅助装备类别",
                        "固定codec辅助类别标记被显示为效果名");
                var permission = panel.DetailTabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "可装备部队");
                TestSupport.Assert(Descendants(permission).OfType<CheckBox>().All(check => !check.Enabled), $"{variant} 辅助页不应允许编辑枪许可");
                var item = workspace.GetDocument("items-high");
                TestSupport.Assert(Convert.ToString(item.Data.Rows[5]["可装备部队"]) == "不适用", "派生许可仍误映射辅助段");
                panel.Bind(BusinessPageKind.Items, 3); Application.DoEvents();
                types = Editor(panel, "宝物类型");
                TestSupport.Assert(types.Text == "2：普通枪", "真实枪类型2显示错误");
                types.SelectedIndex = -1;
                types.Text = "20";
                TestSupport.Assert(panel.Flush(), "类型草稿未提交");
                Application.DoEvents();
                TestSupport.Assert(Convert.ToInt32(workspace.GetDocument("items-low").Data.Rows[3]["宝物类型"]) == 20,
                    $"类型输入未更新当前工作区：{types.Text} / {types.SelectedItem}");
                permission = panel.DetailTabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "可装备部队");
                panel.DetailTabs.SelectedTab = permission; permission.Select(); Application.DoEvents();
                TestSupport.Assert(Descendants(permission).OfType<Label>().Any(label => label.Text.Contains("普通铠甲", StringComparison.Ordinal)),
                    "当前草稿类型变化后可装备部队页仍指向旧枪许可");
                var growth = workspace.GetDocument("detailed-job-growth");
                var oldArmor = Convert.ToInt32(growth.Data.Rows[0]["普通铠甲"]);
                var oldGun = Convert.ToInt32(growth.Data.Rows[0]["普通枪"]);
                Descendants(permission).OfType<CheckBox>().First().Checked = oldArmor == 0;
                TestSupport.Assert(Convert.ToInt32(growth.Data.Rows[0]["普通铠甲"]) == (oldArmor == 0 ? 1 : 0) &&
                    Convert.ToInt32(growth.Data.Rows[0]["普通枪"]) == oldGun, "类型变更后许可写入了错误矩阵列");
                host.Close();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static TableDocument Create(string profile, string variant, int id, byte physicalType, byte physicalMarker)
    {
        var definition = new ProfileRegistry().Resolve(profile, variant).Tables.Single(table => table.Key == "items-low")
            with { DataOffset = 0, RowCount = 1, BeginId = id };
        var bytes = new byte[definition.RowSize]; bytes[0x11] = physicalType; bytes[0x12] = physicalMarker;
        var codec = new RecordCodecRegistry().Get(definition.RecordCodecKey);
        var table = new DataTable(); table.Columns.Add("ID", typeof(int)); table.Columns.Add("宝物类型", typeof(int)); table.Columns.Add("装备特效号", typeof(int));
        table.Rows.Add(id, codec.Decode(bytes, definition.Fields.Single(field => field.Name == "宝物类型")), codec.Decode(bytes, definition.Fields.Single(field => field.Name == "装备特效号")));
        return new TableDocument { Definition = definition, FilePath = string.Empty, Data = table, OriginalFileBytes = bytes, OriginalFileHash = string.Empty };
    }

    private static SearchableIdComboBox Editor(Control panel, string labelText)
    {
        var label = Descendants(panel).OfType<Label>().First(label => label.Text == labelText);
        var layout = (TableLayoutPanel)label.Parent!;
        return (SearchableIdComboBox)layout.GetControlFromPosition(1, layout.GetPositionFromControl(label).Row)!;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }
}
