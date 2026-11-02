using DataManager.Core.Models;
using Iced.Intel;


namespace DataManager.Core.Scanner;

/// <summary>Direct E8 call graph used to show the consumer chain above an effect stub.</summary>
internal sealed class EngineEffectDirectCall
{
    public uint Address { get; init; }
    public uint Target { get; init; }
    public uint? CallerEntry { get; init; }
}

/// <summary>Shortest E8 distance from one root to any effect entry, or -1 when unreachable.</summary>
internal sealed record EngineEffectTransitiveReach(
    int Depth, uint ViaEntry, uint ViaTarget, int ExpandedFunctionCount, bool Capped,
    IReadOnlyList<uint> Path)
{
    public bool Found => Depth >= 0;
    public bool IsIndirect => Depth >= 2;
}

internal sealed class EngineEffectCallGraph
{
    private readonly IReadOnlyList<EngineEffectDirectCall> _calls;
    private readonly IReadOnlyDictionary<uint, IReadOnlyList<EngineEffectDirectCall>> _callers;
    private readonly Dictionary<(uint Entry, int Depth, int Paths), IReadOnlyList<EngineEffectCallPath>> _pathCache = [];
    private readonly Dictionary<(uint Entry, int Depth), IReadOnlyList<EngineEffectCallChainNode>> _chainCache = [];
    private readonly Dictionary<(uint Entry, string Targets, int Depth, int Functions), EngineEffectTransitiveReach> _reachCache = [];
    private readonly Dictionary<uint, uint?> _resolvedEntries = [];
    private readonly EngineEffectBinaryImage _image;
    private Dictionary<uint, IReadOnlyList<EngineEffectDirectCall>>? _callees;
    private readonly Dictionary<uint, HashSet<uint>?> _reachable;
    private readonly Dictionary<uint, HashSet<uint>> _alignedSites = [];
    private readonly Dictionary<uint, bool> _verifiedSites = [];
    private readonly EngineEffectJumpIndex? _jumps;
    private readonly IReadOnlyDictionary<uint, uint?> _entryByAddress;
    private readonly Dictionary<uint, IReadOnlyList<EngineEffectDirectCall>> _verifiedCallers = [];
    private IReadOnlyList<EngineEffectDirectCall>? _unalignedCalls;

    private EngineEffectCallGraph(IReadOnlyList<EngineEffectDirectCall> calls,
        IReadOnlyDictionary<uint, IReadOnlyList<EngineEffectDirectCall>> callers, EngineEffectBinaryImage image,
        EngineEffectJumpIndex? jumps, Dictionary<uint, HashSet<uint>?> reachable,
        IReadOnlyDictionary<uint, uint?> entryByAddress)
    {
        _calls = calls;
        _callers = callers;
        _image = image;
        _jumps = jumps;
        _reachable = reachable;
        _entryByAddress = entryByAddress;
        EntryPoint = image.EntryPoint;
    }

    public IReadOnlyList<EngineEffectDirectCall> Calls => _calls;
    /// <summary>PE AddressOfEntryPoint; roots equal to this are the process entry.</summary>
    public uint EntryPoint { get; }

