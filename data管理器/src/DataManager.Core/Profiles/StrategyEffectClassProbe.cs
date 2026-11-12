using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DataManager.Core.Profiles;

/// <summary>Evidence for the strategy-ID to effect-handler dispatch, independent of the selected profile.</summary>
public static class StrategyEffectClassProbe
{
    public sealed record Layout(bool Verified, long Offset, int MaximumClass, string Evidence,
        string HandlerFamily = "", uint DispatchAddress = 0, uint JumpTableAddress = 0);

    // These signatures cover the complete handler instruction region, including
    // every relative call and every shared branch, but never the mutable class table.
    // Sources: 洗隋/刘备6.4, 召唤猛将6.4, 标准/东吴/氪金6.5, and SHA 25D488C8 山贼6.6.
    private static readonly Dictionary<string, (string Family, int Maximum, bool AlternateDragon)> HandlerSignatures = new(StringComparer.Ordinal)
    {
        ["A54C27B071F30419004F34D0466000362118F21028DBE69AF93C53F829453E52"] = ("legacy", 0x24, false),
        ["38FA6D1C2CD16A5991070D8FB9EDFAD4FA2D38495EC3195A26B69A0AB8A8EA96"] = ("legacy", 0x24, true),
        ["15AD2B012EECD44242BE38CC65FDCCE3E9DB57260DB12324A36966BC362F143C"] = ("legacy", 0x24, false),
        ["DB8B1FFB6308E8D3A9D4142B552E26C68FB5B607829F55DD28EB4B09ECFCBD6F"] = ("shanzei66", 0x29, false)
    };

    private static readonly uint[] LegacyHandlers =
    [0x4256D1,0x4256E3,0x4256F5,0x4256D6,0x4256FA,0x4256FF,0x425704,0x425709,
     0x42573E,0x425743,0x425748,0x42574D,0x425764,0x42576E,0x425773,0x425754,
     0x42571A,0x42571F,0x425724,0x425729,0x42572E,0x425780,0x42578D,0x42579A,
     0x4257AC,0x42579F,0x42594E,0x4257C3,0x425964,0x425978,0x42570E,0x4257CF,
     0x4256F0,0x425715,0x4257B9,0x4257D3,0x4257E0];

    private static readonly uint[] ShanzeiHandlers =
    [0x4256B1,0x4256C3,0x4256D5,0x4256B6,0x4256DA,0x4256DF,0x4256E4,0x4256E9,
     0x42573E,0x425743,0x425748,0x42574D,0x425764,0x42576E,0x425773,0x425754,
     0x42571A,0x42571F,0x425724,0x425729,0x42572E,0x425780,0x42578D,0x425797,
     0x4257AC,0x42579C,0x42594D,0x4257C3,0x425964,0x425979,0x4256EE,0x4257CF,
     0x4256D0,0x425715,0x4257B9,0x4257D3,0x4257E0,0x4257EC,0x4257F6,0x4256F5,
     0x4257A1,0x425800];

    public static Layout Inspect(byte[] executable)
    {
        var unavailable = new Layout(false, 0, -1, "未验证本项目的策略效果分派及处理器，禁止使用继承偏移。");
        if (!EmbeddedEngineLayoutProbe.PeImage.TryRead(executable, out var image)) return unavailable;
        // cmp al,ID-limit; jae cleanup; mov al,[eax+classTable]; jmp [eax*4+jumpTable].
        byte?[] pattern = [0x3C,null,0x0F,0x83,null,null,null,null,0x8A,0x80,null,null,null,null,0xFF,0x24,0x85,null,null,null,null];
        var hits = image.FindCode(pattern);
        if (hits.Count != 1) return unavailable with { Evidence = "策略效果分派指令不唯一或已改变。" };
        var hit = hits[0];
        if (executable[hit + 1] is not (0x90 or 0x96)) return unavailable;
        // Establish that the comparison uses the strategy ID, with EDX=ctx+400h.
        var prefix = new byte[] { 0x8B,0xD1,0x81,0xC2,0x00,0x04,0x00,0x00,0x0F,0xB6,0x01 };
        var prefixStart = hit - prefix.Length;
        while (prefixStart >= 0 && executable[prefixStart + prefix.Length - 1] == 0x90) prefixStart--;
        if (prefixStart < 0 || !executable.AsSpan(prefixStart, prefix.Length).SequenceEqual(prefix) ||
            executable.AsSpan(prefixStart + prefix.Length, hit - prefixStart - prefix.Length).IndexOfAnyExcept((byte)0x90) >= 0)
            return unavailable with { Evidence = "分派之前缺少策略ID读取与ctx+0x400效果标识地址证据。" };
        var tableVa = BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(hit + 10, 4));
        var jumpVa = BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(hit + 17, 4));
        if (!image.TryMap(tableVa, 144, false, true, out var tableOffset) ||
            !image.TryMap(jumpVa, 48 * 4, true, false, out var jumpOffset)) return unavailable;
        var firstHandler = BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(jumpOffset, 4));
        if (jumpVa <= firstHandler || jumpVa - firstHandler > 4096 ||
            !image.TryMap(firstHandler, (int)(jumpVa - firstHandler), true, false, out var handlerOffset)) return unavailable;
        var digest = Convert.ToHexString(SHA256.HashData(executable.AsSpan(handlerOffset, (int)(jumpVa - firstHandler))));
        if (!HandlerSignatures.TryGetValue(digest, out var signature))
            return unavailable with { Evidence = "策略效果处理器的完整指令、共享分支或调用目标已改变，未开放未验证类。" };
        if (executable[hit + 1] != (signature.Family == "legacy" ? 0x96 : 0x90)) return unavailable;
        var expected = signature.Family == "legacy" ? LegacyHandlers : ShanzeiHandlers;
        for (var index = 0; index < 48; index++)
        {
            var target = BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(jumpOffset + index * 4, 4));
            var expectedTarget = index < expected.Length ? expected[index] : expected[0];
            if (signature.AlternateDragon && index == 0x1A) expectedTarget = 0x425950;
            if (target != expectedTarget || !image.TryMap(target, 1, true, false, out _))
                return unavailable with { Evidence = $"策略效果类0x{index:X2}的处理器指针与完整代码签名不一致。" };
        }
        return new(true, tableOffset, signature.Maximum,
            $"策略ID分派VA 0x{image.ToVa(hit):X8}、144字节类表VA 0x{tableVa:X8}→文件+0x{tableOffset:X}、48项跳表及全部处理器指令/相对调用已验签；表数据不参与签名。",
            signature.Family, image.ToVa(hit), jumpVa);
    }
}
