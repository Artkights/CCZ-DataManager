using System.Text;

namespace TransformManager.App;

public sealed record ItemRef(
    int Id, string Name, ItemCategory Category, string CategoryName,
    string TypeName, int EffectId, int EffectValue, bool FromStar);

/// <summary>
/// Reads Data.e5 block 1 (items 0-103) and Star.e5 (104-254) with the same
/// category semantics as DataManager's ItemTypeService/ItemSemanticResolver:
/// 宝物类型 at +0x11, 装备特效号 at +0x12 (file layout), 6.6 marker/effect split,
/// and effect values at +0x16 (weapon/armor) or +0x15 (accessory).
/// <see cref="ByEffect"/> / <see cref="ForEffect"/> only expose weapons and armor;
/// accessories and consumables use separate effect-code spaces and do not
/// participate in treasure transform management.
/// </summary>
public sealed class ItemCatalog
{
    /// <summary>26 equipment columns copied from ItemTypeService.EquipmentColumns.</summary>
    public static readonly string[] EquipmentColumns =
    [
        "普通剑", "特殊剑", "普通枪", "特殊枪", "普通弓", "特殊弓", "普通刀", "特殊刀",
        "普通炮车", "特殊炮车", "普通锤", "特殊锤", "普通斧", "特殊斧", "普通扇", "特殊扇",
        "普通宝剑", "特殊宝剑", "普通将剑", "特殊将剑", "普通铠甲", "特殊铠甲",
        "普通衣服", "特殊衣服", "普通袍服", "特殊袍服"
    ];

    public IReadOnlyList<ItemRef> Items => _items;
    public IReadOnlyDictionary<int, List<ItemRef>> ByEffect => _byEffect;

    private readonly List<ItemRef> _items = new();
    private readonly Dictionary<int, List<ItemRef>> _byEffect = new();
    private readonly bool _dynamic66;

    private ItemCatalog(bool dynamic66) => _dynamic66 = dynamic66;

    public static ItemCatalog FromItems(IEnumerable<ItemRef> items)
    {
        var catalog = new ItemCatalog(false);
        foreach (var item in items.OrderBy(item => item.Id).DistinctBy(item => item.Id))
        {
            catalog._items.Add(item);
            if (item.Category is not (ItemCategory.Weapon or ItemCategory.Armor)) continue;
            if (!catalog._byEffect.TryGetValue(item.EffectId, out var refs))
                catalog._byEffect[item.EffectId] = refs = new List<ItemRef>();
            refs.Add(item);
        }
        return catalog;
    }

    public static ItemCatalog Load(string root, EngineVersion version)
    {
        var catalog = new ItemCatalog(version == EngineVersion.V66);
        string dataPath = Path.Combine(root, "Data.e5");
        if (File.Exists(dataPath))
            catalog.ReadBlock(File.ReadAllBytes(dataPath), 0x818C, 104, star: false);
        string starPath = Path.Combine(root, "Star.e5");
        if (File.Exists(starPath))
            catalog.ReadBlock(File.ReadAllBytes(starPath), 0, 151, star: true);
        return catalog;
    }

    private void ReadBlock(byte[] raw, int offset, int count, bool star)
    {
        if (offset < 0 || offset + count * 25 > raw.Length) return;
        // Profile contract: 6.4/6.5 use a 17-byte name; 6.6 stores the icon at
        // +0x0F and a 15-byte name (ccz-6.6.json items-low/items-high).
        int nameLength = _dynamic66 ? 15 : 17;
        for (int id = 0; id < count; id++)
        {
            int record = offset + id * 25;
            var nameBytes = raw.AsSpan(record, nameLength);
            int zero = nameBytes.IndexOf((byte)0);
            if (zero >= 0) nameBytes = nameBytes[..zero];
            string name;
            try { name = Encoding.GetEncoding(936).GetString(nameBytes).Trim(); }
            catch { name = ""; }
            if (name.Length == 0) continue;

            byte type = raw[record + 0x11];
            byte fileEffect = raw[record + 0x12];
            var category = ResolveCategory(type, fileEffect, out int effectId);
            int effectValue = category switch
            {
                ItemCategory.Accessory => raw[record + 0x15],
                ItemCategory.Consumable => raw[record + 0x15],
                _ => raw[record + 0x16]
            };
            string categoryName = CategoryName(category, type);
            string typeName = type <= 25 ? EquipmentColumns[type] : categoryName;
            int itemId = star ? 104 + id : id;
            var item = new ItemRef(itemId, name, category, categoryName, typeName, effectId, effectValue, star);
            _items.Add(item);
            // 质变只作用于有成长等级的武器/防具；辅助装备与道具/消耗品使用
            // 各自独立的效果号空间（道具见 ConsumableItemEffectCatalogService），
            // 不进入引用表，避免与宝物特效号数值重合。
            if (category is not (ItemCategory.Weapon or ItemCategory.Armor)) continue;
            if (!_byEffect.TryGetValue(effectId, out var list))
            {
                list = new List<ItemRef>();
                _byEffect[effectId] = list;
            }
            list.Add(item);
        }
    }

    private ItemCategory ResolveCategory(byte type, byte fileEffect, out int effectId)
    {
        // 6.6: category marker lives at +0x12 (2=accessory, 3=consumable); the real
        // effect code is +0x11 (see ItemTypeService.ResolveValues).
        if (_dynamic66 && type >= 26 && fileEffect is 2 or 3)
        {
            effectId = type;
            return fileEffect == 2 ? ItemCategory.Accessory : ItemCategory.Consumable;
        }

        ItemCategory category;
        if (type <= 19) category = ItemCategory.Weapon;
        else if (type <= 25) category = ItemCategory.Armor;
        else category = fileEffect switch
        {
            0 => ItemCategory.Weapon,
            1 => ItemCategory.Armor,
            2 => ItemCategory.Accessory,
            3 => ItemCategory.Consumable,
            _ => ItemCategory.Unknown
        };

        effectId = category is ItemCategory.Accessory or ItemCategory.Consumable ? type : fileEffect;
        return category;
    }

    private static string CategoryName(ItemCategory category, byte type)
    {
        if (type <= 25)
        {
            return category switch
            {
                ItemCategory.Weapon => $"武器（{EquipmentColumns[type]}）",
                ItemCategory.Armor => $"防具（{EquipmentColumns[type]}）",
                _ => "未确认类型"
            };
        }
        return category switch
        {
            ItemCategory.Weapon => "武器",
            ItemCategory.Armor => "防具",
            ItemCategory.Accessory => "辅助装备",
            ItemCategory.Consumable => "道具/消耗品",
            _ => "未确认类型"
        };
    }

    public IReadOnlyList<ItemRef> ForEffect(int effect)
        => _byEffect.TryGetValue(effect, out var list) ? list : Array.Empty<ItemRef>();
}
