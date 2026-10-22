namespace ImageManager.Core;

/// <summary>Compatibility fallback for codecs and catalog test doubles without a single-frame decoder.</summary>
internal static class PreviewFrameDecoding
{
    public static DecodedFrame? CopyMatching(Func<IReadOnlyList<DecodedFrame>> decode,
        FrameDescriptor descriptor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frames = decode();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames.FirstOrDefault(frame =>
                frame.Descriptor.SourcePath.Equals(descriptor.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                frame.Descriptor.ImageNumber == descriptor.ImageNumber &&
                frame.Descriptor.FrameIndex == descriptor.FrameIndex &&
                frame.Descriptor.Action == descriptor.Action &&
                frame.Descriptor.SourceOffset == descriptor.SourceOffset);
            return frame == null ? null : new DecodedFrame(frame.Descriptor, new Bitmap(frame.Bitmap),
                frame.PaletteIndexes?.ToArray(), frame.PaletteBgra?.ToArray());
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }
}
