using Iced.Intel;
using TransformManager.App;
using TierManager.App;

namespace EngineGrowth.Core;

/// <summary>Reclaims only reproducible managed code and otherwise untouched filling.
/// All original call paths are restored in memory before new allocations are chosen.</summary>
internal sealed class GrowthTailPreservationRequiredException : Exception;

internal static class ManagedGrowthLayout
{
    public static byte[] RestoreBaseline(TreasureTransformService service, TierProfile tier, byte[] before)
    {
        var game = service.Base;
        if (!game.IsPatched && !tier.HasMarker) return before;
        var pe = game.Exe;
        var reservations = GrowthCaveAllocator.ReadReservations(pe, game, tier);
        foreach (var r in reservations) GrowthCaveAllocator.ValidateReservations(pe, [r]);
        var footprints = new List<GrowthCaveReservation>();
        if (game.IsPatched)
        {
            if (game.Cave is not { } cave) throw new InvalidDataException("既有质变代码洞无法定位。");
            var rules = service.Rules.OrderBy(p => p.Key).Select(p => new EffectRule(p.Key, p.Value)).ToArray();
            var blob = RuleCompiler.Build(rules, cave.Address, game.TransformEntryVa,
                cave.OriginalBytes[0], cave.Length).Blob;
            var current = pe.ReadBytes(cave.Address, blob.Length);
            if (!current.AsSpan().SequenceEqual(blob))
                throw new InvalidDataException("既有质变代码与 TMR1 规则不能完整重建，不能自动重新布局。");
            footprints.Add(new(cave.Address, blob.Length, "质变"));
        }
        if (tier.HasMarker)
        {
            if (!new MarkerReader(pe, tier.MarkerVa).TryRead(out var marker))
                throw new InvalidDataException("既有档位元数据无法读取。");
            int used = checked((int)(tier.MarkerVa - tier.CaveVa) + MarkerCodec.Build(marker).Length);
            if (used <= 0 || used > tier.CaveLength) throw new InvalidDataException("既有档位内容超出所属代码洞。");
            var replay = TierPatchBuilder.Prepare(tier, tier.Entries, before);
            int offset = pe.VaToOffset(tier.CaveVa);
            if (!MatchesTierImage(before, replay.AfterBytes, offset, used, tier, marker.CodeLength))
                throw new InvalidDataException("既有档位代码与 TIER1 规则不能完整重建，不能自动重新布局。");
            footprints.Add(new(tier.CaveVa, used, "档位"));
        }
        if (tier.HasMarker)
        {
            foreach (uint site in tier.CallSites)
                if (pe.ReadByte(site) != 0xE8 ||
                    site + 5 + unchecked((uint)BitConverter.ToInt32(pe.ReadBytes(site + 1, 4))) != tier.TierFuncVa)
                    throw new InvalidDataException("既有档位调用点已被其他补丁修改，不能重新布局。");
            if (pe.ReadByte(tier.DisplayPointerVa) != 0xBA ||
                BitConverter.ToUInt32(pe.ReadBytes(tier.DisplayPointerVa + 1, 4)) != tier.DisplayTableVa)
                throw new InvalidDataException("既有档位显示指针已被其他补丁修改，不能重新布局。");
        }
        GrowthCaveAllocator.ValidateReservations(pe, footprints);
        var after = before.ToArray();
        var image = new PeFile(game.ExePath, after);
        foreach (var range in reservations)
        {
            int offset = pe.VaToOffset(range.Address);
            for (int i = 0; i < range.Length; i++)
            {
                uint va = range.Address + (uint)i;
                if (!footprints.Any(f => va >= f.Address && va < (ulong)f.Address + (uint)f.Length)
                    && before[offset + i] is not (0x00 or 0x90 or 0xCC))
                    throw new GrowthTailPreservationRequiredException();
                // Metadata records the transform block's original fill byte.
                // Preserve unused fill tails exactly instead of repainting an
                // entire historical reservation with NOPs.
                if (footprints.Any(f => va >= f.Address && va < (ulong)f.Address + (uint)f.Length))
                    after[offset + i] = range.Owner == "质变" ? game.Cave!.OriginalBytes[0] : (byte)0x90;
            }
        }
        if (game.IsPatched) image.Patch(game.TransformEntryVa, game.EntryOriginal);
        if (tier.HasMarker)
        {
            foreach (uint site in tier.CallSites)
            {
                if (pe.ReadByte(site) != 0xE8 ||
                    site + 5 + unchecked((uint)BitConverter.ToInt32(pe.ReadBytes(site + 1, 4))) != tier.TierFuncVa)
                    throw new InvalidDataException("既有档位调用点已被其他补丁修改，不能重新布局。");
                image.Patch(site, [0xE8, .. BitConverter.GetBytes(unchecked((int)(tier.OriginalTierFuncVa - (site + 5))))]);
            }
            if (pe.ReadByte(tier.DisplayPointerVa) != 0xBA ||
                BitConverter.ToUInt32(pe.ReadBytes(tier.DisplayPointerVa + 1, 4)) != tier.DisplayTableVa)
                throw new InvalidDataException("既有档位显示指针已被其他补丁修改，不能重新布局。");
            image.Patch(tier.DisplayPointerVa + 1, BitConverter.GetBytes(tier.OriginalTableVa));
        }
        if (HasForeignReference(image, reservations))
            throw new InvalidDataException("旧成长代码洞仍被其他代码或数据引用，不能自动重新布局。");
        return after;
    }

