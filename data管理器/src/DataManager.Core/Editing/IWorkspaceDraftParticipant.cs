using DataManager.Core.Models;

namespace DataManager.Core.Editing;

/// <summary>Native-format drafts join the same save, dirty state and recovery boundary as physical tables.</summary>
public interface IWorkspaceDraftParticipant
{
    bool HasPendingChanges { get; }
    IReadOnlyList<PreparedBinaryEdit> PrepareBinaryEdits() => [];
    IReadOnlyList<PreparedFileEdit> PrepareFileEdits() => [];
    Action CaptureSaveRecovery() => () => { };
    void AcceptSaved();
    void Discard();
}
