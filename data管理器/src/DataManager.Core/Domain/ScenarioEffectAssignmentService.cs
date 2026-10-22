using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scenarios;

namespace DataManager.Core.Domain;

public sealed record ScenarioEffectAssignment
{
    public required string Key { get; init; }
    public required string RelativePath { get; init; }
    public required string SourceHash { get; init; }
    public int SceneIndex { get; init; }
    public int SectionIndex { get; init; }
    public int CommandIndex { get; init; }
    public int CommandOrdinal { get; init; }
    public int FileOffset { get; init; }
    public int SubFunction { get; init; }
    public required string Kind { get; init; }
    public string Remarks { get; init; } = "";
    public string Diagnostic { get; init; } = "";
    public bool LayoutVerified { get; init; }
    public int CharacterSlots { get; init; }
    public bool IsWritable => Tokens.Count > 0;
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public string SourceDisplay => $"{RelativePath} / Scene {SceneIndex} / Section {SectionIndex} / Command {CommandIndex}";
    internal IReadOnlyDictionary<string, ScenarioParameterToken> Tokens { get; init; } = new Dictionary<string, ScenarioParameterToken>();
}

public sealed record ScenarioEffectUpdate(string Key, IReadOnlyDictionary<string, string> Parameters);
internal sealed record ScenarioParameterToken(int Offset, byte[] OriginalBytes, string Prefix = "", string Suffix = "");

/// <summary>Workspace-scoped, undoable RS drafts. Scanning and candidate construction never write game files.</summary>
public sealed class ScenarioEffectAssignmentService : IWorkspaceDraftParticipant
{
    private static readonly ConditionalWeakTable<ProjectWorkspace, ScenarioEffectAssignmentService> Instances = new();
    private readonly ProjectWorkspace _workspace;
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourceHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScenarioEffectAssignment> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _draft = new(StringComparer.Ordinal);
    private readonly List<string> _diagnostics = [];
    private string _identity = "";
    private bool _scanned;
    private int _characterSlots;
    private bool _layoutVerified;
    private long _scanGeneration;

    private ScenarioEffectAssignmentService(ProjectWorkspace workspace) => _workspace = workspace;
    public static ScenarioEffectAssignmentService For(ProjectWorkspace workspace)
    {
        var service = Instances.GetValue(workspace, w => new(w));
        workspace.RegisterDraftParticipant(service);
        service.CheckIdentity();
        return service;
    }
    public bool HasPendingChanges { get { lock (_gate) return _draft.Count > 0; } }
    public bool HasScanResult { get { CheckIdentity(); lock (_gate) return _scanned; } }
    public IReadOnlyList<string> Diagnostics { get { lock (_gate) return _diagnostics.ToArray(); } }
    public event EventHandler? Changed;

    private void CheckIdentity()
    {
        var source = _workspace.Documents.GetValueOrDefault("persons");
        var identity = $"{_workspace.RootPath}|{_workspace.ActiveProfile.ProfileId}|{_workspace.ActiveProfile.VariantId}|{(source == null ? 0 : RuntimeHelpers.GetHashCode(source))}";
        lock (_gate)
        {
            if (_identity == identity) return;
            _identity = identity; _files.Clear(); _sourceHashes.Clear(); _entries.Clear(); _draft.Clear(); _diagnostics.Clear(); _scanned = false; _scanGeneration++;
        }
    }

