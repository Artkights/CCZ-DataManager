using DataManager.Core.Models;

namespace DataManager.Core.Appearance;

public sealed record PersonAppearanceUpdate(int PersonId, ushort? RId = null, ushort? SId = null);

public sealed record PreparedAppearanceSaveGroup(string SaveRelativePath, IReadOnlyList<PreparedFileEdit> Files)
{
    public int UpdatedPersonFields { get; init; }
}

public sealed record AppliedAppearanceFile(string RelativePath, byte[] BeforeBytes, byte[] AfterBytes,
    FileAttributes BeforeAttributes, FileByteChange Change);

public sealed class AppearanceSyncReport
{
    private readonly List<string> _diagnostics = [];
    public int UpdatedSaves { get; internal set; }
    public int UnchangedSaves { get; internal set; }
    public int SkippedSaves { get; internal set; }
    public int UpdatedPersonFields { get; internal set; }
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public bool HasWarnings => SkippedSaves > 0 || Diagnostics.Count > 0;
    public string Summary => $"存档形象{(HasWarnings ? "部分同步" : "同步")}：已更新 {UpdatedSaves} 个存档、{UpdatedPersonFields} 个人物形象字段，{UnchangedSaves} 个已一致，{SkippedSaves} 个跳过。";
    internal void Add(string message) => _diagnostics.Add(message);
}
