﻿using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.History;
using DataManager.Core.Models;

namespace DataManager.Core.Scanner;

/// <summary>
/// Builds a complete, baseline-checked byte preview and commits it through the
/// shared workspace transaction.  The service never chooses offsets by itself:
/// every patch comes from a call site or slot recovered by the scan service.
/// </summary>
public sealed class EngineEffectWriteService
{
    private readonly EngineEffectSlotService _slots = new();

    public EngineEffectWritePreview BuildPreview(ProjectWorkspace workspace, EngineEffectScanReport report,
        EngineEffectWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        var changes = new List<EngineEffectWriteChange>();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (workspace.IsDirty) errors.Add("存在未保存的数据草稿，请先保存或撤销。");
        var callOnly = request.EffectId < 0;
        if (callOnly && (request.NewName != null || request.NewSuffix != null || request.NewDescription != null))
            errors.Add("调用点级写入不能同时修改名称、后缀或说明。");
        EngineEffectScanRow? row = null;
        if (!callOnly)
        {
            row = report.Effects.FirstOrDefault(item => item.EffectId == request.EffectId);
            if (row == null) errors.Add($"扫描报告没有特效 {request.EffectId}。");
        }

        ReadCurrent(report.ExePath, report.ExeSha256, "Ekd5.exe", errors, files);
        byte[]? imsgBytes = null;
        if (!callOnly && request.NewDescription != null)
            imsgBytes = ReadCurrent(report.ImsgPath, report.ImsgSha256, "Imsg.e5", errors, files);

        if (!callOnly && (request.NewName != null || request.NewSuffix != null))
        {
            if (row is not { HasNameSlot: true })
                errors.Add("当前引擎没有此名称槽，名称/后缀只读。");
            else
                AddNameChange(workspace, request, row, changes, errors);
        }

        if (!callOnly && request.NewDescription != null)
        {
            if (row is not { HasDescriptionSlot: true }) errors.Add("当前引擎没有此说明槽，说明只读。");
            else if (imsgBytes != null) AddDescriptionChange(workspace, request, changes, errors);
        }

        foreach (var edit in request.NewCalls.Values.Concat(request.CallEdits))
            AddCallChanges(report, request.EffectId, edit, changes, errors);

        changes = changes.DistinctBy(change => (Path.GetFullPath(change.FilePath).ToUpperInvariant(), change.FileOffset,
            Convert.ToHexString(change.BeforeBytes), Convert.ToHexString(change.AfterBytes))).ToList();
        if (changes.Count == 0 && errors.Count == 0) errors.Add("没有需要写入的修改。");
        foreach (var change in changes)
        {
            if (!files.TryGetValue(change.FilePath, out var current)) continue;
            if (change.FileOffset < 0 || change.FileOffset + change.BeforeBytes.Length > current.LongLength)
            {
                errors.Add($"{change.DisplayName} 的写入偏移超出 {Path.GetFileName(change.FilePath)}。");
                continue;
            }
            var actual = current.AsSpan(checked((int)change.FileOffset), change.BeforeBytes.Length).ToArray();
            if (!actual.AsSpan().SequenceEqual(change.BeforeBytes))
                errors.Add($"{change.DisplayName} 的原字节已被外部修改，请重新扫描。");
        }
        if (errors.Count == 0) errors.AddRange(FindOverlaps(changes));

        var baselinePaths = workspace.Documents.Values
            .Select(document => Path.GetFullPath(document.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<WorkspaceBinaryCandidate>();
        foreach (var group in changes.GroupBy(change => change.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!files.TryGetValue(group.Key, out var before)) continue;
            if (!baselinePaths.Contains(Path.GetFullPath(group.Key)))
                errors.Add($"{Path.GetFileName(group.Key)} 没有工作区基线，不能写入。");
            var after = (byte[])before.Clone();
            foreach (var change in group) change.AfterBytes.CopyTo(after, checked((int)change.FileOffset));
            if (group.Key.Equals(report.ExePath, StringComparison.OrdinalIgnoreCase) &&
                !request.DeferEquipmentFfGuard &&
                request.NewCalls.Values.Concat(request.CallEdits).Any(edit => edit.ItemEffectId == 255) && errors.Count == 0)
            {
                try
                {
                    var native = NativeEffectAdapter.Read(after);
                    if (!native.IsSupported)
                        throw new InvalidDataException("当前引擎无法验证装备255未命中适配：" + string.Join("；", native.Diagnostics));
                    var adapted = NativeEffectAdapter.Build(after, native.HpCurrentSelector, native.MpCurrentSelector, true).After;
                    for (var offset = 0; offset < adapted.Length;)
                    {
                        if (offset < after.Length && after[offset] == adapted[offset]) { offset++; continue; }
                        var start = offset++;
                        while (offset < adapted.Length && (offset >= after.Length || after[offset] != adapted[offset])) offset++;
                        changes.Add(new EngineEffectWriteChange { EffectId = request.EffectId, FilePath = group.Key,
                            FileOffset = start, BeforeBytes = after.Skip(start).Take(offset - start).ToArray(),
                            AfterBytes = adapted[start..offset], Kind = EngineEffectWriteChangeKind.NativeAdapter,
                            DisplayName = "装备255未命中适配（专用PE节及调用守卫）" });
                    }
                    after = adapted;
                }
                catch (InvalidDataException ex) { errors.Add(ex.Message); }
            }
            candidates.Add(new WorkspaceBinaryCandidate(group.Key, before, after, "引擎扫描"));
        }

        var follow = callOnly ? null : FollowEffectId(request, row);
        var summary = callOnly
            ? $"写入调用点：{changes.Count} 项字节补丁"
            : $"写入引擎特效 {request.EffectId}：{changes.Count} 项字节补丁";
        return new EngineEffectWritePreview
        {
            EffectId = request.EffectId,
            FollowEffectId = follow,
            Changes = changes,
            Candidates = candidates,
            Errors = errors,
            CanWrite = errors.Count == 0 && changes.Count > 0,
            Summary = summary
        };
    }

    public EngineEffectWriteResult Commit(ProjectWorkspace workspace, EngineEffectWritePreview preview)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.CanWrite)
            return Failure(preview, string.Join(Environment.NewLine, preview.Errors.DefaultIfEmpty("没有可写入的修改。")));
        if (IsGameRunning(workspace))
            return Failure(preview, "游戏正在运行，请先完全退出游戏再写入引擎。");

        EngineEffectWriteLock? writeLock = null;
        try
        {
            writeLock = EngineEffectWriteLock.Acquire(workspace.RootPath);
            var backup = CreateBackup(workspace.RootPath, preview.Candidates, out var backupError);
            if (backup == null) return Failure(preview, backupError ?? "写前备份失败。");
            var result = new WorkspaceBinaryTransaction().Commit(workspace, preview.Summary, preview.Candidates,
                () => Verify(workspace, preview));
            if (!result.Saved)
                return Failure(preview, string.Join(Environment.NewLine,
                    result.Messages.Append($"写前备份：{backup}")));
            return new EngineEffectWriteResult
            {
                Success = true,
                AppliedChanges = preview.Changes.Count,
                FollowEffectId = preview.FollowEffectId,
                History = result.History,
                BackupPath = backup,
                Message = $"已写入 {preview.Changes.Count} 项；备份：{backup}"
            };
        }
        catch (Exception ex)
        {
            return Failure(preview, $"引擎写入失败：{ex.Message}");
        }
        finally
        {
            writeLock?.Dispose();
        }
    }

