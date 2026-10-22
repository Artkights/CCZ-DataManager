using System.Buffers.Binary;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ImageManager.Core;

public sealed class DllBitmapResourceCodec : IResourceCodec
{
    public ResourceKind Kind => ResourceKind.DllBitmap;
    public bool CanHandle(ImageResourceDescriptor resource) => resource.Kind == Kind;

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
    {
        var bitmaps = DllBitmapReader.Read(resource.Paths.Single());
        var preferred = bitmaps.GroupBy(item => item.Id).OrderBy(group => group.Key)
            .Select(group => group.FirstOrDefault(item => item.Language == 2052) ?? group.First()).ToArray();
        if (preferred.Length == 0) return Array.Empty<LogicalImageAsset>();
        // Item and strategy libraries use consecutive small/large pairs.
        // Interface DLLs instead contain independent, differently sized images
        // under sparse native IDs; a missing ID must never end enumeration.
        if (resource.Category is not (ImageCategory.ItemIcon or ImageCategory.StrategyIcon))
            return preferred.Select(record => new LogicalImageAsset(resource.Key, record.Id,
                $"{resource.DisplayName} ID={record.Id}", resource.Category, Kind,
                [new FrameDescriptor(resource.Key, resource.Paths.Single(), record.Id, 0, 1, "bitmap", "none",
                    record.Width, record.Height, record.Language)], Array.Empty<string>(), false, false,
                $"原生 ID={record.Id}；{record.Width}x{record.Height}；独立位图，保留稀疏编号")).ToArray();
        var baseId = preferred.Min(item => item.Id);
        var byId = preferred.ToDictionary(item => item.Id);
        var assets = new List<LogicalImageAsset>();
        for (var logicalId = 0; ; logicalId++)
        {
            var smallId = baseId + logicalId * 2;
            var largeId = smallId + 1;
            if (!byId.TryGetValue(smallId, out var small)) break;
            var hasLarge = byId.TryGetValue(largeId, out var large);
            var pair = new List<(DllBitmapRecord Record, string Label)> { (small, "small") };
            if (hasLarge) pair.Add((large!, "large"));
            var frames = pair.Select(item => new FrameDescriptor(
                resource.Key, resource.Paths.Single(), item.Record.Id, 0, 1, item.Label, "none",
                item.Record.Width, item.Record.Height, item.Record.Language)).ToArray();
            assets.Add(new LogicalImageAsset(resource.Key, logicalId, $"{resource.DisplayName} {logicalId:000}",
                resource.Category, Kind, frames, Array.Empty<string>(), false, false,
                string.Join(" / ", frames.Select(frame => $"{(frame.Action == "small" ? "小图" : "大图")} ID={frame.ImageNumber}, {frame.Width}x{frame.Height}"))));
            if (!hasLarge) break;
        }
        return assets;
    }

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
    {
        var resources = DllBitmapReader.Read(asset.Frames[0].SourcePath);
        return asset.Frames.Select(frame =>
        {
            var record = resources.First(item => item.Id == frame.ImageNumber && item.Language == frame.SourceOffset);
            return new DecodedFrame(frame, DllBitmapReader.DecodeDib(record.Dib));
        }).ToArray();
    }
}

internal sealed record DllBitmapRecord(int Id, int Language, int Width, int Height, byte[] Dib);

internal static class DllBitmapReader
{
    private const uint LoadLibraryAsDatafile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;
    private static readonly IntPtr RtBitmap = (IntPtr)2;

