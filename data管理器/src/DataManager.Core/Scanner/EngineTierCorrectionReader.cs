using DataManager.Core.Editing;

namespace DataManager.Core.Scanner;

/// <summary>Reads only the 16-byte empty-group layout established by the 6.6 evidence.</summary>
public static class EngineTierCorrectionReader
{
    public const uint TableVa = 0x00507EC0;
    public const int EstablishedLength = 16;

    public static string ReadSummary(ProjectWorkspace workspace)
    {
        if (workspace.ActiveProfile.Profile.Version != "6.6") return "当前基底不适用";
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(workspace.RootPath, "Ekd5.exe"));
            var image = EngineEffectBinaryImage.Read(bytes);
            if (image.VirtualAddressToFileOffset(0x00436E49) is not { } referenceOffset ||
                image.ReadBytes(referenceOffset, 5) is not { } reference ||
                !reference.AsSpan().SequenceEqual(new byte[] { 0xBA, 0xC0, 0x7E, 0x50, 0x00 }))
                return "只读：原生修正表引用未通过校验";
            if (image.VirtualAddressToFileOffset(TableVa) is not { } offset ||
                image.ReadBytes(offset, EstablishedLength) is not { } table)
                return "只读：已知修正表范围无法读取";
            var hex = Convert.ToHexString(table);
            var groups = new HashSet<byte>();
            for (var index = 0; index + 1 < table.Length; index += 2)
            {
                if (table[index] != 0xFF)
                    return $"只读：非空/未知组结构；前16字节 {hex}";
                if (table[index + 1] == 0xFF)
                    return $"只读：{groups.Count} 组，0 条记录，{index + 2} 字节；{hex}";
                if (table[index + 1] > 6 || !groups.Add(table[index + 1]))
                    return $"只读：组索引未通过校验；前16字节 {hex}";
            }
            return $"只读：已知16字节内缺少 FF FF 结束标记；{hex}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
        {
            return "只读扫描失败：" + ex.Message;
        }
    }
}
