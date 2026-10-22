using System.Buffers.Binary;
using DataManager.Core.Binary;
using DataManager.Core.Editing;

namespace DataManager.Core.Migration;

/// <summary>
/// The root Item.e5 in the verified 18-province extension is a 256 x 25
/// data table for item IDs 256..511. It is unrelated to E5/Item.e5 images.
/// A destination needs both the item/description pointer consumers and a
/// matching, verified loader. Copying the files alone cannot add that feature.
/// </summary>
internal static class ExtendedItemMigrationMapper
{
    private const string KnownLoaderSha = "F2827380837060ADB43CEE0B0E399A2E8812A4507B4B2425C928A6D960AA7B11";
    private const int ItemLength = 256 * 25;
    private const int MessageLength = 256 * 200;

    public static bool IsExtendedItemData(string root, string relative)
    {
        if (!string.IsNullOrEmpty(Path.GetDirectoryName(relative))) return false;
        var name = Path.GetFileName(relative);
        if (!name.Equals("Item.e5", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Itemmsg.e5", StringComparison.OrdinalIgnoreCase)) return false;
        var path = DirectFile(root, name);
        if (path == null) return false;
        var expected = name.Equals("Item.e5", StringComparison.OrdinalIgnoreCase) ? ItemLength : MessageLength;
        // File geometry and an executable consumer are independent evidence;
        // this never classifies a real E5 image archive by file name alone.
        return new FileInfo(path).Length == expected && HasEngineConsumers(root);
    }

    public static void Add(ProjectWorkspace source, ProjectWorkspace target, List<MigrationChange> changes)
    {
        var itemPath = DirectFile(source.RootPath, "Item.e5");
        var messagePath = DirectFile(source.RootPath, "Itemmsg.e5");
        var hasItems = itemPath != null && IsExtendedItemData(source.RootPath, "Item.e5");
        var hasMessages = messagePath != null && IsExtendedItemData(source.RootPath, "Itemmsg.e5");
        if (!hasItems && !hasMessages) return;
        var loaderVerified = HasVerifiedLoader(source.RootPath);
        var targetSupported = HasEngineConsumers(target.RootPath) && HasVerifiedLoader(target.RootPath);
        if (loaderVerified && !targetSupported)
        {
            foreach (var name in new[] { "Effect.e5", "Effectmsg.e5", "MeffStudy.e5", "Forge.e5" })
            {
                var path = DirectFile(source.RootPath, name);
                if (path == null) continue;
                changes.Add(new MigrationChange("extended-runtime-capability", "扩展数据运行时支持", name, -1,
                    $"{new FileInfo(path).Length} 字节；18州 ExtendData 消费者已验证", "目标未验证对应消费接口", MigrationChangeKind.Incompatible,
                    "目标缺少本扩展 DLL 及匹配的引擎消费接口。单独迁移物理数据可供管理器编辑和保存，但不能证明目标游戏会读取或执行这些特技/学习/锻造设置；不能通过覆盖 DLL 或注入代码自动补足运行时能力。"));
            }
        }
        if (!loaderVerified || !targetSupported)
        {
            if (loaderVerified && !targetSupported)
                changes.Add(new MigrationChange("extended-runtime", "扩展数据运行时入口", "引擎读取能力", -1,
                    HashService.Sha256(DirectFile(source.RootPath, "ExtendData.dll")!), null, MigrationChangeKind.Incompatible,
                    "源 ExtendData 加载器已验证，目标没有对应加载器和扩展消费者。Effect、Effectmsg、MeffStudy、Forge 可以作为物理数据文件迁入并由本工具复读；这不等于目标游戏已实现其中的特效、学习和锻造功能。必须另行移植并实测目标引擎接口，不能用复制插件替代兼容验证。"));
            // No destination consumer means no individual ID can be migrated.
            // Keep one diagnostic range so the review UI stays small and cannot
            // imply that selecting one of 256 rows would make it work.
            var sourceLayout = string.Join("；", new[]
            {
                hasItems ? "Item.e5 256×25" : null,
                hasMessages ? "Itemmsg.e5 256×200" : null
            }.Where(value => value != null));
            changes.Add(new MigrationChange("extended-items", "扩展物品（256～511）", "整段记录及说明", -1,
                $"ID 256～511；{sourceLayout}", null, MigrationChangeKind.Ignored,
                !loaderVerified
                    ? "源 root Item.e5/Itemmsg.e5 是独立扩展数据，但源加载器未通过当前已知 DLL 指纹验证；整段跳过，禁止当作图标或普通文件迁移。"
                    : "目标没有已验证的物品 ID>255 记录/说明消费者及配套 ExtendData 加载器；整段 ID 256～511 跳过。源为 Item.e5 256×25、Itemmsg.e5 256×200，仅复制文件不能生效，且不能覆盖目标 E5/Item.e5 图标。保持目标引擎，不注入补丁。"));
            return;
        }

        foreach (var path in new[] { hasItems ? itemPath : null, hasMessages ? messagePath : null }.Where(path => path != null))
        {
            var relative = Path.GetFileName(path!);
            var targetPath = DirectFile(target.RootPath, relative);
            var sourceHash = HashService.Sha256(path!);
            var targetHash = targetPath == null ? null : HashService.Sha256(targetPath);
            if (sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase)) continue;
            changes.Add(new MigrationChange("extended-items", relative, "独立扩展数据文件", -1, sourceHash, targetHash,
                targetPath == null ? MigrationChangeKind.ResourceAdded : MigrationChangeKind.ResourceChanged,
                "双方物品/说明指针消费者及独立加载器已验证一致；保留 ID256～511 的原始文件布局，+0x12/+0x13/+0x14 仅由运行时加载器轮转，不对文件重复轮转。")
            { TargetRelativePath = relative });
        }
    }

    private static bool HasVerifiedLoader(string root)
    {
        var path = DirectFile(root, "ExtendData.dll");
        return path != null && HashService.Sha256(path).Equals(KnownLoaderSha, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasEngineConsumers(string root)
    {
        var path = DirectFile(root, "Ekd5.exe");
        if (path == null) return false;
        var bytes = File.ReadAllBytes(path);
        // Check full instruction sequences in executable PE sections, not a
        // coincidental address in strings/data. IDs >255 subtract 256 then
        // multiply by 25; descriptions mask low byte and multiply by 200.
        ReadOnlySpan<byte> item = [0x81, 0xE9, 0x00, 0x01, 0x00, 0x00, 0x6B, 0xC9, 0x19, 0x03, 0x0D, 0x1C, 0x00, 0x4F, 0x00];
        ReadOnlySpan<byte> message = [0x25, 0xFF, 0x00, 0x00, 0x00, 0xB9, 0xC8, 0x00, 0x00, 0x00, 0xF7, 0xE1, 0x8B, 0x0D, 0x24, 0x00, 0x4F, 0x00];
        return ContainsExecutable(bytes, item) && ContainsExecutable(bytes, message);
    }

    private static bool ContainsExecutable(byte[] bytes, ReadOnlySpan<byte> pattern)
    {
        if (bytes.Length < 0x40 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0x5A4D) return false;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        if (pe < 0 || pe > bytes.Length - 24 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pe)) != 0x4550) return false;
        var sections = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 6));
        var optional = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20));
        var table = (long)pe + 24 + optional;
        for (var i = 0; i < sections; i++)
        {
            var offset = table + i * 40L;
            if (offset < 0 || offset > bytes.Length - 40) return false;
            var section = bytes.AsSpan((int)offset, 40);
            if ((BinaryPrimitives.ReadUInt32LittleEndian(section[36..]) & 0x20000000) == 0) continue;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(section[16..]);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(section[20..]);
            if ((long)start + length <= bytes.Length && bytes.AsSpan((int)start, (int)length).IndexOf(pattern) >= 0) return true;
        }
        return false;
    }

    private static string? DirectFile(string root, string name)
        => Directory.Exists(root) ? Directory.EnumerateFiles(root).FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
}
