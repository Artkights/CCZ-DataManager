using DataManager.Core.Domain;
using DataManager.Core.Csv;
using DataManager.Core.Editing;

namespace DataManager.App;

public sealed partial class MainForm
{
    internal void LoadProjectForSmokeTest(string path)
    {
        var report = _workspace.Open(path);
        ApplyOpenedProject(report, path);
    }

    internal void SelectModuleForSmokeTest(string key)
    {
        var tab = _moduleTabs.TabPages.Cast<TabPage>().Single(page => Equals(page.Tag, key));
        _moduleTabs.SelectedTab = tab;
        SelectModuleTab();
    }

    internal ProjectWorkspace WorkspaceForSmokeTest => _workspace;
    internal BusinessEditorPanel BusinessEditorForSmokeTest => _businessEditor;
    internal ListBox EntityListForSmokeTest => _entityList;
    internal TabControl ModuleTabsForSmokeTest => _moduleTabs;
    internal string StatusForSmokeTest => _statusLabel.Text;
    internal void UndoDraftForSmokeTest() => DraftUndo();
    internal DataManager.Core.Models.SaveResult SavePendingForSmokeTest() => CommitPendingEdits();
    internal DataGridView PhysicalGridForSmokeTest => _grid;
    internal void ShowPhysicalTableForSmokeTest(string tableKey)
    {
        _tableSelector.SelectedItem = _tableSelector.Items.Cast<TableChoice>().Single(choice => choice.Key == tableKey);
        SelectTable();
    }

    internal IReadOnlyList<PageCsvExport> ExportBusinessCsvForSmokeTest(string directory)
    {
        if (_businessKind is not { } kind || !_businessEditor.Flush())
            throw new InvalidOperationException("当前业务页有无效编辑，不能导出。");
        return new BusinessDataService(_workspace).ExportPageCsv(kind, directory);
    }

    internal CsvBundleImportResult ImportBusinessCsvForSmokeTest(IReadOnlyDictionary<string, string> files)
    {
        if (_businessKind is not { } kind || !_businessEditor.Flush())
            throw new InvalidOperationException("当前业务页有无效编辑，不能导入。");
        var result = new BusinessDataService(_workspace).ImportPageCsvAtomic(kind, files);
        RefreshBusinessSelection();
        RefreshDirtyState();
        return result;
    }

    private static BusinessPageKind? GetBusinessKind(string? key) => key switch
    {
        "人物" => BusinessPageKind.Persons,
        "兵种系" => BusinessPageKind.JobSeries,
        "兵种" => BusinessPageKind.DetailedJobs,
        "宝物" => BusinessPageKind.Items,
        "策略" => BusinessPageKind.Strategies,
        "商店" => BusinessPageKind.Shop,
        "拓展数据" => BusinessPageKind.ExtendData,
        _ => null
    };

    private void ShowBusinessView(BusinessPageKind kind)
    {
        ActivateBusinessEditor(kind);
        _businessKind = kind;
        _currentDocument = null;
        _historyFilter.Visible = false;
        if (kind == BusinessPageKind.ExtendData)
        {
            _businessEditor.Bind(kind, _businessEditor.ExtendEditor?.CurrentId ?? 0);
            RefreshExtendEntityList();
            _grid.Visible = false; _imageEditor.Visible = false; _businessEditor.Visible = true;
            return;
        }
        var rememberedId = _businessEditor.CurrentId;
        var options = new BusinessDataService(_workspace).GetIdNameOptions(kind);
        _selectingBusinessEntity = true;
        _entityList.BeginUpdate();
        try
        {
            _entityList.Items.Clear();
            foreach (var option in options) _entityList.Items.Add(new EntityChoice(option.Id, option.Name));
            _quickLookup.SetOptions(options.Select(option => new IdNameOption(option.Id, option.Name)));
            if (_entityList.Items.Count > 0)
                _entityList.SelectedIndex = Math.Max(0, _entityList.Items.Cast<EntityChoice>().ToList().FindIndex(option => option.Id == rememberedId));
        }
        finally { _entityList.EndUpdate(); _selectingBusinessEntity = false; }
        _businessEditor.Bind(kind, (_entityList.SelectedItem as EntityChoice)?.Id ?? 0);
        _grid.Visible = false; _imageEditor.Visible = false; _businessEditor.Visible = true;
        SetStatus($"业务详情：{options.Count} 项。关联表编辑统一进入保存事务。");
    }