    private void AddNameChange(ProjectWorkspace workspace, EngineEffectWriteRequest request, EngineEffectScanRow row,
        List<EngineEffectWriteChange> changes, List<string> errors)
    {
        var before = _slots.ReadNameSlot(workspace, request.EffectId);
        if (!before.Exists)
        {
            errors.Add(before.Error ?? "名称槽读取失败。");
            return;
        }
        var after = before.RawBytes;
        if (request.NewName != null)
        {
            var edit = _slots.PreviewName(workspace, request.EffectId, request.NewName);
            if (edit.ValidationError != null)
            {
                errors.Add("名称：" + edit.ValidationError);
                return;
            }
            after = edit.AfterBytes;
        }
        if (request.NewSuffix is { } suffix)
        {
            var edit = _slots.PreviewSuffix(workspace, request.EffectId, suffix);
            if (edit.ValidationError != null)
            {
                errors.Add("后缀：" + edit.ValidationError);
                return;
            }
            after = (byte[])after.Clone();
            after[EngineEffectLayout.NameSuffixOffset] = suffix;
        }
        // A name that fills all 15 payload bytes leaves +0x0F as its terminator.
        // Setting a nonzero suffix in the same request would replace that
        // terminator and make the engine read past the slot.
        var suffixByte = after[EngineEffectLayout.NameSuffixOffset];
        if (suffixByte != 0 &&
            EngineEffectSlotRules.NameEnd(after) >= EngineEffectLayout.NameSuffixOffset)
        {
            errors.Add("名称：名称已占满 15 字节，+0x0F 是名称结束符；" +
                       "请先把名称缩短到 14 字节以内，再设置非零后缀。");
            return;
        }
        if (after.AsSpan().SequenceEqual(before.RawBytes)) return;
        AddChange(changes, errors, new EngineEffectWriteChange
        {
            EffectId = request.EffectId,
            FilePath = before.FilePath,
            FileOffset = before.FileOffset,
            DisplayName = $"名称槽（后缀 {after[EngineEffectLayout.NameSuffixOffset]:X2}）",
            BeforeBytes = before.RawBytes,
            AfterBytes = after,
            Kind = EngineEffectWriteChangeKind.NameSlot,
            ExpectedName = EngineEffectSlotRules.DecodeName(after),
            ExpectedSuffix = after[EngineEffectLayout.NameSuffixOffset]
        });
    }

