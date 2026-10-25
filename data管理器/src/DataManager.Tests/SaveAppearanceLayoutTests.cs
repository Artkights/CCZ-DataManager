using System.Buffers.Binary;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

/// <summary>All mutations stay in memory; original engine samples are read only.</summary>
internal static class SaveAppearanceLayoutTests
{
    public static void Run(string basePath)
    {
        var repository = Directory.Exists(Path.Combine(basePath, "基底"))
            ? basePath : TestSupport.RepositoryRootFromBase(basePath);
        var samples = new[]
        {
            ("ccz-6.4", "洗隋"), ("ccz-6.5", "加强版6.5未加密版"),
            ("ccz-6.6", "复刻版6.6山贼"),
            ("ccz-6.6", "曹操传6.6-V10.1 【解压密码：66】/复刻版6.6")
        };
        var count = 0;
        foreach (var (profile, relative) in samples)
        {
            var path = Path.Combine(repository, "基底", relative, "Ekd5.exe");
            if (!File.Exists(path)) continue;
            var original = File.ReadAllBytes(path);
            var result = SaveAppearanceLayoutProbe.Inspect(original, profile);
            TestSupport.Assert(result is { Verified: true, ROffset: 0xE800, SOffset: 0, PrefixLength: 0x30000,
                RExecutableOffset: 0xE1000, SExecutableOffset: 0xD2800 },
                $"{relative} 未通过人物与原生保存/读档完整链：{result.Evidence}");
            VerifyRejectedMutations(original, profile);
            TestSupport.Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "只读布局验收改变了原始基底。");
            count++;
        }
        TestSupport.Assert(count > 0, "缺少可只读验证的 6.4/6.5/6.6 原始样本。");
        TestSupport.Assert(!SaveAppearanceLayoutProbe.Inspect([], "ccz-6.5").Verified &&
            !SaveAppearanceLayoutProbe.Inspect(new byte[0x110000], "ccz-6.5").Verified,
            "空文件或无 PE 的同长度字节错误启用了存档布局。");
        Console.WriteLine($"SaveAppearanceLayoutTests passed: {count} original engines, signatures/imports/mapping rejection.");
    }

    private static void VerifyRejectedMutations(byte[] original, string profile)
    {
        Reject(original, profile, b => b[Find(b, "55 8B EC 8B 45 08 2B 05 ?? ?? ?? ?? 99 33 C9 B1 48 F7 F9 5D C3") + 16] = 0x49,
            "人物记录除数改变");
        Reject(original, profile, b => b[Find(b, "55 8B EC 83 EC 08 81 7D 08 00 04 00 00 72 04 33 C9 EB 4D") + 9] = 0xFF,
            "S 人物 1024 上界改变");
        var writer = Find(original, "68 00 00 03 00 68 00 10 50 00 8D 4D F8 E8 ?? ?? ?? ?? 01 45 FC");
        Reject(original, profile, b => b[writer + 3] ^= 1, "快照写入长度改变");
        Reject(original, profile, b => b[writer + 6] ^= 2, "写入与读取地址不一致");
        Reject(original, profile, b => b[writer + 38] ^= 1, "后续写入调用不同函数");
        Reject(original, profile, b => original.AsSpan(writer, 106).CopyTo(b.AsSpan(0x80000, 106)), "重复原生写入链");
        var reader = Find(original, "68 00 00 03 00 68 00 10 50 00 6A 00 8D 4D F8 E8 ?? ?? ?? ??");
        Reject(original, profile, b => b[reader + 11] = 1, "快照读取起点不为零");
        Reject(original, profile, b => b[Find(b, "55 8B EC 51 6A 00 FF 75 08 8B 4D FC E8 14 00 00 00 FF 75 10 FF 75 0C 8B 4D FC E8 5F FF FF FF") + 5] = 1,
            "seek 包装的基准不为文件起点");
        Reject(original, profile, b => b[Find(b, "55 8B EC 51 68 ?? ?? ?? ?? 6A 00 6A 00 C6 01 05 E8 ?? ?? ?? ??") + 15] = 6,
            "人物段版本改变");
        Reject(original, profile, b => b[Find(b, "68 F0 D4 00 00 FF 75 F4 8D 4D F8 E8 ?? ?? ?? ?? E8 ?? ?? ?? ??") + 1] ^= 1,
            "原生人物段长度改变");
        Reject(original, profile, b => b[Find(b, "68 3D 31 00 00 FF 75 F4 8D 4D F8 E8 ?? ?? ?? ?? 8B 4D F4 E8 ?? ?? ?? ??") + 1] ^= 1,
            "原生脚本段长度改变");
        Reject(original, profile, b => b[Find(b, "8B 4D FC 80 F9 FA 73 ?? E8 ?? ?? ?? ?? FF 75 F0 E8 ?? ?? ?? ?? 89 45 F0") + 5] = 0xF9,
            "场景战场记录数改变");
        Reject(original, profile, b => b[Find(b, "6A 18 51 50 E8 ?? ?? ?? ?? 83 C4 0C 8B 45 08 83 C0 2A 5D C2 04 00") + 17] = 0x2B,
            "战场记录序列化步长改变");
        Reject(original, profile, b => b[Find(b, "57 72 69 74 65 46 69 6C 65 00")] = (byte)'X', "WriteFile 导入语义改变");
        Reject(original, profile, b => b[Find(b, "52 65 61 64 46 69 6C 65 00")] = (byte)'X', "ReadFile 导入语义改变");
        Reject(original, profile, b => b[Find(b, "53 65 74 46 69 6C 65 50 6F 69 6E 74 65 72 00")] = (byte)'X', "SetFilePointer 导入语义改变");
        Reject(original, profile, b =>
        {
            var pe = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x3C, 4));
            var first = pe + 24 + BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pe + 20, 2));
            b.AsSpan(first + 20, 4).CopyTo(b.AsSpan(first + 40 + 20, 4));
        }, "PE 原始节重叠");
        Reject(original, profile, b =>
        {
            var consumer = Find(b, "55 8B EC 51 E8 ?? ?? ?? ?? 0F B7 04 45 ?? ?? ?? ?? 59 5D C3");
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(consumer + 13, 4), 0x00530FFF);
        }, "R 表越过完整快照边界");
        var changedTable = (byte[])original.Clone();
        changedTable[0xD2800] ^= 1; changedTable[0xE1000 + 2046] = 0xFF;
        TestSupport.Assert(SaveAppearanceLayoutProbe.Inspect(changedTable, profile).Verified,
            "修改人物 R/S 数值不应破坏布局证明或依赖原件哈希。");
        TestSupport.Assert(!SaveAppearanceLayoutProbe.Inspect(original, "unknown-engine").Verified,
            "未知 profile 错误启用存档同步布局。");
        TestSupport.Assert(!SaveAppearanceLayoutProbe.Inspect(original[..0xE17FF], profile).Verified,
            "缺少最后一个 R 字节的截断 PE 仍启用布局。");
    }

    private static void Reject(byte[] original, string profile, Action<byte[]> mutation, string scenario)
    {
        var bytes = (byte[])original.Clone(); mutation(bytes);
        TestSupport.Assert(!SaveAppearanceLayoutProbe.Inspect(bytes, profile).Verified, scenario + "未被存档布局门禁拒绝。");
    }

    private static int Find(byte[] bytes, string text)
    {
        var pattern = text.Split(' ').Select(token => token == "??" ? (byte?)null : Convert.ToByte(token, 16)).ToArray();
        var hits = new List<int>();
        for (var offset = 0; offset <= bytes.Length - pattern.Length; offset++)
        {
            var matched = true;
            for (var i = 0; i < pattern.Length; i++)
                if (pattern[i] is { } expected && bytes[offset + i] != expected) { matched = false; break; }
            if (matched) hits.Add(offset);
        }
        TestSupport.Assert(hits.Count == 1, "测试定位不唯一：" + text);
        return hits[0];
    }
}
