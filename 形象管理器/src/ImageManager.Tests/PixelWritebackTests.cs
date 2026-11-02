using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using ImageManager.App;
using ImageManager.Core;

namespace ImageManager.Tests;

internal static class PixelWritebackTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Color[] Palette = Enumerable.Range(0, 256).Select(index => index switch
    { 0 => Color.Magenta, 1 => Color.Black, 2 => Color.White, 3 => Color.Red, _ => Color.Lime }).ToArray();

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImageManager.PixelWriteback." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var bmp in new[] { false, true }) CheckRsCommit(root, bmp, s: false);
            foreach (var bmp in new[] { false, true }) CheckRsCommit(root, bmp, s: true);
            CheckDifferentPalettesAndArgb();
            CheckTransparentKeyAndNoKey();
            CheckRsPngArgb(root);
            CheckJpegPreview();
            Console.WriteLine("PIXEL_WRITEBACK_OK actual-dialog confirm R/S RAW8/BMP black-erase pencil fill line rectangle transparent-replace first-middle-last shift undo-redo palette-conflict ARGB JPEG-preview");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckRsCommit(string parent, bool bmp, bool s)
    {
        var root = Path.Combine(parent, (s ? "S" : "R") + (bmp ? "BMP" : "RAW"));
        Directory.CreateDirectory(root);
        var paletteBytes = ImageRendering.BuildPaletteBgra(Palette);
        File.WriteAllBytes(Path.Combine(root, "tsb"), paletteBytes);
        var strips = s ? new[] { ("Unit_mov.e5", 48, 48, 11), ("Unit_atk.e5", 64, 64, 12), ("Unit_spc.e5", 48, 48, 5) }
            : new[] { ("Pmapobj.e5", 48, 64, 20), ("Pmapobj.e5", 48, 64, 20) };
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = new List<FrameDescriptor>();
        var sections = new List<PixelEditorSection>();
        var y = 0;
        for (var part = 0; part < strips.Length; part++)
        {
            var (name, width, height, count) = strips[part];
            var path = Path.Combine(root, name); paths[name] = path;
            var payload = bmp ? IndexedBmp(width, height * count, Palette, 1) : Enumerable.Repeat((byte)1, width * height * count).Concat(new byte[] { 0x71, 0x72 }).ToArray();
            if (s) File.WriteAllBytes(path, Archive([payload]));
            else if (part == 0) File.WriteAllBytes(path, Archive([payload, payload]));
            var number = s ? 1 : part + 1;
            for (var frame = 0; frame < count; frame++) descriptors.Add(new(name, path, number, frame, 1,
                s ? new[] { "mov", "atk", "spc" }[part] : "r-action", s ? "none" : part == 0 ? "front" : "back", width, height));
            sections.Add(new("part" + part, new(0, y, width, height * count), height, path, Palette, bmp ? "BMP8" : "RAW8", 0));
            y += height * count;
        }
        var project = new ProjectProfile(root, "pixel-test", ProjectVersion.Ccz65, false, paths, []);
        var kind = s ? ResourceKind.SComposite : ResourceKind.RComposite;
        var asset = new LogicalImageAsset(s ? "SComposite" : "Pmapobj.e5", 0, "fixture", s ? ImageCategory.CharacterS : ImageCategory.CharacterR,
            kind, descriptors, [], false, false, "");
        using var canvasSource = new Bitmap(s ? 64 : 48, y, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvasSource)) g.Clear(Color.Black);
        using var editor = new PixelEditorDialog(canvasSource, "writeback", sections);
        Exception? uiFailure = null;
        editor.Shown += (_, _) => editor.BeginInvoke((Action)(() =>
        {
            try
            {
                var canvas = ControlNamed(editor, "PixelCanvas");
                Set(canvas, "Zoom", 1d);
                SetTool(canvas, "Eraser");
                // Erase a black pixel in every action's last frame.
                foreach (var section in sections) ClickPixel(canvas, new(3, section.Bounds.Bottom - section.FrameHeight + 4));
                SetTool(canvas, "Pencil");
                Set(canvas, "DrawingColor", Color.White);
                ClickPixel(canvas, new(4, sections[0].FrameHeight + 5));
                Set(canvas, "DrawingColor", Color.Lime); ClickPixel(canvas, new(10, 10));
                Invoke(canvas, "ReplaceColors", (object)new (Color, Color)[] { (Color.Lime, Color.Transparent) });
                Set(canvas, "DrawingColor", Color.White); SetTool(canvas, "Line");
                DragPixel(canvas, new(15, 10), new(19, 10));
                var middle = (strips[0].Item4 / 2) * sections[0].FrameHeight;
                SetTool(canvas, "Rectangle"); DragPixel(canvas, new(10, middle + 10), new(14, middle + 14));
                SetTool(canvas, "Fill"); Set(canvas, "DrawingColor", Color.Red); ClickPixel(canvas, new(12, middle + 12));
                Invoke(canvas, "Undo");
                using (var undone = (Bitmap)Invoke(canvas, "Snapshot")!)
                    Require(undone.GetPixel(12, middle + 12).ToArgb() == Color.Black.ToArgb(), "填充撤回没有恢复原色");
                Invoke(canvas, "Redo");
                using (var redone = (Bitmap)Invoke(canvas, "Snapshot")!)
                    Require(redone.GetPixel(12, middle + 12).ToArgb() == Color.Red.ToArgb(), "填充重做没有恢复目标色");
                Buttons(editor).Single(button => button.Text.StartsWith("写回当前", StringComparison.Ordinal)).PerformClick();
            }
            catch (Exception error) { uiFailure = error; editor.Close(); }
        }));
        Require(editor.ShowDialog() == DialogResult.OK && uiFailure == null, "实际编辑器确认失败：" + uiFailure);
        Require(editor.ResultBitmap != null, "确认写回没有返回结果");
        var frames = descriptors.Select(target =>
        {
            var section = sections[s ? Array.FindIndex(strips, item => item.Item1 == target.SourceKey) : target.ImageNumber - 1];
            return new DecodedFrame(target, ImageRendering.Crop(editor.ResultBitmap!,
                new Rectangle(0, section.Bounds.Y + target.FrameIndex * target.Height, target.Width, target.Height)));
        }).ToArray();
        using var alignment = new AllFramesDialog("frame-offset", frames);
        alignment.Shown += (_, _) => alignment.BeginInvoke((Action)(() =>
        {
            Buttons(alignment).Single(button => button.Text == "→").PerformClick();
            Buttons(alignment).Single(button => button.Text == "撤回").PerformClick();
            Buttons(alignment).Single(button => button.Text == "重做").PerformClick();
            Buttons(alignment).Single(button => button.Text == "保存写回").PerformClick();
        }));
        try
        {
            Require(alignment.ShowDialog() == DialogResult.OK, "全部帧位移确认失败");
            var snapshot = MutationPlanningSnapshot.Capture(paths.Values);
            var plan = snapshot.Complete(new RsImportService().PlanImport(project, asset, alignment.ResultFrames)) with
            { ExpectedImages = alignment.ResultFrames.Select(frame => MutationImageExpectation.FromBitmap(frame.Descriptor, kind, frame.Bitmap,
                transparentColor: Palette[0])).ToArray() };
            var history = new HistoryService(Path.Combine(root, "history"));
            var result = new ImageMutationService(history).Commit(project, plan);
            Require(result.Outcome == MutationOutcome.Applied, "UI结果未落盘：" + string.Join("；", result.Diagnostics));
            for (var part = 0; part < strips.Length; part++)
            {
                var (name, width, height, count) = strips[part];
                var payload = E5Archive.OpenFresh(paths[name]).ReadDecoded(s ? 1 : part + 1);
                byte[] indexes;
                if (bmp) { Require(BmpPayloadService.TryGetIndexes(payload, out indexes), "写回改变BMP位深"); }
                else indexes = payload;
                Require(indexes[(count - 1) * height * width + 4 * width + 3] == 0, "末帧黑色橡皮没有真正写回");
                Require(indexes[1] == 1, "未编辑像素被改动");
                if (part == 0)
                {
                    Require(indexes[height * width + 5 * width + 4] == 2, "非首帧白色未保存");
                    Require(indexes[0] == 0, "全部帧右移未产生透明边界");
                    Require(indexes[10 * width + 11] == 0, "首帧透明换色或位移未保存");
                    Require(indexes[10 * width + 18] == 2 && indexes[10 * width + 15] == 1, "首帧直线或右移1像素未保存");
                    var middle = (count / 2) * height;
                    Require(indexes[(middle + 10) * width + 10] == 2 && indexes[(middle + 12) * width + 12] == 3,
                        "中间帧矩形边界或填充未保存");
                }
                if (!bmp) Require(payload[^2] == 0x71 && payload[^1] == 0x72, "RAW尾部被改动");
            }
            Require(history.Undo(project, result.HistoryId!).Success && history.Redo(project, result.HistoryId!).Success, "真实写回撤回/重做失败");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void CheckDifferentPalettesAndArgb()
    {
        using var source = new Bitmap(4, 2, PixelFormat.Format32bppArgb);
        source.SetPixel(0, 0, Color.Black); source.SetPixel(2, 0, Color.Black);
        var alpha = Color.FromArgb(128, 198, 44, 68); source.SetPixel(1, 1, alpha);
        using var editor = new PixelEditorDialog(source, "palettes",
            [new("first", new(0, 0, 2, 2), 0, "a", [Color.Magenta, Color.Black, Color.Red], "BMP8", 0),
             new("second", new(2, 0, 2, 2), 0, "b", [Color.Magenta, Color.Black, Color.Blue], "BMP8", 0)]);
        var canvas = ControlNamed(editor, "PixelCanvas");
        Invoke(canvas, "ReplaceColors", (object)new (Color, Color)[] { (Color.Black, Color.Red) });
        using (var unchanged = (Bitmap)Invoke(canvas, "Snapshot")!)
        {
            Require(unchanged.GetPixel(0, 0).ToArgb() == Color.Black.ToArgb() && unchanged.GetPixel(2, 0).ToArgb() == Color.Black.ToArgb(), "跨色表换色发生部分写入/静默近似");
            Require(unchanged.GetPixel(1, 1).ToArgb() == alpha.ToArgb(), "编辑器初始化/Snapshot改变半透明ARGB");
        }
        Require(ControlNamed(editor, "PixelEditStatus").Text.Contains("分区", StringComparison.Ordinal), "跨色表失败未解释分区操作");
        ((CheckBox)ControlNamed(editor, "ReplacementCurrentSectionOnly")).Checked = true;
        Invoke(canvas, "ReplaceColors", (object)new (Color, Color)[] { (Color.Black, Color.Red) });
        using (var partition = (Bitmap)Invoke(canvas, "Snapshot")!)
            Require(partition.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb() && partition.GetPixel(2, 0).ToArgb() == Color.Black.ToArgb(),
                "选择仅当前区段后未正确分区换色");
        using var crop = ImageRendering.Crop(source, new(1, 1, 1, 1));
        Require(crop.GetPixel(0, 0).ToArgb() == alpha.ToArgb(), "裁剪改变半透明ARGB");
        var pixels = BmpPayloadService.ReadPixels(crop);
        Require(pixels.SequenceEqual(new byte[] { 68, 44, 198, 128 }), "ReadPixels仍经GDI舍入");
        using var plain = new PixelEditorDialog(source, "ARGB");
        var plainCanvas = ControlNamed(plain, "PixelCanvas");
        Set(plainCanvas, "Zoom", 1d); Set(plainCanvas, "DrawingColor", Color.Red); ClickPixel(plainCanvas, new(3, 1));
        Invoke(plainCanvas, "Undo"); Invoke(plainCanvas, "Redo");
        using var after = (Bitmap)Invoke(plainCanvas, "Snapshot")!;
        Require(after.GetPixel(1, 1).ToArgb() == alpha.ToArgb(), "撤回/重做改变未编辑半透明像素");
    }

    private static void CheckTransparentKeyAndNoKey()
    {
        using var edited = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        edited.SetPixel(0, 0, Color.FromArgb(0, 10, 20, 30));
        var alternate = new[] { Color.White, Color.Black, Color.Magenta };
        var bmp = IndexedBmp(1, 1, alternate, 1);
        var rewritten = BmpPayloadService.Rebuild(bmp, edited);
        Require(BmpPayloadService.TryGetIndexes(rewritten, out var indexes) && indexes[0] == 2, "BMP透明键不在0号时写错索引");
        var refused = false;
        try { _ = BmpPayloadService.Rebuild(IndexedBmp(1, 1, [Color.White, Color.Black], 1), edited); }
        catch (InvalidDataException) { refused = true; }
        Require(refused, "没有透明键色的BMP悄悄擦成普通0号颜色");
    }

    private static void CheckRsPngArgb(string parent)
    {
        var root = Path.Combine(parent, "R-PNG-ARGB"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Pmapobj.e5");
        var alpha = Color.FromArgb(128, 198, 44, 68);
        using var strip = new Bitmap(48, 1280, PixelFormat.Format32bppArgb);
        strip.SetPixel(2, 2, alpha); strip.SetPixel(3, 3, Color.Magenta);
        using var stream = new MemoryStream(); strip.Save(stream, ImageFormat.Png);
        File.WriteAllBytes(path, Archive([stream.ToArray(), stream.ToArray()]));
        var project = new ProjectProfile(root, "PNG", ProjectVersion.Ccz65, false, new Dictionary<string, string> { ["Pmapobj.e5"] = path }, []);
        var resource = new ImageResourceDescriptor("Pmapobj.e5", "R", ImageCategory.CharacterR, ResourceKind.RComposite, [path], 2, true, true, false, "", "");
        var asset = new RResourceCodec().ReadAssets(project, resource).Single();
        var frames = new RResourceCodec().DecodeFrames(project, asset);
        try
        {
            var target = frames.First();
            using var editor = new PixelEditorDialog(target.Bitmap, "PNG ARGB");
            editor.Shown += (_, _) => editor.BeginInvoke((Action)(() =>
            {
                var canvas = ControlNamed(editor, "PixelCanvas"); Set(canvas, "Zoom", 1d); Set(canvas, "DrawingColor", Color.White);
                ClickPixel(canvas, new(4, 4)); Buttons(editor).Single(button => button.Text.StartsWith("写回当前", StringComparison.Ordinal)).PerformClick();
            }));
            Require(editor.ShowDialog() == DialogResult.OK && editor.ResultBitmap != null, "PNG实际编辑确认失败");
            var plan = new RsImportService().PlanPixelEdit(project, asset, target.Descriptor, editor.ResultBitmap!) with
            { ExpectedImages = [MutationImageExpectation.FromBitmap(target.Descriptor, asset.Kind, editor.ResultBitmap!)] };
            var saved = new ImageMutationService(new HistoryService(Path.Combine(root, "history"))).Commit(project, plan);
            Require(saved.Success, "PNG R实际写回失败：" + string.Join("；", saved.Diagnostics));
            using var reread = BmpPayloadService.Decode(E5Archive.OpenFresh(path).ReadDecoded(1));
            Require(reread.GetPixel(2, 2).ToArgb() == alpha.ToArgb(), "R/S拼接/写回改变未编辑的半透明ARGB");
            Require(reread.GetPixel(3, 3).ToArgb() == Color.Magenta.ToArgb(), "R/S PNG不透明洋红被当作键色透明化");
            Require(reread.GetPixel(4, 4).ToArgb() == Color.White.ToArgb(), "PNG编辑像素未写回");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void CheckJpegPreview()
    {
        using var source = new Bitmap(17, 9, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++) source.SetPixel(x, y, (x + y) % 2 == 0 ? Color.Red : Color.Lime);
        using var stream = new MemoryStream(); source.Save(stream, ImageFormat.Jpeg);
        using var decoded = BmpPayloadService.Decode(stream.ToArray());
        Require(!BmpPayloadService.ReadPixels(decoded).SequenceEqual(BmpPayloadService.ReadPixels(source)), "JPEG预览夹具未产生有损差异");
        foreach (var accept in new[] { false, true })
        {
            using var preview = new EncodedImagePreviewDialog(decoded, "JPEG确认");
            var picture = (PictureBox)ControlNamed(preview, "EncodedImagePreview");
            Require(BmpPayloadService.ReadPixels((Bitmap)picture.Image!).SequenceEqual(BmpPayloadService.ReadPixels(decoded)),
                "JPEG预览显示了编码前画布或改变了实际候选颜色");
            preview.Shown += (_, _) => preview.BeginInvoke((Action)(() =>
                ((Button)ControlNamed(preview, accept ? "ConfirmEncodedImage" : "CancelEncodedImage")).PerformClick()));
            Require(preview.ShowDialog() == (accept ? DialogResult.OK : DialogResult.Cancel), "JPEG编码预览不能确认或取消");
        }
    }

    private static byte[] IndexedBmp(int width, int height, IReadOnlyList<Color> palette, byte index)
    {
        var stride = (width + 3) & ~3; var offset = 54 + palette.Count * 4;
        var bytes = new byte[offset + stride * height + 2]; bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length - 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), offset); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(46), palette.Count);
        for (var p = 0; p < palette.Count; p++) { bytes[54 + p * 4] = palette[p].B; bytes[55 + p * 4] = palette[p].G; bytes[56 + p * 4] = palette[p].R; }
        for (var y = 0; y < height; y++) bytes.AsSpan(offset + y * stride, width).Fill(index);
        bytes[^2] = 0x71; bytes[^1] = 0x72; return bytes;
    }

    private static byte[] Archive(IReadOnlyList<byte[]> payloads)
    {
        var data = 0x110 + payloads.Count * 12 + 4; var bytes = new byte[data + payloads.Sum(payload => payload.Length)];
        for (var i = 0; i < payloads.Count; i++)
        {
            var at = 0x110 + i * 12;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at), payloads[i].Length); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + 4), payloads[i].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + 8), data); payloads[i].CopyTo(bytes, data); data += payloads[i].Length;
        }
        return bytes;
    }

    private static void SetTool(Control canvas, string name) => Set(canvas, "Tool", Enum.Parse(canvas.GetType().GetProperty("Tool")!.PropertyType, name));
    private static void ClickPixel(Control canvas, Point point)
    { Invoke(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)); Invoke(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)); }
    private static void DragPixel(Control canvas, Point start, Point end)
    { Invoke(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)); Invoke(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, end.X, end.Y, 0)); Invoke(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0)); }
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Members)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);
    private static Control ControlNamed(Control root, string name) => Children(root).Single(control => control.Name == name);
    private static IEnumerable<Button> Buttons(Control root) => Children(root).OfType<Button>();
    private static IEnumerable<Control> Children(Control root) { foreach (Control child in root.Controls) { yield return child; foreach (var descendant in Children(child)) yield return descendant; } }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
