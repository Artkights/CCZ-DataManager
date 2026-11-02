using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ImageManager.Core;

/// <summary>Checks decoded targets rather than treating a new container encoding as a successful edit.</summary>
internal sealed class MutationSemanticVerification
{
    private readonly IReadOnlyList<FileExpectation> _files;
    private MutationSemanticVerification(IReadOnlyList<FileExpectation> files) => _files = files;
    public bool HasChanges => _files.Any(file => file.HasChanges);

    public static MutationSemanticVerification Prepare(ProjectProfile project, MutationPlan plan)
    {
        var files = new List<FileExpectation>();
        foreach (var file in plan.Files)
        {
            file.ExpectedSource.Verify(file.TargetPath);
            var before = File.Exists(file.TargetPath) ? File.ReadAllBytes(file.TargetPath) : null;
            file.ExpectedSource.Verify(file.TargetPath);
            files.Add(PrepareFile(project, file, before, plan.Kind == MutationKind.WholeFileReplace));
        }
        VerifyImages(project, plan, fromDisk: false);
        return new(files);
    }

    public void VerifyWritten(ProjectProfile project, MutationPlan plan)
    {
        foreach (var file in _files)
        {
            if (file.Deleted)
            {
                if (File.Exists(file.Path)) throw new IOException("资源删除后仍存在：" + file.Path);
                continue;
            }
            if (file.ArchiveEntryCount is { } count)
            {
                var archive = E5Archive.OpenFresh(file.Path);
                if (archive.Entries.Count != count) throw new IOException("资源复读的条目数量不符：" + file.Path);
                foreach (var target in file.Entries)
                    if (PayloadSignature(project, file.Path, archive.ReadDecoded(target.Key)) != target.Value)
                        throw new IOException($"资源复读像素/载荷不符：{file.Path}，物理图号 {target.Key}");
            }
            else if (file.DllImages != null)
            {
                if (!SameImages(file.DllImages, DllSignatures(file.Path)))
                    throw new IOException("DLL 原生位图 ID、语言或像素复读不符：" + file.Path);
            }
            else if (file.Signature != PayloadSignature(project, file.Path, File.ReadAllBytes(file.Path)))
                throw new IOException("资源复读像素/载荷不符：" + file.Path);
        }
        VerifyImages(project, plan, fromDisk: true);
    }

    private static FileExpectation PrepareFile(ProjectProfile project, MutationFile file, byte[]? before, bool allowSourceRepair)
    {
        var path = Path.GetFullPath(file.TargetPath);
        if (file.DeleteTarget) return new(path, before != null, true);
        if (IsArchive(path))
        {
            var candidate = E5Archive.Parse(path, file.CandidateBytes);
            E5Archive? old = null;
            if (before != null)
            {
                try { old = E5Archive.Parse(path, before); }
                catch (Exception ex) when (allowSourceRepair && IsInvalidSourcePayload(ex))
                { /* The candidate remains strictly validated below. */ }
            }
            var targets = new Dictionary<int, string>();
            var changed = old == null || old.Entries.Count != candidate.Entries.Count;
            foreach (var entry in candidate.Entries)
            {
                byte[] payload;
                try
                {
                    payload = candidate.ReadDecoded(entry.ImageNumber);
                }
                catch (Exception ex) when (IsInvalidSourcePayload(ex) && old != null &&
                    old.Entries.Any(oldEntry =>
                        oldEntry.StoredLength == entry.StoredLength &&
                        oldEntry.DecodedLength == entry.DecodedLength &&
                        old.ReadStored(oldEntry.ImageNumber).AsSpan()
                            .SequenceEqual(candidate.ReadStored(entry.ImageNumber))))
                {
                    // An unmatched reserved tail is opaque by design. Rebuild copies
                    // its stored bytes verbatim, so it is preserved rather than decoded.
                    continue;
                }
                byte[]? previous = null;
                string? previousSignature = null;
                try
                {
                    previous = old != null && entry.ImageNumber <= old.Entries.Count ? old.ReadDecoded(entry.ImageNumber) : null;
                    if (previous != null && previous.AsSpan().SequenceEqual(payload)) continue;
                    if (previous != null) previousSignature = PayloadSignature(project, path, previous);
                }
                catch (Exception ex) when (IsInvalidSourcePayload(ex))
                {
                    // An opaque old tail can be shifted by an insertion; compare only the
                    // candidate. A whole-file replacement may also repair a corrupt stream.
                    previous = null;
                }
                var signature = PayloadSignature(project, path, payload);
                targets.Add(entry.ImageNumber, signature);
                if (previous == null || signature != previousSignature) changed = true;
            }
            return new(path, changed, false, candidate.Entries.Count, targets);
        }
        if (Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var candidateImages = WithTemporaryDll(file.CandidateBytes, DllSignatures);
            var changed = before == null;
            if (before != null)
            {
                try { changed = !SameImages(candidateImages, WithTemporaryDll(before, DllSignatures)); }
                catch (Exception ex) when (allowSourceRepair && IsInvalidSourcePayload(ex)) { changed = true; }
            }
            return new(path, changed, false, DllImages: candidateImages);
        }
        var expected = PayloadSignature(project, path, file.CandidateBytes);
        var payloadChanged = before == null;
        if (before != null)
        {
            try { payloadChanged = expected != PayloadSignature(project, path, before); }
            catch (Exception ex) when (allowSourceRepair && IsInvalidSourcePayload(ex)) { payloadChanged = true; }
        }
        return new(path, payloadChanged, false, Signature: expected);
    }

