using System.Buffers.Binary;
using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.Core.Profiles;

/// <summary>Proves the 512-item Star loader and its separate IMSG reader through executable PE consumers.</summary>
public static class ExtendedItemLayoutProbe
{
    public sealed record Result(bool StarItems, bool StarDescriptions, bool RootItems, string Evidence);

    public static Result Inspect(byte[] executable)
    {
        if (!EmbeddedEngineLayoutProbe.PeImage.TryRead(executable, out var pe))
            return new(false, false, false, "物品扩展缺少有效 PE32 消费者。");
        byte?[] Pattern(string value) => value.Split(' ').Select(part => part == "??" ? (byte?)null : Convert.ToByte(part, 16)).ToArray();
        var rootItems = pe.FindCode(Pattern("81 E9 00 01 00 00 6B C9 19 03 0D 1C 00 4F 00")).Count == 1 &&
            pe.FindCode(Pattern("25 FF 00 00 00 B9 C8 00 00 00 F7 E1 8B 0D 24 00 4F 00")).Count == 1;
        var initializers = pe.FindCode(Pattern("55 8B EC 68 ?? ?? ?? ?? 6A 01 68 ?? ?? ?? ?? E8 ?? ?? ?? ?? B9 ?? ?? ?? ?? BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 68 00 02 00 00 6A 00 8B 4D F8 3B 4D FC 73 1B E8 ?? ?? ?? ?? C6 41 0E 00 8A 51 12 8B 41 13 66 89 41 12 88 51 14 FF 45 F8 EB DD"));
        var loaders = pe.FindCode(Pattern("55 8B EC 51 B9 ?? ?? ?? ?? 51 52 E8 ?? ?? ?? ?? 85 C0 74 1C 68 D8 27 00 00 FF 75 FC 8B 4D F8 E8 ?? ?? ?? ?? 85 C0 74 08 8B 4D F8 E8 ?? ?? ?? ?? 8B E5 5D C3"));
        var indexers = pe.FindCode(Pattern("55 8B EC 6B C9 19 81 C1 ?? ?? ?? ?? 5D C3"));
        var verified = new List<int>();
        foreach (var hit in initializers)
        {
            var baseVa = U32(hit + 4);
            var destination = U32(hit + 21);
            var nameVa = U32(hit + 26);
            if ((long)baseVa + 104 * 25 != destination || !pe.TryMap(nameVa, 8, false, false, out var name) ||
                !System.Text.Encoding.ASCII.GetString(executable, name, 8).Equals("star.e5\0", StringComparison.OrdinalIgnoreCase) ||
                !loaders.Any(loader => pe.ToVa(loader) == CallTarget(hit + 30)) ||
                !indexers.Any(indexer => pe.ToVa(indexer) == CallTarget(hit + 50) && U32(indexer + 8) == baseVa)) continue;
            verified.Add(hit);
        }
        var star = verified.Count == 1;
        var descriptions = false;
        if (star)
        {
            var readers = pe.FindCode(Pattern("55 8B EC 68 C8 00 00 00 FF 75 08 8B 4D 0C 69 C9 C8 00 00 00 51 B9 ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 55 08 C6 82 C8 00 00 00 00 5D C3"));
            var messages = pe.FindCode(Pattern("55 8B EC 8B 45 0C 05 9A 07 00 00 50 FF 75 10 E8 ?? ?? ?? ?? 83 C4 08"));
            var messageLoaders = pe.FindCode(Pattern("6A 00 68 ?? ?? ?? ?? B9 ?? ?? ?? ?? E8 ?? ?? ?? ??"));
            descriptions = messages.Count == 1 && readers.Count == 1 && CallTarget(messages[0] + 15) == pe.ToVa(readers[0]) &&
                messageLoaders.Count(loader => U32(loader + 8) == U32(readers[0] + 22) &&
                    pe.TryMap(U32(loader + 3), 8, false, false, out var name) &&
                    System.Text.Encoding.ASCII.GetString(executable, name, 8).Equals("IMSG.E5\0", StringComparison.OrdinalIgnoreCase)) == 1;
        }
        return new(star, descriptions, rootItems, star
            ? $"已验证 Star.e5 408×25 加载、目标为物品基址+104×25、512条初始化及ID×25索引完整消费链（VA 0x{pe.ToVa(verified[0]):X8}）；" +
              (descriptions ? "说明读取完整ID+1946后乘200，扩展说明为Imsg.e5+440400。" : "扩展说明消费者未通过，说明区不可用。")
            : "未找到唯一的 Star.e5 512物品完整消费链；不将普通Star尾部解释为扩展。");

        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(offset, 4));
        long CallTarget(int offset) => (long)pe.ToVa(offset) + 5 + BinaryPrimitives.ReadInt32LittleEndian(executable.AsSpan(offset + 1, 4));
    }

    internal static IReadOnlyList<TableDefinition> Apply(string root, IReadOnlyList<TableDefinition> tables, byte[] executable,
        SourceFileReadSession? sourceSession = null)
    {
        var probe = Inspect(executable);
        if (!probe.StarItems) return tables;
        var source = tables.Single(table => table.Key == "items-high");
        var starPath = sourceSession?.Resolve(root, "Star.e5") ?? ProjectFileLocator.Resolve(root, "Star.e5");
        var starBytes = (sourceSession?.GetMetadata(starPath).Exists ?? File.Exists(starPath))
            ? sourceSession?.Read(starPath).Bytes ?? File.ReadAllBytes(starPath) : [];
        var complete = starBytes.Length >= 10200;
        var nameSize = source.Fields.Single(field => field.Name == "名称").Size;
        var lastName = starBytes.Length >= 3800 ? EncodingService.DecodeFixedStringPreservingInvalidBytes(starBytes.AsSpan(3775, nameSize)).Trim() : string.Empty;
        var real255 = !string.IsNullOrWhiteSpace(lastName) && !lastName.Equals("FF-End", StringComparison.OrdinalIgnoreCase) && !lastName.Equals("End", StringComparison.OrdinalIgnoreCase);
        // Once a complete 512-item file is proved, row 255 remains a physical
        // item even when the user clears its name or chooses an end-marker name.
        // Editable content must not change geometry or invalidate saved history.
        var include255 = complete || real255;
        // The shipped 6359-byte Star ends at FF-End and contains padding after
        // it, even in engines which retain the 512-item loader. Its tail is not
        // an extension and the marker must not become a business item.
        if (!complete && starBytes.Length <= 6359 && !real255) return tables;
        var rootItem = Path.Combine(root, "Item.e5");
        var hasRoot = File.Exists(rootItem);
        var conflict = hasRoot && probe.RootItems;
        var evidence = probe.Evidence + (hasRoot
            ? conflict ? " 双源冲突：根Item.e5及Star.e5消费者同时成立，Star扩展仅供只读检查，禁止合并或写入。"
                : " 根Item.e5未被该引擎的独立扩展指针消费链选中，当前使用Star内嵌扩展。"
            : string.Empty);
        return tables.Select(table => table.Key switch
        {
            "items-high" when include255 => table with { RowCount = 152, DisplayName = "宝物（104-255）", CapabilityEvidence = evidence },
            "item-description-high" when probe.StarDescriptions && include255 => table with { RowCount = 152, DisplayName = "宝物说明（104-255）", CapabilityEvidence = evidence },
            "items-extended" => TextFieldConstraints.Apply(table with
            {
                FileName = "Star.e5", DataOffset = 3800, RowCount = 256, RowSize = 25, BeginId = 256,
                Fields = source.Fields, RecordCodecKey = source.RecordCodecKey,
                Capability = !complete ? TableCapability.Unavailable : conflict ? TableCapability.ReadOnly : TableCapability.Editable,
                VerificationLevel = VerificationLevel.SourceReviewed,
                CapabilityEvidence = evidence + (!complete ? " Star.e5缺少完整10200字节，扩展不按残行读取，也不自动补齐。" : string.Empty)
            }),
            "item-description-extended" => TextFieldConstraints.Apply(table with
            {
                FileName = "Imsg.e5", DataOffset = 440400, RowCount = 256, RowSize = 200, BeginId = 256,
                Fields = table.Fields.Select(field => field with { TextConstraint = null }).ToArray(),
                Capability = !probe.StarDescriptions || !complete ? TableCapability.Unavailable : conflict ? TableCapability.ReadOnly : TableCapability.Editable,
                VerificationLevel = VerificationLevel.SourceReviewed, CapabilityEvidence = evidence
            }),
            _ => table
        }).ToArray();
    }
}