    private void AddDescriptionChange(ProjectWorkspace workspace, EngineEffectWriteRequest request,
        List<EngineEffectWriteChange> changes, List<string> errors)
    {
        var edit = _slots.PreviewDescription(workspace, request.EffectId, request.NewDescription);
        if (edit.ValidationError != null)
        {
            errors.Add("说明：" + edit.ValidationError);
            return;
        }
        if (!edit.IsChanged) return;
        AddChange(changes, errors, new EngineEffectWriteChange
        {
            EffectId = request.EffectId,
            FilePath = edit.FilePath,
            FileOffset = edit.FileOffset,
            DisplayName = "Imsg 说明",
            BeforeBytes = edit.BeforeBytes,
            AfterBytes = edit.AfterBytes,
            Kind = EngineEffectWriteChangeKind.Description,
            ExpectedDescription = request.NewDescription ?? string.Empty
        });
    }

    private static void AddCallChanges(EngineEffectScanReport report, int effectId, EngineEffectCallEdit edit,
        List<EngineEffectWriteChange> changes, List<string> errors)
    {
        var candidates = report.Effects.SelectMany(row => row.CallSiteDetails)
            .Concat(report.TableCalls)
            .Concat(report.UnassignedCalls)
            .Concat(report.InternalCalls)
            .Concat(report.LoopCalls)
            .Concat(report.DerivedCalls)
            .Where(call => call.CallAddress == edit.CallAddress)
            .ToArray();
        var matches = candidates.DistinctBy(call => call.Identity).Where(call =>
            edit.Identity is { } identity ? call.Identity == identity :
            (edit.TableIndex is { } tableIndex ? call.TableIndex == tableIndex : call.TableIndex == null)).ToArray();
        var site = matches.Length == 1 ? matches[0] : null;
        if (site == null)
        {
            errors.Add($"调用点 0x{edit.CallAddress:X8} 无唯一匹配实例（{matches.Length} 项），请重新扫描并指定完整身份。");
            return;
        }
        if (site.Pattern == EngineEffectCallPattern.EngineInternal)
        {
            errors.Add($"调用点 0x{edit.CallAddress:X8} 位于引擎内部函数，不能写入。");
            return;
        }
        if (site.Pattern is EngineEffectCallPattern.LoopDerived or EngineEffectCallPattern.NestedDerived &&
            (edit.ValueMode != null || edit.StackingMode != null || edit.PersonalEffectId != null || edit.EntryTarget != null))
        {
            errors.Add($"调用点 0x{edit.CallAddress:X8} 是循环/二级包装展开调用，仅允许修改已定位的装备来源字段；计算参数不能按单点写回。");
            return;
        }
        if (site.Pattern == EngineEffectCallPattern.TableDerived)
        {
            if (edit.TableIndex is null)
            {
                errors.Add($"表驱动调用 0x{edit.CallAddress:X8} 需要指定表索引，不能按单个调用点写回。");
                return;
            }
            if (edit.ValueMode != null || edit.StackingMode != null || edit.PersonalEffectId != null ||
                edit.EntryTarget != null)
            {
                errors.Add($"表驱动调用 0x{edit.CallAddress:X8} 只能修改表项装备号；" +
                           "循环常量、个人号和判定入口只读。");
                return;
            }
        }
        if (site.Pattern == EngineEffectCallPattern.WrapperDerived && edit.EntryTarget != null)
        {
            errors.Add($"派生调用 0x{edit.CallAddress:X8} 的判定入口由桩函数 0x{site.WrapperStubEntry:X8} 决定，" +
                       "不能直接改写；请修改桩函数或调用者常量。");
            return;
        }
        AddPushChange(report.ExePath, effectId, site, site.ValuePush, edit.ValueMode, "效果值模式", 0, byte.MaxValue,
            site.ValueMode, changes, errors);
        AddPushChange(report.ExePath, effectId, site, site.StackingPush, edit.StackingMode, "叠加方式", 0, 3,
            site.StackingMode, changes, errors);
        AddPushChange(report.ExePath, effectId, site, site.ItemPush, edit.ItemEffectId, "装备特效号", 0, byte.MaxValue,
            site.ItemEffectId, changes, errors, byteChannel: true);
        AddPushChange(report.ExePath, effectId, site, site.PersonalPush, edit.PersonalEffectId, "兵种特效号", 0,
            byte.MaxValue, site.PersonalEffectId, changes, errors, byteChannel: true);
        if (edit.EntryTarget is { } target) AddEntryChange(report.ExePath, effectId, site, target, changes, errors);
    }

