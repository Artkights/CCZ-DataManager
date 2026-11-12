namespace ImageManager.Core;

/// <summary>Read-only playback decodes composed frames directly. Large packages retain a small moving
/// window rather than all source planes plus every composed frame. Export/editing still uses the full codec.</summary>
public static class StrategyPreviewPlayback
{
    public const long MaximumWindowBytes = 16L * 1024 * 1024;
    public const long CompleteAnimationThreshold = 8L * 1024 * 1024;
    public static Task<AnimationFrameWindow> OpenAsync(ProjectProfile project, byte[] package,
        CancellationToken token = default, object? latestOwner = null)
    {
        var info = StrategyAnimationPackageCodec.InspectPlayback(package, token);
        return DecodeWindowAsync(project, package, info, 0, token, latestOwner);
    }

    private static Task<AnimationFrameWindow> DecodeWindowAsync(ProjectProfile project, byte[] package,
        StrategyPlaybackInfo info, int start, CancellationToken token, object? latestOwner = null)
        => ImageResourceCoordinator.Shared.DecodeAsync(cancellation =>
        {
            var frameBytes = checked((long)info.Width * info.Height * 4);
            var full = frameBytes * info.FrameCount <= CompleteAnimationThreshold;
            var count = full ? info.FrameCount : Math.Max(1, (int)Math.Min(8, MaximumWindowBytes / Math.Max(1, frameBytes)));
            start = full ? 0 : Math.Clamp(start, 0, info.FrameCount - 1);
            count = Math.Min(count, info.FrameCount - start);
            var frames = new List<Bitmap>(count);
            try
            {
                var codec = new StrategyAnimationPackageCodec();
                for (var index = start; index < start + count; index++)
                { cancellation.ThrowIfCancellationRequested(); frames.Add(codec.DecodePreviewFrame(project, package, index, cancellation)); }
                return new AnimationFrameWindow(frames, start, info.FrameCount, info.IntervalMs,
                    full ? null : (next, nextToken) => DecodeWindowAsync(project, package, info, next, nextToken));
            }
            catch { foreach (var frame in frames) frame.Dispose(); throw; }
        }, result => result.Dispose(), token, latestOwner);
}

public sealed record AnimationFrameWindow(IReadOnlyList<Bitmap> Frames, int Start, int TotalFrames, int IntervalMs,
    Func<int, CancellationToken, Task<AnimationFrameWindow>>? LoadWindowAsync) : IDisposable
{
    public long PixelBytes => Frames.Sum(frame => (long)frame.Width * frame.Height * 4);
    public void Dispose() { foreach (var frame in Frames) frame.Dispose(); }
}
