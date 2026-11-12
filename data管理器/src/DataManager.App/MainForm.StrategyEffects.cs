using System.Globalization;
using DataManager.Core.Csv;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class MainForm
{
    private bool ConfirmStrategyCsvImport(IReadOnlyDictionary<string, string> files)
    {
        if (!files.ContainsKey(StrategyEffectClassService.TableKey)) return true;
        var edits = new List<CompositeCellEdit>();
        var csv = new CsvService();
        foreach (var (key, path) in files.Where(pair => pair.Key is "strategies" or StrategyEffectClassService.TableKey))
        {
            var document = _workspace.GetDocument(key);
            // Parse into an isolated in-memory copy so declining the preview
            // leaves both the user's draft and command history untouched.
            var candidate = new TableDocument
            {
                Definition = document.Definition, FilePath = document.FilePath, Data = document.Data.Copy(),
                OriginalFileBytes = document.OriginalFileBytes, OriginalFileHash = document.OriginalFileHash
            };
            var context = new CsvContext(_workspace.ActiveProfile.ProfileId, _workspace.ActiveProfile.VariantId,
                key, _workspace.ActiveProfile.LayoutRevision);
            var result = csv.Import(candidate, path, context: context);
            edits.AddRange(result.Changes.Select(change => new CompositeCellEdit(key, change.RowIndex, change.ColumnName, change.NewValue)));
        }
        foreach (var edit in edits)
            _workspace.ValidateCellValue(_workspace.GetDocument(edit.TableKey), edit.RowIndex, edit.FieldName, edit.NewValue, edits);
        var warnings = edits.Where(edit => edit.TableKey == StrategyEffectClassService.TableKey)
            .Select(edit => StrategyEffectClassService.GetCompatibilityWarning(_workspace, edit.RowIndex,
                Convert.ToInt32(edit.NewValue, CultureInfo.InvariantCulture), edits))
            .Where(warning => !string.IsNullOrEmpty(warning)).Select(warning => warning!).Distinct().ToArray();
        if (warnings.Length == 0) return true;
        var details = string.Join(Environment.NewLine, warnings.Take(8));
        if (warnings.Length > 8) details += $"\n另有 {warnings.Length - 8} 项同类提示。";
        return MessageBox.Show(this, details + "\n是否继续导入？", "策略效果导入确认",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
