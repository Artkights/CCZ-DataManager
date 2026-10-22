using System.Buffers.Binary;
using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Domain;

public sealed record EquipmentTypeDefinition(int GroupIndex, string Name, bool HasDisplayToggle,
    bool IsVisible, int? DisplayValue, int? NormalTypeId, int? SpecialTypeId);

/// <summary>Project-native category labels. Display names never replace physical permission column identities.</summary>
public sealed class EquipmentTypeCatalogService
{
    public const string NamesTableKey = "equipment-type-names";
    public const string DisplayTableKey = "equipment-type-display";
    public const long NameTableOffset = 0x8AC70;
    public const long DisplayTableOffset = 0x81827;
    public const int NameCount = 15;
    public const int DisplayCount = 13;
    public const int HiddenValue = 255;
    public static IReadOnlyList<int> VisibleValues { get; } = Array.AsReadOnly(new[]
        { 0x00, 0x03, 0x06, 0x09, 0x0C, 0x0F, 0x12, 0x15, 0x18, 0x1B, 0x46, 0x49, 0x4C });

    public IReadOnlyList<EquipmentTypeDefinition> Read(ProjectWorkspace workspace)
    {
        workspace.Documents.TryGetValue(NamesTableKey, out var names);
        workspace.Documents.TryGetValue(DisplayTableKey, out var display);
        return Enumerable.Range(0, NameCount).Select(index =>
        {
            var name = names != null && index < names.Data.Rows.Count
                ? Convert.ToString(names.Data.Rows[index]["名称"], CultureInfo.InvariantCulture) ?? string.Empty
                : FallbackName(index);
            int? value = index < DisplayCount && display != null && index < display.Data.Rows.Count
                ? Convert.ToInt32(display.Data.Rows[index]["显示值"], CultureInfo.InvariantCulture) : null;
            return new EquipmentTypeDefinition(index, name, index < DisplayCount, value != HiddenValue, value,
                index < DisplayCount ? index * 2 : null, index < DisplayCount ? index * 2 + 1 : null);
        }).ToArray();
    }

    public string GetPermissionDisplayName(ProjectWorkspace workspace, string storageColumn)
    {
        var slot = ItemTypeService.EquipmentColumns.ToList().IndexOf(storageColumn);
        return slot < 0 ? storageColumn : GetTypeDisplayName(workspace, slot);
    }

    public string GetTypeDisplayName(ProjectWorkspace workspace, int typeId)
    {
        if (typeId is < 0 or >= 26) return $"类型{typeId}";
        var name = Read(workspace)[typeId / 2].Name.Trim();
        foreach (var prefix in new[] { "普通", "特殊" })
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length)
            { name = name[prefix.Length..]; break; }
        return (typeId % 2 == 0 ? "普通" : "特殊") + (name.Length == 0 ? $"类型{typeId / 2}" : name);
    }

    public static int DisplayValue(int groupIndex, bool isVisible)
        => groupIndex is >= 0 and < DisplayCount
            ? isVisible ? VisibleValues[groupIndex] : HiddenValue
            : throw new ArgumentOutOfRangeException(nameof(groupIndex), "辅助和道具没有原生显示开关。");

    public static IReadOnlyList<TableDefinition> BuildTables(byte[] executable)
    {
        var verified = IsVerifiedLayout(executable, out var evidence);
        var capability = verified ? TableCapability.Editable : TableCapability.Unavailable;
        return
        [
            new(NamesTableKey, "装备类型名称", "Ekd5.exe", NameTableOffset, NameCount, 5, 0,
                [new("名称", 0, 4, FieldKind.FixedString,
                    TextConstraint: new TextFieldConstraint(4, 4, false, "已验证装备类型名称：4 字节 GBK；第 5 字节独立保留。")),
                 new("保留结束字节", 4, 1, FieldKind.RawBytes, FieldPolicy.Preserve)], "宝物",
                VerificationLevel: verified ? VerificationLevel.SourceReviewed : VerificationLevel.InheritedUnverified,
                Capability: capability, CapabilityEvidence: evidence),
            new(DisplayTableKey, "装备类型显示", "Ekd5.exe", DisplayTableOffset, DisplayCount, 1, 0,
                [new("显示值", 0, 1, FieldKind.UInt8)], "宝物",
                VerificationLevel: verified ? VerificationLevel.SourceReviewed : VerificationLevel.InheritedUnverified,
                Capability: capability, CapabilityEvidence: evidence)
        ];
    }

    private static string FallbackName(int groupIndex)
        => groupIndex < DisplayCount ? ItemTypeService.EquipmentColumns[groupIndex * 2][2..]
            : groupIndex == 13 ? "辅助" : "道具";

    /// <summary>Require the actual PE mapping, a code reference to the display table and intact name-slot separators.</summary>
    private static bool IsVerifiedLayout(byte[] bytes, out string evidence)
    {
        evidence = "装备类型名称/显示缺少本项目 PE 布局和消费者引用证据。";
        try
        {
            if (bytes.Length < NameTableOffset + NameCount * 5 || bytes.Length < 64 ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0x5A4D) return false;
            var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
            if (pe < 0 || pe > bytes.Length - 24 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pe)) != 0x4550) return false;
            var optional = pe + 24;
            var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 6));
            if (size < 32 || optional + (long)size + count * 40L > bytes.Length ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional)) != 0x10B) return false;
            var imageBase = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optional + 28));
            var sections = new List<(uint Rva, uint Size, uint Raw, uint Flags)>();
            for (var index = 0; index < count; index++)
            {
                var section = bytes.AsSpan(optional + size + index * 40, 40);
                sections.Add((BinaryPrimitives.ReadUInt32LittleEndian(section[12..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(section[16..]), BinaryPrimitives.ReadUInt32LittleEndian(section[20..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(section[36..])));
            }
            bool Contains((uint Rva, uint Size, uint Raw, uint Flags) section, long start, int length)
                => start >= section.Raw && start + length <= section.Raw + (long)section.Size;
            var nameSection = sections.Where(section => Contains(section, NameTableOffset, NameCount * 5)).ToArray();
            var displaySection = sections.Where(section => Contains(section, DisplayTableOffset, DisplayCount)).ToArray();
            if (nameSection.Length != 1 || displaySection.Length != 1) return false;
            for (var index = 0; index < NameCount; index++)
            {
                var offset = checked((int)NameTableOffset + index * 5);
                if (bytes[offset + 4] != 0) return false;
                _ = EncodingService.DecodeFixedString(bytes.AsSpan(offset, 4));
            }
            var target = checked(imageBase + displaySection[0].Rva + (uint)(DisplayTableOffset - displaySection[0].Raw));
            var pointer = BitConverter.GetBytes(target);
            foreach (var section in sections.Where(section => (section.Flags & 0x20000000u) != 0))
            {
                if (section.Raw >= bytes.Length || section.Size > int.MaxValue) continue;
                var length = Math.Min((int)section.Size, bytes.Length - (int)section.Raw);
                var reference = bytes.AsSpan((int)section.Raw, length).IndexOf(pointer);
                if (reference < 0) continue;
                evidence = $"本项目 PE 节完整映射 15×5 字节名称与 13×1 字节显示表；可执行节 +0x{section.Raw + reference:X} 引用显示表 VA 0x{target:X8}；15 个 GBK 名称槽及结束字节已核对。";
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        { evidence += " " + ex.Message; return false; }
    }
}
