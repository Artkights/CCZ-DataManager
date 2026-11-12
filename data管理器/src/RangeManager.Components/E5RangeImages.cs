using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RangeManager.App;

/// <summary>
/// 读取/写回 Hitarea.e5（攻击范围图标）与 Effarea.e5（穿透范围图标）。
/// 图号 = 形状/字段 ID + 1。6.4/6.5 的条目是 PNG，6.6 的 Hitarea 是
/// 4096 字节的 64×64 索引图，需要用 Spalet.e5 的调色板解码。
/// 图片仅为配套输出资源，几何预览始终来自 EXE；像素外观统一使用内嵌的
/// 信奈 Effarea.e5 第 10 项固定帧和四个 8×8 像素块。
/// </summary>
public sealed class E5RangeImages : IDisposable
{
    public const int GridSize = 64;
    private const int IndexOffset = 0x110;
    private const int EntrySize = 12;

    private sealed class Entry
    {
        public int Number;
        public byte[] Stored = [];
        public int DecodedLength;
        public byte[]? OriginalStored;
        public int OriginalDecodedLength;
        public bool Indexed;
        public byte[] RawSuffix = [];
    }

    private sealed class Archive
    {
        public string Path = "";
        public byte[] Header = [];
        public int Trailer;
        public byte[] DirectoryTrailer = [];
        public List<Entry> Entries = [];
        public readonly Dictionary<int, Bitmap?> Cache = [];
        public bool Dirty;
        public string Error = "";
        public bool Loaded;

        /// <summary>6.6 的 Hitarea 条目是 64×64 索引图，写回时必须保持同样的格式。</summary>
        public bool Indexed;
    }

    private readonly Archive _attack = new();
    private readonly Archive _pierce = new();
    private readonly IReadOnlyList<Color> _palette;
    private readonly bool _paletteValid;
    private readonly Dictionary<int, int> _paletteIndices;

