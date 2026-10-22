using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ImageManager.Core;

/// <summary>File conversion for verified Weather, Pmap and map-image payloads.</summary>
internal static class SpecialImageFileImport
{
    internal static ImageImportBuildResult Build(ImageImportRequest request, CancellationToken token)
    {
        var rows = new List<ImageImportPreviewRow>();
        var files = new List<MutationFile>();
        var warnings = new List<string>();
        try
        {
            var append = request.Operation == ImageImportOperation.Append;
            if (append && request.Resource.Kind == ResourceKind.MapPair)
                throw new InvalidDataException("战场地图使用已有固定地图与地形槽；新增需要已验证的新地形记录，当前只开放现槽替换。");
            var replacements = new Dictionary<string, Dictionary<int, byte[]>>(StringComparer.OrdinalIgnoreCase);
            var appends = new Dictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in request.Assignments.Where(item => item.Included && !string.IsNullOrWhiteSpace(item.SourcePath)).OrderBy(item => item.Group))
            {
                token.ThrowIfCancellationRequested();
                Bitmap? before = null;
                Bitmap? after = null;
                var size = Size.Empty;
                var output = Size.Empty;
                var label = assignment.Target?.DisplayName ?? "未选择目标";
                try
                {
                    if (!assignment.Role.Equals("image", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("专用资源的视觉素材角色必须为 image。");
                    var target = assignment.Target;
                    if (!append && target == null) throw new InvalidDataException("专用图片替换必须绑定现有目标。");
                    if (target != null && target.Kind != request.Resource.Kind) throw new InvalidDataException("专用图片目标类型与当前资源不一致。");
                    var frame = target?.Frames.GroupBy(item => (Path.GetFullPath(item.SourcePath).ToUpperInvariant(), item.ImageNumber))
                        .Single().First();
                    var path = Path.GetFullPath(frame?.SourcePath ?? request.Resource.Paths.Single());
                    if (!request.Resource.Paths.Any(candidate => Path.GetFullPath(candidate).Equals(path, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("目标不属于当前资源。");
                    if (!append && !targets.Add(path + "#" + frame!.ImageNumber)) throw new InvalidDataException("同一目标被重复分配素材。");
                    using var source = ImageImportRasterNormalization.Load(assignment.SourcePath, token);
                    size = source.Bitmap.Size;
                    if (request.Resource.Kind is ResourceKind.WeatherSpecial or ResourceKind.PmapSpecial)
                    {
                        var archive = E5Archive.OpenFresh(path);
                        var weather = request.Resource.Kind == ResourceKind.WeatherSpecial;
                        if (archive.Entries.Count == 0) throw new InvalidDataException("专用资源没有已验证的原始模板，禁止猜测新增头部或结构。");
                        if (weather && archive.Entries.Any(entry => entry.DecodedLength != 43200 || archive.ReadDecoded(entry.ImageNumber).Length != 43200))
                            throw new InvalidDataException("天气档案含未验证的非43200字节条目，拒绝普通图片覆盖。");
                        if (!weather && archive.Entries.Any(entry => !PmapResourceCodec.IsVerifiedPayload(archive.ReadDecoded(entry.ImageNumber))))
                            throw new InvalidDataException("Pmap档案含未验证的头部或平面结构，禁止静态图片导入。");
                        if (!appends.TryGetValue(path, out var additions)) appends[path] = additions = [];
                        var number = append ? checked(archive.Entries.Count + additions.Count + 1) : frame!.ImageNumber;
                        var original = archive.ReadDecoded(append ? archive.Entries.Count : number);
                        output = weather ? new Size(WeatherResourceCodec.FrameWidth, WeatherResourceCodec.FrameHeight * WeatherResourceCodec.FrameCount) :
                            PmapResourceCodec.PayloadSize(original);
                        using var normalized = ImageImportRasterNormalization.Canvas(source.Bitmap, output);
                        var payload = weather ? WeatherResourceCodec.RebuildPayload(request.Project, original, normalized, token) :
                            PmapResourceCodec.RebuildPayload(request.Project, original, normalized, token);
                        var palette = new PaletteService().RequireWritePalette(request.Project);
                        if (!append) before = ImageRendering.DecodeIndexed(weather ? original : original.AsSpan(16).ToArray(), output.Width, output.Height, palette);
                        after = ImageRendering.DecodeIndexed(weather ? payload : payload.AsSpan(16).ToArray(), output.Width, output.Height, palette);
                        if (append) { additions.Add(payload); label = request.Resource.DisplayName + $" #{number}"; }
                        else
                        {
                            if (!replacements.TryGetValue(path, out var byNumber)) replacements[path] = byNumber = [];
                            byNumber.Add(number, payload);
                        }
                        rows.Add(new(label, "image", assignment.SourcePath, size, output, append ? "新增" : weather ? "RAW8 四帧" : "16字节头+RAW8",
                            weather ? "RAW8 四帧" : "16字节头+RAW8",
                            (weather ? "缩放至216×200，依次对应4帧216×50；" : $"缩放至{output.Width}×{output.Height}，逐字节保留16字节原头；") +
                            $"使用项目调色板量化：{new PaletteService().Resolve(request.Project).Source}。",
                            before, after) { SourceCrop = new Rectangle(Point.Empty, size) });
                    }
                    else if (request.Resource.Kind == ResourceKind.MapPair)
                    {
                        var original = File.ReadAllBytes(path);
                        ImageImportRasterNormalization.RequireNoUnknownTail(original);
                        before = ImageImportRasterNormalization.DecodeArgb(original);
                        output = before.Size;
                        ValidateMapSlot(request.Project, target!, output);
                        using var normalized = FitMap(source.Bitmap, output);
                        var payload = Jpeg(normalized);
                        after = ImageImportRasterNormalization.DecodeArgb(payload);
                        files.Add(new(path, payload, "自动适配已有地图尺寸并编码JPG；保留地形层"));
                        rows.Add(new(label, "image", assignment.SourcePath, size, output, "JPG 地图", "JPG24",
                            "缩放至现有地图尺寸并编码JPG；Hexzmap地形层作为校验依赖保留，未从普通图片推断地形。",
                            before, after) { SourceCrop = new Rectangle(Point.Empty, size) });
                    }
                    else throw new InvalidDataException("此专用结构没有已验证的图片编码器。");
                    before = null; after = null;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or
                    ExternalException or NotSupportedException or OverflowException)
                {
                    rows.Add(new(label, "image", assignment.SourcePath, size, output, "", "", "该项未生成可提交候选。", null, null, ex.Message));
                }
                finally { before?.Dispose(); after?.Dispose(); }
            }
            token.ThrowIfCancellationRequested();
            if (!rows.Any(row => row.Error != null))
                foreach (var path in replacements.Keys.Concat(appends.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var archive = E5Archive.OpenFresh(path);
                    var candidate = archive.Rebuild(replacementDecoded: replacements.GetValueOrDefault(path), appendDecoded: appends.GetValueOrDefault(path));
                    var reread = E5Archive.Parse(path, candidate);
                    if (request.Resource.Kind == ResourceKind.WeatherSpecial && reread.Entries.Any(entry => entry.DecodedLength != 43200 || reread.ReadDecoded(entry.ImageNumber).Length != 43200) ||
                        request.Resource.Kind == ResourceKind.PmapSpecial && reread.Entries.Any(entry => !PmapResourceCodec.IsVerifiedPayload(reread.ReadDecoded(entry.ImageNumber))))
                        throw new InvalidDataException("专用图片候选复读结构验证失败。");
                    files.Add(new(path, candidate, "适配并重建已验证的专用RAW8视觉载荷"));
                }
            else files.Clear();
            warnings.Add("专用容器只替换已验证的视觉载荷；不生成或推测未知头部、动画记录及地形数据。");
            return new(files, rows, warnings);
        }
        catch { foreach (var row in rows) row.Dispose(); throw; }
    }

    internal static void ValidateMapSlot(ProjectProfile project, LogicalImageAsset target, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0 || size.Width % 48 != 0 || size.Height % 48 != 0)
            throw new InvalidDataException("目标战场地图尺寸必须为48的整数倍。");
        var hexPath = project.TryGetResource("Hexzmap.e5") ?? throw new FileNotFoundException("缺少 Hexzmap.e5，不能验证地图网格。");
        var archive = E5Archive.OpenFresh(hexPath);
        var number = checked(target.LogicalId + 1);
        if (number <= 0 || number > archive.Entries.Count || archive.ReadDecoded(number).Length != checked(2 + size.Width / 48 * (size.Height / 48)))
            throw new InvalidDataException("目标地图与Hexzmap地形网格不一致，拒绝猜测写入。");
    }

    internal static Bitmap FitMap(Bitmap source, Size target)
    {
        var bitmap = new Bitmap(target.Width, target.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(Point.Empty, target));
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    internal static byte[] Jpeg(Bitmap bitmap)
    {
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray();
    }
}
