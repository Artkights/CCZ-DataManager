using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace ImageManager.Core;

public sealed record E5Entry(int ImageNumber, int StoredLength, int DecodedLength, int DataOffset)
{
    public bool IsCompressed => StoredLength != DecodedLength;
}

public sealed class E5Archive
{
    public const int IndexOffset = 0x110;
    public const int EntrySize = 12;
    private const int LsHeaderLength = 16;
    private const int LsDictionaryLength = 256;
    private readonly byte[] _bytes;
    private static readonly ConcurrentDictionary<string, CachedArchive> Cache = new(StringComparer.OrdinalIgnoreCase);

    private E5Archive(string path, byte[] bytes, IReadOnlyList<E5Entry> entries, int trailerLength)
    {
        Path = path;
        _bytes = bytes;
        Entries = entries;
        DirectoryTrailerLength = trailerLength;
    }

    public string Path { get; }
    public IReadOnlyList<E5Entry> Entries { get; }
    public int DirectoryTrailerLength { get; }

    public static E5Archive Open(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!ShouldCache(fullPath)) return Parse(fullPath, ReadSnapshot(fullPath));
        var info = new FileInfo(fullPath);
        if (Cache.TryGetValue(fullPath, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            return cached.Archive;
        var archive = Parse(fullPath, ReadSnapshot(fullPath));
        Cache[fullPath] = new CachedArchive(info.Length, info.LastWriteTimeUtc, archive);
        return archive;
    }

    /// <summary>
    /// Opens the legacy Meff strategy-animation archive.  A few 6.5/18州
    /// archives put the first payload bytes immediately after the 64-entry
    /// directory, so the four bytes normally interpreted as a directory
    /// trailer are actually the beginning of the PNG/RAW payload.  Ordinary
    /// E5 archives keep the strict zero-trailer validation in <see cref="Open"/>.
    /// </summary>
    public static E5Archive OpenMeff(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        return Parse(fullPath, ReadSnapshot(fullPath), allowNonZeroDirectoryTrailer: true);
    }

    // Mutation builders must use the bytes on disk even when an external tool
    // has preserved both the length and timestamp of a cached resource.
    public static E5Archive OpenFresh(string path) => Parse(System.IO.Path.GetFullPath(path), ReadSnapshot(path));

    private static byte[] ReadSnapshot(string path)
    {
        // Game/debug processes may keep a writable shared handle open. Preview remains read-only.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = new FileInfo(path);
            var length = before.Length; var modified = before.LastWriteTimeUtc;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (length > int.MaxValue) throw new InvalidDataException("E5 资源超过可支持的快照大小。");
            var bytes = new byte[checked((int)length)];
            try { stream.ReadExactly(bytes); }
            catch (EndOfStreamException) { continue; }
            var after = new FileInfo(path);
            if (stream.Length == length && after.Length == length && after.LastWriteTimeUtc == modified) return bytes;
        }
        throw new IOException("读取期间资源文件发生变化，请等待游戏或其他工具完成写入后重试。");
    }

    public static bool TryOpen(string path, out E5Archive? archive, out string error)
    {
        try
        {
            archive = Open(path);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            archive = null;
            error = ex.Message;
            return false;
        }
    }

    public static bool TryOpenMeff(string path, out E5Archive? archive, out string error)
    {
        try
        {
            archive = OpenMeff(path);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            archive = null;
            error = ex.Message;
            return false;
        }
    }

    public static void Invalidate(string path) => Cache.TryRemove(System.IO.Path.GetFullPath(path), out _);

    public static E5Archive Parse(string logicalPath, byte[] bytes)
        => Parse(logicalPath, bytes, allowNonZeroDirectoryTrailer: false);

    private static E5Archive Parse(string logicalPath, byte[] bytes, bool allowNonZeroDirectoryTrailer)
    {
        try { return ParseSequential(logicalPath, bytes, allowNonZeroDirectoryTrailer); }
        catch (InvalidDataException)
        {
            // 洗隋 6.4 Face.e5 has eight JPEG entries redirected to appended payloads.
            // Its directory still ends at an explicit four-byte zero sentinel.
            // Restrict this recovery to Face and prove every uncompressed image;
            // arbitrary E5/RAW files retain the ordinary strict directory rules.
            if (TryParseRedirectedFace(logicalPath, bytes, out var archive)) return archive!;
            throw;
        }
    }

