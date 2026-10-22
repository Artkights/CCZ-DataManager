using System.Data;
using System.Globalization;
using DataManager.Core.Models;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.App;

public sealed partial class MainForm
{
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
