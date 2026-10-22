using System.Globalization;
using DataManager.Core.Models;

namespace DataManager.Core.Binary;

public sealed class BinaryTableWriter
{
    private readonly RecordCodecRegistry _codecs = new();

    public FileByteChange BuildChange(TableDocument document, string projectRoot, IReadOnlyList<TableCellChange> changes)
    {
        if (changes.Count == 0)
        {
            var unchanged = File.ReadAllBytes(document.FilePath);
            var hash = HashService.Sha256(unchanged);
            return new FileByteChange(Path.GetRelativePath(projectRoot, document.FilePath), hash, hash, unchanged.Length, []);
        }

        var before = File.ReadAllBytes(document.FilePath);
        var after = (byte[])before.Clone();
        var chunks = new List<ByteChunk>();
        foreach (var change in changes)
        {
            var rowOffset = checked((int)(change.Table.DataOffset + (long)change.RowIndex * change.Table.RowSize));
            var row = before.AsSpan(rowOffset, change.Table.RowSize);
            foreach (var patch in _codecs.Get(change.Table.RecordCodecKey).EncodeChangedField(row, change.Field, change.NewValue))
            {
                var absolute = checked(rowOffset + patch.Offset);
                var oldBytes = before.AsSpan(absolute, patch.Bytes.Length).ToArray();
                Buffer.BlockCopy(patch.Bytes, 0, after, absolute, patch.Bytes.Length);
                chunks.Add(new ByteChunk(absolute, oldBytes, patch.Bytes));
            }
        }

        return new FileByteChange(
            Path.GetRelativePath(projectRoot, document.FilePath),
            HashService.Sha256(before),
            HashService.Sha256(after),
            after.Length,
            MergeChunks(chunks));
    }

    public void Write(FileByteChange change, string projectRoot, bool useNewBytes)
    {
        var path = Path.Combine(projectRoot, change.RelativePath);
        var current = File.ReadAllBytes(path);
        var expected = useNewBytes ? change.BeforeHash : change.AfterHash;
        var currentHash = HashService.Sha256(current);
        if (!string.Equals(currentHash, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"历史记录冲突：{change.RelativePath} 当前文件已被外部修改。");
        }

        if (change.WholeFileReplacement)
        {
            if (change.Chunks.Count != 1 || change.Chunks[0].Offset != 0)
                throw new InvalidDataException("完整文件历史必须包含唯一的原始/候选快照。");
            var snapshot = change.Chunks[0];
            var source = useNewBytes ? snapshot.OldBytes : snapshot.NewBytes;
            if (!current.AsSpan().SequenceEqual(source))
                throw new IOException($"完整文件历史基线不一致：{change.RelativePath}。");
            current = (byte[])(useNewBytes ? snapshot.NewBytes : snapshot.OldBytes).Clone();
        }
        else foreach (var chunk in change.Chunks)
        {
            var bytes = useNewBytes ? chunk.NewBytes : chunk.OldBytes;
            Buffer.BlockCopy(bytes, 0, current, checked((int)chunk.Offset), bytes.Length);
        }
        var temporary = path + ".data-manager-" + Guid.NewGuid().ToString("N") + ".tmp";
        var attributes = File.GetAttributes(path);
        try
        {
            File.WriteAllBytes(temporary, current);
            if (!HashService.Sha256(temporary).Equals(useNewBytes ? change.AfterHash : change.BeforeHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"候选文件校验失败：{change.RelativePath}。");
            if (!HashService.Sha256(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"提交前文件已被外部修改：{change.RelativePath}。");
            if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, attributes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        var finalHash = HashService.Sha256(path);
        var expectedFinal = useNewBytes ? change.AfterHash : change.BeforeHash;
        if (!string.Equals(finalHash, expectedFinal, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"写入复读校验失败：{change.RelativePath}。");
        }
    }

    private static IReadOnlyList<ByteChunk> MergeChunks(IEnumerable<ByteChunk> input)
        => ByteChunkMerger.MergeAdjacent(input);
}
