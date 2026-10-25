using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace ImageManager.Core;

/// <summary>External image files become native strips; target identity never comes from a material name.</summary>
internal static class RsImageFileImport
{
    private sealed record StripSpec(int Width, int FrameHeight, int Count)
    {
        public int Height => checked(FrameHeight * Count);
        public Size Size => new(Width, Height);
    }

    private sealed record Prepared(ImageImportAssignment Assignment, string Path, int ImageNumber,
        byte[] Payload, ImageImportPreviewRow Row);

    public static ImageImportBuildResult Build(ImageImportRequest request, CancellationToken token)
    {
        var rows = new List<ImageImportPreviewRow>();
        try { return BuildCore(request, token, rows); }
        catch
        {
            foreach (var row in rows) row.Dispose();
            throw;
        }
    }

    private static ImageImportBuildResult BuildCore(ImageImportRequest request, CancellationToken token,
        List<ImageImportPreviewRow> rows)
    {
        token.ThrowIfCancellationRequested();
        var isS = request.Resource.Kind == ResourceKind.SComposite;
        if (!isS && request.Resource.Kind != ResourceKind.RComposite)
            throw new InvalidOperationException("此导入器只处理 R/S 外部图片。");
        if (request.Project.IsReadOnly) throw new InvalidOperationException("当前项目只读，不能导入 R/S 图片。");
        var append = request.Operation == ImageImportOperation.Append;
        var required = isS ? new[] { "mov", "atk", "spc" } : new[] { "front", "back" };
        var prepared = new List<Prepared>();
        var warnings = new List<string>();
        var assignments = request.Assignments.Where(item => item.Included &&
            (append || !string.IsNullOrWhiteSpace(item.SourcePath))).ToArray();
        var archives = new Dictionary<string, E5Archive>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? contextError = null;
        try
        {
            foreach (var role in required)
            {
                var candidates = request.Resource.Paths.Where(path => !isS ||
                    Path.GetFileNameWithoutExtension(path).Equals("Unit_" + role, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length != 1) throw new InvalidDataException($"无法唯一确定 {role} 的 R/S 目标档案。");
                paths[role] = Path.GetFullPath(candidates[0]);
                if (!archives.ContainsKey(paths[role])) archives[paths[role]] = E5Archive.OpenFresh(paths[role]);
            }
            if (append)
            {
                if (isS && (archives.Values.Select(archive => archive.Entries.Count).Distinct().Count() != 1 ||
                            archives.Values.Any(archive => archive.Entries.Count < 240)))
                    throw new InvalidDataException("新增将领 S 要求 Unit_mov/atk/spc 三档图片数量一致且至少已有 240 个兵种固定槽。");
                if (!isS && archives.Values.Single().Entries.Count % 2 != 0)
                    throw new InvalidDataException("Pmapobj 正背条目数量不是偶数，不能成对追加 R 形象。");
            }
        }
        catch (Exception ex) when (IsRowError(ex)) { contextError = ex.Message; }

        var groups = assignments.Select(item => item.Group).Distinct().Order().ToArray();
        var duplicateTargets = !append ? assignments.GroupBy(item => TargetKey(item))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        foreach (var assignment in assignments)
        {
            token.ThrowIfCancellationRequested();
            var role = assignment.Role.ToLowerInvariant();
            var spec = required.Contains(role) ? Specification(role) : null;
            var sourceSize = Size.Empty;
            var originalFormat = append ? "新增" : "未知";
            var outputFormat = isS && !append ? "待确定" : "PNG32";
            var label = append ? $"新增第 {Array.IndexOf(groups, assignment.Group) + 1} 组" : assignment.Target?.DisplayName ?? "未选择目标";
            Bitmap? before = null;
            Bitmap? after = null;
            try
            {
                if (spec == null) throw new InvalidDataException($"未知 R/S 动作：{assignment.Role}。");
                using var input = LoadSource(assignment.SourcePath, token);
                sourceSize = input.Size;
                if (input.Size != spec.Size)
                    throw new InvalidDataException($"{role} 必须为 {spec.Width}×{spec.Height}（{spec.Count} 帧），实际为 {input.Width}×{input.Height}；不会自动缩放或裁切。");
                if (contextError != null) throw new InvalidDataException(contextError);
                var targetPath = paths[role];
                var archive = archives[targetPath];
                var imageNumber = append ? archive.Entries.Count + Array.IndexOf(groups, assignment.Group) * (isS ? 1 : 2) +
                    (isS || role == "front" ? 1 : 2) : ResolveTarget(assignment, isS, targetPath, spec);
                if (!append && duplicateTargets.Contains(TargetKey(assignment)))
                    throw new InvalidDataException("同一目标动作重复分配了多个素材，请保留一项。");
                if (append && assignments.Count(item => item.Group == assignment.Group &&
                    item.Role.Equals(role, StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new InvalidDataException($"新增同一组的 {role} 必须恰好分配一张素材。");
                // Explicit output choices inspect the same-action final template before appending.
                // Null retains the historical API behaviour, including true-colour R appends.
                var templateNumber = isS || role == "back" ? archive.Entries.Count : archive.Entries.Count - 1;
                var original = !append ? archive.ReadDecoded(imageNumber) : request.OutputFormat.HasValue
                    ? templateNumber > 0 ? archive.ReadDecoded(templateNumber)
                        : throw new InvalidDataException("新增 R/S 缺少已确认的同动作编码模板。")
                    : null;
                if (original != null)
                {
                    originalFormat = StorageKind(original);
                    if (originalFormat != "RAW8") ImageImportRasterNormalization.RequireNoUnknownTail(original);
                    else if (original.Length != spec.Width * spec.Height && original.Length != spec.Width * spec.Height + 2)
                        throw new InvalidDataException("目标 RAW8 长度损坏，只允许完整像素区及可选的 2 字节尾部。");
                    if (isS) ValidateSTarget(original, originalFormat, spec);
                    if (!append) before = DecodePayload(original, originalFormat, spec, request.Project, token);
                }
                var notes = new List<string>();
                byte[] payload;
                if (original != null && originalFormat == "RAW8" && (isS || request.OutputFormat.HasValue))
                {
                    outputFormat = "RAW8";
                    payload = EncodeRaw(input, original, request.Project, notes, token);
                    if (request.OutputFormat.HasValue) notes.Add(ImageImportFormatPolicy.GetSupport(request.Resource, true).Description);
                }
                else if (request.OutputFormat == ImageImportOutputFormat.Preserve)
                {
                    outputFormat = originalFormat;
                    payload = EncodePreserved(input, original!, notes, token);
                }
                else
                {
                    outputFormat = request.OutputFormat == ImageImportOutputFormat.Bmp ||
                        request.OutputFormat == null && isS && originalFormat == "BMP" ? "BMP32" : "PNG32";
                    payload = EncodeTrueColor(input, outputFormat == "BMP32", notes, token);
                }
                // Preview the committed encoding, including quantization and BMP alpha semantics.
                after = DecodePayload(payload, outputFormat, spec, request.Project, token);
                if (append) label += isS ? $" → S_{imageNumber:000}" : $" → R{(imageNumber - 1) / 2:000}";
                else if (isS) label += $" / Unit #{imageNumber}";
                var row = new ImageImportPreviewRow(label, role, assignment.SourcePath, sourceSize, after.Size,
                    append && request.OutputFormat.HasValue ? "新增（模板 " + originalFormat + "）" : originalFormat,
                    outputFormat, string.Join("；", notes), before, after);
                rows.Add(row);
                prepared.Add(new(assignment, targetPath, imageNumber, payload, row));
                before = null;
                after = null;
            }
            catch (Exception ex) when (IsRowError(ex))
            {
                rows.Add(new(label, role, assignment.SourcePath, sourceSize, spec?.Size ?? Size.Empty,
                    originalFormat, outputFormat, string.Empty, before, after, ex.Message));
                before = null;
                after = null;
            }
            finally { before?.Dispose(); after?.Dispose(); }
        }

        if (append)
        {
            foreach (var group in groups)
            {
                var entries = assignments.Where(item => item.Group == group).ToArray();
                var missing = required.Where(role => !entries.Any(item => item.Role.Equals(role, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.SourcePath))).ToArray();
                foreach (var role in missing)
                    rows.Add(new($"新增第 {Array.IndexOf(groups, group) + 1} 组", role, string.Empty, Size.Empty,
                        Specification(role).Size, "新增", "PNG32", string.Empty, null, null, $"新增组缺少 {role} 图片，必须完整配对/配组三种动作。"));
                // An incomplete group is never partially appended to any archive.
                if (missing.Length > 0 || entries.Length != required.Length ||
                    prepared.Count(item => item.Assignment.Group == group) != required.Length)
                    prepared.RemoveAll(item => item.Assignment.Group == group);
            }
        }
        if (assignments.Length == 0)
            rows.Add(new("R/S 导入", string.Empty, string.Empty, Size.Empty, Size.Empty, string.Empty, string.Empty,
                string.Empty, null, null, "没有已勾选且分配素材的导入项。"));

        token.ThrowIfCancellationRequested();
        var files = (append && rows.Any(row => row.Error != null) ? Enumerable.Empty<Prepared>() : prepared)
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            token.ThrowIfCancellationRequested();
            var archive = archives[group.Key];
            var bytes = append ? archive.Rebuild(appendDecoded: group.OrderBy(item => item.Assignment.Group)
                .ThenBy(item => item.ImageNumber).Select(item => item.Payload).ToArray()) :
                archive.Rebuild(replacementDecoded: group.ToDictionary(item => item.ImageNumber, item => item.Payload));
            return new MutationFile(group.Key, bytes, append ? "从外部图片追加 R/S 原生动作条" : "从外部图片替换选中 R/S 动作条");
        }).ToArray();
        warnings.Add(request.OutputFormat.HasValue ?
            "按预览中显示的实际格式写入；RAW8 保留原结构与项目调色板。R 正背、S 三动作保持对应物理编号，不自动修改 Data 指定。" :
            isS ? append ? "新增 S 使用 PNG32；mov/atk/spc 同步追加一个物理条目，不自动修改 Data 指定。" :
            "只替换选中的物理 S 图号及已分配动作；RAW8 使用项目调色板，BMP/PNG 按目标种类编码。" :
            "R 外部图片按工具整合包规则编码为 PNG32；新增时成对追加正背条目。");
        return new(files, rows, warnings);
    }

    private static string TargetKey(ImageImportAssignment item)
    {
        var frames = item.Target?.Frames.Where(frame => (item.Target.Kind == ResourceKind.SComposite ?
            frame.Action : frame.Direction).Equals(item.Role, StringComparison.OrdinalIgnoreCase)).ToArray();
        return frames is { Length: > 0 } ? string.Join(';', frames.Select(frame =>
            $"{Path.GetFullPath(frame.SourcePath)}#{frame.ImageNumber}").Distinct(StringComparer.OrdinalIgnoreCase)) :
            $"{item.Target?.StablePhysicalId}|{item.Role.ToLowerInvariant()}";
    }

    private static int ResolveTarget(ImageImportAssignment assignment, bool isS, string path, StripSpec spec)
    {
        var target = assignment.Target ?? throw new InvalidDataException("替换必须绑定已选中的 R/S 形象。");
        if (target.Kind != (isS ? ResourceKind.SComposite : ResourceKind.RComposite))
            throw new InvalidDataException("素材目标类型与当前 R/S 分类不一致。");
        if (isS) SResourceExchangeNaming.ValidateFrameSet(target);
        var frames = target.Frames.Where(frame => (isS ? frame.Action : frame.Direction)
            .Equals(assignment.Role, StringComparison.OrdinalIgnoreCase)).OrderBy(frame => frame.FrameIndex).ToArray();
        if (frames.Length != spec.Count || frames.Any(frame => !Path.GetFullPath(frame.SourcePath).Equals(path, StringComparison.OrdinalIgnoreCase) ||
            frame.Width != spec.Width || frame.Height != spec.FrameHeight) ||
            !frames.Select(frame => frame.FrameIndex).SequenceEqual(Enumerable.Range(0, spec.Count)) ||
            frames.Select(frame => frame.ImageNumber).Distinct().Count() != 1)
            throw new InvalidDataException("目标动作必须绑定同一档案、同一物理图号的完整原生帧集合。");
        if (isS && target.PhysicalImageNumber.HasValue && target.PhysicalImageNumber != frames[0].ImageNumber)
            throw new InvalidDataException("选中 S 的物理编号与其动作帧不一致。");
        return frames[0].ImageNumber;
    }

    private static StripSpec Specification(string role) => role switch
    {
        "front" or "back" => new(48, 64, 20),
        "mov" => new(48, 48, 11),
        "atk" => new(64, 64, 12),
        "spc" => new(48, 48, 5),
        _ => throw new InvalidDataException("未知 R/S 动作。")
    };

    private static Bitmap LoadSource(string path, CancellationToken token)
    {
        using var source = ImageImportRasterNormalization.Load(path, token);
        if (source.Format is not ("BMP" or "PNG" or "JPG"))
            throw new InvalidDataException("R/S 动作条仍只接受 BMP、PNG、JPG/JPEG，必须符合对应帧尺寸与帧数。");
        // GDI+ can draw a truncated stream whose final container boundary is missing.
        // Require complete source containers, while ignoring no extra image metadata.
        if (source.Format is "PNG" or "JPG") _ = BmpPayloadService.GetStandardImageLength(source.Bytes);
        else if (source.Bytes.Length < 26 || BinaryPrimitives.ReadUInt32LittleEndian(source.Bytes.AsSpan(2, 4)) > source.Bytes.Length)
            throw new InvalidDataException("BMP 素材文件边界不完整。");
        return ImageImportRasterNormalization.CopyArgb(source.Bitmap);
    }

    private static Bitmap DecodeArgb(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var image = Image.FromStream(stream, false, true);
        return ImageImportRasterNormalization.CopyArgb((Bitmap)image);
    }

    private static string StorageKind(byte[] bytes)
        => bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M' ? "BMP" :
            bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "PNG" :
            bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 216 ? "JPG" : "RAW8";

    private static void ValidateSTarget(byte[] bytes, string kind, StripSpec spec)
    {
        if (kind == "RAW8")
        {
            var trailing = bytes.Length - checked(spec.Width * spec.Height);
            if (trailing is not (0 or 2)) throw new InvalidDataException("目标 RAW8 长度损坏，只允许完整像素区及可选的 2 字节尾部。");
            return;
        }
        if (kind == "JPG") throw new InvalidDataException("S 目标禁止 JPG 存储，请先修复目标档案。");
        using var stream = new MemoryStream(bytes, false);
        using var image = Image.FromStream(stream, false, true);
        if (image.Size != spec.Size || Image.GetPixelFormatSize(image.PixelFormat) is not (8 or 24 or 32))
            throw new InvalidDataException("目标 S 的原始尺寸或位深损坏，要求原生尺寸及 8/24/32 位图像。");
    }

    private static byte[] EncodeTrueColor(Bitmap input, bool bmp, List<string> notes, CancellationToken token)
    {
        using var bitmap = NormalizeTrueColor(input, token, bmp);
        using var output = new MemoryStream();
        bitmap.Save(output, bmp ? ImageFormat.Bmp : ImageFormat.Png);
        notes.Add(bmp ? "BMP32；不沿用源/目标索引调色板。" : "32 位 PNG；保留非键色 alpha。");
        if (bmp) notes.Add("BI_RGB不保存alpha；完全透明转F700FF键色，其余像素按RGB实色写入。");
        notes.Add("洋红透明阈值 R≥247/G≤8/B≥248；不缩放、不裁切。");
        return output.ToArray();
    }

    private static byte[] EncodePreserved(Bitmap input, byte[] original, List<string> notes, CancellationToken token)
    {
        using var bitmap = NormalizeTrueColor(input, token);
        var payload = ImageImportTargetEncoding.Standard(original, bitmap, token);
        notes.Add("保留目标或末尾同动作模板的存储格式；BMP保留位深、调色板和行布局。洋红及完全透明按目标透明规则写入。");
        if (StorageKind(original) == "JPG") notes.Add("JPG有损存储，不保存透明通道。");
        return payload;
    }

    private static Bitmap NormalizeTrueColor(Bitmap input, CancellationToken token, bool bmp = false)
    {
        var pixels = ImageImportRasterNormalization.ReadArgbPixels(input);
        for (var y = 0; y < input.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < input.Width; x++)
            {
                var at = (y * input.Width + x) * 4;
                if (pixels[at + 3] == 0 || pixels[at + 2] >= 247 && pixels[at + 1] <= 8 && pixels[at] >= 248)
                {
                    if (!bmp) pixels.AsSpan(at, 4).Clear();
                    else
                    {
                        pixels[at] = 255; pixels[at + 1] = 0; pixels[at + 2] = 247; pixels[at + 3] = 255;
                    }
                }
                else if (bmp) pixels[at + 3] = 255;
            }
        }
        return FromPixels(input.Size, pixels);
    }

    private static byte[] EncodeRaw(Bitmap input, byte[] original, ProjectProfile project, List<string> notes, CancellationToken token)
    {
        var paletteService = new PaletteService();
        var resolution = paletteService.Resolve(project);
        var palette = paletteService.RequireWritePalette(project);
        if (palette.Count != 256) throw new InvalidDataException("RAW8 写入要求有效的 256 色项目调色板。");
        var pixels = ImageImportRasterNormalization.ReadArgbPixels(input);
        var count = checked(input.Width * input.Height);
        var output = new byte[count + (original.Length == count + 2 ? 2 : 0)];
        var exact = new Dictionary<int, byte>();
        for (var p = 1; p < palette.Count; p++) exact.TryAdd(palette[p].R << 16 | palette[p].G << 8 | palette[p].B, (byte)p);
        var nearest = new Dictionary<int, byte>();
        var approximated = 0;
        for (var y = 0; y < input.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < input.Width; x++)
            {
                var index = y * input.Width + x;
                var at = index * 4;
                var r = pixels[at + 2]; var g = pixels[at + 1]; var b = pixels[at];
                if (pixels[at + 3] == 0 || r >= 248 && g <= 8 && b >= 248) continue;
                var key = r << 16 | g << 8 | b;
                if (exact.TryGetValue(key, out var found)) { output[index] = found; continue; }
                approximated++;
                if (!nearest.TryGetValue(key, out found))
                {
                    var distance = int.MaxValue;
                    found = 1;
                    for (var p = 1; p < palette.Count; p++)
                    {
                        var color = palette[p]; var dr = r - color.R; var dg = g - color.G; var db = b - color.B;
                        var candidate = dr * dr + dg * dg + db * db;
                        if (candidate >= distance) continue;
                        distance = candidate; found = (byte)p;
                    }
                    nearest[key] = found;
                }
                output[index] = found;
            }
        }
        if (output.Length > count) original.AsSpan(count, 2).CopyTo(output.AsSpan(count));
        notes.Add($"调色板：{resolution.Source}；{approximated:N0} 个像素采用最近色，无抖动。");
        notes.Add("透明及 R≥248/G≤8/B≥248 洋红写索引0；RAW8 不保存半透明，其余非零 alpha 按 RGB 实色量化。");
        if (output.Length > count) notes.Add("原样保留目标 RAW8 的 2 字节尾部。");
        return output;
    }

    private static Bitmap DecodePayload(byte[] bytes, string kind, StripSpec spec, ProjectProfile project, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (kind != "RAW8") return DecodeArgb(bytes);
        if (bytes.Length != spec.Width * spec.Height && bytes.Length != spec.Width * spec.Height + 2)
            throw new InvalidDataException("原生 RAW8 动作条长度损坏，无法生成可靠的编码后预览。");
        var palette = new PaletteService().RequireWritePalette(project);
        var pixels = new byte[checked(spec.Width * spec.Height * 4)];
        for (var y = 0; y < spec.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < spec.Width; x++)
            {
                var index = bytes[y * spec.Width + x];
                if (index == 0) continue;
                var color = palette[index]; var at = (y * spec.Width + x) * 4;
                pixels[at] = color.B; pixels[at + 1] = color.G; pixels[at + 2] = color.R; pixels[at + 3] = 255;
            }
        }
        return FromPixels(spec.Size, pixels);
    }

    private static Bitmap FromPixels(Size size, byte[] pixels)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < size.Height; y++)
                Marshal.Copy(pixels, y * size.Width * 4, IntPtr.Add(data.Scan0, y * data.Stride), size.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static bool IsRowError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or ExternalException or OverflowException;
}