    public static IReadOnlyList<DllBitmapRecord> Read(string path, bool requireAllBitmapRecords = false)
    {
        var module = LoadLibraryExW(Path.GetFullPath(path), IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero) throw new InvalidOperationException("无法以资源方式载入 DLL，Win32Error=" + Marshal.GetLastWin32Error());
        try
        {
            var records = new List<DllBitmapRecord>();
            var errors = new List<string>();
            EnumResNameProc? nameCallback = null;
            EnumResLangProc? languageCallback = null;
            languageCallback = (_, type, name, language, _) =>
            {
                var resource = FindResourceExW(module, type, name, language);
                if (resource == IntPtr.Zero) { if (requireAllBitmapRecords) errors.Add("RT_BITMAP资源地址无法读取。"); return true; }
                var size = SizeofResource(module, resource);
                var loaded = LoadResource(module, resource);
                var pointer = LockResource(loaded);
                if (size == 0 || pointer == IntPtr.Zero) { if (requireAllBitmapRecords) errors.Add("RT_BITMAP资源为空或无法锁定。"); return true; }
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                var id = NumericId(name);
                if (id <= 0 || bytes.Length < 12)
                {
                    if (requireAllBitmapRecords) errors.Add(id <= 0 ? "包含未确认的命名RT_BITMAP资源。" : $"RT_BITMAP ID={id}信息头被截断。");
                    return true;
                }
                try
                {
                    var header = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                    var width = header == 12 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)) : Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)));
                    var height = header == 12 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)) : Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
                    if (width == 0 || height == 0 || (header != 12 && (header < 40 || header > bytes.Length)))
                    {
                        if (requireAllBitmapRecords) errors.Add($"RT_BITMAP ID={id}信息头或尺寸无效。");
                        return true;
                    }
                    records.Add(new DllBitmapRecord(id, language, width, height, bytes));
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
                { if (requireAllBitmapRecords) errors.Add($"RT_BITMAP ID={id}：{ex.Message}"); }
                return true;
            };
            nameCallback = (_, type, name, _) =>
            {
                if (NumericId(name) > 0)
                {
                    if (!EnumResourceLanguagesW(module, type, name, languageCallback!, IntPtr.Zero) && requireAllBitmapRecords)
                        errors.Add("RT_BITMAP语言目录无法完整枚举，Win32Error=" + Marshal.GetLastWin32Error());
                }
                else if (requireAllBitmapRecords) errors.Add("包含未确认的命名RT_BITMAP资源。");
                return true;
            };
            if (!EnumResourceNamesW(module, RtBitmap, nameCallback, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1813) throw new InvalidOperationException("枚举 RT_BITMAP 失败，Win32Error=" + error);
            }
            if (errors.Count > 0) throw new InvalidDataException(string.Join("；", errors));
            return records;
        }
        finally { FreeLibrary(module); }
    }

    public static Bitmap DecodeDib(byte[] dib)
    {
        if (dib.Length < 16) throw new InvalidDataException("DIB 数据过短。");
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0, 4));
        // The game and toolbox store even positive-height BI_RGB 32bpp resources
        // top first. GDI's ordinary BMP decoder reverses those rows and discards
        // their alpha, so use the verified storage decoder for this exact case.
        if (headerSize is 40 or 52 or 56 or 108 or 124 && dib.Length >= headerSize &&
            BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2)) == 32 &&
            BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16, 4)) == 0)
            return DllImportEncoding.Decode(dib);
        int pixelOffset;
        if (headerSize == 12)
        {
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(10, 2));
            pixelOffset = 14 + headerSize + (bitCount <= 8 ? (1 << bitCount) * 3 : 0);
        }
        else if (headerSize >= 40 && headerSize <= dib.Length)
        {
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
            var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32, 4));
            var paletteCount = colorsUsed > 0 ? checked((int)colorsUsed) : bitCount <= 8 ? 1 << bitCount : 0;
            var masks = headerSize == 40 && BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16, 4)) == 3 ? 12 : 0;
            pixelOffset = checked(14 + headerSize + masks + paletteCount * 4);
        }
        else throw new InvalidDataException("DIB 信息头不受支持。");

        var bmp = new byte[checked(dib.Length + 14)];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), (uint)pixelOffset);
        Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
        using var stream = new MemoryStream(bmp, writable: false);
        using var image = Image.FromStream(stream, false, true);
        return new Bitmap(image);
    }

    private static int NumericId(IntPtr value)
    {
        var raw = unchecked((ulong)value.ToInt64());
        return (raw >> 16) == 0 ? (int)(raw & 0xFFFF) : -1;
    }

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    private delegate bool EnumResLangProc(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceLanguagesW(IntPtr module, IntPtr type, IntPtr name, EnumResLangProc callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResourceExW(IntPtr module, IntPtr type, IntPtr name, ushort language);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr resource);
}

public static class DllBitmapMutation
{
    private const int RtBitmap = 2;

    public static byte[] BuildAppendCandidate(string path, IReadOnlyList<Bitmap> sources)
    {
        if (sources.Count == 0) throw new InvalidOperationException("没有待新增的 DLL 图标。");
        var records = DllBitmapReader.Read(path).OrderBy(item => item.Id).ThenBy(item => item.Language).ToArray();
        var ids = records.Select(item => item.Id).Distinct().OrderBy(value => value).ToArray();
        var referenceSmall = records.FirstOrDefault() ?? throw new InvalidDataException("DLL 没有可参考的 RT_BITMAP。");
        var referenceLarge = records.Skip(1).FirstOrDefault() ?? referenceSmall;
        var nextId = ids.Length == 0 ? 1 : ids[^1] + 1;
        var language = referenceSmall.Language;
        var updates = new List<ResourceUpdate>();
        foreach (var source in sources)
        {
            updates.Add(new ResourceUpdate(nextId++, language, EncodeDib(source, referenceSmall.Width, referenceSmall.Height)));
            updates.Add(new ResourceUpdate(nextId++, language, EncodeDib(source, referenceLarge.Width, referenceLarge.Height)));
        }
        return Apply(path, updates, Array.Empty<(int, int)>());
    }

