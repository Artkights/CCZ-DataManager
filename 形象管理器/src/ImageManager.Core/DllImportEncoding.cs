using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ImageManager.Core;

/// <summary>Preserves verified RT_BITMAP storage during file import; does not change pixel-editor encoding.</summary>
internal static class DllImportEncoding
{
    internal sealed record Update(int Id, int Language, byte[] Dib);
    private sealed record Layout(int Width, int Height, int Bits, int PixelOffset, int Stride,
        bool TopFirst, IReadOnlyList<Color> Palette);

    internal static string Format(byte[] dib) => $"DIB{ReadLayout(dib).Bits}";

    internal static byte[] ResizeTemplate(byte[] template, Size size)
    {
        var layout = ReadLayout(template);
        if (size == new Size(layout.Width, layout.Height)) return (byte[])template.Clone();
        if (size.Width <= 0 || size.Height <= 0) throw new InvalidDataException("新增 DLL 画布尺寸无效。");
        var header = BinaryPrimitives.ReadInt32LittleEndian(template);
        if (header == 12 && (size.Width > ushort.MaxValue || size.Height > ushort.MaxValue))
            throw new InvalidDataException("CORE DIB 目标尺寸超过 16 位字段范围。");
        if (header == 124 && (BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(112)) != 0 ||
                             BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(116)) != 0))
            throw new InvalidDataException("带嵌入色彩配置的 V5 DIB 尚不支持调整画布尺寸。");
        var stride = checked((int)(((long)size.Width * layout.Bits + 31) / 32 * 4));
        var pixels = checked(stride * size.Height);
        var oldEnd = checked(layout.PixelOffset + layout.Stride * layout.Height);
        var result = new byte[checked(layout.PixelOffset + pixels + template.Length - oldEnd)];
        template.AsSpan(0, layout.PixelOffset).CopyTo(result);
        template.AsSpan(oldEnd).CopyTo(result.AsSpan(layout.PixelOffset + pixels));
        if (header == 12)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), (ushort)size.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), (ushort)size.Height);
        }
        else
        {
            var oldHeight = BinaryPrimitives.ReadInt32LittleEndian(template.AsSpan(8));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), size.Width);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), oldHeight < 0 ? -size.Height : size.Height);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20), pixels);
        }
        return result;
    }

    internal static Bitmap Decode(byte[] dib)
    {
        var layout = ReadLayout(dib);
        var bitmap = new Bitmap(layout.Width, layout.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < layout.Height; y++)
        for (var x = 0; x < layout.Width; x++)
            bitmap.SetPixel(x, y, ReadPixel(dib, layout, x, y));
        return bitmap;
    }

    internal static byte[] Encode(byte[] template, Bitmap bitmap, bool iconKeys, CancellationToken token)
    {
        var layout = ReadLayout(template);
        if (bitmap.Width != layout.Width || bitmap.Height != layout.Height)
            throw new InvalidDataException($"DIB 目标尺寸为 {layout.Width}×{layout.Height}，实际 {bitmap.Width}×{bitmap.Height}。");
        var result = (byte[])template.Clone();
        var colors = new Dictionary<int, byte>();
        for (var y = 0; y < layout.Height; y++)
        {
            if (y % 32 == 0) token.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var old = ReadPixel(template, layout, x, y);
                var transparent = color.A == 0;
                if (color.ToArgb() == old.ToArgb()) continue;
                var row = layout.PixelOffset + (layout.TopFirst ? y : layout.Height - 1 - y) * layout.Stride;
                if (layout.Bits == 8)
                {
                    if (transparent) { result[row + x] = 0; continue; }
                    var key = color.R << 16 | color.G << 8 | color.B;
                    if (!colors.TryGetValue(key, out var best))
                    {
                        var distance = int.MaxValue; best = 0;
                        var first = iconKeys && layout.Palette.Count > 1 ? 1 : 0;
                        for (var index = first; index < layout.Palette.Count; index++)
                        {
                            var candidate = layout.Palette[index];
                            var dr = color.R - candidate.R; var dg = color.G - candidate.G; var db = color.B - candidate.B;
                            var current = dr * dr + dg * dg + db * db;
                            if (current >= distance) continue;
                            distance = current; best = (byte)index;
                            if (current == 0) break;
                        }
                        colors[key] = best;
                    }
                    result[row + x] = best;
                }
                else
                {
                    var offset = row + x * (layout.Bits / 8);
                    if (transparent && layout.Bits == 24) color = Color.Blue;
                    result[offset] = color.B; result[offset + 1] = color.G; result[offset + 2] = color.R;
                    if (layout.Bits == 32) result[offset + 3] = color.A;
                }
            }
        }
        return result;
    }

    private static Color ReadPixel(byte[] bytes, Layout layout, int x, int y)
    {
        var row = layout.PixelOffset + (layout.TopFirst ? y : layout.Height - 1 - y) * layout.Stride;
        if (layout.Bits == 8)
        {
            var index = bytes[row + x];
            if (index >= layout.Palette.Count) throw new InvalidDataException("DIB 像素超出自身调色板。");
            return layout.Palette[index];
        }
        var at = row + x * (layout.Bits / 8);
        return Color.FromArgb(layout.Bits == 32 ? bytes[at + 3] : 255, bytes[at + 2], bytes[at + 1], bytes[at]);
    }

    private static Layout ReadLayout(byte[] dib)
    {
        if (dib.Length < 12) throw new InvalidDataException("DIB 信息头不完整。");
        var header = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var core = header == 12;
        if (!core && (header is not (40 or 52 or 56 or 108 or 124) || dib.Length < header))
            throw new InvalidDataException("仅支持经过验证的未压缩 8/24/32bpp DIB 信息头。");
        var width = core ? BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(4)) : BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
        var signedHeight = core ? BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(6)) : BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(core ? 8 : 12));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(core ? 10 : 14));
        if (width <= 0 || signedHeight == 0 || signedHeight == int.MinValue || planes != 1 || bits is not (8 or 24 or 32))
            throw new InvalidDataException("DIB 尺寸或色深无效；支持 8/24/32bpp。");
        if (!core && BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16)) != 0)
            throw new InvalidDataException("压缩或带位掩码的 DIB 尚未验证，未生成覆盖候选。");
        var height = Math.Abs(signedHeight);
        var paletteCount = bits == 8 ? 256 : 0;
        if (!core && bits == 8)
        {
            var used = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
            if (used > 256) throw new InvalidDataException("DIB 调色板项数无效。");
            if (used != 0) paletteCount = (int)used;
        }
        var paletteEntrySize = core ? 3 : 4;
        var offset = checked(header + paletteCount * paletteEntrySize);
        var stride = checked((int)(((long)width * bits + 31) / 32 * 4));
        if ((long)offset + (long)stride * height > dib.Length) throw new InvalidDataException("DIB 像素区被截断。");
        var palette = Enumerable.Range(0, paletteCount).Select(index =>
        {
            var at = header + index * paletteEntrySize;
            return Color.FromArgb(dib[at + 2], dib[at + 1], dib[at]);
        }).ToArray();
        // CCZ stores positive-height 32bpp resources top first; 8/24bpp follow ordinary DIB row order.
        return new Layout(width, height, bits, offset, stride, signedHeight < 0 || bits == 32, palette);
    }

    internal static byte[] BuildCandidate(string path, IReadOnlyList<Update> updates, CancellationToken token)
    {
        if (updates.Count == 0) throw new InvalidOperationException("没有待写入的 DLL 位图。");
        if (updates.GroupBy(item => (item.Id, item.Language)).Any(group => group.Count() > 1))
            throw new InvalidDataException("同一 DLL ID/LANG 被重复分配。");
        if (updates.Any(item => item.Id <= 0 || item.Id > ushort.MaxValue || item.Language < 0 || item.Language > ushort.MaxValue))
            throw new InvalidDataException("DLL ID/LANG 超出可写范围。");
        var original = DllBitmapReader.Read(path, true);
        var temporary = Path.Combine(Path.GetTempPath(), "ImageManager.FileImport", Guid.NewGuid().ToString("N"), Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        var handle = IntPtr.Zero;
        try
        {
            File.Copy(path, temporary);
            token.ThrowIfCancellationRequested();
            handle = BeginUpdateResourceW(temporary, false);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("无法创建 DLL 候选，Win32Error=" + Marshal.GetLastWin32Error());
            foreach (var update in updates)
            {
                token.ThrowIfCancellationRequested();
                if (!UpdateResourceW(handle, (IntPtr)2, (IntPtr)update.Id, (ushort)update.Language, update.Dib, update.Dib.Length))
                    throw new InvalidOperationException("DLL 位图候选写入失败，Win32Error=" + Marshal.GetLastWin32Error());
            }
            if (!EndUpdateResourceW(handle, false)) throw new InvalidOperationException("DLL 候选结束写入失败，Win32Error=" + Marshal.GetLastWin32Error());
            handle = IntPtr.Zero;
            token.ThrowIfCancellationRequested();
            var after = DllBitmapReader.Read(temporary, true).ToDictionary(item => (item.Id, item.Language));
            var changed = updates.ToDictionary(item => (item.Id, item.Language));
            foreach (var record in original)
            {
                if (changed.ContainsKey((record.Id, record.Language))) continue;
                if (!after.TryGetValue((record.Id, record.Language), out var current) || !record.Dib.AsSpan().SequenceEqual(current.Dib))
                    throw new InvalidDataException("DLL 候选改变了未选中的位图资源。");
            }
            foreach (var update in updates)
                if (!after.TryGetValue((update.Id, update.Language), out var record) || !update.Dib.AsSpan().SequenceEqual(record.Dib))
                    throw new InvalidDataException("DLL 候选复读与预览载荷不一致。");
            return File.ReadAllBytes(temporary);
        }
        finally
        {
            if (handle != IntPtr.Zero) EndUpdateResourceW(handle, true);
            try { Directory.Delete(Path.GetDirectoryName(temporary)!, true); } catch (IOException) { }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResourceW(string fileName, [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResourceW(IntPtr handle, IntPtr type, IntPtr name, ushort language, byte[] bytes, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResourceW(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
