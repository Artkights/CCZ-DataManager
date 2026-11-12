using System.Text;
using TransformManager.App;

namespace TierManager.App;

public static class TierPatchBuilder
{
    public static EngineGrowth.Core.GrowthPatchPlan Prepare(TierProfile profile, IReadOnlyList<TierEntry> rows, byte[] before)
    {
        var validation = Validate(profile, rows, out var thresholds, out var labels);
        if (validation != null) throw new InvalidDataException(validation);
        if (!profile.CanWrite)
            throw new InvalidDataException(profile.ReadOnlyReason.Length > 0 ? profile.ReadOnlyReason : "当前基底不支持写入。");
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)),
                profile.ExeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件在载入后被修改，请重新扫描。");

        var after = before.ToArray();
        var image = new PeFile(profile.ExePath, after);
        uint codeVa = profile.CaveVa;
        var code = BuildFunction(profile, thresholds, labels, codeVa, out var calls);
        foreach (var (pos, target) in calls)
        {
            int actual = unchecked((int)(codeVa + (uint)pos + 4 + (uint)BitConverter.ToInt32(code, pos)));
            if ((uint)actual != target)
                throw new InvalidDataException($"生成代码自检失败：0x{codeVa + (uint)pos - 1:X8} 应调用 0x{target:X8}，实际 0x{(uint)actual:X8}。");
        }
        uint tableVa = Align4(codeVa + (uint)code.Length);
        int slots = profile.DisplayIndexOffset + labels.Count;
        uint labelsBase = tableVa + (uint)(slots * 4);
        var labelBytes = BuildLabels(labels, out var labelOffsets);
        uint markerVa = Align4(labelsBase + (uint)labelBytes.Length);
        var marker = new TierMarker
        {
            Kind = (int)profile.Kind,
            TierBase = profile.TierBase,
            CodeVa = codeVa,
            CodeLength = (ushort)code.Length,
            TableVa = tableVa,
            DisplayIndexOffset = profile.DisplayIndexOffset,
            DisplayPointerVa = profile.DisplayPointerVa,
            OriginalTierFuncVa = profile.OriginalTierFuncVa,
            OriginalTableVa = profile.OriginalTableVa,
            CaveVa = profile.CaveVa,
            CaveLength = (uint)profile.CaveLength,
            MaskTarget = profile.MaskTarget ?? 0,
            InlineMask = profile.InlineMask,
            CorrectionTarget = profile.CorrectionTarget ?? 0,
            MaskSequence = profile.MaskSequence
        };
        marker.CallSites.AddRange(profile.CallSites);
        marker.Thresholds.AddRange(thresholds);
        marker.Labels.AddRange(labels);
        var markerBytes = MarkerCodec.Build(marker);
        uint end = markerVa + (uint)markerBytes.Length;
        if (end > profile.CaveVa + (uint)profile.CaveLength)
            throw new InvalidDataException("档位数/字母过多，代码洞空间不足。");

        if (profile.HasMarker && profile.MarkerVa != 0)
            image.Patch(profile.MarkerVa, new byte[5]);

        image.Patch(codeVa, code);
        var table = new byte[slots * 4];
        if (profile.DisplayIndexOffset > 0 && labels.Count > 0)
        {
            uint fallback = labelsBase + (uint)labelOffsets[0];
            for (int i = 0; i < profile.DisplayIndexOffset; i++)
                BitConverter.GetBytes(fallback).CopyTo(table, i * 4);
        }
        for (int i = 0; i < labels.Count; i++)
        {
            uint labelVa = labelsBase + (uint)labelOffsets[i];
            BitConverter.GetBytes(labelVa).CopyTo(table, (profile.DisplayIndexOffset + i) * 4);
        }
        image.Patch(tableVa, table);
        image.Patch(labelsBase, labelBytes);
        image.Patch(markerVa, markerBytes);

        foreach (var site in profile.CallSites)
        {
            if (image.ReadByte(site) != 0xE8)
                throw new InvalidDataException($"调用点 0x{site:X8} 不是 E8。");
            image.Patch(site, [0xE8, .. BitConverter.GetBytes((int)(codeVa - (site + 5)))]);
        }
        if (image.ReadByte(profile.DisplayPointerVa) != 0xBA)
            throw new InvalidDataException("显示表指针不是 BA。");
        image.Patch(profile.DisplayPointerVa + 1, BitConverter.GetBytes(tableVa));

        var allowed = new List<(uint Va, int Length)>
        {
            (profile.CaveVa, profile.CaveLength)
        };
        if (profile.HasMarker && profile.MarkerVa != 0) allowed.Add((profile.MarkerVa, 5));
        foreach (var site in profile.CallSites) allowed.Add((site, 5));
        allowed.Add((profile.DisplayPointerVa + 1, 4));
        var violation = FindUnauthorizedChange(before, after, allowed);
        if (violation != null) throw new InvalidDataException(violation);

        Verify(profile, rows, after);
        return new EngineGrowth.Core.GrowthPatchPlan(before, after,
            $"档位表 {labels.Count} 档，阈值 {string.Join("/", thresholds)}");
    }

    public static void Verify(TierProfile expected, IReadOnlyList<TierEntry> rows, byte[] bytes)
    {
        var validation = Validate(expected, rows, out var thresholds, out var labels);
        if (validation != null) throw new InvalidDataException(validation);
        var readBack = TierScanner.LoadFromBytes(expected.ExePath, bytes, expected.RootPath);
        if (!SameModel(readBack, thresholds, labels, expected))
            throw new InvalidDataException("档位候选回读校验失败。");
    }

    public static string? Validate(TierProfile profile, IReadOnlyList<TierEntry> rows,
        out byte[] thresholds, out List<string> labels)
    {
        thresholds = [];
        labels = [];
        if (rows.Count is < 2 or > 12) return "档位数量必须在 2..12 之间。";
        if (rows[0].Threshold != null) return "第一行必须是无阈值的基准档。";
        var values = new List<byte>();
        for (int i = 1; i < rows.Count; i++)
        {
            if (rows[i].Threshold is not { } value) return $"第 {i + 1} 行缺少阈值。";
            if (values.Count > 0 && value <= values[^1]) return "阈值必须严格递增。";
            values.Add(value);
        }
        var gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Label)) return "显示字母不能为空。";
            if (row.Label.Contains('\0')) return "显示不能包含空字符。";
            int byteCount;
            try { byteCount = gbk.GetByteCount(row.Label); }
            catch (EncoderFallbackException) { return "显示包含 GBK 不能表示的字符。"; }
            if (byteCount > TierProfile.MaxLabelBytes)
                return $"显示字母过长：{row.Label}（每个最多 {TierProfile.MaxLabelBytes} 个 GBK 字节）";
            labels.Add(row.Label);
        }
        thresholds = values.ToArray();
        return null;
    }

    private static bool SameModel(TierProfile actual, byte[] thresholds, List<string> labels, TierProfile expected)
    {
        if (!actual.CanWrite) return false;
        if (!actual.Thresholds.SequenceEqual(thresholds)) return false;
        if (!actual.Labels.SequenceEqual(labels, StringComparer.Ordinal)) return false;
        if (actual.TierBase != expected.TierBase) return false;
        return actual.CallSites.Count == expected.CallSites.Count;
    }

    private static byte[] BuildFunction(TierProfile profile, byte[] thresholds, List<string> labels, uint codeVa,
        out List<(int Pos, uint Target)> calls)
    {
        var code = new CodeBuilder(codeVa);
        string invalid = "invalid";
        string epilogue = "epilogue";
        string maskEntry = "mask";
        string correctionEntry = "correction";
        bool hasCorrection = profile.CorrectionTarget != null;
        string done = profile.MaskSequence.Length > 0 ? maskEntry
            : hasCorrection ? correctionEntry
            : epilogue;

        code.Emit(0x55, 0x8B, 0xEC);             // push ebp; mov ebp,esp
        code.Emit(0x8B, 0x45, 0x08);             // mov eax,[ebp+8]
        code.Emit(0x3C, 0x04);                   // cmp al,4
        code.Jcc(0x87, invalid);                 // ja invalid
        code.Emit(0x8A, 0x44, 0x08, 0x21);       // mov al,[eax+ecx+21h]
        var ordered = thresholds
            .Select((threshold, index) => (Threshold: threshold, Tier: (byte)(profile.TierBase + index + 1)))
            .OrderByDescending(item => item.Threshold)
            .ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            string next = "next" + i;
            code.Emit(0x3C, ordered[i].Threshold); // cmp al,threshold
            code.Jcc(profile.UnsignedThresholds ? (byte)0x82 : (byte)0x8C, next); // jb / jl next
            code.Emit(0xB0, ordered[i].Tier);      // mov al,tier
            code.Jmp(done);
            code.Label(next);
        }
        code.Emit(0xB0, checked((byte)profile.TierBase)); // fallback
        code.Jmp(done);

        code.Label(invalid);
        code.Emit(0x31, 0xC0);                   // xor eax,eax
        code.Jmp(profile.MaskSequence.Length > 0 ? maskEntry
            : hasCorrection ? correctionEntry
            : epilogue);

        if (profile.MaskSequence.Length > 0)
        {
            code.Label(maskEntry);
            var sequence = profile.MaskSequence;
            if (sequence.Length < 5 || sequence[^5] != 0xE8)
                throw new InvalidOperationException("强化成长掩码序列格式不支持。");
            code.Emit(sequence[..^5]);
            code.Call(profile.MaskTarget ?? throw new InvalidOperationException("缺少强化成长查询目标。"));
            code.Emit(0x85, 0xC0);               // test eax,eax
            code.Jcc(0x84, epilogue);            // je epilogue
            code.Emit(0x8B, 0x4D, 0x08);         // mov ecx,[ebp+8]
            code.Emit(0xD3, 0xE8);               // shr eax,cl
            code.Emit(0x83, 0xE0, 0x01);         // and eax,1
            code.Emit(0xD1, 0xE0);               // shl eax,1
            code.Emit(0x01, 0x45, 0xFC);         // add [ebp-4],eax
        }
        else if (profile.CorrectionTarget is { } correction)
        {
            code.Label(correctionEntry);
            if (profile.CorrectionHelper is { } helper)
            {
                code.Emit(0x51);                 // push ecx
                code.Call(helper);
                code.Emit(0x59);                 // pop ecx
            }
            code.Emit(0x8B, 0x55, 0x08);         // mov edx,[ebp+8]
            code.Call(correction);
        }

        code.Label(epilogue);
        if (profile.MaskSequence.Length > 0) code.Emit(0x58); // pop eax
        code.Emit(0x8B, 0xE5);                   // mov esp,ebp
        code.Emit(0x5D);                         // pop ebp
        code.Emit(0xC2, 0x04, 0x00);             // ret 4
        calls = code.Calls.ToList();
        return code.Build();
    }

    private static byte[] BuildLabels(List<string> labels, out List<int> offsets)
    {
        var gbk = Encoding.GetEncoding("GBK");
        using var stream = new MemoryStream();
        offsets = new List<int>();
        foreach (var label in labels)
        {
            offsets.Add((int)stream.Length);
            var bytes = gbk.GetBytes(label);
            stream.Write(bytes);
            stream.WriteByte(0);
        }
        return stream.ToArray();
    }

    private static uint Align4(uint value) => (value + 3) & ~3u;

    private static string? FindUnauthorizedChange(byte[] before, byte[] after,
        IReadOnlyList<(uint Va, int Length)> allowed)
    {
        if (before.Length != after.Length) return "写入后文件长度变化，已取消。";
        var permitted = new HashSet<int>();
        // Convert allowed VAs using a PeFile over the staged image.
        var pe = new PeFile("Ekd5.exe", after);
        foreach (var (va, length) in allowed)
        {
            if (!pe.TryVaToOffset(va, out int offset)) continue;
            for (int i = 0; i < length; i++) permitted.Add(offset + i);
        }
        int unexpected = 0;
        for (int i = 0; i < before.Length; i++)
            if (before[i] != after[i] && !permitted.Contains(i)) unexpected++;
        return unexpected == 0 ? null : $"写入包含未授权字节：{unexpected} 字节，已取消。";
    }

    private sealed class CodeBuilder(uint baseVa)
    {
        private readonly List<byte> _bytes = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int Pos, string Label)> _jumps = [];
        private readonly List<(int Pos, uint Target)> _calls = [];

        public void Emit(params byte[] bytes) => _bytes.AddRange(bytes);
        public void Label(string name) => _labels[name] = _bytes.Count;
        public void Jmp(string label)
        {
            Emit(0xE9);
            _jumps.Add((_bytes.Count, label));
            Emit(0, 0, 0, 0);
        }
        public void Jcc(byte opcode, string label)
        {
            Emit(0x0F, opcode);
            _jumps.Add((_bytes.Count, label));
            Emit(0, 0, 0, 0);
        }
        public void Call(uint target)
        {
            Emit(0xE8);
            _calls.Add((_bytes.Count, target));
            Emit(0, 0, 0, 0);
        }

        public IReadOnlyList<(int Pos, uint Target)> Calls => _calls;
        public byte[] Build()
        {
            var result = _bytes.ToArray();
            foreach (var (pos, label) in _jumps)
            {
                if (!_labels.TryGetValue(label, out int target))
                    throw new InvalidOperationException("缺少标签：" + label);
                int rel = target - (pos + 4);
                BitConverter.GetBytes(rel).CopyTo(result, pos);
            }
            foreach (var (pos, target) in _calls)
            {
                int rel = unchecked((int)(target - (baseVa + (uint)pos + 4)));
                BitConverter.GetBytes(rel).CopyTo(result, pos);
            }
            return result;
        }
    }
}