    private static void AddPushChange(string exePath, int effectId, EngineEffectCallSite site,
        EngineEffectPushArgument argument, int? newValue, string field, int minimum, int maximum, int? oldValue,
        List<EngineEffectWriteChange> changes, List<string> errors, bool byteChannel = false)
    {
        if (newValue is not { } value) return;
        if (value < minimum || value > maximum)
        {
            errors.Add($"{field} {value} 超出允许范围 {minimum}..{maximum}。");
            return;
        }
        if (oldValue == value) return;
        var plan = argument.WritePlan;
        if (!plan.IsWritable)
        {
            errors.Add($"调用点 0x{site.CallAddress:X8} 的{field}只读：{plan.BlockReason ?? "来源未证明"}");
            return;
        }
        if (plan.Definition == null || plan.ImmediateLength is not (1 or 4) || plan.ImmediateFileOffset <= 0 ||
            plan.BeforeBytes.Length != plan.ImmediateLength)
        {
            errors.Add($"调用点 0x{site.CallAddress:X8} 的{field}缺少可写立即数位置。");
            return;
        }
        if (plan.OldValue is not { } planValue || oldValue is null ||
            (byteChannel ? EngineEffectByteContract.Decode(planValue) != oldValue : planValue != (uint)oldValue.Value))
        {
            errors.Add($"调用点 0x{site.CallAddress:X8} 的{field}原值与扫描结果不一致。");
            return;
        }
        if (argument.Length == 2 && value > 0x7F && !byteChannel)
        {
            errors.Add($"{field} {value} 需要 5 字节压栈，当前 2 字节立即数只能写 0..127。");
            return;
        }
        byte[] after = plan.ImmediateLength == 1
            ? new byte[] { (byte)value }
            : new byte[] { (byte)value, 0, 0, 0 };
        var computed = plan.Definition.Address != argument.Address;
        var displayName = site.Pattern == EngineEffectCallPattern.TableDerived
            ? $"0x{site.CallAddress:X8} 表 0x{site.TableAddress:X8}[{site.TableIndex}] {field}"
            : $"0x{site.CallAddress:X8} {field}" +
              (computed ? $"（定义 0x{plan.Definition.Address:X8}）" : string.Empty);
        AddChange(changes, errors, new EngineEffectWriteChange
        {
            EffectId = effectId,
            FilePath = exePath,
            FileOffset = plan.ImmediateFileOffset,
            DisplayName = displayName,
            BeforeBytes = plan.BeforeBytes,
            AfterBytes = after,
            Kind = EngineEffectWriteChangeKind.Push,
            ExpectedValue = (uint)value
        });
    }

