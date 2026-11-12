using DataManager.Core.Domain;
using DataManager.Core.Csv;
using DataManager.Core.Editing;

namespace DataManager.App;

public sealed partial class MainForm
{
    internal void LoadProjectForSmokeTest(string path)
    {
        path = DataManager.Core.Binary.ProjectRootLocator.Resolve(path);
        RangeManager.App.RangeSaveTransaction.Recover(path);
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
        var choice = _tableSelector.Items.Cast<TableChoice>().Single(choice => choice.Key == tableKey);
        // A changed selection raises SelectedIndexChanged synchronously. Invoke
        // explicitly only when the current item was already selected.
        if (Equals(_tableSelector.SelectedItem, choice)) SelectTable();
        else _tableSelector.SelectedItem = choice;
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
        RefreshQueryNavigation();
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
        _businessEditor.Bind(kind, _businessEditor.CurrentId);
        if (kind == BusinessPageKind.ExtendData)
        {
            _businessEditor.Bind(kind, _businessEditor.ExtendEditor?.CurrentId ?? 0);
            _grid.Visible = false; _imageEditor.Visible = false; _businessEditor.Visible = true;
            RefreshExtendEntityList();
            return;
        }
        _grid.Visible = false; _imageEditor.Visible = false; _businessEditor.Visible = true;
        RefreshQueryNavigation();
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

    private void RefreshSavedNames() => RefreshQueryNavigation();

    private void RefreshExtendEntityList(bool keepSearch = true)
    {
        if (_businessKind != BusinessPageKind.ExtendData || _businessEditor.ExtendEditor is not { } panel) return;
        if (!keepSearch && QueryPageKey is { } key)
        {
            ResetCurrentQuery(key);
        }
        RefreshQueryNavigation();
    }
}