    /// <summary>When earlier repeated writes left stale data, keep all those
    /// bytes in place under the same owner and release only a uniform tail.</summary>
    public static IReadOnlyList<GrowthCaveReservation> PreserveAndCompact(TreasureTransformService service,
        TierProfile tier, byte[] bytes)
    {
        var pe = service.Base.Exe;
        var ranges = GrowthCaveAllocator.ReadReservations(pe, service.Base, tier);
        var compact = new List<GrowthCaveReservation>();
        foreach (var range in ranges)
        {
            int offset = pe.VaToOffset(range.Address);
            int last = range.Length - 1;
            while (last >= 0 && bytes[offset + last] is 0x00 or 0x90 or 0xCC) last--;
            int length = Math.Min(range.Length, Math.Max(1024, (last + 1 + 255) / 256 * 256));
            compact.Add(range with { Length = length });
        }
        GrowthCaveAllocator.ValidateReservations(pe, compact);
        foreach (var range in compact)
        {
            if (range.Owner == "质变" && service.Base.Cave is { } cave)
            {
                var restore = pe.ReadBytes(cave.Address, range.Length);
                var oldRules = service.Rules.Select(p => new EffectRule(p.Key, p.Value)).ToArray();
                int used = RuleCompiler.RequiredSize(oldRules, cave.Address, service.Base.TransformEntryVa,
                    cave.OriginalBytes[0], cave.Length);
                Array.Fill(restore, cave.OriginalBytes[0], 0, Math.Min(used, restore.Length));
                service.Base.UseCave(new CaveInfo(cave.Address, range.Length, restore));
            }
            if (range.Owner == "档位" && tier.CaveVa == range.Address) tier.CaveLength = range.Length;
        }
        return compact;
    }

    private static bool MatchesTierImage(byte[] before, byte[] rebuilt, int offset, int used, TierProfile profile, int codeLength)
    {
        if (before.AsSpan(offset, used).SequenceEqual(rebuilt.AsSpan(offset, used))) return true;
        // Early TierWriter releases encoded CALL's displacement without
        // subtracting CodeVa. Accept exactly this historical four-byte error
        // at a generated, known mask/correction call, never arbitrary code.
        var legacy = rebuilt.AsSpan(offset, used).ToArray();
        var decoder = Decoder.Create(32, new ByteArrayCodeReader(rebuilt, offset, codeLength), profile.CaveVa, DecoderOptions.None);
        var known = new uint?[] { profile.MaskTarget, profile.CorrectionTarget, profile.CorrectionHelper };
        while (decoder.IP < (ulong)profile.CaveVa + (uint)codeLength)
        {
            var instruction = decoder.Decode();
            if (instruction.Length == 0) return false;
            if (instruction.Code != Code.Call_rel32_32 || !known.Contains((uint)instruction.NearBranchTarget)) continue;
            int displacement = checked((int)(instruction.IP - profile.CaveVa)) + 1;
            uint corrected = BitConverter.ToUInt32(legacy, displacement);
            uint oldEncoding = unchecked(corrected + profile.CaveVa);
            if (BitConverter.ToUInt32(before, offset + displacement) == oldEncoding)
                BitConverter.GetBytes(oldEncoding).CopyTo(legacy, displacement);
        }
        // Early writers left unused display indices as null pointers.
        // Current writers fill those explicitly with the base-tier label.
        for (int i = 0; i < profile.DisplayIndexOffset; i++)
        {
            int slot = checked((int)(profile.DisplayTableVa - profile.CaveVa)) + i * 4;
            if (slot < 0 || slot + 4 > used) return false;
            if (BitConverter.ToUInt32(before, offset + slot) == 0)
                Array.Clear(legacy, slot, 4);
        }
        return before.AsSpan(offset, used).SequenceEqual(legacy);
    }

    private static bool HasForeignReference(PeFile pe, IReadOnlyList<GrowthCaveReservation> ranges)
    {
        bool Inside(uint va) => ranges.Any(r => va >= r.Address && va < (ulong)r.Address + (uint)r.Length);
        // All managed references have already been restored and managed bytes
        // erased, so a remaining reference belongs to an unknown consumer.
        for (int i = 0; i + 4 <= pe.Bytes.Length; i++)
            if (Inside(BitConverter.ToUInt32(pe.Bytes, i))) return true;
        foreach (var section in pe.Sections.Where(s => s.IsExecutable))
        {
            var decoder = Decoder.Create(32, new ByteArrayCodeReader(pe.Bytes,
                (int)section.RawPointer, (int)section.RawSize), section.VirtualAddress, DecoderOptions.None);
            ulong end = (ulong)section.VirtualAddress + section.RawSize;
            while (decoder.IP < end)
            {
                var instruction = decoder.Decode();
                if (instruction.Length == 0) break;
                if (instruction.IsInvalid || instruction.FlowControl is not
                    (FlowControl.Call or FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)) continue;
                for (int op = 0; op < instruction.OpCount; op++)
                    if (instruction.GetOpKind(op) is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64
                        && Inside((uint)instruction.NearBranchTarget)) return true;
            }
        }
        return false;
    }
}