    private void SelectBusinessEntity(int id)
    {
        if (_selectingBusinessEntity || _businessKind == null) return;
        if (!_businessEditor.Flush())
        {
            _selectingBusinessEntity = true;
            try
            {
                _entityList.SelectedIndex = _entityList.Items.Cast<object>().ToList().FindIndex(item => item is EntityChoice entity && entity.Id == _businessEditor.CurrentId);
            }
            finally { _selectingBusinessEntity = false; }
            return;
        }
        var index = _entityList.Items.Cast<object>().ToList().FindIndex(item => item is EntityChoice entity && entity.Id == id);
        if (index < 0) { SetStatus($"当前业务页没有 ID={id}。", true); return; }
        _selectingBusinessEntity = true;
        try
        {
            _entityList.SelectedIndex = index;
            _businessEditor.Bind(_businessKind.Value, id);
        }
        catch (Exception ex) { SetStatus("详情读取失败：" + ex.Message, true); }
        finally { _selectingBusinessEntity = false; }
    }

    private void RefreshBusinessSelection()
    {
        if (_businessKind == BusinessPageKind.ExtendData && _businessEditor.ExtendEditor is { } extend)
        {
            extend.RefreshFromWorkspace();
            RefreshExtendEntityList();
            return;
        }
        if (_businessKind is { } kind && _entityList.SelectedItem is EntityChoice entity)
            _businessEditor.Bind(kind, entity.Id);
    }

    private void RefreshSavedNames()
    {
        if (_businessKind is { } kind)
        {
            if (kind == BusinessPageKind.ExtendData)
            {
                RefreshExtendEntityList();
                _businessEditor.Bind(kind, _businessEditor.CurrentId);
                return;
            }
            var id = _businessEditor.CurrentId;
            var options = new BusinessDataService(_workspace).GetIdNameOptions(kind);
            _selectingBusinessEntity = true;
            _entityList.BeginUpdate();
            try
            {
                _entityList.Items.Clear();
                foreach (var option in options) _entityList.Items.Add(new EntityChoice(option.Id, option.Name));
                _quickLookup.SetOptions(options.Select(option => new IdNameOption(option.Id, option.Name)));
                _entityList.SelectedIndex = _entityList.Items.Cast<EntityChoice>().ToList().FindIndex(option => option.Id == id);
            }
            finally { _entityList.EndUpdate(); _selectingBusinessEntity = false; }
            _businessEditor.Bind(kind, id);
        }
        else if (_currentDocument is { } document)
        {
            var selectedId = _grid.CurrentRow?.DataBoundItem is System.Data.DataRowView view ? Convert.ToInt32(view.Row["ID"]) : -1;
            _selectingBusinessEntity = true;
            try
            {
                PopulateEntityList(document); PopulateQuickLookup(document);
                _entityList.SelectedIndex = _entityList.Items.Cast<EntityChoice>().ToList().FindIndex(option => option.Id == selectedId);
            }
            finally { _selectingBusinessEntity = false; }
        }
    }

    private void RefreshExtendEntityList(bool keepSearch = true)
    {
        if (_businessKind != BusinessPageKind.ExtendData || _businessEditor.ExtendEditor is not { } panel) return;
        var options = panel.GetEntityOptions();
        var query = keepSearch ? panel.SearchText : string.Empty;
        var filtered = string.IsNullOrWhiteSpace(query) ? options : options.Where(option =>
            option.Id.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            option.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        _selectingBusinessEntity = true;
        _entityList.BeginUpdate();
        try
        {
            _entityList.Items.Clear();
            foreach (var option in filtered) _entityList.Items.Add(new EntityChoice(option.Id, option.Name));
            _entityList.SelectedIndex = _entityList.Items.Cast<EntityChoice>().ToList().FindIndex(option => option.Id == panel.CurrentId);
            _quickLookup.SetOptions(options);
            if (_quickLookup.Text != query) _quickLookup.SetBoundValue(query);
        }
        finally { _entityList.EndUpdate(); _selectingBusinessEntity = false; }
        SetStatus($"{panel.DetailTabs.SelectedTab?.Text}：{options.Count} 项；编辑进入草稿，顶部保存统一写回。");
    }
}
