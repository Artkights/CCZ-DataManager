using System.Runtime.CompilerServices;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Domain;

/// <summary>Shared auxiliary-group draft, used by both the editor and whole-page workbook exchange.</summary>
public sealed class AccessoryGroupDraftService : IWorkspaceDraftParticipant
{
    private static readonly ConditionalWeakTable<ProjectWorkspace, AccessoryGroupDraftService> Instances = new();
    private readonly ProjectWorkspace _workspace;
    private AccessoryGroupDraftService(ProjectWorkspace workspace) { _workspace = workspace; ProjectKey = Key(workspace); Reset(new AccessoryJobGroupService().Read(workspace)); }
    public static AccessoryGroupDraftService For(ProjectWorkspace workspace)
    {
        var draft = Instances.GetValue(workspace, current => new(current));
        if (draft.ProjectKey != Key(workspace)) { draft.ProjectKey = Key(workspace); draft.Discard(); }
        workspace.RegisterDraftParticipant(draft);
        return draft;
    }
    private static string Key(ProjectWorkspace workspace) => $"{Path.GetFullPath(workspace.RootPath)}|{workspace.ActiveProfile.ProfileId}|{workspace.ActiveProfile.VariantId}|{workspace.ActiveProfile.LayoutRevision}";
    public string ProjectKey { get; private set; }
    public string SourceSignature { get; private set; } = "";
    public string SourceHash { get; private set; } = "";
    public string SavedGroupsSignature { get; private set; } = "";
    public List<List<int>> Groups { get; } = [];
    public int SelectedIndex { get; set; }
    public bool HasChanges => Signature(Snapshot()) != SavedGroupsSignature;
    public bool HasPendingChanges => HasChanges;
    public IReadOnlyList<IReadOnlyList<int>> Snapshot() => Groups.Select(group => (IReadOnlyList<int>)group.ToArray()).ToArray();
    private static string Signature(IEnumerable<IReadOnlyList<int>> groups) => string.Join(';', groups.Select(group => string.Join(',', group)));
    public void Reset(AccessoryJobGroupReadResult source)
    {
        Groups.Clear(); Groups.AddRange(source.Groups.Select(group => group.JobSeriesIds.ToList()));
        SavedGroupsSignature = Signature(Snapshot()); SourceHash = source.SourceHash;
        SourceSignature = $"{source.FileOffset}:{source.WritableLength}:{SavedGroupsSignature}";
        SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, Groups.Count - 1));
    }
    private void Restore(IReadOnlyList<IReadOnlyList<int>> groups)
    { Groups.Clear(); Groups.AddRange(groups.Select(group => group.ToList())); }
    public void Apply(IReadOnlyList<IReadOnlyList<int>> groups)
    {
        var preview = new AccessoryJobGroupService().Preview(_workspace, groups);
        if (!preview.CanWrite) throw new InvalidDataException(string.Join(Environment.NewLine, preview.Diagnostics));
        var before = Snapshot(); var after = groups.Select(group => (IReadOnlyList<int>)group.ToArray()).ToArray();
        if (Signature(before) == Signature(after)) return;
        Restore(after);
        _workspace.Commands.Push(new DelegateEditCommand("导入辅助装备分组", () => Restore(before), () => Restore(after)));
    }
    public IReadOnlyList<PreparedBinaryEdit> PrepareBinaryEdits()
        => HasChanges ? [new AccessoryJobGroupService().PrepareSave(_workspace, Snapshot(), SourceHash)] : [];
    public Action CaptureSaveRecovery()
    {
        var groups = Snapshot(); var projectKey = ProjectKey; var signature = SourceSignature;
        var hash = SourceHash; var saved = SavedGroupsSignature; var selected = SelectedIndex;
        return () => { Restore(groups); ProjectKey = projectKey; SourceSignature = signature;
            SourceHash = hash; SavedGroupsSignature = saved; SelectedIndex = selected; };
    }
    public void AcceptSaved() => Reset(new AccessoryJobGroupService().Read(_workspace));
    public void Discard() => Reset(new AccessoryJobGroupService().Read(_workspace));
}
