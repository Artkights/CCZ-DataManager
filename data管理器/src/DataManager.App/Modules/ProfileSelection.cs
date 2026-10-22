using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class MainForm
{
    private void SyncProfileSelectors()
    {
        _suppressProfileEvents = true;
        try
        {
            for (var index = 0; index < _profileSelector.Items.Count; index++)
            {
                if ((_profileSelector.Items[index] as ProfileChoice)?.Id == _workspace.ActiveProfile.ProfileId) _profileSelector.SelectedIndex = index;
            }
            PopulateVariantSelector(_workspace.ActiveProfile.ProfileId, _workspace.ActiveProfile.VariantId);
            _variantSelector.Visible = true;
        }
        finally { _suppressProfileEvents = false; }
    }

    private void PopulateVariantSelector(string profileId, string selectedVariantId)
    {
        var profile = _workspace.AvailableProfiles.Single(item => item.Id == profileId);
        _variantSelector.Items.Clear();
        foreach (var variant in profile.Variants) _variantSelector.Items.Add(new VariantChoice(variant.Id, variant.DisplayName));
        for (var index = 0; index < _variantSelector.Items.Count; index++)
        {
            if ((_variantSelector.Items[index] as VariantChoice)?.Id == selectedVariantId) _variantSelector.SelectedIndex = index;
        }
        if (_variantSelector.SelectedIndex < 0 && _variantSelector.Items.Count > 0) _variantSelector.SelectedIndex = 0;
    }

    private void ProfileSelectionChanged()
    {
        if (_suppressProfileEvents || string.IsNullOrWhiteSpace(_workspace.RootPath) || _profileSelector.SelectedItem is not ProfileChoice choice) return;
        var profile = _workspace.AvailableProfiles.Single(item => item.Id == choice.Id);
        SwitchProfile(choice.Id, profile.DefaultVariantId);
    }

    private void VariantSelectionChanged()
    {
        if (_suppressProfileEvents || string.IsNullOrWhiteSpace(_workspace.RootPath) || _variantSelector.SelectedItem is not VariantChoice choice) return;
        SwitchProfile(_workspace.ActiveProfile.ProfileId, choice.Id);
    }

    private void SwitchProfile(string profileId, string variantId)
    {
        if (_workspace.ActiveProfile.ProfileId == profileId && _workspace.ActiveProfile.VariantId == variantId) return;
        if (!PreparePendingEdits()) { SyncProfileSelectors(); return; }
        if (HasPendingChanges && !ConfirmDiscardOrSave("切换 Profile/布局"))
        {
            SyncProfileSelectors();
            return;
        }
        try
        {
            var loaded = ProjectLoadDialog.LoadWorkspace(this, _workspace.RootPath, profileId, variantId);
            if (loaded == null) { SyncProfileSelectors(); return; }
            _workspace = loaded;
            var report = loaded.LastScan!;
            ApplyOpenedProject(report, _workspace.RootPath);
            SetStatus($"已手动选择 {report.ProfileDisplayName} / {report.VariantDisplayName}；覆盖仅对当前会话有效。", report.ProfileMatch?.MatchKind == ProfileMatchKind.Forced);
        }
        catch (Exception ex)
        {
            SyncProfileSelectors();
            MessageBox.Show(this, ex.Message, "切换 Profile 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed record ProfileChoice(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record VariantChoice(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