    public IReadOnlyList<ScenarioEffectAssignment> Read(bool ensureScanned = true)
    {
        CheckIdentity();
        if (ensureScanned && !_scanned) return Scan();
        lock (_gate) return _entries.Values.Select(WithDraft).OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.CommandOrdinal).ToArray();
    }

    public IReadOnlyList<ScenarioEffectAssignment> Scan(CancellationToken cancellationToken = default)
    {
        CheckIdentity();
        string identity; string root; long generation;
        lock (_gate) { identity = _identity; root = _workspace.RootPath; generation = _scanGeneration; }
        var rs = Path.Combine(root, "RS");
        var executablePath = ProjectFileLocator.Resolve(root, "Ekd5.exe");
        var executable = File.Exists(executablePath) ? File.ReadAllBytes(executablePath) : [];
        var (characterSlots, verified) = InspectEffectSlots(executable);
        Dictionary<string, string> cachedHashes;
        Dictionary<string, ScenarioEffectAssignment[]> cachedEntries;
        lock (_gate)
        {
            var reusable = _scanned && _identity == identity && _scanGeneration == generation &&
                _characterSlots == characterSlots && _layoutVerified == verified;
            cachedHashes = reusable ? new(_sourceHashes, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            cachedEntries = reusable ? _entries.Values.GroupBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, ScenarioEffectAssignment>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        foreach (var path in Directory.Exists(rs) ? Directory.EnumerateFiles(rs, "*.eex", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path); var bytes = File.ReadAllBytes(path);
            try
            {
                var hash = HashService.Sha256(bytes);
                // Reuse the structural index only after reading and hashing
                // the entire current file and checking the engine semantics.
                // Directory enumeration still detects additions/deletions.
                var parsed = cachedHashes.GetValueOrDefault(relative) == hash
                    ? cachedEntries.GetValueOrDefault(relative) ?? []
                    : ParseAssignmentsCore(relative, bytes, hash, characterSlots, verified, cancellationToken);
                files[relative] = bytes; sourceHashes[relative] = hash;
                foreach (var entry in parsed) entries.Add(entry.Key, entry);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
            { diagnostics.Add($"{relative}：{ex.Message}"); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_identity != identity || _workspace.RootPath != root || _scanGeneration != generation) return [];
            // Never replace the baseline of an unsaved draft with externally changed bytes.
            foreach (var old in _draft.Keys)
            {
                if (!_entries.TryGetValue(old, out var entry)) continue;
                if (sourceHashes.GetValueOrDefault(entry.RelativePath) != entry.SourceHash)
                    throw new InvalidDataException($"{entry.RelativePath} 已在外部改变；当前剧本草稿已保留。");
            }
            _files.Clear(); foreach (var file in files) _files.Add(file.Key, file.Value);
            _sourceHashes.Clear(); foreach (var hash in sourceHashes) _sourceHashes.Add(hash.Key, hash.Value);
            _entries.Clear(); foreach (var entry in entries) _entries.Add(entry.Key, entry.Value);
            _diagnostics.Clear(); _diagnostics.AddRange(diagnostics); _scanned = true;
            _characterSlots = characterSlots; _layoutVerified = verified;
            return _entries.Values.Select(WithDraft).OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.CommandOrdinal).ToArray();
        }
    }

    public void ValidateUpdates(IEnumerable<ScenarioEffectUpdate> updates)
    {
        _ = Read();
        lock (_gate)
        {
            var proposals = Merge(updates);
            _ = Prepare(proposals);
        }
    }

    public void ApplyUpdate(ScenarioEffectUpdate update) => ApplyUpdates([update]);
    public void ApplyUpdates(IEnumerable<ScenarioEffectUpdate> updates)
    {
        _ = Read();
        lock (_gate)
        {
            var before = Clone(_draft); var after = Merge(updates);
            _ = Prepare(after);
            if (Equivalent(before, after)) return;
            SetDraft(after);
            _workspace.Commands.Push(new DelegateEditCommand("修改剧本特效分配", () => Restore(before), () => Restore(after)));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, Dictionary<string, string>> Merge(IEnumerable<ScenarioEffectUpdate> updates)
    {
        var result = Clone(_draft); var seen = new HashSet<string>();
        foreach (var update in updates)
        {
            if (!seen.Add(update.Key)) throw new InvalidDataException("重复的剧本分配记录：" + update.Key);
            if (!_entries.TryGetValue(update.Key, out var entry)) throw new InvalidDataException("剧本来源记录不存在，请重新扫描：" + update.Key);
            if (!entry.IsWritable) throw new InvalidDataException($"{entry.SourceDisplay} 的参数不完整，仅可读取，不能写回。");
            var values = result.GetValueOrDefault(update.Key) ?? new Dictionary<string, string>(entry.Parameters, StringComparer.Ordinal);
            foreach (var pair in update.Parameters)
            {
                if (!entry.Tokens.ContainsKey(pair.Key)) throw new InvalidDataException($"{entry.SourceDisplay} 不存在可写参数 {pair.Key}。");
                var value = pair.Value ?? "";
                if (value != Baseline(entry, pair.Key)) ValidateExpression(entry, pair.Key, value);
                values[pair.Key] = value;
            }
            if (values.All(pair => Baseline(entry, pair.Key) == pair.Value)) result.Remove(update.Key);
            else result[update.Key] = values;
        }
        return result;
    }

    private void ValidateExpression(ScenarioEffectAssignment entry, string field, string value)
    {
        if (!Regex.IsMatch(value, @"^-?\*?\d+$", RegexOptions.CultureInvariant) ||
            !long.TryParse(value.Replace("*", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number is < int.MinValue or > int.MaxValue)
            throw new InvalidDataException($"{entry.SourceDisplay} 的 {field} 必须为 32 位整数或 *变量号表达式。");
        if (value.Contains('*') || number < 0) return;
        if (field == "EffectId" && number > 254) throw new InvalidDataException("剧本特效编号应在 0..254 内。");
        if (field == "Slot" && entry.SubFunction == 5 && entry.LayoutVerified && number >= entry.CharacterSlots + (entry.CharacterSlots == 4 ? 2 : 1))
            throw new InvalidDataException("特效槽位超出当前引擎的真实槽位范围。");
        if (field == "Slot" && entry.SubFunction == 10 && number > 1) throw new InvalidDataException("专属/套装位置应为 0 或 1。");
        if (field is "WeaponId" or "ArmorId" or "AccessoryId" or "ItemId")
        {
            if (number > 255) throw new InvalidDataException("剧本物品编号超过 255。");
            if (number == 255 || field == "ItemId") return;
            var boundary = ItemCategoryBoundary.Read(_workspace.RootPath);
            var expected = field == "WeaponId" ? ItemCategory.Weapon : field == "ArmorId" ? ItemCategory.Armor : ItemCategory.Accessory;
            var doc = _workspace.Documents.Values.FirstOrDefault(d => d.Definition.Key is "items-low" or "items-high" && number >= d.Definition.BeginId && number < d.Definition.BeginId + d.Data.Rows.Count);
            if (doc != null && ItemTypeService.Resolve(doc, (int)number - doc.Definition.BeginId, boundary).Category != expected)
                throw new InvalidDataException("套装物品顺序必须为武器、防具、辅助。");
        }
    }

    public IReadOnlyList<PreparedFileEdit> PrepareFileEdits()
    {
        CheckIdentity(); lock (_gate) return Prepare(_draft);
    }
    private IReadOnlyList<PreparedFileEdit> Prepare(Dictionary<string, Dictionary<string, string>> draft)
    {
        var result = new List<PreparedFileEdit>();
        foreach (var group in draft.GroupBy(pair => _entries[pair.Key].RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var original = _files[group.Key]; var expectedHash = _sourceHashes[group.Key];
            var path = SafePath(group.Key);
            if (!File.Exists(path) || HashService.Sha256(path) != expectedHash)
                throw new InvalidDataException($"剧本 {group.Key} 已被外部修改，草稿未覆盖原文件。");
            var patches = group.SelectMany(pair => pair.Value.Where(value => value.Value != Baseline(_entries[pair.Key], value.Key)).Select(value =>
            {
                var token = _entries[pair.Key].Tokens[value.Key];
                return new ScenarioTokenReplacement(token.Offset, token.OriginalBytes, Encoding.ASCII.GetBytes(token.Prefix + value.Value + token.Suffix), token.OriginalBytes.Length == 0);
            })).ToArray();
            var candidate = new ScenarioParameterPatchBuilder().Build(original, patches);
            var candidateHash = HashService.Sha256(candidate);
            result.Add(new(group.Key, expectedHash, candidate, "人物/套装特效", $"修改 {group.Count()} 条剧本分配", actual =>
            {
                if (HashService.Sha256(actual) != candidateHash) throw new InvalidDataException("剧本写回字节与局部补丁候选不一致。");
                _ = EexStructure.Parse(actual);
            }));
        }
        return result;
    }

    private string SafePath(string relative)
    {
        var root = Path.GetFullPath(_workspace.RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("剧本路径超出当前项目。");
        return full;
    }
    public void AcceptSaved()
    {
        lock (_gate) { _draft.Clear(); _scanned = false; _files.Clear(); _sourceHashes.Clear(); _entries.Clear(); _scanGeneration++; }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Discard()
    {
        lock (_gate) { _draft.Clear(); _scanned = false; _files.Clear(); _sourceHashes.Clear(); _entries.Clear(); _scanGeneration++; }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public Action CaptureSaveRecovery()
    {
        lock (_gate)
        {
            var identity = _identity; var scanned = _scanned;
            // Baseline arrays are privately owned and never mutated; patch
            // construction allocates its candidate. Retaining these immutable
            // snapshots keeps recovery complete without copying every RS file.
            var files = new Dictionary<string, byte[]>(_files, StringComparer.OrdinalIgnoreCase);
            var sourceHashes = new Dictionary<string, string>(_sourceHashes, StringComparer.OrdinalIgnoreCase);
            var characterSlots = _characterSlots; var layoutVerified = _layoutVerified;
            var entries = _entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var draft = Clone(_draft); var diagnostics = _diagnostics.ToArray();
            return () =>
            {
                lock (_gate)
                {
                    _identity = identity; _scanned = scanned; _scanGeneration++;
                    _files.Clear(); foreach (var file in files) _files.Add(file.Key, file.Value);
                    _sourceHashes.Clear(); foreach (var hash in sourceHashes) _sourceHashes.Add(hash.Key, hash.Value);
                    _characterSlots = characterSlots; _layoutVerified = layoutVerified;
                    _entries.Clear(); foreach (var entry in entries) _entries.Add(entry.Key, entry.Value);
                    SetDraft(draft); _diagnostics.Clear(); _diagnostics.AddRange(diagnostics);
                }
                foreach (var handler in Changed?.GetInvocationList() ?? [])
                    try { ((EventHandler)handler)(this, EventArgs.Empty); } catch { /* Recovery must retain the draft even if a view is disposed. */ }
            };
        }
    }
    private ScenarioEffectAssignment WithDraft(ScenarioEffectAssignment entry) => _draft.TryGetValue(entry.Key, out var values)
        ? entry with { Parameters = new Dictionary<string, string>(values, StringComparer.Ordinal) } : entry;
    private static string Baseline(ScenarioEffectAssignment entry, string field) => entry.Parameters.GetValueOrDefault(field, field == "Value" ? "0" : "");
    private void Restore(Dictionary<string, Dictionary<string, string>> values)
    { lock (_gate) SetDraft(values); Changed?.Invoke(this, EventArgs.Empty); }
    private void SetDraft(Dictionary<string, Dictionary<string, string>> values)
    { _draft.Clear(); foreach (var pair in values) _draft.Add(pair.Key, new(pair.Value, StringComparer.Ordinal)); }
    private static Dictionary<string, Dictionary<string, string>> Clone(Dictionary<string, Dictionary<string, string>> values)
        => values.ToDictionary(pair => pair.Key, pair => new Dictionary<string, string>(pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
    private static bool Equivalent(Dictionary<string, Dictionary<string, string>> a, Dictionary<string, Dictionary<string, string>> b)
        => a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && pair.Value.Count == value.Count && pair.Value.All(p => value.GetValueOrDefault(p.Key) == p.Value));

    private static (int CharacterSlots, bool Verified) InspectEffectSlots(byte[] bytes)
    {
        var ranges = ExecutableRanges(bytes);
        bool UniqueConsumer(string prefixHex, int writerDistance, string writerHex)
        {
            var prefix = Convert.FromHexString(prefixHex); var writer = Convert.FromHexString(writerHex); var matches = 0;
            foreach (var (start, length) in ranges)
                for (var offset = start; offset <= start + length - Math.Max(prefix.Length, writerDistance + writer.Length); offset++)
                    if (bytes.AsSpan(offset, prefix.Length).SequenceEqual(prefix) &&
                        bytes.AsSpan(offset + writerDistance, writer.Length).SequenceEqual(writer)) matches++;
            return matches == 1;
        }
        var modern = UniqueConsumer("558BEC83EC0C546A026A04FF7508", 39, "3C0377056BD003EB103C05772383E8046BD00283C20C");
        var legacy = UniqueConsumer("0FB645F83CFE77246BC808030D", 17, "8A45F93C0377146BD00203CA668B55FA");
        return modern && !legacy ? (4, true) : legacy && !modern ? (3, true) : (0, false);
    }

    private static IReadOnlyList<(int Start, int Length)> ExecutableRanges(byte[] bytes)
    {
        try
        {
            if (bytes.Length < 64 || bytes[0] != 'M' || bytes[1] != 'Z') return [];
            var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C, 4));
            if (pe < 0 || pe > bytes.Length - 24 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pe, 4)) != 0x4550) return [];
            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 6, 2));
            var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20, 2));
            var optional = pe + 24;
            if (optionalSize < 32 || optional > bytes.Length - optionalSize || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional, 2)) != 0x10B) return [];
            var result = new List<(int, int)>();
            for (var index = 0; index < count; index++)
            {
                var at = checked(optional + optionalSize + index * 40);
                if (at > bytes.Length - 40) return [];
                var raw = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 20, 4));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 16, 4));
                if ((ulong)raw + size > (ulong)bytes.Length) return [];
                if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 36, 4)) & 0x20000000) != 0)
                    result.Add((checked((int)raw), checked((int)size)));
            }
            if (result.Any(a => result.Any(b => a != b && a.Item1 < b.Item1 + (long)b.Item2 && b.Item1 < a.Item1 + (long)a.Item2))) return [];
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException) { return []; }
    }

    public static IReadOnlyList<ScenarioEffectAssignment> ParseAssignments(string relativePath, byte[] bytes, int characterSlots,
        bool layoutVerified = true, CancellationToken cancellationToken = default)
        => ParseAssignmentsCore(relativePath, bytes, HashService.Sha256(bytes), characterSlots, layoutVerified, cancellationToken);

    private static IReadOnlyList<ScenarioEffectAssignment> ParseAssignmentsCore(string relativePath, byte[] bytes, string hash,
        int characterSlots, bool layoutVerified, CancellationToken cancellationToken)
    {
        var structure = EexStructure.Parse(bytes, cancellationToken);
        var result = new List<ScenarioEffectAssignment>();
        foreach (var command in structure.Commands.Where(c => c.Id == 0x72))
        {
            var sub = command.Parameters.FirstOrDefault(p => p.Tag == 4)?.Number;
            var text = command.Parameters.FirstOrDefault(p => p.Tag == 5);
            if (sub is not (5 or 10) || text == null) continue;
            var tokens = new Dictionary<string, ScenarioParameterToken>(StringComparer.Ordinal);
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            var lines = ByteLines(bytes, text.Offset, text.Length - 1); var remarksStart = text.Offset + text.Length - 1;
            var kind = sub == 5 ? "人物/兵种" : "专属/套装"; var diagnostic = "";
            bool Add(string name, int start, int length)
            {
                while (length > 0 && bytes[start] is 32 or 9) { start++; length--; }
                while (length > 0 && bytes[start + length - 1] is 32 or 9) length--;
                var raw = bytes.AsSpan(start, length).ToArray();
                if (!ScenarioParameterPatchBuilder.IsExpression(raw)) return false;
                tokens[name] = new(start, raw); parameters[name] = Encoding.ASCII.GetString(raw); return true;
            }
            if (sub == 5)
            {
                var names = new[] { "EffectId", "Slot", "TargetId", "Value" };
                var mandatory = 3;
                for (var i = 0; i < mandatory; i++)
                    if (i >= lines.Count || !Add(names[i], lines[i].Start, lines[i].Length)) { diagnostic = "个人/兵种参数前三行不完整。"; break; }
                var used = 3;
                if (diagnostic.Length == 0 && layoutVerified && characterSlots == 4 && lines.Count > 3 && Add("Value", lines[3].Start, lines[3].Length)) used = 4;
                if (lines.Count > used) remarksStart = lines[used].Start;
                if (diagnostic.Length == 0 && characterSlots == 4 && !parameters.ContainsKey("Value")) diagnostic = "特效值未显式提供，当前引擎读取为 0。";
                if (characterSlots == 4 && layoutVerified && parameters.Count >= 3 && !parameters.ContainsKey("Value"))
                {
                    var newline = lines.Count > 1 && bytes[lines[0].Start + lines[0].Length] == 13 ?
                        (lines[1].Start - lines[0].Start - lines[0].Length == 2 ? "\r\n" : "\r") : "\n";
                    var hasRemarks = lines.Count > 3;
                    tokens["Value"] = new(hasRemarks ? lines[3].Start : text.Offset + text.Length - 1, [], hasRemarks ? "" : newline, hasRemarks ? newline : "");
                }
                if (!layoutVerified) diagnostic = "未确认引擎槽位语义，仅显示原始参数。";
            }
            else
            {
                var first = lines.Count > 0 ? Encoding.ASCII.GetString(bytes, lines[0].Start, lines[0].Length).Trim() : "";
                kind = first == "0" ? "人物专属" : first == "1" ? "套装" : "专属/套装";
                if (first is not ("0" or "1") || lines.Count < 2) diagnostic = "专属/套装类型或数据行无效。";
                else
                {
                    var names = first == "0" ? new[] { "Slot", "EffectId", "TargetId", "ItemId", "Value" }
                        : new[] { "Slot", "EffectId", "WeaponId", "ArmorId", "AccessoryId", "Value" };
                    var line = lines[1]; var cursor = line.Start; var part = 0;
                    for (var i = line.Start; i <= line.Start + line.Length; i++)
                    {
                        if (i < line.Start + line.Length && bytes[i] != (byte)',') continue;
                        if (part >= names.Length || !Add(names[part], cursor, i - cursor)) { diagnostic = "专属/套装数字参数无效。"; break; }
                        part++; cursor = i + 1;
                    }
                    if (part != names.Length) diagnostic = "专属/套装参数数量不匹配。";
                    if (lines.Count > 2) remarksStart = lines[2].Start;
                }
            }
            if (sub == 10 && diagnostic.Length > 0 || parameters.Count < 3) tokens.Clear();
            if (sub == 5 && layoutVerified && int.TryParse(parameters.GetValueOrDefault("Slot"), out var slot) && slot >= 0)
                kind = slot < characterSlots ? "人物" : slot < characterSlots + (characterSlots == 4 ? 2 : 1) ? "兵种" : "人物/兵种";
            result.Add(new()
            {
                Key = $"{relativePath.Replace('\\', '/')}|{command.Scene}|{command.Section}|{command.Index}|{command.Ordinal}",
                RelativePath = relativePath, SourceHash = hash, SceneIndex = command.Scene, SectionIndex = command.Section,
                CommandIndex = command.Index, CommandOrdinal = command.Ordinal, FileOffset = command.Offset, SubFunction = sub.Value,
                Kind = kind, LayoutVerified = layoutVerified, CharacterSlots = characterSlots, Parameters = parameters, Tokens = tokens,
                Diagnostic = diagnostic, Remarks = EncodingService.DecodeFixedStringPreservingInvalidBytes(bytes.AsSpan(remarksStart, text.Offset + text.Length - 1 - remarksStart))
            });
        }
        return result;
    }
    private static List<(int Start, int Length)> ByteLines(byte[] bytes, int start, int length)
    {
        var result = new List<(int, int)>(); var line = start; var end = start + length;
        for (var i = start; i < end; i++)
        {
            if (bytes[i] is not (10 or 13)) continue;
            result.Add((line, i - line));
            if (bytes[i] == 13 && i + 1 < end && bytes[i + 1] == 10) i++;
            line = i + 1;
        }
        result.Add((line, end - line)); return result;
    }
}
