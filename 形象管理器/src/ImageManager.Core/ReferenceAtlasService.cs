using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ImageManager.Core;

public sealed class ReferenceAtlasService : IReferenceAtlasService
{
    private readonly IReferenceAtlasLayoutPlanner _layouts;
    private readonly IAiAtlasNormalizer _normalizer;

    public ReferenceAtlasService(IReferenceAtlasLayoutPlanner? layouts = null, IAiAtlasNormalizer? normalizer = null)
    {
        _layouts = layouts ?? new ReferenceAtlasLayoutPlanner();
        _normalizer = normalizer ?? new AiAtlasNormalizer(_layouts);
    }

    public ReferenceAtlasExportResult Export(string destinationDirectory, LogicalImageAsset asset,
        IReadOnlyList<DecodedFrame> frames, Color keyColor)
        => ExportCore(destinationDirectory, asset, frames, keyColor,
            asset.Kind == ResourceKind.RComposite ? $"R{asset.LogicalId:000}" : SResourceExchangeNaming.BaseName(asset),
            new ReferenceAtlasExportOptions { IncludeTransparent = true });

    public ReferenceAtlasExportResult Export(string destinationDirectory, ReferenceAtlasContext context,
        Color keyColor, ReferenceAtlasExportOptions? options = null)
        => ExportCore(destinationDirectory, context.Asset, context.Frames, keyColor,
            context.BaseName, options ?? new ReferenceAtlasExportOptions());

    private ReferenceAtlasExportResult ExportCore(string destinationDirectory, LogicalImageAsset asset,
        IReadOnlyList<DecodedFrame> frames, Color keyColor, string baseName, ReferenceAtlasExportOptions options)
    {
        if (asset.Kind is not (ResourceKind.RComposite or ResourceKind.SComposite))
            throw new InvalidOperationException("参考总图只适用于 R/S 逻辑形象。");
        if (frames.Count == 0) throw new InvalidOperationException("没有可导出的帧。");
        if (asset.Kind == ResourceKind.SComposite)
            SResourceExchangeNaming.ValidateFrameSet(asset, frames.Select(frame => frame.Descriptor).ToArray());
        if (options.Scale is not (1 or 2 or 4))
            throw new ArgumentOutOfRangeException(nameof(options), "参考图倍率只支持 1、2、4。");
        if (asset.Kind == ResourceKind.SComposite)
            destinationDirectory = Path.Combine(destinationDirectory, SResourceExchangeNaming.Category(asset));
        Directory.CreateDirectory(destinationDirectory);

        var layout = _layouts.Plan(asset, frames);
        using var transparent = new Bitmap(layout.CanvasSize, layout.CanvasSize, PixelFormat.Format32bppArgb);
        using var keyed = new Bitmap(layout.CanvasSize, layout.CanvasSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(transparent))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            foreach (var placement in layout.Placements)
            {
                using var frame = MakeKeyTransparent(placement.Frame.Bitmap, keyColor);
                graphics.DrawImageUnscaled(frame, placement.Bounds.X, placement.Bounds.Y);
            }
        }
        using (var graphics = Graphics.FromImage(keyed))
        {
            graphics.Clear(Color.FromArgb(255, keyColor.R, keyColor.G, keyColor.B));
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.DrawImageUnscaled(transparent, 0, 0);
        }

