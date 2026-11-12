using System.Text;
using TransformManager.App;

namespace TierManager.App;

public static class TierScanner
{
    private static readonly byte[] TierPrologue = [0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x08, 0x3C, 0x04];
    private static readonly byte[] DirectLoad = [0x8A, 0x44, 0x08, 0x21];
    private const uint CorrectionDefault = 0x00436E3D;

    public static TierProfile Load(string rootPath)
    {
        string root = Path.GetFullPath(rootPath);
        string exe = Path.Combine(root, "Ekd5.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("找不到 Ekd5.exe", exe);
        return LoadFromBytes(exe, File.ReadAllBytes(exe), root);
    }

    public static TierProfile LoadFromBytes(string exePath, byte[] bytes, string rootPath)
    {
        var pe = new PeFile(exePath, bytes);
        var profile = new TierProfile
        {
            RootPath = Path.GetFullPath(rootPath),
            ExePath = Path.GetFullPath(exePath),
            ExeSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
        };

        if (TryReadMarker(pe, profile)) return profile;

        var candidates = new List<Candidate>();
        foreach (var section in pe.Sections.Where(s => s.IsExecutable))
        {
            int start = pe.VaToOffset(section.VirtualAddress);
            int length = checked((int)Math.Min(section.RawSize, (uint)(pe.Bytes.Length - start)));
            for (int i = 0; i + TierPrologue.Length <= length; i++)
            {
                if (!Matches(pe.Bytes, start + i, TierPrologue)) continue;
                uint va = section.VirtualAddress + (uint)i;
                var body = pe.ReadBytes(va, 220);
                bool direct = IndexOf(body, DirectLoad) >= 0;
                uint? table = FindTableRef(pe, va, body);
                var mask = FindMask(pe, va, body);
                bool correction = FindCallTarget(body, va, CorrectionDefault);
                if (direct || table != null || mask.Sequence.Length > 0 || correction)
                    candidates.Add(new Candidate(va, direct, table, mask, correction));
            }
        }

        var chosen = candidates.FirstOrDefault(c => c.TableVa != null)
                     ?? candidates.FirstOrDefault(c => c.Mask.Sequence.Length > 0)
                     ?? candidates.FirstOrDefault(c => c.Correction)
                     ?? candidates.FirstOrDefault(c => c.Direct);
        if (chosen == null)
        {
            profile.Kind = TierPathKind.Unknown;
            profile.ReadOnlyReason = "未找到档位函数签名。";
            return profile;
        }

        profile.TierFuncVa = chosen.Va;
        profile.OriginalTierFuncVa = chosen.Va;
        var bodyBytes = pe.ReadBytes(chosen.Va, 220);
        profile.CallSites.AddRange(FindCallers(pe, chosen.Va));

        if (chosen.TableVa is uint tableVa)
        {
            profile.Kind = chosen.Correction ? TierPathKind.TableCorrection : TierPathKind.Table;
            profile.DisplayTableVa = tableVa;
            var tableBytes = pe.ReadBytes(tableVa, 16);
            foreach (var value in tableBytes)
            {
                if (value == 0xFF) break;
                profile.Thresholds.Add(value);
            }
            profile.TierBase = 0;
        }
        else if (chosen.Mask.Sequence.Length > 0)
        {
            profile.Kind = TierPathKind.Mask;
            profile.MaskTarget = chosen.Mask.Target;
            profile.InlineMask = chosen.Mask.Inline;
            profile.MaskSequence = chosen.Mask.Sequence;
            ParseDirect(pe, chosen.Va, bodyBytes, profile);
        }
        else
        {
            profile.Kind = chosen.Correction ? TierPathKind.Correction : TierPathKind.Direct;
            ParseDirect(pe, chosen.Va, bodyBytes, profile);
        }

        if (chosen.Correction)
        {
            profile.CorrectionTarget = FindCorrectionTarget(bodyBytes, chosen.Va) ?? CorrectionDefault;
            profile.CorrectionHelper = FindCorrectionHelper(bodyBytes, chosen.Va, profile.CorrectionTarget.Value);
        }

        profile.UnsignedThresholds = chosen.TableVa != null || DetectUnsignedThresholds(bodyBytes);

        var display = FindDisplay(pe);
        if (display != null)
        {
            profile.DisplayPointerVa = display.PointerVa;
            profile.DisplayTableVa = display.TableVa;
            profile.OriginalTableVa = display.TableVa;
            profile.DisplayIndexOffset = display.IndexOffset;
            profile.Labels.AddRange(display.Labels);
        }

        if (profile.Labels.Count < profile.Thresholds.Count + 1)
        {
            while (profile.Labels.Count < profile.Thresholds.Count + 1)
                profile.Labels.Add("?");
        }
        else if (profile.Labels.Count > profile.Thresholds.Count + 1)
        {
            profile.Labels.RemoveRange(profile.Thresholds.Count + 1,
                profile.Labels.Count - profile.Thresholds.Count - 1);
        }

        profile.RebuildEntries();

        var cave = FindCave(pe);
        if (cave is { } found)
        {
            profile.CaveVa = found.Va;
            profile.CaveLength = found.Length;
        }

        if (profile.Thresholds.Count == 0)
            profile.ReadOnlyReason = "未解析到阈值。";
        else if (profile.CallSites.Count == 0)
            profile.ReadOnlyReason = "未找到档位函数调用点。";
        else if (profile.DisplayPointerVa == 0)
            profile.ReadOnlyReason = "未找到档位显示表指针。";
        else if (profile.CaveLength < 900)
            profile.ReadOnlyReason = "没有足够大的安全代码洞。";
        else if (!IsContiguous(profile))
            profile.ReadOnlyReason = "档位返回值不是连续编号，暂不支持写入。";

        return profile;
    }

    private static void ParseDirect(PeFile pe, uint funcVa, byte[] body, TierProfile profile)
    {
        int load = IndexOf(body, DirectLoad);
        if (load < 0) return;
        int i = load + DirectLoad.Length;
        var returns = new List<byte>();
        while (i + 8 <= body.Length && body[i] == 0x3C)
        {
            if (body[i + 2] is not (0x7C or 0x72) || body[i + 3] != 0x04 || body[i + 4] != 0xB0)
                break;
            profile.Thresholds.Add(body[i + 1]);
            returns.Add(body[i + 5]);
            i += 8;
        }
        byte fallback = 1;
        if (i + 2 <= body.Length && body[i] == 0xB0)
            fallback = body[i + 1];
        profile.Thresholds.Reverse();
        returns.Reverse();
        profile.TierBase = returns.Count > 0 ? Math.Min(returns.Min(), fallback) : fallback;
    }

    private static bool IsContiguous(TierProfile profile)
    {
        if (profile.Kind is TierPathKind.Table or TierPathKind.TableCorrection)
            return profile.TierBase == 0;
        for (int i = 0; i <= profile.Thresholds.Count; i++)
            if (profile.TierBase + i > 255) return false;
        return true;
    }

    private static uint? FindTableRef(PeFile pe, uint funcVa, byte[] body)
    {
        for (int i = 0; i + 6 <= body.Length; i++)
        {
            if (body[i] is not (0x3A or 0x3B)) continue;
            if (body[i + 1] is not (0x90 or 0x88 or 0x80)) continue;
            uint table = BitConverter.ToUInt32(body, i + 2);
            if (pe.TryVaToOffset(table, out _) && pe.ReadBytes(table, 1) is [0 or 1])
                return table;
        }
        return null;
    }

    private static MaskInfo FindMask(PeFile pe, uint funcVa, byte[] body)
    {
        // Standard: push eax; call chain-function containing C6..CE.
        for (int i = 0; i + 6 <= body.Length; i++)
        {
            if (body[i] != 0x50 || body[i + 1] != 0xE8) continue;
            uint callVa = funcVa + (uint)i + 1;
            uint target = callVa + 5 + unchecked((uint)BitConverter.ToInt32(body, i + 2));
            if (!pe.TryVaToOffset(target, out _)) continue;
            var targetBody = pe.ReadBytes(target, 260);
            int hits = 0;
            for (int id = 0xC6; id <= 0xCE; id++)
                if (IndexOf(targetBody, [0x68, (byte)id, 0, 0, 0]) >= 0) hits++;
            if (hits == 9)
                return new MaskInfo(target, false, body.AsSpan(i, 6).ToArray());
        }
        // Inline: push eax; push 0/1/0/0; push id; call 0x42518F.
        for (int i = 0; i + 12 <= body.Length; i++)
        {
            if (body[i] != 0x50 || body[i + 1] != 0x6A) continue;
            int p = i;
            int pushes = 0;
            while (p + 5 <= body.Length && body[p] == 0x6A)
            {
                pushes++;
                p += 2;
            }
            if (pushes < 3 || p + 5 > body.Length || body[p] != 0x68) continue;
            int id = BitConverter.ToInt32(body, p + 1);
            if (id is < 0 or > 0xFF) continue;
            int call = p + 5;
            if (body[call] != 0xE8) continue;
            uint callVa = funcVa + (uint)call;
            uint target = callVa + 5 + unchecked((uint)BitConverter.ToInt32(body, call + 1));
            if (target != 0x0042518F) continue;
            return new MaskInfo(target, true, body.AsSpan(i, call + 5 - i).ToArray());
        }
        return new MaskInfo(0, false, []);
    }

    private static uint? FindCorrectionTarget(byte[] body, uint funcVa)
        => FindCallTarget(body, funcVa, CorrectionDefault) ? CorrectionDefault : null;

    private static uint? FindCorrectionHelper(byte[] body, uint funcVa, uint correctionTarget)
    {
        for (int i = 10; i + 5 <= body.Length; i++)
        {
            if (!IsCallTo(body, funcVa, i, correctionTarget)) continue;
            int p = i - 10;
            if (body[p] != 0x51 || body[p + 1] != 0xE8) continue;
            if (body[p + 6] != 0x59 || body[p + 7] != 0x8B || body[p + 8] != 0x55 || body[p + 9] != 0x08)
                continue;
            uint callVa = funcVa + (uint)(p + 1);
            return callVa + 5 + unchecked((uint)BitConverter.ToInt32(body, p + 2));
        }
        return null;
    }

    private static bool IsCallTo(byte[] body, uint funcVa, int index, uint wanted)
    {
        if (index < 0 || index + 5 > body.Length || body[index] != 0xE8) return false;
        uint callVa = funcVa + (uint)index;
        return callVa + 5 + unchecked((uint)BitConverter.ToInt32(body, index + 1)) == wanted;
    }

    private static bool DetectUnsignedThresholds(byte[] body)
    {
        int load = IndexOf(body, DirectLoad);
        if (load < 0) return false;
        int i = load + DirectLoad.Length;
        return i + 3 <= body.Length && body[i] == 0x3C && body[i + 2] == 0x72;
    }

    private static bool FindCallTarget(byte[] body, uint funcVa, uint wanted)
    {
        for (int i = 0; i + 5 <= body.Length; i++)
        {
            if (body[i] != 0xE8) continue;
            uint callVa = funcVa + (uint)i;
            int rel = BitConverter.ToInt32(body, i + 1);
            if (callVa + 5 + unchecked((uint)rel) == wanted) return true;
        }
        return false;
    }

    private static List<uint> FindCallers(PeFile pe, uint target)
    {
        var result = new List<uint>();
        foreach (var section in pe.Sections.Where(s => s.IsExecutable))
        {
            int start = pe.VaToOffset(section.VirtualAddress);
            int length = checked((int)Math.Min(section.RawSize, (uint)(pe.Bytes.Length - start)));
            for (int i = 0; i + 5 <= length; i++)
            {
                if (pe.Bytes[start + i] != 0xE8) continue;
                uint callVa = section.VirtualAddress + (uint)i;
                int rel = BitConverter.ToInt32(pe.Bytes, start + i + 1);
                if (callVa + 5 + unchecked((uint)rel) == target)
                    result.Add(callVa);
            }
        }
        return result;
    }

    private static DisplayInfo? FindDisplay(PeFile pe)
    {
        DisplayInfo? best = null;
        int bestScore = 0;
        foreach (var section in pe.Sections.Where(s => s.IsExecutable))
        {
            int start = pe.VaToOffset(section.VirtualAddress);
            int length = checked((int)Math.Min(section.RawSize, (uint)(pe.Bytes.Length - start)));
            for (int i = 0; i + 5 <= length; i++)
            {
                if (pe.Bytes[start + i] != 0xBA) continue;
                uint tableVa = BitConverter.ToUInt32(pe.Bytes, start + i + 1);
                if (!pe.TryVaToOffset(tableVa, out _)) continue;
                var labels = new List<string>();
                int indexOffset = 0;
                for (int skip = 0; skip <= 2; skip++)
                {
                    var probe = new List<string>();
                    for (int index = skip; index < 12; index++)
                    {
                        uint pointerVa = tableVa + (uint)(index * 4);
                        if (!pe.TryVaToOffset(pointerVa, out int pointerOffset)) break;
                        uint labelVa = BitConverter.ToUInt32(pe.Bytes, pointerOffset);
                        if (!pe.TryVaToOffset(labelVa, out int labelOffset)) break;
                        string label = ReadLabel(pe.Bytes, labelOffset);
                        if (label.Length == 0 || label.Length > 12) break;
                        probe.Add(label);
                    }
                    if (probe.Count > labels.Count)
                    {
                        labels = probe;
                        indexOffset = skip;
                    }
                }
                if (labels.Count < 2) continue;
                bool hasKnown = labels.Any(l => l is "C" or "0" or "1" or "B" or "A");
                int score = labels.Count * 10 + (hasKnown ? 20 : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new DisplayInfo(section.VirtualAddress + (uint)i, tableVa, indexOffset, labels);
                }
            }
        }
        return best;
    }

    private static string ReadLabel(byte[] bytes, int offset)
    {
        int end = offset;
        int max = Math.Min(bytes.Length, offset + 12);
        while (end < max && bytes[end] != 0) end++;
        try
        {
            return Encoding.GetEncoding("GBK").GetString(bytes, offset, end - offset);
        }
        catch
        {
            return Encoding.ASCII.GetString(bytes, offset, end - offset);
        }
    }

    private static Cave? FindCave(PeFile pe)
    {
        var targets = new List<uint>();
        foreach (var section in pe.Sections.Where(s => s.IsExecutable))
        {
            int start = pe.VaToOffset(section.VirtualAddress);
            int length = checked((int)Math.Min(section.RawSize, (uint)(pe.Bytes.Length - start)));
            for (int i = 0; i < length; i++)
            {
                byte op = pe.Bytes[start + i];
                uint va = section.VirtualAddress + (uint)i;
                if (op == 0xE8 || op == 0xE9)
                {
                    if (i + 5 > length) continue;
                    targets.Add(va + 5 + unchecked((uint)BitConverter.ToInt32(pe.Bytes, start + i + 1)));
                }
                else if (op is 0xEB or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x76 or 0x77 or
                         0x78 or 0x79 or 0x7A or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F)
                {
                    if (i + 2 > length) continue;
                    targets.Add(va + 2 + unchecked((uint)(sbyte)pe.Bytes[start + i + 1]));
                }
            }
        }
        targets.Sort();
        var targetValues = targets.ToArray();
        var absoluteValues = CollectAbsoluteReferences(pe);

        Cave? best = null;
        foreach (var section in pe.Sections.Where(s => s.IsExecutable))
        {
            int start = pe.VaToOffset(section.VirtualAddress);
            int length = checked((int)Math.Min(section.RawSize, (uint)(pe.Bytes.Length - start)));
            int runStart = -1;
            byte fill = 0;
            for (int i = 0; i <= length; i++)
            {
                byte value = i < length ? pe.Bytes[start + i] : (byte)0;
                bool isFill = i < length && (value == 0x90 || value == 0xCC || value == 0x00) &&
                              (runStart < 0 || value == fill);
                if (isFill)
                {
                    if (runStart < 0) { runStart = i; fill = value; }
                    continue;
                }
                if (runStart >= 0)
                {
                    int runLength = i - runStart;
                    uint va = section.VirtualAddress + (uint)runStart;
                    bool referenced = HasValueInRange(targetValues, va, runLength);
                    bool absolute = HasValueInRange(absoluteValues, va, runLength);
                    if (!referenced && !absolute && runLength >= 900 && (best == null || runLength > best.Length))
                        best = new Cave(va, runLength);
                }
                runStart = -1;
            }
        }
        return best;
    }

    private static uint[] CollectAbsoluteReferences(PeFile pe)
    {
        var ranges = pe.Sections
            .Where(s => s.IsExecutable)
            .Select(s => (Start: s.VirtualAddress, End: s.VirtualAddress + s.RawSize))
            .ToArray();
        var values = new List<uint>();
        for (int i = 0; i + 4 <= pe.Bytes.Length; i++)
        {
            uint value = BitConverter.ToUInt32(pe.Bytes, i);
            foreach (var (start, end) in ranges)
                if (value >= start && value < end) { values.Add(value); break; }
        }
        values.Sort();
        return values.ToArray();
    }

    private static bool HasValueInRange(uint[] sorted, uint start, int length)
    {
        if (sorted.Length == 0 || length <= 0) return false;
        uint end = start + (uint)length;
        int index = Array.BinarySearch(sorted, start);
        if (index < 0) index = ~index;
        return index < sorted.Length && sorted[index] < end;
    }

    private static bool TryReadMarker(PeFile pe, TierProfile profile)
    {
        foreach (var section in pe.Sections.Where(s => s.IsExecutable))
        {
            int start = pe.VaToOffset(section.VirtualAddress);
            int length = checked((int)Math.Min(section.RawSize, (uint)(pe.Bytes.Length - start)));
            int hit = IndexOf(pe.Bytes.AsSpan(start, length).ToArray(), "TIER1"u8.ToArray());
            if (hit < 0) continue;
            uint markerVa = section.VirtualAddress + (uint)hit;
            var reader = new MarkerReader(pe, markerVa);
            if (!reader.TryRead(out var marker)) continue;
            profile.HasMarker = true;
            profile.MarkerVa = markerVa;
            profile.Kind = (TierPathKind)marker.Kind;
            profile.TierBase = marker.TierBase;
            profile.TierFuncVa = marker.CodeVa;
            profile.OriginalTierFuncVa = marker.OriginalTierFuncVa;
            profile.OriginalTableVa = marker.OriginalTableVa;
            profile.DisplayPointerVa = marker.DisplayPointerVa;
            profile.DisplayTableVa = marker.TableVa;
            profile.DisplayIndexOffset = marker.DisplayIndexOffset;
            profile.MaskTarget = marker.MaskTarget == 0 ? null : marker.MaskTarget;
            profile.InlineMask = marker.InlineMask;
            profile.MaskSequence = marker.MaskSequence;
            profile.CorrectionTarget = marker.CorrectionTarget == 0 ? null : marker.CorrectionTarget;
            profile.CallSites.AddRange(marker.CallSites);
            profile.Thresholds.AddRange(marker.Thresholds);
            profile.Labels.AddRange(marker.Labels);
            profile.CaveVa = marker.CaveVa;
            profile.CaveLength = checked((int)marker.CaveLength);
            if (profile.OriginalTierFuncVa != 0)
            {
                var originalBody = pe.ReadBytes(profile.OriginalTierFuncVa, 220);
                profile.UnsignedThresholds =
                    profile.Kind is TierPathKind.Table or TierPathKind.TableCorrection ||
                    DetectUnsignedThresholds(originalBody);
                if (profile.CorrectionTarget is { } correction)
                    profile.CorrectionHelper = FindCorrectionHelper(originalBody, profile.OriginalTierFuncVa, correction);
            }
            profile.RebuildEntries();
            if (profile.CaveLength < 900)
                profile.ReadOnlyReason = "已写入档位补丁，但找不到足够大的代码洞用于再次编辑。";
            return true;
        }
        return false;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle);

    private static bool Matches(byte[] bytes, int offset, byte[] pattern)
    {
        if (offset < 0 || offset + pattern.Length > bytes.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (bytes[offset + i] != pattern[i]) return false;
        return true;
    }

    private sealed record Candidate(uint Va, bool Direct, uint? TableVa, MaskInfo Mask, bool Correction);
    private sealed record MaskInfo(uint Target, bool Inline, byte[] Sequence);
    private sealed record DisplayInfo(uint PointerVa, uint TableVa, int IndexOffset, List<string> Labels);
    private sealed record Cave(uint Va, int Length);
}
