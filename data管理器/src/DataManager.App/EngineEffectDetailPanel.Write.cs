using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.App;

public sealed partial class EngineEffectDetailPanel
{
    public Func<IWin32Window?, bool>? CommitAllRequested { get; set; }

    public bool HasPendingEquipmentUnbind => _pendingCalls.Values.Any(edit => edit.ItemEffectId == 255);

    public EngineEffectWritePreview? BuildPendingPreview(bool deferEquipmentFfGuard = false)
    {
        if (_workspace == null || _report == null || _row == null || !HasPendingChanges) return null;
        return new EngineEffectWriteService().BuildPreview(_workspace, _report, new EngineEffectWriteRequest
        {
            EffectId = _row.EffectId, NewName = _pendingName, NewSuffix = _pendingSuffix,
            NewDescription = _pendingDescription, CallEdits = _pendingCalls.Values.ToArray(),
            DeferEquipmentFfGuard = deferEquipmentFfGuard
        });
    }

    public void AcceptPendingCommit(EngineEffectWriteResult result)
    {
        if (!result.Success) return;
        LastFollowEffectId = result.FollowEffectId;
        LastWrittenCallAddress = null;
        ClearPendingCore();
        UpdatePendingUi();
    }

    public bool TryCommitPending(IWin32Window? owner)
    {
        if (CommitAllRequested != null) return CommitAllRequested(owner);
        owner ??= FindForm();
        if (_workspace == null || _report == null || _row == null) return false;
        if (!HasPendingChanges) return true;
        if (!ConfirmRiskyChanges(owner)) return false;
        var request = new EngineEffectWriteRequest
        {
            EffectId = _row.EffectId,
            NewName = _pendingName,
            NewSuffix = _pendingSuffix,
            NewDescription = _pendingDescription,
            CallEdits = _pendingCalls.Values.ToArray()
        };
        var service = new EngineEffectWriteService();
        EngineEffectWritePreview preview;
        try
        {
            preview = service.BuildPreview(_workspace, _report, request);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "引擎写入预览", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        using (var dialog = new EngineEffectWritePreviewDialog(preview))
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        }
        EngineEffectWriteResult result;
        try
        {
            result = service.Commit(_workspace, preview);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "引擎写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        if (!result.Success)
        {
            MessageBox.Show(owner, result.Message, "引擎写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        LastFollowEffectId = result.FollowEffectId;
        LastWrittenCallAddress = null;
        ClearPendingCore();
        UpdatePendingUi();
        MessageBox.Show(owner, result.Message, "引擎写入完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        WriteCompleted?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool ConfirmRiskyChanges(IWin32Window? owner)
    {
        var warnings = new List<string>();
        if (_pendingSuffix is >= 7 and <= 0x14)
            warnings.Add($"后缀改为 0x{_pendingSuffix:X2}（{EngineEffectSuffixLabels.Name(_pendingSuffix.Value)}，候选语义）。");
        foreach (var edit in _pendingCalls.Values)
        {
            var candidates = _row?.CallSiteDetails.Concat(_row.DerivedCallDetails)
                .Concat(_row.TableCallDetails)
                .Where(call => call.CallAddress == edit.CallAddress)
                .ToArray();
            var site = candidates?.FirstOrDefault(call => call.Identity == edit.Identity);
            if (site == null) continue;
            if (site.Pattern == EngineEffectCallPattern.TableDerived && edit.ItemEffectId is { } tableItem)
                warnings.Add($"表驱动表项 0x{site.TableAddress:X8}[{site.TableIndex}] 的装备号改为 {tableItem}；" +
                             "会影响所有使用该表的对象。");
            if (edit.ValueMode is { } valueMode && valueMode is not (0 or 1))
                warnings.Add($"调用点 0x{edit.CallAddress:X8} 的效果值模式改为 {valueMode}（未验证）。");
            if (edit.StackingMode == 3)
                warnings.Add($"调用点 0x{edit.CallAddress:X8} 的叠加方式改为 3（待测试）。");
            if (edit.ItemEffectId is >= 0x80)
                warnings.Add($"调用点 0x{edit.CallAddress:X8} 的装备特效号改为 0x{edit.ItemEffectId:X2}（未验证）。");
            if (edit.PersonalEffectId is { } personal && personal != site.PersonalEffectId)
                warnings.Add($"调用点 0x{edit.CallAddress:X8} 的兵种特效号改为 {personal}；写入后调用点会移动到该名称行。");
            if (edit.EntryTarget is { } target && target != site.EntryTarget)
                warnings.Add($"调用点 0x{edit.CallAddress:X8} 的判定入口改为 0x{target:X8}。");
        }
        if (warnings.Count == 0) return true;
        return MessageBox.Show(owner,
            string.Join(Environment.NewLine, warnings) + Environment.NewLine + Environment.NewLine + "继续写入？",
            "引擎写入确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }
}
