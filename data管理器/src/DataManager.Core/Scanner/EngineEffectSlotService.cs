﻿using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Scanner;

/// <summary>
/// Reads and previews the two native text pools used by the engine-effect page.
/// No method writes a file; callers pass the edit to the shared binary transaction.
/// </summary>
public sealed class EngineEffectSlotService
{
    public EngineEffectNameSlot ReadNameSlot(ProjectWorkspace workspace, int effectId, byte[]? fileSnapshot = null)
    {
        var layout = EngineEffectLayout.Resolve(workspace);
        var fallback = EngineEffectSlotRules.NameConstraint(0);
        if (fileSnapshot == null && !File.Exists(layout.ExePath))
            return MissingName(effectId, layout.ExePath, fallback, "未找到 Ekd5.exe。");
        if (effectId < 0 || effectId >= layout.NameCount)
            return MissingName(effectId, layout.ExePath, fallback, "当前引擎没有这个名称行。");
        var offset = layout.NameOffset + effectId * (long)EngineEffectLayout.NameSlotSize;
        try
        {
            var bytes = fileSnapshot ?? File.ReadAllBytes(layout.ExePath);
            if (offset < 0 || offset + EngineEffectLayout.NameSlotSize > bytes.LongLength)
                return MissingName(effectId, layout.ExePath, fallback, "名称槽超出 Ekd5.exe 范围。");
            var raw = bytes.AsSpan(checked((int)offset), EngineEffectLayout.NameSlotSize).ToArray();
            var capacity = EngineEffectSlotRules.NameCapacity(raw);
            return new EngineEffectNameSlot(effectId, layout.ExePath, offset, raw,
                EngineEffectSlotRules.DecodeName(raw), raw[EngineEffectLayout.NameSuffixOffset], capacity,
                EngineEffectSlotRules.NameConstraint(capacity), true);
        }
        catch (Exception ex)
        {
            return MissingName(effectId, layout.ExePath, fallback, ex.Message);
        }
    }

    public EngineEffectDescriptionSlot ReadDescriptionSlot(ProjectWorkspace workspace, int effectId, byte[]? fileSnapshot = null)
    {
        var layout = EngineEffectLayout.Resolve(workspace);
        var fallback = EngineEffectSlotRules.DescriptionConstraint;
        if (!layout.HasImsg)
            return MissingDescription(effectId, layout.ImsgPath, fallback, "当前引擎没有可用的 Imsg 说明槽。");
        if (effectId < 0 || effectId >= layout.NameCount)
            return MissingDescription(effectId, layout.ImsgPath, fallback, "当前引擎没有这个说明行。");
        var offset = layout.DescriptionOffset + effectId * (long)layout.DescriptionStride;
        try
        {
            var bytes = fileSnapshot ?? File.ReadAllBytes(layout.ImsgPath);
            if (offset < 0 || offset + EngineEffectLayout.DescriptionSlotSize > bytes.LongLength)
                return MissingDescription(effectId, layout.ImsgPath, fallback, "说明槽超出 Imsg.e5 范围。");
            var raw = bytes.AsSpan(checked((int)offset), EngineEffectLayout.DescriptionSlotSize).ToArray();
            return new EngineEffectDescriptionSlot(effectId, layout.ImsgPath, offset, raw,
                EngineEffectSlotRules.DecodeDescription(raw), EngineEffectSlotRules.DescriptionByteCount(raw),
                fallback, true);
        }
        catch (Exception ex)
        {
            return MissingDescription(effectId, layout.ImsgPath, fallback, ex.Message);
        }
    }

    public EngineEffectSlotEdit PreviewName(ProjectWorkspace workspace, int effectId, string? newName)
    {
        var slot = ReadNameSlot(workspace, effectId);
        if (!slot.Exists) return Failed(slot.FilePath, slot.FileOffset, slot.RawBytes, "名称", slot.Error);
        var normalized = newName ?? string.Empty;
        var validation = slot.Constraint.Evaluate(normalized);
        if (!validation.CanEncode) return Failed(slot.FilePath, slot.FileOffset, slot.RawBytes, "名称", validation.Error);
        if (normalized.Equals(slot.Name, StringComparison.Ordinal))
            return new(slot.FilePath, slot.FileOffset, slot.RawBytes, slot.RawBytes, "名称");
        byte[] encoded;
        try { encoded = EncodingService.GameEncoding.GetBytes(normalized); }
        catch (Exception ex) { return Failed(slot.FilePath, slot.FileOffset, slot.RawBytes, "名称", ex.Message); }
        var after = (byte[])slot.RawBytes.Clone();
        // Only the old visible text may be erased. Bytes beyond its NUL can be
        // private parameters and the +0x0F suffix must survive every rename.
        // A 15-byte name ends at +0x0F, so that byte is cleared as part of the name.
        var oldEnd = EngineEffectSlotRules.NameEnd(slot.RawBytes);
        after.AsSpan(0, oldEnd).Clear();
        encoded.CopyTo(after, 0);
        if (!EngineEffectSlotRules.DecodeName(after).Equals(normalized, StringComparison.Ordinal))
            return Failed(slot.FilePath, slot.FileOffset, slot.RawBytes, "名称", "名称不能按原始 GBK 字节无损复读。");
        return new(slot.FilePath, slot.FileOffset, slot.RawBytes, after, "名称");
    }