    public static byte[] BuildAppendItemCandidate(string path, IReadOnlyList<Bitmap> largeSources,
        IItemIconDerivationService derivation)
    {
        if (largeSources.Count == 0) throw new InvalidOperationException("没有待新增的宝物大图。");
        var records = DllBitmapReader.Read(path).OrderBy(item => item.Id).ThenBy(item => item.Language).ToArray();
        var ids = records.Select(item => item.Id).Distinct().OrderBy(value => value).ToArray();
        if (ids.Length < 2) throw new InvalidDataException("DLL 缺少可参考的小图/大图 RT_BITMAP 对。");
        var small = records.First(item => item.Id == ids[0]);
        var large = records.First(item => item.Id == ids[1]);
        if (large.Width != small.Width * 2 || large.Height != small.Height * 2)
            throw new InvalidDataException("DLL 宝物图标大小图尺寸不是严格两倍关系。");
        var nextId = ids[^1] + 1;
        var updates = new List<ResourceUpdate>();
        foreach (var source in largeSources)
        {
            if (source.Size != new Size(large.Width, large.Height))
                throw new InvalidDataException($"宝物大图尺寸必须为 {large.Width}x{large.Height}。");
            using var derived = derivation.DeriveSmall(source, new Size(small.Width, small.Height), Color.Blue);
            updates.Add(new ResourceUpdate(nextId++, small.Language, EncodeDib(derived, small.Width, small.Height)));
            updates.Add(new ResourceUpdate(nextId++, large.Language, EncodeDib(source, large.Width, large.Height)));
        }
        return Apply(path, updates, Array.Empty<(int, int)>());
    }

    public static byte[] BuildAppendSinglesCandidate(string path, IReadOnlyList<Bitmap> sources)
    {
        if (sources.Count == 0) throw new InvalidOperationException("没有待新增的 DLL 位图。");
        var records = DllBitmapReader.Read(path).OrderBy(item => item.Id).ThenBy(item => item.Language).ToArray();
        var reference = records.FirstOrDefault() ?? throw new InvalidDataException("DLL 没有可参考的 RT_BITMAP。");
        var nextId = records.Max(item => item.Id) + 1;
        var updates = sources.Select(source => new ResourceUpdate(nextId++, reference.Language,
            EncodeDib(source, reference.Width, reference.Height))).ToArray();
        return Apply(path, updates, Array.Empty<(int, int)>());
    }

    public static byte[] BuildAppendNativeSinglesCandidate(string path, IReadOnlyList<Bitmap> sources)
    {
        if (sources.Count == 0) throw new InvalidOperationException("没有待新增的 DLL 位图。");
        var records = DllBitmapReader.Read(path, requireAllBitmapRecords: true)
            .OrderBy(item => item.Id).ThenBy(item => item.Language).ToArray();
        var reference = records.FirstOrDefault() ?? throw new InvalidDataException("DLL 没有可参考的 RT_BITMAP。");
        var nextId = records.Max(item => item.Id) + 1;
        if ((long)nextId + sources.Count - 1 > ushort.MaxValue)
            throw new InvalidOperationException($"原生 RT_BITMAP ID 不能超过 {ushort.MaxValue}；下个 ID={nextId}，还能追加 {ushort.MaxValue - nextId + 1} 张。");
        var updates = sources.Select(source => new ResourceUpdate(nextId++, reference.Language,
            EncodeDib(source, source.Width, source.Height))).ToArray();
        return Apply(path, updates, Array.Empty<(int, int)>());
    }

    public static byte[] BuildDeleteNativeCandidate(string path, IReadOnlyCollection<int> resourceIdsToDelete)
    {
        if (resourceIdsToDelete.Count == 0) throw new InvalidOperationException("没有待删除的 RT_BITMAP ID。");
        var records = DllBitmapReader.Read(path, requireAllBitmapRecords: true);
        var allIds = records.Select(item => item.Id).Distinct().ToHashSet();
        if (resourceIdsToDelete.Any(id => !allIds.Contains(id))) throw new InvalidOperationException("待删除的 RT_BITMAP ID 不存在。");
        if (allIds.All(resourceIdsToDelete.Contains)) throw new InvalidOperationException("不能删除 DLL 的全部 RT_BITMAP。");
        var deletes = records.Where(item => resourceIdsToDelete.Contains(item.Id))
            .Select(item => (item.Id, item.Language)).ToArray();
        return Apply(path, Array.Empty<ResourceUpdate>(), deletes);
    }