    private static void AddEntryChange(string exePath, int effectId, EngineEffectCallSite site, uint target,
        List<EngineEffectWriteChange> changes, List<string> errors)
    {
        if (!EngineEffectScanService.EntryNames.ContainsKey(target))
        {
            errors.Add($"判定入口 0x{target:X8} 不是已验证的四参数入口。");
            return;
        }
        if (target == site.EntryTarget) return;
        var raw = site.CallInstruction.RawBytes;
        if (raw.Length != 5 || raw[0] != 0xE8 || site.CallInstruction.FileOffset <= 0)
        {
            errors.Add($"调用点 0x{site.CallAddress:X8} 不是 E8 rel32 调用，不能修改判定入口。");
            return;
        }
        var relative = unchecked((int)(target - (site.CallAddress + 5)));
        var after = new byte[5];
        after[0] = 0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(after.AsSpan(1), relative);
        AddChange(changes, errors, new EngineEffectWriteChange
        {
            EffectId = effectId,
            FilePath = exePath,
            FileOffset = site.CallInstruction.FileOffset,
            DisplayName = $"0x{site.CallAddress:X8} 判定入口",
            BeforeBytes = raw,
            AfterBytes = after,
            Kind = EngineEffectWriteChangeKind.Entry,
            ExpectedValue = unchecked((uint)relative),
            ExpectedTarget = target
        });
    }

    private static void AddChange(List<EngineEffectWriteChange> changes, List<string> errors,
        EngineEffectWriteChange change)
    {
        var existing = changes.FirstOrDefault(existing => existing.FilePath.Equals(change.FilePath, StringComparison.OrdinalIgnoreCase) &&
                                    existing.FileOffset == change.FileOffset);
        if (existing != null)
        {
            if (existing.BeforeBytes.AsSpan().SequenceEqual(change.BeforeBytes) &&
                existing.AfterBytes.AsSpan().SequenceEqual(change.AfterBytes)) return;
            errors.Add($"{change.DisplayName} 与其他修改写入同一文件位置。");
            return;
        }
        changes.Add(change);
    }

    private static IEnumerable<string> FindOverlaps(IReadOnlyList<EngineEffectWriteChange> changes)
    {
        foreach (var group in changes.GroupBy(change => change.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(change => change.FileOffset).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                if (previous.FileOffset + previous.AfterBytes.Length > ordered[index].FileOffset)
                    yield return $"{previous.DisplayName} 与 {ordered[index].DisplayName} 的写入区间重叠。";
            }
        }
    }

    private static int? FollowEffectId(EngineEffectWriteRequest request, EngineEffectScanRow? row)
    {
        if (row == null || request.NewCalls.Count + request.CallEdits.Count != 1) return null;
        var edit = request.NewCalls.Values.Concat(request.CallEdits).First();
        if (edit.PersonalEffectId is not { } personal) return null;
        var site = row.CallSiteDetails.FirstOrDefault(call => call.CallAddress == edit.CallAddress);
        return site != null && site.PersonalEffectId != personal ? personal : null;
    }

