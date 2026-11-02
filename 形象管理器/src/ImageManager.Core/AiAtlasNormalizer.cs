using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ImageManager.Core;

public sealed partial class AiAtlasNormalizer : IAiAtlasNormalizer
{
    private readonly IReferenceAtlasLayoutPlanner _layouts;

    public AiAtlasNormalizer(IReferenceAtlasLayoutPlanner? layouts = null)
        => _layouts = layouts ?? new ReferenceAtlasLayoutPlanner();

    public AiAtlasNormalizationResult Normalize(string pngPath, LogicalImageAsset asset,
        IReadOnlyList<DecodedFrame> referenceFrames, AiAtlasNormalizationOptions options)
        => Normalize(pngPath, asset, referenceFrames, options, CancellationToken.None);

    public AiAtlasNormalizationResult Normalize(string pngPath, LogicalImageAsset asset,
        IReadOnlyList<DecodedFrame> referenceFrames, AiAtlasNormalizationOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(pngPath);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(referenceFrames);
        ArgumentNullException.ThrowIfNull(options);
        ValidatePng(pngPath);
        if (asset.Kind is not (ResourceKind.RComposite or ResourceKind.SComposite))
            throw new InvalidOperationException("AI 参考图导回只适用于 R/S 逻辑形象。");

        var layout = _layouts.Plan(asset, referenceFrames);
        using var loaded = new Bitmap(pngPath);
        using var source = ToArgb(loaded);
        var pixels = ReadPixels(source, cancellationToken);
        var effective = ResolveEffectiveBounds(pixels, source.Size, options.SourceMapping?.EffectiveBounds);
        var background = ClassifyEffectiveBackground(pixels, source.Size, effective, options);
        var foreground = BuildEffectiveForeground(pixels, source.Size, effective, background, options, cancellationToken);
        var diagnostics = new List<string>
        {
            $"输入 {source.Width}x{source.Height}，标准画布 {layout.CanvasSize}x{layout.CanvasSize}，倍率 {source.Width / (double)layout.CanvasSize:0.######}x。",
            background.Kind switch
            {
                AtlasBackgroundKind.Transparent => "检测到有效 Alpha，直接使用透明通道。",
                AtlasBackgroundKind.KeyColor => $"检测到不透明键色背景 #{background.Color.R:X2}{background.Color.G:X2}{background.Color.B:X2}。",
                _ => $"检测到不透明背景 #{background.Color.R:X2}{background.Color.G:X2}{background.Color.B:X2}；背景恢复有损，必须人工检查。"
            }
        };

        var mapped = BuildFlexibleMapping(layout, foreground, source.Size, effective, options, diagnostics, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        diagnostics.Add(options.GeometryMode == FrameAlignmentMode.None
            ? "按固定或自动配准后的格位一次性还原；不自动按原人物前景大小二次缩放。"
            : "已显式启用原人物占位对齐，请检查人物比例变化。");
        var sourcePreview = new Bitmap(source);
        var maskPreview = BuildMaskPreview(pixels, foreground, source.Size);
        var sourceIsSmaller = source.Width < layout.CanvasSize;
        if (sourceIsSmaller)
            diagnostics.Add("输入画布小于标准画布，已使用最近邻放大；已经丢失的细节无法恢复。");
        var normalized = new List<DecodedFrame>(layout.Placements.Count);
        var rawFrames = new List<DecodedFrame>(layout.Placements.Count);
        var alignedFrames = new List<DecodedFrame>(layout.Placements.Count);
        var frameDiagnostics = new List<AiAtlasFrameDiagnostic>(layout.Placements.Count);
        var referenceGeometry = new Dictionary<string, FrameGeometryProfile>(StringComparer.Ordinal);
        var quantizationReports = new Dictionary<string, FrameColorQuantizationReport>(StringComparer.Ordinal);
        var paletteIndexes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var frameComparisons = new Dictionary<string, AiAtlasFrameComparison>(StringComparer.Ordinal);
        var originalAtlas = ComposeAtlas(layout, referenceFrames);
        try
        {
            foreach (var placement in layout.Placements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = FrameKey(placement.Frame.Descriptor);
                var sourceBounds = mapped.Rectangles[key];
                using var raw = RestoreMappedFrame(pixels, foreground, source.Size, sourceBounds,
                    placement.Bounds.Size, mapped, key, options);
                var referenceProfile = BuildGeometryProfile(placement.Frame.Bitmap, options.AlphaThreshold);
                referenceGeometry[key] = referenceProfile;
                var aiProfile = BuildGeometryProfile(pixels, foreground, source.Size, sourceBounds,
                    placement.Bounds.Size, options.AlphaThreshold);
                var alignment = new AlignmentMetrics(1, 0, false);
                var resized = options.GeometryMode == FrameAlignmentMode.AlignToReferenceOccupancy
                    ? AlignFrame(pixels, foreground, source.Size, sourceBounds, placement.Bounds.Size,
                        referenceProfile, aiProfile, sourceIsSmaller ? AtlasResamplingKernel.NearestNeighbor : options.Kernel,
                        out alignment)
                    : new Bitmap(raw);
                if (options.GeometryMode != FrameAlignmentMode.AlignToReferenceOccupancy)
                    alignment = new AlignmentMetrics(1, 0, false);
                var targetAdjustment = CombinedAdjustment(placement.Frame.Descriptor,
                    mapped.SectionKeys[key], options);
                if (targetAdjustment != AtlasFrameAdjustment.Default)
                {
                    using var unadjusted = resized;
                    resized = ApplyTargetAdjustment(unadjusted, targetAdjustment, options.Kernel);
                }
                rawFrames.Add(new DecodedFrame(placement.Frame.Descriptor, new Bitmap(raw)));
                alignedFrames.Add(new DecodedFrame(placement.Frame.Descriptor, new Bitmap(resized)));
                if (TryPalette(placement.Frame.PaletteBgra, out var framePalette))
                {
                    using var beforePalette = resized;
                    var quantized = QuantizeToPalette(beforePalette, framePalette, options.OrderedDither,
                        options.AlphaThreshold, cancellationToken);
                    resized = quantized.Bitmap;
                    quantizationReports[key] = quantized.Report;
                    paletteIndexes[key] = quantized.Indexes;
                }
                else paletteIndexes[key] = null;

                var coverage = ForegroundCoverage(alignedFrames[^1].Bitmap);
                var confidence = mapped.Confidences.TryGetValue(key, out var rowConfidence)
                    ? rowConfidence : 0.5;
                AtlasIssueSeverity severity;
                string message;
                if (coverage < 0.0005)
                {
                    severity = AtlasIssueSeverity.Error;
                    confidence = Math.Min(confidence, 0.15);
                    message = "该必需帧没有检测到前景；可能缺帧、错格或背景蒙版过强。";
                }
                else if (mapped.Issues.TryGetValue(key, out var mappingIssue))
                {
                    severity = AtlasIssueSeverity.Error;
                    confidence = Math.Min(confidence, 0.25);
                    message = mappingIssue;
                }
                else if (sourceIsSmaller || confidence < 0.55 || TouchesEdge(alignedFrames[^1].Bitmap))
                {
                    severity = AtlasIssueSeverity.Warning;
                    message = sourceIsSmaller
                        ? "源图小于标准格位，最近邻放大无法恢复已经丢失的细节。"
                        : TouchesEdge(alignedFrames[^1].Bitmap)
                        ? "前景接触帧边界，写回前请检查是否被裁切或跨格。"
                        : "该帧使用低置信度配准结果，写回前请检查位置与动作。";
                }
                else
                {
                    severity = AtlasIssueSeverity.Information;
                    message = "帧尺寸和语义格位有效。";
                }

                normalized.Add(new DecodedFrame(placement.Frame.Descriptor, resized,
                    paletteIndexes[key], placement.Frame.PaletteBgra?.ToArray()));
                frameComparisons[key] = new AiAtlasFrameComparison
                {
                    Descriptor = placement.Frame.Descriptor,
                    ReferenceFrame = new Bitmap(placement.Frame.Bitmap),
                    RawAiFrame = new Bitmap(raw),
                    AlignedAiFrame = new Bitmap(alignedFrames[^1].Bitmap),
                    FinalCczFrame = new Bitmap(resized)
                };
                var alignedBounds = FindForegroundBounds(alignedFrames[^1].Bitmap, options.AlphaThreshold, excludeKeyColor: false);
                var baselineOffset = alignment.BaselineOffset;
                var quantizationReport = quantizationReports.GetValueOrDefault(key);
                var silhouetteIou = ComputeSilhouetteIoU(referenceProfile.ForegroundBounds, alignedBounds);
                var aiColorError = ComputeColorError(placement.Frame.Bitmap, alignedFrames[^1].Bitmap,
                    options.AlphaThreshold);
                var warning = severity == AtlasIssueSeverity.Warning ||
                              background.Kind == AtlasBackgroundKind.OpaqueColor ||
                              alignment.IsClipped || Math.Abs(alignment.Scale - 1) > .35;
                frameDiagnostics.Add(new AiAtlasFrameDiagnostic(key, placement.Frame.Descriptor,
                    sourceBounds, placement.Bounds, confidence, severity, message)
                {
                    ReferenceForegroundBounds = referenceProfile.ForegroundBounds,
                    AiForegroundBounds = aiProfile.ForegroundBounds,
                    AlignedForegroundBounds = alignedBounds,
                    Scale = alignment.Scale,
                    BaselineOffset = baselineOffset,
                    ForegroundAreaRatio = aiProfile.Occupancy - referenceProfile.Occupancy,
                    SilhouetteIoU = silhouetteIou,
                    ColorError = quantizationReport?.AverageColorError ?? 0,
                    AiColorError = aiColorError,
                    KeyPixelCount = quantizationReport?.KeyPixelCount ?? 0,
                    NonKeyPixelCount = quantizationReport?.NonKeyPixelCount ?? 0,
                    IsClipped = alignment.IsClipped || TouchesEdge(alignedFrames[^1].Bitmap),
                    RequiresManualReview = background.Kind == AtlasBackgroundKind.OpaqueColor ||
                                           alignment.IsClipped || confidence < .7 || warning
                });
            }

            ValidateReservedCells(layout, foreground, source.Size, mapped, frameDiagnostics);
            var contentWarnings = frameDiagnostics.Count(item => item.Severity == AtlasIssueSeverity.Warning);
            diagnostics.Add($"已对 {frameDiagnostics.Count} 个帧独立建立前景几何；其中 {contentWarnings} 个帧需要人工比较 AI 内容、占位或裁切。" );
            var rawAtlas = ComposeAtlas(layout, rawFrames);
            var alignedAtlas = ComposeAtlas(layout, alignedFrames);
            var normalizedAtlas = ComposeAtlas(layout, normalized);
            foreach (var frame in rawFrames) frame.Dispose();
            foreach (var frame in alignedFrames) frame.Dispose();
            return new AiAtlasNormalizationResult
            {
                SourcePath = Path.GetFullPath(pngPath),
                SourceSize = source.Size,
                SourceMapping = ToPublicMapping(mapped, effective, options),
                TargetCanvasSize = layout.CanvasSize,
                BackgroundKind = background.Kind,
                BackgroundColor = background.Color,
                Frames = normalized,
                FrameDiagnostics = frameDiagnostics,
                Diagnostics = diagnostics,
                SourcePreview = sourcePreview,
                MaskPreview = maskPreview,
                NormalizedAtlas = normalizedAtlas,
                OriginalAtlasPreview = originalAtlas,
                RawAiAtlasPreview = rawAtlas,
                AlignedAiAtlasPreview = alignedAtlas,
                FinalCczAtlasPreview = new Bitmap(normalizedAtlas),
                ReferenceGeometry = referenceGeometry,
                QuantizationReports = quantizationReports,
                FrameComparisons = frameComparisons
            };
        }
        catch
        {
            foreach (var frame in normalized) frame.Dispose();
            foreach (var frame in rawFrames) frame.Dispose();
            foreach (var frame in alignedFrames) frame.Dispose();
            foreach (var comparison in frameComparisons.Values) comparison.Dispose();
            originalAtlas.Dispose();
            sourcePreview.Dispose();
            maskPreview.Dispose();
            throw;
        }
    }

    private static void ValidatePng(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 AI 参考图。", path);
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("AI 参考图只接受无损 PNG。");
        Span<byte> signature = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        if (stream.Read(signature) != signature.Length ||
            !signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("文件扩展名为 PNG，但文件头不是有效 PNG。");
    }

    private static Bitmap ToArgb(Bitmap source)
    {
        // Clone in pixel coordinates: DrawImageUnscaled still considers image/device
        // DPI in a PerMonitorV2 host and can resample the one-pixel outside frame.
        return source.Clone(new Rectangle(Point.Empty, source.Size), PixelFormat.Format32bppArgb);
    }

    private static int[] ReadPixels(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        var pixels = new int[checked(bitmap.Width * bitmap.Height)];
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width, bitmap.Width);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return pixels;
    }

    private static Color DominantBorderColor(IReadOnlyList<Color> colors)
    {
        if (colors.Count == 0) return Color.Black;
        var group = colors.GroupBy(color => (color.R / 8, color.G / 8, color.B / 8))
            .OrderByDescending(item => item.Count()).First().ToArray();
        return Color.FromArgb(255, (int)group.Average(color => color.R),
            (int)group.Average(color => color.G), (int)group.Average(color => color.B));
    }

    private static Bitmap BuildMaskPreview(int[] pixels, bool[] foreground, Size size)
    {
        var output = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        var data = output.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[size.Width];
            for (var y = 0; y < size.Height; y++)
            {
                for (var x = 0; x < size.Width; x++)
                {
                    var index = y * size.Width + x;
                    row[x] = foreground[index] ? pixels[index] | unchecked((int)0xFF000000) : 0;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { output.UnlockBits(data); }
        return output;
    }

    private static DetectedRow[]? DetectRows(bool[] foreground, Size size, Rectangle search, int expected)
    {
        var projection = new double[search.Height];
        for (var y = search.Top; y < search.Bottom; y++)
        {
            var count = 0;
            for (var x = search.Left; x < search.Right; x++)
                if (foreground[y * size.Width + x]) count++;
            projection[y - search.Top] = count;
        }
        projection = Smooth(projection, Math.Max(1, size.Height / 350));
        var maximum = projection.Length == 0 ? 0 : projection.Max();
        if (maximum <= 0) return null;
        var activeThreshold = Math.Max(1, maximum * .025);
        var bands = new List<(int Start, int End, double Weight)>();
        var start = -1;
        for (var index = 0; index <= projection.Length; index++)
        {
            var active = index < projection.Length && projection[index] >= activeThreshold;
            if (active && start < 0) start = index;
            if (!active && start >= 0)
            {
                if (index - start >= Math.Max(2, size.Height / 500))
                    bands.Add((start + search.Top, index - 1 + search.Top,
                        projection.AsSpan(start, index - start).ToArray().Sum()));
                start = -1;
            }
        }
        bands = MergeNearbyBands(bands, Math.Max(1, size.Height / 500)).ToList();
        if (bands.Count < expected) return null;
        if (bands.Count > expected)
            bands = SelectOrderedBands(bands, expected, search.Height / (double)expected);
        if (bands.Count != expected) return null;

        var centers = bands.Select(band => (band.Start + band.End) / 2d).ToArray();
        var boundaries = new int[expected + 1];
        for (var index = 1; index < expected; index++)
            boundaries[index] = (int)Math.Round((centers[index - 1] + centers[index]) / 2);
        boundaries[0] = Math.Max(search.Top,
            (int)Math.Round(centers[0] - (boundaries[1] - centers[0])));
        boundaries[^1] = Math.Min(search.Bottom,
            (int)Math.Round(centers[^1] + (centers[^1] - boundaries[^2])));
        if (expected == 1) { boundaries[0] = search.Top; boundaries[1] = search.Bottom; }
        var rows = new DetectedRow[expected];
        for (var index = 0; index < expected; index++)
        {
            var top = Math.Clamp(boundaries[index], search.Top, search.Bottom - 1);
            var bottom = Math.Clamp(boundaries[index + 1], top + 1, search.Bottom);
            var centerError = Math.Abs((top + bottom) / 2d - centers[index]) / Math.Max(1, bottom - top);
            rows[index] = new DetectedRow(top, bottom - top, Math.Clamp(.95 - centerError, .35, .95));
        }
        return rows;
    }

    private static double[] Smooth(double[] values, int radius)
    {
        if (radius <= 0) return values;
        var output = new double[values.Length];
        var sum = 0d;
        for (var index = 0; index < values.Length; index++)
        {
            sum += values[index];
            if (index - radius - 1 >= 0) sum -= values[index - radius - 1];
            var left = Math.Max(0, index - radius);
            output[index] = sum / (index - left + 1);
        }
        return output;
    }

    private static IEnumerable<(int Start, int End, double Weight)> MergeNearbyBands(
        IReadOnlyList<(int Start, int End, double Weight)> bands, int maximumGap)
    {
        if (bands.Count == 0) yield break;
        var current = bands[0];
        for (var index = 1; index < bands.Count; index++)
        {
            var next = bands[index];
            if (next.Start - current.End - 1 <= maximumGap)
                current = (current.Start, next.End, current.Weight + next.Weight);
            else { yield return current; current = next; }
        }
        yield return current;
    }

    private static List<(int Start, int End, double Weight)> SelectOrderedBands(
        IReadOnlyList<(int Start, int End, double Weight)> bands, int count, double expectedSpacing)
    {
        // Dynamic programming avoids exponential subset search on noisy AI backgrounds.
        var scores = new double[count, bands.Count];
        var previous = new int[count, bands.Count];
        for (var row = 0; row < count; row++)
        for (var index = 0; index < bands.Count; index++)
        {
            scores[row, index] = double.NegativeInfinity;
            previous[row, index] = -1;
            var weight = Math.Log10(bands[index].Weight + 1);
            if (row == 0) { scores[row, index] = weight; continue; }
            for (var prior = row - 1; prior < index; prior++)
            {
                var distance = (bands[index].Start + bands[index].End - bands[prior].Start - bands[prior].End) / 2d;
                var score = scores[row - 1, prior] + weight - Math.Abs(distance - expectedSpacing) / Math.Max(1, expectedSpacing) * 3;
                if (score <= scores[row, index]) continue;
                scores[row, index] = score;
                previous[row, index] = prior;
            }
        }
        var last = Enumerable.Range(0, bands.Count).MaxBy(index => scores[count - 1, index]);
        var selected = new List<(int Start, int End, double Weight)>();
        for (var row = count - 1; row >= 0; row--) { selected.Add(bands[last]); last = previous[row, last]; }
        selected.Reverse();
        return selected;
    }

    private static Rectangle Clamp(Rectangle rectangle, Size size)
    {
        var left = Math.Clamp(rectangle.Left, 0, Math.Max(0, size.Width - 1));
        var top = Math.Clamp(rectangle.Top, 0, Math.Max(0, size.Height - 1));
        var right = Math.Clamp(rectangle.Right, left + 1, size.Width);
        var bottom = Math.Clamp(rectangle.Bottom, top + 1, size.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static AtlasFrameAdjustment CombinedAdjustment(FrameDescriptor descriptor, string sectionKey,
        AiAtlasNormalizationOptions options)
    {
        var values = new List<AtlasFrameAdjustment>();
        if (options.Adjustments.TryGetValue("all", out var all)) values.Add(all);
        if (options.Adjustments.TryGetValue(sectionKey, out var section)) values.Add(section);
        if (options.Adjustments.TryGetValue("frame:" + FrameKey(descriptor), out var frame)) values.Add(frame);
        if (values.Count == 0) return AtlasFrameAdjustment.Default;
        var scale = 100d;
        foreach (var value in values) scale *= value.ScalePercent / 100d;
        return new AtlasFrameAdjustment(values.Sum(value => value.OffsetX), values.Sum(value => value.OffsetY),
            Math.Clamp((int)Math.Round(scale), 25, 400));
    }

    private static FrameGeometryProfile BuildGeometryProfile(Bitmap bitmap, int alphaThreshold)
    {
        var pixels = ReadPixels(bitmap);
        var bounds = FindForegroundBounds(pixels, bitmap.Size, alphaThreshold);
        var foregroundPixels = pixels.Count(pixel =>
            Color.FromArgb(pixel).A >= alphaThreshold && !IsKeyColor(Color.FromArgb(pixel)));
        var occupancy = foregroundPixels / (double)Math.Max(1, bitmap.Width * bitmap.Height);
        return new FrameGeometryProfile(bounds,
            bounds.IsEmpty ? new PointF(bitmap.Width / 2f, bitmap.Height / 2f) :
                new PointF(bounds.Left + bounds.Width / 2f, bounds.Top + bounds.Height / 2f),
            bounds.IsEmpty ? bitmap.Height : bounds.Bottom,
            occupancy, false,
            new Rectangle(1, 1, Math.Max(1, bitmap.Width - 2), Math.Max(1, bitmap.Height - 2)));
    }

    private static FrameGeometryProfile BuildGeometryProfile(int[] pixels, bool[] foreground,
        Size sourceSize, Rectangle sourceBounds, Size targetSize, int alphaThreshold)
    {
        var bounds = FindForegroundBounds(pixels, foreground, sourceSize, sourceBounds);
        var local = bounds.IsEmpty ? Rectangle.Empty :
            new Rectangle(bounds.X - sourceBounds.X, bounds.Y - sourceBounds.Y, bounds.Width, bounds.Height);
        var foregroundPixels = 0;
        for (var y = sourceBounds.Top; y < sourceBounds.Bottom; y++)
        for (var x = sourceBounds.Left; x < sourceBounds.Right; x++)
        {
            var index = y * sourceSize.Width + x;
            if (foreground[index] && Color.FromArgb(pixels[index]).A >= 16) foregroundPixels++;
        }
        var occupancy = foregroundPixels / (double)Math.Max(1, targetSize.Width * targetSize.Height);
        return new FrameGeometryProfile(local,
            local.IsEmpty ? new PointF(targetSize.Width / 2f, targetSize.Height / 2f) :
                new PointF(local.Left + local.Width / 2f, local.Top + local.Height / 2f),
            local.IsEmpty ? targetSize.Height : local.Bottom,
            occupancy, false,
            new Rectangle(1, 1, Math.Max(1, targetSize.Width - 2), Math.Max(1, targetSize.Height - 2)));
    }

    private static Rectangle FindForegroundBounds(Bitmap bitmap, int alphaThreshold, bool excludeKeyColor = true)
        => FindForegroundBounds(ReadPixels(bitmap), bitmap.Size, alphaThreshold, excludeKeyColor);

    private static Rectangle FindForegroundBounds(int[] pixels, Size size, int alphaThreshold, bool excludeKeyColor = true)
    {
        var left = size.Width; var top = size.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < size.Height; y++)
        for (var x = 0; x < size.Width; x++)
        {
            var color = Color.FromArgb(pixels[y * size.Width + x]);
            if (color.A < alphaThreshold || excludeKeyColor && IsKeyColor(color)) continue;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static Rectangle FindForegroundBounds(int[] pixels, bool[] foreground, Size sourceSize, Rectangle source)
    {
        var left = source.Right; var top = source.Bottom; var right = source.Left - 1; var bottom = source.Top - 1;
        for (var y = source.Top; y < source.Bottom; y++)
        for (var x = source.Left; x < source.Right; x++)
        {
            var index = y * sourceSize.Width + x;
            var color = Color.FromArgb(pixels[index]);
            if (!foreground[index] || color.A < 16) continue;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static Bitmap AlignFrame(int[] pixels, bool[] foreground, Size sourceSize, Rectangle sourceBounds,
        Size targetSize, FrameGeometryProfile reference, FrameGeometryProfile ai, AtlasResamplingKernel kernel,
        out AlignmentMetrics metrics)
    {
        if (reference.ForegroundBounds.IsEmpty || ai.ForegroundBounds.IsEmpty)
        {
            metrics = new AlignmentMetrics(1, 0, false);
            return Resample(pixels, foreground, sourceSize, sourceBounds, targetSize, kernel);
        }

        var sourceForeground = new Rectangle(sourceBounds.X + ai.ForegroundBounds.X,
            sourceBounds.Y + ai.ForegroundBounds.Y, ai.ForegroundBounds.Width, ai.ForegroundBounds.Height);
        var scale = Math.Min(reference.ForegroundBounds.Width / (double)Math.Max(1, ai.ForegroundBounds.Width),
            reference.ForegroundBounds.Height / (double)Math.Max(1, ai.ForegroundBounds.Height));
        scale = Math.Clamp(scale, .15, 8);
        var desired = new Size(Math.Max(1, (int)Math.Round(sourceForeground.Width * scale)),
            Math.Max(1, (int)Math.Round(sourceForeground.Height * scale)));
        using var content = Resample(pixels, foreground, sourceSize, sourceForeground, desired, kernel);
        var outputPixels = new int[targetSize.Width * targetSize.Height];
        var contentPixels = ReadPixels(content);
        var targetX = reference.ForegroundBounds.Left +
                      (reference.ForegroundBounds.Width - desired.Width) / 2;
        var targetY = reference.ForegroundBounds.Bottom - desired.Height;
        var clipped = targetX < 0 || targetY < 0 || targetX + desired.Width > targetSize.Width ||
                      targetY + desired.Height > targetSize.Height;
        for (var y = 0; y < desired.Height; y++)
        for (var x = 0; x < desired.Width; x++)
        {
            var dx = targetX + x; var dy = targetY + y;
            if ((uint)dx >= (uint)targetSize.Width || (uint)dy >= (uint)targetSize.Height) continue;
            outputPixels[dy * targetSize.Width + dx] = contentPixels[y * desired.Width + x];
        }
        metrics = new AlignmentMetrics(scale,
            (ai.Baseline * scale) - reference.Baseline, clipped);
        return BitmapFromPixels(outputPixels, targetSize);
    }

    private static double ComputeSilhouetteIoU(Rectangle reference, Rectangle actual)
    {
        if (reference.IsEmpty || actual.IsEmpty) return 0;
        var intersection = Rectangle.Intersect(reference, actual);
        if (intersection.IsEmpty) return 0;
        var area = reference.Width * reference.Height + actual.Width * actual.Height -
                   intersection.Width * intersection.Height;
        return area <= 0 ? 0 : intersection.Width * intersection.Height / (double)area;
    }

    private static double ComputeColorError(Bitmap reference, Bitmap actual, int alphaThreshold)
    {
        if (reference.Size != actual.Size) return 100;
        double total = 0; var count = 0;
        for (var y = 0; y < reference.Height; y++)
        for (var x = 0; x < reference.Width; x++)
        {
            var left = reference.GetPixel(x, y);
            var right = actual.GetPixel(x, y);
            var leftVisible = left.A >= alphaThreshold && !IsKeyColor(left);
            var rightVisible = right.A >= alphaThreshold;
            if (!leftVisible && !rightVisible) continue;
            if (leftVisible != rightVisible) { total += 40; count++; continue; }
            total += LabDistance(ToLab(left), ToLab(right));
            count++;
        }
        return count == 0 ? 0 : total / count;
    }

    private static bool IsKeyColor(Color color)
        => color.A > 0 && ((color.R is 247 or 255 && color.G <= 8 && color.B >= 245) ||
                           (color.R <= 8 && color.G <= 8 && color.B >= 245));

    private static Bitmap Resample(int[] pixels, bool[] foreground, Size sourceSize, Rectangle source,
        Size target, AtlasResamplingKernel kernel)
        => kernel switch
        {
            AtlasResamplingKernel.NearestNeighbor => ResampleNearest(pixels, foreground, sourceSize, source, target),
            AtlasResamplingKernel.Lanczos3 => ResampleLanczos(pixels, foreground, sourceSize, source, target),
            _ => ResampleArea(pixels, foreground, sourceSize, source, target)
        };

    private static Bitmap ResampleNearest(int[] pixels, bool[] foreground, Size sourceSize, Rectangle source, Size target)
    {
        var output = new int[target.Width * target.Height];
        for (var y = 0; y < target.Height; y++)
        for (var x = 0; x < target.Width; x++)
        {
            var sx = Math.Clamp(source.Left + (int)((x + .5) * source.Width / target.Width), source.Left, source.Right - 1);
            var sy = Math.Clamp(source.Top + (int)((y + .5) * source.Height / target.Height), source.Top, source.Bottom - 1);
            var index = sy * sourceSize.Width + sx;
            output[y * target.Width + x] = foreground[index] ? pixels[index] | unchecked((int)0xFF000000) : 0;
        }
        return BitmapFromPixels(output, target);
    }

    private static Bitmap ResampleArea(int[] pixels, bool[] foreground, Size sourceSize, Rectangle source, Size target)
    {
        if (source.Size == target)
        {
            var direct = new int[target.Width * target.Height];
            for (var y = 0; y < target.Height; y++)
            for (var x = 0; x < target.Width; x++)
            {
                var index = (source.Top + y) * sourceSize.Width + source.Left + x;
                direct[y * target.Width + x] = foreground[index] ? pixels[index] | unchecked((int)0xFF000000) : 0;
            }
            return BitmapFromPixels(direct, target);
        }
        var output = new int[target.Width * target.Height];
        var scaleX = source.Width / (double)target.Width;
        var scaleY = source.Height / (double)target.Height;
        for (var dy = 0; dy < target.Height; dy++)
        for (var dx = 0; dx < target.Width; dx++)
        {
            var left = source.Left + dx * scaleX;
            var right = source.Left + (dx + 1) * scaleX;
            var top = source.Top + dy * scaleY;
            var bottom = source.Top + (dy + 1) * scaleY;
            double sumA = 0, sumR = 0, sumG = 0, sumB = 0, sumWeight = 0;
            for (var sy = (int)Math.Floor(top); sy < Math.Ceiling(bottom); sy++)
            for (var sx = (int)Math.Floor(left); sx < Math.Ceiling(right); sx++)
            {
                if (sx < source.Left || sx >= source.Right || sy < source.Top || sy >= source.Bottom) continue;
                var weight = Math.Max(0, Math.Min(right, sx + 1) - Math.Max(left, sx)) *
                             Math.Max(0, Math.Min(bottom, sy + 1) - Math.Max(top, sy));
                if (weight <= 0) continue;
                sumWeight += weight;
                var index = sy * sourceSize.Width + sx;
                if (!foreground[index]) continue;
                var color = Color.FromArgb(pixels[index]);
                var alpha = color.A / 255d;
                sumA += alpha * weight;
                sumR += color.R * alpha * weight;
                sumG += color.G * alpha * weight;
                sumB += color.B * alpha * weight;
            }
            output[dy * target.Width + dx] = PremultipliedColor(sumA, sumR, sumG, sumB, sumWeight);
        }
        return BitmapFromPixels(output, target);
    }

    private static Bitmap ResampleLanczos(int[] pixels, bool[] foreground, Size sourceSize, Rectangle source, Size target)
    {
        var output = new int[target.Width * target.Height];
        var scaleX = source.Width / (double)target.Width;
        var scaleY = source.Height / (double)target.Height;
        var supportX = Math.Max(3, 3 * scaleX);
        var supportY = Math.Max(3, 3 * scaleY);
        for (var dy = 0; dy < target.Height; dy++)
        for (var dx = 0; dx < target.Width; dx++)
        {
            var centerX = source.Left + (dx + .5) * scaleX - .5;
            var centerY = source.Top + (dy + .5) * scaleY - .5;
            double sumA = 0, sumR = 0, sumG = 0, sumB = 0, sumWeight = 0;
            for (var sy = Math.Max(source.Top, (int)Math.Floor(centerY - supportY));
                 sy <= Math.Min(source.Bottom - 1, Math.Ceiling(centerY + supportY)); sy++)
            for (var sx = Math.Max(source.Left, (int)Math.Floor(centerX - supportX));
                 sx <= Math.Min(source.Right - 1, Math.Ceiling(centerX + supportX)); sx++)
            {
                var weight = Lanczos((sx - centerX) / Math.Max(1, scaleX), 3) *
                             Lanczos((sy - centerY) / Math.Max(1, scaleY), 3);
                if (weight <= 0) continue;
                sumWeight += weight;
                var index = sy * sourceSize.Width + sx;
                if (!foreground[index]) continue;
                var color = Color.FromArgb(pixels[index]);
                var alpha = color.A / 255d;
                sumA += alpha * weight;
                sumR += color.R * alpha * weight;
                sumG += color.G * alpha * weight;
                sumB += color.B * alpha * weight;
            }
            output[dy * target.Width + dx] = PremultipliedColor(sumA, sumR, sumG, sumB, sumWeight);
        }
        return BitmapFromPixels(output, target);
    }

    private static double Lanczos(double value, int a)
    {
        value = Math.Abs(value);
        if (value < 1e-9) return 1;
        if (value >= a) return 0;
        var pi = Math.PI * value;
        return a * Math.Sin(pi) * Math.Sin(pi / a) / (pi * pi);
    }

    private static int PremultipliedColor(double sumA, double sumR, double sumG, double sumB, double weight)
    {
        if (weight <= 0 || sumA <= 1e-8) return 0;
        var alpha = Math.Clamp((int)Math.Round(sumA / weight * 255), 0, 255);
        var red = Math.Clamp((int)Math.Round(sumR / sumA), 0, 255);
        var green = Math.Clamp((int)Math.Round(sumG / sumA), 0, 255);
        var blue = Math.Clamp((int)Math.Round(sumB / sumA), 0, 255);
        return Color.FromArgb(alpha, red, green, blue).ToArgb();
    }

    private static Bitmap BitmapFromPixels(int[] pixels, Size size)
    {
        var output = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        var data = output.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < size.Height; y++)
                Marshal.Copy(pixels, y * size.Width, data.Scan0 + y * data.Stride, size.Width);
        }
        finally { output.UnlockBits(data); }
        return output;
    }

    private static Bitmap ApplyTargetAdjustment(Bitmap source, AtlasFrameAdjustment adjustment,
        AtlasResamplingKernel kernel)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * adjustment.ScalePercent / 100d));
        var height = Math.Max(1, (int)Math.Round(source.Height * adjustment.ScalePercent / 100d));
        var x = (source.Width - width) / 2 + adjustment.OffsetX;
        var y = (source.Height - height) / 2 + adjustment.OffsetY;
        var sourcePixels = ReadPixels(source);
        var mask = sourcePixels.Select(pixel => (uint)pixel >> 24 >= 16).ToArray();
        using var scaled = Resample(sourcePixels, mask, source.Size, new Rectangle(Point.Empty, source.Size),
            new Size(width, height), kernel);
        var outputPixels = new int[source.Width * source.Height];
        var scaledPixels = ReadPixels(scaled);
        for (var sy = 0; sy < height; sy++)
        for (var sx = 0; sx < width; sx++)
        {
            var dx = x + sx; var dy = y + sy;
            if ((uint)dx >= (uint)source.Width || (uint)dy >= (uint)source.Height) continue;
            outputPixels[dy * source.Width + dx] = scaledPixels[sy * width + sx];
        }
        var output = BitmapFromPixels(outputPixels, source.Size);
        return output;
    }

    private static bool TryPalette(byte[]? bgra, out Color[] palette)
    {
        palette = Array.Empty<Color>();
        if (bgra == null || bgra.Length < 1024) return false;
        var colors = new Color[256];
        for (var index = 0; index < colors.Length; index++)
        {
            var offset = index * 4;
            colors[index] = Color.FromArgb(255, bgra[offset + 2], bgra[offset + 1], bgra[offset]);
        }
        if (colors.Select(color => color.ToArgb()).Distinct().Count() < 8) return false;
        palette = colors;
        return true;
    }

    private static QuantizedImage QuantizeToPalette(Bitmap source, IReadOnlyList<Color> palette,
        bool orderedDither, int alphaThreshold, CancellationToken cancellationToken)
    {
        int? keyIndex = null;
        for (var index = 0; index < palette.Count; index++)
            if (palette[index].G == 0 && palette[index].B == 255 && palette[index].R is 247 or 255)
            { keyIndex = index; break; }
        int[,] bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
        keyIndex ??= 0;
        var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var indexes = new byte[checked(source.Width * source.Height)];
        var paletteLab = palette.Select(ToLab).ToArray();
        var used = new HashSet<int>();
        double totalError = 0; double maxError = 0; var nonKey = 0; var keyPixels = 0;
        try
        {
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            if (x == 0) cancellationToken.ThrowIfCancellationRequested();
            var color = source.GetPixel(x, y);
            var offset = y * source.Width + x;
            if (color.A < alphaThreshold)
            {
                indexes[offset] = (byte)keyIndex.Value;
                output.SetPixel(x, y, palette[keyIndex.Value]);
                used.Add(keyIndex.Value); keyPixels++;
                continue;
            }
            var delta = orderedDither ? (bayer[y & 3, x & 3] - 7.5) * 2 : 0;
            var red = Math.Clamp((int)Math.Round(color.R + delta), 0, 255);
            var green = Math.Clamp((int)Math.Round(color.G + delta), 0, 255);
            var blue = Math.Clamp((int)Math.Round(color.B + delta), 0, 255);
            var sourceLab = ToLab(Color.FromArgb(255, red, green, blue));
            var best = 0;
            var distance = double.MaxValue;
            for (var index = 0; index < palette.Count; index++)
            {
                if (index == keyIndex.Value) continue;
                var current = LabDistance(sourceLab, paletteLab[index]);
                if (current >= distance) continue;
                distance = current;
                best = index;
                if (current == 0) break;
            }
            indexes[offset] = (byte)best;
            output.SetPixel(x, y, palette[best]);
            used.Add(best); nonKey++;
            var error = LabDistance(sourceLab, paletteLab[best]);
            totalError += error; maxError = Math.Max(maxError, error);
        }
        return new QuantizedImage(output, indexes, new FrameColorQuantizationReport(keyPixels, nonKey,
            used.Count, nonKey == 0 ? 0 : totalError / nonKey, maxError));
        }
        catch { output.Dispose(); throw; }
    }

    private static double ForegroundCoverage(Bitmap bitmap)
    {
        var pixels = ReadPixels(bitmap);
        return pixels.Count(pixel => (uint)pixel >> 24 >= 16) /
               (double)pixels.Length;
    }

    private static bool TouchesEdge(Bitmap bitmap)
    {
        var pixels = ReadPixels(bitmap);
        for (var x = 0; x < bitmap.Width; x++)
            if ((uint)pixels[x] >> 24 >= 16 ||
                (uint)pixels[(bitmap.Height - 1) * bitmap.Width + x] >> 24 >= 16) return true;
        for (var y = 0; y < bitmap.Height; y++)
            if ((uint)pixels[y * bitmap.Width] >> 24 >= 16 ||
                (uint)pixels[y * bitmap.Width + bitmap.Width - 1] >> 24 >= 16) return true;
        return false;
    }

    private static void ValidateReservedCells(ReferenceAtlasLayoutPlan layout, bool[] foreground, Size sourceSize,
        SourceMapping mapping, List<AiAtlasFrameDiagnostic> diagnostics)
    {
        foreach (var reserved in mapping.ReservedRectangles)
        {
            var count = 0;
            for (var y = reserved.Bounds.Top; y < reserved.Bounds.Bottom; y++)
            for (var x = reserved.Bounds.Left; x < reserved.Bounds.Right; x++)
                if (foreground[y * sourceSize.Width + x]) count++;
            var ratio = count / (double)(reserved.Bounds.Width * reserved.Bounds.Height);
            if (ratio < .003) continue;
            var related = layout.Placements.First(item => item.Frame.Descriptor.Turn == reserved.Turn &&
                item.Frame.Descriptor.Action != "atk").Frame.Descriptor;
            diagnostics.Add(new AiAtlasFrameDiagnostic($"reserved:{reserved.Turn}:{reserved.Row}:{reserved.Column}",
                related, reserved.Bounds, Rectangle.Empty, Math.Max(0, .4 - ratio), AtlasIssueSeverity.Warning,
                "保留区域含未分配内容，请检查是否为背景残留或跨格特效。"));
        }
    }

    private static IEnumerable<ReservedCell> CanonicalReservedRectangles(ReferenceAtlasLayoutPlan layout)
    {
        if (layout.LayoutId != ReferenceAtlasLayoutPlanner.SLayoutId) yield break;
        var turns = layout.Placements.Select(item => item.Frame.Descriptor.Turn).Distinct().Order().ToArray();
        for (var ordinal = 0; ordinal < turns.Length; ordinal++)
        {
            var turn = turns[ordinal];
            var originX = layout.TurnCount == 1 ? 25 : 14;
            var originY = layout.TurnCount == 1 ? 0 : ordinal * 150;
            yield return new ReservedCell(turn, "small", 1, 5,
                new Rectangle(originX + 250, originY + 50, 48, 48));
            yield return new ReservedCell(turn, "small", 2, 5,
                new Rectangle(originX + 250, originY + 100, 48, 48));
        }
    }

    private static Bitmap ComposeAtlas(ReferenceAtlasLayoutPlan layout, IReadOnlyList<DecodedFrame> frames)
    {
        var byKey = frames.ToDictionary(frame => FrameKey(frame.Descriptor));
        var output = new Bitmap(layout.CanvasSize, layout.CanvasSize, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(output);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        foreach (var placement in layout.Placements)
            graphics.DrawImageUnscaled(byKey[FrameKey(placement.Frame.Descriptor)].Bitmap,
                placement.Bounds.Location);
        return output;
    }

    private static string SectionKey(FrameDescriptor descriptor)
        => $"section:{descriptor.Turn}:{(descriptor.Action == "atk" ? "atk" : "small")}";

    public static string FrameKey(FrameDescriptor descriptor)
        => $"{descriptor.SourceKey}|{descriptor.ImageNumber}|{descriptor.FrameIndex}|{descriptor.Turn}|{descriptor.Action}|{descriptor.Direction}";

    private static Lab ToLab(Color color)
    {
        static double Linear(byte component)
        {
            var value = component / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        var r = Linear(color.R); var g = Linear(color.G); var b = Linear(color.B);
        var x = (r * .4124564 + g * .3575761 + b * .1804375) / .95047;
        var y = r * .2126729 + g * .7151522 + b * .072175;
        var z = (r * .0193339 + g * .119192 + b * .9503041) / 1.08883;
        static double Pivot(double value) => value > .008856 ? Math.Pow(value, 1d / 3d) : 7.787 * value + 16d / 116d;
        var fx = Pivot(x); var fy = Pivot(y); var fz = Pivot(z);
        return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double LabDistance(Lab left, Lab right)
        => Math.Sqrt(Math.Pow(left.L - right.L, 2) + Math.Pow(left.A - right.A, 2) + Math.Pow(left.B - right.B, 2));

    private sealed class SourceMapping
    {
        public Dictionary<string, Rectangle> Rectangles { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> Confidences { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> SectionKeys { get; } = new(StringComparer.Ordinal);
        public List<ReservedCell> ReservedRectangles { get; } = [];
        public Dictionary<string, string> Issues { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> GroupScales { get; } = new(StringComparer.Ordinal);
    }

    private sealed record QuantizedImage(Bitmap Bitmap, byte[] Indexes, FrameColorQuantizationReport Report);
    private readonly record struct AlignmentMetrics(double Scale, double BaselineOffset, bool IsClipped);
    private sealed record BackgroundInfo(AtlasBackgroundKind Kind, Color Color);
    private sealed record DetectedRow(int Top, int Height, double Confidence);
    private sealed record ReservedCell(int Turn, string Section, int Row, int Column, Rectangle Bounds);
    private readonly record struct Lab(double L, double A, double B);
}