    private static bool IsInvalidSourcePayload(Exception exception)
        => exception is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException or
            NotSupportedException or System.Runtime.InteropServices.ExternalException or InvalidOperationException;

    private static bool IsArchive(string path)
        => Path.GetExtension(path).Equals(".e5", StringComparison.OrdinalIgnoreCase) &&
           !Path.GetFileName(path).StartsWith("Mcall", StringComparison.OrdinalIgnoreCase) &&
           !Path.GetFileName(path).Equals("Data.e5", StringComparison.OrdinalIgnoreCase) &&
           !Path.GetFileName(path).Equals("Mark.e5", StringComparison.OrdinalIgnoreCase);

    private static string PayloadSignature(ProjectProfile project, string path, byte[] bytes)
    {
        var name = Path.GetFileName(path);
        if (new[] { "Data.e5", "Pmap.e5", "Hexzmap.e5", "Spalet.e5", "Mark.e5" }
            .Contains(name, StringComparer.OrdinalIgnoreCase)) return "payload:" + Hash(bytes);
        if (Path.GetFileName(path).StartsWith("Mcall", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("Meff.e5", StringComparison.OrdinalIgnoreCase))
        {
            using var animation = new StrategyAnimationPackageCodec().Decode(project, bytes);
            return "animation:" + Hash(animation.RecordBytes) + ":" + Hash(animation.TailBytes) + ":" +
                string.Join(";", animation.PlaneFrames.Select(frame => MutationImageExpectation.HashPixels(frame)));
        }
        if (ImageRendering.IsStandardImage(bytes))
        {
            using var image = BmpPayloadService.Decode(bytes);
            var length = BmpPayloadService.GetStandardImageLength(bytes);
            return "image:" + MutationImageExpectation.HashPixels(image) + ":tail:" + Hash(bytes.AsSpan(length));
        }
        // RAW indexes, Pmap/Hexzmap terrain and other structured payloads are
        // compared after E5 decompression. Their non-pixel bytes remain meaningful.
        return "payload:" + Hash(bytes);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static Dictionary<string, string> DllSignatures(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in DllBitmapArchive.Read(path, requireAllBitmapRecords: true))
        {
            using var image = DllBitmapArchive.Decode(entry);
            result.Add(entry.Id + ":" + entry.Language, MutationImageExpectation.HashPixels(image, blueKey: true));
        }
        return result;
    }

    private static bool SameImages(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var other) && item.Value == other);

    private static T WithTemporaryDll<T>(byte[] bytes, Func<string, T> read)
    {
        var path = Path.Combine(Path.GetTempPath(), "ImageManager.Semantics." + Guid.NewGuid().ToString("N") + ".dll");
        try { File.WriteAllBytes(path, bytes); return read(path); }
        finally { ResourceFileRecovery.TryDelete(path); }
    }

