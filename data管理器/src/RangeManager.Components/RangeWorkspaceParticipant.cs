using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace RangeManager.App;

/// <summary>Joins ranges to the host's draft history and its single multi-file save transaction.</summary>
public sealed class RangeWorkspaceParticipant : IWorkspaceDraftParticipant, IDisposable
{
    private readonly RangeEditSession _session;
    private readonly Action<IEditCommand> _sink;
    private RangeSaveCandidate? _prepared;
    private bool _disposed;

    public RangeWorkspaceParticipant(RangeEditSession session, EditCommandManager commands)
    {
        _session = session;
        _sink = commands.Push;
        session.CommandSink = _sink;
        session.Changed += SessionChanged;
    }

    public RangeEditSession Session => _session;
    public bool HasPendingChanges => !_disposed && _session.IsDirty;
    private void SessionChanged(object? sender, EventArgs e) => _prepared = null;
    private RangeSaveCandidate Prepared
    {
        get { _session.VerifyBaselineFiles(); return _prepared ??= _session.PrepareSaveCandidate(); }
    }
    private string Relative(string absolute) => Path.GetRelativePath(_session.Game.RootPath, absolute);

    public IReadOnlyList<PreparedBinaryEdit> PrepareBinaryEdits()
    {
        if (!HasPendingChanges) return [];
        var candidate = Prepared;
        // Files contributing a full candidate already carry their own SHA guard.
        // Include unchanged E5 files and Spalet as read dependencies, without writing them.
        return candidate.ExpectedHashes.Where(pair => !candidate.Files.ContainsKey(pair.Key) && pair.Value.Length != 0)
            .Select(pair => new PreparedBinaryEdit(Relative(pair.Key), pair.Value, [], "范围管理", "范围资源基线校验"))
            .ToArray();
    }

    public IReadOnlyList<PreparedFileEdit> PrepareFileEdits()
    {
        if (!HasPendingChanges) return [];
        var candidate = Prepared;
        return candidate.Files.Where(pair => !StringComparer.OrdinalIgnoreCase.Equals(pair.Key, candidate.ExecutablePath))
            .Select(pair => new PreparedFileEdit(Relative(pair.Key), candidate.ExpectedHashes[pair.Key],
                (byte[])pair.Value.Clone(), "范围管理", "同步范围图像", E5RangeImages.ValidateArchiveBytes)).ToArray();
    }

    public IReadOnlyList<PreparedFileTransform> PrepareFileTransforms()
    {
        if (!HasPendingChanges) return [];
        var candidate = Prepared;
        return [new PreparedFileTransform(Relative(candidate.ExecutablePath), candidate.ExpectedHashes[candidate.ExecutablePath],
            context => TransformExecutable(candidate, context), "范围管理", "重建范围定义及运行时", bytes =>
            {
                using var game = new GameBase(candidate.RootPath, bytes);
                _ = NativeRangeCatalog.Read(game);
                _ = ManagedRangeRuntime.ReadBindings(game);
                _ = RangeExtensionMapping.Read(game);
                candidate.ExecutableVerifier?.Invoke(bytes);
            })];
    }

    internal static byte[] TransformExecutable(RangeSaveCandidate draft, PreparedFileTransformContext context)
    {
        byte[] baseline = context.BaselineBytes, patched = context.CandidateBytes;
        if (!baseline.AsSpan().SequenceEqual(draft.BaselineExecutable))
            throw new IOException("范围管理与数据工作区的 EXE 基线不一致，请重新打开工程。");
        if (patched.Length != baseline.Length)
            throw new InvalidDataException("普通 EXE 补丁改变了文件长度，无法合并范围重建。");

        // R is built from B before touching P. Native header/hook/pointer/map
        // changes can be compared at their original offsets; managed payloads
        // and overlay bytes may move and therefore always reject ordinary edits.
        byte[] rangeOnly = draft.BuildExecutable((byte[])baseline.Clone());
        var pe = new PeFile(draft.ExecutablePath, baseline);
        int protectedOffset = ManagedRangeRuntime.RebuildProtectedOffset(pe);
        foreach (var patch in context.AppliedPatches)
        {
            long end = checked(patch.Offset + patch.NewBytes.Length);
            if (patch.Offset < 0 || end > baseline.Length || patch.ExpectedBytes.Length != patch.NewBytes.Length)
                throw new InvalidDataException("普通 EXE 补丁越过范围基线边界。");
            int at = checked((int)patch.Offset);
            if (!baseline.AsSpan(at, patch.ExpectedBytes.Length).SequenceEqual(patch.ExpectedBytes) ||
                !patched.AsSpan(at, patch.NewBytes.Length).SequenceEqual(patch.NewBytes))
                throw new InvalidDataException("普通 EXE 补丁与工作区候选不一致。");
            if (end > protectedOffset)
                throw new InvalidDataException($"EXE 补丁 0x{at:X} 位于旧范围扩展区或 overlay，范围重建会迁移该区域，不能合并保存。");
            for (int i = at; i < end; i++)
                if (i >= rangeOnly.Length || baseline[i] != rangeOnly[i])
                    throw new InvalidDataException($"EXE 补丁 0x{i:X} 与范围原生指针、映射或运行时修改重叠，不能合并保存。");
        }

        // Run the real builder against P. Never overlay raw B->P offsets on R.
        byte[] combined = draft.BuildExecutable((byte[])patched.Clone());
        foreach (var patch in context.AppliedPatches)
        {
            int at = checked((int)patch.Offset);
            if (at + patch.NewBytes.Length > combined.Length ||
                !combined.AsSpan(at, patch.NewBytes.Length).SequenceEqual(patch.NewBytes))
                throw new InvalidDataException($"范围重建未保留普通 EXE 补丁 0x{at:X}，已拒绝本次保存。");
        }
        return combined;
    }

    public Action CaptureSaveRecovery()
    {
        var recover = _session.CaptureSaveRecovery();
        return () => { recover(); _prepared = null; };
    }
    public void AcceptSaved() { _session.AcceptSaved(); _prepared = null; }
    public void Discard()
    {
        if (_session.MatchesDiskBaselines()) _session.Discard();
        else _session.ReloadFromDisk();
        _prepared = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Changed -= SessionChanged;
        if (_session.CommandSink == _sink) _session.CommandSink = null;
        _prepared = null;
    }
}
