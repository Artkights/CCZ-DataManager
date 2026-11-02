using System.Globalization;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Scanner;

public sealed partial class EngineEffectAssignmentService
{
    private static readonly string[] AssignmentDocuments = ["items-low", "items-high", "items-extended", "detailed-jobs"];

    public string Fingerprint(ProjectWorkspace workspace, EngineEffectScanReport report)
    {
        var text = new StringBuilder("assignment-v2|").Append(report.ExeSha256).Append('|').Append(report.ImsgSha256);
        foreach (var key in AssignmentDocuments)
        {
            if (!workspace.Documents.TryGetValue(key, out var document)) continue;
            text.Append('|').Append(key).Append('|').Append(HashService.Sha256(document.FilePath))
                .Append('|').Append(document.Definition.RowSize).Append('|').Append(document.Definition.DataOffset);
            foreach (System.Data.DataRow row in document.Data.Rows)
                foreach (var value in row.ItemArray)
                {
                    var cell = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    text.Append('|').Append(cell.Length).Append(':').Append(cell);
                }
        }
        return HashService.Sha256(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static ProjectWorkspace CurrentAssignmentWorkspace(ProjectWorkspace workspace)
    {
        var changed = AssignmentDocuments.Where(workspace.Documents.ContainsKey)
            .Select(key => workspace.Documents[key]).Any(document =>
                !HashService.Sha256(document.FilePath).Equals(document.OriginalFileHash, StringComparison.OrdinalIgnoreCase));
        if (!changed) return workspace;
        if (workspace.IsDirty) throw new InvalidDataException("分配数据已被外部修改，且当前存在数据草稿；请先处理草稿并重新打开项目。");
        var fresh = new ProjectWorkspace();
        fresh.Open(workspace.RootPath);
        return fresh;
    }
}
