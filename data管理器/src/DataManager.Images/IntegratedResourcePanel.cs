using ImageManager.App;
using ImageManager.Core;

namespace DataManager.Images;

/// <summary>Hosts the original image editor controls so their mutation and recovery behavior stays identical.</summary>
public sealed class IntegratedResourcePanel : UserControl
{
    private readonly ResourceCatalogAdapter _adapter;
    private readonly IImageCatalogService _catalog;
    private readonly bool _ownsAdapter;
    private readonly ImagePreviewService _previews = new();
    private ProjectProfile? _project;
    private ImageGridPage? _page;
    private string _category = "R";
    private Func<string, IReadOnlySet<int>?>? _usedIdsProvider;
    private Func<IReadOnlyList<SImageAssignmentReference>?>? _sAssignmentReferencesProvider;
    private readonly TextBox _search = new() { Width = 260, PlaceholderText = "输入编号或名称筛选资源" };
    private readonly Label _diagnostic = new() { AutoSize = true };
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, CachedPage> _pages = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ImageResourceDescriptor> _resources = [];
    private long _projectRevision;
    private bool _restoringSearch;
    private Task _loadingTask = Task.CompletedTask;
    private long _catalogRevision = -1;
    private long _activationOrder;
    private bool _trimmingPlayback;
    private string? _loadingRoot;
    public Task LoadingTask => _loadingTask.IsCompleted && _page != null ? _page.LoadingTask : _loadingTask;
    public static IReadOnlyList<(string Key, string Name)> Categories { get; } =
        [("R", "R 形象"), ("S", "S 形象"), ("Face", "头像"), ("Tou", "大头像"), ("Item", "宝物图标"),
         ("Strategy", "策略图标"), ("LargeAnimation", "大动画"), ("SmallAnimation", "小动画"),
         ("AttackRange", "攻击范围"), ("PierceRange", "穿透范围")];
    public IReadOnlyList<(string Key, string Name)> ProjectCategories { get; private set; } = Categories;
    public event Action? CategoriesChanged;

    public IntegratedResourcePanel() : this(null) { }

