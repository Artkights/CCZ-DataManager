using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace DataManager.Core.Scenarios;

public sealed record ScenarioTokenReplacement(int Offset, byte[] ExpectedBytes, byte[] NewBytes, bool IsValueInsertion = false);
public sealed record EexParameter(int Tag, int Offset, int Length, int? Number = null);
public sealed record EexCommand(int Scene, int Section, int Index, int Ordinal, int Id, int Offset,
    IReadOnlyList<EexParameter> Parameters);
public sealed record EexLengthPrefix(int Offset, int ContentOffset, int Length);

/// <summary>A structural index over original bytes. Text is never decoded or re-encoded here.</summary>
public sealed class EexStructure
{
    public List<int> SceneOffsets { get; } = [];
    public List<EexLengthPrefix> Lengths { get; } = [];
    public List<EexCommand> Commands { get; } = [];

    public static EexStructure Parse(byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 14 || !bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 69, 69, 88, 0 }))
            throw new InvalidDataException("不是有效的 EEX 剧本。");
        var first = Read32(bytes, 10);
        if (first < 14 || first > bytes.Length || (first - 10) % 4 != 0)
            throw new InvalidDataException("EEX Scene 偏移表无效。");
        var result = new EexStructure();
        for (var offset = 10; offset < first; offset += 4)
        {
            var scene = Read32(bytes, offset);
            if (scene < first || scene > bytes.Length - 2 || result.SceneOffsets.Contains(scene))
                throw new InvalidDataException("EEX Scene 偏移越界或重复。");
            result.SceneOffsets.Add(scene);
        }
        for (var scene = 0; scene < result.SceneOffsets.Count; scene++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cursor = result.SceneOffsets[scene];
            var boundary = result.SceneOffsets.Where(value => value > cursor).DefaultIfEmpty(bytes.Length).Min();
            var sections = Read16(bytes, cursor); cursor += 2;
            for (var section = 0; section < sections; section++)
            {
                Ensure(cursor, 2, boundary);
                var length = Read16(bytes, cursor);
                Ensure(cursor + 2, length, boundary);
                result.Lengths.Add(new(cursor, cursor + 2, length)); cursor += 2;
                var index = 0;
                ParseBlock(cursor, length, scene + 1, section + 1, ref index, true, 0);
                cursor += length;
            }
        }
        return result;

        void ParseBlock(int start, int length, int scene, int section, ref int index, bool root, int depth)
        {
            if (depth > 512) throw new InvalidDataException("剧本嵌套深度超过 512。");
            var cursor = start; var end = checked(start + length); var head = root; var pending = false;
            while (cursor < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Commands.Count >= 1_000_000) throw new InvalidDataException("剧本命令数量过多。");
                Ensure(cursor, 2, end);
                var commandOffset = cursor; var id = Read16(bytes, cursor); cursor += 2;
                if (id >= EexCommandLayout.Instructions.Length) throw new InvalidDataException($"未知剧本命令 0x{id:X}，偏移 0x{commandOffset:X}。");
                var layout = EexCommandLayout.Instructions[id];
                var count = id switch { 0x46 => 11 * 20, 0x47 => 12 * 80, _ => layout.Length };
                var parameters = new List<EexParameter>();
                for (var i = 0; i < count; i++)
                {
                    var code = layout[id == 0x46 ? i % 11 : id == 0x47 ? i % 12 : i];
                    if (code == -1) break;
                    Ensure(cursor, 2, end); var tag = Read16(bytes, cursor); cursor += 2;
                    var valueStart = cursor; int? number = null;
                    if (code == 5)
                    {
                        var zero = Array.IndexOf(bytes, (byte)0, cursor, end - cursor);
                        if (zero < 0) throw new InvalidDataException("剧本文本缺少 NUL 终止符。");
                        cursor = zero + 1;
                    }
                    else if (code == 0x35)
                    {
                        Ensure(cursor, 2, end); var items = Read16(bytes, cursor);
                        Ensure(cursor + 2, checked(items * 2), end); cursor += 2 + items * 2;
                    }
                    else if (code == 4)
                    {
                        Ensure(cursor, 4, end); number = Read32(bytes, cursor); cursor += 4;
                    }
                    else { Ensure(cursor, 2, end); number = Read16(bytes, cursor); cursor += 2; }
                    parameters.Add(new(code, valueStart, cursor - valueStart, number));
                }
                result.Commands.Add(new(scene, section, ++index, result.Commands.Count, id, commandOffset, parameters));
                var opens = head && id == 0 || pending && EexCommandLayout.CanOpenSubEvent[id] != 0;
                if (opens)
                {
                    if (cursor + 2 > end)
                        throw new InvalidDataException($"Scene {scene} Section {section} 的命令 0x{id:X}（0x{commandOffset:X}）声明子块，但父块 0x{start:X}..0x{end:X} 没有完整长度前缀。");
                    Ensure(cursor, 2, end); var childLength = Read16(bytes, cursor);
                    Ensure(cursor + 2, childLength, end);
                    result.Lengths.Add(new(cursor, cursor + 2, childLength)); cursor += 2;
                    ParseBlock(cursor, childLength, scene, section, ref index, false, depth + 1);
                    cursor += childLength; head = false; pending = false;
                }
                else if (id != 1) pending = false;
                if (id == 1) pending = true;
            }
            if (cursor != end) throw new InvalidDataException("EEX 块长度不一致。");
        }
    }

    internal static int Read16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    internal static int Read32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Ensure(int offset, int length, int boundary)
    {
        if (offset < 0 || length < 0 || (long)offset + length > boundary)
            throw new InvalidDataException($"EEX 数据越界：0x{offset:X}，长度 {length}。");
    }
}

