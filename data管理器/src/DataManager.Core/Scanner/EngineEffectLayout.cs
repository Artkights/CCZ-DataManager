using DataManager.Core.Binary;
using DataManager.Core.Editing;

namespace DataManager.Core.Scanner;

/// <summary>
/// Disk geometry shared by the scan, slot and write services. The name pool and
/// the Imsg description pool are native regions of the current 6.4/6.5/6.6
/// layouts; they are not derived from a table record codec.
/// </summary>
internal sealed record EngineEffectLayout(
    string Version,
    string ExePath,
    string ImsgPath,
    long NameOffset,
    int NameCount,
    long DescriptionOffset,
    int DescriptionStride,
    int DescriptionCapacity,
    bool HasImsg)
{
    public const int NameSlotSize = 16;
    public const int NameSuffixOffset = 0x0F;
    public const int NameMaxPayload = 14;
    public const int DescriptionSlotSize = 200;
    public const int DescriptionMaxPayload = 199;
    public const long DefaultDescriptionOffset = 86000;

    public static EngineEffectLayout Resolve(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var version = workspace.ActiveProfile.Profile.Version;
        var exePath = ProjectFileLocator.Resolve(workspace.RootPath, "Ekd5.exe");
        var imsgPath = ProjectFileLocator.Resolve(workspace.RootPath, "Imsg.e5");
        var is64 = string.Equals(version, "6.4", StringComparison.Ordinal) ||
                   workspace.ActiveProfile.ProfileId.Equals("ccz-6.4", StringComparison.OrdinalIgnoreCase);
        var nameDefinition = workspace.Definitions.FirstOrDefault(definition => definition.Key == "effect-names");
        var nameCount = nameDefinition is { RowCount: > 0 } ? nameDefinition.RowCount : is64 ? 180 : 255;
        var nameOffset = nameDefinition is { DataOffset: > 0 } ? nameDefinition.DataOffset : is64 ? 0xD0D60L : 0x9E800L;
        var descriptionDefinition = workspace.Definitions.FirstOrDefault(definition => definition.Key == "effect-descriptions");
        var descriptionOffset = descriptionDefinition is { DataOffset: > 0 }
            ? descriptionDefinition.DataOffset
            : DefaultDescriptionOffset;
        var hasImsg = false;
        if (File.Exists(imsgPath))
        {
            var length = new FileInfo(imsgPath).Length;
            hasImsg = descriptionOffset >= 0 &&
                      descriptionOffset + (long)nameCount * DescriptionSlotSize <= length;
        }
        return new EngineEffectLayout(version, exePath, imsgPath, nameOffset, nameCount,
            descriptionOffset, DescriptionSlotSize, DescriptionMaxPayload, hasImsg);
    }

    public bool HasNameOffset => NameOffset >= 0;
}

internal static class EngineEffectSlotRules
{
    /// <summary>
    /// The engine reads the name as a C string: it ends at the first NUL, and a
    /// slot whose 15 payload bytes are all used ends at +0x0F (that byte is the
    /// terminator, not a suffix). Decoding a fixed 14 bytes split the last GBK
    /// character of those slots into an unencodable surrogate.
    /// </summary>
    public static int NameEnd(ReadOnlySpan<byte> slot)
    {
        var terminator = slot[..EngineEffectLayout.NameSlotSize].IndexOf((byte)0);
        return terminator < 0
            ? EngineEffectLayout.NameSuffixOffset
            : Math.Min(terminator, EngineEffectLayout.NameSuffixOffset);
    }

    public static string DecodeName(ReadOnlySpan<byte> slot)
        => EncodingService.DecodeFixedStringPreservingInvalidBytes(slot[..NameEnd(slot)]);

    /// <summary>
    /// Capacity never enters the suffix byte and never overwrites a nonzero preserved tail.
    /// A slot whose +0x0F byte is zero may use 15 payload bytes (the byte stays the
    /// terminator); otherwise the suffix byte survives and the name is limited to 14.
    /// </summary>
    public static int NameCapacity(ReadOnlySpan<byte> slot)
    {
        var suffix = slot[EngineEffectLayout.NameSuffixOffset];
        var limit = suffix == 0 ? EngineEffectLayout.NameSuffixOffset : EngineEffectLayout.NameMaxPayload;
        var terminator = slot[..EngineEffectLayout.NameSlotSize].IndexOf((byte)0);
        if (terminator < 0 || terminator >= limit) return limit;
        for (var index = terminator + 1; index < limit; index++)
            if (slot[index] != 0) return Math.Max(0, index - 1);
        return limit;
    }

    public static TextFieldConstraint NameConstraint(int capacity)
        => new(EngineEffectLayout.NameSlotSize, capacity, true,
            capacity >= EngineEffectLayout.NameSuffixOffset
                ? "16 字节名称槽；+0x0F 为 0 时可写满 15 字节名（该字节作结束符），不保留后缀"
                : "16 字节名称槽；保留 +0x0F 后缀字节，名称最多 14 个 GBK 字节");

    public static TextFieldConstraint DescriptionConstraint
        => new(EngineEffectLayout.DescriptionSlotSize, EngineEffectLayout.DescriptionMaxPayload, false,
            "Imsg 200 字节说明槽；末字节保留 NUL 结束符，可用 199 字节。");

    /// <summary>The description reader appends a NUL after 200 bytes; keep the slot's final byte at zero.</summary>
    public static string DecodeDescription(ReadOnlySpan<byte> slot)
        => EncodingService.DecodeFixedStringPreservingInvalidBytes(slot);

    public static int DescriptionByteCount(ReadOnlySpan<byte> slot)
    {
        var end = slot.IndexOf((byte)0);
        return end >= 0 ? end : slot.Length;
    }
}