    private static void VerifyImages(ProjectProfile project, MutationPlan plan, bool fromDisk)
    {
        foreach (var group in plan.ExpectedImages.GroupBy(image => Path.GetFullPath(image.Target.SourcePath), StringComparer.OrdinalIgnoreCase))
        {
            var file = plan.Files.SingleOrDefault(file => SamePath(file.TargetPath, group.Key));
            if (file == null || file.DeleteTarget)
                throw new InvalidDataException("预期图片未包含在写入目标中：" + group.Key);
            var bytes = fromDisk ? File.ReadAllBytes(file.TargetPath) : file.CandidateBytes;
            var archive = IsArchive(file.TargetPath)
                ? fromDisk ? E5Archive.OpenFresh(file.TargetPath) : E5Archive.Parse(file.TargetPath, bytes) : null;
            var payloads = new Dictionary<int, byte[]>();
            foreach (var expected in group)
            {
                var descriptor = expected.Target;
                if (!payloads.TryGetValue(descriptor.ImageNumber, out var payload))
                    payloads[descriptor.ImageNumber] = payload = archive?.ReadDecoded(descriptor.ImageNumber) ?? bytes;
                using var image = ReadExpectedImage(project, expected, payload, fromDisk);
                if (image.Width != descriptor.Width || image.Height != descriptor.Height ||
                    MutationImageExpectation.HashPixels(image, Path.GetExtension(descriptor.SourcePath).Equals(".dll", StringComparison.OrdinalIgnoreCase),
                        expected.TransparentColor) != expected.PixelSha256)
                    throw new InvalidDataException($"{(fromDisk ? "磁盘复读" : "写入候选")}没有包含编辑像素：{descriptor.SourceKey}，物理图号 {descriptor.ImageNumber}，帧 {descriptor.FrameIndex}");
            }
        }
    }

    private static Bitmap ReadExpectedImage(ProjectProfile project, MutationImageExpectation expected, byte[] bytes, bool fromDisk)
    {
        var target = expected.Target;
        if (Path.GetExtension(target.SourcePath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            Bitmap Read(string path)
            {
                var entry = DllBitmapArchive.Read(path, requireAllBitmapRecords: true)
                    .SingleOrDefault(entry => entry.Id == target.ImageNumber && entry.Language == target.SourceOffset)
                    ?? throw new InvalidDataException("预期原生位图 ID/语言不存在。");
                return DllBitmapArchive.Decode(entry);
            }
            return fromDisk ? Read(target.SourcePath) : WithTemporaryDll(bytes, Read);
        }
        var payload = bytes;
        if (expected.IsAnimationPlane)
        {
            using var animation = new StrategyAnimationPackageCodec().Decode(project, payload);
            if (target.FrameIndex < 0 || target.FrameIndex >= animation.PlaneFrames.Count)
                throw new InvalidDataException("预期原生动画平面不存在。");
            return ImageImportRasterNormalization.CopyArgb(animation.PlaneFrames[target.FrameIndex]);
        }
        var strip = expected.Kind is ResourceKind.RComposite or ResourceKind.SComposite or ResourceKind.WeatherSpecial;
        using var decoded = ImageRendering.IsStandardImage(payload) ? BmpPayloadService.Decode(payload)
            : DecodeRawPixels(payload, target.Width, strip ? payload.Length / target.Width : target.Height,
                new PaletteService().RequireWritePalette(project));
        if (!strip) return ImageImportRasterNormalization.CopyArgb(decoded);
        var rectangle = new Rectangle(0, checked(target.FrameIndex * target.Height), target.Width, target.Height);
        if (!new Rectangle(Point.Empty, decoded.Size).Contains(rectangle))
            throw new InvalidDataException("预期物理帧超出动作条。");
        return ImageRendering.Crop(decoded, rectangle);
    }

    private static Bitmap DecodeRawPixels(byte[] payload, int width, int height, IReadOnlyList<Color> palette)
    {
        var bitmap = ImageRendering.DecodeIndexed(payload, width, height, palette);
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[checked(width * 4)];
            for (var y = 0; y < height; y++)
            {
                var scan = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(scan, row, 0, row.Length);
                for (var x = 0; x < width; x++)
                    if (payload[y * width + x] == 0) row.AsSpan(x * 4, 4).Clear();
                Marshal.Copy(row, 0, scan, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static bool SamePath(string left, string right)
        => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed record FileExpectation(string Path, bool HasChanges, bool Deleted,
        int? ArchiveEntryCount = null, Dictionary<int, string>? EntryTargets = null,
        Dictionary<string, string>? DllImages = null, string? Signature = null)
    {
        public IReadOnlyDictionary<int, string> Entries => EntryTargets ?? new Dictionary<int, string>();
    }
}