    public void Verify(ProjectWorkspace workspace, EngineEffectWritePreview preview)
    {
        var snapshots = preview.Changes.Select(change => Path.GetFullPath(change.FilePath)).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        foreach (var change in preview.Changes)
        {
            var bytes = snapshots[Path.GetFullPath(change.FilePath)];
            if (change.FileOffset < 0 || change.FileOffset + change.AfterBytes.Length > bytes.LongLength)
                throw new InvalidDataException($"{change.DisplayName} 写入后偏移越界。");
            var actual = bytes.AsSpan(checked((int)change.FileOffset), change.AfterBytes.Length).ToArray();
            if (!actual.AsSpan().SequenceEqual(change.AfterBytes))
                throw new InvalidDataException($"{change.DisplayName} 写入后字节不一致。");
            switch (change.Kind)
            {
                case EngineEffectWriteChangeKind.Push:
                    if (DecodeImmediate(actual) != change.ExpectedValue)
                        throw new InvalidDataException($"{change.DisplayName} 写入后立即数复读不一致。");
                    break;
                case EngineEffectWriteChangeKind.Entry:
                    if (unchecked((uint)BinaryPrimitives.ReadInt32LittleEndian(actual.AsSpan(1))) != change.ExpectedValue)
                        throw new InvalidDataException($"{change.DisplayName} 写入后调用目标复读不一致。");
                    break;
                case EngineEffectWriteChangeKind.NameSlot:
                    var name = _slots.ReadNameSlot(workspace, change.EffectId, bytes);
                    if (!name.Exists || !name.Name.Equals(change.ExpectedName, StringComparison.Ordinal) ||
                        name.SuffixByte != change.ExpectedSuffix)
                        throw new InvalidDataException($"{change.DisplayName} 写入后名称/后缀复读不一致。");
                    break;
                case EngineEffectWriteChangeKind.Description:
                    var description = _slots.ReadDescriptionSlot(workspace, change.EffectId, bytes);
                    if (!description.Exists ||
                        !description.Text.Equals(change.ExpectedDescription, StringComparison.Ordinal))
                        throw new InvalidDataException($"{change.DisplayName} 写入后说明复读不一致。");
                    break;
            }
        }
    }

    private static uint? DecodeImmediate(byte[] raw)
        => raw.Length == 1 ? raw[0]
            : raw.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(raw)
            : null;

    private static byte[]? ReadCurrent(string path, string expectedSha, string label, List<string> errors,
        Dictionary<string, byte[]> cache)
    {
        if (cache.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path))
        {
            errors.Add($"未找到 {label}。");
            return null;
        }
        var bytes = File.ReadAllBytes(path);
        if (!string.IsNullOrWhiteSpace(expectedSha) &&
            !HashService.Sha256(bytes).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{label} 已被外部修改，请重新扫描。");
            return null;
        }
        cache[path] = bytes;
        return bytes;
    }

    /// <summary>Only a game process launched from this project's EXE owns these files.</summary>
    public static bool IsGameRunning(ProjectWorkspace workspace)
    {
        var target = Path.GetFullPath(ProjectFileLocator.Resolve(workspace.RootPath, "Ekd5.exe"));
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.Equals("Ekd5", StringComparison.OrdinalIgnoreCase)) continue;
                var module = TryGetProcessPath(process);
                // An unreadable module cannot prove it owns this project's file.
                // The atomic replace below still fails if the target EXE is in
                // use, so unrelated game instances never block this project.
                if (string.IsNullOrWhiteSpace(module)) continue;
                if (Path.GetFullPath(module).Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        return false;
    }

    private static string? TryGetProcessPath(Process process)
    {
        var handle = OpenProcess(0x1000, false, process.Id);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                var size = buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size)) return buffer.ToString();
            }
            finally { CloseHandle(handle); }
        }
        try { return process.MainModule?.FileName; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (NotSupportedException) { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
        System.Text.StringBuilder name, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    internal static string? CreateBackup(string projectRoot, IReadOnlyList<WorkspaceBinaryCandidate> candidates,
        out string? error)
    {
        error = null;
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            var directory = Path.Combine(ProjectHistoryPaths.GetProjectStorageRoot(projectRoot), "引擎写入", "备份", stamp);
            Directory.CreateDirectory(directory);
            foreach (var candidate in candidates)
            {
                var beforeHash = HashService.Sha256(candidate.BeforeBytes);
                if (!HashService.Sha256(candidate.FilePath).Equals(beforeHash, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"{Path.GetFileName(candidate.FilePath)} 在备份前已被外部修改。";
                    return null;
                }
                var target = Path.Combine(directory, Path.GetFileName(candidate.FilePath));
                File.Copy(candidate.FilePath, target, true);
                if (!HashService.Sha256(target).Equals(beforeHash, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"备份校验失败：{target}";
                    return null;
                }
            }
            return directory;
        }
        catch (Exception ex)
        {
            error = "写前备份失败：" + ex.Message;
            return null;
        }
    }

    private static EngineEffectWriteResult Failure(EngineEffectWritePreview preview, string message) => new()
    {
        Success = false,
        AppliedChanges = 0,
        FollowEffectId = preview.FollowEffectId,
        Message = message
    };
}