    public EngineEffectSlotEdit PreviewSuffix(ProjectWorkspace workspace, int effectId, byte newSuffix)
    {
        var slot = ReadNameSlot(workspace, effectId);
        if (!slot.Exists) return Failed(slot.FilePath, slot.FileOffset, slot.RawBytes, "后缀", slot.Error);
        // An existing value must always round-trip, even when it lies outside the
        // validated range (6.5 特效 254 stores 0x64); only new values are bounded.
        if (newSuffix == slot.SuffixByte)
            return new(slot.FilePath, slot.FileOffset, slot.RawBytes, slot.RawBytes, "后缀");
        var version = EngineEffectLayout.Resolve(workspace).Version;
        var maximum = EngineEffectSuffixLabels.MaximumSuffix(version);
        if (newSuffix > maximum)
            return Failed(slot.FilePath, slot.FileOffset, slot.RawBytes, "后缀",
                $"后缀 0x{newSuffix:X2} 超出 {version} 已验证范围 0x00..0x{maximum:X2}；" +
                "更高代码在该基底的名称后缀选择器中不产生后缀。");
        // A 15-byte name uses +0x0F as its terminator; a nonzero suffix there
        // would make the engine read past the slot, so the name must shrink first.
        if (newSuffix != 0 &&
            EngineEffectSlotRules.NameEnd(slot.RawBytes) >= EngineEffectLayout.NameSuffixOffset)
            return Failed(slot.FilePath, slot.FileOffset, slot.RawBytes, "后缀",
                "名称已占满 15 字节，+0x0F 是名称结束符；请先缩短名称再设置后缀。");
        var after = (byte[])slot.RawBytes.Clone();
        after[EngineEffectLayout.NameSuffixOffset] = newSuffix;
        return new(slot.FilePath, slot.FileOffset, slot.RawBytes, after, "后缀");
    }

    public EngineEffectSlotEdit PreviewDescription(ProjectWorkspace workspace, int effectId, string? newText)
    {
        var slot = ReadDescriptionSlot(workspace, effectId);
        if (!slot.Exists) return Failed(slot.FilePath, slot.FileOffset, slot.RawBytes, "说明", slot.Error);
        var normalized = newText ?? string.Empty;
        var validation = slot.Constraint.Evaluate(normalized);
        if (!validation.CanEncode) return Failed(slot.FilePath, slot.FileOffset, slot.RawBytes, "说明", validation.Error);
        if (normalized.Equals(slot.Text, StringComparison.Ordinal))
            return new(slot.FilePath, slot.FileOffset, slot.RawBytes, slot.RawBytes, "说明");
        byte[] encoded;
        try { encoded = EncodingService.GameEncoding.GetBytes(normalized); }
        catch (Exception ex) { return Failed(slot.FilePath, slot.FileOffset, slot.RawBytes, "说明", ex.Message); }
        var after = new byte[EngineEffectLayout.DescriptionSlotSize];
        encoded.CopyTo(after, 0);
        return new(slot.FilePath, slot.FileOffset, slot.RawBytes, after, "说明");
    }

    private static EngineEffectNameSlot MissingName(int effectId, string path, TextFieldConstraint constraint, string? error)
        => new(effectId, path, -1, [], string.Empty, 0, 0, constraint, false, error);

    private static EngineEffectDescriptionSlot MissingDescription(int effectId, string path,
        TextFieldConstraint constraint, string? error)
        => new(effectId, path, -1, [], string.Empty, 0, constraint, false, error);

    private static EngineEffectSlotEdit Failed(string path, long offset, byte[] before, string field, string? error)
        => new(path, offset, before, before, field, error ?? "无法生成写入预览。");
}
