using System.Text;

namespace RangeManager.App;

public enum PenetrationCenter
{
    Target,
    Self
}

/// <summary>
/// 把 0x4364C7 的“中心槽”改成攻击者自身槽。
/// 做法：在可执行的空白区写入一段蹦床代码，入口前 10/13 字节改为 E9 跳转。
/// 蹦床用 (unit - 0x4A7B20) / 0x30 求自身槽；指针不像单位指针时回退到原来的目标槽。
/// </summary>
public sealed class CenterPatch
{
    private const uint SlotToUnitVa = 0x004061F9;
    private const uint UnitArrayBase = 0x004A7B20;
    private const uint UnitStride = 0x30;
    private const int CaveBytes = 96;

    private static readonly byte[] LongPattern = [0x55, 0x8B, 0xEC, 0x51, 0x51, 0x8A, 0x4D, 0x08, 0xE8];
    private static readonly byte[] ShortPattern = [0x55, 0x8B, 0xEC, 0x51, 0x8A, 0x4D, 0x08, 0xE8];

    private readonly PeFile _exe;
    private readonly uint _entry;
    private readonly int _prologueLength;
    private readonly bool _longPrologue;
    private readonly uint _cave;
    private readonly uint _avoidStart;
    private readonly uint _avoidEnd;

    public PenetrationCenter Current { get; private set; } = PenetrationCenter.Target;
    public string Status { get; private set; } = "";
    public bool CanApply => _entry != 0 && _cave != 0;
    public uint EntryVa => _entry;
    public uint CaveVa => _cave;

    public CenterPatch(PeFile exe, uint avoidStart, uint avoidEnd)
    {
        _exe = exe;
        _avoidStart = avoidStart;
        _avoidEnd = avoidEnd;
        _entry = FindEntry(exe, out _longPrologue, out _prologueLength);
        if (_entry == 0)
        {
            Status = "未找到穿透展开入口（0x4364C7 模式不匹配），无法切换穿透中心";
            return;
        }

        byte first = exe.ReadByte(_entry);
        if (first == 0xE9)
        {
            _cave = DecodeJumpTarget(exe);
            Current = PenetrationCenter.Self;
            Status = $"当前：以自身为中心（蹦床 0x{_cave:X8}）";
            return;
        }

        _cave = FindCave(exe, out string caveDetail);
        if (_cave == 0)
        {
            Status = "未找到可写代码洞：" + caveDetail;
            return;
        }
        Current = PenetrationCenter.Target;
        Status = $"当前：以目标为中心（入口 0x{_entry:X8}，蹦床预留 0x{_cave:X8}）";
    }

    public bool Apply(PenetrationCenter mode)
    {
        if (!CanApply)
        {
            Status = "未找到入口或代码洞，无法切换";
            return false;
        }
        try
        {
            if (mode == PenetrationCenter.Self)
            {
                var cave = BuildCave(_cave, _entry + (uint)_prologueLength, _longPrologue);
                if (cave.Length > CaveBytes)
                    throw new InvalidOperationException("蹦床代码超出预留空间");
                _exe.WriteBytes(_cave, cave);
                var entry = new byte[_prologueLength];
                entry[0] = 0xE9;
                WriteU32(entry, 1, _cave - (_entry + 5));
                for (int i = 5; i < entry.Length; i++) entry[i] = 0x90;
                _exe.WriteBytes(_entry, entry);
                VerifyTrampoline(cave.Length);
                Current = PenetrationCenter.Self;
                Status = $"已切换：以自身为中心（蹦床 0x{_cave:X8}）";
            }
            else
            {
                _exe.WriteBytes(_entry, BuildOriginalPrologue());
                Current = PenetrationCenter.Target;
                Status = $"已切换：以目标为中心（入口 0x{_entry:X8} 已还原）";
            }
            return true;
        }
        catch (Exception ex)
        {
            Status = "写入失败：" + ex.Message;
            return false;
        }
    }

    private byte[] BuildOriginalPrologue()
    {
        var bytes = new List<byte> { 0x55, 0x8B, 0xEC, 0x51 };
        if (_longPrologue) bytes.Add(0x51);
        bytes.AddRange([0x8A, 0x4D, 0x08, 0xE8]);
        int callAt = bytes.Count;
        bytes.AddRange(BitConverter.GetBytes((int)(SlotToUnitVa - (_entry + (uint)callAt + 4))));
        return bytes.ToArray();
    }

    private static byte[] BuildCave(uint cave, uint returnVa, bool longPrologue)
    {
        var code = new List<byte>();
        void Emit(params byte[] values) => code.AddRange(values);
        void EmitU32(uint value) => code.AddRange(BitConverter.GetBytes(value));
        void EmitJmp(uint target, int at)
        {
            int rel = (int)(target - (cave + (uint)at + 4));
            code.AddRange(BitConverter.GetBytes(rel));
        }

        Emit(0x55, 0x8B, 0xEC);              // push ebp; mov ebp, esp
        Emit(0x51);                          // push ecx（保存攻击者指针）
        if (longPrologue) Emit(0x51);        // 原版第二次 push ecx
        Emit(0x89, 0xC8);                    // mov eax, ecx
        Emit(0x2D); EmitU32(UnitArrayBase);  // sub eax, 4A7B20h
        Emit(0x31, 0xD2);                    // xor edx, edx
        Emit(0xB9); EmitU32(UnitStride);     // mov ecx, 30h
        Emit(0xF7, 0xF1);                    // div ecx → eax=槽号
        Emit(0x85, 0xD2);                    // test edx, edx
        int jneAt = code.Count; Emit(0x75, 0x00);
        Emit(0x3D); EmitU32(0xFF);           // cmp eax, 0FFh
        int jaAt = code.Count; Emit(0x77, 0x00);
        Emit(0x88, 0x45, 0x08);              // mov [ebp+8], al
        int fallback = code.Count;
        code[jneAt + 1] = (byte)(fallback - (jneAt + 2));
        code[jaAt + 1] = (byte)(fallback - (jaAt + 2));
        Emit(0x8A, 0x4D, 0x08);              // mov cl, [ebp+8]
        Emit(0xE8); EmitJmp(SlotToUnitVa, code.Count); // call 4061F9h
        Emit(0xE9); EmitJmp(returnVa, code.Count);      // jmp 返回点
        return code.ToArray();
    }