        var pngPath = Path.Combine(destinationDirectory, baseName + ".png");
        var transparentPath = options.IncludeTransparent
            ? Path.Combine(destinationDirectory, baseName + ".transparent.png") : string.Empty;
        SaveScaledPng(keyed, pngPath, options.Scale);
        if (options.IncludeTransparent) SaveScaledPng(transparent, transparentPath, options.Scale);
        var promptPath = UpdatePromptDocument(destinationDirectory, asset, layout, keyColor, baseName, options);
        return new ReferenceAtlasExportResult(pngPath, transparentPath, promptPath);
    }

    public AiAtlasNormalizationResult Normalize(string pngPath, LogicalImageAsset asset,
        IReadOnlyList<DecodedFrame> referenceFrames, AiAtlasNormalizationOptions options)
        => _normalizer.Normalize(pngPath, asset, referenceFrames, options);

    public AiAtlasNormalizationResult Normalize(string pngPath, ReferenceAtlasContext context,
        AiAtlasNormalizationOptions options, CancellationToken cancellationToken = default)
        => _normalizer.Normalize(pngPath, context.Asset, context.Frames, options, cancellationToken);

    public static Color ResolveKeyColor(IReadOnlyList<Color> palette)
    {
        var preferred = palette.FirstOrDefault(color => color.G == 0 && color.B == 255 && color.R is 247 or 255);
        return preferred.IsEmpty ? Color.Magenta : Color.FromArgb(255, preferred.R, preferred.G, preferred.B);
    }

    public static string PixelHash(Bitmap bitmap)
    {
        using var normalized = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(normalized)) graphics.DrawImageUnscaled(bitmap, 0, 0);
        var data = normalized.LockBits(new Rectangle(0, 0, normalized.Width, normalized.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowLength = checked(normalized.Width * 4);
            using var stream = new MemoryStream(checked(rowLength * normalized.Height));
            var row = new byte[rowLength];
            for (var y = 0; y < normalized.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                stream.Write(row);
            }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }
        finally { normalized.UnlockBits(data); }
    }

    private static Bitmap MakeKeyTransparent(Bitmap source, Color keyColor)
    {
        var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var color = source.GetPixel(x, y);
            var key = color.G == 0 && color.B == 255 && color.R is 247 or 255 ||
                      color.R == keyColor.R && color.G == keyColor.G && color.B == keyColor.B;
            output.SetPixel(x, y, color.A == 0 || key ? Color.Transparent : color);
        }
        return output;
    }

    private static void SavePngAtomically(Bitmap bitmap, string target)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            bitmap.Save(temporary, ImageFormat.Png);
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void SaveScaledPng(Bitmap bitmap, string path, int scale)
    {
        if (scale == 1) { SavePngAtomically(bitmap, path); return; }
        using var enlarged = new Bitmap(bitmap.Width * scale, bitmap.Height * scale, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(enlarged))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.DrawImage(bitmap, new Rectangle(0, 0, enlarged.Width, enlarged.Height),
                new Rectangle(Point.Empty, bitmap.Size), GraphicsUnit.Pixel);
        }
        SavePngAtomically(enlarged, path);
    }

    private static string UpdatePromptDocument(string directory, LogicalImageAsset asset,
        ReferenceAtlasLayoutPlan layout, Color keyColor, string baseName, ReferenceAtlasExportOptions options)
    {
        var start = $"<!-- asset:{baseName}:start -->";
        var end = $"<!-- asset:{baseName}:end -->";
        var target = Path.Combine(directory, "形象图提示词.md");
        var text = File.Exists(target) ? File.ReadAllText(target) : string.Empty;
        if (!text.Contains("<!-- image-manager-atlas-prompt-v5 -->", StringComparison.Ordinal))
            text = PromptHeader(keyColor);

        var entry = BuildPromptEntry(start, end, baseName, layout, keyColor, options);
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        var endIndex = startIndex < 0 ? -1 : text.IndexOf(end, startIndex, StringComparison.Ordinal);
        if (startIndex >= 0 && endIndex >= 0)
            text = text[..startIndex] + entry + text[(endIndex + end.Length)..];
        else
            text = text.TrimEnd() + Environment.NewLine + Environment.NewLine + entry;

        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text.TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temporary, target, true);
        return target;
    }

    private static string PromptHeader(Color keyColor)
    {
        var hex = $"#{keyColor.R:X2}{keyColor.G:X2}{keyColor.B:X2}";
        return """
<!-- image-manager-atlas-prompt-v5 -->
# 形象图 AI 修改提示词

## 必须遵守

- 优先把无 `.transparent` 后缀的 PNG 作为动作格式参考图；它使用不透明键色背景，避免 AI 平台把透明层压成黑色。
- 透明版只用于人工观察，不要同时把两版作为两个不同角色或两套动作理解。
- 当前文件只是一套完整动作，不要额外生成进阶版本、其他人物或其他套动作。
- 保持画布尺寸、格子数量、动作顺序、方向和空白保留格不变；每格只能出现对应的一帧。
- 不得合并帧、交换行列、裁掉攻击特效或在空白保留格中新增内容。
- 输出必须是无损 PNG，宽高必须与本次主参考图完全一致。禁止擅自输出 1024/1536 等平台预设画布、裁切、添加外边距或改变行列比例。
- 背景应保持本项目键色，不能改成黑色、白色、场景或棋盘格。若平台强制改变背景，导入时必须检查蒙版。
- 同一人物各方向和动作应保持造型、装备、配色、光源及像素风格一致。
- 攻击和特技可以使用格内空间，但不得进入相邻格。
- 只修改要求中的人物外观、服装、武器和配色；严格沿用原参考图每一格的动作、朝向、占格比例、脚底基线、轮廓粗细和低像素画法。
- 像素边缘必须硬朗，保持原生 48×48、48×64 或 64×64 像素的细节尺度；整数倍参考图中，一个原生像素必须对应一个同色方块。禁止抗锯齿、模糊、渐变、插值、景深、柔光、高清插画和微小纹理。
- 格间空隙必须保持纯键色，不得绘制网格线、文字、编号、水印、边框或标注。不得镜像、复制同一姿态充当不同动作。
- 导回默认按格位定位并只做一次最近邻还原，不会为了贴合原人物轮廓而再次拉伸 AI 人物；格位漂移会自动配准，低置信度结果必须人工确认。
- 索引图片导回时使用目标原有调色板，默认不抖动，颜色损失会在确认窗口中单独统计；真彩图片保持原有真彩存储。

## S 形象统一语义

- 48x48 第 1 行 mov0..5：正面移动两帧、背面移动两帧、侧面移动两帧。
- 48x48 第 2 行 mov6..10：正面受击、背面受击、侧面受击、倒地、退场；末格为空。
- 48x48 第 3 行 spc0..4：特技五帧；末格为空。
- 64x64 三行分别为正面 atk0..3、背面 atk4..7、侧面 atk8..11。

当前项目键色：KEY_COLOR
""".Replace("KEY_COLOR", hex, StringComparison.Ordinal);
    }

    private static string BuildPromptEntry(string start, string end, string baseName,
        ReferenceAtlasLayoutPlan layout, Color keyColor, ReferenceAtlasExportOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine(start).AppendLine($"## {baseName}").AppendLine();
        builder.AppendLine($"- AI 主文件：`{baseName}.png`");
        if (options.IncludeTransparent) builder.AppendLine($"- 透明观察版：`{baseName}.transparent.png`；它与主文件是同一套动作。");
        var exportedSize = layout.CanvasSize * options.Scale;
        builder.AppendLine($"- 必须输出：`{exportedSize}×{exportedSize}` PNG；不得改变尺寸或长宽比。");
        builder.AppendLine($"- 原生画布：`{layout.CanvasSize}×{layout.CanvasSize}`；放大倍率：`{options.Scale}`；每个原生像素必须是 `{options.Scale}×{options.Scale}` 的同色方块。");
        builder.AppendLine($"- 帧数：`{layout.Placements.Count}`；键色：`#{keyColor.R:X2}{keyColor.G:X2}{keyColor.B:X2}`；下表坐标已按输出倍率换算。");
        foreach (var section in layout.Sections)
            builder.AppendLine($"- {section.Name.Replace(ChineseTurn(section.Turn) + "转 ", string.Empty, StringComparison.Ordinal)}：({section.X * options.Scale},{section.Y * options.Scale}) {section.Width * options.Scale}×{section.Height * options.Scale}");
        builder.AppendLine().AppendLine(layout.TurnCount > 1
            ? "| 套号 | 动作 | 方向 | 帧号 | 输出矩形 |" : "| 动作 | 方向 | 帧号 | 输出矩形 |");
        builder.AppendLine(layout.TurnCount > 1 ? "| ---: | --- | --- | ---: | --- |" : "| --- | --- | ---: | --- |");
        foreach (var placement in layout.Placements)
        {
            var frame = placement.Frame.Descriptor;
            builder.AppendLine($"| {(layout.TurnCount > 1 ? frame.Turn + " | " : string.Empty)}{frame.Action} | {frame.Direction} | {frame.FrameIndex} | " +
                               $"({placement.Bounds.X * options.Scale},{placement.Bounds.Y * options.Scale}) {placement.Bounds.Width * options.Scale}×{placement.Bounds.Height * options.Scale} |");
        }
        builder.AppendLine(end);
        return builder.ToString().TrimEnd();
    }

    private static string ChineseTurn(int turn) => turn switch
    {
        1 => "一",
        2 => "二",
        3 => "三",
        _ => turn.ToString()
    };
}
