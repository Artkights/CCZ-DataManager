using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RangeManager.App;

/// <summary>Versioned, mapped EXE range data. No method in this class writes a file.</summary>
public static class ManagedRangeRuntime
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CCZRNG02");
    private static readonly byte[] MappingMagic = Encoding.ASCII.GetBytes("CCZRNG03");
    private const int FooterSize = 64;
    private const uint PierceEntry = 0x00435CE9;
    // Native cursor pierce-preview predicate entries by engine generation.
    // 6.4/6.5 share the 0x451ECD jump-table predicate; 6.6 uses 0x451EC5
    // with an explicit type branch chain.
    private const uint PreviewMoveTypeDispatch = 0x0045456F;
    // The mouse-move cursor-box renderer is shared by 6.4/6.5/6.6. Its
    // default path only repaints a 1x1 cell, so managed shapes need their
    // own bounding box.
    private const uint PreviewMoveDefault = 0x004545F3;
    private const uint PreviewMoveResume = 0x004545FD;
    private sealed record PreviewProfile(
        EngineVersion Version,
        uint Entry,
        byte[] EntryBytes,
        uint EntryResume,
        uint MoveTypeDispatch,
        uint MoveDefault,
        uint MoveResume,
        bool SupportsUnitRelative,
        uint UnitPointerVa,
        uint UnitCaptureHook,
        byte[] UnitCaptureBytes,
        uint StrategyContext);
    private static PreviewProfile? PreviewProfileFor(EngineVersion version) => version switch
    {
        EngineVersion.V64 or EngineVersion.V65 => new(version,
            0x00451ECD,
            [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x18, 0x89, 0x4D, 0xEC],
            0x00451ED6,
            PreviewMoveTypeDispatch, PreviewMoveDefault, PreviewMoveResume,
            SupportsUnitRelative: false,
            UnitPointerVa: 0,
            // 0x455372 is the shared call to 0x425AD0 taken by every preview
            // type after [ebp-0x1c] is resolved from arg8. Capturing here also
            // covers type 2/3 (strategy preview), which skips the type-1-only
            // capture at 0x455474.
            UnitCaptureHook: 0x00455372,
            UnitCaptureBytes: [0xE8, 0x59, 0x07, 0xFD, 0xFF],
            StrategyContext: 0x00497AF8),
        EngineVersion.V66 => new(EngineVersion.V66,
            0x00451EC5,
            [0x55, 0x8B, 0xEC, 0x33, 0xC0],
            0x00451ECA,
            PreviewMoveTypeDispatch, PreviewMoveDefault, PreviewMoveResume,
            SupportsUnitRelative: true,
            UnitPointerVa: 0x00500AC0,
            UnitCaptureHook: 0,
            UnitCaptureBytes: [],
            StrategyContext: 0x00497AF8),
        _ => null
    };
    /// <summary>
    /// Identifies the generated Pierce dispatcher ABI.  Older blocks lack this
    /// field and still contain the even-cursor guard, which suppresses the
    /// engine's 0x004400FA / 0x004D2110 preview callers that seed cursor 1.
    /// </summary>
    public const string PierceHandlerContract = "pierce-cursor-any-0..50-v2";
    /// <summary>
    /// Identifies the managed cursor-preview dispatcher ABI. Any block whose
    /// managed pierce bindings predate this contract is rebuilt on the next
    /// save or explicit runtime rebuild.
    /// </summary>
    public const string PreviewHandlerContract = "preview-entry-strategy-v8";
    public const string PreviewUnitCaptureContract = "preview-unit-capture-455372-v3";
    public const string PreviewUnitCaptureUnavailable = "preview-unit-capture-unavailable";
    public const string PreviewUnavailableContract = "preview-entry-unavailable";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public sealed class RuntimeState
    {
        public int Format { get; set; } = 2;
        public EngineVersion Version { get; set; }
        public byte[] OriginalSectionHeader { get; set; } = [];
        public uint OriginalSizeOfImage { get; set; }
        public uint OriginalSizeOfCode { get; set; }
        public uint OriginalInitializedSize { get; set; }
        public uint OriginalSecurityOffset { get; set; }
        public uint OriginalSymbolOffset { get; set; }
        public const string CoordinateContractText = "Preserve caller's byte cursor 0..50 at 00500E7C (0, 1 and 2 are all valid; 004400FA seeds 004D2110 effect-class values); reserve terminator at 00500E49+cursor; at most 24 additional coordinates. Invalid cursor produces an empty target append without touching coordinate memory.";
        public string CoordinateContract { get; set; } = CoordinateContractText;
        /// <summary>
        /// Generated Pierce dispatcher ABI; null in legacy blocks. Do not give
        /// this property a non-null initializer: JSON deserialization would
        /// then make an old block look current and skip its migration.
        /// </summary>
        public string? HandlerContract { get; set; }
        /// <summary>
        /// Managed cursor-preview dispatcher ABI; null in legacy blocks. Do
        /// not initialize this property: an old block must stay visibly old
        /// so a save/rebuild migrates it.
        /// </summary>
        public string? PreviewContract { get; set; }
        /// <summary>Runtime scratch slot holding the acting unit pointer for 6.4/6.5 preview.</summary>
        public uint PreviewUnitPointerVa { get; set; }
        /// <summary>Unit-pointer capture hook contract; null in legacy blocks.</summary>
        public string? UnitCaptureContract { get; set; }
        public List<Hook> Hooks { get; set; } = [];
        public List<RangeBinding> Bindings { get; set; } = [];
        /// <summary>Optional diagnostic ring buffer written by the Pierce handler.</summary>
        public uint TraceBufferVa { get; set; }
        public int TraceCapacity { get; set; }
        /// <summary>256-byte managed mapping. The metadata version is 2 even though its footer is CCZRNG03.</summary>
        public ExtensionMappings? Mapping { get; set; }
    }

    public sealed class ExtensionMappings
    {
        public int Format { get; set; } = 2;
        public byte[] Values { get; set; } = [];
        public uint HookAddress { get; set; }
        public uint TableAddress { get; set; }
        public string Contract { get; set; } = "CCZRNG03-v2";
    }

    public sealed class Hook
    {
        public uint Address { get; set; }
        public byte[] Original { get; set; } = [];
        public byte[] Installed { get; set; } = [];
    }

    private sealed record Layout(int Pe, int Optional, int LastHeader, uint Raw, uint RawSize,
        uint Rva, uint VirtualSize, uint Characteristics, uint ImageBase, uint FileAlignment, uint SectionAlignment);
    private sealed record Existing(RuntimeState State, int Start, int End, Layout Layout);

    public static IReadOnlyList<RangeBinding> ReadBindings(GameBase game)
    {
        var found = ReadExisting(game.Exe);
        if (found == null) return [];
        if (found.State.Version != game.Version) throw new InvalidDataException("范围扩展区的引擎版本不匹配");
        return found.State.Bindings.Select(b =>
        {
            b.IsManaged = true;
            b.IsModified = false;
            return b;
        }).ToList();
    }

    /// <summary>Reads the persisted 256-byte CCZRNG03 mapping, if present.</summary>
    public static RangeExtensionMapping? ReadExtensionMappings(GameBase game)
    {
        var found = ReadExisting(game.Exe);
        if (found?.State.Mapping is not { } mapping) return null;
        if (mapping.Format != 2 || mapping.Values.Length != RangeExtensionMapping.ValueCount)
            throw new InvalidDataException("CCZRNG03 映射目录版本或长度无效");
        if (mapping.Values[0xFF] != 0xFF || mapping.Values.Take(0xFF).Any(v => v == 0xFF))
            throw new InvalidDataException("CCZRNG03 映射目录只能在 0xFF 槽保留哨兵");
        return RangeExtensionMapping.FromManaged(mapping.Values);
    }

    /// <summary>
    /// True when the executable already carries managed bindings but their
    /// generated Pierce dispatcher predates the current cursor contract.
    /// A legacy block lacks <see cref="RuntimeState.HandlerContract"/> and
    /// must be rebuilt by saving any range change.
    /// </summary>
    public static bool NeedsHandlerRebuild(GameBase game)
    {
        var found = ReadExisting(game.Exe);
        if (found == null || found.State.Bindings.Count == 0) return false;
        if (!string.Equals(found.State.HandlerContract, PierceHandlerContract, StringComparison.Ordinal))
            return true;
        var profile = PreviewProfileFor(found.State.Version);
        if (profile != null && found.State.Bindings.Count != 0)
        {
            string? preview = found.State.PreviewContract;
            if (preview == null ||
                (preview != PreviewHandlerContract && preview != PreviewUnavailableContract))
                return true;
            var managed = found.State.Bindings.Where(b => b.Key.Kind == RangeKind.Pierce).ToList();
            if (profile.Version != EngineVersion.V66 &&
                managed.Any(b => b.Center == RangeCenterMode.Self ||
                    b.Definition.Groups.Any(g => g.Geometry == RangeGeometryKind.Directional)))
            {
                string? capture = found.State.UnitCaptureContract;
                if (capture == null ||
                    (capture != PreviewUnitCaptureContract && capture != PreviewUnitCaptureUnavailable))
                    return true;
            }
        }
        return false;
    }

    /// <summary>True when a managed pierce binding needs the cursor preview dispatcher.</summary>
    private static bool HasPreviewPoints(RangeBinding binding) =>
        binding.Definition.Groups.Any(group =>
            group.Geometry is RangeGeometryKind.Mirror or RangeGeometryKind.Directional) &&
        ExpandLocal(binding).Any(point => !point.SidePair);

    /// <summary>Version-specific preview eligibility; legacy profiles cover mirror target only.</summary>
    private static bool IsPreviewBinding(PreviewProfile profile, RangeBinding binding) =>
        HasPreviewPoints(binding) &&
        (profile.SupportsUnitRelative ||
         binding.Center == RangeCenterMode.Target &&
         binding.Definition.Groups.All(group => group.Geometry == RangeGeometryKind.Mirror));

    /// <summary>Returns the persisted runtime state for diagnostics/trace readers.</summary>
    public static RuntimeState? ReadState(GameBase game) => ReadExisting(game.Exe)?.State;

    /// <summary>Restore our known hook windows for native signature inspection; retain actual range pointers.</summary>
    public static byte[] GetNativeBytes(PeFile exe)
    {
        byte[] result = (byte[])exe.Bytes.Clone();
        var found = ReadExisting(exe);
        if (found != null)
            foreach (var hook in found.State.Hooks)
                hook.Original.CopyTo(result, exe.VaToOffset(hook.Address));
        return result;
    }

    /// <summary>Existing managed bytes and the overlay are relocated during a rebuild.</summary>
    internal static int RebuildProtectedOffset(PeFile exe)
    {
        var existing = ReadExisting(exe);
        if (existing != null)
            return Math.Min(existing.Start, checked((int)(existing.Layout.Raw + U32(existing.State.OriginalSectionHeader, 16))));
        var layout = ReadLayout(exe.Bytes);
        return checked((int)(layout.Raw + layout.RawSize));
    }

    public static byte[] Build(GameBase game, IReadOnlyList<RangeBinding> bindings)
        => Build(game, bindings, null, false);

    /// <summary>
    /// Builds the range runtime and, when required, a CCZRNG03 managed
    /// mapping. The old two-argument overload intentionally retains native
    /// behaviour for callers that only edit range definitions. Pass
    /// <paramref name="trace"/> to reserve a diagnostic ring buffer that the
    /// Pierce handler records every call into.
    /// </summary>
    public static byte[] Build(GameBase game, IReadOnlyList<RangeBinding> bindings,
        RangeExtensionMapping? extension = null, bool trace = false)
    {
        if (bindings.Select(b => b.Key).Distinct().Count() != bindings.Count)
            throw new InvalidDataException("范围编号重复");
        var selected = bindings.Where(b => b.IsModified || b.IsManaged).ToList();
        if (game.Version == EngineVersion.V65 &&
            selected.Any(b => b.Key == new RangeKey(RangeKind.Pierce, 0x0A)))
            throw new InvalidDataException("6.5 穿透 0A 使用独立目标判定分支，不能由通用范围运行时覆盖");
        foreach (var binding in selected) ValidateBinding(binding);
        foreach (RangeKind kind in selected.Select(b => b.Key.Kind).Distinct())
        {
            var errors = NativeRangeContract.Validate(game, kind);
            if (errors.Count != 0) throw new InvalidDataException(string.Join("；", errors));
            if (kind == RangeKind.Pierce && !NativeRangeContract.TryReadPierceCenter(game, out _, out string centerError))
                throw new InvalidDataException(centerError);
        }
        var old = ReadExisting(game.Exe);
        extension ??= old?.State.Mapping is { } persisted
            ? RangeExtensionMapping.FromManaged(persisted.Values)
            : null;
        if (extension != null) extension.ValidateForSave();
        byte[] native = RemoveOldBlock(game.Exe, old);
        if (selected.Count == 0 && (extension == null || (!extension.IsDirty && !extension.HasManagedMappings))) return native;
        var pe = new PeFile(game.Exe.Path, native);
        if (extension != null && extension.IsDirty)
        {
            // Native edits remain confined to the original 33-byte table;
            // never write the logical 256-byte view over the following code.
            pe.WriteBytes(RangeExtensionMapping.TableVa,
                extension.Values.Take(RangeExtensionMapping.NativeCount).ToArray());
            native = pe.Bytes;
        }
        ValidateNativeContracts(game.Version, pe);
        Layout layout = ReadLayout(native);
        // Extend the final real section. Its existing resource bytes and the complete overlay remain intact.
        int sectionStart = checked((int)Align(Math.Max(layout.RawSize, layout.VirtualSize), 16));
        uint blockVa = layout.ImageBase + layout.Rva + (uint)sectionStart;
        var state = old?.State ?? new RuntimeState
        {
            Version = game.Version,
            OriginalSectionHeader = native.AsSpan(layout.LastHeader, 40).ToArray(),
            OriginalSizeOfImage = U32(native, layout.Optional + 56),
            OriginalSizeOfCode = U32(native, layout.Optional + 4),
            OriginalInitializedSize = U32(native, layout.Optional + 8),
            OriginalSecurityOffset = U32(native, layout.Optional + 128),
            OriginalSymbolOffset = U32(native, layout.Pe + 12)
        };
        state.Hooks = [];
        state.Mapping = null;
        // A rebuild must also migrate a legacy block to the current dispatcher
        // contract and coordinate description.
        state.CoordinateContract = RuntimeState.CoordinateContractText;
        state.HandlerContract = PierceHandlerContract;
        state.Bindings = selected.Select(b =>
        {
            var copy = b.Clone(b.Definition.Clone());
            copy.IsManaged = true;
            copy.IsModified = false;
            return copy;
        }).ToList();
        // Code has a fixed start independent of the JSON length. The final JSON follows all code/data.
        var payload = new List<byte>();
        var definitionPointers = new Dictionary<string, uint>(StringComparer.Ordinal);

        // The managed table lives in the new section. Its native 33-byte
        // prefix is copied verbatim, while all remaining values are explicit
        // identity mappings until the user changes them.
        if (extension?.HasManagedMappings == true)
        {
            AlignList(payload, 16);
            uint mapVa = blockVa + (uint)payload.Count;
            payload.AddRange(extension.Values);
            AlignList(payload, 16);
            uint mapRouter = blockVa + (uint)payload.Count;
            var mapCode = new X86(mapRouter);
            string fallback = "map-fallback";
            // Guard the index before using it as an offset. The native call
            // sites pass a byte range value, but the guard also protects the
            // shared helper from malformed external callers.
            mapCode.Emit(0x81, 0xF9); mapCode.U32(0xFE); // cmp ecx,0xFE
            mapCode.Jcc(0x87, fallback);
            mapCode.Emit(0xBA); mapCode.U32(mapVa); // table pointer in EDX
            mapCode.Emit(0x31, 0xC0); // clear the high bytes of the result
            mapCode.Emit(0x8A, 0x04, 0x0A); // mov al,[edx+ecx]
            mapCode.JumpLabel("map-return");
            mapCode.Mark(fallback);
            mapCode.Emit(0x8B, 0xC1); // out-of-domain values retain default
            mapCode.Mark("map-return");
            mapCode.Jump(0x0041CEA3u);
            payload.AddRange(mapCode.Finish());
            uint mapEntry = game.Version == EngineVersion.V66 ? 0x0041CE9Eu : 0x0041CE9Du;
            InstallHook(native, pe, state, mapEntry, mapRouter, 6);
            state.Mapping = new ExtensionMappings
            {
                Format = 2,
                Values = [.. extension.Values],
                HookAddress = mapEntry,
                TableAddress = mapVa
            };
        }
        foreach (var binding in selected.Where(IsTableDriven))
        {
            // A center-only edit must retain the native shared segment.  The
            // session keeps aliases on one RangeDefinition, but only the
            // changed binding may be selected for a native record; blindly
            // allocating a new segment here would split that alias.  A real
            // geometry edit is detected by comparing the existing bytes.
            byte[] requested = RangeShapeTable.Serialize(binding.Definition.Groups.SelectMany(g => g.Generators).ToList());
            if (binding.NativePointer != 0 &&
                pe.TryVaToOffset(binding.NativePointer, out int nativeDefinition) &&
                FitsMappedSection(pe, binding.NativePointer, nativeDefinition, requested.Length) &&
                native.AsSpan(nativeDefinition, requested.Length).SequenceEqual(requested))
                continue;
            string identity = Identity(binding);
            if (!definitionPointers.TryGetValue(identity, out uint pointer))
            {
                AlignList(payload, 4);
                pointer = blockVa + (uint)payload.Count;
                foreach (var generator in binding.Definition.Groups.SelectMany(g => g.Generators))
                {
                    payload.Add(unchecked((byte)generator.Dx));
                    payload.Add(unchecked((byte)generator.Dy));
                }
                payload.Add(0xFF); payload.Add(0xFF);
                definitionPointers.Add(identity, pointer);
            }
            foreach (uint slot in binding.PointerSlots.Distinct())
            {
                if (!pe.TryVaToOffset(slot, out _)) throw new InvalidDataException($"范围指针槽 0x{slot:X8} 无文件映射");
                Put32(native, pe.VaToOffset(slot), pointer);
            }
            var stored = state.Bindings.Single(b => b.Key == binding.Key);
            stored.NativePointer = pointer;
            stored.Source = $"Ekd5.exe 扩展生成元 0x{pointer:X8}";
        }

        var newAttack = selected.Where(b => b.Key.Kind == RangeKind.Attack && b.Key.Id >= 0x26).ToList();
        if (newAttack.Count != 0)
        {
            AlignList(payload, 16);
            uint router = blockVa + (uint)payload.Count;
            var code = new X86(router);
            foreach (var binding in newAttack)
            {
                string next = "a" + binding.Key.Id;
                code.Emit(0x3C, (byte)binding.Key.Id); code.Jcc(0x85, next);
                code.Emit(0xBA); code.U32(definitionPointers[Identity(binding)]);
                code.Jump(game.Version == EngineVersion.V66 ? 0x004365A3u : 0x004365A8u);
                code.Mark(next);
            }
            uint upper = game.Version == EngineVersion.V66 ? 0x00436579u : 0x0043657Eu;
            uint loop = game.Version == EngineVersion.V66 ? 0x004365D3u : 0x00436562u;
            code.Emit(0x3C, 0x25); code.JccAddress(0x87, loop);
            code.Emit(0x3C, 0x0A); code.Jump(upper + 6);
            payload.AddRange(code.Finish());
            InstallHook(native, pe, state, upper, router, 6);
        }

        var pierce = selected.Where(b => b.Key.Kind == RangeKind.Pierce).ToList();
        uint traceVa = 0;
        int traceCapacity = 0;
        if (trace && pierce.Count != 0)
        {
            AlignList(payload, 16);
            traceVa = blockVa + (uint)payload.Count;
            traceCapacity = 127;
            var traceBuffer = new byte[4096];
            Put32(traceBuffer, 0, 0x54524350); // "PCRT"
            Put32(traceBuffer, 4, 0);
            Put32(traceBuffer, 8, (uint)traceCapacity);
            payload.AddRange(traceBuffer);
        }
        if (pierce.Count != 0)
        {
            AlignList(payload, 16);
            uint routerVa = blockVa + (uint)payload.Count;
            var router = new X86(routerVa);
            // Preserve EAX exactly on the path to the original function.
            router.Emit(0x50, 0x8A, 0x44, 0x24, 0x0C);
            foreach (var binding in pierce)
            {
                router.Emit(0x3C, (byte)binding.Key.Id); router.Jcc(0x84, "p" + binding.Key.Id);
            }
            router.Emit(0x58);
            router.Emit(pe.Bytes.AsSpan(pe.VaToOffset(PierceEntry), 6).ToArray());
            router.Jump(PierceEntry + 6);
            foreach (var binding in pierce)
            {
                router.Mark("p" + binding.Key.Id); router.Emit(0x58);
                EmitPierceHandler(router, game.Version, binding, traceVa, traceCapacity);
            }
            payload.AddRange(router.Finish());
            InstallHook(native, pe, state, PierceEntry, routerVa, 6);
        }
        var previewProfile = PreviewProfileFor(game.Version);
        if (previewProfile != null)
        {
            // Preview covers every editable native/managed pierce binding,
            // not only the selected ones that need runtime execution handlers.
            var previewSource = bindings
                .Where(binding => binding.Key.Kind == RangeKind.Pierce && binding.Editable)
                .Select(binding => NativeRangeCatalog.EnsurePreviewDefinition(binding, game))
                .Where(binding => HasPreviewPoints(binding)).ToList();
            var activeProfile = previewProfile;
            bool legacyNeedsUnit = previewProfile.Version != EngineVersion.V66 &&
                previewSource.Any(binding => binding.Center == RangeCenterMode.Self ||
                    binding.Definition.Groups.Any(group => group.Geometry == RangeGeometryKind.Directional));
            if (legacyNeedsUnit)
            {
                if (UnitCaptureContractMatches(pe, previewProfile))
                {
                    AlignList(payload, 4);
                    uint scratchVa = blockVa + (uint)payload.Count;
                    payload.AddRange(new byte[4]);
                    AlignList(payload, 16);
                    uint captureVa = blockVa + (uint)payload.Count;
                    var capture = new X86(captureVa);
                    EmitLegacyUnitCapture(capture, scratchVa);
                    payload.AddRange(capture.Finish());
                    InstallHook(native, pe, state, previewProfile.UnitCaptureHook, captureVa, 5);
                    state.UnitCaptureContract = PreviewUnitCaptureContract;
                    state.PreviewUnitPointerVa = scratchVa;
                    activeProfile = previewProfile with
                    {
                        SupportsUnitRelative = true,
                        UnitPointerVa = scratchVa
                    };
                }
                else
                {
                    state.UnitCaptureContract = PreviewUnitCaptureUnavailable;
                    state.PreviewUnitPointerVa = 0;
                }
            }
            else
            {
                state.UnitCaptureContract = null;
                state.PreviewUnitPointerVa = previewProfile.UnitPointerVa;
            }
            var previewBindings = previewSource
                .Where(binding => IsPreviewBinding(activeProfile, binding)).ToList();
            if (previewBindings.Count == 0)
            {
                state.PreviewContract = null;
            }
            else if (PreviewEntryContractMatches(pe, previewProfile))
            {
                AlignList(payload, 16);
                uint trampolineVa = blockVa + (uint)payload.Count;
                var trampoline = new X86(trampolineVa);
                trampoline.Emit(previewProfile.EntryBytes);
                trampoline.Jump(previewProfile.EntryResume);
                payload.AddRange(trampoline.Finish());

                AlignList(payload, 16);
                uint dispatcherVa = blockVa + (uint)payload.Count;
                var dispatcher = new X86(dispatcherVa);
                EmitPreviewDispatcher(dispatcher, trampolineVa, activeProfile.UnitPointerVa,
                    previewProfile.StrategyContext, previewBindings);
                payload.AddRange(dispatcher.Finish());
                InstallHook(native, pe, state, previewProfile.Entry, dispatcherVa, 5);
                state.PreviewContract = PreviewHandlerContract;
            }
            else
            {
                // A foreign patch owns the preview entry. Keep native preview
                // and avoid a rebuild loop; the diagnostics explain the state.
                state.PreviewContract = PreviewUnavailableContract;
            }
            var targetBoxes = previewBindings
                .Where(binding => binding.Center == RangeCenterMode.Target)
                .Select(PreviewBounds).Where(box => box != null).ToList();
            var selfBoxes = previewBindings
                .Where(binding => binding.Center == RangeCenterMode.Self)
                .Select(PreviewBounds).Where(box => box != null).ToList();
            if ((targetBoxes.Count != 0 || selfBoxes.Count != 0) &&
                PreviewMoveContractMatches(pe, previewProfile))
            {
                AlignList(payload, 16);
                uint typeVa = blockVa + (uint)payload.Count;
                var typeCode = new X86(typeVa);
                EmitPreviewTypeDispatch(typeCode, previewBindings);
                payload.AddRange(typeCode.Finish());
                InstallHook(native, pe, state, previewProfile.MoveTypeDispatch, typeVa, 5);

                AlignList(payload, 16);
                uint boxVa = blockVa + (uint)payload.Count;
                var boxCode = new X86(boxVa);
                EmitPreviewBoxHandler(boxCode, activeProfile.UnitPointerVa, targetBoxes, selfBoxes);
                payload.AddRange(boxCode.Finish());
                InstallHook(native, pe, state, previewProfile.MoveDefault, boxVa, 5);
            }
        }
        else
        {
            state.PreviewContract = null;
            state.UnitCaptureContract = null;
            state.PreviewUnitPointerVa = 0;
        }
        state.TraceBufferVa = traceVa;
        state.TraceCapacity = traceCapacity;
        AlignList(payload, 16);
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        int metadataAt = payload.Count;
        payload.AddRange(metadata);
        int rawSize = checked((int)Align((uint)(sectionStart + payload.Count + FooterSize), layout.FileAlignment));
        int rawEnd = checked((int)layout.Raw + rawSize);
        int previousEnd = checked((int)(layout.Raw + layout.RawSize));
        byte[] result = new byte[checked(rawEnd + native.Length - previousEnd)];
        native.AsSpan(0, previousEnd).CopyTo(result);
        payload.CopyTo(result, checked((int)layout.Raw + sectionStart));
        native.AsSpan(previousEnd).CopyTo(result.AsSpan(rawEnd));
        int footer = rawEnd - FooterSize;
        byte[] footerMagic = state.Mapping == null ? Magic : MappingMagic;
        footerMagic.CopyTo(result, footer);
        // CCZRNG03 carries the v2 mapping metadata in the same authenticated
        // JSON block; the footer magic identifies the extended mapping hook.
        Put32(result, footer + 8, 2);
        Put32(result, footer + 12, (uint)sectionStart);
        Put32(result, footer + 16, (uint)(sectionStart + metadataAt));
        Put32(result, footer + 20, (uint)metadata.Length);
        SHA256.HashData(result.AsSpan(checked((int)layout.Raw + sectionStart), footer - checked((int)layout.Raw + sectionStart)))
            .CopyTo(result, footer + 24);
        Put32(result, layout.LastHeader + 8, (uint)(rawSize));
        Put32(result, layout.LastHeader + 16, (uint)rawSize);
        Put32(result, layout.LastHeader + 36, (layout.Characteristics | 0xE0000060u) & ~0x02000000u);
        Put32(result, layout.Optional + 56, Align(layout.Rva + (uint)rawSize, layout.SectionAlignment));
        Put32(result, layout.Optional + 4, checked(state.OriginalSizeOfCode + (uint)(rawSize - layout.RawSize)));
        Put32(result, layout.Optional + 8, checked(state.OriginalInitializedSize + (uint)(rawSize - layout.RawSize)));
        Put32(result, layout.Optional + 64, 0); // Modified PE checksum must not claim the old value.
        ShiftOverlayOffsets(result, layout, previousEnd, rawEnd - previousEnd);
        var verify = new PeFile(game.Exe.Path, result);
        _ = ReadExisting(verify) ?? throw new InvalidDataException("范围扩展区复读失败");
        return result;
    }


    private static bool IsTableDriven(RangeBinding b) =>
        (b.Key.Kind == RangeKind.Attack || b.PointerSlots.Count != 0) &&
        b.Definition.Groups.All(g => g.Geometry == RangeGeometryKind.Mirror);

    private static bool FitsMappedSection(PeFile exe, uint address, int offset, int length)
    {
        var section = exe.SectionOf(address);
        return offset >= section.RawOffset &&
            (long)offset + length <= (long)section.RawOffset + section.RawSize;
    }

    private static string Identity(RangeBinding b) => string.IsNullOrEmpty(b.Definition.Identity)
        ? b.Key.ToString() : b.Definition.Identity;

    private static void ValidateBinding(RangeBinding b)
    {
        if (!b.Editable && b.IsModified) throw new InvalidDataException(b.ReadOnlyReason);
        if (!Enum.IsDefined(b.Center)) throw new InvalidDataException("范围中心模式无效");
        if (b.Key.Id is < 0 or >= 0xFF) throw new InvalidDataException("运行时编号必须为 0..254");
        if (b.Key.Kind == RangeKind.Attack && b.Key.Id is 0x0A or 0x0B)
            throw new InvalidDataException("攻击 0A/0B 是引擎特殊分派，不能覆盖");
        if (b.Key.Kind == RangeKind.Attack && b.Definition.Groups.Any(g => g.Geometry != RangeGeometryKind.Mirror))
            throw new InvalidDataException("攻击范围只支持真实镜像生成元");
        foreach (var g in b.Definition.Groups)
        {
        if (g.OrdinalStart is < 0 or > 24) throw new InvalidDataException("衰减顺序起点必须为0..24");
        foreach (var c in g.Generators)
            if (c.Dx is < -32 or > 31 || c.Dy is < -32 or > 31 || c.Dx == -1 && c.Dy == -1)
                throw new InvalidDataException("生成元坐标必须为 -32..31，且不能使用 FFFF 终止符");
        }
        if (b.RuntimeCacheId is < -1 or >= 0xFF) throw new InvalidDataException("范围缓存编号无效");
        if (b.Definition.Groups.Sum(g => g.Generators.Count) > 256)
            throw new InvalidDataException("每个范围最多256个生成元");
        if (b.Key.Kind == RangeKind.Pierce && ExpandLocal(b).Count > 24)
            throw new InvalidDataException("穿透范围最多24个附加坐标，另为主目标保留一个坐标预算");
    }

    private sealed record PointSpec(int X, int Y, bool Directional, bool SidePair, int Rank, int? Constant);
    private static List<PointSpec> ExpandLocal(RangeBinding binding)
    {
        var points = new List<PointSpec>();
        foreach (var group in binding.Definition.Groups)
        {
            int ordinal = group.OrdinalStart;
            foreach (var c in NativeRangeCatalog.OrderedOffsets(group))
            {
                ordinal++;
                int rank = group.WeightMode == RangeWeightMode.Ordinal ? ordinal : Math.Max(Math.Abs(c.Dx), Math.Abs(c.Dy));
                var point = new PointSpec(c.Dx, c.Dy, group.Geometry != RangeGeometryKind.Mirror, group.Geometry == RangeGeometryKind.SidePair,
                    Math.Max(1, rank), group.WeightMode == RangeWeightMode.Constant ? Math.Clamp(group.ConstantWeight, 0, 100) : null);
                if (!points.Any(p => p.X == point.X && p.Y == point.Y && p.Directional == point.Directional)) points.Add(point);
            }
        }
        return points;
    }

    private static bool PreviewEntryContractMatches(PeFile pe, PreviewProfile profile)
    {
        try
        {
            if (!pe.Match(profile.Entry, profile.EntryBytes)) return false;
            if (profile.Version == EngineVersion.V66)
                return pe.Match(0x00451FF5, 0x3C, 0x0B, 0x73, 0x48) &&
                       pe.Match(0x0045205A, 0x90, 0x90, 0x90, 0x90, 0x90);
            return true;
        }
        catch { return false; }
    }

    private static bool PreviewMoveContractMatches(PeFile pe, PreviewProfile profile)
    {
        try
        {
            return pe.Match(profile.MoveTypeDispatch, 0x80, 0x7D, 0xE8, 0x00, 0x76) &&
                   pe.Match(profile.MoveDefault, 0xC6, 0x45, 0xF0, 0x01, 0x8A);
        }
        catch { return false; }
    }

    private static bool UnitCaptureContractMatches(PeFile pe, PreviewProfile profile)
    {
        try
        {
            return profile.UnitCaptureHook != 0 && profile.UnitCaptureBytes.Length != 0 &&
                   pe.Match(profile.UnitCaptureHook, profile.UnitCaptureBytes);
        }
        catch { return false; }
    }

    /// <summary>
    /// 6.4/6.5 resolve the preview owner into [ebp-0x1c] before the shared
    /// 0x425AD0 call at 0x455372. Type 1 is the physical preview, types 2/3
    /// are the strategy preview; both need the same owner so the dispatcher
    /// can rotate directional shapes from the owner towards the cursor.
    /// Type 4 is an unrelated unit-info pass and must not overwrite it.
    /// </summary>
    private static void EmitLegacyUnitCapture(X86 c, uint scratch)
    {
        string skip = "captureSkip";
        c.Emit(0x0F, 0xB6, 0x45, 0x10); // movzx eax, byte [ebp+0x10] preview type
        c.Emit(0x84, 0xC0);             // test al,al
        c.Jcc(0x84, skip);              // je skip
        c.Emit(0x3C, 0x03);             // cmp al,3
        c.Jcc(0x87, skip);              // ja skip
        c.Emit(0x8B, 0x4D, 0xE4);       // mov ecx,[ebp-0x1c] owner unit
        c.Emit(0x89, 0x0D); c.U32(scratch); // mov [scratch],ecx
        c.Mark(skip);
        c.Call(0x00425AD0);
        c.Jump(0x00455377);
    }

    /// <summary>
    /// Entry dispatcher for the native cursor pierce predicate. Managed IDs
    /// (including native IDs the editor changed) are evaluated from the
    /// managed generators; everything else executes the original entry bytes
    /// and continues the native function.
    /// </summary>
    private static void EmitPreviewDispatcher(X86 c, uint trampoline, uint unitPointerVa,
        uint strategyContext, IReadOnlyList<RangeBinding> bindings)
    {
        c.Emit(0x0F, 0xB6, 0x41, 0x61); // movzx eax, byte [ecx+0x61]
        foreach (var binding in bindings)
        {
            c.Emit(0x3C, (byte)binding.Key.Id);
            c.Jcc(0x84, "pv" + binding.Key.Id);
        }
        c.Jump(trampoline);
        foreach (var binding in bindings)
        {
            string id = binding.Key.Id.ToString();
            string prefix = "pv" + id;
            string match = prefix + "_m", nomatch = prefix + "_nm", done = prefix + "_done";
            string directionUnavailable = prefix + "_dir0", basisReady = prefix + "_basis";
            c.Mark(prefix);
            c.Emit(0x55, 0x8B, 0xEC); // push ebp; mov ebp,esp
            c.Emit(0x81, 0xEC); c.U32(0x40); // sub esp,0x40
            c.Emit(0x89, 0x4D, 0xFC); // [ebp-4] = ctx
            // testX / testY = packed argument / 0x30
            c.Emit(0x6A, 0x30, 0x8B, 0x45, 0x08);
            c.Call(0x0040BAD4);
            c.Emit(0x88, 0x45, 0xF8);
            c.Emit(0x6A, 0x30, 0x8B, 0x45, 0x0C);
            c.Call(0x0040BAD4);
            c.Emit(0x88, 0x45, 0xF4);
            if (binding.Center == RangeCenterMode.Self)
            {
                c.Emit(0xA1); c.U32(unitPointerVa); // mov eax,[0x500AC0]
                c.Emit(0x85, 0xC0); c.Jcc(0x84, nomatch);
                c.Emit(0x8B, 0x40, 0x06); // eax = [eax+6], packed map coord
                c.Emit(0x0F, 0xB6, 0xC8); // movzx ecx,al
                c.Emit(0x89, 0x4D, 0xF0); // centre X
                c.Emit(0xC1, 0xE8, 0x08); // shr eax,8
                c.Emit(0x0F, 0xB6, 0xC0); // movzx eax,al
                c.Emit(0x89, 0x45, 0xEC); // centre Y
            }
            else
            {
                c.Emit(0x8B, 0x4D, 0xFC);
                c.Emit(0x0F, 0xB6, 0x41, 0x54); // centre X
                c.Emit(0x0F, 0xB6, 0x51, 0x55); // centre Y
                c.Emit(0x3C, 0xFF); c.Jcc(0x84, nomatch);
                c.Emit(0x80, 0xFA, 0xFF); c.Jcc(0x84, nomatch);
                c.Emit(0x89, 0x45, 0xF0);
                c.Emit(0x89, 0x55, 0xEC);
            }
            var points = ExpandLocal(binding).Where(p => !p.SidePair).ToList();
            if (points.Any(p => p.Directional))
            {
                c.Emit(0xA1); c.U32(unitPointerVa);
                c.Emit(0x85, 0xC0); c.Jcc(0x84, directionUnavailable);
                c.Emit(0x8B, 0x15); c.U32(0x004B4248); // edx = cursor
                c.Emit(0x80, 0xFA, 0xFF); c.Jcc(0x84, directionUnavailable);
                c.Emit(0xFF, 0x70, 0x06); // push [eax+6]
                c.Emit(0x52);             // push edx
                c.Call(0x0042230E);
                c.Emit(0x89, 0x45, 0xE0); // raw direction
                c.Emit(0x0F, 0xBE, 0xC8); // movsx ecx,al
                c.Emit(0x89, 0x4D, 0xE4); // forward X
                c.Emit(0x8B, 0x45, 0xE0);
                c.Emit(0xC1, 0xF8, 0x08); // sar eax,8
                c.Emit(0x0F, 0xBE, 0xC0); // movsx eax,al
                c.Emit(0x89, 0x45, 0xD8); // forward Y
                c.JumpLabel(basisReady);
                c.Mark(directionUnavailable);
                // Strategy previews may not populate the physical actor/cursor
                // globals. Derive the direction from the strategy context's
                // caster (+0x01) and primary target (+0x0C) instead of
                // defaulting to +X.
                string strategyFallback = prefix + "_strategyFallback";
                c.Emit(0xB9); c.U32(strategyContext); // mov ecx,strategyContext
                c.Emit(0x0F, 0xB6, 0x41, 0x0C); // movzx eax,[ecx+0x0C] target id
                c.Emit(0x3C, 0xFF); c.Jcc(0x84, strategyFallback);
                c.Emit(0x0F, 0xB6, 0x51, 0x01); // movzx edx,[ecx+0x01] caster id
                c.Emit(0x80, 0xFA, 0xFF); c.Jcc(0x84, strategyFallback);
                c.Emit(0x89, 0x45, 0xD0); // [ebp-0x30] target id
                c.Emit(0x8B, 0xCA); // mov ecx,edx (caster id)
                c.Call(0x004061F9);
                c.Emit(0x8B, 0x41, 0x06); // eax=[ecx+6] caster coord
                c.Emit(0x89, 0x45, 0xCC); // [ebp-0x34] caster coord
                c.Emit(0x8B, 0x4D, 0xD0); // ecx=target id
                c.Call(0x004061F9);
                c.Emit(0x8B, 0x51, 0x06); // edx=[ecx+6] target coord
                c.Emit(0xFF, 0x75, 0xCC); // push caster coord
                c.Emit(0x52); // push target coord
                c.Call(0x0042230E);
                c.Emit(0x89, 0x45, 0xD0); // raw direction
                c.Emit(0x0F, 0xBE, 0xC8); c.Emit(0x89, 0x4D, 0xE4);
                c.Emit(0x8B, 0x45, 0xD0); c.Emit(0xC1, 0xF8, 0x08);
                c.Emit(0x0F, 0xBE, 0xC0); c.Emit(0x89, 0x45, 0xD8);
                c.JumpLabel(basisReady);
                c.Mark(strategyFallback);
                c.Emit(0xC7, 0x45, 0xE4, 0x01, 0x00, 0x00, 0x00);
                c.Emit(0xC7, 0x45, 0xD8, 0x00, 0x00, 0x00, 0x00);
                c.Mark(basisReady);
                c.Emit(0x8B, 0x4D, 0xE4, 0x0B, 0x4D, 0xD8); // fx | fy
                string basisOk = prefix + "_basisOk";
                c.Jcc(0x85, basisOk);
                c.Emit(0xC7, 0x45, 0xE4, 0x01, 0x00, 0x00, 0x00);
                c.Mark(basisOk);
            }
            int index = 0;
            foreach (var point in points)
            {
                string next = prefix + "_" + index++;
                if (point.Directional)
                {
                    c.Emit(0x8B, 0x45, 0xE4); c.Emit(0x69, 0xC0); c.U32(unchecked((uint)point.X));
                    c.Emit(0x8B, 0x55, 0xD8); c.Emit(0x69, 0xD2); c.U32(unchecked((uint)point.Y));
                    c.Emit(0x29, 0xD0, 0x03, 0x45, 0xF0); // centreX + dx*fx - dy*fy
                    c.Emit(0x89, 0x45, 0xE8);             // x
                    c.Emit(0x8B, 0x45, 0xD8); c.Emit(0x69, 0xC0); c.U32(unchecked((uint)point.X));
                    c.Emit(0x8B, 0x55, 0xE4); c.Emit(0x69, 0xD2); c.U32(unchecked((uint)point.Y));
                    c.Emit(0x01, 0xD0, 0x03, 0x45, 0xEC); // centreY + dx*fy + dy*fx
                    c.Emit(0x89, 0x45, 0xDC);             // y
                    if (binding.ExcludePrimaryTarget) EmitSkipIfComputedAtCentre(c, next);
                    c.Emit(0x8B, 0x45, 0xE8, 0x3A, 0x45, 0xF8); c.Jcc(0x85, next);
                    c.Emit(0x8B, 0x45, 0xDC, 0x3A, 0x45, 0xF4); c.Jcc(0x85, next);
                    c.JumpLabel(match);
                }
                else
                {
                    if (binding.ExcludePrimaryTarget && point.X == 0 && point.Y == 0) continue;
                    c.Emit(0x8B, 0x4D, 0xF0); // ecx = centre X
                    c.Emit(0x8A, 0x45, 0xF8, 0x2A, 0xC1); // al = testX - centreX
                    c.Emit(0x8B, 0x55, 0xEC); // edx = centre Y
                    c.Emit(0x8A, 0x65, 0xF4, 0x2A, 0xE2); // ah = testY - centreY
                    c.Emit(0x3C, (byte)point.X); c.Jcc(0x85, next);
                    c.Emit(0x80, 0xFC, (byte)point.Y); c.Jcc(0x85, next);
                    c.JumpLabel(match);
                }
                c.Mark(next);
            }
            c.JumpLabel(nomatch);
            c.Mark(match);
            c.Emit(0xB8); c.U32(1);
            c.JumpLabel(done);
            c.Mark(nomatch);
            c.Emit(0x31, 0xC0); // xor eax,eax
            c.Mark(done);
            c.Emit(0x8B, 0xE5, 0x5D, 0xC2, 0x08, 0x00); // mov esp,ebp; pop ebp; ret 8
        }
    }

    private static void EmitSkipIfComputedAtCentre(X86 c, string next)
    {
        c.Emit(0x8B, 0x45, 0xE8, 0x3B, 0x45, 0xF0); c.Jcc(0x85, next + "d");
        c.Emit(0x8B, 0x45, 0xDC, 0x3B, 0x45, 0xEC); c.Jcc(0x85, next + "d");
        c.JumpLabel(next);
        c.Mark(next + "d");
    }

    /// <summary>
    /// Routes managed pierce types to the expanded-box path before the native
    /// 1/2/3 size branches run. Non-managed types keep the original decision.
    /// </summary>
    private static void EmitPreviewTypeDispatch(X86 c, IReadOnlyList<RangeBinding> bindings)
    {
        c.Emit(0x8A, 0x45, 0xE8); // al = [ebp-0x18], the pierce type
        foreach (var binding in bindings)
        {
            c.Emit(0x3C, (byte)binding.Key.Id);
            c.Jcc(0x84, "mt" + binding.Key.Id);
        }
        // Native decision: type 0 -> default 1x1, otherwise continue at 0x454575.
        c.Emit(0x84, 0xC0); // test al,al
        c.Jcc(0x84, "moveDefault");
        c.Jump(0x00454575);
        foreach (var binding in bindings)
        {
            c.Mark("mt" + binding.Key.Id);
            c.Emit(0xC6, 0x45, 0xE8, 0xFE); // mov byte [ebp-0x18],0xFE
            c.JumpLabel("moveDefault");
        }
        c.Mark("moveDefault");
        c.Jump(PreviewMoveDefault);
    }

    /// <summary>
    /// The mouse-move path 0x004544D3 chooses its repaint rectangle from the
    /// pierce type. Managed types are routed here with sentinel 0xFE. Target
    /// centred shapes use the union cursor box; self-centred shapes get a
    /// second unit-centred render so they repaint while the mouse moves.
    /// </summary>
    private static void EmitPreviewBoxHandler(X86 c, uint unitPointerVa,
        List<(int MinX, int MaxX, int MinY, int MaxY)?> targetBoxes,
        List<(int MinX, int MaxX, int MinY, int MaxY)?> selfBoxes)
    {
        if (selfBoxes.Count != 0)
        {
            int selfMinX = selfBoxes.Min(box => box!.Value.MinX);
            int selfMaxX = selfBoxes.Max(box => box!.Value.MaxX);
            int selfMinY = selfBoxes.Min(box => box!.Value.MinY);
            int selfMaxY = selfBoxes.Max(box => box!.Value.MaxY);
            EmitUnitBox(c, unitPointerVa, selfMinX, selfMaxX, selfMinY, selfMaxY);
        }
        if (targetBoxes.Count != 0)
        {
            int minX = targetBoxes.Min(box => box!.Value.MinX);
            int maxX = targetBoxes.Max(box => box!.Value.MaxX);
            int minY = targetBoxes.Min(box => box!.Value.MinY);
            int maxY = targetBoxes.Max(box => box!.Value.MaxY);
            c.Emit(0x80, 0x7D, 0xE8, 0xFE); // cmp byte [ebp-0x18],0xFE
            c.Jcc(0x85, "boxDefault");
            c.Emit(0x53); // push ebx; the original function does not use it
            c.Emit(0x8B, 0x4D, 0xEC); // ecx = this
            c.Emit(0x0F, 0xB6, 0x41, 0x54); // eax = centre X
            c.Emit(0x0F, 0xB6, 0x51, 0x55); // edx = centre Y
            EmitClampedBox(c, "box", minX, maxX, minY, maxY);
            c.Emit(0x5B); // pop ebx
            c.Jump(PreviewMoveResume);
        }
        c.Mark("boxDefault");
        c.Emit(0xC6, 0x45, 0xF0, 0x01); // mov byte [ebp-0x10],1 (height)
        c.Emit(0xC6, 0x45, 0xF4, 0x01); // mov byte [ebp-0xc],1 (width)
        c.Jump(PreviewMoveResume);
    }

    /// <summary>
    /// Repaints the self-centred shape around the acting unit. The predicate
    /// still filters by the current type, so this only adds repaint area; it
    /// does not draw cells outside the managed shape.
    /// </summary>
    private static void EmitUnitBox(X86 c, uint unitPointerVa, int minX, int maxX, int minY, int maxY)
    {
        string skip = "unitBoxSkip";
        c.Emit(0x80, 0x7D, 0xE8, 0xFE); // only managed sentinel
        c.Jcc(0x85, skip);
        c.Emit(0x8B, 0x0D); c.U32(unitPointerVa); // mov ecx,[unitPtr]
        c.Emit(0x85, 0xC9); c.Jcc(0x84, skip); // test ecx,ecx
        c.Emit(0x0F, 0xB6, 0x41, 0x06); // movzx eax,[ecx+6] unit map X
        c.Emit(0x0F, 0xB6, 0x51, 0x07); // movzx edx,[ecx+7] unit map Y
        EmitClampedRect(c, "unit", minX, maxX, minY, maxY);
        c.Emit(0x56); // push esi
        c.Emit(0x57); // push edi
        c.Emit(0x53); // push height
        c.Emit(0x51); // push width
        c.Emit(0x52); // push Y
        c.Emit(0x50); // push X
        c.Emit(0x8B, 0x4D, 0xEC); // mov ecx,[ebp-0x14] this
        c.Call(0x0045218F);
        c.Emit(0x5F); // pop edi
        c.Emit(0x5E); // pop esi
        c.Emit(0x5B); // pop ebx (saved by EmitClampedRect)
        c.Mark(skip);
    }

    /// <summary>
    /// Computes start/size into EAX(startX), EDX(startY), ECX(width),
    /// EBX(height), clamped to the 0x30-cell map. Saves EBX on the stack;
    /// the caller must pop it after the render call.
    /// </summary>
    private static void EmitClampedRect(X86 c, string tag, int minX, int maxX, int minY, int maxY)
    {
        c.Emit(0x53); // push ebx (callee-saved)
        c.Emit(0x83, 0xC0, (byte)(sbyte)minX);
        c.Jcc(0x89, tag + "x0");
        c.Emit(0x31, 0xC0);
        c.Mark(tag + "x0");
        c.Emit(0x83, 0xF8, 0x2F);
        c.Jcc(0x8E, tag + "x1");
        c.Emit(0xB8); c.U32(0x2F);
        c.Mark(tag + "x1");
        c.Emit(0x83, 0xC2, (byte)(sbyte)minY);
        c.Jcc(0x89, tag + "y0");
        c.Emit(0x31, 0xD2);
        c.Mark(tag + "y0");
        c.Emit(0x83, 0xFA, 0x2F);
        c.Jcc(0x8E, tag + "y1");
        c.Emit(0xBA); c.U32(0x2F);
        c.Mark(tag + "y1");
        c.Emit(0xB9); c.U32((uint)(maxX - minX + 1)); // ecx = width
        c.Emit(0xBB); c.U32(0x30);
        c.Emit(0x2B, 0xD8); // ebx = 0x30 - x
        c.Emit(0x3B, 0xCB); // cmp ecx,ebx
        c.Jcc(0x8E, tag + "w");
        c.Emit(0x8B, 0xCB); // mov ecx,ebx
        c.Mark(tag + "w");
        c.Emit(0x51); // push width
        c.Emit(0xBB); c.U32((uint)(maxY - minY + 1)); // ebx = height
        c.Emit(0xB9); c.U32(0x30);
        c.Emit(0x2B, 0xCA); // ecx = 0x30 - y
        c.Emit(0x3B, 0xD9); // cmp ebx,ecx
        c.Jcc(0x8E, tag + "h");
        c.Emit(0x8B, 0xD9); // mov ebx,ecx
        c.Mark(tag + "h");
        c.Emit(0x59); // pop ecx (width)
    }

    /// <summary>
    /// Bounding box for the mouse-move repaint. Mirror points use their real
    /// extents; directional points can rotate to any of eight headings, so a
    /// square with radius max(|dx|+|dy|) safely covers every orientation.
    /// Returns null when the binding has no managed preview points.
    /// </summary>
    private static (int MinX, int MaxX, int MinY, int MaxY)? PreviewBounds(RangeBinding binding)
    {
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var point in ExpandLocal(binding).Where(p => !p.SidePair))
        {
            if (point.Directional)
            {
                int extent = Math.Abs(point.X) + Math.Abs(point.Y);
                minX = Math.Min(minX, -extent); maxX = Math.Max(maxX, extent);
                minY = Math.Min(minY, -extent); maxY = Math.Max(maxY, extent);
            }
            else
            {
                minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
            }
        }
        return minX == int.MaxValue ? null : (minX, maxX, minY, maxY);
    }

    /// <summary>
    /// Computes start=(centre+min), size=(max-min+1), clamped to the 0x30-cell
    /// screen. EAX/EDX hold the centre on entry; EBX is scratch.
    /// </summary>
    private static void EmitClampedBox(X86 c, string tag, int minX, int maxX, int minY, int maxY)
    {
        c.Emit(0x83, 0xC0, (byte)(sbyte)minX);
        c.Jcc(0x89, tag + "x0");
        c.Emit(0x31, 0xC0); // xor eax,eax
        c.Mark(tag + "x0");
        c.Emit(0x83, 0xF8, 0x2F);
        c.Jcc(0x8E, tag + "x1");
        c.Emit(0xB8); c.U32(0x2F);
        c.Mark(tag + "x1");
        c.Emit(0x83, 0xC2, (byte)(sbyte)minY);
        c.Jcc(0x89, tag + "y0");
        c.Emit(0x31, 0xD2); // xor edx,edx
        c.Mark(tag + "y0");
        c.Emit(0x83, 0xFA, 0x2F);
        c.Jcc(0x8E, tag + "y1");
        c.Emit(0xBA); c.U32(0x2F);
        c.Mark(tag + "y1");
        c.Emit(0xB9); c.U32((uint)(maxX - minX + 1)); // ecx = width
        c.Emit(0xBB); c.U32(0x30);
        c.Emit(0x2B, 0xD8); // ebx = 0x30 - x
        c.Emit(0x3B, 0xCB); // cmp ecx,ebx
        c.Jcc(0x8E, tag + "w");
        c.Emit(0x8B, 0xCB); // mov ecx,ebx
        c.Mark(tag + "w");
        c.Emit(0x88, 0x4D, 0xF4); // width -> [ebp-0xc]
        c.Emit(0xB9); c.U32((uint)(maxY - minY + 1));
        c.Emit(0xBB); c.U32(0x30);
        c.Emit(0x2B, 0xDA); // ebx = 0x30 - y
        c.Emit(0x3B, 0xCB);
        c.Jcc(0x8E, tag + "h");
        c.Emit(0x8B, 0xCB);
        c.Mark(tag + "h");
        c.Emit(0x88, 0x4D, 0xF0); // height -> [ebp-0x10]
        c.Emit(0x88, 0x45, 0xF8); // start X -> [ebp-8]
        c.Emit(0x88, 0x55, 0xFC); // start Y -> [ebp-4]
    }

    private static void EmitPierceHandler(X86 c, EngineVersion version, RangeBinding binding,
        uint traceVa, int traceCapacity)
    {
        bool v66 = version == EngineVersion.V66;
        // 24 points x 8 bytes = 192-byte dedup array. Keep it inside a
        // 0x120-byte frame so it cannot overwrite the saved EBP/return.
        c.Emit(0x55, 0x8B, 0xEC, 0x81, 0xEC); c.U32(0x120); c.Emit(0x89, 0x4D, 0xFC); // frame; owner
        c.Emit(0x8B, 0x45, 0x10, 0x89, 0x45, 0xF8); // output cursor
        c.Emit(0x31, 0xC0, 0x89, 0x45, 0xF4); // target counter
        c.Emit(0x89, 0x85); c.U32(unchecked((uint)-284)); // independent seen-coordinate count
        if (traceVa != 0)
        {
            // Keep the caller cursor and the entry target count for the trace
            // record written at the end of this handler.
            c.Emit(0x0F, 0xB6, 0x05); c.U32(0x00500E7C); c.Emit(0x89, 0x45, 0xC8);
            c.Emit(0x8B, 0x45, 0xF4); c.Emit(0x89, 0x45, 0xCC);
        }
        string invalid = "invalid" + binding.Key.Id;
        // The native dispatcher only bounds the coordinate cursor; it never
        // requires an even value. 0x004400FA seeds byte[0x500E7C] from the
        // effect-class table 0x004D2110, whose values include 1, so an even
        // guard would turn those preview/effect calls into an empty list.
        // A cursor above 50 is clamped rather than clearing the whole call:
        // repeated preview passes can leave a stale cursor behind.
        string cursorOk = "cursorOk" + binding.Key.Id;
        c.Emit(0x0F, 0xB6, 0x15); c.U32(0x00500E7C);
        c.Emit(0x83, 0xFA, 50); c.Jcc(0x86, cursorOk);
        c.Emit(0xC6, 0x05); c.U32(0x00500E7C); c.Emit(50);
        c.Mark(cursorOk);
        c.Emit(0x8B, 0x45, 0x08, 0x89, 0x45, 0xDC); // original primary coordinate
        if (binding.Center == RangeCenterMode.Self) c.Emit(0x8B, 0x41, 0x06);
        c.Emit(0x0F, 0xB6, 0xD0, 0x89, 0x55, 0xEC, 0x0F, 0xB6, 0xD4, 0x89, 0x55, 0xE8);
        // Direction basis is calculated from the same centre/reference pair as the original dispatcher.
        c.Emit(0x8B, 0x4D, 0xFC);
        if (v66) c.Emit(0xFF, 0x71, 0x06);
        else
        {
            string x = "refx" + binding.Key.Id, y = "refy" + binding.Key.Id;
            c.Emit(0x8B, 0x45, 0x18, 0x3C, 0xFF); c.Jcc(0x85, x);
            c.Emit(0x8A, 0x41, 0x06); c.Mark(x);
            c.Emit(0x8B, 0x55, 0x1C, 0x80, 0xFA, 0xFF); c.Jcc(0x85, y);
            c.Emit(0x8A, 0x51, 0x07); c.Mark(y);
            c.Emit(0x88, 0xD4, 0x50);
        }
        // The direction helper always receives the original primary target as
        // its first packed coordinate.  A self-centred binding changes the
        // emission origin only; using the self slot here reverses or collapses
        // directional ranges when the owner and target are distinct.
        if (binding.Center == RangeCenterMode.Self)
            c.Emit(0x8B, 0x45, 0xDC, 0x8A, 0x65, 0xE8, 0x50);
        else
            c.Emit(0x8B, 0x45, 0xEC, 0x8A, 0x65, 0xE8, 0x50);
        c.Call(0x0042230E);
        c.Emit(0x0F, 0xBE, 0xD0, 0x89, 0x55, 0xE4, 0x0F, 0xBE, 0xD4, 0x89, 0x55, 0xE0);
        string done = "done" + binding.Key.Id;
        int index = 0;
        foreach (var p in ExpandLocal(binding))
        {
            string skip = "skip" + binding.Key.Id + "_" + index++;
            c.Emit(0x80, 0x3D); c.U32(0x00500E7C); c.Emit(50); c.Jcc(0x83, done);
            if (p.Directional)
            {
                string basis = skip + "basis";
                if (p.SidePair)
                {
                    // 4254FA orders its two sides by the undirected line,
                    // rather than rotating the first side with the heading.
                    // Vertical and coincident pairs both use (-1,0) first in
                    // 6.4/6.5; 6.6's opposite local side sign reverses them.
                    c.Emit(0xFF, 0x75, 0xE4, 0xFF, 0x75, 0xE0);
                    c.Emit(0x83, 0x7D, 0xE4, 0x00); c.Jcc(0x85, basis);
                    c.Emit(0xC7, 0x45, 0xE0, 0xFF, 0xFF, 0xFF, 0xFF);
                    c.JumpLabel(basis + "ready");
                    c.Mark(basis);
                    c.Emit(0x8B, 0x45, 0xE4, 0x0F, 0xAF, 0x45, 0xE0, 0x89, 0x45, 0xE0);
                    c.Emit(0xC7, 0x45, 0xE4, 0x01, 0x00, 0x00, 0x00);
                    c.Mark(basis + "ready");
                }
                // x = centreX + dx*forwardX - dy*forwardY
                c.Emit(0x8B, 0x45, 0xE4, 0x69, 0xC0); c.U32(unchecked((uint)p.X));
                c.Emit(0x8B, 0x55, 0xE0, 0x69, 0xD2); c.U32(unchecked((uint)p.Y));
                c.Emit(0x29, 0xD0, 0x03, 0x45, 0xEC, 0x89, 0x45, 0xD8);
                c.Emit(0x8B, 0x45, 0xE0, 0x69, 0xC0); c.U32(unchecked((uint)p.X));
                c.Emit(0x8B, 0x55, 0xE4, 0x69, 0xD2); c.U32(unchecked((uint)p.Y));
                c.Emit(0x01, 0xD0, 0x03, 0x45, 0xE8, 0x89, 0x45, 0xD4);
                if (p.SidePair)
                {
                    c.Emit(0x58, 0x89, 0x45, 0xE0, 0x58, 0x89, 0x45, 0xE4);
                }
            }
            else
            {
                c.Emit(0x8B, 0x45, 0xEC, 0x05); c.U32(unchecked((uint)p.X)); c.Emit(0x89, 0x45, 0xD8);
                c.Emit(0x8B, 0x45, 0xE8, 0x05); c.U32(unchecked((uint)p.Y)); c.Emit(0x89, 0x45, 0xD4);
            }
            // Reject signed overflow before the native byte-coordinate helpers can wrap it.
            foreach (byte local in new byte[] { 0xD8, 0xD4 })
            {
                c.Emit(0x83, 0x7D, local, 0x00); c.Jcc(0x8C, skip);
                c.Emit(0x81, 0x7D, local); c.U32(255); c.Jcc(0x8F, skip);
            }
            if (binding.ExcludePrimaryTarget)
            {
                c.Emit(0x8B, 0x45, 0xD8, 0x8A, 0x65, 0xD4, 0x66, 0x3B, 0x45, 0xDC); c.Jcc(0x84, skip);
            }
            string seenLoop = skip + "seen", unseen = skip + "new";
            string dedupDone = skip + "dedupDone";
            if (p.Directional && !p.SidePair)
            {
                // The original ray advances by its (possibly zero) heading.
                // A coincident centre therefore appends the same point once
                // for each step, retaining the native ordinal attenuation.
                c.Emit(0x8B, 0x45, 0xE4, 0x0B, 0x45, 0xE0); c.Jcc(0x84, dedupDone);
            }
            c.Emit(0x8B, 0x45, 0xD8, 0x8A, 0x65, 0xD4, 0x31, 0xD2);
            c.Mark(seenLoop);
            c.Emit(0x3B, 0x95); c.U32(unchecked((uint)-284)); c.Jcc(0x83, unseen);
            c.Emit(0x3B, 0x84, 0x95); c.U32(unchecked((uint)-280)); c.Jcc(0x84, skip);
            c.Emit(0x42); c.JumpLabel(seenLoop);
            c.Mark(unseen);
            c.Emit(0x89, 0x84, 0x95); c.U32(unchecked((uint)-280));
            c.Emit(0xFF, 0x85); c.U32(unchecked((uint)-284));
            c.Mark(dedupDone);
            c.Emit(0x8B, 0x45, 0xF4, 0x89, 0x45, 0xD0);
            c.Emit(0xFF, 0x75, 0x14, 0xFF, 0x75, 0xF8, 0xFF, 0x75, 0xD4, 0xFF, 0x75, 0xD8);
            c.Emit(0xB8); c.U32((uint)p.Rank);
            c.Emit(0x8B, 0x55, 0xF4, 0x8B, 0x4D, 0xFC); c.Call(v66 ? 0x00435C10u : 0x00435BF2u);
            c.Emit(0x89, 0x45, 0xF8, 0x89, 0x4D, 0xF4);
            if (p.Constant.HasValue)
            {
                c.Emit(0x3B, 0x4D, 0xD0); c.Jcc(0x86, skip);
                c.Emit(0x8B, 0x55, 0xD0, 0xC6, 0x82); c.U32(0x00500E7D); c.Emit((byte)p.Constant.Value);
            }
            c.Mark(skip);
        }
        c.Mark(done);
        if (traceVa != 0)
        {
            string noTrace = "notrace" + binding.Key.Id;
            c.Emit(0x50, 0x51, 0x52); // push eax/ecx/edx
            c.Emit(0xA1); c.U32(traceVa + 4); // eax = count
            c.Emit(0x3D); c.U32((uint)traceCapacity); c.Jcc(0x83, noTrace);
            c.Emit(0x8B, 0xC8, 0xC1, 0xE1, 0x05); // ecx = count * 32
            c.Emit(0x81, 0xC1); c.U32(traceVa + 16);
            c.Emit(0x8B, 0x45, 0x04, 0x89, 0x01); // caller
            c.Emit(0x0F, 0xB6, 0x45, 0x0C, 0x89, 0x41, 0x04); // code
            c.Emit(0x8B, 0x45, 0xC8, 0x89, 0x41, 0x08); // cursor
            c.Emit(0x8B, 0x45, 0x08, 0x89, 0x41, 0x0C); // centre
            c.Emit(0x8B, 0x45, 0xFC, 0x89, 0x41, 0x10); // owner
            c.Emit(0x8B, 0x45, 0x14, 0x89, 0x41, 0x14); // filter
            c.Emit(0xC7, 0x41, 0x18); c.U32((uint)ExpandLocal(binding).Count);
            c.Emit(0x8B, 0x45, 0xF4, 0x2B, 0x45, 0xCC, 0x89, 0x41, 0x1C); // appended
            c.Emit(0xFF, 0x05); c.U32(traceVa + 4);
            c.Mark(noTrace);
            c.Emit(0x5A, 0x59, 0x58); // pop edx/ecx/eax
        }
        c.Emit(0x8B, 0x45, 0xF8, 0xC6, 0x00, 0xFF);
        c.Emit(0x0F, 0xB6, 0x0D); c.U32(0x00500E7C);
        c.Emit(0xC6, 0x81); c.U32(0x00500E49); c.Emit(0xFF);
        c.Emit(0xC6, 0x05); c.U32(0x00500E7C); c.Emit((byte)(binding.RuntimeCacheId >= 0 ? binding.RuntimeCacheId : binding.Key.Id));
        c.Emit(0x8B, 0x45, 0x10, 0x8B, 0xE5, 0x5D, 0xC2, (byte)(v66 ? 0x14 : 0x18), 0x00);
        c.Mark(invalid);
        c.Emit(0x8B, 0x45, 0x10, 0xC6, 0x00, 0xFF, 0x8B, 0xE5, 0x5D, 0xC2, (byte)(v66 ? 0x14 : 0x18), 0x00);
    }

    private static void ValidateNativeContracts(EngineVersion version, PeFile pe)
    {
        if (!pe.Match(PierceEntry, 0x55, 0x8B, 0xEC, 0x51, 0x33, 0xC0))
            throw new InvalidDataException("穿透入口已有未知补丁，拒绝覆盖");
        uint upper = version == EngineVersion.V66 ? 0x00436579u : 0x0043657Eu;
        if (!pe.Match(upper, 0x3C, 0x25) || !pe.Match(upper + 4, 0x3C, 0x0A))
            throw new InvalidDataException("攻击编号上限契约不匹配");
        uint leaf = version == EngineVersion.V66 ? 0x00435C10u : 0x00435BF2u;
        byte[] signature = version == EngineVersion.V66
            ? [0x55, 0x8B, 0xEC, 0x52, 0x50, 0x51, 0x68, 0x7D, 0x0E, 0x50, 0x00]
            : [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x24, 0x56, 0x89, 0x4D, 0xF4];
        if (!pe.Match(leaf, signature) || !pe.Match(0x004174FF, 0x55, 0x8B, 0xEC, 0x8B, 0x55, 0x08))
            throw new InvalidDataException("穿透落格或坐标写入契约不匹配");
    }

    private static void InstallHook(byte[] image, PeFile pe, RuntimeState state, uint address, uint target, int length)
    {
        int offset = pe.VaToOffset(address);
        byte[] original = image.AsSpan(offset, length).ToArray();
        byte[] installed = Enumerable.Repeat((byte)0x90, length).ToArray();
        installed[0] = 0xE9;
        Put32(installed, 1, unchecked(target - address - 5));
        installed.CopyTo(image, offset);
        state.Hooks.Add(new Hook { Address = address, Original = original, Installed = installed });
    }

    private static Existing? ReadExisting(PeFile exe)
    {
        Layout l = ReadLayout(exe.Bytes);
        int end = checked((int)(l.Raw + l.RawSize));
        if (end < FooterSize || end > exe.Bytes.Length) throw new InvalidDataException("PE末节边界无效");
        int footer = end - FooterSize;
        bool hasManagedMapping = exe.Bytes.AsSpan(footer, 8).SequenceEqual(MappingMagic);
        if (!hasManagedMapping && !exe.Bytes.AsSpan(footer, 8).SequenceEqual(Magic)) return null;
        if (U32(exe.Bytes, footer + 8) != 2) throw new InvalidDataException("不支持的范围扩展版本");
        int start = checked((int)(l.Raw + U32(exe.Bytes, footer + 12)));
        int metadata = checked((int)(l.Raw + U32(exe.Bytes, footer + 16)));
        int length = checked((int)U32(exe.Bytes, footer + 20));
        if (start < l.Raw || metadata < start || length <= 0 || metadata + length > footer)
            throw new InvalidDataException("范围扩展目录损坏");
        if (!SHA256.HashData(exe.Bytes.AsSpan(start, footer - start)).AsSpan().SequenceEqual(exe.Bytes.AsSpan(footer + 24, 32)))
            throw new InvalidDataException("范围扩展区完整性校验失败");
        RuntimeState state = JsonSerializer.Deserialize<RuntimeState>(exe.Bytes.AsSpan(metadata, length), Json)
            ?? throw new InvalidDataException("范围扩展元数据为空");
        if (state.Format != 2 || state.OriginalSectionHeader.Length != 40) throw new InvalidDataException("范围扩展头损坏");
        int originalEnd = checked((int)(l.Raw + U32(state.OriginalSectionHeader, 16)));
        if (U32(state.OriginalSectionHeader, 20) != l.Raw || U32(state.OriginalSectionHeader, 12) != l.Rva ||
            originalEnd > start)
            throw new InvalidDataException("范围扩展记录的原始 PE 节边界不匹配");
        if (hasManagedMapping && (state.Mapping == null || state.Mapping.Format != 2))
            throw new InvalidDataException("CCZRNG03 缺少 v2 映射元数据");
        if (hasManagedMapping && (state.Mapping!.Values.Length != RangeExtensionMapping.ValueCount || state.Mapping.Values[0xFF] != 0xFF || state.Mapping.Values.Take(0xFF).Any(v => v == 0xFF)))
            throw new InvalidDataException("CCZRNG03 映射元数据长度或哨兵值无效");
        foreach (var hook in state.Hooks)
            if (hook.Original.Length == 0 || hook.Original.Length != hook.Installed.Length ||
                !exe.Match(hook.Address, hook.Installed))
                throw new InvalidDataException($"范围入口0x{hook.Address:X8}已被其他修改覆盖");
        if (hasManagedMapping && state.Mapping is { HookAddress: var mapHook } &&
            !state.Hooks.Any(h => h.Address == mapHook))
            throw new InvalidDataException("CCZRNG03 映射钩子未登记");
        if (hasManagedMapping && state.Mapping is { TableAddress: var mapTable, Values: var mapValues })
        {
            if (!exe.TryVaToOffset(mapTable, out int mapOffset) ||
                mapOffset < start || mapOffset + mapValues.Length > footer ||
                !exe.Bytes.AsSpan(mapOffset, mapValues.Length).SequenceEqual(mapValues))
                throw new InvalidDataException("CCZRNG03 映射表载荷与元数据不一致");
        }
        foreach (var binding in state.Bindings.Where(IsTableDriven))
        {
            foreach (uint slot in binding.PointerSlots)
                if (!exe.TryVaToOffset(slot, out _) || exe.ReadU32(slot) != binding.NativePointer)
                    throw new InvalidDataException($"{binding.Key} 的原生指针槽0x{slot:X8}已被其他修改覆盖");
            byte[] expected = RangeShapeTable.Serialize(binding.Definition.Groups.SelectMany(g => g.Generators).ToList());
            // A centre-only change deliberately retains its original mapped
            // shared segment. Both that native segment and an owned payload
            // are valid, while expansion padding and overlays remain excluded.
            if (!exe.TryVaToOffset(binding.NativePointer, out int offset) ||
                !FitsMappedSection(exe, binding.NativePointer, offset, expected.Length) ||
                !((long)offset + expected.Length <= originalEnd ||
                  offset >= start && (long)offset + expected.Length <= footer) ||
                !exe.Bytes.AsSpan(offset, expected.Length).SequenceEqual(expected))
                throw new InvalidDataException($"{binding.Key} 的真实生成元与扩展目录不一致");
        }
        return new Existing(state, start, end, l);
    }

    private static byte[] RemoveOldBlock(PeFile exe, Existing? old)
    {
        if (old == null) return (byte[])exe.Bytes.Clone();
        var s = old.State; var l = old.Layout;
        uint originalRaw = U32(s.OriginalSectionHeader, 16);
        int originalEnd = checked((int)(l.Raw + originalRaw));
        byte[] result = new byte[checked(originalEnd + exe.Bytes.Length - old.End)];
        exe.Bytes.AsSpan(0, originalEnd).CopyTo(result);
        exe.Bytes.AsSpan(old.End).CopyTo(result.AsSpan(originalEnd));
        s.OriginalSectionHeader.CopyTo(result, l.LastHeader);
        Put32(result, l.Optional + 56, s.OriginalSizeOfImage);
        Put32(result, l.Optional + 4, s.OriginalSizeOfCode);
        Put32(result, l.Optional + 8, s.OriginalInitializedSize);
        ShiftOverlayOffsets(result, l, old.End, originalEnd - old.End);
        foreach (var hook in s.Hooks) hook.Original.CopyTo(result, exe.VaToOffset(hook.Address));
        return result;
    }

    private static Layout ReadLayout(byte[] image)
    {
        int pe = checked((int)U32(image, 0x3C)); int op = pe + 24;
        if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(op)) != 0x10B)
            throw new InvalidDataException("范围扩展只支持PE32");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
        int section = op + BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
        int last = section + (count - 1) * 40;
        uint raw = U32(image, last + 20), size = U32(image, last + 16), rva = U32(image, last + 12);
        for (int i = 0; i < count - 1; i++)
        {
            int at = section + i * 40;
            if (U32(image, at + 12) >= rva || U32(image, at + 20) + U32(image, at + 16) > raw)
                throw new InvalidDataException("PE末节不是最高文件/虚拟节，不能安全扩展");
        }
        // File-pointer debug entries require an independent relocation contract.
        if (U32(image, op + 96 + 6 * 8) != 0 && U32(image, op + 100 + 6 * 8) != 0)
            throw new InvalidDataException("该PE含调试目录，尚未验证扩展时的文件偏移重定位");
        return new Layout(pe, op, last, raw, size, rva, U32(image, last + 8), U32(image, last + 36),
            U32(image, op + 28), U32(image, op + 36), U32(image, op + 32));
    }

    private static void ShiftOverlayOffsets(byte[] image, Layout l, int boundary, int shift)
    {
        foreach (int at in new[] { l.Optional + 128, l.Pe + 12 })
        {
            uint value = U32(image, at);
            if (value != 0 && value >= boundary) Put32(image, at, checked((uint)(value + shift)));
        }
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    private static uint Align(uint value, uint alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static void AlignList(List<byte> list, int alignment) { while (list.Count % alignment != 0) list.Add(0); }

    private sealed class X86(uint address)
    {
        private readonly List<byte> code = [];
        private readonly Dictionary<string, int> labels = [];
        private readonly List<(int At, string Label)> fixups = [];
        public void Emit(params byte[] bytes) => code.AddRange(bytes);
        public void U32(uint value) => code.AddRange(BitConverter.GetBytes(value));
        public void Mark(string label) => labels.Add(label, code.Count);
        public void Call(uint target) { Emit(0xE8); Relative(target); }
        public void Jump(uint target) { Emit(0xE9); Relative(target); }
        public void JccAddress(byte condition, uint target) { Emit(0x0F, condition); Relative(target); }
        public void Jcc(byte condition, string label) { Emit(0x0F, condition); fixups.Add((code.Count, label)); U32(0); }
        public void JumpLabel(string label) { Emit(0xE9); fixups.Add((code.Count, label)); U32(0); }
        private void Relative(uint target) => U32(unchecked(target - (address + (uint)code.Count + 4)));
        public byte[] Finish()
        {
            byte[] result = code.ToArray();
            foreach (var f in fixups) Put32(result, f.At, unchecked((uint)(labels[f.Label] - f.At - 4)));
            return result;
        }
    }
}
