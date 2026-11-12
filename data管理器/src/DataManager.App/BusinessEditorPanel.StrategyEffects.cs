using DataManager.Core.Domain;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private bool _confirmingStrategyEffect;
    internal Func<string, bool>? StrategyEffectConfirmationForSmokeTest { get; set; }

    private bool ConfirmStrategyEffect(Control owner, int rowIndex, int newClass)
    {
        var warning = StrategyEffectClassService.GetCompatibilityWarning(_workspace, rowIndex, newClass);
        if (string.IsNullOrEmpty(warning)) return true;
        _confirmingStrategyEffect = true;
        try
        {
            return StrategyEffectConfirmationForSmokeTest?.Invoke(warning) ??
                MessageBox.Show(owner.FindForm(), warning + "\n仅修改效果索引，是否继续？", "切换策略效果",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        finally { _confirmingStrategyEffect = false; }
    }
}
