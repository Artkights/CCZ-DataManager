using System.Buffers.Binary;
using System.Text;
using Ccz.Shared;

namespace ImageManager.Core;

public sealed class CharacterAssignmentService : ICharacterAssignmentService
{
    private const int DataOffset = 396;
    private const int DataRowSize = 32;
    private const int NameLength = 13;
    private const int JobOffsetInRow = 26;
    private const int RowCount = 1024;

    static CharacterAssignmentService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public CharacterAssignmentLayoutProfile GetLayout(ProjectProfile project)
    {
        if (project.Version != ProjectVersion.Ccz64) return CharacterAssignmentLayoutProfile.For(project.Version);
        var path = project.TryGetResource("Ekd5.exe");
        return path != null && File.Exists(path)
            ? Ccz64AssignmentLayoutProbe.Inspect(File.ReadAllBytes(path))
            : CharacterAssignmentLayoutProfile.For(project.Version) with { Evidence = "缺少当前项目 Ekd5.exe，禁止猜测 6.4 R/S 偏移。" };
    }

    public IReadOnlyList<CharacterAssignment> Read(ProjectProfile project)
    {
        var dataPath = project.TryGetResource("Data.e5") ?? throw new FileNotFoundException("缺少 Data.e5。");
        var exePath = project.TryGetResource("Ekd5.exe") ?? throw new FileNotFoundException("缺少 Ekd5.exe。");
        var data = File.ReadAllBytes(dataPath);
        var executable = File.ReadAllBytes(exePath);
        if (data.Length < DataOffset + RowCount * DataRowSize) throw new InvalidDataException("Data.e5 人物表长度不足。");
        var layout = GetLayout(project);
        if (project.Version == ProjectVersion.Ccz64 && !layout.CanWrite)
            throw new InvalidDataException(layout.Evidence);
        if (executable.Length < layout.RequiredExecutableLength)
            throw new InvalidDataException($"Ekd5.exe 长度不足，指定表至少需要 {layout.RequiredExecutableLength} 字节。");
        var gbk = Encoding.GetEncoding(936);
        var rows = new CharacterAssignment[RowCount];
        for (var index = 0; index < RowCount; index++)
        {
            var nameBytes = data.AsSpan(DataOffset + index * DataRowSize, NameLength);
            var terminator = nameBytes.IndexOf((byte)0);
            if (terminator < 0) terminator = nameBytes.Length;
            var name = gbk.GetString(nameBytes[..terminator]).Trim();
            var jobId = data[DataOffset + index * DataRowSize + JobOffsetInRow];
            var r = BinaryPrimitives.ReadUInt16LittleEndian(executable.AsSpan(layout.ROffset + index * layout.FieldWidth, 2));
            var s = BinaryPrimitives.ReadUInt16LittleEndian(executable.AsSpan(layout.SOffset + index * layout.FieldWidth, 2));
            rows[index] = new CharacterAssignment(index, name, jobId, r, s);
        }
        return rows;
    }

    public MutationPlan PlanSave(ProjectProfile project, IReadOnlyList<CharacterAssignment> assignments)
        => MutationPlanningSnapshot.Build(new[] { "Ekd5.exe", "Data.e5", "Pmapobj.e5", "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5" }
                .Select(project.TryGetResource).OfType<string>(), () => PlanSaveCore(project, assignments));

    private MutationPlan PlanSaveCore(ProjectProfile project, IReadOnlyList<CharacterAssignment> assignments)
    {
        var layout = GetLayout(project);
        if (!layout.CanWrite) throw new InvalidOperationException("当前版本没有可验证的人物指定表布局，禁止写入。");
        if (project.IsReadOnly) throw new InvalidOperationException("当前项目处于只读安全模式。");
        var exePath = project.TryGetResource("Ekd5.exe") ?? throw new FileNotFoundException("缺少 Ekd5.exe。");
        var bytes = File.ReadAllBytes(exePath);
        if (bytes.Length < layout.RequiredExecutableLength) throw new InvalidDataException("Ekd5.exe 指定表长度不足。");
        var rPath = project.TryGetResource("Pmapobj.e5") ?? throw new FileNotFoundException("缺少 Pmapobj.e5。");
        var rMaximum = Math.Max(0, E5Archive.OpenFresh(rPath).Entries.Count / 2 - 1);
        var sPaths = new[] { "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5" }
            .Select(key => project.TryGetResource(key) ?? throw new FileNotFoundException("缺少 " + key + "。"));
        var physicalSMaximum = sPaths.Select(path => E5Archive.OpenFresh(path).Entries.Count).Min();
        var changed = 0;
        foreach (var row in assignments)
        {
            if (row.RowIndex < 0 || row.RowIndex >= RowCount) throw new ArgumentOutOfRangeException(nameof(assignments));
            if (row.RId is < 0 or > ushort.MaxValue || row.SId is < 0 or > ushort.MaxValue)
                throw new InvalidDataException($"第 {row.RowIndex + 1} 行 R/S 编号超出 UInt16 范围。");
            var rOffset = layout.ROffset + row.RowIndex * layout.FieldWidth;
            var sOffset = layout.SOffset + row.RowIndex * layout.FieldWidth;
            var oldR = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(rOffset, 2));
            var oldS = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(sOffset, 2));
            if (oldR != row.RId)
            {
                if (row.RId > rMaximum)
                    throw new InvalidDataException($"第 {row.RowIndex + 1} 行 R{row.RId:000} 超过完整正反组上限 R{rMaximum:000}。");
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(rOffset, 2), (ushort)row.RId);
                changed++;
            }
            if (oldS != row.SId)
            {
                var physical = SImageNumberingRules.GetAssignedPhysicalImageNumbers(row.SId, row.JobId).Max();
                if (physical > physicalSMaximum)
                    throw new InvalidDataException($"第 {row.RowIndex + 1} 行 S{row.SId:000} 映射到 Unit #{physical}，超过共同资源上限 #{physicalSMaximum}。");
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(sOffset, 2), (ushort)row.SId);
                changed++;
            }
        }
        if (changed == 0) throw new InvalidOperationException("人物 R/S 指定没有发生变化。");
        return new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.AssignmentSave,
            $"保存人物 R/S 形象指定（{changed} 个字段）",
            [new MutationFile(exePath, bytes, "写入 Ekd5.exe R/S 指定表")],
            [$"角色名与职业只读；仅写入发生变化的 UInt16 字段。布局证据：{layout.Evidence}。"], false);
    }
}
