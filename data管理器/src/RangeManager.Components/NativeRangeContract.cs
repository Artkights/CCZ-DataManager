using System.Security.Cryptography;

namespace RangeManager.App;

/// <summary>
/// Code identities for the three locally audited native range engines. Range
/// payloads and pointer tables are deliberately outside these code identities.
/// A different executable SHA may therefore retain an identical range contract.
/// </summary>
public static class NativeRangeContract
{
    private sealed record Window(uint Address, int Length, string Hash, string Name);

    public static string Id(GameBase game, RangeKind kind) =>
        $"native-{game.VersionText}-{kind.ToString().ToLowerInvariant()}-20261108-v1";

    public static bool TryReadPierceCenter(GameBase game, out RangeCenterMode center, out string error)
    {
        center = RangeCenterMode.Target;
        error = "";
        const uint entry = 0x004364C7;
        bool longPrologue = game.Version != EngineVersion.V66;
        int prefixLength = longPrologue ? 13 : 12;
        int wrapperLength = longPrologue ? 59 : 45;
        byte[] native = ManagedRangeRuntime.GetNativeBytes(game.Exe);
        try
        {
            int offset = game.Exe.VaToOffset(entry);
            byte[] wrapper = native.AsSpan(offset, wrapperLength).ToArray();
            var original = new List<byte> { 0x55, 0x8B, 0xEC, 0x51 };
            if (longPrologue) original.Add(0x51);
            original.AddRange([0x8A, 0x4D, 0x08, 0xE8]);
            original.AddRange(BitConverter.GetBytes(unchecked(0x004061F9u - (entry + (uint)prefixLength))));
            if (wrapper[0] == 0xE9)
            {
                if (wrapper.AsSpan(5, prefixLength - 5).ToArray().Any(b => b != 0x90))
                    throw new InvalidDataException("穿透中心入口跳转的填充字节不属于已知补丁");
                uint cave = unchecked(entry + 5 + BitConverter.ToUInt32(wrapper, 1));
                byte[] expected = CenterCave(cave, entry + (uint)prefixLength, longPrologue);
                int caveOffset = game.Exe.VaToOffset(cave);
                if ((game.Exe.SectionOf(cave).Characteristics & 0x20000000) == 0 ||
                    !native.AsSpan(caveOffset, expected.Length).SequenceEqual(expected))
                    throw new InvalidDataException("穿透中心蹦床的自身槽换算、槽调用或回跳不匹配已知补丁");
                original.ToArray().CopyTo(wrapper, 0);
                center = RangeCenterMode.Self;
            }
            string expectedHash = longPrologue
                ? "38EB4758296AF74052A96E2B66292C1BD0C859528A07D55A4D6D3C4F4015C775"
                : "715146E86EF8B9DB718C4A3E2F7A7DF84C46E4DA3CE4E395AAA3026CC419CCA7";
            if (Convert.ToHexString(SHA256.HashData(wrapper)) != expectedHash)
                throw new InvalidDataException("0x004364C7 穿透中心包装器不是已验证的原生或自身中心实现");
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static byte[] CenterCave(uint cave, uint continuation, bool longPrologue)
    {
        var code = new List<byte> { 0x55, 0x8B, 0xEC, 0x51 };
        if (longPrologue) code.Add(0x51);
        code.AddRange([0x89, 0xC8, 0x2D]); code.AddRange(BitConverter.GetBytes(0x004A7B20u));
        code.AddRange([0x31, 0xD2, 0xB9]); code.AddRange(BitConverter.GetBytes(0x30u));
        code.AddRange([0xF7, 0xF1, 0x85, 0xD2]);
        int jne = code.Count; code.AddRange([0x75, 0]);
        code.Add(0x3D); code.AddRange(BitConverter.GetBytes(0xFFu));
        int ja = code.Count; code.AddRange([0x77, 0]);
        code.AddRange([0x88, 0x45, 0x08]);
        int fallback = code.Count;
        code[jne + 1] = (byte)(fallback - (jne + 2));
        code[ja + 1] = (byte)(fallback - (ja + 2));
        code.AddRange([0x8A, 0x4D, 0x08, 0xE8]);
        code.AddRange(BitConverter.GetBytes(unchecked(0x004061F9u - (cave + (uint)code.Count + 4))));
        code.Add(0xE9);
        code.AddRange(BitConverter.GetBytes(unchecked(continuation - (cave + (uint)code.Count + 4))));
        return code.ToArray();
    }

    public static IReadOnlyList<string> Validate(GameBase game, RangeKind kind)
    {
        // The runtime returns a clone with only its own installed hook windows
        // restored. Never replace live data pointers with an installation backup.
        byte[] native = ManagedRangeRuntime.GetNativeBytes(game.Exe);
        var errors = new List<string>();
        foreach (var window in Windows(game.Version, kind))
        {
            if (!game.Exe.TryVaToOffset(window.Address, out int offset) ||
                offset + window.Length > native.Length)
            {
                errors.Add($"{window.Name} 0x{window.Address:X8} 不在文件节范围内");
                continue;
            }
            string actual = Convert.ToHexString(SHA256.HashData(native.AsSpan(offset, window.Length)));
            if (!actual.Equals(window.Hash, StringComparison.Ordinal))
            {
                string hookHint = LooksLikeExternalEntryJump(native, offset, window.Length)
                    ? "；检测到该入口已被外部跳转/补丁占用，不能在未知调用链上安装范围分派"
                    : "";
                errors.Add($"{window.Name} 0x{window.Address:X8} 代码身份不匹配（{actual[..12]}）{hookHint}；须重新核实该基底的范围契约");
            }
        }
        return errors;
    }

    private static bool LooksLikeExternalEntryJump(byte[] bytes, int offset, int length)
    {
        int available = Math.Min(length, bytes.Length - offset);
        if (available <= 0) return false;
        if (bytes[offset] is 0xE8 or 0xE9) return true;
        // Several local patches preserve the x86 function prologue and place
        // their trampoline immediately after it (55 8B EC E8/E9 ...). Treat
        // that shape as an occupied entry too; it cannot be safely chained
        // without knowing the patch's ABI and continuation contract.
        return available >= 4 && bytes[offset] == 0x55 && bytes[offset + 1] == 0x8B &&
            bytes[offset + 2] == 0xEC && bytes[offset + 3] is 0xE8 or 0xE9;
    }

    private static IEnumerable<Window> Windows(EngineVersion version, RangeKind kind)
    {
        if (kind == RangeKind.Attack)
        {
            yield return version switch
            {
                EngineVersion.V64 => new(0x436511, 261, "7CC8CE17B024C3DEBB858AF4A520760BEC15643C97ED53BB251481B5C437D18B", "攻击分派"),
                EngineVersion.V65 => new(0x436511, 261, "2E35DCEF95EB08A4992E00F06AF93F05D6A317A767916769E71D6FEE93A27D70", "攻击分派"),
                _ => new(0x436511, 256, "DDBD98E2267690694969F09E3A40E486C274A6A5F32C2A090D04CD862392C39B", "攻击分派")
            };
            yield return version == EngineVersion.V66
                ? new(0x436611, 221, "25D77882B9B51AE5E999B1B7AE282282D9AA647658B3E6E99B14AD037D4A9AFF", "攻击生成元展开")
                : new(0x436616, 221, "CC83259B99BADE94A2BDA25DD783CF7E2CE660CB93E4F32DAD07202390C30C81", "攻击生成元展开");
            yield break;
        }

        yield return version switch
        {
            EngineVersion.V64 => new(0x435CE9, 576, "5E819C8A21F68ADFD851ADB24BCA79BB97D258B28DB927647F55725C5422C3CA", "穿透分派"),
            EngineVersion.V65 => new(0x435CE9, 527, "6452CA80FB269C0F808A91A223A3A4530345F5812079F2CAB839EF078004DA72", "穿透分派"),
            _ => new(0x435CE9, 317, "3D3B7FEA9A2D5A0F47426A1028FB485289ECB7DE4E0272F915E613C3BF801AB5", "穿透分派")
        };
        yield return new(0x42230E, 52, "8BCCE3D71F1BA8BD851DB2906FBD9119404E840AD2C77960B4F990ED7B451533", "攻击方向计算");
        if (version == EngineVersion.V66)
        {
            yield return new(0x42399B, 260, "B1FD0F2F1F579C261EBAF6CE479D5D519961C5F57B8B9AEC22276AC55705C969", "穿透生成元展开");
            yield return new(0x435C10, 217, "1B653502D93F2C9EDAE0866854EC77FB3540A7549EFC23CD411BC781AE4E0A8A", "穿透过滤与衰减");
            yield return new(0x4254FA, 85, "74AB68A1B208145366B2B70C61977A84B3920751453F9165643D0C72085089AD", "双侧方向计算");
        }
        else
        {
            yield return version == EngineVersion.V64
                ? new(0x42398A, 243, "F186A2DBB62A992887B7E6AAAA96E9D32510E5ED310B567CD2C57D8A0416867B", "穿透邻格枚举")
                : new(0x42399B, 226, "11C7DF1693F942182F1EFE02677888990EAE83BF7FB960E72C80F197F48641FC", "穿透邻格枚举");
            yield return new(0x435BF2, 247, "7A6B24A069896FDD823C6ACDEA350D2489B62BFA34084188D476405990E22350", "穿透过滤与衰减");
            yield return new(0x4359F5, 179, "5F1E20E0A492D71B7D647C4E623BF2D6867E6FBB427CC75B508B91406C117E4F", "邻格方向查表");
            yield return new(0x406E2F, 140, "0E5451B32C01FC7F79BF40DB3EA7AAB78ABC0F1FB4C077F631902AE02C265E7A", "轴向附加格");
            yield return new(0x437968, 142, "E154C1CFA8C23FE7254CFC89E7B8F0BDAEEA5DEC13763E031B32E5D51F14E4B0", "边带附加格");
            yield return new(0x4254FA, 85, "87BC5F5F99DBB02E7E2B84D687DD9894858C1E8D12940988C00D3A67A825D65A", "双侧方向计算");
            if (version == EngineVersion.V65)
            {
                yield return new(0x40CE50, 146, "B85E3F1D5E12BB12908115AE90D35FE5AD6E2C45DD07E62F727F3F4C48AFA910", "自身中心与排除主目标");
                yield return new(0x40D000, 87, "36D716436FF421F2E58408066E1447841309A9501BA5F0F1ED12F9F9592984D6", "双侧加直线组合");
            }
        }
    }
}
