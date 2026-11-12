namespace TransformManager.App;

public sealed record RuleMetadata(int Version, int FillByte, int CaveLength, byte[] Prologue, IReadOnlyList<EffectRule> Rules);

/// <summary>Compiles per-effect (level -> value) rules into a cave code block plus a readable data block.</summary>
public static class RuleCompiler
{
    public const int MaxStagesPerEffect = 8;
    private static readonly byte[] Magic = "TMR1"u8.ToArray();
    private static readonly byte[] OriginalPrologue = [0x55, 0x8B, 0xEC, 0x0F, 0xB6, 0xD2];

    public static (byte[] Blob, int CodeLength) Build(IReadOnlyList<EffectRule> rules, uint caveVa, uint entryVa,
        int fillByte = 0x90, int caveLength = 0)
    {
        var normalized = rules
            .Where(rule => rule.Stages.Count > 0)
            .GroupBy(rule => rule.EffectId)
            .Select(group => new EffectRule(group.Key,
                group.SelectMany(rule => rule.Stages)
                    .GroupBy(stage => stage.Level)
                    .Select(stage => stage.Last())
                    .OrderByDescending(stage => stage.Level)
                    .ToList()))
            .OrderBy(rule => rule.EffectId)
            .ToList();

        foreach (var rule in normalized)
        {
            if (rule.EffectId is < 0 or > 255) throw new InvalidDataException($"特效号超出范围：{rule.EffectId}");
            if (rule.Stages.Count > MaxStagesPerEffect) throw new InvalidDataException($"每条规则最多 {MaxStagesPerEffect} 段。");
            foreach (var stage in rule.Stages)
            {
                if (stage.Level is < 1 or > 99) throw new InvalidDataException("等级必须在 1..99。");
                if (stage.Value is < 0 or > 255) throw new InvalidDataException("数值必须在 0..255。");
            }
        }

        var code = new List<byte>();
        var labels = new Dictionary<string, int>();
        var rel8 = new List<(int Offset, string Label)>();
        var rel32 = new List<(int Offset, string Label)>();
        var absolute = new List<(int Offset, uint Target)>();

        // 原函数入口会先执行 movzx edx,dl；规则路径提前返回时也保持相同的
        // EDX 零扩展契约。EBP/ESP 不动，ECX/EAX 返回规则值，EBX/ESI/EDI 不碰。
        code.AddRange([0x0F, 0xB6, 0xD2]); // movzx edx,dl

        for (int r = 0; r < normalized.Count; r++)
        {
            var rule = normalized[r];
            labels[$"block{r}"] = code.Count;
            code.AddRange([0x80, 0x78, 0xFE, (byte)rule.EffectId]); // cmp byte ptr [eax-2], id
            int jne = code.Count + 2;
            code.AddRange([0x0F, 0x85, 0x00, 0x00, 0x00, 0x00]);     // jne next block (near)
            rel32.Add((jne, $"next{r}"));

            for (int s = 0; s < rule.Stages.Count; s++)
            {
                var stage = rule.Stages[s];
                labels[$"stage{r}_{s}"] = code.Count;
                code.AddRange([0x80, 0xFA, (byte)stage.Level]);      // cmp dl, level
                int jb = code.Count + 2;
                code.AddRange([0x0F, 0x82, 0x00, 0x00, 0x00, 0x00]); // jb next stage / fallback
                rel32.Add((jb, s + 1 < rule.Stages.Count ? $"stage{r}_{s + 1}" : "fallback"));
                code.AddRange([0xB9, (byte)stage.Value, 0x00, 0x00, 0x00]); // mov ecx, value
                code.AddRange([0x8B, 0xC1, 0xC3]);                   // mov eax, ecx; ret
            }
            labels[$"next{r}"] = code.Count;
        }

        labels["fallback"] = code.Count;
        code.AddRange([0x55, 0x8B, 0xEC, 0x0F, 0xB6, 0xD2]);         // push ebp; mov ebp,esp; movzx edx,dl
        int jmpOffset = code.Count + 1;
        code.AddRange([0xE9, 0x00, 0x00, 0x00, 0x00]);                // jmp entry+5
        absolute.Add((jmpOffset, entryVa + 6)); // 入口只替换了 5 字节，跳过残留的 D2

        foreach (var (offset, label) in rel8)
        {
            int target = labels[label];
            code[offset] = checked((byte)(target - (offset + 1)));
        }
        foreach (var (offset, label) in rel32)
        {
            int target = labels[label];
            int delta = target - (offset + 4);
            code[offset] = (byte)delta;
            code[offset + 1] = (byte)(delta >> 8);
            code[offset + 2] = (byte)(delta >> 16);
            code[offset + 3] = (byte)(delta >> 24);
        }
        foreach (var (offset, targetVa) in absolute)
        {
            int delta = unchecked((int)(targetVa - (caveVa + (uint)(offset + 4))));
            code[offset] = (byte)delta;
            code[offset + 1] = (byte)(delta >> 8);
            code[offset + 2] = (byte)(delta >> 16);
            code[offset + 3] = (byte)(delta >> 24);
        }

        int codeLength = code.Count;
        var blob = new List<byte>(code);
        blob.AddRange(Magic);
        blob.Add(1);
        blob.Add((byte)fillByte);
        blob.AddRange(BitConverter.GetBytes(caveLength));
        blob.AddRange(OriginalPrologue);
        blob.Add(checked((byte)normalized.Count));
        foreach (var rule in normalized)
        {
            blob.Add((byte)rule.EffectId);
            blob.Add(checked((byte)rule.Stages.Count));
            foreach (var stage in rule.Stages)
            {
                blob.Add((byte)stage.Level);
                blob.Add((byte)stage.Value);
            }
        }
        return (blob.ToArray(), codeLength);
    }

    public static RuleMetadata? ReadMetadata(byte[] memory)
    {
        var result = new List<EffectRule>();
        int magicIndex = memory.AsSpan().IndexOf(Magic);
        if (magicIndex < 0) return null;
        int cursor = magicIndex + Magic.Length;
        if (cursor + 13 > memory.Length) return null;
        int version = memory[cursor++];
        if (version != 1) return null;
        int fill = memory[cursor++];
        int caveLength = BitConverter.ToInt32(memory, cursor);
        cursor += 4;
        if (caveLength < 600 || caveLength > 16 * 1024 * 1024 || fill is not (0x00 or 0x90 or 0xCC)) return null;
        var prologue = memory.AsSpan(cursor, 6).ToArray();
        cursor += 6;
        int count = memory[cursor++];
        for (int i = 0; i < count; i++)
        {
            if (cursor + 2 > memory.Length) return null;
            int effect = memory[cursor++];
            int stageCount = memory[cursor++];
            if (stageCount is < 1 or > MaxStagesPerEffect) return null;
            var stages = new List<RuleStage>();
            for (int s = 0; s < stageCount; s++)
            {
                if (cursor + 2 > memory.Length) return null;
                stages.Add(new RuleStage(memory[cursor++], memory[cursor++]));
            }
            result.Add(new EffectRule(effect, stages));
        }
        return new RuleMetadata(version, fill, caveLength, prologue, result);
    }

    public static List<EffectRule> ReadBlob(byte[] memory)
        => ReadMetadata(memory)?.Rules.ToList() ?? new List<EffectRule>();

    public static int RequiredSize(IReadOnlyList<EffectRule> rules, uint caveVa, uint entryVa,
        int fillByte = 0x90, int caveLength = 0)
        => Build(rules, caveVa, entryVa, fillByte, caveLength).Blob.Length;
}