    /// <summary>写入后回读蹦床，确认调用槽换算与返回跳转都指向正确地址。</summary>
    private void VerifyTrampoline(int codeLength)
    {
        uint jumpAt = _cave + (uint)codeLength - 5;
        if (_exe.ReadByte(_cave) != 0x55 || _exe.ReadByte(jumpAt) != 0xE9)
            throw new InvalidOperationException("蹦床签名不匹配");
        if (jumpAt + 5 + _exe.ReadU32(jumpAt + 1) != _entry + (uint)_prologueLength)
            throw new InvalidOperationException("蹦床返回地址不正确");
        uint callAt = _cave + (uint)codeLength - 10;
        if (_exe.ReadByte(callAt) != 0xE8 || callAt + 5 + _exe.ReadU32(callAt + 1) != SlotToUnitVa)
            throw new InvalidOperationException("蹦床槽换算调用不正确");
    }

    private uint DecodeJumpTarget(PeFile exe)
    {
        uint rel = exe.ReadU32(_entry + 1);
        return _entry + 5 + rel;
    }

    private static uint FindEntry(PeFile exe, out bool longPrologue, out int prologueLength)
    {
        uint best = 0;
        bool bestLong = false;
        int bestLength = 0;
        int bestScore = -1;
        foreach (var pattern in new[] { LongPattern, ShortPattern })
        {
            for (int i = 0; i + pattern.Length + 4 < exe.Bytes.Length; i++)
            {
                bool match = true;
                for (int k = 0; k < pattern.Length; k++)
                {
                    if (exe.Bytes[i + k] != pattern[k]) { match = false; break; }
                }
                if (!match) continue;
                uint va = OffsetToVa(exe, i);
                if (va == 0) continue;
                int callAt = pattern.Length - 1;
                uint callVa = va + (uint)callAt;
                uint target = callVa + 5 + ReadU32At(exe.Bytes, i + callAt + 1);
                if (target != SlotToUnitVa) continue;
                // 6.6 存在两个同构函数；穿透展开入口固定在范围引擎块 0x4364C7。
                int score = va == 0x004364C7 ? 2 : va is >= 0x00435000 and < 0x00438000 ? 1 : 0;
                if (score <= bestScore) continue;
                bestScore = score;
                best = va;
                bestLong = pattern == LongPattern;
                bestLength = bestLong ? 13 : 12;
            }
        }
        longPrologue = bestLong;
        prologueLength = bestLength;
        return best;
    }

    private uint FindCave(PeFile exe, out string detail)
    {
        uint best = 0;
        string bestSection = "";
        foreach (var section in exe.Sections)
        {
            bool executable = (section.Characteristics & 0x20000000) != 0;
            bool writable = section.IsWritable;
            if (!executable || !writable) continue;
            int offset = checked((int)section.RawOffset);
            int size = (int)Math.Min(section.RawSize, (uint)(exe.Bytes.Length - offset));
            int run = 0, runStart = 0;
            for (int i = 0; i <= size; i++)
            {
                bool filler = i < size && (exe.Bytes[offset + i] is 0x00 or 0xCC or 0x90);
                if (filler)
                {
                    if (run == 0) runStart = i;
                    run++;
                    continue;
                }
                if (run >= CaveBytes)
                {
                    uint startVa = section.VirtualAddress + (uint)runStart;
                    uint endVa = startVa + (uint)run;
                    uint candidate = Align16(Math.Max(startVa, _avoidEnd + 0x10));
                    if (candidate + CaveBytes <= endVa && !OverlapsAvoid(candidate))
                    {
                        if (candidate > best)
                        {
                            best = candidate;
                            bestSection = $"{section.Name}+0x{candidate - section.VirtualAddress:X}";
                        }
                    }
                }
                run = 0;
            }
        }
        detail = best == 0 ? "没有 96 字节以上的可执行空白" : bestSection;
        return best;
    }

    private bool OverlapsAvoid(uint start) =>
        _avoidStart != 0 && _avoidEnd > _avoidStart && start < _avoidEnd && start + CaveBytes > _avoidStart;

    private static uint Align16(uint value) => (value + 15) & ~15u;

    private static uint OffsetToVa(PeFile exe, int offset)
    {
        foreach (var section in exe.Sections)
        {
            uint delta = (uint)offset - section.RawOffset;
            if (delta < section.RawSize) return section.VirtualAddress + delta;
        }
        return 0;
    }

    private static uint ReadU32At(byte[] bytes, int offset) =>
        (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);

    private static void WriteU32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    public string Describe() => new StringBuilder()
        .AppendLine($"入口：0x{_entry:X8}（{( _longPrologue ? "6.4/6.5" : "6.6")} 布局，返回 0x{returnVa:X8}）")
        .AppendLine($"蹦床：0x{_cave:X8}")
        .AppendLine($"状态：{(Current == PenetrationCenter.Self ? "以自身为中心" : "以目标为中心")}")
        .ToString();

    private uint returnVa => _entry + (uint)_prologueLength;
}
