using System.Globalization;
using System.Text;
using DataManager.Core.Csv;
using DataManager.Core.Editing;

namespace DataManager.Core.Domain;

/// <summary>Logical group CSV exchange. Import validates a draft and never writes the executable.</summary>
public sealed class AccessoryJobGroupCsvService
{
    public const string TableKey = "job-series-accessory-groups";
    private const string Marker = "#DataManagerCSV";
    public const string DefaultFileName = "兵种系-辅助装备分组.csv";
    private static readonly string[] Columns = ["分组编号", "兵种系编号列表"];
    private static readonly string[] LegacyColumns = ["GroupID", "JobSeriesIDs"];
    private readonly AccessoryJobGroupService _groups = new();

    public void Export(ProjectWorkspace workspace, string path, IReadOnlyList<IReadOnlyList<int>> groups, Encoding? encoding = null)
    {
        var preview = Validate(workspace, groups);
        var profile = workspace.ActiveProfile;
        var records = new List<IReadOnlyList<string>>
        {
            new[] { Marker, "1", profile.ProfileId, profile.VariantId, TableKey, profile.LayoutRevision }, Columns
        };
        records.AddRange(preview.Groups.Select(group => (IReadOnlyList<string>)new[]
            { group.Index.ToString(CultureInfo.InvariantCulture), string.Join(',', group.JobSeriesIds) }));
        CsvService.WriteRecords(path, records, encoding);
    }

    public IReadOnlyList<IReadOnlyList<int>> ImportDraft(ProjectWorkspace workspace, string path)
    {
        var records = CsvService.ReadRecords(path, Columns.Concat(LegacyColumns)).Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value))).ToArray();
        var profile = workspace.ActiveProfile;
        if (records.Length > 0) records[0] = CsvService.NormalizeMetadata(records[0]);
        if (records.Length < 3 || records[0].Count != 6 || records[0][0] != Marker || records[0][1] != "1")
            throw new InvalidDataException("辅助分组 CSV 缺少有效 schema 元数据或分组记录。");
        var metadata = records[0];
        if (metadata[2] != profile.ProfileId || metadata[3] != profile.VariantId || metadata[4] != TableKey || metadata[5] != profile.LayoutRevision)
            throw new InvalidDataException("辅助分组 CSV 的 Profile、Variant、TableKey 或布局修订与当前项目不匹配。");
        if (records[1].Count != 2 || !records[1].Select((column, index) => column == Columns[index] || column.Equals(LegacyColumns[index], StringComparison.OrdinalIgnoreCase)).All(valid => valid))
            throw new InvalidDataException("辅助分组 CSV 列必须为 分组编号,兵种系编号列表（兼容 GroupID,JobSeriesIDs），不能包含重复或未知列。");
        var imported = new SortedDictionary<int, IReadOnlyList<int>>();
        foreach (var row in records.Skip(2))
        {
            if (row.Count != 2 || !int.TryParse(row[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 0)
                throw new InvalidDataException("辅助分组 CSV 的 GroupID 或列数无效。");
            if (!imported.TryAdd(id, ParseIds(row[1]))) throw new InvalidDataException($"辅助分组 CSV 包含重复 GroupID：{id}。");
        }
        if (!imported.Keys.SequenceEqual(Enumerable.Range(0, imported.Count)))
            throw new InvalidDataException("辅助分组 CSV 的 GroupID 必须从 0 连续编号；分组顺序影响引擎语义。");
        var groups = imported.Values.ToArray();
        Validate(workspace, groups);
        return groups;
    }

    public AccessoryJobGroupPreview Validate(ProjectWorkspace workspace, IReadOnlyList<IReadOnlyList<int>> groups)
    {
        var preview = _groups.Preview(workspace, groups);
        if (!preview.CanWrite) throw new InvalidDataException(string.Join(Environment.NewLine, preview.Diagnostics));
        return preview;
    }

    public static IReadOnlyList<int> ParseIds(string text)
    {
        var values = text.Split([',', '，', ';', '；', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (values.Length == 0) throw new InvalidDataException("辅助分组不能为空。");
        return values.Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id : throw new InvalidDataException($"辅助分组兵种系 ID 不是整数：{value}。")).ToArray();
    }
}
