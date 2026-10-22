using System.Drawing;

namespace ImageManager.Core;

/// <summary>Builds reviewable candidates for non R/S external raster imports.</summary>
internal static class RasterImageFileImport
{
    internal static ImageImportBuildResult Build(ImageImportRequest request, CancellationToken token)
    {
        var builder = new Builder(request, token);
        try { return builder.Build(); }
        catch { builder.DisposeRows(); throw; }
    }

    private sealed class E5Work(string path)
    {
        internal readonly E5Archive Archive = E5Archive.OpenFresh(path);
        internal readonly Dictionary<int, byte[]> Replacements = [];
        internal readonly List<byte[]> Appends = [];
        internal bool AppendPairs;
    }

    private sealed class Builder(ImageImportRequest request, CancellationToken token)
    {
        private readonly List<ImageImportPreviewRow> _rows = [];
        private readonly List<string> _warnings = [];
        private readonly Dictionary<string, E5Work> _e5 = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<DllImportEncoding.Update>> _dll = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<DllBitmapRecord>> _dllRecords = new(StringComparer.OrdinalIgnoreCase);

        internal void DisposeRows() { foreach (var row in _rows) row.Dispose(); }

        internal ImageImportBuildResult Build()
        {
            var assignments = request.Assignments.Where(item => item.Included).ToArray();
            if (assignments.Length == 0) return new([], [], []);
            var paired = request.Resource.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon;
            if (paired)
            {
                var groups = assignments.GroupBy(item => request.Operation == ImageImportOperation.Replace
                    ? item.Target?.StablePhysicalId ?? "missing-target:" + item.Group : "append:" + item.Group);
                foreach (var group in groups)
                {
                    token.ThrowIfCancellationRequested();
                    Attempt(group.ToArray(), () => Icon(group.ToArray()));
                }
            }
            else foreach (var assignment in assignments)
            {
                token.ThrowIfCancellationRequested();
                Attempt([assignment], () =>
                {
                    if (!assignment.Role.Equals("image", StringComparison.OrdinalIgnoreCase) &&
                        !(request.Resource.Kind == ResourceKind.FaceComposite && assignment.Role.StartsWith("expression:", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("普通图片的素材角色必须为 image；头像0须明确指定表情编号。");
                    using var source = ImageImportRasterNormalization.Load(assignment.SourcePath, token);
                    if (request.Resource.Kind == ResourceKind.FaceComposite) Face(assignment, source);
                    else if (request.Resource.Kind == ResourceKind.DllBitmap) NativeDll(assignment, source);
                    else StandardE5(assignment, source);
                });
            }
            if (_rows.Any(row => row.Error != null)) return new([], _rows, _warnings);
            var files = new List<MutationFile>();
            try
            {
                foreach (var item in _e5)
                {
                    token.ThrowIfCancellationRequested();
                    var work = item.Value;
                    var insert = work.AppendPairs && work.Archive.Entries.Count % 2 != 0 && work.Appends.Count > 0
                        ? new Dictionary<int, IReadOnlyList<byte[]>> { [work.Archive.Entries.Count] = work.Appends } : null;
                    var candidate = work.Archive.Rebuild(replacementDecoded: work.Replacements,
                        appendDecoded: insert == null ? work.Appends : null, insertBeforeDecoded: insert);
                    files.Add(new MutationFile(item.Key, candidate, "按文件导入预览重建 E5 图片载荷"));
                }
                foreach (var item in _dll)
                {
                    token.ThrowIfCancellationRequested();
                    files.Add(new MutationFile(item.Key, DllImportEncoding.BuildCandidate(item.Key, item.Value, token),
                        "按文件导入预览更新全部目标 RT_BITMAP 语言变体"));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsInputFailure(ex))
            {
                _rows.Add(new ImageImportPreviewRow(request.Resource.DisplayName, "构建候选", "", Size.Empty, Size.Empty,
                    "", "", "候选复读未通过；整个批次不可提交。", null, null, ex.Message));
                return new([], _rows, _warnings);
            }
            return new(files, _rows, _warnings.Distinct().ToArray());
        }

        private void Attempt(IReadOnlyList<ImageImportAssignment> assignments, Action action)
        {
            try { action(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsInputFailure(ex))
            {
                foreach (var assignment in assignments)
                    _rows.Add(new ImageImportPreviewRow(assignment.Target?.DisplayName ?? "末尾新增", assignment.Role,
                        assignment.SourcePath, Size.Empty, Size.Empty, "", "", "该映射未生成可提交候选。", null, null, ex.Message));
            }
        }

        private static bool IsInputFailure(Exception ex)
            => ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException
                or System.Runtime.InteropServices.ExternalException or OverflowException;

        private E5Work E5(string path)
        {
            if (!_e5.TryGetValue(path, out var value)) _e5[path] = value = new E5Work(path);
            return value;
        }

        private IReadOnlyList<DllBitmapRecord> DllRecords(string path)
        {
            if (!_dllRecords.TryGetValue(path, out var records)) _dllRecords[path] = records = DllBitmapReader.Read(path, true);
            return records;
        }

        private List<DllImportEncoding.Update> DllUpdates(string path)
        {
            if (!_dll.TryGetValue(path, out var updates)) _dll[path] = updates = [];
            return updates;
        }

        private static LogicalImageAsset Target(ImageImportAssignment assignment)
            => assignment.Target ?? throw new InvalidDataException("替换素材缺少明确的目标图片。");

        private static void Replace(E5Work work, int number, byte[] payload)
        {
            if (!work.Replacements.TryAdd(number, payload)) throw new InvalidDataException($"E5 图号 #{number} 被重复分配。");
        }

        private void StandardE5(ImageImportAssignment assignment, ImageImportRasterNormalization.Source source)
        {
            if (request.Resource.Kind != ResourceKind.E5Indexed || request.Resource.Category is ImageCategory.CharacterR
                    or ImageCategory.CharacterS or ImageCategory.StrategyAnimation || request.Resource.Paths.Count != 1)
                throw new InvalidDataException("该资源含专用动作或容器结构，请使用对应专用导入入口。");
            var path = request.Resource.Paths.Single();
            var work = E5(path);
            byte[] original;
            FrameDescriptor frame;
            string label;
            var existing = request.Operation == ImageImportOperation.Replace;
            if (request.Operation == ImageImportOperation.Replace)
            {
                var target = Target(assignment);
                frame = target.Frames.Single();
                if (!Path.GetFullPath(frame.SourcePath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("图片目标不属于当前资源文件。");
                original = work.Archive.ReadDecoded(frame.ImageNumber);
                label = target.DisplayName;
            }
            else
            {
                if (work.Archive.Entries.Count == 0)
                    throw new InvalidDataException("空 E5 档案尚无经过验证的目录结构，无法新增普通图片。");
                var number = work.Archive.Entries.Count;
                original = work.Archive.ReadDecoded(number);
                frame = new FrameDescriptor(request.Resource.Key, path, number, 0, 1, "image", "none", 0, 0);
                label = $"{request.Resource.DisplayName} #{work.Archive.Entries.Count + work.Appends.Count + 1}";
            }

            var standard = ImageRendering.IsStandardImage(original);
            Size size;
            string format;
            if (standard)
            {
                ImageImportRasterNormalization.RequireNoUnknownTail(original);
                using var decoded = ImageImportRasterNormalization.DecodeArgb(original);
                size = decoded.Size;
                format = ImageImportRasterNormalization.Format(original);
            }
            else
            {
                size = PlainRawSize(frame, original, request.Resource.Category);
                var pixels = checked(size.Width * size.Height);
                if (original.Length != pixels && original.Length != pixels + 2)
                    throw new InvalidDataException("普通 RAW 静态图仅支持已登记几何的像素区，以及0或2字节合法尾部；专用结构请使用对应入口。");
                format = "RAW8";
            }
            using var normalized = ImageImportRasterNormalization.Canvas(source.Bitmap, size);
            var payload = standard ? ImageImportTargetEncoding.Standard(original, normalized, token)
                : ImageImportTargetEncoding.Raw8(original, size.Width, size.Height, normalized,
                    new PaletteService().RequireWritePalette(request.Project), token);
            if (existing) Replace(work, frame.ImageNumber, payload);
            else work.Appends.Add(payload);
            var notes = $"按{(existing ? "目标" : "末尾已验证同类模板")} {size.Width}×{size.Height} 画布缩放填满，转换为 {format}；未自动修改游戏引用。";
            if (format == "RAW8")
            {
                notes += " 按项目调色板量化，完全透明使用索引0，非零半透明按RGB实色量化，并保留合法尾部。";
                _warnings.Add("普通 RAW 静态图按项目调色板量化，请检查候选预览颜色。");
            }
            if (format == "JPG") { notes += " JPG 为有损存储且不保存透明通道。"; _warnings.Add("目标 JPG 为有损存储且没有透明通道，请检查候选预览。"); }
            if (format == "BMP") notes += " 保留目标位深、调色板与行布局；完全透明转为目标透明索引或洋红键色，非零半透明按RGB实色写入。";
            var before = !existing ? null : standard ? ImageImportRasterNormalization.DecodeArgb(original)
                : ImageRendering.DecodeIndexed(original, size.Width, size.Height, new PaletteService().Load(request.Project));
            var after = standard ? ImageImportRasterNormalization.DecodeArgb(payload)
                : ImageRendering.DecodeIndexed(payload, size.Width, size.Height, new PaletteService().Load(request.Project));
            _rows.Add(new ImageImportPreviewRow(label, "image", assignment.SourcePath, source.Bitmap.Size,
                size, existing ? format : "无（新增）", format, notes, before, after)
                { SourceCrop = new Rectangle(Point.Empty, source.Bitmap.Size) });
        }

        private static Size PlainRawSize(FrameDescriptor frame, byte[] payload, ImageCategory category)
        {
            if (frame.Width > 0 && frame.Height > 0) return new Size(frame.Width, frame.Height);
            // This geometry is already recognized by CodecHelpers.DecodeEntry.
            // The extra two bytes are the established optional RAW container tail.
            if (Path.GetFileName(frame.SourcePath).Equals("Logo.e5", StringComparison.OrdinalIgnoreCase) &&
                payload.Length is 48 * 48 or 48 * 48 + 2) return new Size(48, 48);
            if (category == ImageCategory.Range && payload.Length is 64 * 64 or 64 * 64 + 2)
                return new Size(64, 64);
            throw new InvalidDataException("目标 RAW 没有由资源解码器登记的静态几何，不能推测宽高或覆盖未知结构。");
        }

        private void Face(ImageImportAssignment assignment, ImageImportRasterNormalization.Source source)
        {
            var facePath = request.Resource.Paths.First(path => Path.GetFileName(path).Equals("Face.e5", StringComparison.OrdinalIgnoreCase));
            var work = E5(facePath);
            byte[] original; int number; string label;
            if (request.Operation == ImageImportOperation.Replace)
            {
                var target = Target(assignment);
                var faceFrames = target.Frames.Where(item => item.SourceKey.Equals("Face.e5", StringComparison.OrdinalIgnoreCase));
                FrameDescriptor frame;
                if (target.IsFixedSlot)
                {
                    if (!assignment.Role.StartsWith("expression:", StringComparison.OrdinalIgnoreCase) ||
                        !int.TryParse(assignment.Role.AsSpan(11), out var expression) || expression is < 0 or > 7)
                        throw new InvalidDataException("Face 0 八表情组须明确选择 expression:0～7；一次只写对应表情与其Tou变体。");
                    frame = faceFrames.Single(item => item.FrameIndex == expression);
                    label = $"{target.DisplayName} / 表情{expression} / Face #{frame.ImageNumber}";
                }
                else
                {
                    if (!assignment.Role.Equals("image", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("普通头像不接受八表情角色。");
                    frame = faceFrames.Single(); label = target.DisplayName;
                }
                number = frame.ImageNumber; original = work.Archive.ReadDecoded(number);
            }
            else
            {
                if (!assignment.Role.Equals("image", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("新增头像不接受八表情角色。");
                if (work.Archive.Entries.Count == 0) throw new InvalidDataException("Face.e5 缺少已确认的新增编码模板。");
                number = work.Archive.Entries.Count + work.Appends.Count + 1;
                original = work.Archive.ReadDecoded(work.Archive.Entries.Count); label = $"头像 {number - 8}";
            }
            var standard = ImageRendering.IsStandardImage(original);
            if (standard) ImageImportRasterNormalization.RequireNoUnknownTail(original);
            var size = standard ? new Size(120, 120) : FacePayloadService.GetSize(original);
            using var fitted = ImageImportRasterNormalization.Portrait(source.Bitmap, size);
            var payload = standard ? ImageImportRasterNormalization.Png(fitted)
                : ImageImportTargetEncoding.Raw8(original, size.Width, size.Height, fitted,
                    new PaletteService().RequireWritePalette(request.Project), token);
            if (request.Operation == ImageImportOperation.Replace) Replace(work, number, payload);
            else work.Appends.Add(payload);
            _rows.Add(new ImageImportPreviewRow(label, assignment.Role.StartsWith("expression:", StringComparison.OrdinalIgnoreCase) ? assignment.Role : "Face", assignment.SourcePath, source.Bitmap.Size, size,
                standard ? ImageImportRasterNormalization.Format(original) : "RAW8",
                standard ? "PNG32" : "RAW8", standard ? "居中正方形裁切，120×120 高质量双三次；输出 PNG32。"
                    : "按已确认旧版头像实际尺寸居中裁切；按项目调色板量化，保留合法容器尾部。",
                request.Operation == ImageImportOperation.Replace ? standard ? ImageImportRasterNormalization.DecodeArgb(original)
                    : FacePayloadService.Decode(request.Project, original) : null,
                standard ? ImageImportRasterNormalization.DecodeArgb(payload) : FacePayloadService.Decode(request.Project, payload))
                { SourceCrop = ImageImportRasterNormalization.PortraitCrop(source.Bitmap.Size, size) });

            var touPath = request.Resource.Paths.FirstOrDefault(path => Path.GetFileName(path).Equals("Tou.dll", StringComparison.OrdinalIgnoreCase));
            if (touPath == null) { _warnings.Add("Tou.dll 缺失：本次只写入 Face.e5 小头像。"); return; }
            var records = DllRecords(touPath);
            var id = checked(number + 300);
            var variants = records.Where(record => record.Id == id).ToArray();
            if (request.Operation == ImageImportOperation.Append)
            {
                if (variants.Length != 0 || DllUpdates(touPath).Any(update => update.Id == id))
                    throw new InvalidDataException($"Tou.dll ID={id} 已存在，无法与新增 Face #{number} 安全对齐。");
                var templateId = work.Archive.Entries.Count + 300;
                variants = records.Where(record => record.Id == templateId).ToArray();
                if (variants.Length == 0) throw new InvalidDataException($"Tou.dll 缺少与末尾 Face 对齐的 ID={templateId} 编码模板。");
            }
            else if (variants.Length == 0) { _warnings.Add($"Tou.dll 缺少 ID={id}：该头像只写入 Face.e5。"); return; }
            foreach (var variant in variants)
            {
                token.ThrowIfCancellationRequested();
                using var tou = ImageImportRasterNormalization.Portrait(source.Bitmap, new Size(variant.Width, variant.Height));
                AddDll(touPath, variant, id, tou, assignment, "Tou", false,
                    "原始素材按该语言变体实际尺寸居中裁切；保留 DIB 色深、调色板、行序、填充和尾部。",
                    request.Operation == ImageImportOperation.Replace, source.Bitmap.Size,
                    ImageImportRasterNormalization.PortraitCrop(source.Bitmap.Size, tou.Size));
            }
        }

        private void NativeDll(ImageImportAssignment assignment, ImageImportRasterNormalization.Source source)
        {
            var path = request.Resource.Paths.Single();
            var records = DllRecords(path);
            if (request.Operation == ImageImportOperation.Append)
            {
                if (records.Count == 0) throw new InvalidDataException("DLL 缺少可参考的语言模板。");
                var id = checked(Math.Max(records.Max(record => record.Id), DllUpdates(path).Select(update => update.Id).DefaultIfEmpty().Max()) + 1);
                if (id > ushort.MaxValue) throw new InvalidDataException("DLL 原生 ID 已达到上限。");
                var templateId = records.Min(record => record.Id);
                foreach (var reference in records.Where(record => record.Id == templateId))
                {
                    using var canvas = ImageImportRasterNormalization.Canvas(source.Bitmap, new Size(reference.Width, reference.Height));
                    AddDll(path, reference, id, canvas, assignment, "image", false,
                        "按最大原生 ID 之后追加并保留既有稀疏 ID；继承已验证模板画布、色深、调色板、行序和语言，素材缩放填满模板画布。", false,
                        source.Bitmap.Size, new Rectangle(Point.Empty, source.Bitmap.Size));
                }
                return;
            }
            var target = Target(assignment);
            var ids = target.Frames.Select(frame => frame.ImageNumber).Distinct().ToHashSet();
            var variants = records.Where(record => ids.Contains(record.Id)).ToArray();
            if (variants.Length == 0) throw new InvalidDataException("目标 DLL ID 不存在。");
            foreach (var variant in variants)
            {
                using var canvas = ImageImportRasterNormalization.Canvas(source.Bitmap, new Size(variant.Width, variant.Height));
                AddDll(path, variant, variant.Id, canvas, assignment, "image", false,
                    "保留原生 ID 和目标画布；素材缩放填满各语言变体实际尺寸；保留其 DIB 编码布局。", true,
                    source.Bitmap.Size, new Rectangle(Point.Empty, source.Bitmap.Size));
            }
        }

        private void Icon(IReadOnlyList<ImageImportAssignment> assignments)
        {
            if (assignments.Any(item => !item.Role.Equals("large", StringComparison.OrdinalIgnoreCase) && !item.Role.Equals("small", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("图标素材角色只能为 large 或 small。");
            var largeAssignment = assignments.SingleOrDefault(item => item.Role.Equals("large", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("每组图标必须有一张 large 素材。");
            var smallAssignment = assignments.SingleOrDefault(item => item.Role.Equals("small", StringComparison.OrdinalIgnoreCase));
            using var largeSource = ImageImportRasterNormalization.Load(largeAssignment.SourcePath, token);
            using var smallSource = smallAssignment == null ? null : ImageImportRasterNormalization.Load(smallAssignment.SourcePath, token);
            if (request.Resource.Kind == ResourceKind.DllBitmap)
                DllIcon(largeAssignment, smallAssignment, largeSource, smallSource);
            else if (request.Resource.Kind == ResourceKind.E5Indexed)
                E5Icon(largeAssignment, smallAssignment, largeSource, smallSource);
            else throw new InvalidDataException("该图标资源格式没有可用的大小图导入规则。");
        }

        private void E5Icon(ImageImportAssignment largeAssignment, ImageImportAssignment? smallAssignment,
            ImageImportRasterNormalization.Source largeSource, ImageImportRasterNormalization.Source? smallSource)
        {
            var path = request.Resource.Paths.Single();
            var work = E5(path);
            int[] numbers;
            byte[][] originals;
            string label;
            if (request.Operation == ImageImportOperation.Replace)
            {
                var target = Target(largeAssignment); label = target.DisplayName;
                numbers = new[] { target.Frames.Single(frame => frame.Action == "small").ImageNumber,
                    target.Frames.Single(frame => frame.Action == "large").ImageNumber };
                originals = numbers.Select(work.Archive.ReadDecoded).ToArray();
                foreach (var original in originals)
                {
                    if (!ImageRendering.IsStandardImage(original)) throw new InvalidDataException("图标目标不是独立标准图片。");
                    ImageImportRasterNormalization.RequireNoUnknownTail(original);
                }
            }
            else
            {
                var complete = work.Archive.Entries.Count / 2 * 2;
                if (complete < 2) throw new InvalidDataException("图标资源缺少可验证的完整大小图编码模板。");
                originals = new[] { work.Archive.ReadDecoded(complete - 1), work.Archive.ReadDecoded(complete) };
                foreach (var original in originals)
                {
                    if (!ImageRendering.IsStandardImage(original)) throw new InvalidDataException("图标模板不是独立标准图片。");
                    ImageImportRasterNormalization.RequireNoUnknownTail(original);
                }
                work.AppendPairs = true;
                var first = work.Archive.Entries.Count / 2 * 2 + work.Appends.Count + 1;
                numbers = [first, first + 1]; label = $"{request.Resource.DisplayName} {(first - 1) / 2}";
            }
            using var oldSmall = BmpPayloadService.Decode(originals[0]);
            using var oldLarge = BmpPayloadService.Decode(originals[1]);
            using var large = ImageImportRasterNormalization.Icon(largeSource.Bitmap, oldLarge.Size, false, token);
            using var small = smallSource == null ? ImageImportRasterNormalization.SmallIcon(large, oldSmall.Size, false)
                : ImageImportRasterNormalization.Icon(smallSource.Bitmap, oldSmall.Size, false, token);
            var payloads = new[] { ImageImportRasterNormalization.E5Icon(small), ImageImportRasterNormalization.E5Icon(large) };
            if (request.Operation == ImageImportOperation.Replace)
                for (var index = 0; index < 2; index++) Replace(work, numbers[index], payloads[index]);
            else work.Appends.AddRange(payloads);
            for (var index = 0; index < 2; index++)
            {
                var assignment = index == 0 ? smallAssignment ?? largeAssignment : largeAssignment;
                var source = index == 0 ? smallSource ?? largeSource : largeSource;
                var role = index == 0 ? "small" : "large";
                var crop = ImageImportRasterNormalization.IconCrop(source.Bitmap,
                    index == 0 && smallSource != null ? oldSmall.Size : oldLarge.Size, false, token);
                _rows.Add(new ImageImportPreviewRow($"{label} #{numbers[index]}", role, assignment.SourcePath,
                    source.Bitmap.Size, index == 0 ? small.Size : large.Size,
                    request.Operation == ImageImportOperation.Append ? "无（新增）" : ImageImportRasterNormalization.Format(originals[index]), "BMP24",
                    index == 0 && smallSource == null ? "由规范化大图的完整画布最近邻生成目标小图；完全透明转游戏洋红键，半透明按 RGB 实色写入。"
                        : "非标准尺寸裁有效区后等比居中最近邻；标准尺寸保留留白；完全透明转游戏洋红键，半透明按 RGB 实色写入。",
                    request.Operation == ImageImportOperation.Append ? null : BmpPayloadService.Decode(originals[index]), BmpPayloadService.Decode(payloads[index]))
                    { SourceCrop = crop });
            }
        }

        private void DllIcon(ImageImportAssignment largeAssignment, ImageImportAssignment? smallAssignment,
            ImageImportRasterNormalization.Source largeSource, ImageImportRasterNormalization.Source? smallSource)
        {
            var path = request.Resource.Paths.Single();
            var records = DllRecords(path);
            if (records.Count == 0) throw new InvalidDataException("DLL 没有已确认的大小图编码模板。");
            int smallId; int largeId; DllBitmapRecord[] smallVariants; DllBitmapRecord[] largeVariants;
            if (request.Operation == ImageImportOperation.Replace)
            {
                var target = Target(largeAssignment);
                smallId = target.Frames.Single(frame => frame.Action == "small").ImageNumber;
                largeId = target.Frames.Single(frame => frame.Action == "large").ImageNumber;
                smallVariants = records.Where(record => record.Id == smallId).ToArray();
                largeVariants = records.Where(record => record.Id == largeId).ToArray();
            }
            else
            {
                var baseId = records.Min(record => record.Id);
                smallId = checked(Math.Max(records.Max(record => record.Id), DllUpdates(path).Select(update => update.Id).DefaultIfEmpty().Max()) + 1);
                largeId = checked(smallId + 1);
                if ((smallId - baseId) % 2 != 0) throw new InvalidDataException("DLL 末尾存在未配对图标，不能跳过缺项追加新的大小图组。");
                if (largeId > ushort.MaxValue) throw new InvalidDataException("DLL 图标 ID 已达到上限。");
                smallVariants = records.Where(record => record.Id == baseId).ToArray();
                largeVariants = records.Where(record => record.Id == baseId + 1).ToArray();
            }
            if (smallVariants.Length == 0 || largeVariants.Length == 0) throw new InvalidDataException("DLL 缺少完整大小图对。");
            foreach (var variant in largeVariants)
            {
                using var large = ImageImportRasterNormalization.Icon(largeSource.Bitmap, new Size(variant.Width, variant.Height), true, token);
                AddDll(path, variant, largeId, large, largeAssignment, "large", true,
                    "裁有效区并等比居中最近邻；精确目标画布保留留白；保留目标 DIB 编码与调色板。", request.Operation == ImageImportOperation.Replace,
                    largeSource.Bitmap.Size, ImageImportRasterNormalization.IconCrop(largeSource.Bitmap, large.Size, true, token));
            }
            foreach (var variant in smallVariants)
            {
                var parent = largeVariants.FirstOrDefault(largeVariant => largeVariant.Language == variant.Language);
                if (parent == null)
                {
                    if (largeVariants.Select(item => new Size(item.Width, item.Height)).Distinct().Count() != 1)
                        throw new InvalidDataException($"LANG={variant.Language} 的小图缺少唯一可用的父大图尺寸模板。");
                    parent = largeVariants[0];
                }
                if (smallSource == null && (parent.Width != variant.Width * 2 || parent.Height != variant.Height * 2))
                    throw new InvalidDataException($"DLL 大小图模板不是2倍尺寸，无法执行透明优先2×2派生；请提供显式 small 素材。LANG={variant.Language}");
                using var large = ImageImportRasterNormalization.Icon(largeSource.Bitmap, new Size(parent.Width, parent.Height), true, token);
                using var small = smallSource == null ? ImageImportRasterNormalization.SmallIcon(large,
                    new Size(variant.Width, variant.Height), true) : ImageImportRasterNormalization.Icon(smallSource.Bitmap,
                    new Size(variant.Width, variant.Height), true, token);
                AddDll(path, variant, smallId, small, smallAssignment ?? largeAssignment, "small", true,
                    smallSource == null ? "从规范化大图生成小图；2×2依右下、右上、左下、左上取首个非透明像素。"
                        : "使用显式小图素材；标准画布保留留白，非标准尺寸裁有效区等比居中最近邻。", request.Operation == ImageImportOperation.Replace,
                    (smallSource ?? largeSource).Bitmap.Size, ImageImportRasterNormalization.IconCrop((smallSource ?? largeSource).Bitmap,
                        smallSource == null ? large.Size : small.Size, true, token));
            }
        }

        private void AddDll(string path, DllBitmapRecord variant, int id, Bitmap bitmap,
            ImageImportAssignment assignment, string role, bool iconKeys, string notes, bool existing, Size sourceSize, Rectangle sourceCrop)
        {
            var payload = DllImportEncoding.Encode(variant.Dib, bitmap, iconKeys, token);
            var outputFormat = DllImportEncoding.Format(payload);
            if (outputFormat == "DIB8")
            {
                notes += " 颜色按目标调色板量化；完全透明写入索引0。";
                _warnings.Add("8位 DLL 位图使用目标调色板量化，请检查候选预览的颜色。");
            }
            else if (outputFormat == "DIB24")
                notes += " 24位不保存 alpha：完全透明转纯蓝色键，半透明按 RGB 实色写入。";
            var updates = DllUpdates(path);
            if (updates.Any(update => update.Id == id && update.Language == variant.Language))
                throw new InvalidDataException($"DLL ID={id}/LANG={variant.Language} 被重复分配。");
            updates.Add(new(id, variant.Language, payload));
            _rows.Add(new ImageImportPreviewRow($"{Path.GetFileName(path)} ID={id} / LANG={variant.Language}", role,
                assignment.SourcePath, sourceSize, bitmap.Size, existing ? DllImportEncoding.Format(variant.Dib) : "无（新增）",
                outputFormat, notes, existing ? DllImportEncoding.Decode(variant.Dib) : null,
                DllImportEncoding.Decode(payload)) { SourceCrop = sourceCrop });
        }
    }
}
