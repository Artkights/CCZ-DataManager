using System.Data;
using System.Globalization;
using DataManager.Core.Models;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.App;

public sealed partial class MainForm
{
    private async void LocatePersonSImage(int personId, int sId, int jobId)
    {
        try
        {
            // A malformed unused person row must not prevent locating this verified target.
            if (!_workspace.Documents.TryGetValue("person-s-assign", out var assignments) ||
                assignments.Definition.Capability != TableCapability.Editable ||
                assignments.Definition.VerificationLevel == VerificationLevel.InheritedUnverified ||
                string.IsNullOrWhiteSpace(assignments.Definition.CapabilityEvidence) ||
                !assignments.Data.Columns.Contains("S形象ID") || sId is < 0 or >= ushort.MaxValue)
                throw new InvalidOperationException("当前人物 S 指定未通过布局验证，不能据此定位物理形象。");
            var root = _workspace.RootPath;
            var candidates = SImageLocationCandidates.Create(sId, jobId);
            var selected = candidates[0].PhysicalImageNumber;
            if (candidates.Count > 1)
            {
                using var choose = new SImageLocationDialog(_resourceAdapter ??= new(), root, personId, sId, candidates);
                if (choose.ShowDialog(this) != DialogResult.OK) return;
                selected = choose.SelectedPhysicalImageNumber;
            }
            _lastResourceCategory = "S";
            var tab = _moduleTabs.TabPages.Cast<TabPage>().Single(page => Equals(page.Tag, "__resources"));
            _moduleTabs.SelectedTab = tab;
            if (!_resourceModule || _workspace.RootPath != root) return;
            if (await _imageEditor.LocateSPhysicalImageAsync(selected))
                SetStatus($"按人物 {personId} 当前草稿 S={sId} 定位至 Unit #{selected}；图片导入即时保存，存档同步按已保存指定。 ");
            else SetStatus($"无法定位完整 Unit #{selected}，请检查当前项目的三个动作资源。", true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
        {
            SetStatus("S 形象定位失败：" + ex.Message, true);
        }
    }

    private IReadOnlyList<SImageAssignmentReference>? GetCurrentSAssignmentReferences()
    {
        // Reuse the same evidence gate as the physical idle filter without changing its policy.
        if (GetVerifiedUsedImageIds("S") == null ||
            !_workspace.Documents.TryGetValue("person-s-assign", out var assignmentDocument)) return null;
        _workspace.Documents.TryGetValue("persons", out var personDocument);
        var people = new Dictionary<int, DataRow>();
        if (personDocument != null)
            for (var index = 0; index < personDocument.Data.Rows.Count; index++)
                people[CurrentPersonId(personDocument, index)] = personDocument.Data.Rows[index];
        var mapping = new SPhysicalMappingService();
        var references = new List<SImageAssignmentReference>();
        for (var index = 0; index < assignmentDocument.Data.Rows.Count; index++)
        {
            var row = assignmentDocument.Data.Rows[index];
            var id = CurrentPersonId(assignmentDocument, index);
            if (!int.TryParse(Convert.ToString(row["S形象ID"], CultureInfo.InvariantCulture), out var sId)) return null;
            people.TryGetValue(id, out var person);
            var name = person?.Table.Columns.Contains("名称") == true
                ? Convert.ToString(person["名称"], CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(name)) name = "（未命名）";
            var job = 0;
            if (sId == 0 && (person == null || !person.Table.Columns.Contains("兵种") ||
                !int.TryParse(Convert.ToString(person["兵种"], CultureInfo.InvariantCulture), out job))) return null;
            var physical = mapping.ResolveUsedPhysicalIds([new CharacterAssignment(id, name, job, 0, sId)]).Order().ToArray();
            references.Add(new SImageAssignmentReference(id, name, sId, physical));
        }
        return references;
    }

    private static int CurrentPersonId(TableDocument document, int index)
        => document.Data.Columns.Contains("ID")
            ? Convert.ToInt32(document.Data.Rows[index]["ID"], CultureInfo.InvariantCulture)
            : document.Definition.BeginId + index;
}