    public E5RangeImages(string rootPath)
    {
        _palette = LoadPalette(rootPath, out _paletteValid);
        _paletteIndices = _palette
            .Select((color, index) => (Key: RgbKey(color), Index: index))
            .GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.First().Index);
        _attack.Path = FindFile(rootPath, "Hitarea.e5");
        _pierce.Path = FindFile(rootPath, "Effarea.e5");
        LoadArchive(_attack);
        LoadArchive(_pierce);
    }

    public string AttackPath => _attack.Path;
    public string PiercePath => _pierce.Path;
    public int AttackCount => _attack.Entries.Count;
    public int PierceCount => _pierce.Entries.Count;
    public bool CanReadAttack => _attack.Loaded;
    public bool CanReadPierce => _pierce.Loaded;
    public bool AttackIsIndexed => _attack.Loaded && _attack.Indexed;
    public bool PierceIsIndexed => _pierce.Loaded && _pierce.Indexed;

    /// <summary>返回某个图号解码后的字节数，用于核对写回格式。</summary>
    public int DecodedLength(bool attack, int shapeId)
    {
        var archive = Pick(attack);
        int number = shapeId + 1;
        if (archive == null || !archive.Loaded || number < 1 || number > archive.Entries.Count) return 0;
        return archive.Entries[number - 1].DecodedLength;
    }
    public bool IsDirty => _attack.Dirty || _pierce.Dirty;
    public string AttackError => _attack.Error;
    public string PierceError => _pierce.Error;

    public string Summary =>
        $"Hitarea.e5 {(_attack.Loaded ? _attack.Entries.Count + " 张" : "不可用")} · " +
        $"Effarea.e5 {(_pierce.Loaded ? _pierce.Entries.Count + " 张" : "不可用")}";

    private static string FindFile(string root, string name)
    {
        string inE5 = System.IO.Path.Combine(root, "E5", name);
        if (File.Exists(inE5)) return inE5;
        string direct = System.IO.Path.Combine(root, name);
        return File.Exists(direct) ? direct : inE5;
    }

    private static IReadOnlyList<Color> LoadPalette(string root, out bool valid)
    {
        valid = false;
        var gray = Enumerable.Range(0, 256).Select(i => Color.FromArgb(255, i, i, i)).ToArray();
        string path = FindFile(root, "Spalet.e5");
        if (!File.Exists(path)) return gray;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var entries = ParseDirectory(bytes, out _, out _);
            foreach (var (stored, decoded, offset) in entries)
            {
                if (decoded < 768 || offset + stored > bytes.Length) continue;
                var payload = stored == decoded
                    ? bytes.AsSpan(offset, stored).ToArray()
                    : LsDecode(bytes.AsSpan(0x10, 256).ToArray(), bytes.AsSpan(offset, stored).ToArray(), decoded);
                if (payload.Length < 768) continue;
                valid = true;
                return Enumerable.Range(0, 256)
                    .Select(i => Color.FromArgb(255, payload[i * 3 + 1], payload[i * 3 + 2], payload[i * 3]))
                    .ToArray();
            }
        }
        catch
        {
            // 调色板只影响 6.6 的 RAW 图标；失败时退化为灰度预览。
        }
        return gray;
    }

    private static List<(int Stored, int Decoded, int Offset)> ParseDirectory(byte[] bytes, out int trailer, out int firstPayload)
    {
        trailer = 0;
        firstPayload = 0;
        var list = new List<(int, int, int)>();
        if (bytes.Length < IndexOffset + EntrySize + 4) return list;
        int first = (int)ReadBe(bytes, IndexOffset + 8);
        if (first <= IndexOffset || first > bytes.Length) return list;
        int region = first - IndexOffset;
        trailer = region % EntrySize;
        if (trailer is not (0 or 4)) throw new InvalidDataException("E5 目录尾部长度不合法");
        int count = (region - trailer) / EntrySize;
        for (int i = 0; i < count; i++)
        {
            int at = IndexOffset + i * EntrySize;
            int stored = (int)ReadBe(bytes, at);
            int decoded = (int)ReadBe(bytes, at + 4);
            int offset = (int)ReadBe(bytes, at + 8);
            if (stored <= 0 || decoded <= 0 || decoded > 64 * 1024 * 1024 || offset < first || (long)offset + stored > bytes.Length)
                throw new InvalidDataException($"E5 第 {i + 1} 项长度或偏移不合法");
            list.Add((stored, decoded, offset));
        }
        firstPayload = first;
        return list;
    }

    private void LoadArchive(Archive archive)
    {
        archive.Entries.Clear();
        archive.Cache.Clear();
        archive.Dirty = false;
        archive.Error = "";
        archive.Loaded = false;
        if (!File.Exists(archive.Path))
        {
            archive.Error = "未找到 " + System.IO.Path.GetFileName(archive.Path);
            return;
        }
        try
        {
            var bytes = File.ReadAllBytes(archive.Path);
            var list = ParseDirectory(bytes, out int trailer, out int first);
            if (list.Count == 0) throw new InvalidDataException("E5 目录为空或偏移无效");
            archive.Header = bytes.AsSpan(0, Math.Min(IndexOffset, bytes.Length)).ToArray();
            archive.Trailer = trailer == 4 ? 4 : 0;
            archive.DirectoryTrailer = bytes.AsSpan(first - archive.Trailer, archive.Trailer).ToArray();
            for (int i = 0; i < list.Count; i++)
            {
                var (stored, decoded, offset) = list[i];
                var payload = bytes.AsSpan(offset, stored).ToArray();
                var decodedPayload = stored == decoded ? payload : LsDecode(archive.Header.AsSpan(0x10, 256).ToArray(), payload, decoded);
                bool indexed = !LooksLikeImage(decodedPayload) && decodedPayload.Length is 4096 or 4098;
                if (!indexed && !LooksLikeImage(decodedPayload)) throw new InvalidDataException($"第 {i + 1} 项不是支持的范围图像");
                archive.Entries.Add(new Entry
                {
                    Number = i + 1,
                    Stored = payload,
                    DecodedLength = decoded,
                    OriginalStored = payload,
                    OriginalDecodedLength = decoded,
                    Indexed = indexed,
                    RawSuffix = indexed ? decodedPayload.AsSpan(4096).ToArray() : []
                });
            }
            archive.Loaded = true;
            archive.Indexed = false;
            try
            {
                var firstEntry = list[0];
                var firstPayload = firstEntry.Stored == firstEntry.Decoded
                    ? bytes.AsSpan(firstEntry.Offset, firstEntry.Stored).ToArray()
                    : LsDecode(bytes.AsSpan(0x10, 256).ToArray(),
                        bytes.AsSpan(firstEntry.Offset, firstEntry.Stored).ToArray(), firstEntry.Decoded);
                // 索引图即使被 LS 压缩，解码后也应是 4096 字节且不是 PNG/BMP/JPEG。
                archive.Indexed = (firstPayload.Length is 4096 or 4098) && !LooksLikeImage(firstPayload);
            }
            catch
            {
                archive.Indexed = false;
            }
            _ = first;
        }
        catch (Exception ex)
        {
            archive.Error = System.IO.Path.GetFileName(archive.Path) + "：" + ex.Message;
        }
    }

    private Archive? Pick(bool attack) => attack ? _attack : _pierce;

    /// <summary>按图号取真实图标；图号 = ID + 1，越界返回 null。</summary>
    public Bitmap? Get(bool attack, int shapeId)
    {
        var archive = Pick(attack);
        if (archive == null || !archive.Loaded) return null;
        int number = shapeId + 1;
        if (number <= 0 || number > archive.Entries.Count) return null;
        if (archive.Cache.TryGetValue(number, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            bitmap = Decode(archive, archive.Entries[number - 1]);
        }
        catch
        {
            bitmap = null;
        }
        archive.Cache[number] = bitmap;
        return bitmap;
    }

    /// <summary>
    /// Returns an owned copy of one decoded E5 range entry.  The archive cache
    /// keeps its own bitmaps alive for the lifetime of the session, while UI
    /// controls such as the extension mapping grid may dispose their row
    /// previews independently.
    /// </summary>
    public Bitmap? Copy(bool attack, int shapeId)
    {
        var source = Get(attack, shapeId);
        if (source == null) return null;
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
            copy.SetPixel(x, y, source.GetPixel(x, y));
        return copy;
    }

    /// <summary>
    /// Returns a fresh copy of the fixed pixel template embedded with the
    /// manager.  The template is always Signal's Effarea.e5 entry 10; the
    /// current base, range id and either E5 archive never influence colours
    /// or line artwork.
    /// </summary>
    public RangePixelTiles CreateFixedPixelTiles() => FixedRangePixelTemplate.Create();

    /// <summary>
    /// Compatibility entry point retained for existing callers. Pixel
    /// materials are fixed and are never selected from the current base.
    /// </summary>
    public RangePixelTiles CreatePixelTiles(bool attack, int shapeId) => CreateFixedPixelTiles();

    /// <summary>把生成的新图标写进对应图号；图号缺失时按顺序补齐空图。格式与档案一致。</summary>
    public void SetIcon(bool attack, int shapeId, Bitmap bitmap)
    {
        var archive = Pick(attack);
        if (archive == null || !archive.Loaded)
            throw new InvalidDataException(System.IO.Path.GetFileName(archive?.Path ?? "") + " 不可用，无法写回图标");
        int number = shapeId + 1;
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(shapeId));
        while (archive.Entries.Count < number)
        {
            using var tiles = CreateFixedPixelTiles();
            using var blank = RangeBitmapRenderer.RenderIconFromTiles(attack ? RangeKind.Attack : RangeKind.Pierce, [], null, tiles);
            var blankPayload = EncodeIcon(archive.Indexed, [], blank);
            archive.Entries.Add(new Entry
            {
                Number = archive.Entries.Count + 1,
                Stored = blankPayload,
                DecodedLength = blankPayload.Length,
                OriginalStored = null,
                OriginalDecodedLength = 0,
                Indexed = archive.Indexed
            });
            archive.Dirty = true;
        }
        var entry = archive.Entries[number - 1];
        var payload = EncodeIcon(entry.Indexed, entry.RawSuffix, bitmap);
        // 没有实质变化时不标记脏数据，避免无意义地重写 E5。
        if (entry.Stored.AsSpan().SequenceEqual(payload)) return;
        entry.Stored = payload;
        entry.DecodedLength = payload.Length;
        archive.Cache.Remove(number);
        archive.Dirty = true;
    }

    /// <summary>恢复某个图号的原始像素；工具新增的图号会被移除。</summary>
    public void RestoreIcon(bool attack, int shapeId)
    {
        var archive = Pick(attack);
        if (archive == null || !archive.Loaded) return;
        int number = shapeId + 1;
        if (number <= 0) return;
        if (number > archive.Entries.Count) return;
        var entry = archive.Entries[number - 1];
        if (entry.OriginalStored == null)
        {
            // 仅当它是尾部追加的最后一个图号时才缩短档案；其它情况保留图号并写回空白图，
            // 否则会把后面工具追加的图号一起删掉，造成图号错位。
            if (number == archive.Entries.Count)
            {
                archive.Entries.RemoveAt(number - 1);
                archive.Cache.Remove(number);
                archive.Dirty = true;
                return;
            }
            using var tiles = CreateFixedPixelTiles();
            using var blank = RangeBitmapRenderer.RenderIconFromTiles(attack ? RangeKind.Attack : RangeKind.Pierce, [], null, tiles);
            var payload = EncodeIcon(entry.Indexed, entry.RawSuffix, blank);
            if (entry.Stored.AsSpan().SequenceEqual(payload)) return;
            entry.Stored = payload;
            entry.DecodedLength = payload.Length;
            archive.Cache.Remove(number);
            archive.Dirty = true;
            return;
        }
        if (entry.Stored.AsSpan().SequenceEqual(entry.OriginalStored)) return;
        entry.Stored = entry.OriginalStored;
        entry.DecodedLength = entry.OriginalDecodedLength;
        archive.Cache.Remove(number);
        archive.Dirty = true;
    }

    /// <summary>该图号是否已被工具改写（与磁盘原始像素不同）。</summary>
    public bool IsIconModified(bool attack, int shapeId)
    {
        var archive = Pick(attack);
        if (archive == null || !archive.Loaded) return false;
        int number = shapeId + 1;
        if (number <= 0 || number > archive.Entries.Count) return false;
        var entry = archive.Entries[number - 1];
        if (entry.OriginalStored == null) return true;
        return !entry.Stored.AsSpan().SequenceEqual(entry.OriginalStored);
    }

    public void Save()
    {
        SaveArchive(_attack);
        SaveArchive(_pierce);
    }

    /// <summary>Build candidates without changing disk or accepting a new undo baseline.</summary>
    public IReadOnlyDictionary<string, byte[]> BuildPendingFiles()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in new[] { _attack, _pierce })
            if (archive.Loaded && archive.Dirty)
            {
                byte[] bytes = BuildArchive(archive);
                ValidateArchiveBytes(bytes);
                result.Add(archive.Path, bytes);
            }
        return result;
    }

    public static void ValidateArchiveBytes(byte[] bytes)
    {
        var entries = ParseDirectory(bytes, out _, out _);
        if (entries.Count == 0) throw new InvalidDataException("范围 E5 目录为空");
        foreach (var (stored, decoded, offset) in entries)
        {
            byte[] payload = stored == decoded ? bytes.AsSpan(offset, stored).ToArray()
                : LsDecode(bytes.AsSpan(0x10, 256).ToArray(), bytes.AsSpan(offset, stored).ToArray(), decoded);
            if (LooksLikeImage(payload))
            {
                using var stream = new MemoryStream(payload, writable: false);
                using var image = Image.FromStream(stream);
                if (image.Width != 64 || image.Height != 64) throw new InvalidDataException("范围资源必须为 64×64");
            }
            else if (payload.Length is not (4096 or 4098))
                throw new InvalidDataException("范围 RAW 索引图长度不合法");
        }
    }

    private static byte[] BuildArchive(Archive archive)
    {
        int dataStart = IndexOffset + archive.Entries.Count * EntrySize + archive.Trailer;
        int total = dataStart + archive.Entries.Sum(e => e.Stored.Length);
        var output = new byte[total];
        Buffer.BlockCopy(archive.Header, 0, output, 0, Math.Min(archive.Header.Length, IndexOffset));
        archive.DirectoryTrailer.CopyTo(output, dataStart - archive.Trailer);
        int payloadOffset = dataStart;
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            var entry = archive.Entries[i];
            int at = IndexOffset + i * EntrySize;
            WriteBe(output, at, (uint)entry.Stored.Length);
            WriteBe(output, at + 4, (uint)entry.DecodedLength);
            WriteBe(output, at + 8, (uint)payloadOffset);
            Buffer.BlockCopy(entry.Stored, 0, output, payloadOffset, entry.Stored.Length);
            payloadOffset += entry.Stored.Length;
        }
        return output;
    }

    private static void SaveArchive(Archive archive)
    {
        if (!archive.Dirty || !archive.Loaded) return;
        byte[] output = BuildArchive(archive);
        ValidateArchiveBytes(output);
        string backup = archive.Path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        File.Copy(archive.Path, backup, overwrite: false);
        string temp = archive.Path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, output);
        File.Move(temp, archive.Path, overwrite: true);
        foreach (var entry in archive.Entries)
        {
            entry.OriginalStored = entry.Stored;
            entry.OriginalDecodedLength = entry.DecodedLength;
        }
        archive.Dirty = false;
    }

    private Bitmap? Decode(Archive archive, Entry entry)
    {
        byte[] payload;
        if (entry.Stored.Length == entry.DecodedLength)
        {
            payload = entry.Stored;
        }
        else
        {
            var dictionary = archive.Header.Length >= 0x110
                ? archive.Header.AsSpan(0x10, 256).ToArray()
                : new byte[256];
            payload = LsDecode(dictionary, entry.Stored, entry.DecodedLength);
        }

        if (LooksLikeImage(payload))
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var image = new Bitmap(stream);
            var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++) copy.SetPixel(x, y, image.GetPixel(x, y));
            return copy;
        }

        // 6.6 的 Hitarea 是 64×64 索引图（4096 字节，个别档案 4098）。
        if (payload.Length >= GridSize * GridSize) return DecodeIndexed(payload);
        return null;
    }

    private Bitmap DecodeIndexed(byte[] payload)
    {
        var bitmap = new Bitmap(GridSize, GridSize, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, GridSize, GridSize), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var output = new byte[data.Stride * GridSize];
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    var color = _palette[payload[y * GridSize + x]];
                    int at = y * data.Stride + x * 4;
                    output[at] = color.B;
                    output[at + 1] = color.G;
                    output[at + 2] = color.R;
                    output[at + 3] = 255;
                }
            }
            Marshal.Copy(output, 0, data.Scan0, output.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static bool LooksLikeImage(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && ((head[0] == 'B' && head[1] == 'M') || (head[0] == 0xFF && head[1] == 0xD8)))
            return true;
        if (head.Length < 8) return false;
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return head[..8].SequenceEqual(png);
    }

    /// <summary>按档案原有格式编码：PNG 或 64×64 调色板索引图。</summary>
    private byte[] EncodeIcon(bool indexed, byte[] suffix, Bitmap bitmap)
    {
        if (bitmap.Width != GridSize || bitmap.Height != GridSize)
            throw new InvalidDataException("范围资源必须保持 64×64，不能截取大图的左上角");
        if (!indexed) return EncodePng(bitmap);
        if (!_paletteValid) throw new InvalidDataException("Spalet.e5 缺失或无法解析，不能写入索引图");
        return [.. EncodeIndexed(bitmap), .. suffix];
    }

    private byte[] EncodeIndexed(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, GridSize, GridSize), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * GridSize];
        var output = new byte[GridSize * GridSize];
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    int at = y * data.Stride + x * 4;
                    output[y * GridSize + x] = (byte)PaletteIndex(
                        Color.FromArgb(pixels[at + 3], pixels[at + 2], pixels[at + 1], pixels[at]));
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return output;
    }

    private int PaletteIndex(Color color)
    {
        int key = RgbKey(color);
        if (_paletteIndices.TryGetValue(key, out int index)) return index;
        int distance = int.MaxValue;
        index = 0;
        for (int i = 0; i < _palette.Count; i++)
        {
            var candidate = _palette[i];
            int red = color.R - candidate.R, green = color.G - candidate.G, blue = color.B - candidate.B;
            int candidateDistance = red * red + green * green + blue * blue;
            // Ascending iteration and a strict comparison keep the lowest
            // index when two palette entries are equally close.
            if (candidateDistance >= distance) continue;
            distance = candidateDistance;
            index = i;
        }
        _paletteIndices.Add(key, index);
        return index;
    }

    private static int RgbKey(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] LsDecode(byte[] dictionary, byte[] encoded, int decodedLength)
    {
        var decoded = new byte[decodedLength];
        int inputIndex = 0, bitPosition = 7, outputIndex = 0, backDistance = 0;
        while (outputIndex < decodedLength)
        {
            if (inputIndex >= encoded.Length) throw new InvalidDataException("LS 压缩流提前结束");
            uint code = 0;
            int bitLength = 0;
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
                if (inputIndex >= encoded.Length) throw new InvalidDataException("LS 压缩掩码提前结束");
                bit = (encoded[inputIndex] >> bitPosition) & 1;
                mask = (mask << 1) | (uint)bit;
                if (--bitPosition < 0) { bitPosition = 7; inputIndex++; }
            }
            code += mask;
            if (backDistance == 0 && code >= 256)
            {
                backDistance = (int)code - 256;
                if (backDistance == 0) throw new InvalidDataException("LS 回溯距离为零");
                continue;
            }
            if (backDistance == 0)
            {
                decoded[outputIndex++] = dictionary[code];
                continue;
            }
            int count = (int)code + 3;
            while (count-- > 0)
            {
                int source = outputIndex - backDistance;
                if (source < 0 || outputIndex >= decoded.Length) throw new InvalidDataException("LS 回溯越界");
                decoded[outputIndex++] = decoded[source];
            }
            backDistance = 0;
        }
        return decoded;
    }

    private static uint ReadBe(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    private static void WriteBe(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);

    /// <summary>Compatibility entry point; the live editor and E5 use the same renderer.</summary>
    public static Bitmap RenderIcon(ShapeLayer layer, IReadOnlyCollection<CellOffset> cells)
    {
        using var tiles = FixedRangePixelTemplate.Create();
        return RangeBitmapRenderer.RenderIconFromTiles(
            layer == ShapeLayer.Attack ? RangeKind.Attack : RangeKind.Pierce, cells, null, tiles);
    }

    public void Dispose()
    {
        foreach (var archive in new[] { _attack, _pierce })
        {
            foreach (var bitmap in archive.Cache.Values)
                bitmap?.Dispose();
            archive.Cache.Clear();
        }
    }
}
