using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Scanner;

/// <summary>Rebinds Imsg only; all EXE analysis and evidence retain their original identities.</summary>
public static class EngineEffectReportTextService
{
    public static EngineEffectScanReport RefreshDescriptions(ProjectWorkspace workspace, EngineEffectScanReport source)
    {
        var layout = EngineEffectLayout.Resolve(workspace);
        var exeSha = HashService.Sha256(layout.ExePath);
        if (!exeSha.Equals(source.ExeSha256, StringComparison.OrdinalIgnoreCase) || layout.Version != source.EngineVersion ||
            layout.NameCount != source.NameCount || source.Effects.Any(row => row.HasNameSlot &&
                row.NameSlotOffset != layout.NameOffset + (long)row.EffectId * EngineEffectLayout.NameSlotSize) || !Path.GetFullPath(layout.ExePath).Equals(Path.GetFullPath(source.ExePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("说明刷新需要相同引擎字节、布局及项目；请重新完整扫描。");
        var bytes = File.Exists(layout.ImsgPath) ? File.ReadAllBytes(layout.ImsgPath) : null;
        var rows = source.Effects.Select(row =>
        {
            var offset = layout.DescriptionOffset + (long)row.EffectId * layout.DescriptionStride;
            var exists = bytes != null && layout.HasImsg && row.EffectId >= 0 && row.EffectId < layout.NameCount &&
                offset >= 0 && offset <= bytes.LongLength - EngineEffectLayout.DescriptionSlotSize;
            var span = exists ? bytes!.AsSpan(checked((int)offset), EngineEffectLayout.DescriptionSlotSize) : [];
            return row with
            {
                Description = exists ? EngineEffectSlotRules.DecodeDescription(span) : string.Empty,
                DescriptionByteCount = exists ? EngineEffectSlotRules.DescriptionByteCount(span) : 0,
                HasDescriptionSlot = exists, DescriptionSlotOffset = exists ? offset : -1
            };
        }).ToArray();
        var diagnostics = source.Diagnostics.Where(text => text != "未找到 Imsg.e5，特效说明只读。" &&
            text != "Imsg.e5 不足以覆盖特效说明槽，说明只读。").ToList();
        if (bytes == null) diagnostics.Insert(0, "未找到 Imsg.e5，特效说明只读。");
        else if (!layout.HasImsg) diagnostics.Insert(0, "Imsg.e5 不足以覆盖特效说明槽，说明只读。");
        return source with { ImsgPath = layout.ImsgPath, ImsgSha256 = bytes == null ? string.Empty : HashService.Sha256(bytes),
            Effects = rows, Diagnostics = diagnostics };
    }
}
