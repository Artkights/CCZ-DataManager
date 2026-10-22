using System.Diagnostics;
using System.Drawing.Imaging;
using ImageManager.Core;

namespace ImageManager.App;

public sealed class ImageGridPage : UserControl
{
    private readonly string _title;
    private readonly IImageCatalogService _catalog;
    private readonly IImageMutationService _mutations;
    private readonly IReferenceAtlasService _atlases;
    private readonly RsImportService _rsImports;
    private readonly IBatchRenameService _renames;
    private readonly IImagePreviewService _previews;
    private readonly MapPairService _mapPairs = new();
    private readonly RsStripExchangeService _strips;
    private readonly ImagePageCapabilities _capabilities;
    private readonly ComboBox _resourceSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly CheckBox _idle = new() { Text = "空闲", AutoSize = true, Checked = false };
    private readonly CheckBox _background = new() { Text = "背景色", AutoSize = true, Checked = true };
    private readonly ComboBox _sMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 82 };
    private readonly VirtualImageGallery _gallery = new();
    private readonly SelectedAnimationPreview _animationPreview = new();
    private readonly Panel _galleryHost = new() { Dock = DockStyle.Fill };
    private readonly SplitContainer _previewSplit = new() { Dock = DockStyle.Fill, SplitterWidth = 6 };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 25, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    private readonly Label _writeConstraints = new() { Dock = DockStyle.Bottom, Height = 46, AutoEllipsis = true,
        Padding = new Padding(8, 4, 8, 0), BackColor = Color.FromArgb(241, 245, 249) };
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _usedIds = [];
    private readonly ToolTip _paletteTip = new();
    private ProjectProfile? _project;
    private List<ImageResourceDescriptor> _resources = [];
    private List<LogicalImageAsset> _assets = [];
    private string _search = string.Empty;
    private CancellationTokenSource? _renderCancellation;
    private long _renderGeneration;
    private CancellationTokenSource? _loadCancellation;
    private readonly Dictionary<string, Task<AssetLoadResult>> _resourceLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _assetFailures = new(StringComparer.OrdinalIgnoreCase);
    private long _loadRevision;
    private bool _initializing;
    private bool _previewLayoutInitialized;
    private bool _isActive = true;
    private string? _focusedAssetId;
    private Task _loadingTask = Task.CompletedTask;

    public Task LoadingTask => _loadingTask;
    public string SearchText => _search;
    public IImageImportPreparationService FileImports { get; set; } = new ImageImportPreparationService();
    public Func<IReadOnlyList<SImageAssignmentReference>?>? SAssignmentReferencesProvider { get; set; }
    public event Action<IReadOnlyList<string>>? ResourcesChanged;
    public int RetainedAnimationCount => _animationPreview.RetainedAnimationCount;
    public long RetainedAnimationBytes => _animationPreview.RetainedBytes;
    public event Action? PlaybackCacheChanged;
    public void TrimPlaybackCache(int count, long bytes) => _animationPreview.TrimCache(count, bytes);

    public event Action? ProjectStructureChanged;

    public ImageGridPage(string title, IImageCatalogService catalog, IImageMutationService mutations,
        IReferenceAtlasService atlases, RsImportService rsImports, IBatchRenameService renames,
        IImagePreviewService previews, ImagePageCapabilities capabilities)
    {
        _title = title;
        _catalog = catalog;
        _mutations = mutations;
        _atlases = atlases;
        _rsImports = rsImports;
        _strips = new RsStripExchangeService(catalog, rsImports);
        _renames = renames;
        _previews = previews;
        _capabilities = capabilities;
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        _galleryHost.Controls.Add(_gallery);
        _previewSplit.Panel1.Controls.Add(_galleryHost);
        _previewSplit.Panel2.Controls.Add(_animationPreview);
        _previewSplit.Panel2Collapsed = true;
        Controls.Add(_previewSplit);
        Controls.Add(_status);
        Controls.Add(_writeConstraints);
        Controls.Add(BuildToolbar());
        _gallery.SelectionChanged += UpdateWriteConstraints;
        _animationPreview.CacheChanged += () => PlaybackCacheChanged?.Invoke();
        _resourceSelector.SelectedIndexChanged += (_, _) => { if (!_initializing) LoadSelectedResource(); };
        _idle.CheckedChanged += (_, _) => RenderGallery();
        _background.CheckedChanged += (_, _) => RenderGallery(refreshThumbnails: true);
        _sMode.Items.AddRange(["将领", "兵种"]);
        _sMode.SelectedIndex = 0;
        _sMode.SelectedIndexChanged += (_, _) => LoadSelectedResource();
        VisibleChanged += (_, _) => { if (!Visible) _animationPreview.Pause(); };
    }

    public void LoadProject(ProjectProfile project, IEnumerable<ImageResourceDescriptor> resources, IReadOnlySet<int>? usedIds = null)
    {
        _loadCancellation?.Cancel();
        _resourceLoads.Clear();
        _project = project;
        var palette = new PaletteService().Resolve(project);
        _paletteTip.SetToolTip(_resourceSelector, $"RAW 调色板来源：{palette.Source}\n{palette.Diagnostic}");
        _resources = resources.ToList();
        if (_resources.All(resource => resource.Kind == ResourceKind.McallAnimation))
            _resources = _resources.OrderBy(item => ParseMcallFileName(item.Paths[0])?.Id ?? int.MaxValue)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToList();
        _usedIds.Clear();
        if (usedIds != null) _usedIds.UnionWith(usedIds);
        var items = _resources.Select(item => new ResourceItem(item)).ToList();
        if (_resources.Count > 0 && _resources.All(resource => resource.Kind == ResourceKind.McallAnimation))
        {
            var first = _resources.FirstOrDefault(resource => resource.CanMutate) ?? _resources[0];
            items.Insert(0, new ResourceItem(first with { Key = "__all-mcall", DisplayName = "全部大动画",
                Paths = _resources.SelectMany(resource => resource.Paths).ToArray(), EntryCount = _resources.Count,
                CanPreview = true, CanMutate = _resources.Any(resource => resource.CanMutate),
                Status = "按 Mcall 数值编号汇总；无法解析的文件保留并显示原因" }, true));
        }
        _initializing = true;
        try { _resourceSelector.DataSource = items; }
        finally { _initializing = false; }
        SetWriteEnabled(!project.IsReadOnly);
        if (_resources.Count == 0)
        {
            _assets.Clear();
            _gallery.ClearItems();
            _status.Text = _title + "：当前项目没有检测到对应资源。";
        }
        else LoadSelectedResource();
    }

    public void SetSearch(string search)
    {
        search = search.Trim();
        if (_search == search) return;
        _search = search;
        RenderGallery();
    }

    public void ActivateGallery()
    {
        SetActive(true);
        _gallery.RefreshViewport();
        if (FocusedAsset() is { } focused && IsAnimatedAsset(focused))
            ActivatePreview(focused);
    }
    public void SetActive(bool active) { _isActive = active; _animationPreview.SetActive(active); }

    public void UpdateUsedIds(IReadOnlySet<int>? usedIds)
    {
        var changed = !_usedIds.SetEquals(usedIds ?? new HashSet<int>());
        _usedIds.Clear();
        if (usedIds != null) _usedIds.UnionWith(usedIds);
        if (changed && _idle.Checked) RenderGallery();
        else UpdateWriteConstraints();
    }

    public void InvalidateResources(IReadOnlyList<string> paths)
    {
        var changed = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in _resources.Where(resource => resource.Paths.Any(path => changed.Contains(Path.GetFullPath(path)))))
        {
            _resourceLoads.Remove(resource.Key + ":person");
            _resourceLoads.Remove(resource.Key + ":job");
        }
        _animationPreview.InvalidatePaths(paths);
        _previews.InvalidatePaths(paths);
        if (SelectedResources().Any(resource => resource.Paths.Any(path => changed.Contains(Path.GetFullPath(path)))))
            LoadSelectedResource(preserveState: true);
    }

    public bool ExecuteShortcut(Keys keys)
    {
        if (keys == (Keys.Control | Keys.N) && ButtonEnabled("add")) { AddImages(); return true; }
        if (keys == Keys.Delete && ButtonEnabled("delete")) { DeleteImages(); return true; }
        if (keys == (Keys.Control | Keys.I) && ButtonEnabled("replace")) { ReplaceImages(); return true; }
        if (keys == (Keys.Control | Keys.E) && Has(ImagePageCapabilities.Export)) { ExportImages(); return true; }
        if (keys == Keys.Space && Has(ImagePageCapabilities.Play)) { Play(); return true; }
        if (keys == Keys.F4 && Has(ImagePageCapabilities.AllFrames)) { ShowAllFrames(); return true; }
        if (keys == (Keys.Control | Keys.P) && ButtonEnabled("pixel")) { PixelEdit(); return true; }
        if (keys == (Keys.Control | Keys.Shift | Keys.E) && Has(ImagePageCapabilities.ReferenceAtlas)) { ExportAtlas(); return true; }
        if (keys == (Keys.Control | Keys.Shift | Keys.I) && ButtonEnabled("atlas-import")) { ImportAtlas(); return true; }
        if (keys == Keys.F2 && ButtonEnabled("rename")) { BatchRename(); return true; }
        if (keys == Keys.F1 && Has(ImagePageCapabilities.Documentation)) { OpenDocumentation(); return true; }
        if (keys == (Keys.Control | Keys.A)) { SetAllSelected(true); return true; }
        if (keys == Keys.F5)
        {
            foreach (var resource in SelectedResources()) { _resourceLoads.Remove(resource.Key + ":person"); _resourceLoads.Remove(resource.Key + ":job"); }
            var paths = SelectedResources().SelectMany(resource => resource.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _previews.InvalidatePaths(paths); _animationPreview.InvalidatePaths(paths);
            LoadSelectedResource(preserveState: true); return true;
        }
        return false;
    }

    private Control BuildToolbar()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, MinimumSize = new Size(0, 42),
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(7), WrapContents = true };
        panel.Controls.Add(new Label { Text = "资源", AutoSize = true, Margin = new Padding(3, 8, 3, 0) });
        panel.Controls.Add(_resourceSelector);
        foreach (var item in new (string Key, string Text, Action Action, ImagePageCapabilities Capability)[]
                 {
                     ("add", "新增图片", AddImages, ImagePageCapabilities.Add),
                     ("delete", "删除图片", DeleteImages, ImagePageCapabilities.Delete),
                     ("replace", "替换图片", ReplaceImages, ImagePageCapabilities.Replace),
                     ("map-pair", "导入地图与地形", ImportMapPair, ImagePageCapabilities.Replace),
                     ("export", "导出图片", ExportImages, ImagePageCapabilities.Export),
                     ("frame-export", "导出单帧", ExportIndividualFrames, ImagePageCapabilities.Export),
                     ("play", "播放动作", Play, ImagePageCapabilities.Play),
                     ("frames", "全部帧数", ShowAllFrames, ImagePageCapabilities.AllFrames),
                     ("pixel", "像素编辑", PixelEdit, ImagePageCapabilities.PixelEdit),
                     ("atlas-export", "导出参考图", ExportAtlas, ImagePageCapabilities.ReferenceAtlas),
                     ("atlas-import", "导入参考图", ImportAtlas, ImagePageCapabilities.ReferenceAtlas),
                     ("rename", "批量修改文件名", BatchRename, ImagePageCapabilities.BatchRename),
                     ("help", "说明文档", OpenDocumentation, ImagePageCapabilities.Documentation)
                 })
        {
            if (!Has(item.Capability)) continue;
            var button = new Button { Text = item.Text, AutoSize = true, MinimumSize = new Size(0, 30), Margin = new Padding(2) };
            if (item.Key == "map-pair") button.Visible = false;
            button.Click += (_, _) => item.Action();
            _buttons[item.Key] = button;
            panel.Controls.Add(button);
        }
        if (Has(ImagePageCapabilities.IdleFilter)) panel.Controls.Add(_idle);
        if (Has(ImagePageCapabilities.BackgroundToggle)) panel.Controls.Add(_background);
        if (Has(ImagePageCapabilities.SPreviewSelector))
        {
            panel.Controls.Add(new Label { Text = "类型", AutoSize = true, Margin = new Padding(8, 8, 2, 0) });
            panel.Controls.Add(_sMode);
            panel.Controls.Add(new Label { Text = "按物理图号展示 · 移动第0帧", AutoSize = true, Margin = new Padding(8, 8, 2, 0) });
        }
        return panel;
    }

    private void LoadSelectedResource(bool preserveState = false)
        => _loadingTask = LoadSelectedResourceAsync(preserveState);

    private async Task LoadSelectedResourceAsync(bool preserveState)
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;
        var revision = ++_loadRevision;
        if (_project == null || _resourceSelector.SelectedItem is not ResourceItem selected) return;
        var project = _project;
        var resources = SelectedResources().ToArray();
        var job = Has(ImagePageCapabilities.SPreviewSelector) && _sMode.SelectedIndex == 1;
        if (!preserveState)
        {
            _focusedAssetId = null;
            _gallery.FocusedAssetId = null;
            _animationPreview.Clear();
        }
        ConfigurePreviewLayout(resources);
        ConfigureWriteButtons(selected.Descriptor);
        _animationPreview.SetActive(_isActive);
        _gallery.Enabled = false;
        foreach (var button in _buttons.Values) button.Enabled = false;
        _status.Text = "正在读取 " + selected.Descriptor.DisplayName + "…";
        try
        {
            var requests = resources.Select(resource =>
            {
                var key = resource.Key + (job ? ":job" : ":person");
                if (!_resourceLoads.TryGetValue(key, out var task))
                {
                    task = Task.Run(() => ReadResource(project, resource, job));
                    _resourceLoads[key] = task;
                }
                return task;
            }).ToArray();
            var loaded = await Task.WhenAll(requests).WaitAsync(token);
            if (IsDisposed || revision != _loadRevision || token.IsCancellationRequested) return;
            _assets = loaded.SelectMany(result => result.Assets).ToList();
            _assetFailures.Clear();
            foreach (var result in loaded)
                foreach (var failure in result.Failures) _assetFailures[failure.Key] = failure.Value;
            _gallery.Enabled = true;
            ConfigureWriteButtons(selected.Descriptor);
            foreach (var key in new[] { "export", "frame-export", "play", "frames", "atlas-export", "help" })
                if (_buttons.TryGetValue(key, out var button)) button.Enabled = true;
            // A fresh resource result must replace card bindings even when IDs remain unchanged.
            _renderCancellation?.Cancel();
            _renderCancellation?.Dispose();
            _renderCancellation = new CancellationTokenSource();
            Interlocked.Increment(ref _renderGeneration);
            RenderGallery(preserveState, refreshThumbnails: true);
            if (_focusedAssetId != null)
            {
                var focused = FocusedAsset();
                if (focused != null) ActivatePreview(focused);
                else { _focusedAssetId = null; _animationPreview.Clear(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed && revision == _loadRevision) _status.Text = "读取失败：" + ex.Message;
        }
    }

    private AssetLoadResult ReadResource(ProjectProfile project, ImageResourceDescriptor resource, bool job)
    {
        try
        {
            if (!resource.CanPreview) throw new InvalidDataException(resource.Status);
            var assets = job ? new SResourceCodec().ReadJobAssets(project, resource).ToList()
                : _catalog.ReadAssets(project, resource).ToList();
            if (job) assets = NameJobSAssets(assets);
            return new(assets, new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            // Standalone Mcall files each occupy one stable slot, including broken files.
            if (resource.Kind != ResourceKind.McallAnimation) throw;
            var id = ParseMcallFileName(resource.Paths[0])?.Id ?? 0;
            var asset = new LogicalImageAsset(resource.Key, id, resource.DisplayName, resource.Category, resource.Kind,
                [new FrameDescriptor(resource.Key, resource.Paths[0], 0, 0, 1, "mcall", "none", 0, 0)],
                Array.Empty<string>(), false, true, "无法预览：" + ex.Message);
            return new([asset], new Dictionary<string, string> { [asset.StablePhysicalId] = ex.Message });
        }
    }

    private IEnumerable<ImageResourceDescriptor> SelectedResources()
        => _resourceSelector.SelectedItem is ResourceItem { IsAll: true } ? _resources :
            SelectedResource() is { } selected ? [selected] : [];

    private void ConfigurePreviewLayout(IReadOnlyList<ImageResourceDescriptor> resources)
    {
        var animated = resources.Any(resource => resource.Category == ImageCategory.StrategyAnimation ||
            resource.Kind is ResourceKind.WeatherSpecial or ResourceKind.DtAnimation);
        var weather = resources.Count > 0 && resources.All(resource => resource.Kind == ResourceKind.WeatherSpecial);
        var orientation = weather ? Orientation.Horizontal : Orientation.Vertical;
        if (_previewSplit.Orientation != orientation)
        {
            _previewSplit.Panel1MinSize = 0; _previewSplit.Panel2MinSize = 0;
            _previewSplit.Orientation = orientation;
            _previewLayoutInitialized = false;
        }
        _previewSplit.Panel2Collapsed = !animated;
        if (!animated) return;
        void Arrange()
        {
            if (_previewLayoutInitialized || IsDisposed) return;
            var extent = weather ? _previewSplit.ClientSize.Height : _previewSplit.ClientSize.Width;
            if (extent <= 360) return;
            var preview = weather ? Math.Clamp(extent / 3, 180, 320) : 300;
            _previewSplit.SplitterDistance = Math.Max(120, extent - preview - _previewSplit.SplitterWidth);
            _previewSplit.Panel1MinSize = weather ? 120 : 222;
            _previewSplit.Panel2MinSize = weather ? 140 : 200;
            _previewLayoutInitialized = true;
        }
        Arrange();
        if (!_previewLayoutInitialized) _previewSplit.SizeChanged += Initialize;
        void Initialize(object? sender, EventArgs args)
        {
            Arrange();
            if (_previewLayoutInitialized) _previewSplit.SizeChanged -= Initialize;
        }
    }

    private List<LogicalImageAsset> NameJobSAssets(List<LogicalImageAsset> assets)
    {
        var names = _project == null ? new Dictionary<int, string>() : JobSDisplayService.LoadNames(_project);
        return JobSDisplayService.ApplyNames(assets, names);
    }

    private void RenderGallery(bool preserveSelection = true, bool refreshThumbnails = false)
    {
        if (_project == null) return;
        if (_renderCancellation == null) _renderCancellation = new CancellationTokenSource();
        if (refreshThumbnails)
        {
            _renderCancellation.Cancel();
            _renderCancellation.Dispose();
            _renderCancellation = new CancellationTokenSource();
            Interlocked.Increment(ref _renderGeneration);
        }
        var generation = _renderGeneration;
        var filtered = FilteredAssets();
        _gallery.SetItems(filtered, asset => CreateCard(asset, generation), preserveSelection, !refreshThumbnails);
        _gallery.FocusedAssetId = _focusedAssetId;
        _status.Text = $"{SelectedResource()?.DisplayName}：共 {filtered.Count} 项；滚动时按需显示缩略图";
        UpdateWriteConstraints();
    }

    private List<LogicalImageAsset> FilteredAssets()
        => _assets.Where(asset => (!Has(ImagePageCapabilities.SPreviewSelector) ||
                                  asset.SMode == (_sMode.SelectedIndex == 1 ? SImageMode.Job : SImageMode.Person)) &&
                                 (!_idle.Checked || !_usedIds.Contains(asset.LogicalId)) &&
                                  (_search.Length == 0 || asset.DisplayName.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                                   asset.Kind == ResourceKind.SComposite && $"S_{SResourceExchangeNaming.PhysicalId(asset):000}".Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                                   asset.LogicalId.ToString().Contains(_search, StringComparison.OrdinalIgnoreCase))).ToList();

    private Control CreateCard(LogicalImageAsset asset, long generation)
    {
        var descriptors = PreviewDescriptors(asset);
        var card = new BufferedGalleryPanel { BorderStyle = BorderStyle.FixedSingle, Tag = asset, BackColor = Color.White, Padding = new Padding(2) };
        var cardCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _renderCancellation?.Token ?? CancellationToken.None);
        card.Disposed += (_, _) =>
        {
            cardCancellation.Cancel();
            cardCancellation.Dispose();
        };
        var check = new CheckBox { Text = asset.DisplayName, Dock = DockStyle.Top, Height = 30, Tag = "selector", Padding = new Padding(5, 0, 0, 0) };
        _paletteTip.SetToolTip(check, asset.Status + "；勾选用于批量操作，点击缩略图设为当前项");
        card.Controls.Add(check);
        card.Click += (_, _) => ActivatePreview(asset);
        if (_assetFailures.TryGetValue(asset.StablePhysicalId, out var failure))
        {
            var error = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Firebrick, Text = "无法预览\n" + failure, AutoEllipsis = true };
            error.Click += (_, _) => ActivatePreview(asset);
            _paletteTip.SetToolTip(error, failure);
            card.Controls.Add(error); error.BringToFront();
            return card;
        }
        var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, BackColor = Color.FromArgb(238, 240, 242), Padding = new Padding(5) };
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewPanel.ColumnCount = descriptors.Length;
        for (var column = 0; column < descriptors.Length; column++)
            previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / descriptors.Length));
        for (var index = 0; index < descriptors.Length; index++)
        {
            var descriptor = descriptors[index];
            var viewport = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(2) };
            var box = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Margin = Padding.Empty,
                BackColor = Color.FromArgb(225, 228, 232), Tag = descriptor };
            viewport.Controls.Add(box);
            viewport.SizeChanged += (_, _) => ArrangePreview(box);
            var tip = new ToolTip();
            tip.SetToolTip(box, descriptor.Action switch { "small" => "小图", "large" => "大图", _ => descriptor.Action });
            previewPanel.Controls.Add(viewport, index, 0);
            box.Click += (_, _) => ActivatePreview(asset);
            viewport.Click += (_, _) => ActivatePreview(asset);
            var started = false;
            void StartLoading()
            {
                if (started || box.IsDisposed) return;
                started = true;
                _ = LoadThumbnailAsync(asset, descriptor, box, generation,
                    cardCancellation.Token);
            }
            box.HandleCreated += (_, _) => StartLoading();
            if (box.IsHandleCreated) StartLoading();
        }
        card.Controls.Add(previewPanel);
        previewPanel.BringToFront();
        return card;
    }

    private IReadOnlyList<LogicalImageAsset> SelectedAssets() => _gallery.SelectedItems;

    private LogicalImageAsset? FocusedAsset()
        => _assets.FirstOrDefault(asset => asset.StablePhysicalId == _focusedAssetId);

    private void ActivatePreview(LogicalImageAsset asset)
    {
        if (_project == null || IsDisposed || !_gallery.Enabled) return;
        _focusedAssetId = asset.StablePhysicalId;
        _gallery.FocusedAssetId = _focusedAssetId;
        if (IsAnimatedAsset(asset))
        {
            if (_assetFailures.TryGetValue(asset.StablePhysicalId, out var error)) _animationPreview.ShowFailure(asset, error);
            else _ = _animationPreview.BindAsync(_project, _catalog, asset);
        }
        UpdateWriteConstraints();
    }

    private static bool IsAnimatedAsset(LogicalImageAsset asset)
        => asset.Category == ImageCategory.StrategyAnimation || asset.Kind is ResourceKind.WeatherSpecial or ResourceKind.DtAnimation;

    private FrameDescriptor[] PreviewDescriptors(LogicalImageAsset asset)
    {
        if (asset.Kind == ResourceKind.RComposite)
            return asset.Frames.GroupBy(frame => frame.Direction).OrderBy(group => group.Key == "front" ? 0 : 1)
                .Select(group => group.First()).Take(2).ToArray();
        if (asset.Kind == ResourceKind.SComposite)
            return SPreviewCandidates(asset).Take(1).ToArray();
        if (asset.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon)
            return [asset.Frames.FirstOrDefault(frame => frame.Action == "large") ?? asset.Frames[^1]];
        return asset.Frames.GroupBy(frame => frame.Action).Select(group => group.First()).Take(2).DefaultIfEmpty(asset.Frames[0]).ToArray();
    }

    private async Task LoadThumbnailAsync(LogicalImageAsset asset, FrameDescriptor descriptor, PictureBox target,
        long generation, CancellationToken cancellationToken)
    {
        try
        {
            Bitmap? bitmap = null;
            Exception? lastError = null;
            var candidates = asset.Kind == ResourceKind.SComposite ? SPreviewCandidates(asset) : [descriptor];
            foreach (var candidate in candidates)
            {
                try
                {
                    var viewport = target.Parent;
                    var available = viewport == null ? Size.Empty : new Size(
                        viewport.ClientSize.Width - viewport.Padding.Horizontal,
                        viewport.ClientSize.Height - viewport.Padding.Vertical);
                    if (available.Width <= 1 || available.Height <= 1) available = new Size(208, 146);
                    bitmap = await _previews.GetThumbnailAsync(_project!, _catalog, asset, candidate,
                        available, _background.Checked, cancellationToken);
                    if (bitmap != null) break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { lastError = ex; }
            }
            if (bitmap == null && lastError != null) throw lastError;
            if (bitmap == null) return;
            if (cancellationToken.IsCancellationRequested || generation != Volatile.Read(ref _renderGeneration) ||
                target.IsDisposed || !target.IsHandleCreated || target.Tag is not FrameDescriptor current || current != descriptor)
            {
                bitmap.Dispose();
                return;
            }
            var old = target.Image;
            target.Image = bitmap;
            ArrangePreview(target);
            old?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!target.IsDisposed) new ToolTip().SetToolTip(target, "预览失败：" + ex.Message);
        }
    }

    private static void ArrangePreview(PictureBox picture)
    {
        if (picture.Parent is not { } viewport || picture.Image == null || picture.IsDisposed) return;
        var available = new Rectangle(viewport.Padding.Left, viewport.Padding.Top,
            Math.Max(0, viewport.ClientSize.Width - viewport.Padding.Horizontal),
            Math.Max(0, viewport.ClientSize.Height - viewport.Padding.Vertical));
        picture.Bounds = PreviewImageLayout.FitImageBounds(picture.Image.Size, available);
    }

    private FrameDescriptor[] SPreviewCandidates(LogicalImageAsset asset)
    {
        var physical = SResourceExchangeNaming.PhysicalId(asset);
        return asset.Frames.Where(frame => frame.ImageNumber == physical && frame.Action == "mov" && frame.FrameIndex == 0).Take(1).ToArray();
    }

    private SPreviewContext SelectedSContext() => SPreviewContext.Default;

    private bool Has(ImagePageCapabilities capability) => (_capabilities & capability) == capability;
    private bool ButtonEnabled(string key) => _buttons.TryGetValue(key, out var button) && button.Enabled;

    private ImageResourceDescriptor? SelectedResource()
        => (_resourceSelector.SelectedItem as ResourceItem)?.Descriptor;

    private void AddImages()
    {
        if (_project == null || SelectedResource() is not { } resource) return;
        if (resource.Kind == ResourceKind.SComposite && _sMode.SelectedIndex == 1)
        {
            // All 240 job IDs are reserved. Import into a selected fixed slot
            // instead of appending a character S resource under the wrong ID.
            ReplaceImages();
            return;
        }
        var sourceSnapshot = CaptureSources(resource.Paths);
        if (sourceSnapshot == null) return;
        void CommitVerified(MutationPlan candidate, bool reload = true) => Commit(sourceSnapshot.Complete(candidate), reload);
        try
        {
            if (ImageImportPreparationService.Supports(resource))
            {
                using var import = new ImageImportDialog(_project, resource, ImageImportOperation.Append, preparation: FileImports);
                if (import.ShowDialog(this) == DialogResult.OK && import.ResultPlan is { } prepared)
                    CommitVerified(prepared);
                return;
            }
            MutationPlan plan;
            if (resource.Kind == ResourceKind.McallAnimation)
            {
                using var dialog = AnimationPackageOpenDialog(true);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var packages = dialog.FileNames.Select(ValidateAnimationPackage).ToArray();
                var directory = Path.GetDirectoryName(resource.Paths[0])!;
                var existing = _project.ResourcePaths.Values.Select(ParseMcallFileName)
                    .Where(item => item.HasValue).Select(item => item!.Value).ToArray();
                var nextId = existing.Length == 0 ? 0L : (long)existing.Max(item => item.Id) + 1;
                if (nextId + packages.Length - 1 > 154)
                    throw new InvalidOperationException("Mcall 字段只能表示实际编号 0..154，无法继续追加。");
                var digits = Math.Max(2, existing.Select(item => item.Digits).DefaultIfEmpty(2).Max());
                var files = packages.Select((bytes, index) => new MutationFile(
                    Path.Combine(directory, "Mcall" + (nextId + index).ToString("D" + digits) + ".e5"), bytes,
                    "新增独立 Mcall 动画包", AllowCreate: true)).ToArray();
                plan = new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Append,
                    $"新增 {packages.Length} 个 Mcall 文件", files,
                    ["新增文件不会自动修改策略表中的 Mcall 字段。"], false);
                CommitVerified(plan, false);
                ProjectStructureChanged?.Invoke();
                return;
            }
            if (resource.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase))
            {
                using var dialog = AnimationPackageOpenDialog(true);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var packages = dialog.FileNames.Select(ValidateAnimationPackage).ToArray();
                var path = resource.Paths.Single();
                plan = new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Append,
                    $"向 Meff 追加 {packages.Length} 个动画包",
                    [new MutationFile(path, E5Archive.OpenFresh(path).Rebuild(appendDecoded: packages),
                        "追加经过结构验证的策略动画包")],
                    ["新增条目不会自动修改策略表中的 Meff 字段。"], false);
            }
            else if (resource.Kind is ResourceKind.RComposite or ResourceKind.SComposite)
            {
                using var folder = new FolderBrowserDialog { Description = resource.Kind == ResourceKind.RComposite
                    ? "选择包含 front.bmp/back.bmp 的目录" : "选择包含 mov.bmp/atk.bmp/spc.bmp 的目录" };
                if (folder.ShowDialog(this) != DialogResult.OK) return;
                plan = _rsImports.PlanAppend(_project, resource, folder.SelectedPath);
            }
            else
            {
                using var dialog = ImageOpenDialog(true);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                plan = _mutations.PlanAppend(_project, resource, dialog.FileNames);
            }
            CommitVerified(plan);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void DeleteImages()
    {
        if (_project == null) return;
        var selected = SelectedAssets();
        if (selected.Count == 0) { ShowSelectionRequired(); return; }
        if (!EnsureWritable(selected)) return;
        var sourceSnapshot = CaptureSources(selected.SelectMany(asset => asset.Frames).Select(frame => frame.SourcePath));
        if (sourceSnapshot == null) return;
        void CommitVerified(MutationPlan candidate, bool reload = true) => Commit(sourceSnapshot.Complete(candidate), reload);
        if (selected.All(asset => asset.Kind == ResourceKind.McallAnimation))
        {
            var warnings = new List<string>
            {
                "删除 Mcall 文件不会修改策略表字段；仍引用该编号的策略将找不到大动画。"
            };
            warnings.AddRange(selected.SelectMany(asset => asset.ReferencedBy).Distinct().Select(reference => "已知引用：" + reference));
            if (MessageBox.Show(this, string.Join(Environment.NewLine, warnings) + Environment.NewLine + Environment.NewLine +
                                      "确定删除所选 Mcall 文件？", "高风险：删除 Mcall 文件",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                var files = selected.Select(asset => new MutationFile(asset.Frames.Single().SourcePath,
                    Array.Empty<byte>(), "删除独立 Mcall 动画包", DeleteTarget: true)).ToArray();
                var plan = new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Delete,
                    $"删除 {selected.Count} 个 Mcall 文件", files, warnings, false,
                    new Dictionary<string, string> { ["ReferencePolicy"] = "delete-file-only" });
                CommitVerified(plan, false);
                ProjectStructureChanged?.Invoke();
            }
            catch (Exception ex) { ShowError(ex); }
            return;
        }
        if (selected.Any(asset => asset.Kind == ResourceKind.MapPair))
        {
            MessageBox.Show(this, "战场地图槽不能通过删除前移；请使用成对导入替换 JPG 与 Hexzmap 地形层。", "固定地图槽");
            return;
        }
        try
        {
            var plan = _mutations.PlanDelete(_project, selected);
            var used = selected.Where(asset => _usedIds.Contains(asset.LogicalId)).Select(asset => asset.DisplayName).ToArray();
            if (used.Length > 0)
                plan = plan with
                {
                    Warnings = plan.Warnings.Concat(["Data 已使用：" + string.Join("、", used) + "；删除后引用将错位。"]).ToArray()
                };
            var physicalGeneralS = selected.All(asset => asset.Kind == ResourceKind.SComposite && asset.SMode == SImageMode.Person);
            if (physicalGeneralS)
            {
                IReadOnlyList<SImageAssignmentReference>? references = null;
                try { references = SAssignmentReferencesProvider?.Invoke(); }
                catch (Exception ex)
                {
                    plan = plan with { Warnings = plan.Warnings.Concat(["读取当前人物引用失败：" + ex.Message]).ToArray() };
                }
                var physicalNumbers = selected.SelectMany(asset => asset.Frames).Select(frame => frame.ImageNumber).Distinct();
                var maximumPhysicalNumber = selected.SelectMany(asset => asset.Frames).Select(frame => frame.SourcePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => E5Archive.Open(path).Entries.Count).Max();
                plan = plan with
                {
                    Warnings = plan.Warnings.Concat(SImageDeleteImpact.BuildWarnings(physicalNumbers, references, maximumPhysicalNumber)).ToArray()
                };
                using var confirmation = new SImageDeleteConfirmationDialog(plan.Warnings);
                if (confirmation.ShowDialog(this) != DialogResult.OK) return;
                CommitVerified(plan with { RequiresRiskConfirmation = false });
                return;
            }
            var text = string.Join(Environment.NewLine, plan.Warnings) + Environment.NewLine + Environment.NewLine + "确定继续？";
            var nativeIdsOnly = selected.All(asset => asset.Kind == ResourceKind.DllBitmap &&
                asset.Category is not (ImageCategory.ItemIcon or ImageCategory.StrategyIcon));
            if (MessageBox.Show(this, text, nativeIdsOnly ? "删除所选原生 ID，其他编号保持" : "高风险：删除并前移编号",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            CommitVerified(plan);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ReplaceImages()
    {
        if (_project == null) return;
        var selected = SelectedAssets();
        if (selected.Count == 0) { ShowSelectionRequired(); return; }
        if (!EnsureWritable(selected)) return;
        var sourceSnapshot = CaptureSources(selected.SelectMany(asset => asset.Frames).Select(frame => frame.SourcePath));
        if (sourceSnapshot == null) return;
        void CommitVerified(MutationPlan candidate, bool reload = true) => Commit(sourceSnapshot.Complete(candidate), reload);
        if (SelectedResource() is { } rasterResource && ImageImportPreparationService.Supports(rasterResource))
        {
            try
            {
                using var import = new ImageImportDialog(_project, rasterResource, ImageImportOperation.Replace, selected, FileImports);
                if (import.ShowDialog(this) == DialogResult.OK && import.ResultPlan is { } prepared)
                    CommitVerified(prepared);
            }
            catch (Exception ex) { ShowError(ex); }
            return;
        }
        try
        {
            if (selected.All(asset => asset.Kind == ResourceKind.McallAnimation))
            {
                using var packageDialog = AnimationPackageOpenDialog(selected.Count > 1);
                if (packageDialog.ShowDialog(this) != DialogResult.OK) return;
                if (packageDialog.FileNames.Length != selected.Count)
                    throw new InvalidOperationException($"需要 {selected.Count} 个动画包，实际选择 {packageDialog.FileNames.Length} 个。");
                var packages = packageDialog.FileNames.Select(ValidateAnimationPackage).ToArray();
                CommitVerified(new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Replace,
                    $"替换 {selected.Count} 个 Mcall 动画包", selected.Zip(packages).Select(pair =>
                        new MutationFile(pair.First.Frames.Single().SourcePath, pair.Second, "替换对应 Mcall 独立文件")).ToArray(),
                    Array.Empty<string>(), false));
                return;
            }
            if (selected.All(asset => asset.Category == ImageCategory.StrategyAnimation))
            {
                using var packageDialog = AnimationPackageOpenDialog(true);
                if (packageDialog.ShowDialog(this) != DialogResult.OK) return;
                if (packageDialog.FileNames.Length != selected.Count)
                    throw new InvalidOperationException($"需要 {selected.Count} 个动画包，实际选择 {packageDialog.FileNames.Length} 个。");
                var packages = packageDialog.FileNames.Select(ValidateAnimationPackage).ToArray();
                var sourcePath = selected[0].Frames.Single().SourcePath;
                if (selected.Any(asset => !asset.Frames.Single().SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("一次替换只能处理同一 Meff 档案内的动画条目。");
                var replacements = selected.Zip(packages).ToDictionary(pair => pair.First.Frames.Single().ImageNumber,
                    pair => pair.Second);
                CommitVerified(new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Replace,
                    $"替换 {selected.Count} 个 Meff 动画包",
                    [new MutationFile(sourcePath, E5Archive.OpenFresh(sourcePath).Rebuild(replacementDecoded: replacements),
                        "替换经过结构验证的策略动画包")], Array.Empty<string>(), false));
                return;
            }
            if (selected.All(asset => asset.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon))
            {
                using var itemDialog = ImageOpenDialog(true);
                if (itemDialog.ShowDialog(this) != DialogResult.OK) return;
                if (itemDialog.FileNames.Length != selected.Count)
                    throw new InvalidOperationException($"宝物替换只选择大图：需要 {selected.Count} 张，实际选择 {itemDialog.FileNames.Length} 张。");
                CommitVerified(_mutations.PlanReplace(_project, selected, itemDialog.FileNames));
                return;
            }
            if (selected.All(asset => asset.Kind is ResourceKind.DllBitmap or ResourceKind.FaceComposite) &&
                selected.Select(asset => asset.Kind).Distinct().Count() == 1)
            {
                using var dllDialog = ImageOpenDialog(true);
                if (dllDialog.ShowDialog(this) != DialogResult.OK) return;
                CommitVerified(_mutations.PlanReplace(_project, selected, dllDialog.FileNames));
                return;
            }
            using var dialog = ImageOpenDialog(true);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            CommitVerified(_mutations.PlanReplace(_project, selected, dialog.FileNames));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ImportMapPair()
    {
        if (_project == null) return;
        var selected = SelectedAssets();
        if (selected.Count != 1 || selected[0].Kind != ResourceKind.MapPair)
        { MessageBox.Show(this, "请先勾选一张地图。", "导入地图与地形"); return; }
        if (!EnsureWritable(selected)) return;
        var paths = selected[0].Frames.Select(frame => frame.SourcePath)
            .Concat(_project.TryGetResource("Hexzmap.e5") is { } hex ? [hex] : Array.Empty<string>());
        var snapshot = CaptureSources(paths); if (snapshot == null) return;
        using var folder = new FolderBrowserDialog { Description = $"选择底图素材与 {selected[0].DisplayName}.BMP 地形层所在目录；底图自动转换目标MOD规格，地形层保持原始网格" };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        try { Commit(snapshot.Complete(_mapPairs.PlanImport(_project, selected[0], folder.SelectedPath))); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ExportImages()
    {
        if (_project == null) return;
        var selected = SelectedAssets();
        if (selected.Count == 0) { ShowSelectionRequired(); return; }
        using var folder = new FolderBrowserDialog { Description = "选择导出目录" };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            foreach (var asset in selected)
            {
                if (asset.Kind is ResourceKind.RComposite or ResourceKind.SComposite)
                {
                    _strips.Export(_project, asset, folder.SelectedPath);
                    continue;
                }
                if (asset.Kind == ResourceKind.MapPair)
                {
                    _mapPairs.Export(_project, asset, folder.SelectedPath);
                    continue;
                }
                var frames = _catalog.DecodeFrames(_project, asset);
                try
                {
                    if (asset.Kind == ResourceKind.WeatherSpecial)
                    {
                        using var strip = new Bitmap(WeatherResourceCodec.FrameWidth,
                            WeatherResourceCodec.FrameHeight * WeatherResourceCodec.FrameCount, PixelFormat.Format24bppRgb);
                        using (var graphics = Graphics.FromImage(strip))
                            foreach (var frame in frames)
                                graphics.DrawImageUnscaled(frame.Bitmap, 0, frame.Descriptor.FrameIndex * WeatherResourceCodec.FrameHeight);
                        strip.Save(Path.Combine(folder.SelectedPath, Sanitize(asset.DisplayName) + ".png"), ImageFormat.Png);
                        continue;
                    }
                    if (asset.Category == ImageCategory.StrategyAnimation)
                    {
                        var source = asset.Frames.Single();
                        var package = StrategyAnimationResourceCodec.ReadPackageBytes(source);
                        File.WriteAllBytes(Path.Combine(folder.SelectedPath,
                            Sanitize(asset.DisplayName) + ".animation.bin"), package);
                    }
                    foreach (var frame in frames.Where(frame => asset.Category is not (ImageCategory.ItemIcon or ImageCategory.StrategyIcon) ||
                                                              frame.Descriptor.Action == "large"))
                    {
                        var fileName = $"{Sanitize(asset.DisplayName)}_{frame.Descriptor.Action}_{frame.Descriptor.Turn}_{frame.Descriptor.FrameIndex:000}.png";
                        frame.Bitmap.Save(Path.Combine(folder.SelectedPath, fileName), ImageFormat.Png);
                    }
                }
                finally { foreach (var frame in frames) frame.Dispose(); }
            }
            _status.Text = $"已导出 {selected.Count} 个逻辑图片到 {folder.SelectedPath}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Play()
    {
        if (_project == null) return;
        var asset = FocusedAsset();
        if (asset == null) { ShowFocusRequired(); return; }
        if (IsAnimatedAsset(asset))
        {
            if (_assetFailures.TryGetValue(asset.StablePhysicalId, out var error)) _animationPreview.ShowFailure(asset, error);
            else if (_animationPreview.AssetId == asset.StablePhysicalId) _animationPreview.Toggle();
            else await _animationPreview.BindAsync(_project, _catalog, asset);
            return;
        }
        try
        {
            var frames = _catalog.DecodeFrames(_project, asset);
            var interval = 100;
            if (asset.Category == ImageCategory.StrategyAnimation && asset.Frames.Count == 1)
            {
                var source = asset.Frames[0];
                var bytes = StrategyAnimationResourceCodec.ReadPackageBytes(source);
                using var animation = StrategyAnimationDecoder.Decode(_project, bytes);
                interval = animation.IntervalMs;
            }
            var playbackFrames = frames.ToArray();
            using var dialog = new AnimationDialog(asset.DisplayName,
                playbackFrames.Select(frame => new Bitmap(frame.Bitmap)).ToArray(), interval);
            foreach (var frame in frames) frame.Dispose();
            dialog.ShowDialog(this);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ExportIndividualFrames()
    {
        if (_project == null) return;
        var selected = SelectedAssets();
        if (selected.Count == 0) { ShowSelectionRequired(); return; }
        using var folder = new FolderBrowserDialog { Description = "选择单帧 PNG 导出目录；S 形象按类型和物理编号分类" };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            foreach (var asset in selected)
            {
                var destination = asset.Kind == ResourceKind.SComposite
                    ? Path.Combine(folder.SelectedPath, SResourceExchangeNaming.RelativePath(asset))
                    : Path.Combine(folder.SelectedPath, Sanitize(asset.DisplayName));
                Directory.CreateDirectory(destination);
                var frames = _catalog.DecodeFrames(_project, asset);
                try
                {
                    foreach (var frame in frames)
                    {
                        var name = asset.Kind == ResourceKind.SComposite
                            ? $"{SResourceExchangeNaming.BaseName(asset)}_{frame.Descriptor.Action}_{frame.Descriptor.FrameIndex:000}.png"
                            : $"{Sanitize(asset.DisplayName)}_{frame.Descriptor.Action}_{frame.Descriptor.Direction}_{frame.Descriptor.FrameIndex:000}.png";
                        frame.Bitmap.Save(Path.Combine(destination, name), ImageFormat.Png);
                    }
                }
                finally { foreach (var frame in frames) frame.Dispose(); }
            }
            _status.Text = $"已按物理图号导出 {selected.Count} 项的独立 PNG 帧。";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowAllFrames()
    {
        if (_project == null) return;
        var asset = FocusedAsset();
        if (asset == null) { ShowFocusRequired(); return; }
        try
        {
            var frames = _catalog.DecodeFrames(_project, asset);
            using var dialog = new AllFramesDialog(asset.DisplayName, frames);
            try
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    Commit(_rsImports.PlanImport(_project, asset, dialog.ResultFrames));
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void PixelEdit()
    {
        if (_project == null) return;
        var asset = FocusedAsset();
        if (asset == null) { ShowFocusRequired(); return; }
        if (!EnsureWritable([asset])) return;
        var sourceSnapshot = CaptureSources(asset.Frames.Select(frame => frame.SourcePath));
        if (sourceSnapshot == null) return;
        void CommitVerified(MutationPlan candidate, bool reload = true) => Commit(sourceSnapshot.Complete(candidate), reload);
        try
        {
            if (asset.Kind is ResourceKind.RComposite or ResourceKind.SComposite)
            {
                PixelEditRs(asset);
                return;
            }
            var descriptor = asset.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon
                ? asset.Frames.FirstOrDefault(frame => frame.Action == "large") ?? asset.Frames[^1]
                : asset.Frames.First();
            using var source = asset.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon
                ? PreviewLoader.LoadDescriptor(_project, _catalog, asset, descriptor)
                  ?? throw new InvalidOperationException("无法读取宝物大图。")
                : asset.Category == ImageCategory.StrategyAnimation ? LoadAnimationPlane(descriptor)
                : PreviewLoader.LoadRepresentative(_project, _catalog, asset, descriptor.Turn)
                               ?? throw new InvalidOperationException("无法读取待编辑帧。");
            using var editor = new PixelEditorDialog(source, asset.DisplayName,
                [new PixelEditorSection("图片", new Rectangle(Point.Empty, source.Size), 0, descriptor.SourcePath)],
                null, asset.Kind is not (ResourceKind.McallAnimation or ResourceKind.WeatherSpecial));
            if (editor.ShowDialog(this) != DialogResult.OK || editor.ResultBitmap == null) return;
            MutationPlan plan;
            if (asset.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon)
            {
                plan = _mutations.PlanItemIconEdit(_project, asset, editor.ResultBitmap,
                    editor.SaveMode == PixelSaveMode.AppendCopy);
            }
            else if (asset.Kind == ResourceKind.MapPair)
            {
                using var stream = new MemoryStream();
                editor.ResultBitmap.Save(stream, ImageFormat.Jpeg);
                plan = new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.PixelEdit,
                    "编辑战场地图底图 " + asset.DisplayName,
                    [new MutationFile(descriptor.SourcePath, stream.ToArray(), "替换地图 JPG")],
                    ["本次只修改底图，Hexzmap 地形层保持不变；成对变更请使用替换图片导入 JPG+BMP。"], true);
            }
            else if (asset.Kind == ResourceKind.FaceComposite)
            {
                var faceFrame = asset.Frames.First(frame => frame.SourceKey.Equals("Face.e5", StringComparison.OrdinalIgnoreCase));
                using var stream = new MemoryStream();
                editor.ResultBitmap.Save(stream, ImageFormat.Png);
                var faceArchive = E5Archive.OpenFresh(faceFrame.SourcePath);
                var facePayload = FacePayloadService.Rebuild(_project, faceArchive.ReadDecoded(faceFrame.ImageNumber), editor.ResultBitmap);
                var faceCandidate = editor.SaveMode == PixelSaveMode.AppendCopy
                    ? faceArchive.Rebuild(appendDecoded: [facePayload])
                    : faceArchive.Rebuild(replacementDecoded: new Dictionary<int, byte[]> { [faceFrame.ImageNumber] = facePayload });
                var files = new List<MutationFile> { new(faceFrame.SourcePath, faceCandidate, "头像像素写回 Face") };
                var touFrame = asset.Frames.FirstOrDefault(frame => frame.SourceKey.Equals("Tou.dll", StringComparison.OrdinalIgnoreCase));
                if (touFrame != null)
                {
                    var touCandidate = editor.SaveMode == PixelSaveMode.AppendCopy
                        ? DllBitmapMutation.BuildAppendSinglesCandidate(touFrame.SourcePath, [editor.ResultBitmap])
                        : DllBitmapMutation.BuildReplacementCandidate(touFrame.SourcePath,
                            [(touFrame.ImageNumber, touFrame.SourceOffset, editor.ResultBitmap, touFrame.Width, touFrame.Height)]);
                    files.Add(new MutationFile(touFrame.SourcePath, touCandidate, "同步像素写回 Tou"));
                }
                plan = new MutationPlan(Guid.NewGuid().ToString("N"),
                    editor.SaveMode == PixelSaveMode.AppendCopy ? MutationKind.Append : MutationKind.PixelEdit,
                    (editor.SaveMode == PixelSaveMode.AppendCopy ? "复制并追加 " : "像素写回 ") + asset.DisplayName,
                    files, touFrame == null ? ["Tou.dll 缺失：按小头像降级模式写入。"] : Array.Empty<string>(), false);
            }
            else if (asset.Kind == ResourceKind.DllBitmap)
            {
                if (editor.SaveMode == PixelSaveMode.AppendCopy)
                {
                    using var copy = new Bitmap(editor.ResultBitmap);
                    var candidate = DllBitmapMutation.BuildAppendNativeSinglesCandidate(descriptor.SourcePath, [copy]);
                    plan = new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Append, "复制并追加 " + asset.DisplayName,
                        [new MutationFile(descriptor.SourcePath, candidate, "按原生 ID 追加独立 RT_BITMAP，保留素材尺寸")],
                        ["现有稀疏 ID 保持；新增图标不会自动更新游戏引用。"], false);
                }
                else
                {
                    var replacements = asset.Frames.Select(frame =>
                        (frame.ImageNumber, frame.SourceOffset, editor.ResultBitmap, frame.Width, frame.Height)).ToArray();
                    var candidate = DllBitmapMutation.BuildReplacementCandidate(descriptor.SourcePath, replacements);
                    plan = new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.PixelEdit, "像素写回 " + asset.DisplayName,
                        [new MutationFile(descriptor.SourcePath, candidate, "像素写回 RT_BITMAP")], Array.Empty<string>(), false);
                }
            }
            else if (asset.Category == ImageCategory.StrategyAnimation)
            {
                var wholeFile = descriptor.ImageNumber == 0;
                var sourceBytes = StrategyAnimationResourceCodec.ReadPackageBytes(descriptor);
                var codec = new StrategyAnimationPackageCodec();
                using var package = codec.Decode(_project, sourceBytes);
                var plane = package.PlaybackSequence.First();
                var packageBytes = codec.RebuildFramePlanes(_project, package,
                    new Dictionary<int, Bitmap> { [plane] = editor.ResultBitmap });
                if (wholeFile && editor.SaveMode == PixelSaveMode.AppendCopy)
                    throw new InvalidOperationException("Mcall 复制新增需要新文件编号与项目资源注册，当前仅允许写回现有动画包。");
                var candidate = wholeFile
                    ? packageBytes
                    : editor.SaveMode == PixelSaveMode.AppendCopy
                        ? E5Archive.OpenFresh(descriptor.SourcePath).Rebuild(appendDecoded: [packageBytes])
                        : E5Archive.OpenFresh(descriptor.SourcePath).Rebuild(
                            replacementDecoded: new Dictionary<int, byte[]> { [descriptor.ImageNumber] = packageBytes });
                plan = new MutationPlan(Guid.NewGuid().ToString("N"),
                    editor.SaveMode == PixelSaveMode.AppendCopy ? MutationKind.Append : MutationKind.PixelEdit,
                    (editor.SaveMode == PixelSaveMode.AppendCopy ? "复制动画包并追加 " : "写回动画帧 ") + asset.DisplayName,
                    [new MutationFile(descriptor.SourcePath, candidate, "保留 20 字节记录、标志与尾部，仅更新帧平面")],
                    ["本次编辑对应播放序列的首帧；动画记录顺序及未知字段保持不变。"], false);
            }
            else
            {
                plan = _mutations.PlanReplaceSingleFrame(_project, asset, descriptor, editor.ResultBitmap,
                    editor.SaveMode == PixelSaveMode.AppendCopy);
            }
            CommitVerified(plan);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void PixelEditRs(LogicalImageAsset asset)
    {
        if (_project == null) return;
        var sourceSnapshot = CaptureSources(asset.Frames.Select(frame => frame.SourcePath));
        if (sourceSnapshot == null) return;
        void CommitVerified(MutationPlan candidate, bool reload = true) => Commit(sourceSnapshot.Complete(candidate), reload);
        var decoded = _catalog.DecodeFrames(_project, asset);
        try
        {
            var sourceSections = asset.EditableSections.Count > 0
                ? asset.EditableSections
                : asset.Frames.GroupBy(frame => (frame.SourceKey, frame.SourcePath, frame.ImageNumber))
                    .Select(group =>
                    {
                        var first = group.First();
                        return new EditableSourceSection(first.Action, first.SourceKey, first.SourcePath, first.ImageNumber,
                            first.Width, first.Height * group.Count(), first.Width, first.Height, group.Count());
                    }).ToArray();
            var width = sourceSections.Max(section => section.Width);
            var height = sourceSections.Sum(section => section.Height);
            using var megaCanvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var placements = new List<(EditableSourceSection Source, PixelEditorSection Ui, FrameDescriptor[] Frames)>();
            using (var graphics = Graphics.FromImage(megaCanvas))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                var y = 0;
                foreach (var section in sourceSections)
                {
                    var descriptors = asset.Frames.Where(frame =>
                            frame.SourcePath.Equals(section.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                            frame.ImageNumber == section.ImageNumber)
                        .OrderBy(frame => frame.FrameIndex).ToArray();
                    foreach (var descriptor in descriptors)
                    {
                        var frame = decoded.First(item => SameDescriptor(item.Descriptor, descriptor));
                        graphics.DrawImageUnscaled(frame.Bitmap, 0, y + descriptor.FrameIndex * descriptor.Height);
                    }
                    var bounds = new Rectangle(0, y, section.Width, section.Height);
                    var ui = new PixelEditorSection(section.Name, bounds, section.FrameHeight, section.SourcePath);
                    placements.Add((section, ui, descriptors));
                    y += section.Height;
                }
            }

            using var editor = new PixelEditorDialog(megaCanvas, asset.DisplayName,
                placements.Select(item => item.Ui).ToArray(), new PaletteService().Load(_project),
                allowAppendCopy: asset.Kind != ResourceKind.SComposite || asset.SMode != SImageMode.Job);
            if (editor.ShowDialog(this) != DialogResult.OK || editor.ResultBitmap == null) return;
            var editedFrames = new List<DecodedFrame>(asset.Frames.Count);
            try
            {
                foreach (var placement in placements)
                foreach (var descriptor in placement.Frames)
                {
                    var rectangle = new Rectangle(placement.Ui.Bounds.X,
                        placement.Ui.Bounds.Y + descriptor.FrameIndex * descriptor.Height,
                        descriptor.Width, descriptor.Height);
                    var bitmap = ImageRendering.Crop(editor.ResultBitmap, rectangle);
                    var original = decoded.First(item => SameDescriptor(item.Descriptor, descriptor));
                    var unchanged = ReferenceAtlasService.PixelHash(bitmap) == ReferenceAtlasService.PixelHash(original.Bitmap);
                    editedFrames.Add(new DecodedFrame(descriptor, bitmap,
                        unchanged ? original.PaletteIndexes?.ToArray() : null,
                        original.PaletteBgra?.ToArray()));
                }
                var plan = editor.SaveMode == PixelSaveMode.AppendCopy
                    ? _rsImports.PlanAppendCopy(_project, asset, editedFrames, SelectedSContext().Turn)
                    : _rsImports.PlanImport(_project, asset, editedFrames);
                CommitVerified(plan);
            }
            finally { foreach (var frame in editedFrames) frame.Dispose(); }
        }
        finally { foreach (var frame in decoded) frame.Dispose(); }
    }

    private Bitmap LoadAnimationPlane(FrameDescriptor descriptor)
    {
        var bytes = StrategyAnimationResourceCodec.ReadPackageBytes(descriptor);
        using var package = StrategyAnimationDecoder.Decode(_project!, bytes);
        // Editing targets the native plane. Playback may add record offsets or
        // opacity on a larger canvas, which must never be baked into storage.
        return new Bitmap(package.PlaneFrames[package.PlaybackSequence[0]]);
    }

    private static bool SameDescriptor(FrameDescriptor left, FrameDescriptor right)
        => left.SourcePath.Equals(right.SourcePath, StringComparison.OrdinalIgnoreCase) &&
           left.ImageNumber == right.ImageNumber && left.FrameIndex == right.FrameIndex && left.Action == right.Action;

    private void ExportAtlas()
    {
        if (!Has(ImagePageCapabilities.ReferenceAtlas) || _project == null) return;
        var selected = SelectedAssets();
        if (selected.Count == 0) { ShowSelectionRequired(); return; }
        using var options = new ImageExchangeOptionsDialog(true, SelectedSContext().Turn);
        if (options.ShowDialog(this) != DialogResult.OK) return;
        using var folder = new FolderBrowserDialog { Description = "选择参考总图导出目录" };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            foreach (var asset in selected)
            {
                var frames = _catalog.DecodeFrames(_project, asset);
                try { _atlases.Export(folder.SelectedPath, ReferenceAtlasContext.Create(asset, frames, SelectedSContext().Turn), ProjectKeyColor(), options.AtlasOptions); }
                finally { foreach (var frame in frames) frame.Dispose(); }
            }
            _status.Text = $"已导出 {selected.Count} 套当前预览参考图（{options.AtlasOptions.Scale} 倍）和格式提示词。";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ImportAtlas()
    {
        if (!Has(ImagePageCapabilities.ReferenceAtlas) || _project == null) return;
        var assets = SelectedAssets();
        if (assets.Count == 0) { ShowSelectionRequired(); return; }
        if (!ChooseSImportNaming(assets, out var naming)) return;
        var sourceSnapshot = CaptureSources(assets.SelectMany(asset => asset.Frames).Select(frame => frame.SourcePath));
        if (sourceSnapshot == null) return;
        void CommitVerified(MutationPlan candidate, bool reload = true) => Commit(sourceSnapshot.Complete(candidate), reload);
        using var dialog = new OpenFileDialog { Filter = "PNG 参考总图 (*.png)|*.png", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var filesByAsset = MatchAtlasFiles(assets, dialog.FileNames, naming);
            var imports = new List<(LogicalImageAsset Asset, IReadOnlyList<DecodedFrame> Frames)>();
            try
            {
                foreach (var asset in assets)
                {
                    var referenceFrames = _catalog.DecodeFrames(_project, asset);
                    try
                    {
                        var context = ReferenceAtlasContext.Create(asset, referenceFrames, SelectedSContext().Turn);
                        using var preview = new AiAtlasImportDialog(_atlases, filesByAsset[asset], context, referenceFrames);
                        if (preview.ShowDialog(this) != DialogResult.OK) return;
                        imports.Add((asset, preview.ConfirmedFrames));
                    }
                    finally { foreach (var frame in referenceFrames) frame.Dispose(); }
                }
                CommitVerified(_rsImports.PlanImportMany(_project, imports));
                _status.Text = $"已通过 AI 归一化预览导入 {imports.Count} 个 R/S 逻辑形象。";
            }
            finally { foreach (var frame in imports.SelectMany(item => item.Frames)) frame.Dispose(); }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static IReadOnlyDictionary<LogicalImageAsset, string> MatchAtlasFiles(
        IReadOnlyList<LogicalImageAsset> assets, IReadOnlyList<string> paths, RsImportNaming naming = RsImportNaming.Physical)
    {
        if (assets.Count == 1)
        {
            if (paths.Count != 1) throw new InvalidDataException("单个逻辑形象每次只选择一张 PNG；文件名可以任意。");
            return new Dictionary<LogicalImageAsset, string> { [assets[0]] = paths[0] };
        }
        if (paths.Count != assets.Count)
            throw new InvalidDataException($"批量导入需要 {assets.Count} 张 PNG，实际选择 {paths.Count} 张。");

        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".transparent", StringComparison.OrdinalIgnoreCase))
                name = name[..^".transparent".Length];
            if (!byName.TryAdd(name, path))
                throw new InvalidDataException($"{name} 同时选择了键色版和透明版，无法判断应导入哪一张。");
        }
        var result = new Dictionary<LogicalImageAsset, string>();
        foreach (var asset in assets)
        {
            var expected = SResourceExchangeNaming.ImportBaseName(asset, naming);
            if (!byName.TryGetValue(expected, out var path))
                throw new InvalidDataException($"批量导入缺少 {expected}.png；S 文件按所选的物理编号或旧编号格式匹配，不能自动推测编号基准。");
            result[asset] = path;
        }
        return result;
    }

    private bool ChooseSImportNaming(IReadOnlyList<LogicalImageAsset> assets, out RsImportNaming naming)
    {
        naming = RsImportNaming.Physical;
        var sAssets = assets.Where(asset => asset.Kind == ResourceKind.SComposite).ToArray();
        if (sAssets.Length == 0) return true;
        using var dialog = new Form
        {
            Text = "S 形象导入编号格式", StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(620, 330)
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = "选择素材的编号格式，并核对本次导入的物理目标。旧编号不会自动识别。" }, 0, 0);
        var format = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 10, 0, 10) };
        format.Items.AddRange(["物理编号：将领 S_242 / 兵种 S_002", "旧编号：将领 S002 → 242 / 兵种 兵种S001 → 2"]);
        var targets = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
        format.SelectedIndexChanged += (_, _) => targets.Text = string.Join(Environment.NewLine,
            sAssets.Select(asset => SResourceExchangeNaming.DescribeImport(asset,
                format.SelectedIndex == 1 ? RsImportNaming.Legacy : RsImportNaming.Physical)));
        layout.Controls.Add(format, 0, 1); layout.Controls.Add(targets, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "按此格式选择素材", AutoSize = true, DialogResult = DialogResult.OK };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); layout.Controls.Add(buttons, 0, 3);
        dialog.Controls.Add(layout); dialog.AcceptButton = ok; dialog.CancelButton = cancel; format.SelectedIndex = 0;
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        naming = format.SelectedIndex == 1 ? RsImportNaming.Legacy : RsImportNaming.Physical;
        return true;
    }

    private Color ProjectKeyColor()
        => ReferenceAtlasService.ResolveKeyColor(new PaletteService().Load(_project!));

    private void BatchRename()
    {
        if (_project == null) return;
        using var dialog = new BatchRenameDialog(_renames, _project);
        dialog.ShowDialog(this);
    }

    private void OpenDocumentation()
    {
        BuiltInDocumentation.Show(this);
    }

    private void Commit(MutationPlan plan, bool reloadSelectedResource = true)
    {
        if (_project == null) return;
        if (plan.RequiresRiskConfirmation && MessageBox.Show(this, string.Join(Environment.NewLine, plan.Warnings),
                "确认立即写回", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var result = _mutations.Commit(_project, plan);
        if (!result.Success) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
        _previews.InvalidatePaths(result.WrittenPaths);
        _animationPreview.InvalidatePaths(result.WrittenPaths);
        foreach (var resource in _resources.Where(resource => resource.Paths.Intersect(result.WrittenPaths, StringComparer.OrdinalIgnoreCase).Any()))
        {
            _resourceLoads.Remove(resource.Key + ":person");
            _resourceLoads.Remove(resource.Key + ":job");
        }
        if (reloadSelectedResource) LoadSelectedResource(preserveState: true);
        ResourcesChanged?.Invoke(result.WrittenPaths);
        _status.Text = "已立即保存并复读校验：" + result.Summary + "；历史编号 " + result.HistoryId;
    }

    private MutationPlanningSnapshot? CaptureSources(IEnumerable<string> paths)
    {
        try { return MutationPlanningSnapshot.Capture(paths); }
        catch (Exception ex) { ShowError(ex); return null; }
    }

    private void UpdateWriteConstraints()
    {
        var resource = SelectedResource();
        if (_project == null || resource == null) { _writeConstraints.Text = ""; return; }
        if (_project.IsReadOnly || !resource.CanMutate)
        {
            _writeConstraints.Text = "当前资源只读：" + resource.Status;
            return;
        }
        var selected = SelectedAssets();
        var focused = FocusedAsset();
        if (_buttons.TryGetValue("pixel", out var pixel))
            pixel.Enabled = focused != null && !_project.IsReadOnly && ResourceForAsset(focused)?.CanMutate == true &&
                focused.Kind != ResourceKind.PmapSpecial;
        if (_buttons.TryGetValue("play", out var play)) play.Enabled = focused != null && !_assetFailures.ContainsKey(focused.StablePhysicalId);
        if (_buttons.TryGetValue("frames", out var framesButton)) framesButton.Enabled = focused != null;
        var first = selected.FirstOrDefault();
        var selection = first == null ? "请选择目标查看尺寸与帧数。" :
            $"已选 {selected.Count} 项；首项 {first.Frames.Count} 帧" +
            (first.Frames.Where(frame => frame.Width > 0 && frame.Height > 0)
                .Select(frame => $"{frame.Width}×{frame.Height}").Distinct().ToArray() is { Length: > 0 } dimensions
                ? "；单帧 " + string.Join(" / ", dimensions) : "；替换尺寸按目标图片校验") + "。";
        if (first?.Category == ImageCategory.StrategyAnimation)
            selection = $"已选 {selected.Count} 项；动画帧数、画布与播放顺序按动画包记录解析。";
        string rule;
        if (resource.Kind == ResourceKind.McallAnimation)
        {
            var ids = _project.ResourcePaths.Values.Select(ParseMcallFileName).Where(item => item.HasValue)
                .Select(item => item!.Value.Id).ToArray();
            var next = ids.Select(id => (long)id).DefaultIfEmpty(-1).Max() + 1;
            var remaining = Math.Max(0L, 155 - next);
            rule = $"Mcall 编号 0～154；下个编号 {next}；还能追加 {remaining} 个独立动画包。";
            if (_buttons.TryGetValue("add", out var add)) add.Enabled = remaining > 0;
        }
        else if (resource.Kind == ResourceKind.SComposite && _sMode.SelectedIndex == 1)
            rule = "兵种 S 对应 Unit 图号 1～240；导入原位替换，删除仅清空；不能追加或移动编号。";
        else if (resource.Kind == ResourceKind.SComposite)
            rule = "将领 S 按物理图号逐张管理，每项28帧；mov 48×528、atk 64×768、spc 48×240，三文件同步写回。";
        else if (resource.Kind == ResourceKind.RComposite)
            rule = "新增须提供 front/back 两条 48×1280 动作条，各20帧；按目标调色板写回。";
        else if (resource.Kind == ResourceKind.MapPair)
            rule = "固定地图槽；替换图片只改底图并自动转为目标JPG画布；“导入地图与地形”同步写入同名BMP专用地形网格。";
        else if (resource.Kind == ResourceKind.WeatherSpecial)
            rule = $"天气新增与替换自动适配216×200整条并按目标调色板编码；固定4个{WeatherResourceCodec.FrameWidth}×{WeatherResourceCodec.FrameHeight}帧；可末尾新增，禁止删除或重命名编号。";
        else if (resource.Kind == ResourceKind.PmapSpecial)
            rule = "Pmap新增与替换自动适配目标MOD画布及调色板，保留已验证的16字节专用头；禁止删除/重命名编号，像素编辑暂不可用。";
        else if (resource.Kind == ResourceKind.FaceComposite)
            rule = resource.Paths.Any(path => Path.GetFileName(path).Equals("Tou.dll", StringComparison.OrdinalIgnoreCase))
                ? "头像按目标模板编码并同步 Face/Tou；新增扩容，现有编号保持。" : "头像按目标模板编码；Tou.dll 缺失，本次仅写 Face。";
        else if (resource.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase))
        {
            var nextId = _assets.Select(asset => (long)asset.LogicalId).DefaultIfEmpty(-1).Max() + 1;
            rule = $"Meff容器可扩容；策略映射为单字节（0～254；255无动画），下个逻辑ID={nextId}。新增/替换须通过校验的动画包；像素编辑首播放平面，保留记录及尾部。";
        }
        else if (resource.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon)
            rule = "大图接受任意尺寸并适配目标MOD画布；小图可选，未给则由大图最近邻派生；成对写回并保留孤立尾条目。";
        else if (resource.Kind == ResourceKind.DllBitmap)
        {
            var nextId = _assets.SelectMany(asset => asset.Frames).Select(frame => (long)frame.ImageNumber).DefaultIfEmpty(0).Max() + 1;
            var remaining = Math.Max(0L, ushort.MaxValue - nextId + 1);
            rule = $"独立位图保留稀疏原生 ID；素材自动适配目标MOD画布及DIB编码，新增按目标模板转换；下个 ID={nextId}，还能追加 {remaining} 张。";
            if (_buttons.TryGetValue("add", out var add)) add.Enabled = remaining > 0;
        }
        else rule = "新增与替换接受任意尺寸图片，自动适配目标MOD的画布、透明规则与存储编码；实际候选可在转换预览核对。";
        var fixedJob = resource.Kind == ResourceKind.SComposite && _sMode.SelectedIndex == 1;
        if (_buttons.TryGetValue("delete", out var delete))
            delete.Enabled = fixedJob || resource.Kind != ResourceKind.WeatherSpecial && resource.Kind != ResourceKind.MapPair && resource.Kind != ResourceKind.PmapSpecial &&
                !selected.Any(asset => asset.IsFixedSlot);
        if (selected.Count > 0 && !fixedJob && resource.Kind is not (ResourceKind.WeatherSpecial or ResourceKind.PmapSpecial or ResourceKind.MapPair))
        {
            if (selected.Any(asset => asset.IsFixedSlot)) selection += " 所选包含固定槽，禁止物理删除。";
            else if (resource.Kind == ResourceKind.SComposite)
            {
                var ids = selected.Select(SResourceExchangeNaming.PhysicalId).Order().ToArray();
                var last = _assets.Select(SResourceExchangeNaming.PhysicalId).DefaultIfEmpty(ids[0]).Max();
                selection += $" 仅物理删除图号 {string.Join("、", ids)}；" + (ids[0] < last
                    ? $"原图号 {ids[0] + 1}～{last} 中未删除的条目前移；Data 引用不自动调整。"
                    : "删除末项，其余图号保持。" );
            }
            else if (resource.Kind == ResourceKind.McallAnimation)
                selection += " 删除仅移除所选独立文件，其他编号保持；原引用将缺少动画。";
            else if (resource.Kind == ResourceKind.DllBitmap &&
                resource.Category is not (ImageCategory.ItemIcon or ImageCategory.StrategyIcon))
                selection += " 删除仅移除所选原生 ID，其他 ID 与内容保持；该 ID 的游戏引用将缺少图像。";
            else
            {
                var firstDeleted = selected.Min(asset => asset.LogicalId);
                var last = _assets.Select(asset => asset.LogicalId).DefaultIfEmpty(firstDeleted).Max();
                selection += firstDeleted < last
                    ? $" 删除后，原 ID {firstDeleted + 1}～{last} 中未删除的后续项会前移；引用不会自动修正。"
                    : " 删除末项不会前移其余编号；该编号的引用不会自动修正。";
            }
        }
        _writeConstraints.Text = "资源操作会立即保存。" + rule + Environment.NewLine + selection;
        _writeConstraints.Text += focused == null ? " 当前项：请点击缩略图。" : " 当前项：" + focused.DisplayName + "。";
        _paletteTip.SetToolTip(_writeConstraints, _writeConstraints.Text);
    }

    private void SetWriteEnabled(bool enabled)
    {
        foreach (var key in new[] { "add", "delete", "replace", "map-pair", "pixel", "atlas-import", "rename" })
            if (_buttons.TryGetValue(key, out var button)) button.Enabled = enabled;
    }

    private void ConfigureWriteButtons(ImageResourceDescriptor resource)
    {
        var writable = _project is { IsReadOnly: false } && resource.CanMutate;
        if (_buttons.TryGetValue("add", out var addButton))
            addButton.Text = resource.Kind == ResourceKind.SComposite && _sMode.SelectedIndex == 1 ? "导入兵种形象" : "新增图片";
        if (_buttons.TryGetValue("delete", out var deleteButton))
            deleteButton.Text = resource.Kind == ResourceKind.SComposite && _sMode.SelectedIndex == 1 ? "清空兵种形象" : "删除图片";
        if (_buttons.TryGetValue("map-pair", out var mapPairButton)) mapPairButton.Visible = resource.Kind == ResourceKind.MapPair;
        SetWriteEnabled(writable);
        foreach (var key in new[] { "add", "delete", "replace", "map-pair", "pixel", "atlas-import", "rename" })
            if (_buttons.TryGetValue(key, out var button))
                _paletteTip.SetToolTip(button, "此入口完成后立即写回磁盘，并记录可撤回历史。");
        if (resource.Kind == ResourceKind.WeatherSpecial)
            foreach (var key in new[] { "delete", "rename" })
                if (_buttons.TryGetValue(key, out var button)) button.Enabled = false;
        if (resource.Kind == ResourceKind.PmapSpecial)
            foreach (var key in new[] { "delete", "rename", "pixel" })
                if (_buttons.TryGetValue(key, out var button)) button.Enabled = false;
        if (resource.Kind == ResourceKind.MapPair && _buttons.TryGetValue("add", out var mapAdd))
        {
            mapAdd.Enabled = false;
            _paletteTip.SetToolTip(mapAdd, "地图使用固定槽，请替换底图或导入地图与地形。");
        }
    }

    private void SetAllSelected(bool selected)
        => _gallery.SelectAll(selected);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var cancellation = _renderCancellation;
            _renderCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
            var loading = _loadCancellation;
            _loadCancellation = null;
            loading?.Cancel();
            loading?.Dispose();
            ++_loadRevision;
            _paletteTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private static OpenFileDialog ImageOpenDialog(bool multi) => new()
    {
        Filter = ImageImportSourceDiscovery.FileFilter,
        Multiselect = multi
    };

    private static OpenFileDialog AnimationPackageOpenDialog(bool multi) => new()
    {
        Filter = "策略动画包 (*.e5;*.bin)|*.e5;*.bin|所有文件 (*.*)|*.*",
        Multiselect = multi,
        Title = "选择包含播放记录与已验证图片载荷的完整动画包"
    };

    private byte[] ValidateAnimationPackage(string path)
    {
        if (_project == null) throw new InvalidOperationException("尚未打开项目。");
        var bytes = File.ReadAllBytes(path);
        using var package = new StrategyAnimationPackageCodec().Decode(_project, bytes);
        return bytes;
    }

    private static (int Id, int Digits)? ParseMcallFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!stem.StartsWith("Mcall", StringComparison.OrdinalIgnoreCase)) return null;
        var digits = new string(stem.Skip(5).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var id) ? (id, digits.Length) : null;
    }

    private static string Sanitize(string name)
        => string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static void ShowSelectionRequired() => MessageBox.Show("请先勾选至少一个图片。", "未选择图片");
    private static void ShowFocusRequired() => MessageBox.Show("请先点击缩略图，选择当前项。", "没有当前项");

    private ImageResourceDescriptor? ResourceForAsset(LogicalImageAsset asset)
        => _resources.FirstOrDefault(resource => resource.Key.Equals(asset.ResourceKey, StringComparison.OrdinalIgnoreCase));

    private bool EnsureWritable(IReadOnlyList<LogicalImageAsset> assets)
    {
        var invalid = assets.FirstOrDefault(asset => ResourceForAsset(asset)?.CanMutate != true || _assetFailures.ContainsKey(asset.StablePhysicalId));
        if (_project?.IsReadOnly == false && invalid == null) return true;
        ShowError(new InvalidOperationException(invalid == null ? "当前项目只读。" : invalid.DisplayName + "：" + invalid.Status));
        return false;
    }
    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);


    private sealed record ResourceItem(ImageResourceDescriptor Descriptor, bool IsAll = false)
    {
        public override string ToString() => Descriptor.DisplayName;
    }

    private sealed record AssetLoadResult(List<LogicalImageAsset> Assets, IReadOnlyDictionary<string, string> Failures);
}

