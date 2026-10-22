using System.Drawing;

namespace ImageManager.Core;

/// <summary>Read-only access to native RT_BITMAP records for container conversion.</summary>
public sealed record DllBitmapEntry(int Id, int Language, int Width, int Height, byte[] Dib);

public static class DllBitmapArchive
{
    public static IReadOnlyList<DllBitmapEntry> Read(string path, bool requireAllBitmapRecords = false)
        => DllBitmapReader.Read(path, requireAllBitmapRecords).Select(item => new DllBitmapEntry(item.Id, item.Language, item.Width, item.Height, item.Dib)).ToArray();

    public static Bitmap Decode(DllBitmapEntry entry) => DllBitmapReader.DecodeDib(entry.Dib);
}