    public static EngineEffectCallGraph Build(EngineEffectBinaryImage image, EngineEffectJumpIndex? jumps = null)
    {
        var calls = new List<EngineEffectDirectCall>();
        var callers = new Dictionary<uint, List<EngineEffectDirectCall>>();
        var reachable = new Dictionary<uint, HashSet<uint>?>();
        var entryByAddress = new Dictionary<uint, uint?>();
        foreach (var raw in image.Context.RawCalls)
        {
            image.Context.CheckCancellation();
            var address = raw.Address;
            var target = raw.Target;
            if (image.SectionOf(target) == null) continue;
            var callerEntry = EngineEffectFunctionAnalyzer.ResolveFunctionEntry(image, jumps, address, reachable);
            entryByAddress[address] = callerEntry;
            var call = new EngineEffectDirectCall
            {
                Address = address,
                Target = target,
                CallerEntry = callerEntry
            };
            calls.Add(call);
            if (!callers.TryGetValue(target, out var list))
            {
                list = [];
                callers[target] = list;
            }
            list.Add(call);
        }
        return new EngineEffectCallGraph(calls,
            callers.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<EngineEffectDirectCall>)pair.Value.OrderBy(call => call.Address).ToArray()),
            image, jumps, reachable, entryByAddress);
    }

    /// <summary>
    /// Callers of <paramref name="entry"/> whose call site a control-flow walk
    /// from the caller's function entry confirms. Raw byte scanning treats every
    /// 0xE8 byte as a call, so data bytes inside code sections can invent callers;
    /// those are reported through <see cref="UnalignedCalls"/> instead of being
    /// shown as real chain nodes.
    /// </summary>
    public IReadOnlyList<EngineEffectDirectCall> CallersOf(uint entry)
    {
        if (_verifiedCallers.TryGetValue(entry, out var cached)) return cached;
        IReadOnlyList<EngineEffectDirectCall> result = [];
        if (_callers.TryGetValue(entry, out var calls))
            result = calls.Where(IsVerifiedCall).ToArray();
        _verifiedCallers[entry] = result;
        return result;
    }

    /// <summary>Call sites that the raw byte scan produced but the control-flow walk rejects.</summary>
    public IReadOnlyList<EngineEffectDirectCall> UnalignedCalls
    {
        get
        {
            if (_unalignedCalls != null) return _unalignedCalls;
            _unalignedCalls = _calls.Where(call => !IsVerifiedCall(call)).ToArray();
            return _unalignedCalls;
        }
    }

    /// <summary>
    /// A call is verified when the control-flow walk from the caller entry reaches
    /// its address, when an inbound jump opens a code cave the walk can enter, or
    /// when a linear decode from the entry lands exactly on the call. The last
    /// rule covers real code that has no attributable prologue, while still
    /// rejecting 0xE8 bytes that sit inside another instruction or inside data.
    /// When the caller entry is unknown the edge cannot be checked and is kept.
    /// </summary>
    private bool IsVerifiedCall(EngineEffectDirectCall call)
    {
        if (call.CallerEntry is not { } caller) return true;
        if (_verifiedSites.TryGetValue(call.Address, out var cached)) return cached;
        if (!_reachable.TryGetValue(caller, out var reached))
        {
            reached = EngineEffectFunctionAnalyzer.ReachableAddresses(_image, caller);
            _reachable[caller] = reached;
        }
        var verified = reached == null || reached.Contains(call.Address) ||
                       (!EngineEffectFunctionAnalyzer.HasPaddingGap(_image, caller, call.Address) &&
                        (ReachableViaJump(call.Address) || AlignedInFunction(caller, call.Address)));
        _verifiedSites[call.Address] = verified;
        return verified;
    }

    public uint? CallerEntryOf(uint callAddress)
    {
        if (_entryByAddress.TryGetValue(callAddress, out var entry)) return entry;
        if (_resolvedEntries.TryGetValue(callAddress, out entry)) return entry;
        entry = EngineEffectFunctionAnalyzer.ResolveFunctionEntry(_image, _jumps, callAddress, _reachable);
        _resolvedEntries.Add(callAddress, entry);
        return entry;
    }

    /// <summary>
    /// Calls that a linear decode from the function entry lands on exactly are
    /// instruction boundaries, so the 0xE8 byte is a real call. Bytes inside an
    /// instruction or inside data never align. The walk alone is too strict:
    /// functions without the <c>55 8B EC</c> prologue (compiler fragments, C++
    /// exception funclets) have no attributable entry, and their calls would be
    /// rejected even though they are genuine.
    /// </summary>
    private bool AlignedInFunction(uint entry, uint callAddress)
    {
        if (!_alignedSites.TryGetValue(entry, out var calls))
        {
            calls = new HashSet<uint>();
            foreach (var item in EngineEffectDecoder.DecodeForward(_image, entry, 0x2000))
            {
                if (item.Instruction.Mnemonic == Mnemonic.Call) calls.Add(item.Address);
            }
            _alignedSites[entry] = calls;
        }
        return calls.Contains(callAddress);
    }

    /// <summary>
    /// Code caves live behind a function's final return; an inbound direct jump
    /// makes them reachable even though the intra-function walk stops earlier.
    /// </summary>
    private bool ReachableViaJump(uint callAddress)
    {
        if (_jumps == null) return false;
        foreach (var jump in _jumps.SourcesNear(callAddress, 0x200))
        {
            if (jump.Target == callAddress) return true;
            if (EngineEffectFunctionAnalyzer.IsReachable(_image, jump.Target, callAddress) == true) return true;
        }
        return false;
    }

    /// <summary>
    /// Breadth-first search over direct E8 edges from a root. Depth 1 means the
    /// root calls an effect entry directly; higher depths mean it reaches one
    /// through intermediate engine functions. Only call sites that an aligned
    /// linear decode confirms are traversed, so stray 0xE8 bytes cannot create
    /// phantom reachability.
    /// </summary>
    public EngineEffectTransitiveReach ReachEffect(uint entry, IReadOnlySet<uint> effectEntries,
        int maxDepth = 4, int maxFunctions = 512)
    {
        var key = (entry, string.Join(",", effectEntries.OrderBy(value => value)), maxDepth, maxFunctions);
        if (_reachCache.TryGetValue(key, out var cached)) return cached;
        var result = ReachEffectCore(entry, effectEntries, maxDepth, maxFunctions);
        _reachCache.Add(key, result);
        return result;
    }

    private EngineEffectTransitiveReach ReachEffectCore(uint entry, IReadOnlySet<uint> effectEntries,
        int maxDepth, int maxFunctions)
    {
        if (effectEntries.Contains(entry))
            return new EngineEffectTransitiveReach(0, entry, entry, 0, false, [entry]);
        var callees = CalleesByEntry();
        var visited = new HashSet<uint> { entry };
        var parents = new Dictionary<uint, uint>();
        var frontier = new List<uint> { entry };
        var expanded = 0;
        for (var level = 1; level <= maxDepth; level++)
        {
            var next = new List<uint>();
            foreach (var node in frontier)
            {
                _image.Context.CheckCancellation();
                if (!callees.TryGetValue(node, out var calls)) continue;
                foreach (var call in calls)
                {
                    if (!IsVerifiedCall(call)) continue;
                    expanded++;
                    var target = call.Target;
                    if (effectEntries.Contains(target))
                        return new EngineEffectTransitiveReach(level, node, target, expanded, false,
                            BuildReachPath(parents, entry, node, target));
                    if (visited.Add(target))
                    {
                        parents[target] = node;
                        next.Add(target);
                    }
                    if (expanded >= maxFunctions)
                        return new EngineEffectTransitiveReach(-1, 0, 0, expanded, true, []);
                }
            }
            if (next.Count == 0) break;
            frontier = next;
        }
        return new EngineEffectTransitiveReach(-1, 0, 0, expanded, false, []);
    }

    private static IReadOnlyList<uint> BuildReachPath(IReadOnlyDictionary<uint, uint> parents,
        uint entry, uint last, uint target)
    {
        var path = new List<uint> { last };
        var node = last;
        while (node != entry && parents.TryGetValue(node, out var parent))
        {
            path.Add(parent);
            node = parent;
        }
        path.Reverse();
        path.Add(target);
        return path.Distinct().ToArray();
    }

    private IReadOnlyDictionary<uint, IReadOnlyList<EngineEffectDirectCall>> CalleesByEntry()
    {
        if (_callees != null) return _callees;
        var map = new Dictionary<uint, List<EngineEffectDirectCall>>();
        foreach (var call in _calls)
        {
            if (call.CallerEntry is not { } caller) continue;
            if (!map.TryGetValue(caller, out var targets))
            {
                targets = [];
                map[caller] = targets;
            }
            targets.Add(call);
        }
        _callees = map.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<EngineEffectDirectCall>)pair.Value.OrderBy(call => call.Address).ToArray());
        return _callees;
    }

    public IReadOnlyList<EngineEffectCallChainNode> ChainOf(uint entry, int depth = 3)
    {
        var key = (entry, depth);
        if (_chainCache.TryGetValue(key, out var cached)) return cached;
        var result = ChainOfCore(entry, depth);
        _chainCache.Add(key, result);
        return result;
    }

    private IReadOnlyList<EngineEffectCallChainNode> ChainOfCore(uint entry, int depth)
    {
        var result = new List<EngineEffectCallChainNode>();
        var visited = new HashSet<uint> { entry };
        var frontier = new List<uint> { entry };
        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<uint>();
            foreach (var target in frontier)
            {
                foreach (var caller in CallersOf(target))
                {
                    result.Add(new EngineEffectCallChainNode
                    {
                        Target = target,
                        CallAddress = caller.Address,
                        CallerEntry = caller.CallerEntry,
                        Depth = level + 1
                    });
                    if (caller.CallerEntry is { } callerEntry && visited.Add(callerEntry))
                        next.Add(callerEntry);
                }
            }
            frontier = next;
        }
        return result;
    }

    /// <summary>
    /// Enumerates every E8 caller path from a function to a root (a function with
    /// no incoming E8 call).  Paths are capped so a shared low-level function
    /// cannot explode the scan; cycles, unknown callers and depth limits are
    /// reported instead of being silently dropped.
    /// </summary>
    public IReadOnlyList<EngineEffectCallPath> PathsOf(uint entry, int maxDepth = 64, int maxPaths = 64)
    {
        var key = (entry, maxDepth, maxPaths);
        if (_pathCache.TryGetValue(key, out var cached)) return cached;
        var paths = BuildPaths(entry, maxDepth, maxPaths);
        _pathCache[key] = paths;
        return paths;
    }

    public static IReadOnlyList<uint> RootsOf(IReadOnlyList<EngineEffectCallPath> paths)
        => paths.Where(path => path.RootEntry is { } && !path.Truncated && !path.PathLimitReached &&
                               !path.IsCycle && !path.UnknownCaller)
            .Select(path => path.RootEntry!.Value).Distinct().OrderBy(value => value).ToArray();

    private IReadOnlyList<EngineEffectCallPath> BuildPaths(uint entry, int maxDepth, int maxPaths)
    {
        var result = new List<EngineEffectCallPath>();
        var nodes = new List<EngineEffectCallPathNode>();
        var visited = new HashSet<uint> { entry };
        var capped = false;

        void AddPath(uint? root, bool truncated, bool cycle, bool unknown)
        {
            result.Add(new EngineEffectCallPath
            {
                Nodes = nodes.ToArray(),
                RootEntry = root,
                RootHasNoCallers = root != null,
                RootIsEntryPoint = root != null && root == EntryPoint,
                Truncated = truncated,
                IsCycle = cycle,
                UnknownCaller = unknown,
                Summary = DescribePath(entry, nodes, root, truncated, cycle, unknown)
            });
        }

        void Visit(uint target, int depth)
        {
            _image.Context.CheckCancellation();
            if (result.Count >= maxPaths) { capped = true; return; }
            var callers = CallersOf(target);
            if (callers.Count == 0)
            {
                AddPath(target, false, false, false);
                return;
            }
            if (depth >= maxDepth)
            {
                AddPath(null, true, false, false);
                return;
            }
            foreach (var caller in callers)
            {
                if (result.Count >= maxPaths) { capped = true; return; }
                if (caller.CallerEntry is not { } callerEntry)
                {
                    nodes.Add(new EngineEffectCallPathNode
                    {
                        CallAddress = caller.Address,
                        ChildTarget = target,
                        Depth = depth + 1,
                        CallerUnknown = true
                    });
                    AddPath(null, false, false, true);
                    nodes.RemoveAt(nodes.Count - 1);
                    continue;
                }
                if (!visited.Add(callerEntry))
                {
                    nodes.Add(new EngineEffectCallPathNode
                    {
                        FunctionEntry = callerEntry,
                        CallAddress = caller.Address,
                        ChildTarget = target,
                        Depth = depth + 1,
                        IsCycle = true
                    });
                    AddPath(null, false, true, false);
                    nodes.RemoveAt(nodes.Count - 1);
                    continue;
                }
                nodes.Add(new EngineEffectCallPathNode
                {
                    FunctionEntry = callerEntry,
                    CallAddress = caller.Address,
                    ChildTarget = target,
                    Depth = depth + 1
                });
                Visit(callerEntry, depth + 1);
                nodes.RemoveAt(nodes.Count - 1);
                visited.Remove(callerEntry);
            }
        }

        Visit(entry, 0);
        if (capped)
        {
            result.Add(new EngineEffectCallPath
            {
                PathLimitReached = true,
                Summary = $"E8 调用链超过 {maxPaths} 条，其余分支未展开"
            });
        }
        return result;
    }

    private string DescribePath(uint entry, IReadOnlyList<EngineEffectCallPathNode> nodes,
        uint? root, bool truncated, bool cycle, bool unknown)
    {
        var rootSuffix = root != null && root == EntryPoint ? "（PE 入口）" : string.Empty;
        if (nodes.Count == 0)
            return root is { } selfRoot
                ? $"0x{entry:X8} 是 E8 根（无直接 E8 调用者）{rootSuffix}"
                : $"0x{entry:X8}";
        var text = new System.Text.StringBuilder();
        text.Append($"0x{entry:X8}");
        foreach (var node in nodes)
        {
            if (node.CallerUnknown)
                text.Append($" ← 0x{node.CallAddress:X8}（调用者函数未知）");
            else
                text.Append($" ← 0x{node.CallAddress:X8}（函数 0x{node.FunctionEntry:X8}）");
        }
        if (root is { } rootEntry)
            text.Append($" → E8 根 0x{rootEntry:X8}{rootSuffix}");
        else if (cycle)
            text.Append(" → 调用环");
        else if (unknown)
            text.Append(" → 调用者函数未知");
        else if (truncated)
            text.Append(" → 深度上限，未到 E8 根");
        return text.ToString();
    }

    public static string Describe(IReadOnlyList<EngineEffectCallChainNode> chain)
    {
        if (chain.Count == 0) return "未发现直接调用者（可能为跳转/表分派入口）。";
        return string.Join(" ← ", chain.Take(3).Select(node =>
            $"0x{node.CallAddress:X8} 调用 0x{node.Target:X8}" +
            (node.CallerEntry is { } caller ? $"（调用者函数 0x{caller:X8}）" : "（调用者函数未知）")));
    }
}
