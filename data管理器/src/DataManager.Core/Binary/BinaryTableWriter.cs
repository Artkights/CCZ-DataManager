using System.Globalization;
using DataManager.Core.History;
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
        var path = ProjectHistoryPaths.ResolveInside(projectRoot, change.RelativePath);
        var sourceExists = useNewBytes ? change.BeforeExists : change.AfterExists;
        var targetExists = useNewBytes ? change.AfterExists : change.BeforeExists;
        if (!change.BeforeExists && !change.AfterExists)
            throw new InvalidDataException("文件历史的前后状态不能都不存在。");
        if ((!sourceExists || !targetExists) && !change.WholeFileReplacement)
            throw new InvalidDataException("文件创建和删除历史必须包含完整前后快照。");
        VerifyState(change, projectRoot, useAfter: !useNewBytes);
        var current = sourceExists ? File.ReadAllBytes(path) : [];
        var expected = useNewBytes ? change.BeforeHash : change.AfterHash;
        if (sourceExists && !HashService.Sha256(current).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"历史记录冲突：{change.RelativePath} 当前文件已被外部修改。");
        }

        if (change.WholeFileReplacement)
        {
            if (change.Chunks.Count != 1 || change.Chunks[0].Offset != 0)
                throw new InvalidDataException("完整文件历史必须包含唯一的原始/候选快照。");
            var snapshot = change.Chunks[0];
            if ((!change.BeforeExists && snapshot.OldBytes.Length != 0) ||
                (!change.AfterExists && snapshot.NewBytes.Length != 0))
                throw new InvalidDataException("不存在文件的一侧必须使用空快照。");
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
        var expectedFinal = useNewBytes ? change.AfterHash : change.BeforeHash;
        if (targetExists && !HashService.Sha256(current).Equals(expectedFinal, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"候选文件校验失败：{change.RelativePath}。");
        var temporary = path + ".data-manager-" + Guid.NewGuid().ToString("N") + ".tmp";
        var attributes = sourceExists ? File.GetAttributes(path) : FileAttributes.Normal;
        try
        {
            if (targetExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(temporary, current);
                if (!HashService.Sha256(temporary).Equals(expectedFinal, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"候选文件校验失败：{change.RelativePath}。");
            }
            VerifyState(change, projectRoot, useAfter: !useNewBytes);
            if (sourceExists && attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            if (targetExists) File.Move(temporary, path, overwrite: sourceExists);
            else File.Delete(path);
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, attributes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        VerifyState(change, projectRoot, useAfter: useNewBytes);
    }

    internal static bool MatchesState(FileByteChange change, string projectRoot, bool useAfter)
    {
        var path = ProjectHistoryPaths.ResolveInside(projectRoot, change.RelativePath);
        var exists = useAfter ? change.AfterExists : change.BeforeExists;
        if (Directory.Exists(path)) return false;
        if (!File.Exists(path)) return !exists;
        return exists && HashService.Sha256(path).Equals(useAfter ? change.AfterHash : change.BeforeHash,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static void VerifyState(FileByteChange change, string projectRoot, bool useAfter)
    {
        if (!MatchesState(change, projectRoot, useAfter))
            throw new IOException($"历史记录冲突：{change.RelativePath} 当前文件存在性或 SHA-256 已被外部修改。");
    }

    private static IReadOnlyList<ByteChunk> MergeChunks(IEnumerable<ByteChunk> input)
        => ByteChunkMerger.MergeAdjacent(input);
}