    public static byte[] BuildReplacementCandidate(string path, IReadOnlyList<(int Id, int Language, Bitmap Bitmap, int Width, int Height)> replacements)
    {
        if (replacements.Count == 0) throw new InvalidOperationException("没有 DLL 替换项。");
        var updates = replacements.Select(item => new ResourceUpdate(item.Id, item.Language,
            EncodeDib(item.Bitmap, item.Width, item.Height))).ToArray();
        return Apply(path, updates, Array.Empty<(int, int)>());
    }

    public static byte[] BuildCompactCandidate(string path, IReadOnlyCollection<int> resourceIdsToDelete)
    {
        var records = DllBitmapReader.Read(path).OrderBy(item => item.Id).ThenBy(item => item.Language).ToArray();
        var allIds = records.Select(item => item.Id).Distinct().OrderBy(value => value).ToArray();
        if (resourceIdsToDelete.Any(id => !allIds.Contains(id))) throw new InvalidOperationException("待删除的 RT_BITMAP ID 不存在。");
        var remainingIds = allIds.Where(id => !resourceIdsToDelete.Contains(id)).ToArray();
        if (remainingIds.Length == 0) throw new InvalidOperationException("不能删除 DLL 的全部 RT_BITMAP。");
        var updates = new List<ResourceUpdate>();
        for (var index = 0; index < remainingIds.Length; index++)
        {
            var sourceId = remainingIds[index];
            var destinationId = allIds[index];
            foreach (var record in records.Where(item => item.Id == sourceId))
                updates.Add(new ResourceUpdate(destinationId, record.Language, record.Dib));
        }
        var deletes = allIds.Skip(remainingIds.Length)
            .SelectMany(id => records.Where(item => item.Id == id).Select(item => (item.Id, item.Language))).ToArray();
        return Apply(path, updates, deletes);
    }

    public static byte[] EncodeDib(Bitmap source, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new InvalidDataException("DLL 图标目标尺寸无效。");
        using var normalized = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(normalized))
        {
            graphics.Clear(Color.Blue);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = normalized.GetPixel(x, y);
            if (color.A < 128) color = Color.Blue;
            var offset = (y * width + x) * 4;
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
            pixels[offset + 3] = 255;
        }
        var dib = new byte[40 + pixels.Length];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8, 4), -height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14, 2), 32);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20, 4), pixels.Length);
        Buffer.BlockCopy(pixels, 0, dib, 40, pixels.Length);
        return dib;
    }

    private static byte[] Apply(string path, IReadOnlyList<ResourceUpdate> updates, IReadOnlyList<(int Id, int Language)> deletes)
    {
        var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ImageManager.Dll", Guid.NewGuid().ToString("N") + ".dll");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(temporary)!);
        File.Copy(path, temporary, true);
        var handle = BeginUpdateResourceW(temporary, false);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("BeginUpdateResource 失败，Win32Error=" + Marshal.GetLastWin32Error());
        var completed = false;
        try
        {
            foreach (var update in updates)
                if (!UpdateResourceW(handle, (IntPtr)RtBitmap, (IntPtr)update.Id, (ushort)update.Language, update.Dib, update.Dib.Length))
                    throw new InvalidOperationException($"更新 RT_BITMAP ID={update.Id} 失败，Win32Error={Marshal.GetLastWin32Error()}。");
            foreach (var item in deletes)
                if (!UpdateResourceW(handle, (IntPtr)RtBitmap, (IntPtr)item.Id, (ushort)item.Language, IntPtr.Zero, 0))
                    throw new InvalidOperationException($"删除 RT_BITMAP ID={item.Id} 失败，Win32Error={Marshal.GetLastWin32Error()}。");
            if (!EndUpdateResourceW(handle, false)) throw new InvalidOperationException("EndUpdateResource 失败，Win32Error=" + Marshal.GetLastWin32Error());
            completed = true;
            handle = IntPtr.Zero;
            _ = DllBitmapReader.Read(temporary);
            return File.ReadAllBytes(temporary);
        }
        finally
        {
            if (!completed && handle != IntPtr.Zero) EndUpdateResourceW(handle, true);
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private sealed record ResourceUpdate(int Id, int Language, byte[] Dib);

    [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResourceW(string fileName, [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);
    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResourceW(IntPtr handle, IntPtr type, IntPtr name, ushort language, byte[] data, int size);
    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResourceW(IntPtr handle, IntPtr type, IntPtr name, ushort language, IntPtr data, int size);
    [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResourceW(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
