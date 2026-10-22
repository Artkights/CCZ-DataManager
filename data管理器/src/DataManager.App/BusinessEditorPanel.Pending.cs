using DataManager.Core.Domain;
using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private readonly List<Func<bool>> _pendingChecks = [];
    private Action? _accessoryDraftRefresh;
    private string? _runtimeEvidenceHash;
    private bool _runtimeEvidenceVerified;

    private bool RuntimeConsumersVerified()
    {
        var executable = _workspace.Documents.Values.FirstOrDefault(document => Path.GetFileName(document.FilePath).Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase));
        if (executable == null) return false;
        if (_runtimeEvidenceHash != executable.OriginalFileHash)
        {
            _runtimeEvidenceHash = executable.OriginalFileHash;
            _runtimeEvidenceVerified = TextConsumerEvidence.IsVerifiedEngine(executable.OriginalFileBytes);
        }
        return _runtimeEvidenceVerified;
    }

    public bool TryUndoFocusedText(bool redo = false)
    {
        ByteLimitedTextBox? FindFocused(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is ByteLimitedTextBox text && text.ContainsFocus) return text;
                if (control.ContainsFocus && FindFocused(control) is { } child) return child;
            }
            return null;
        }
        return FindFocused(this)?.InputGuard.Undo(redo) == true;
    }

    private static TextFieldConstraint ConstraintForRow(TableDocument document, int rowIndex, TableFieldDefinition field)
    {
        var offset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        return TextFieldConstraints.Get(document.Definition, field, document.OriginalFileBytes.AsSpan(offset, document.Definition.RowSize));
    }

    public bool HasPendingChanges => _pendingChecks.Any(check => check()) || _accessoryGroupDraft?.HasChanges == true || _extendEditor?.HasPendingChanges == true;

    private void NotifyPendingChanges()
    {
        if (!_binding) PendingChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowEditorError(Control editor, string message, bool focus = true)
    {
        _errors.SetError(editor, message);
        _status.Text = message; _status.ForeColor = Color.Firebrick; _status.Visible = true;
        if (!focus) return;
        for (Control? parent = editor; parent != null; parent = parent.Parent)
            if (parent is TabPage page && page.Parent is TabControl tabs) tabs.SelectedTab = page;
        if (editor.CanFocus) editor.Focus();
    }

    public IReadOnlyList<PreparedBinaryEdit> GetPreparedEdits()
    {
        if (_accessoryGroupDraft is not { HasChanges: true } draft) return [];
        var service = new AccessoryJobGroupService();
        if (draft.SourceSignature != AccessorySourceSignature(service.Read(_workspace)))
            throw new InvalidDataException("辅助分组基准已改变。请先导出当前草稿，再重新载入分组后继续编辑。");
        return [service.PrepareSave(_workspace, draft.Snapshot(), draft.SourceHash)];
    }

    public void AcceptSavedChanges()
    {
        if (_accessoryGroupDraft != null) _accessoryGroupDraft.Reset(new AccessoryJobGroupService().Read(_workspace));
        _accessoryDraftRefresh?.Invoke(); NotifyPendingChanges();
    }

    public void DiscardPendingChanges()
    {
        _binding = true;
        try
        {
            _extendEditor?.DiscardPendingChanges();
            _extendEditor?.ResetProject();
            _accessoryGroupDraft = null; _accessoryDraftRefresh = null;
            _flushers.Clear(); _pendingChecks.Clear(); _entityRebinders.Clear(); _resourcePickerRefreshers.Clear();
            _compactLayoutSignature = null; _refreshItemPermissions = null;
            _errors.Clear(); _status.Visible = false;
            DisposeDetailPages();
        }
        finally { _binding = false; }
        NotifyPendingChanges();
    }
}
