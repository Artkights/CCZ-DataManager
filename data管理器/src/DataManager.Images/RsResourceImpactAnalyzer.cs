using System.Globalization;
using DataManager.Core.Appearance;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using ImageManager.Core;

namespace DataManager.Images;

/// <summary>A resource candidate compared with the same physical archive before this operation.</summary>
public sealed record RsResourceChange(string ResourceName, byte[] BeforeBytes, byte[]? AfterBytes);

public sealed record RsResourceImpact(IReadOnlySet<int> AffectedRIds, IReadOnlySet<int> AffectedSPhysicalNumbers)
{
    public bool HasChanges => AffectedRIds.Count != 0 || AffectedSPhysicalNumbers.Count != 0;
}

/// <summary>
/// Derives affected slots from decoded E5 payloads, independent of the image editor entry point
/// and of directory offsets, compression, display labels, and import naming conventions.
/// </summary>
public sealed class RsResourceImpactAnalyzer
{
    public static bool IsRsResourceName(string name)
        => Path.GetFileName(name).Equals("Pmapobj.e5", StringComparison.OrdinalIgnoreCase) ||
           Path.GetFileName(name).Equals("Unit_mov.e5", StringComparison.OrdinalIgnoreCase) ||
           Path.GetFileName(name).Equals("Unit_atk.e5", StringComparison.OrdinalIgnoreCase) ||
           Path.GetFileName(name).Equals("Unit_spc.e5", StringComparison.OrdinalIgnoreCase);

    public RsResourceImpact Analyze(IEnumerable<RsResourceChange> changes, CancellationToken cancellationToken = default)
    {
        var rIds = new HashSet<int>();
        var sNumbers = new HashSet<int>();
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRsResourceName(change.ResourceName)) continue;
            if (change.AfterBytes != null && change.BeforeBytes.AsSpan().SequenceEqual(change.AfterBytes)) continue;
            var before = change.BeforeBytes.Length == 0 ? null : E5Archive.Parse(change.ResourceName, change.BeforeBytes);
            var after = change.AfterBytes is not { Length: > 0 } ? null : E5Archive.Parse(change.ResourceName, change.AfterBytes);
            var beforeCount = before?.Entries.Count ?? 0;
            var afterCount = after?.Entries.Count ?? 0;
            var sameDictionary = before != null && after != null && change.BeforeBytes.AsSpan(16, 256)
                .SequenceEqual(change.AfterBytes!.AsSpan(16, 256));
            var isR = Path.GetFileName(change.ResourceName).Equals("Pmapobj.e5", StringComparison.OrdinalIgnoreCase);
            for (var number = 1; number <= Math.Max(beforeCount, afterCount); number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (number <= beforeCount && number <= afterCount)
                {
                    var oldEntry = before!.Entries[number - 1];
                    var newEntry = after!.Entries[number - 1];
                    // Rebuild preserves untouched payloads but moves directory offsets. Compare
                    // their contents without decoding the entire mod's unchanged action strips.
                    // Equal compressed bytes also require the same LS dictionary.
                    if (oldEntry.DecodedLength == newEntry.DecodedLength &&
                        oldEntry.StoredLength == newEntry.StoredLength && (!oldEntry.IsCompressed || sameDictionary) &&
                        change.BeforeBytes.AsSpan(oldEntry.DataOffset, oldEntry.StoredLength)
                            .SequenceEqual(change.AfterBytes!.AsSpan(newEntry.DataOffset, newEntry.StoredLength))) continue;
                    if (before.ReadDecoded(number).AsSpan().SequenceEqual(after.ReadDecoded(number))) continue;
                }
                if (isR) rIds.Add((number - 1) / 2);
                else sNumbers.Add(number);
            }
        }
        return new RsResourceImpact(rIds, sNumbers);
    }

    /// <summary>
    /// The caller supplies a separately opened saved/staged workspace. Never pass the UI's
    /// draft workspace when deciding what an immediate image write should copy to saves.
    /// </summary>
    public IReadOnlyList<PersonAppearanceUpdate> CreatePersonUpdates(ProjectWorkspace savedWorkspace, RsResourceImpact impact)
    {
        var updates = new SortedDictionary<int, PersonAppearanceUpdate>();
        if (impact.AffectedRIds.Count > 0)
        {
            var r = VerifiedAssignments(savedWorkspace, "person-r-assign", "R形象ID");
            for (var index = 0; index < r.Data.Rows.Count; index++)
            {
                var value = Convert.ToUInt16(r.Data.Rows[index]["R形象ID"], CultureInfo.InvariantCulture);
                if (!impact.AffectedRIds.Contains(value)) continue;
                var id = PersonId(r, index);
                updates[id] = new PersonAppearanceUpdate(id, RId: value);
            }
        }
        if (impact.AffectedSPhysicalNumbers.Count > 0)
        {
            var s = VerifiedAssignments(savedWorkspace, "person-s-assign", "S形象ID");
            savedWorkspace.Documents.TryGetValue("persons", out var persons);
            var jobs = persons?.Data.Columns.Contains("兵种") == true
                ? Enumerable.Range(0, persons.Data.Rows.Count).ToDictionary(index => PersonId(persons, index),
                    index => Convert.ToInt32(persons.Data.Rows[index]["兵种"], CultureInfo.InvariantCulture))
                : new Dictionary<int, int>();
            var mapping = new SPhysicalMappingService();
            for (var index = 0; index < s.Data.Rows.Count; index++)
            {
                var value = Convert.ToUInt16(s.Data.Rows[index]["S形象ID"], CultureInfo.InvariantCulture);
                var id = PersonId(s, index);
                var job = 0;
                // Empty/unused person rows can contain an out-of-range job. They cannot
                // reference any of the supported 80 job triplets; do not guess a mapping.
                if (value == 0 && (!jobs.TryGetValue(id, out job) || job is < 0 or > 79)) continue;
                if (!mapping.ResolveUsedPhysicalIds([new CharacterAssignment(id, string.Empty, job, 0, value)])
                        .Overlaps(impact.AffectedSPhysicalNumbers)) continue;
                updates.TryGetValue(id, out var previous);
                updates[id] = new PersonAppearanceUpdate(id, previous?.RId, value);
            }
        }
        return updates.Values.ToArray();
    }

    private static TableDocument VerifiedAssignments(ProjectWorkspace workspace, string key, string fieldName)
    {
        if (!workspace.Documents.TryGetValue(key, out var document) ||
            document.Definition.Capability != TableCapability.Editable ||
            document.Definition.VerificationLevel == VerificationLevel.InheritedUnverified ||
            string.IsNullOrWhiteSpace(document.Definition.CapabilityEvidence) || !document.Data.Columns.Contains(fieldName))
            throw new InvalidDataException($"人物{fieldName}缺少已验证的磁盘布局，未同步存档。");
        return document;
    }

    private static int PersonId(TableDocument document, int index)
        => document.Data.Columns.Contains("ID")
            ? Convert.ToInt32(document.Data.Rows[index]["ID"], CultureInfo.InvariantCulture)
            : document.Definition.BeginId + index;
}