    public IntegratedResourcePanel(ResourceCatalogAdapter? adapter)
    {
        _ownsAdapter = adapter == null;
        _adapter = adapter ?? new ResourceCatalogAdapter();
        _catalog = _adapter.CreateImageCatalogService();
        Dock = DockStyle.Fill;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false, Padding = new Padding(4) };
        bar.Controls.Add(_search); bar.Controls.Add(_diagnostic);
        Controls.Add(_content); Controls.Add(bar);
        _search.TextChanged += (_, _) =>
        {
            if (_restoringSearch) return;
            _page?.SetSearch(_search.Text);
            if (_pages.TryGetValue(_category, out var entry)) entry.Search = _search.Text;
        };
    }

    public void LoadProject(string root, string category = "R", Func<string, IReadOnlySet<int>?>? usedIdsProvider = null,
        bool forceReload = false, Func<IReadOnlyList<SImageAssignmentReference>?>? sAssignmentReferencesProvider = null)
    {
        _sAssignmentReferencesProvider = sAssignmentReferencesProvider;
        if (!forceReload && !_loadingTask.IsCompleted && string.Equals(_loadingRoot, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            _usedIdsProvider = usedIdsProvider;
            _category = category;
            CategoriesChanged?.Invoke();
            return;
        }
        if (!forceReload && _project != null && Path.GetFullPath(root).Equals(_project.RootPath, StringComparison.OrdinalIgnoreCase)
            && _catalogRevision == _adapter.Revision)
        {
            _usedIdsProvider = usedIdsProvider;
            foreach (var key in new[] { "R", "S" })
            {
                if (!_pages.TryGetValue(key, out var entry)) continue;
                IReadOnlySet<int>? ids = null;
                try { ids = usedIdsProvider?.Invoke(key); } catch { }
                if (entry.HasVerifiedReferences != (ids != null))
                {
                    if (_page == entry.Page) _page = null;
                    entry.Page.Dispose();
                    _pages.Remove(key);
                }
                else entry.Page.UpdateUsedIds(ids);
            }
            ShowCategory(category);
            _loadingTask = _page?.LoadingTask ?? Task.CompletedTask;
            CategoriesChanged?.Invoke();
            return;
        }
        _loadingTask = LoadProjectAsync(root, category, usedIdsProvider);
    }

    public void RefreshResources()
    {
        if (_project == null) return;
        _adapter.Invalidate(_project.RootPath);
        LoadProject(_project.RootPath, _category, _usedIdsProvider, forceReload: true,
            sAssignmentReferencesProvider: _sAssignmentReferencesProvider);
    }

    public void ClearProject()
    {
        ++_projectRevision;
        var paths = _resources.SelectMany(resource => resource.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _page = null;
        _project = null;
        _usedIdsProvider = null;
        _sAssignmentReferencesProvider = null;
        foreach (var entry in _pages.Values) entry.Page.Dispose();
        _pages.Clear();
        _content.Controls.Clear();
        _resources = [];
        _catalogRevision = -1;
        ProjectCategories = Categories;
        _loadingTask = Task.CompletedTask;
        _loadingRoot = null;
        _previews.InvalidatePaths(paths);
        RestoreSearch(string.Empty);
        _diagnostic.Text = string.Empty;
    }

    private async Task LoadProjectAsync(string root, string category, Func<string, IReadOnlySet<int>?>? usedIdsProvider)
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        if (_project != null && !Path.GetFullPath(root).Equals(_project.RootPath, StringComparison.OrdinalIgnoreCase)) ClearProject();
        var revision = ++_projectRevision;
        _loadingRoot = Path.GetFullPath(root);
        _usedIdsProvider = usedIdsProvider;
        _category = category;
        _diagnostic.Text = "正在读取资源目录…";
        CategoriesChanged?.Invoke();
        try
        {
            var loaded = await Task.Run(() =>
            {
                var project = _adapter.GetProjectProfile(root);
                return (Project: project, Resources: _catalog.BuildCatalog(project));
            });
            if (IsDisposed || revision != _projectRevision) return;
            _page = null;
            foreach (var entry in _pages.Values) entry.Page.Dispose();
            _pages.Clear();
            _content.Controls.Clear();
            _project = loaded.Project;
            _resources = loaded.Resources;
            _catalogRevision = _adapter.Revision;
            ProjectCategories = BuildProjectCategories(_resources);
            CategoriesChanged?.Invoke();
            ShowCategory(_category);
            if (_page != null) await _page.LoadingTask;
        }
        catch (Exception ex)
        {
            if (!IsDisposed && revision == _projectRevision) _diagnostic.Text = "资源读取失败：" + ex.Message;
        }
        finally { if (revision == _projectRevision) _loadingRoot = null; }
    }

    public void ShowCategory(string category)
    {
        // Old saved selections continue to open a useful resource page.
        if (category.Equals("Resource:Pmap.e5", StringComparison.OrdinalIgnoreCase) ||
            category.Equals("Pmap.e5", StringComparison.OrdinalIgnoreCase)) category = "Resource:Mmap.e5";
        category = category switch
        {
            "History" => "R",
            "Resource:Pmap.e5" or "Pmap.e5" => "Resource:Mmap.e5",
            _ => category
        };
        if (_project == null) { _category = category; return; }
        if (_category == category && _page != null) { _page.ActivateGallery(); return; }
        if (_page != null)
        {
            if (_pages.TryGetValue(_category, out var previous)) previous.Search = _search.Text;
            _page.SetActive(false);
            _page.Visible = false;
            _content.Controls.Remove(_page);
        }
        _category = category;
        if (_pages.TryGetValue(category, out var cached))
        {
            _page = cached.Page;
            RestoreSearch(cached.Search);
            _content.Controls.Add(_page);
            _page.Visible = true;
            _page.ActivateGallery();
            cached.LastActivated = ++_activationOrder;
            EnforcePlaybackBudget();
            _diagnostic.Text = cached.Diagnostic;
            return;
        }
        var history = ProjectImageHistory.Create(_project);
        IReadOnlySet<int>? usedIds = null;
        var referenceDiagnostic = string.Empty;
        if (category is "R" or "S")
        {
            try { usedIds = _usedIdsProvider?.Invoke(category)?.Where(id => id >= 0).ToHashSet(); }
            catch (Exception ex) { referenceDiagnostic = "：" + ex.Message; }
        }
        var capabilities = category switch
        {
            "R" => ImagePageCapabilities.CharacterRs,
            "S" => ImagePageCapabilities.CharacterS,
            "Item" => ImagePageCapabilities.ItemIcon,
            "Animation" or "LargeAnimation" or "SmallAnimation" => ImagePageCapabilities.Standard | ImagePageCapabilities.Play,
            _ => ImagePageCapabilities.Standard
        };
        var resources = _resources.Where(resource => MatchesCategory(resource, category)).ToArray();
        if (resources.Any(resource => resource.Key.Equals("Mmap.e5", StringComparison.OrdinalIgnoreCase)))
            capabilities |= ImagePageCapabilities.SceneTerrainEdit;
        if (resources.Any(resource => resource.Kind is ResourceKind.WeatherSpecial or ResourceKind.DtAnimation)) capabilities |= ImagePageCapabilities.Play;
        if (category is "R" or "S" && usedIds == null)
        {
            capabilities &= ~ImagePageCapabilities.IdleFilter;
            referenceDiagnostic = " · 人物引用未验证，空闲筛选已隐藏" + referenceDiagnostic;
        }
        var title = ProjectCategories.FirstOrDefault(item => item.Key == category).Name ??
                    (category == "Other" ? "其他图片" : category == "Animation" ? "策略动画" : "图片资源");
        _page = new ImageGridPage(title, _catalog,
            new LiveImageMutationService(_adapter, history), new ReferenceAtlasService(), new RsImportService(),
            new ProjectBatchRenameService(_project), _previews, capabilities);
        _page.FileImports = new LiveImageImportPreparationService();
        _page.SAssignmentReferencesProvider = () => _sAssignmentReferencesProvider?.Invoke();
        _page.ProjectStructureChanged += () =>
        {
            if (_project == null) return;
            _adapter.Invalidate(_project.RootPath);
            LoadProject(_project.RootPath, _category, _usedIdsProvider,
                sAssignmentReferencesProvider: _sAssignmentReferencesProvider);
        };
        var createdPage = _page;
        _page.ResourcesChanged += paths =>
        {
            _catalogRevision = _adapter.Revision;
            foreach (var entry in _pages.Values)
                if (entry.Page != createdPage) entry.Page.InvalidateResources(paths);
        };
        _page.PlaybackCacheChanged += EnforcePlaybackBudget;
        _content.Controls.Add(_page);
        RestoreSearch(string.Empty);
        _page.LoadProject(_project, resources, usedIds);
        _page.ActivateGallery();
        _diagnostic.Text = $"{resources.Length} 组" + (_project.IsReadOnly ? " · 只读" : "") + referenceDiagnostic;
        _pages[category] = new CachedPage(_page, _diagnostic.Text, usedIds != null) { LastActivated = ++_activationOrder };
        EnforcePlaybackBudget();
    }

    private void EnforcePlaybackBudget()
    {
        if (_trimmingPlayback || _page == null) return;
        _trimmingPlayback = true;
        try
        {
            var remainingCount = Math.Max(0, 2 - _page.RetainedAnimationCount);
            var remainingBytes = Math.Max(0, 128L * 1024 * 1024 - _page.RetainedAnimationBytes);
            foreach (var entry in _pages.Values.Where(entry => entry.Page != _page).OrderByDescending(entry => entry.LastActivated))
            {
                entry.Page.TrimPlaybackCache(Math.Min(1, remainingCount), remainingBytes);
                remainingCount = Math.Max(0, remainingCount - entry.Page.RetainedAnimationCount);
                remainingBytes = Math.Max(0, remainingBytes - entry.Page.RetainedAnimationBytes);
            }
        }
        finally { _trimmingPlayback = false; }
    }

    private void RestoreSearch(string search)
    {
        _restoringSearch = true;
        try { _search.Text = search; }
        finally { _restoringSearch = false; }
    }

    public bool ExecuteShortcut(Keys keys)
    {
        return _page?.ExecuteShortcut(keys) == true;
    }

    private static IReadOnlyList<(string Key, string Name)> BuildProjectCategories(IReadOnlyList<ImageResourceDescriptor> resources)
    {
        var categories = Categories.Where(category => category.Key != "Tou" || resources.Any(resource => resource.Key == "Tou"))
            .Select(category => (category.Key, Name: ResourceDisplayNames.WithSources(category.Name,
                resources.Where(resource => MatchesCategory(resource, category.Key))))).ToList();
        categories.AddRange(resources.Where(IsOtherResource).OrderBy(resource => resource.DisplayName, StringComparer.CurrentCulture)
            .Select(resource => ("Resource:" + resource.Key, ResourceDisplayNames.WithSources(resource))));
        return categories;
    }

    private static bool MatchesCategory(ImageResourceDescriptor resource, string category) => category switch
    {
        "R" => resource.Category == ImageCategory.CharacterR,
        "S" => resource.Category == ImageCategory.CharacterS,
        "Face" => resource.Key == "Face",
        "Tou" => resource.Key == "Tou",
        "Item" => resource.Category == ImageCategory.ItemIcon,
        "Strategy" => resource.Category == ImageCategory.StrategyIcon,
        "LargeAnimation" => resource.Kind == ResourceKind.McallAnimation,
        "SmallAnimation" => resource.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase),
        "Animation" => resource.Category == ImageCategory.StrategyAnimation,
        "AttackRange" => resource.Key.Equals("Hitarea.e5", StringComparison.OrdinalIgnoreCase),
        "PierceRange" => resource.Key.Equals("Effarea.e5", StringComparison.OrdinalIgnoreCase),
        "Other" => IsOtherResource(resource),
        _ => resource.Key.Equals(category.StartsWith("Resource:", StringComparison.Ordinal) ? category[9..] : category,
            StringComparison.OrdinalIgnoreCase)
    };

    private static bool IsOtherResource(ImageResourceDescriptor resource)
        => resource.Category is not (ImageCategory.CharacterR or ImageCategory.CharacterS or ImageCategory.Portrait or
            ImageCategory.ItemIcon or ImageCategory.StrategyIcon or ImageCategory.StrategyAnimation) &&
           resource.Key is not ("Hitarea.e5" or "Effarea.e5") &&
           !resource.Key.Equals("Pmap.e5", StringComparison.OrdinalIgnoreCase);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearProject();
            _previews.Dispose();
            if (_ownsAdapter) _adapter.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class CachedPage(ImageGridPage page, string diagnostic, bool hasVerifiedReferences)
    {
        public ImageGridPage Page { get; } = page;
        public string Diagnostic { get; } = diagnostic;
        public bool HasVerifiedReferences { get; } = hasVerifiedReferences;
        public long LastActivated { get; set; }
        public string Search { get; set; } = string.Empty;
    }

    /// <summary>Read caches never grant write permission; each plan and commit uses a live profile.</summary>
    private sealed class LiveImageImportPreparationService : IImageImportPreparationService
    {
        private readonly ImageImportPreparationService _inner = new();
        public async Task<PreparedImageImport> PrepareAsync(ImageImportRequest request, CancellationToken cancellationToken = default)
        {
            var live = await Task.Run(() => new ProjectProfileService().Detect(request.Project.RootPath), cancellationToken);
            if (live.IsReadOnly || live.Version != request.Project.Version)
                throw new InvalidOperationException("项目版本或写入能力已变化，请重新打开项目。");
            return await IntegratedSceneMutationPolicy.PrepareAsync(_inner, request with { Project = live }, cancellationToken);
        }
    }

    /// <summary>Read caches never grant write permission; each plan and commit uses a live profile.</summary>
    private sealed class LiveImageMutationService(ResourceCatalogAdapter adapter, HistoryService history) : IImageMutationService
    {
        private readonly ImageMutationService _inner = new(history);
        private static ProjectProfile Live(ProjectProfile project)
        {
            var live = new ProjectProfileService().Detect(project.RootPath);
            if (live.IsReadOnly || live.Version != project.Version)
                throw new InvalidOperationException("项目版本或写入能力已变化，请重新打开项目。");
            return live;
        }
        public MutationPlan PlanAppend(ProjectProfile project, ImageResourceDescriptor resource, IReadOnlyList<string> sources)
        {
            var live = Live(project);
            if (!resource.CanMutate) throw new InvalidOperationException("资源当前不可修改：" + resource.Status);
            return IntegratedSceneMutationPolicy.Append(live, resource.Key, () => _inner.PlanAppend(live, resource, sources));
        }
        public MutationPlan PlanReplace(ProjectProfile project, IReadOnlyList<LogicalImageAsset> assets, IReadOnlyList<string> sources)
        {
            foreach (var asset in assets) IntegratedSceneMutationPolicy.RequireImageResource(asset.ResourceKey);
            return _inner.PlanReplace(Live(project), assets, sources);
        }
        public MutationPlan PlanDelete(ProjectProfile project, IReadOnlyList<LogicalImageAsset> assets)
        {
            var live = Live(project);
            return IntegratedSceneMutationPolicy.Delete(live, assets, () => _inner.PlanDelete(live, assets));
        }
        public MutationPlan PlanWholeFileReplace(ProjectProfile project, string targetPath, string sourcePath)
        {
            IntegratedSceneMutationPolicy.RequireImageResource(Path.GetFileName(targetPath));
            return _inner.PlanWholeFileReplace(Live(project), targetPath, sourcePath);
        }
        public MutationPlan PlanItemIconEdit(ProjectProfile project, LogicalImageAsset asset, Bitmap large, bool appendCopy)
            => _inner.PlanItemIconEdit(Live(project), asset, large, appendCopy);
        public MutationPlan PlanReplaceSingleFrame(ProjectProfile project, LogicalImageAsset asset,
            FrameDescriptor descriptor, Bitmap edited, bool appendCopy = false)
        {
            var live = Live(project);
            IntegratedSceneMutationPolicy.RequireImageResource(asset.ResourceKey);
            return appendCopy ? IntegratedSceneMutationPolicy.Append(live, asset.ResourceKey,
                    () => _inner.PlanReplaceSingleFrame(live, asset, descriptor, edited, true), descriptor.ImageNumber)
                : _inner.PlanReplaceSingleFrame(live, asset, descriptor, edited, false);
        }
        public MutationResult Commit(ProjectProfile project, MutationPlan plan)
        {
            var live = Live(project);
            IntegratedSceneMutationPolicy.ValidateCommit(live, plan);
            foreach (var file in plan.Files)
            {
                var name = Path.GetFileName(file.TargetPath);
                if (File.Exists(Path.Combine(live.RootPath, name)) && File.Exists(Path.Combine(live.RootPath, "E5", name)))
                    throw new InvalidOperationException("根目录与 E5 目录存在同名资源，请先确认目标：" + name);
            }
            try { return ProjectImageHistory.Commit(live, plan); }
            finally { adapter.Invalidate(project.RootPath); }
        }
    }

    private sealed class ProjectBatchRenameService(ProjectProfile project) : IBatchRenameService
    {
        public RenamePlan Preview(string rootPath, RenameRule rule)
            => new BatchRenameService(ProjectImageHistory.Create(project)).Preview(rootPath, rule);

        public MutationResult Apply(ProjectProfile profile, RenamePlan plan)
            => new BatchRenameService(ProjectImageHistory.PrepareForWrite(profile))
                .Apply(ProjectImageHistory.Canonicalize(profile), plan);
    }
}