    private static bool TryParseRedirectedFace(string path, byte[] bytes, out E5Archive? archive)
    {
        archive = null;
        if (!System.IO.Path.GetFileName(path).Equals("Face.e5", StringComparison.OrdinalIgnoreCase) ||
            bytes.Length < IndexOffset + EntrySize + 4 || !bytes.AsSpan(0, 4).SequenceEqual("Ls12"u8)) return false;
        var entries = new List<E5Entry>();
        var directoryEnd = IndexOffset;
        var firstPayload = bytes.Length;
        while (directoryEnd <= bytes.Length - 4)
        {
            var stored = ReadUInt32(bytes, directoryEnd);
            if (stored == 0) break;
            if (directoryEnd > bytes.Length - EntrySize || directoryEnd + EntrySize > firstPayload) return false;
            var decoded = ReadUInt32(bytes, directoryEnd + 4);
            var dataOffset = ReadUInt32(bytes, directoryEnd + 8);
            if (stored != decoded || stored > int.MaxValue || dataOffset > int.MaxValue ||
                dataOffset < directoryEnd + EntrySize || (ulong)dataOffset + stored > (ulong)bytes.Length) return false;
            firstPayload = Math.Min(firstPayload, (int)dataOffset);
            entries.Add(new(entries.Count + 1, (int)stored, (int)decoded, (int)dataOffset));
            directoryEnd += EntrySize;
        }
        if (entries.Count < 2 || directoryEnd > bytes.Length - 4 || ReadUInt32(bytes, directoryEnd) != 0 ||
            firstPayload < directoryEnd + 4 || entries[0].DataOffset == directoryEnd + 4) return false;
        var ordered = entries.OrderBy(entry => entry.DataOffset).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var entry = ordered[index];
            if (index > 0 && entry.DataOffset < ordered[index - 1].DataOffset + (long)ordered[index - 1].StoredLength ||
                !ImageRendering.IsStandardImage(bytes.AsSpan(entry.DataOffset, entry.StoredLength).ToArray())) return false;
        }
        archive = new E5Archive(path, bytes, entries, 4);
        return true;
    }

    private static E5Archive ParseSequential(string logicalPath, byte[] bytes, bool allowNonZeroDirectoryTrailer)
    {
        if (bytes.Length < IndexOffset + EntrySize) throw new InvalidDataException("文件过短，不包含 E5 索引。");
        var firstOffset = ReadUInt32(bytes, IndexOffset + 8);
        if (firstOffset <= IndexOffset || firstOffset > bytes.Length) throw new InvalidDataException("E5 首个载荷偏移无效。");
        var directoryRegion = checked((int)firstOffset - IndexOffset);
        var trailer = directoryRegion % EntrySize;
        if (trailer is not (0 or 4)) throw new InvalidDataException($"E5 目录尾长度 {trailer} 不受支持。");
        if (!allowNonZeroDirectoryTrailer && trailer == 4 && bytes.AsSpan((int)firstOffset - 4, 4).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("E5 目录的四字节尾部不是全零。");
        var count = (directoryRegion - trailer) / EntrySize;
        if (count <= 0) throw new InvalidDataException("E5 目录没有条目。");

        var entries = new List<E5Entry>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = IndexOffset + index * EntrySize;
            var stored = checked((int)ReadUInt32(bytes, offset));
            var decoded = checked((int)ReadUInt32(bytes, offset + 4));
            var dataOffset = checked((int)ReadUInt32(bytes, offset + 8));
            if (stored <= 0 || decoded <= 0 || dataOffset < firstOffset || (long)dataOffset + stored > bytes.Length)
                throw new InvalidDataException($"E5 图号 #{index + 1} 的目录项无效。");
            entries.Add(new E5Entry(index + 1, stored, decoded, dataOffset));
        }
        return new E5Archive(logicalPath, bytes, entries, trailer);
    }

    public byte[] ReadStored(int imageNumber)
    {
        var entry = GetEntry(imageNumber);
        return _bytes.AsSpan(entry.DataOffset, entry.StoredLength).ToArray();
    }

    public byte[] ReadDecoded(int imageNumber)
    {
        var entry = GetEntry(imageNumber);
        var stored = ReadStored(imageNumber);
        if (!entry.IsCompressed) return stored;
        if (_bytes.Length < LsHeaderLength + LsDictionaryLength) throw new InvalidDataException("E5 文件缺少 LS 字典。");
        var dictionary = _bytes.AsSpan(LsHeaderLength, LsDictionaryLength).ToArray();
        return DecodeLs(dictionary, stored, entry.DecodedLength);
    }

    public byte[] Rebuild(
        IReadOnlyDictionary<int, byte[]>? replacementDecoded = null,
        IReadOnlyCollection<int>? deleteImageNumbers = null,
        IReadOnlyList<byte[]>? appendDecoded = null,
        IReadOnlyDictionary<int, IReadOnlyList<byte[]>>? insertBeforeDecoded = null)
    {
        replacementDecoded ??= new Dictionary<int, byte[]>();
        deleteImageNumbers ??= Array.Empty<int>();
        appendDecoded ??= Array.Empty<byte[]>();
        insertBeforeDecoded ??= new Dictionary<int, IReadOnlyList<byte[]>>();
        var deletes = deleteImageNumbers.ToHashSet();
        if (deletes.Any(number => number <= 0 || number > Entries.Count)) throw new ArgumentOutOfRangeException(nameof(deleteImageNumbers));
        if (replacementDecoded.Keys.Any(number => number <= 0 || number > Entries.Count || deletes.Contains(number)))
            throw new ArgumentOutOfRangeException(nameof(replacementDecoded));
        if (insertBeforeDecoded.Keys.Any(number => number <= 0 || number > Entries.Count || deletes.Contains(number)))
            throw new ArgumentOutOfRangeException(nameof(insertBeforeDecoded));
        if (replacementDecoded.Values.Any(bytes => bytes.Length == 0) || appendDecoded.Any(bytes => bytes.Length == 0) ||
            insertBeforeDecoded.Values.SelectMany(payloads => payloads).Any(bytes => bytes.Length == 0))
            throw new InvalidDataException("E5 条目载荷不能为空。");

        var payloads = new List<(byte[] Bytes, int DecodedLength)>();
        foreach (var entry in Entries)
        {
            if (deletes.Contains(entry.ImageNumber)) continue;
            if (insertBeforeDecoded.TryGetValue(entry.ImageNumber, out var inserted))
                payloads.AddRange(inserted.Select(bytes => (bytes, bytes.Length)));
            if (replacementDecoded.TryGetValue(entry.ImageNumber, out var replacement)) payloads.Add((replacement, replacement.Length));
            else payloads.Add((ReadStored(entry.ImageNumber), entry.DecodedLength));
        }
        payloads.AddRange(appendDecoded.Select(bytes => (bytes, bytes.Length)));
        if (payloads.Count == 0) throw new InvalidOperationException("不能生成没有条目的 E5 档案。");

        var dataStart = checked(IndexOffset + payloads.Count * EntrySize + DirectoryTrailerLength);
        var totalLength = checked(dataStart + payloads.Sum(item => item.Bytes.Length));
        var output = new byte[totalLength];
        Buffer.BlockCopy(_bytes, 0, output, 0, Math.Min(IndexOffset, _bytes.Length));
        var payloadOffset = dataStart;
        for (var index = 0; index < payloads.Count; index++)
        {
            var directoryOffset = IndexOffset + index * EntrySize;
            var payload = payloads[index];
            WriteUInt32(output, directoryOffset, checked((uint)payload.Bytes.Length));
            WriteUInt32(output, directoryOffset + 4, checked((uint)payload.DecodedLength));
            WriteUInt32(output, directoryOffset + 8, checked((uint)payloadOffset));
            Buffer.BlockCopy(payload.Bytes, 0, output, payloadOffset, payload.Bytes.Length);
            payloadOffset += payload.Bytes.Length;
        }
        _ = Parse(Path, output);
        return output;
    }

    private E5Entry GetEntry(int imageNumber)
    {
        if (imageNumber <= 0 || imageNumber > Entries.Count) throw new ArgumentOutOfRangeException(nameof(imageNumber));
        return Entries[imageNumber - 1];
    }

    private static byte[] DecodeLs(byte[] dictionary, byte[] encoded, int decodedLength)
    {
        var decoded = new byte[decodedLength];
        var inputIndex = 0;
        var bitPosition = 7;
        var outputIndex = 0;
        var backDistance = 0;
        while (outputIndex < decodedLength)
        {
            if (inputIndex >= encoded.Length) throw new InvalidDataException("LS 压缩流提前结束。");
            uint code = 0;
            var bitLength = 0;
            int bit;
            do
            {
                bit = (encoded[inputIndex] >> bitPosition) & 1;
                code = (code << 1) | (uint)bit;
                bitLength++;
                if (--bitPosition < 0) { bitPosition = 7; inputIndex++; }
            } while (bit != 0);
            uint mask = 0;
            while (bitLength-- > 0)
            {
                if (inputIndex >= encoded.Length) throw new InvalidDataException("LS 压缩掩码提前结束。");
                bit = (encoded[inputIndex] >> bitPosition) & 1;
                mask = (mask << 1) | (uint)bit;
                if (--bitPosition < 0) { bitPosition = 7; inputIndex++; }
            }
            code += mask;
            if (backDistance == 0 && code >= LsDictionaryLength)
            {
                backDistance = checked((int)code - LsDictionaryLength);
                if (backDistance == 0) throw new InvalidDataException("LS 回溯距离为零。");
                continue;
            }
            if (backDistance == 0)
            {
                if (code >= dictionary.Length) throw new InvalidDataException("LS 字典索引越界。");
                decoded[outputIndex++] = dictionary[(int)code];
                continue;
            }
            var copyCount = checked((int)code + 3);
            while (copyCount-- > 0)
            {
                var source = outputIndex - backDistance;
                if (source < 0 || outputIndex >= decoded.Length) throw new InvalidDataException("LS 回溯区间无效。");
                decoded[outputIndex++] = decoded[source];
            }
            backDistance = 0;
        }
        return decoded;
    }

    private static uint ReadUInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    private static void WriteUInt32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);

    private static bool ShouldCache(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.Equals("Pmapobj.e5", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Unit_mov.e5", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Unit_atk.e5", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Unit_spc.e5", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Face.e5", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Fb.e5", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Item.e5", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Mtem.e5", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Spalet.e5", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CachedArchive(long Length, DateTime LastWriteUtc, E5Archive Archive);
}