/// <summary>Replaces selected ASCII numeric tokens and only repairs structural lengths and relative jumps.</summary>
public sealed class ScenarioParameterPatchBuilder
{
    public byte[] Build(byte[] original, IReadOnlyList<ScenarioTokenReplacement> replacements)
    {
        var structure = EexStructure.Parse(original);
        var patches = replacements.Where(p => !p.ExpectedBytes.AsSpan().SequenceEqual(p.NewBytes)).OrderBy(p => p.Offset).ToArray();
        if (patches.Length == 0) return original.ToArray();
        var textParameters = structure.Commands.Where(command => command.Id == 0x72)
            .SelectMany(command => command.Parameters.Where(parameter => parameter.Tag == 5)).OrderBy(parameter => parameter.Offset).ToArray();
        var textIndex = -1;
        var priorEnd = -1;
        foreach (var patch in patches)
        {
            if (patch.Offset < 0 || patch.ExpectedBytes.Length == 0 && !patch.IsValueInsertion || (long)patch.Offset + patch.ExpectedBytes.Length > original.Length || patch.Offset < priorEnd ||
                !original.AsSpan(patch.Offset, patch.ExpectedBytes.Length).SequenceEqual(patch.ExpectedBytes))
                throw new InvalidDataException("剧本参数补丁重叠、越界或原始内容已改变。");
            if (!(patch.IsValueInsertion ? IsInsertedValue(patch.NewBytes) : IsExpression(patch.NewBytes))) throw new InvalidDataException("剧本参数只接受整数或 *变量号表达式。");
            while (textIndex + 1 < textParameters.Length && textParameters[textIndex + 1].Offset <= patch.Offset) textIndex++;
            if (textIndex < 0 || patch.Offset + patch.ExpectedBytes.Length >= textParameters[textIndex].Offset + textParameters[textIndex].Length)
                throw new InvalidDataException("补丁不在 72 指令的文本参数中。");
            priorEnd = patch.Offset + patch.ExpectedBytes.Length;
        }
        var starts = new int[patches.Length]; var ends = new int[patches.Length]; var deltas = new int[patches.Length + 1];
        for (var index = 0; index < patches.Length; index++)
        {
            var patch = patches[index]; starts[index] = patch.Offset; ends[index] = checked(patch.Offset + patch.ExpectedBytes.Length);
            deltas[index + 1] = checked(deltas[index] + patch.NewBytes.Length - patch.ExpectedBytes.Length);
        }
        var candidate = new byte[checked(original.Length + deltas[^1])]; var source = 0; var destination = 0;
        foreach (var patch in patches)
        {
            var length = patch.Offset - source;
            original.AsSpan(source, length).CopyTo(candidate.AsSpan(destination)); destination += length;
            patch.NewBytes.CopyTo(candidate.AsSpan(destination)); destination += patch.NewBytes.Length;
            source = patch.Offset + patch.ExpectedBytes.Length;
        }
        original.AsSpan(source).CopyTo(candidate.AsSpan(destination));
        int Map(int offset) => checked(offset + deltas[Bound(ends, offset, upper: true)]);
        for (var i = 0; i < structure.SceneOffsets.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(candidate.AsSpan(10 + i * 4, 4), Map(structure.SceneOffsets[i]));
        foreach (var block in structure.Lengths)
        {
            var delta = checked(deltas[Bound(starts, checked(block.ContentOffset + block.Length), upper: false)] -
                deltas[Bound(starts, block.ContentOffset, upper: false)]);
            var next = checked(block.Length + delta);
            if (next is < 0 or > ushort.MaxValue) throw new InvalidDataException("修改后的 Section 或嵌套块超过 65535 字节。");
            BinaryPrimitives.WriteUInt16LittleEndian(candidate.AsSpan(Map(block.Offset), 2), (ushort)next);
        }
        var commandOffsets = structure.Commands.Select(command => command.Offset).ToHashSet();
        foreach (var command in structure.Commands.Where(command => command.Id == 0x76))
        {
            var parameter = command.Parameters.First(p => p.Tag == 4);
            var target = checked(command.Offset + 8 + parameter.Number!.Value);
            var newDisplacement = checked(Map(target) - Map(command.Offset) - 8);
            if (newDisplacement != parameter.Number && !commandOffsets.Contains(target))
                throw new InvalidDataException($"0x76 跳转目标 0x{target:X} 不对应已解析命令，无法安全调整参数长度。");
            BinaryPrimitives.WriteInt32LittleEndian(candidate.AsSpan(Map(parameter.Offset), 4), newDisplacement);
        }
        var reread = EexStructure.Parse(candidate);
        if (!structure.Commands.Select(c => (c.Scene, c.Section, c.Index, c.Id)).SequenceEqual(reread.Commands.Select(c => (c.Scene, c.Section, c.Index, c.Id))))
            throw new InvalidDataException("局部参数修改改变了剧本命令结构。");
        // Every byte outside numeric tokens and the documented structural fields must survive verbatim.
        var allowed = new List<(int Start, int End)>();
        void Allow(int offset, int length) => allowed.Add((offset, checked(offset + length)));
        foreach (var p in patches) Allow(p.Offset, p.ExpectedBytes.Length);
        Allow(10, structure.SceneOffsets.Count * 4);
        foreach (var block in structure.Lengths) Allow(block.Offset, 2);
        foreach (var c in structure.Commands.Where(c => c.Id == 0x76)) Allow(c.Parameters.First(p => p.Tag == 4).Offset, 4);
        var verifiedUntil = 0;
        foreach (var interval in allowed.OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (interval.Start > verifiedUntil) VerifyUnchanged(verifiedUntil, interval.Start - verifiedUntil);
            verifiedUntil = Math.Max(verifiedUntil, interval.End);
        }
        if (verifiedUntil < original.Length) VerifyUnchanged(verifiedUntil, original.Length - verifiedUntil);
        return candidate;

        void VerifyUnchanged(int offset, int length)
        {
            // Zero-length insertions remain interval boundaries, so no span
            // crosses an inserted token. Every other original byte outside
            // the exact tokens/structural fields is compared without copying.
            if (!original.AsSpan(offset, length).SequenceEqual(candidate.AsSpan(Map(offset), length)))
                throw new InvalidDataException("补丁改变了未授权的剧本字节。");
        }
    }

    private static int Bound(int[] values, int value, bool upper)
    {
        var first = 0; var end = values.Length;
        while (first < end)
        {
            var middle = first + (end - first) / 2;
            if (values[middle] < value || upper && values[middle] == value) first = middle + 1;
            else end = middle;
        }
        return first;
    }

    internal static bool IsExpression(byte[] value)
        => value.Length > 0 && value.All(b => b < 128) && Regex.IsMatch(System.Text.Encoding.ASCII.GetString(value), @"^-?\*?\d+$", RegexOptions.CultureInvariant);
    private static bool IsInsertedValue(byte[] value) => value.Length > 0 && value.All(b => b < 128) &&
        Regex.IsMatch(System.Text.Encoding.ASCII.GetString(value), @"^(?:\r\n|\r|\n)?-?\*?\d+(?:\r\n|\r|\n)?$", RegexOptions.CultureInvariant);
}
