using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace RangeManager.App;

/// <summary>Integration checks use private EXE/E5 copies and never a copied Data.e5.</summary>
public static class RangeVerification
{
    private sealed record CheckResult(string Base, string Name, bool Passed, string Detail, long Milliseconds);
    private sealed record Entry(byte[] Stored, int DecodedLength);
    private static readonly string[] BaseNames = ["洗隋", "加强版6.5未加密版", "复刻版6.6山贼"];
    private static readonly string[] ResourceNames = ["Hitarea.e5", "Effarea.e5", "Spalet.e5"];

    public static int Run(string[] args)
    {
        string workspace = FindWorkspace();
        string output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(workspace,
            "data管理器", "验证", "2026-11-08-范围管理器新版", "verification.json");
        string temporary = Path.Combine(Path.GetTempPath(), "ccz-range-v2-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var results = new List<CheckResult>();
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? crossBaseIconFingerprint = null;
        int serial = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        void Case(string baseName, string name, Action<string> action)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                string copy = Path.Combine(temporary, (++serial).ToString("D3"));
                CopyFixture(Path.Combine(workspace, "基底", baseName), copy);
                action(copy);
                results.Add(new(baseName, name, true, "通过", timer.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                results.Add(new(baseName, name, false, ex.ToString(), timer.ElapsedMilliseconds));
            }
        }
        void RawWriteCase(string baseName, string name, Action<string> action) =>
            Case(baseName, name, action);
        try
        {
            string templateArchive = Path.Combine(workspace, "基底", "信奈的野望（15+1关版，密码nobuna）", "E5", "Effarea.e5");
            sourceHashes[templateArchive] = RangeSaveTransaction.Hash(templateArchive);
            Case("加强版6.5未加密版", "固定信奈第10项来源哈希、内嵌素材裁剪与完整网格线", copy =>
            {
                using var images = new E5RangeImages(copy);
                VerifyFixedTemplateSource(workspace, images, Path.GetDirectoryName(output)!);
            });
            foreach (string baseName in BaseNames)
            {
                string source = Path.Combine(workspace, "基底", baseName);
                foreach (string path in SourceFiles(source)) sourceHashes[path] = RangeSaveTransaction.Hash(path);
                Case(baseName, "原生目录、真实生成元与分支几何", copy =>
                {
                    Assert(!File.Exists(Path.Combine(copy, "Data.e5")) && !File.Exists(Path.Combine(copy, "E5", "Data.e5")), "测试副本不应包含 Data.e5");
                    using var session = new RangeEditSession(copy);
                    var rows = session.Bindings;
                    Assert(rows.Count(r => r.Key.Kind == RangeKind.Attack) == 38, "攻击目录应含36段和2个特殊编号");
                    Assert(rows.Where(r => r.Key.Kind == RangeKind.Attack && r.Key.Id is not (10 or 11)).All(r => r.Editable), "当前基底攻击契约必须全部匹配");
                    Assert(rows.Where(r => r.Key.Kind == RangeKind.Pierce && r.Key.Id != 0 &&
                        !(session.Game.Version == EngineVersion.V65 && r.Key.Id == 10)).All(r => r.Editable), "当前基底可编辑穿透契约必须全部匹配");
                    int Count(int id) => session.Preview(new(RangeKind.Pierce, id), RangePreviewContext.Default).Count;
                    Assert(Count(0) == 0 && Count(1) == 4 && Count(2) == 8 && Count(3) == 12, "穿透普通生成元展开错误");
                    Assert(Count(4) == 1 && Count(5) == 5 && Count(7) == 2 && Count(9) == 2, "方向型原生几何错误");
                    bool six = session.Game.Version == EngineVersion.V66;
                    Assert(Count(6) == (six ? 20 : 24) && Count(8) == (six ? 24 : 20), "6/8号应按当前版本真实分派展开");
                    if (session.Game.Version == EngineVersion.V65)
                    {
                        Assert(Count(10) == 7 && Count(11) == 5, "自身排除或双侧加直线组合遗漏");
                        var predicate = session.Get(new(RangeKind.Pierce, 10));
                        Assert(!predicate.Editable && predicate.Special == "原生独立穿透谓词", "6.5 0A独立谓词必须保留原生语义");
                        MustThrow<InvalidDataException>(() => session.UpdateText(predicate.Key, 0, "2,0", out _), "不可将独立谓词转为通用穿透叶写入器");
                        MustThrow<InvalidDataException>(() => session.Delete(predicate.Key), "不可清空原生独立谓词");
                    }
                    if (six) Assert(Count(10) == 8, "6.6自身中心共享段错误");
                    foreach (var context in new[] { new RangePreviewContext(new(2, 0)), new(new(2, 2)), new(new(-2, -2)) })
                        Assert(session.Preview(new(RangeKind.Pierce, 5), context).Count == 5, "方向旋转应保持五个延长格");
                    Assert(!session.IsDirty, "只读目录不应产生修改");
                });
                Case(baseName, "拓展范围原生33项及完整攻击目录映射读取、保存与重开", copy =>
                {
                    using (var session = new RangeEditSession(copy))
                    {
                        Assert(session.Extension.Supported && session.Extension.Values.Count == RangeExtensionMapping.ValueCount,
                            "三基底拓展范围映射契约未识别为256项逻辑目录");
                        Assert(session.Extension.Values[0] == 0x01 && session.Extension.Values[1] == 0x06 &&
                            session.Extension.Values[0x20] == 0x24, "拓展范围映射原始字节读取错误");
                        Assert(session.Extension.Values[0x21] == 0x21 && session.Extension.Values[0x25] == 0x25,
                            "0x21..0x25 的真实攻击目录没有进入逻辑映射视图");
                        Assert(session.UpdateExtension(0, 0x02, out _), "拓展范围映射修改失败");
                        Assert(session.UpdateExtension(0x21, 0x22, out _), "表外真实攻击编号映射修改失败");
                        Assert(session.IsDirty, "拓展范围映射修改没有进入草稿");
                        session.Save();
                    }
                    using (var reopened = new RangeEditSession(copy))
                    {
                        Assert(reopened.Extension.Values[0] == 0x02 && reopened.Extension.Values[1] == 0x06 &&
                            reopened.Extension.Values[0x21] == 0x22,
                            "拓展范围映射保存后重开不一致");
                        Assert(reopened.UpdateExtension(0, 0x01, out _), "拓展范围映射第二次修改失败");
                        reopened.Undo();
                        Assert(reopened.Extension.Values[0] == 0x02, "拓展范围映射撤回失败");
                    }
                });
                RawWriteCase(baseName, "新增有效编号、同步保存、重开和再次保存", copy =>
                {
                    var beforeAttack = ReadArchive(Resource(copy, "Hitarea.e5"));
                    var beforePierce = ReadArchive(Resource(copy, "Effarea.e5"));
                    using (var session = new RangeEditSession(copy))
                    {
                        var attack = session.Add(RangeKind.Attack);
                        var pierce = session.Add(RangeKind.Pierce, RangeGeometryKind.Mirror, RangeCenterMode.Target);
                        var targetLine = session.Add(RangeKind.Pierce, RangeGeometryKind.Directional, RangeCenterMode.Target);
                        var selfMirror = session.Add(RangeKind.Pierce, RangeGeometryKind.Mirror, RangeCenterMode.Self);
                        var selfLine = session.Add(RangeKind.Pierce, RangeGeometryKind.Directional, RangeCenterMode.Self);
                        Assert(attack.Id == 0x26 && pierce.Id == 0x0C && targetLine.Id == 0x0D &&
                            selfMirror.Id == 0x0E && selfLine.Id == 0x0F, "新增应分配可用运行时编号26/C..F");
                        Assert(session.UpdateText(attack, 0, "4,0; 1,1", out _), "攻击生成元输入失败");
                        Assert(session.UpdateText(pierce, 0, "1,0; 1,1", out _), "穿透生成元输入失败");
                        Assert(session.UpdateText(targetLine, 0, "1,0", out _), "目标中心方向生成元输入失败");
                        Assert(session.UpdateText(selfMirror, 0, "1,0; 1,1", out _), "自身中心镜像生成元输入失败");
                        Assert(session.UpdateText(selfLine, 0, "1,0", out _), "自身中心方向生成元输入失败");
                        Assert(session.Get(pierce).Center == RangeCenterMode.Target &&
                            session.Get(targetLine).Center == RangeCenterMode.Target &&
                            session.Get(selfMirror).Center == RangeCenterMode.Self &&
                            session.Get(selfLine).Center == RangeCenterMode.Self, "新增穿透中心模式未保留");
                        var defaultContext = RangePreviewContext.Default;
                        Assert(session.Preview(targetLine, defaultContext).Contains(new(2, 0)), "目标中心方向范围未围绕主目标展开");
                        Assert(session.Preview(selfLine, defaultContext).Contains(new(1, 0)), "自身中心方向范围未围绕攻击者展开");
                        var diagonalContext = new RangePreviewContext(new(2, 1));
                        Assert(session.Preview(targetLine, diagonalContext).Contains(new(3, 2)), "目标中心方向旋转错误");
                        Assert(session.Preview(selfLine, diagonalContext).Contains(new(1, 1)), "自身中心方向旋转错误");
                        session.Save();
                        Assert(!session.IsDirty, "保存成功应建立新基线");
                    }
                    using (var session = new RangeEditSession(copy))
                    {
                        var attack = new RangeKey(RangeKind.Attack, 0x26);
                        var pierce = new RangeKey(RangeKind.Pierce, 0x0C);
                        var targetLine = new RangeKey(RangeKind.Pierce, 0x0D);
                        var selfMirror = new RangeKey(RangeKind.Pierce, 0x0E);
                        var selfLine = new RangeKey(RangeKind.Pierce, 0x0F);
                        Assert(session.Get(attack).IsManaged && session.Get(pierce).IsManaged, "新增编号必须持久保存在EXE扩展区");
                        Assert(session.Get(pierce).Center == RangeCenterMode.Target &&
                            session.Get(targetLine).Center == RangeCenterMode.Target &&
                            session.Get(selfMirror).Center == RangeCenterMode.Self &&
                            session.Get(selfLine).Center == RangeCenterMode.Self, "重开后新增中心模式不一致");
                        Assert(session.Preview(attack, RangePreviewContext.Default).Count == 8, "新增攻击复读不一致");
                        Assert(session.Preview(pierce, RangePreviewContext.Default).Count == 8, "新增穿透复读不一致");
                        Assert(session.Preview(targetLine, RangePreviewContext.Default).Contains(new(2, 0)) &&
                            session.Preview(selfLine, RangePreviewContext.Default).Contains(new(1, 0)), "重开后中心展开不一致");
                        Assert(session.Bindings.Where(b => b.Editable && b.NativePointer != 0).All(b => session.Game.Exe.TryVaToOffset(b.NativePointer, out _)), "重开后存在悬挂原生指针");
                        session.UpdateText(attack, 0, "5,0", out _);
                        session.UpdateText(pierce, 0, "2,0", out _);
                        session.UpdateText(selfMirror, 0, "2,0", out _);
                        session.Save();
                    }
                    using (var session = new RangeEditSession(copy))
                    {
                        Assert(session.Preview(new(RangeKind.Attack, 0x26), RangePreviewContext.Default).Any(c => c.Dx == 5), "再次保存丢失攻击定义");
                        Assert(session.Preview(new(RangeKind.Pierce, 0x0C), RangePreviewContext.Default).Any(c => c.Dx == 3), "再次保存丢失穿透定义");
                        Assert(session.Get(new(RangeKind.Pierce, 0x0E)).Center == RangeCenterMode.Self &&
                            session.Preview(new(RangeKind.Pierce, 0x0E), RangePreviewContext.Default).Any(c => c.Dx == 2), "再次保存丢失自身中心定义");
                        Assert(session.Get(new(RangeKind.Pierce, 0x0D)).Center == RangeCenterMode.Target &&
                            session.Preview(new(RangeKind.Pierce, 0x0D), RangePreviewContext.Default).Contains(new(2, 0)), "目标中心定义被自身中心修改污染");
                        Assert(session.Bindings.Where(b => b.Editable && b.NativePointer != 0).All(b => session.Game.Exe.TryVaToOffset(b.NativePointer, out _)), "再次保存造成悬挂原生指针");
                    }
                    AssertUnchangedPayloads(beforeAttack, ReadArchive(Resource(copy, "Hitarea.e5")), []);
                    AssertUnchangedPayloads(beforePierce, ReadArchive(Resource(copy, "Effarea.e5")), []);
                    using var images = new E5RangeImages(copy);
                    Assert(images.Get(true, 0x26) is { Width: 64, Height: 64 } &&
                        images.Get(false, 0x0C) is { Width: 64, Height: 64 } &&
                        images.Get(false, 0x0D) is { Width: 64, Height: 64 } &&
                        images.Get(false, 0x0E) is { Width: 64, Height: 64 } &&
                        images.Get(false, 0x0F) is { Width: 64, Height: 64 }, "新增E5条目不可复读");
                    if (images.AttackIsIndexed) Assert(images.DecodedLength(true, 0x26) == 4096, "新增6.6 RAW必须保持4096字节");
                    if (images.PierceIsIndexed) Assert(images.DecodedLength(false, 0x0C) == 4096, "新增穿透RAW必须保持4096字节");
                    // The compositor must use the embedded Signal Effarea
                    // entry-10 template. It is deliberately independent of
                    // the current base's E5 entries, so changing a fixture's
                    // colours cannot silently change the preview or output.
                    using var attackTiles = images.CreateFixedPixelTiles();
                    using var pierceTiles = images.CreateFixedPixelTiles();
                    Assert(attackTiles.Frame is { Width: 64, Height: 64 }, "固定 E5 方框模板必须为 64×64");
                    Assert(TileSize(attackTiles.Background) == (8, 8) &&
                        TileSize(attackTiles.Own) == (8, 8) &&
                        TileSize(attackTiles.Attack) == (8, 8) &&
                        TileSize(attackTiles.Pierce) == (8, 8), "固定 E5 像素块必须全部为 8×8");
                    Assert(BitmapFingerprint(attackTiles.Frame!) == BitmapFingerprint(pierceTiles.Frame!), "攻击与穿透未使用同一固定方框模板");
                    Assert(BitmapFingerprint(attackTiles.Background) == BitmapFingerprint(pierceTiles.Background) &&
                        BitmapFingerprint(attackTiles.Own) == BitmapFingerprint(pierceTiles.Own) &&
                        BitmapFingerprint(attackTiles.Attack) == BitmapFingerprint(pierceTiles.Attack) &&
                        BitmapFingerprint(attackTiles.Pierce) == BitmapFingerprint(pierceTiles.Pierce), "攻击与穿透未使用同一组固定像素块");
                    using var square = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Pierce,
                        [new CellOffset(-3, -3), new CellOffset(3, 3)], new(1, 0), pierceTiles);
                    Assert(square.Width == 64 && square.Height == 64, "E5 拼接输出必须保持 64×64 正方形");
                    using var blank = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Attack, [], null, attackTiles);
                    Assert(attackTiles.Frame is { Width: 64, Height: 64 }, "E5 像素块缺少原始方框模板");
                    for (int i = 0; i < 64; i++)
                    {
                        Assert(blank.GetPixel(i, 0).ToArgb() == attackTiles.Frame!.GetPixel(i, 0).ToArgb() &&
                            blank.GetPixel(0, i).ToArgb() == attackTiles.Frame.GetPixel(0, i).ToArgb(),
                            "E5 拼接丢失原始外框像素");
                        if (i % 9 == 0)
                        {
                            Assert(blank.GetPixel(i, 32).ToArgb() == attackTiles.Frame.GetPixel(i, 32).ToArgb(),
                                $"E5 拼接丢失原始竖网格线像素（{i}）：{blank.GetPixel(i, 32).ToArgb():X8}/{attackTiles.Frame.GetPixel(i, 32).ToArgb():X8}");
                            Assert(blank.GetPixel(32, i).ToArgb() == attackTiles.Frame.GetPixel(32, i).ToArgb(),
                                $"E5 拼接丢失原始横网格线像素（{i}）：{blank.GetPixel(32, i).ToArgb():X8}/{attackTiles.Frame.GetPixel(32, i).ToArgb():X8}");
                        }
                    }
                });
                Case(baseName, "原生落格顺序与编组元数据复制", copy =>
                {
                    using var session = new RangeEditSession(copy);
                    CellOffset[] ring = [new(0, -1), new(1, 0), new(0, 1), new(-1, 0), new(1, -1), new(1, 1), new(-1, 1), new(-1, -1)];
                    CellOffset[] axis2 = [new(2, 0), new(-2, 0), new(0, -2), new(0, 2)];
                    CellOffset[] axis3 = [new(3, 0), new(-3, 0), new(0, -3), new(0, 3)];
                    CellOffset[] band = [new(-1, -2), new(-1, 2), new(0, -2), new(0, 2), new(1, -2), new(1, 2),
                        new(-2, -1), new(2, -1), new(-2, 0), new(2, 0), new(-2, 1), new(2, 1)];
                    var centered = new RangePreviewContext(new(0, 0));
                    Assert(session.Preview(new(RangeKind.Pierce, 2), centered).SequenceEqual(ring), "八邻格顺序应与原生方向表逐项一致");
                    if (session.Game.Version != EngineVersion.V66)
                    {
                        Assert(session.Preview(new(RangeKind.Pierce, 3), centered).SequenceEqual(ring.Concat(axis2)), "旧版3号轴附加落格顺序改变");
                        Assert(session.Preview(new(RangeKind.Pierce, 8), centered).SequenceEqual(ring.Concat(band)), "旧版8号边带双循环顺序改变");
                        Assert(session.Preview(new(RangeKind.Pierce, 6), centered).SequenceEqual(ring.Concat(band).Concat(axis3)), "旧版6号远轴组合顺序改变");
                        var binding = session.Get(new(RangeKind.Pierce, 6));
                        var cloned = RangeModelCopies.Clone([binding]).Single();
                        Assert(cloned.Definition.Groups[1].EmissionOrder == RangeEmissionOrder.LegacyBand &&
                            cloned.Definition.Groups[2].EmissionOrder == RangeEmissionOrder.LegacyAxis &&
                            NativeRangeCatalog.Expand(cloned, centered).SequenceEqual(NativeRangeCatalog.Expand(binding, centered)), "模型复制丢失原生发射顺序策略");
                        var json = JsonSerializer.Serialize(binding);
                        var restored = JsonSerializer.Deserialize<RangeBinding>(json) ?? throw new InvalidDataException("元数据往返失败");
                        Assert(NativeRangeCatalog.Expand(restored, centered).SequenceEqual(NativeRangeCatalog.Expand(binding, centered)), "元数据序列化丢失落格顺序");
                    }
                    var attack = session.Get(new(RangeKind.Attack, 0));
                    var attackOrder = new CellOffset[] { new(1, 0), new(-1, 0), new(0, -1), new(0, 1) };
                    Assert(NativeRangeCatalog.Expand(attack, centered).SequenceEqual(attackOrder), "攻击镜像轴顺序必须保留原生R,L,U,D");
                    foreach (int id in new[] { 4, 5, 7 })
                    {
                        var line = session.Get(new(RangeKind.Pierce, id));
                        int nativeLimit = id == 4 ? 1 : id == 5 ? 5 : 2;
                        Assert(line.RuntimeCacheId == nativeLimit && RangeModelCopies.Clone([line]).Single().RuntimeCacheId == nativeLimit,
                            "原生方向分支结束缓存应保留实际循环上限");
                    }
                    if (session.Game.Version == EngineVersion.V65)
                    {
                        var composite = session.Get(new(RangeKind.Pierce, 11));
                        CellOffset[] expected = [new(1, -1), new(1, 1), new(2, 0), new(3, 0), new(4, 0)];
                        Assert(NativeRangeCatalog.Expand(composite, RangePreviewContext.Default).SequenceEqual(expected), "6.5 0B必须保留两侧后接三格直线的真实循环");
                        Assert(composite.RuntimeCacheId == 5 && composite.Definition.Groups[1].OrdinalStart == 2, "6.5 0B衰减须从第3级继续且缓存5");
                        var cloned = RangeModelCopies.Clone([composite]).Single();
                        var restored = JsonSerializer.Deserialize<RangeBinding>(JsonSerializer.Serialize(composite)) ?? throw new InvalidDataException("组合元数据往返失败");
                        Assert(cloned.RuntimeCacheId == 5 && cloned.Definition.Groups[1].OrdinalStart == 2 &&
                            restored.RuntimeCacheId == 5 && restored.Definition.Groups[1].OrdinalStart == 2, "组合复制或序列化丢失衰减起点及缓存语义");
                    }
                    var side = new RangeKey(RangeKind.Pierce, 9);
                    int originalFirst = session.Get(side).Definition.Groups[0].Generators[0].Dy;
                    Assert(session.UpdateText(side, 0, $"0,{originalFirst}; 0,{-originalFirst}; 0,{originalFirst}", out _), "双侧等价输入应可归一");
                    Assert(session.Get(side).Definition.Groups[0].Generators.Count == 1 &&
                        session.Get(side).Definition.Groups[0].Generators[0].Dy == originalFirst, "双侧去重应保留首次侧向及顺序");
                    var headings = new CellOffset[] { new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
                        new(1, 1), new(-1, -1), new(1, -1), new(-1, 1), new(0, 0) };
                    var nativeFirst = new CellOffset[] { new(0, -1), new(0, -1), new(-1, 0), new(-1, 0),
                        new(1, -1), new(1, -1), new(-1, -1), new(-1, -1), new(-1, 0) };
                    for (int i = 0; i < headings.Length; i++)
                    {
                        int nativeSign = session.Game.Version == EngineVersion.V66 ? -1 : 1;
                        var first = new CellOffset(nativeFirst[i].Dx * nativeSign, nativeFirst[i].Dy * nativeSign);
                        CellOffset[] expected = [new(headings[i].Dx + first.Dx, headings[i].Dy + first.Dy),
                            new(headings[i].Dx - first.Dx, headings[i].Dy - first.Dy)];
                        var actual = session.Preview(side, new(headings[i]));
                        Assert(actual.SequenceEqual(expected), $"双侧顺序必须按原生4254FA坐标分支匹配八朝向与重合场景（i={i} heading={headings[i]} actual={string.Join("/", actual.Select(c => c.ToString()))} expected={string.Join("/", expected.Select(c => c.ToString()))} sign={nativeSign} stored={originalFirst}）");
                    }
                    var sideCells = session.Preview(side, RangePreviewContext.Default);
                    session.EditCell(side, 0, sideCells[^1], RangePreviewContext.Default, add: false);
                    Assert(session.Get(side).Definition.Groups[0].Generators.Count == 0, "取消双侧任一格应删除完整等价组");
                });
                Case(baseName, "已知自身中心补丁与未知包装器识别", copy =>
                {
                    using var game = new GameBase(copy);
                    var patch = new CenterPatch(game.Exe, 0, 0);
                    Assert(patch.CanApply && patch.Apply(PenetrationCenter.Self), "无法在私有内存副本安装已知自身中心补丁");
                    var rows = NativeRangeCatalog.Read(game);
                    Assert(rows.Where(r => r.Key.Kind == RangeKind.Pierce && r.Key.Id != 0 &&
                        !(game.Version == EngineVersion.V65 && r.Key.Id == 10)).All(r => r.Editable && r.Center == RangeCenterMode.Self), "已知自身中心补丁未被正确识别");
                    var line = rows.Single(r => r.Key == new RangeKey(RangeKind.Pierce, 5));
                    var lineCells = NativeRangeCatalog.Expand(line, new(new(2, 0)));
                    Assert(line.Center == RangeCenterMode.Self && lineCells.Any(c => c.Dx > 0), "自身中心直线应沿自身到主目标方向展开");
                    Assert(NativeRangeCatalog.ToGenerator(line, 0, new(1, 0), RangePreviewContext.Default) == new CellOffset(1, 0), "自身中心直线应支持按实际方向逆变换");
                    var side = rows.Single(r => r.Key == new RangeKey(RangeKind.Pierce, 9));
                    int originalSide = side.Definition.Groups[0].Generators[0].Dy;
                    Assert(NativeRangeCatalog.Expand(side, RangePreviewContext.Default).ToHashSet().SetEquals([new(0, -1), new(0, 1)]), "自身中心双侧应按自身到主目标方向保留垂直两格");
                    var inverseA = NativeRangeCatalog.ToGenerator(side, 0, new(0, -1), RangePreviewContext.Default);
                    var inverseB = NativeRangeCatalog.ToGenerator(side, 0, new(0, 1), RangePreviewContext.Default);
                    Assert(inverseA == new CellOffset(0, originalSide) && inverseB == new CellOffset(0, originalSide), $"自身中心双侧点击应使用原生坐标分支的可逆基底（实际 {inverseA}/{inverseB}，原侧 {originalSide}）");
                    game.Exe.WriteByte(patch.CaveVa, 0x90);
                    rows = NativeRangeCatalog.Read(game);
                    Assert(rows.Where(r => r.Key.Kind == RangeKind.Pierce).All(r => !r.Editable), "未知中心补丁不应继续猜测目标中心");
                });
                RawWriteCase(baseName, "删除清空保留编号与图号", copy =>
                {
                    using var session = new RangeEditSession(copy);
                    var key = session.Add(RangeKind.Attack);
                    session.UpdateText(key, 0, "2,0", out _);
                    session.Save();
                    int rows = session.Bindings.Count;
                    int imagesBefore = ReadArchive(Resource(copy, "Hitarea.e5")).Count;
                    session.Delete(key);
                    Assert(session.Bindings.Count == rows && session.Get(key).Definition.Groups.All(g => g.Generators.Count == 0), "删除必须保留编号并清空定义");
                    session.Save();
                    using var reloaded = new RangeEditSession(copy);
                    Assert(reloaded.Bindings.Count == rows && reloaded.Preview(key, RangePreviewContext.Default).Count == 0, "删除状态未持久保存");
                    Assert(ReadArchive(Resource(copy, "Hitarea.e5")).Count == imagesBefore, "删除不应移动或减少图号");
                });
                RawWriteCase(baseName, "EXE指针与扩展数据真值校验", copy =>
                {
                    var key = new RangeKey(RangeKind.Attack, 0);
                    RangeKey managedPierce;
                    uint slot;
                    int savedCount;
                    using (var session = new RangeEditSession(copy))
                    {
                        session.UpdateText(key, 0, "1,0; 2,0", out _);
                        managedPierce = session.Add(RangeKind.Pierce);
                        session.UpdateText(managedPierce, 0, "2,0", out _);
                        session.Save();
                        slot = session.Get(key).PointerSlots.Single();
                        savedCount = session.Preview(key, RangePreviewContext.Default).Count;
                        Assert(savedCount == 8, "原生表覆盖保存基线错误");
                    }
                    string executable = Path.Combine(copy, "Ekd5.exe");
                    byte[] intact = File.ReadAllBytes(executable);
                    var external = new PeFile(executable);
                    uint redirected = external.ReadU32(GameBase.RangeShapeTableVa + 4);
                    external.WriteU32(slot, redirected);
                    File.WriteAllBytes(executable, external.Bytes);
                    MustThrow<InvalidDataException>(() => { using var _ = new RangeEditSession(copy); },
                        "原生表槽已被外部重定向时必须拒绝旧元数据，不能显示旧图");

                    // A changed managed payload must also be detected by the
                    // extension integrity hash; do not rewrite its footer hash.
                    File.WriteAllBytes(executable, intact);
                    external = new PeFile(executable);
                    uint pointer = external.ReadU32(slot);
                    int payloadOffset = external.VaToOffset(pointer);
                    byte[] damaged = (byte[])intact.Clone();
                    damaged[payloadOffset] ^= 1;
                    File.WriteAllBytes(executable, damaged);
                    MustThrow<InvalidDataException>(() => { using var _ = new RangeEditSession(copy); }, "外部改写扩展段必须拒绝，不能显示旧元数据几何");

                    // A managed record must not override the safety state of a
                    // newly unknown native center wrapper during Load's merge.
                    external = new PeFile(executable, intact);
                    external.WriteByte(0x004364C7, 0x90);
                    File.WriteAllBytes(executable, external.Bytes);
                    using var unknownCenter = new RangeEditSession(copy);
                    Assert(unknownCenter.Get(managedPierce).IsManaged &&
                        unknownCenter.Bindings.Where(b => b.Key.Kind == RangeKind.Pierce).All(b => !b.Editable),
                        "扩展记录合并不能绕过未知中心包装器的只读状态");
                    MustThrow<InvalidDataException>(() => unknownCenter.Add(RangeKind.Pierce), "未知中心包装器必须阻止新增穿透编号");
                });
                Case(baseName, "E5原图改色不影响EXE几何与固定素材预览", copy =>
                {
                    var attack = new RangeKey(RangeKind.Attack, 1);
                    var pierce = new RangeKey(RangeKind.Pierce, 1);
                    IReadOnlyList<CellOffset> expectedAttack;
                    IReadOnlyList<CellOffset> expectedPierce;
                    string expectedIcon;
                    using (var session = new RangeEditSession(copy))
                    {
                        expectedAttack = session.Preview(attack, RangePreviewContext.Default);
                        expectedPierce = session.Preview(pierce, RangePreviewContext.Default);
                        using var tiles = session.CreatePreviewTiles(pierce);
                        using var rendered = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Pierce, expectedPierce, RangePreviewContext.Default.Target, tiles);
                        expectedIcon = BitmapFingerprint(rendered);
                        crossBaseIconFingerprint ??= expectedIcon;
                        Assert(crossBaseIconFingerprint == expectedIcon, "三基底相同穿透几何的固定素材输出不一致");
                    }
                    using (var images = new E5RangeImages(copy))
                    {
                        using var wrong = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var graphics = Graphics.FromImage(wrong)) graphics.Clear(Color.Black);
                        images.SetIcon(true, 1, wrong);
                        images.SetIcon(false, 1, wrong);
                        images.Save();
                    }
                    using var reopened = new RangeEditSession(copy);
                    Assert(reopened.Preview(attack, RangePreviewContext.Default).SequenceEqual(expectedAttack) &&
                        reopened.Preview(pierce, RangePreviewContext.Default).SequenceEqual(expectedPierce), "故意错画的E5条目改变了EXE范围真值");
                    using var fixedTiles = reopened.CreatePreviewTiles(pierce);
                    using var actualIcon = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Pierce, expectedPierce, RangePreviewContext.Default.Target, fixedTiles);
                    Assert(BitmapFingerprint(actualIcon) == expectedIcon, "当前基底E5改色改变了固定素材输出");
                    File.Delete(Resource(copy, "Hitarea.e5"));
                    File.Delete(Resource(copy, "Effarea.e5"));
                    using var missingImages = new E5RangeImages(copy);
                    using var independentTiles = missingImages.CreateFixedPixelTiles();
                    using var independentIcon = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Pierce, expectedPierce, RangePreviewContext.Default.Target, independentTiles);
                    Assert(BitmapFingerprint(independentIcon) == expectedIcon, "缺失目标基底E5时固定素材不可独立加载");
                });
                Case(baseName, "文本校验、鼠标幂等、归一化与跨页草稿", copy =>
                {
                    using var form = new MainForm(openDefaultBase: false);
                    try
                    {
                        form.OpenBaseForTest(copy);
                        var session = form.SessionForTest ?? throw new InvalidDataException("UI未载入测试基底");
                        Assert(form.TabsForTest.TabPages.Count == 3 && form.TabsForTest.TabPages[0].Text == "攻击范围" &&
                            form.TabsForTest.TabPages[1].Text == "穿透范围" && form.TabsForTest.TabPages[2].Text == "拓展范围", "上方三个范围页签不完整");
                        Assert(!form.AttackEditorForTest.EditTextForTest(1, "1,"), "不完整输入应保留为错误草稿");
                        string invalid = form.AttackEditorForTest.GeneratorsForTest.Text;
                        form.SelectPageForTest(1); form.SelectPageForTest(0);
                        Assert(form.AttackEditorForTest.GeneratorsForTest.Text == invalid && session.GetTextError(new(RangeKind.Attack, 1), 0) != null, "跨页丢失错误草稿");
                        MustThrow<InvalidDataException>(session.Save, "存在错误草稿时必须阻止保存");
                        session.Restore(new(RangeKind.Attack, 1));
                        var key = session.Add(RangeKind.Attack);
                        Assert(form.AttackEditorForTest.SelectRangeForTest(key.Id), "UI未显示新增编号");
                        var preview = form.AttackEditorForTest.PreviewForTest;
                        var mouse = typeof(ShapeGridControl).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        void Click(MouseButtons button, CellOffset cell)
                        {
                            Point location = preview.CellCenterForTest(cell);
                            mouse.Invoke(preview, [new MouseEventArgs(button, 1, location.X, location.Y, 0)]);
                        }
                        Click(MouseButtons.Left, new(2, 0)); Click(MouseButtons.Left, new(2, 0));
                        Assert(session.Get(key).Definition.Groups[0].Generators.Count == 1 && session.Preview(key, RangePreviewContext.Default).Count == 4, "左键重复选中应幂等并展开镜像组");
                        Click(MouseButtons.Right, new(-2, 0)); Click(MouseButtons.Right, new(-2, 0));
                        Assert(session.Get(key).Definition.Groups[0].Generators.Count == 0, "右键重复取消应幂等并删除整个镜像组");
                        Assert(session.UpdateText(key, 0, "1,0; -1,0; 0,1; 0,-1; 2,0", out _), "合法正负轴输入应可归一");
                        Assert(session.Get(key).Definition.Groups[0].Generators.Count == 2, "正负/轴等价生成元未去重");
                        Assert(!session.UpdateText(key, 0, "-1,-1", out _), "FFFF保留字必须拒绝");
                        Assert(!session.UpdateText(key, 0, "32,0", out _), "超出可编辑偏移必须拒绝");
                    }
                    finally { form.CloseForTest(); }
                });
                Case(baseName, "穿透容量与大范围图片边界", copy =>
                {
                    var original = FileHashes(copy);
                    using var session = new RangeEditSession(copy);
                    var key = session.Add(RangeKind.Pierce);
                    session.UpdateText(key, 0, "1,0;1,1;2,0;2,1;1,2;2,2;3,0", out _);
                    MustThrow<InvalidDataException>(session.Validate, "超过穿透目标预算必须拒绝");
                    AssertHashes(original);
                    session.Restore(key);
                    key = session.Add(RangeKind.Attack);
                    session.UpdateText(key, 0, "31,31", out _);
                    session.Validate();
                    using var tiles = session.CreatePreviewTiles(key);
                    using var tiny = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Attack, [new(31, 31)], null, tiles);
                    Assert(CountDominant(tiny, 'r') > 0 && CountDominant(tiny, 'b') > 0, "单像素格不能裁掉真实活动格与自身标记");
                    using var large = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Pierce, [new(5, 0)], new(1, 0), tiles);
                    Assert(CountDominant(large, 'g') > 0 && CountDominant(large, 'b') > 0, ">7格固定素材图片必须完整显示穿透和自身");
                    var allowed = new HashSet<int>();
                    foreach (var source in new[] { tiles.Background, tiles.Own, tiles.Attack, tiles.Pierce, tiles.Frame! })
                        for (int y = 0; y < source.Height; y++)
                        for (int x = 0; x < source.Width; x++) allowed.Add(source.GetPixel(x, y).ToArgb());
                    foreach (var icon in new[] { tiny, large })
                        for (int y = 0; y < icon.Height; y++)
                            for (int x = 0; x < icon.Width; x++)
                                Assert(allowed.Contains(icon.GetPixel(x, y).ToArgb()), "E5输出包含固定素材以外的生成颜色");
                    MustThrow<InvalidDataException>(() => { using var _ = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Attack, [new(32, 0)], null, tiles); }, "超过64像素容量应明确拒绝而非裁剪");
                    var layout = new RangeGridLayout(new(10, 20, 55, 45), 11, 9, 5);
                    Assert(layout.TryHitTest(new(12, 22), out var corner) && corner == new CellOffset(-5, -4), "点击逆变换错误");
                    Assert(!layout.TryHitTest(new(9, 22), out _), "网格外点击必须拒绝");
                });
                Case(baseName, "事务失败回滚与外部文件漂移", copy =>
                {
                    var original = FileHashes(copy);
                    var candidates = original.Keys.Where(p => Path.GetFileName(p) != "Spalet.e5").ToDictionary(p => p,
                        p => File.ReadAllBytes(p).Concat(new byte[] { 0x7E }).ToArray(), StringComparer.OrdinalIgnoreCase);
                    MustThrow<IOException>(() => RangeSaveTransaction.Commit(copy, candidates, original,
                        index => { if (index == 1) throw new IOException("injected failure before second replacement"); }), "故障注入必须中断提交");
                    AssertHashes(original);
                    Assert(!File.Exists(Path.Combine(copy, ".ccz-range-save.json")), "回滚后不应残留未完成日志");
                    using var session = new RangeEditSession(copy);
                    var key = session.Add(RangeKind.Attack);
                    session.UpdateText(key, 0, "2,0", out _);
                    string external = Resource(copy, "Hitarea.e5");
                    File.AppendAllText(external, "external-drift");
                    string changed = RangeSaveTransaction.Hash(external);
                    MustThrow<IOException>(session.Save, "外部资源漂移必须阻止保存");
                    Assert(RangeSaveTransaction.Hash(external) == changed && RangeSaveTransaction.Hash(Path.Combine(copy, "Ekd5.exe")) == original[Path.Combine(copy, "Ekd5.exe")], "漂移阻断不能覆盖外部变化或EXE");
                });
                Case(baseName, "prepared中断恢复与committed日志清理", copy =>
                {
                    string path = Path.Combine(copy, "Ekd5.exe");
                    byte[] before = File.ReadAllBytes(path);
                    byte[] after = before.Concat(new byte[] { 0x5A }).ToArray();
                    string backup = path + ".bak-range-interruption-test";
                    string staged = path + ".range-stage-interruption-test";
                    string journal = Path.Combine(copy, ".ccz-range-save.json");
                    string beforeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before));
                    string afterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(after));
                    void WriteJournal(string phase)
                    {
                        File.WriteAllBytes(backup, before);
                        File.WriteAllBytes(staged, after);
                        File.WriteAllText(journal, JsonSerializer.Serialize(new
                        {
                            Phase = phase,
                            Files = new[] { new { Path = path, Backup = backup, Staged = staged, BeforeHash = beforeHash, AfterHash = afterHash } }
                        }));
                    }
                    WriteJournal("prepared");
                    File.WriteAllBytes(path, after);
                    RangeSaveTransaction.Recover(copy);
                    Assert(RangeSaveTransaction.Hash(path) == beforeHash && !File.Exists(staged) && !File.Exists(journal), "prepared中断未完整恢复旧文件及清理候选");
                    WriteJournal("committed");
                    File.WriteAllBytes(path, after);
                    RangeSaveTransaction.Recover(copy);
                    Assert(RangeSaveTransaction.Hash(path) == afterHash && !File.Exists(staged) && !File.Exists(journal), "committed恢复应保留已提交新文件并清理日志");
                    Assert(RangeSaveTransaction.Hash(backup) == beforeHash, "恢复流程损坏了可追溯备份");
                });
                if (baseName == "复刻版6.6山贼")
                {
                    Case(baseName, "仅改中心保留原生共享段、严格复读及后续共享几何编辑", copy =>
                    {
                        var attackKey = new RangeKey(RangeKind.Attack, 0);
                        var pierceKey = new RangeKey(RangeKind.Pierce, 1);
                        using var session = new RangeEditSession(copy);
                        Assert(ReferenceEquals(session.Get(attackKey).Definition, session.Get(pierceKey).Definition),
                            "中心专项缺少原生共享定义");
                        uint nativePointer = session.Get(pierceKey).NativePointer;
                        Assert(session.UpdateCenter(pierceKey, RangeCenterMode.Self, out var error), error);
                        Assert(session.Get(pierceKey).IsModified && !session.Get(attackKey).IsModified,
                            "仅改中心不应修改攻击几何");
                        session.Save();
                        Assert(session.Get(pierceKey).Center == RangeCenterMode.Self &&
                            session.Get(pierceKey).NativePointer == nativePointer &&
                            session.Get(attackKey).NativePointer == nativePointer &&
                            ReferenceEquals(session.Get(attackKey).Definition, session.Get(pierceKey).Definition),
                            "仅改中心保存后原生共享指针或定义身份丢失");
                        using (var reopened = new RangeEditSession(copy))
                            Assert(reopened.Get(pierceKey).Center == RangeCenterMode.Self &&
                                ReferenceEquals(reopened.Get(attackKey).Definition, reopened.Get(pierceKey).Definition),
                                "仅改中心重开后共享关系丢失");
                        string exePath = Path.Combine(copy, "Ekd5.exe");
                        byte[] pristine = File.ReadAllBytes(exePath);
                        var corrupt = new PeFile(exePath, pristine);
                        corrupt.WriteByte(nativePointer, (byte)(corrupt.ReadByte(nativePointer) ^ 1));
                        File.WriteAllBytes(exePath, corrupt.Bytes);
                        bool refused = false;
                        try { using var invalid = new RangeEditSession(copy); }
                        catch (InvalidDataException) { refused = true; }
                        finally { File.WriteAllBytes(exePath, pristine); }
                        Assert(refused, "原生共享段与元数据不一致时必须拒绝读取");
                        Assert(session.UpdateText(pierceKey, 0, "1,0; 3,0", out error), error);
                        session.Save();
                        Assert(session.Get(attackKey).NativePointer == session.Get(pierceKey).NativePointer &&
                            ReferenceEquals(session.Get(attackKey).Definition, session.Get(pierceKey).Definition),
                            "中心修改后的共享几何编辑拆开了别名");
                        using var final = new RangeEditSession(copy);
                        Assert(final.Preview(attackKey, RangePreviewContext.Default).ToHashSet().SetEquals(
                            new[] { new CellOffset(1, 0), new CellOffset(-1, 0), new CellOffset(0, 1), new CellOffset(0, -1),
                                new CellOffset(3, 0), new CellOffset(-3, 0), new CellOffset(0, 3), new CellOffset(0, -3) }) &&
                            final.Get(pierceKey).Center == RangeCenterMode.Self,
                            "后续共享编辑的几何或中心未重开一致");
                    });
                    Case(baseName, "RAW精确调色板颜色正向编码与复读", copy =>
                    {
                        var before = ReadArchive(Resource(copy, "Hitarea.e5"));
                        using var images = new E5RangeImages(copy);
                        Assert(images.AttackIsIndexed, "该测试必须使用真实 RAW 档案");
                        var source = images.Get(true, 0) ?? throw new InvalidDataException("RAW原图无法解码");
                        Color[] colors = [source.GetPixel(0, 0), source.GetPixel(1, 1), source.GetPixel(32, 32), source.GetPixel(63, 63)];
                        using var exact = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        for (int y = 0; y < 64; y++)
                        for (int x = 0; x < 64; x++) exact.SetPixel(x, y, colors[(x + y) % colors.Length]);
                        images.SetIcon(true, 0, exact);
                        images.Save();
                        using var reopened = new E5RangeImages(copy);
                        var actual = reopened.Get(true, 0) ?? throw new InvalidDataException("RAW写入后无法复读");
                        Assert(BitmapFingerprint(actual) == BitmapFingerprint(exact), "精确调色板颜色未逐像素复读一致");
                        Assert(reopened.DecodedLength(true, 0) is 4096 or 4098, "RAW载荷长度被改变");
                        AssertUnchangedPayloads(before, ReadArchive(Resource(copy, "Hitarea.e5")), [0]);
                    });
                    Case(baseName, "RAW固定素材最近色保存、重开及Spalet保持不变", copy =>
                    {
                        string palette = Resource(copy, "Spalet.e5");
                        string paletteHash = RangeSaveTransaction.Hash(palette);
                        using var session = new RangeEditSession(copy);
                        var key = session.Add(RangeKind.Attack);
                        Assert(session.UpdateText(key, 0, "1,0", out _), "创建近色测试草稿失败");
                        session.Save();
                        Assert(!session.IsDirty, "近色保存后应接受新基线");
                        using var reopened = new RangeEditSession(copy);
                        Assert(reopened.Preview(key, RangePreviewContext.Default).ToHashSet().SetEquals(
                            new[] { new CellOffset(1, 0), new CellOffset(-1, 0), new CellOffset(0, 1), new CellOffset(0, -1) }),
                            "近色保存后范围几何未重开一致");
                        using var images = new E5RangeImages(copy);
                        Assert(images.DecodedLength(true, key.Id) == 4096 && images.Get(true, key.Id) != null,
                            "近色保存后新增RAW索引图不可复读");
                        Assert(RangeSaveTransaction.Hash(palette) == paletteHash, "近色编码不能修改Spalet");
                        Assert(!File.Exists(Path.Combine(copy, ".ccz-range-save.json")), "成功保存后不应保留事务日志");
                    });
                    RawWriteCase(baseName, "共享别名、撤销重做、恢复与重复保存", copy =>
                    {
                        using var session = new RangeEditSession(copy);
                        var attack = new RangeKey(RangeKind.Attack, 0);
                        var pierce = new RangeKey(RangeKind.Pierce, 1);
                        string original = session.GetText(attack, 0);
                        Assert(ReferenceEquals(session.Get(attack).Definition, session.Get(pierce).Definition), "物理共享定义没有合并");
                        var beforeAttack = ReadArchive(Resource(copy, "Hitarea.e5"));
                        var beforePierce = ReadArchive(Resource(copy, "Effarea.e5"));
                        session.UpdateText(pierce, 0, "1,0; 2,0", out _);
                        Assert(session.Get(attack).IsModified && session.Get(pierce).IsModified && session.Preview(attack, RangePreviewContext.Default).Count == 8, "共享编辑未联动");
                        session.Undo();
                        Assert(session.GetText(attack, 0) == original && !session.IsDirty, "撤销没有同时恢复共享定义");
                        session.Redo();
                        Assert(session.Preview(attack, RangePreviewContext.Default).Count == 8, "重做没有恢复共享编辑");
                        session.Restore(pierce);
                        Assert(session.GetText(attack, 0) == original && !session.IsDirty, "恢复原始未作用到所有别名");
                        session.UpdateText(attack, 0, "1,0; 2,0", out _);
                        session.Save();
                        Assert(ReferenceEquals(session.Get(attack).Definition, session.Get(pierce).Definition), "保存重开后共享身份断开");
                        Assert(session.Preview(attack, RangePreviewContext.Default).Count == 8 && session.Preview(pierce, RangePreviewContext.Default).Count == 8, "保存重开后共享几何丢失");
                        session.UpdateText(pierce, 0, "1,0; 3,0", out _);
                        session.Save();
                        Assert(session.Game.Exe.ReadU32(0x4D1400) == session.Game.Exe.ReadU32(0x4D13A0), "重复保存后共享指针分离");
                        AssertUnchangedPayloads(beforeAttack, ReadArchive(Resource(copy, "Hitarea.e5")), [0]);
                        AssertUnchangedPayloads(beforePierce, ReadArchive(Resource(copy, "Effarea.e5")), [1]);
                    });
                }
            }
        }
        catch (Exception ex) { results.Add(new("全局", "验证基础设施", false, ex.ToString(), 0)); }
        finally
        {
            foreach (var (path, before) in sourceHashes)
                results.Add(new(Path.GetFileName(Path.GetDirectoryName(path)) ?? "基底", "真实基底文件未修改：" + Path.GetFileName(path),
                    RangeSaveTransaction.Hash(path) == before, before, 0));
        }
        bool passed = results.All(r => r.Passed);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Tool = "攻击穿透范围管理器", Version = typeof(RangeVerification).Assembly.GetName().Version?.ToString(),
            Created = DateTimeOffset.Now, Passed = passed, Checks = results.Count, Results = results,
            SourceHashes = sourceHashes,
            IndexedColourPolicy = "精确RGB优先；缺色时选调色板RGB平方距离最小项，同距最低索引；Spalet不修改。",
            RuntimeEvidence = "本报告验证工具的编解码、EXE/E5持久化及事务；不将可加载或静态机器码检查表述为战斗命中实测。"
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var item in results) Console.WriteLine($"{(item.Passed ? "PASS" : "FAIL")} {item.Base} {item.Name}{(item.Passed ? "" : " " + item.Detail)}");
        Console.WriteLine((passed ? "RANGE_V2_VERIFICATION_OK " : "RANGE_V2_VERIFICATION_FAILED ") + output);
        // The generated GUID directory is the sole recursive cleanup target.
        string full = Path.GetFullPath(temporary);
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("ccz-range-v2-verify-", StringComparison.Ordinal))
            try { Directory.Delete(full, recursive: true); } catch { }
        return passed ? 0 : 1;
    }

    private static int CountDominant(Bitmap image, char channel)
    {
        int count = 0;
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            var color = image.GetPixel(x, y);
            if (channel == 'r' && color.R > color.G * 1.5 && color.R > color.B * 1.5 ||
                channel == 'g' && color.G > color.R * 1.5 && color.G > color.B * 1.5 ||
                channel == 'b' && color.B > color.R * 1.5 && color.B > color.G * 1.5) count++;
        }
        return count;
    }

    private static (int Width, int Height) TileSize(Bitmap bitmap) => (bitmap.Width, bitmap.Height);

    private static byte[] EmbeddedBytes(string fileName)
    {
        using var stream = typeof(E5RangeImages).Assembly.GetManifestResourceStream("RangeManager.App.Assets.Range." + fileName)
            ?? throw new InvalidDataException("未内嵌固定素材：" + fileName);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static void VerifyFixedTemplateSource(string workspace, E5RangeImages images, string evidenceDirectory)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        const string archiveSha = "BE79C7364350E3E3B2B84576D2F0686A4518BF23650D3598C19C704FE6328D83";
        const string entrySha = "043739505A4B2577FBED71D6C1504F9B3481E7E3F5A36F69663CF6E71DD909A5";
        string path = Path.Combine(workspace, "基底", "信奈的野望（15+1关版，密码nobuna）", "E5", "Effarea.e5");
        Require(RangeSaveTransaction.Hash(path) == archiveSha, "信奈固定素材源档案哈希不符");
        var sourceEntry = ReadArchive(path)[9];
        Require(sourceEntry.Stored.Length == sourceEntry.DecodedLength && Hash(sourceEntry.Stored) == entrySha,
            "信奈第10项原始载荷哈希不符");
        using var manifest = JsonDocument.Parse(EmbeddedBytes("SignalEffarea10Manifest.json"));
        var root = manifest.RootElement;
        Require(root.GetProperty("sourceEntry").GetInt32() == 10 &&
            root.GetProperty("sourceArchiveSha256").GetString() == archiveSha &&
            root.GetProperty("sourceEntrySha256").GetString() == entrySha, "固定素材清单来源信息不符");
        foreach (var file in root.GetProperty("files").EnumerateObject())
        {
            string name = file.Value.GetProperty("file").GetString()!;
            Require(Hash(EmbeddedBytes(name)).Equals(file.Value.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase),
                "内嵌固定素材文件哈希不符：" + name);
        }
        Require(EmbeddedBytes("SignalEffarea10Frame.png").AsSpan().SequenceEqual(sourceEntry.Stored),
            "内嵌原图未保留信奈第10项原始 PNG 载荷");
        using var stream = new MemoryStream(sourceEntry.Stored, writable: false);
        using var source = new Bitmap(stream);
        using var tiles = images.CreateFixedPixelTiles();
        Require(tiles.Frame is { Width: 64, Height: 64 } && source.Size == tiles.Frame.Size, "固定原图必须为64×64");
        Require(BitmapFingerprint(source) == BitmapFingerprint(tiles.Frame!), "内嵌原图解码改变了源像素或透明度");
        foreach (var (name, tile, gx, gy) in new[]
        {
            ("background", tiles.Background, 0, 0), ("attack", tiles.Attack, 1, 1),
            ("own", tiles.Own, 2, 2), ("pierce", tiles.Pierce, 2, 0)
        })
        {
            var coordinate = root.GetProperty("tiles").GetProperty(name);
            Require(coordinate[0].GetInt32() == gx && coordinate[1].GetInt32() == gy, "固定取样坐标漂移：" + name);
            Require(tile.Width == 8 && tile.Height == 8, "固定像素块不是8×8：" + name);
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Require(tile.GetPixel(x, y).ToArgb() == source.GetPixel(1 + gx * 9 + x, 1 + gy * 9 + y).ToArgb(),
                    $"固定像素块与原始裁剪不符：{name} ({x},{y})");
        }
        using var icon = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Pierce, [new(0, -1), new(-1, 1)], new(1, 0), tiles);
        Require(icon.Width == 64 && icon.Height == 64, "固定素材输出不是64×64正方形");
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
            if (x % 9 == 0 || y % 9 == 0)
                Require(icon.GetPixel(x, y).ToArgb() == source.GetPixel(x, y).ToArgb(),
                    $"固定外框/分隔线像素改变：({x},{y})");
        Directory.CreateDirectory(evidenceDirectory);
        icon.Save(Path.Combine(evidenceDirectory, "fixed-template-native7.png"), System.Drawing.Imaging.ImageFormat.Png);
        using var wide = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Attack,
            ShapeGeometry.ExpandMirror([new(20, 0)]), null, tiles);
        wide.Save(Path.Combine(evidenceDirectory, "fixed-template-41.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    private static string BitmapFingerprint(Bitmap bitmap)
    {
        byte[] pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        int offset = 0;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan(offset, 4), bitmap.GetPixel(x, y).ToArgb());
            offset += 4;
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
    }

    private static void MustThrow<T>(Action action, string reason) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidDataException(reason);
    }

    private static string FindWorkspace()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), Application.StartupPath })
            for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "基底")) && Directory.Exists(Path.Combine(dir.FullName, "data管理器"))) return dir.FullName;
        throw new DirectoryNotFoundException("无法定位含基底和data管理器的工作目录");
    }

    private static string Resource(string root, string name)
    {
        string nested = Path.Combine(root, "E5", name);
        return File.Exists(nested) ? nested : Path.Combine(root, name);
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        yield return Path.Combine(root, "Ekd5.exe");
        foreach (string name in ResourceNames) yield return Resource(root, name);
        string data = Resource(root, "Data.e5");
        if (File.Exists(data)) yield return data;
    }

    private static void CopyFixture(string source, string target)
    {
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(target, "E5"));
        File.Copy(Path.Combine(source, "Ekd5.exe"), Path.Combine(target, "Ekd5.exe"));
        foreach (string name in ResourceNames)
        {
            string file = Resource(source, name);
            if (File.Exists(file)) File.Copy(file, Path.Combine(target, "E5", name));
        }
    }

    private static Dictionary<string, string> FileHashes(string root) => new[] { Path.Combine(root, "Ekd5.exe"),
        Resource(root, "Hitarea.e5"), Resource(root, "Effarea.e5"), Resource(root, "Spalet.e5") }
        .Where(File.Exists).ToDictionary(p => p, RangeSaveTransaction.Hash, StringComparer.OrdinalIgnoreCase);

    private static void AssertHashes(IReadOnlyDictionary<string, string> before)
    {
        foreach (var (path, hash) in before)
            if (RangeSaveTransaction.Hash(path) != hash) throw new InvalidDataException("文件未完整回滚：" + path);
    }

    private static List<Entry> ReadArchive(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int first = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0x118, 4)));
        int count = (first - 0x110) / 12;
        var entries = new List<Entry>();
        for (int i = 0; i < count; i++)
        {
            int at = 0x110 + i * 12;
            int stored = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4)));
            int decoded = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 4, 4)));
            int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8, 4)));
            entries.Add(new(bytes.AsSpan(offset, stored).ToArray(), decoded));
        }
        return entries;
    }

    private static void AssertUnchangedPayloads(List<Entry> before, List<Entry> after, HashSet<int> modified)
    {
        if (after.Count < before.Count) throw new InvalidDataException("旧E5条目数减少");
        for (int i = 0; i < before.Count; i++)
            if (!modified.Contains(i) && (before[i].DecodedLength != after[i].DecodedLength || !before[i].Stored.AsSpan().SequenceEqual(after[i].Stored)))
                throw new InvalidDataException($"未编辑的E5条目 {i + 1} 载荷发生改变");
    }
}
