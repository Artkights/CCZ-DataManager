using System.Data;
using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Models;
using DataManager.Images;

namespace DataManager.App;

public sealed partial class MainForm
{
    private void ShowResourceView()
    {
        _businessEditor.Visible = false;
        _grid.Visible = false;
        _resourcePreview.Visible = false;
        _imageEditor.Visible = true;
        _historyFilter.Visible = false;
        _currentDocument = null;
        if (string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        try
        {
            var category = (_entityList.SelectedItem as ResourceChoice)?.Key ?? _lastResourceCategory;
            _imageEditor.LoadProject(_workspace.RootPath, category, usedIdsProvider: GetVerifiedUsedImageIds,
                sAssignmentReferencesProvider: GetCurrentSAssignmentReferences);
            RefreshResourceCategories();
            _diagnosticBox.Text = "选择左侧资源分类。新增、替换、删除、导入导出与历史由原形象管理器控件处理。删除导致编号前移时不会自动修正其他文件引用。";
            SetStatus("图片设定已载入。");
        }
        catch (Exception ex) { _diagnosticBox.Text = ex.Message; SetStatus("形象资源加载失败：" + ex.Message, true); }
    }

    private void RefreshResourceCategories()
    {
        if (!_resourceModule) return;
        var selected = (_entityList.SelectedItem as ResourceChoice)?.Key ?? _lastResourceCategory;
        var categories = _imageEditor.ProjectCategories;
        if (_entityList.Items.Count == categories.Count &&
            _entityList.Items.Cast<object>().Select((item, index) => item is ResourceChoice resource &&
                resource.Key == categories[index].Key && resource.Name == categories[index].Name).All(value => value))
        {
            // A returning resource module keeps its logical categories and
            // selected page. Rebuilding the list would fire another activation.
            if (_entityList.SelectedItem is ResourceChoice current) _lastResourceCategory = current.Key;
            return;
        }
        _entityList.BeginUpdate();
        _refreshingResourceCategories = true;
        try
        {
            _entityList.Items.Clear();
            foreach (var category in categories)
                _entityList.Items.Add(new ResourceChoice(category.Key, category.Name));
            var match = _entityList.Items.Cast<ResourceChoice>().Select((item, index) => (item, index))
                .FirstOrDefault(pair => pair.item.Key == selected);
            if (_entityList.Items.Count > 0) _entityList.SelectedIndex = match.item == null ? 0 : match.index;
            if (_entityList.SelectedItem is ResourceChoice current) _lastResourceCategory = current.Key;
        }
        finally { _refreshingResourceCategories = false; _entityList.EndUpdate(); }
        if (_entityList.SelectedItem is ResourceChoice active) _imageEditor.ShowCategory(active.Key);
    }

    private IReadOnlySet<int>? GetVerifiedUsedImageIds(string category)
    {
        if (category is not ("R" or "S")) return null;
        var dependencies = category == "R"
            ? new[] { new RecordQueryDependency("person-r-assign", "ID"), new("person-r-assign", "R形象ID") }
            : [new RecordQueryDependency("person-s-assign", "ID"), new("person-s-assign", "S形象ID"),
                new("persons", "ID"), new("persons", "兵种")];
        return WorkspaceReadCache.For(_workspace).GetOrCreateData("image-usage:" + category, dependencies,
            () => new VerifiedImageUsage(ReadVerifiedUsedImageIds(category)),
            value => 64L + (value.Ids?.Count ?? 0) * 24L).Ids;
    }

    private sealed record VerifiedImageUsage(IReadOnlySet<int>? Ids);

    private IReadOnlySet<int>? ReadVerifiedUsedImageIds(string category)
    {
        var key = category switch { "R" => "person-r-assign", "S" => "person-s-assign", _ => null };
        if (key == null || !_workspace.Documents.TryGetValue(key, out var document) ||
            document.Definition.Capability != TableCapability.Editable ||
            document.Definition.VerificationLevel == VerificationLevel.InheritedUnverified ||
            string.IsNullOrWhiteSpace(document.Definition.CapabilityEvidence)) return null;
        var name = category == "R" ? "R形象ID" : "S形象ID";
        var field = document.Definition.Fields.FirstOrDefault(field => field.Name == name);
        if (field == null || !document.Data.Columns.Contains(name) || document.Definition.DataOffset < 0 ||
            document.Definition.EndOffsetExclusive > document.OriginalFileBytes.LongLength) return null;
        if (category == "S")
        {
            // Gallery IDs are physical Unit numbers. Project the current draft's compact
            // assignments here, before crossing into the resource editor.
            var assignments = new List<ImageManager.Core.CharacterAssignment>();
            Dictionary<int, int>? jobs = null;
            foreach (DataRow row in document.Data.Rows)
            {
                if (!int.TryParse(Convert.ToString(row[name], CultureInfo.InvariantCulture), out var sId) ||
                    sId < 0 || sId >= ushort.MaxValue) return null;
                var personId = row.Table.Columns.Contains("ID") ? Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture)
                    : document.Definition.BeginId + document.Data.Rows.IndexOf(row);
                var job = 0;
                if (sId == 0)
                {
                    if (jobs == null)
                    {
                        if (!_workspace.Documents.TryGetValue("persons", out var persons) ||
                            persons.Definition.Capability != TableCapability.Editable ||
                            persons.Definition.VerificationLevel == VerificationLevel.InheritedUnverified ||
                            (persons.Definition.VerificationLevel is not (VerificationLevel.BaselineVerified or VerificationLevel.VariantVerified) &&
                                string.IsNullOrWhiteSpace(persons.Definition.CapabilityEvidence)) ||
                            !persons.Data.Columns.Contains("兵种")) return null;
                        jobs = new Dictionary<int, int>();
                        for (var index = 0; index < persons.Data.Rows.Count; index++)
                        {
                            var person = persons.Data.Rows[index];
                            var id = persons.Data.Columns.Contains("ID") ? Convert.ToInt32(person["ID"], CultureInfo.InvariantCulture)
                                : persons.Definition.BeginId + index;
                            if (int.TryParse(Convert.ToString(person["兵种"], CultureInfo.InvariantCulture), out var value)) jobs[id] = value;
                        }
                    }
                    if (!jobs.TryGetValue(personId, out job) || job is < 0 or > 79) return null;
                }
                assignments.Add(new ImageManager.Core.CharacterAssignment(personId, string.Empty, job, 0, sId));
            }
            return new ImageManager.Core.SPhysicalMappingService().ResolveUsedPhysicalIds(assignments);
        }
        var result = new HashSet<int>();
        foreach (DataRow row in document.Data.Rows)
        {
            if (int.TryParse(Convert.ToString(row[name], CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var id) && id >= Math.Max(0, field.Minimum) && id <= field.Maximum && id < ushort.MaxValue)
                result.Add(id);
        }
        return result;
    }

}
