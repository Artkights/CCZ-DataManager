﻿using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly EngineEffectDetailPanel _engineDetail;
    private readonly CheckBox _engineOnlyWithCalls = new() { Text = "仅显示有调用点", AutoSize = true, Margin = Padding.Empty };
    private readonly FlowLayoutPanel _engineFilterHost = new()
    {
        Dock = DockStyle.Top,
        Height = 30,
        Padding = new Padding(2, 3, 2, 2),
        Visible = false
    };
    private EngineEffectScanReport? _engineReport;
    private string? _engineReportFingerprint;
    private readonly Dictionary<string, EngineEffectScanReport> _engineReportCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _engineScanCancellation;
    private Task<EngineEffectScanReport>? _engineScanTask;
    private string? _engineScanTaskKey;
    private int _engineScanGeneration;
    private int _engineViewRequestVersion;
    private readonly Button _engineCancelScan = new() { Text = "取消扫描", AutoSize = true, Visible = false };
    internal bool EngineScanPendingForSmokeTest => _engineScanTask is { IsCompleted: false };
    private readonly List<EngineEffectScanRow> _engineRows = [];
    private bool _suppressEngineSelection;

    private bool IsEngineTab => _moduleTabs.SelectedTab?.Tag as string == "__engine";

    internal EngineEffectDetailPanel EngineDetailForSmokeTest => _engineDetail;
    internal EngineEffectScanReport? EngineReportForSmokeTest => _engineReport;
    internal void SelectEngineEffectForSmokeTest(int effectId) => SelectEngineEffectById(effectId);
    internal CheckBox EngineOnlyWithCallsForSmokeTest => _engineOnlyWithCalls;

    private async void ShowEngineScanView(bool forceRescan = false)
    {
        if (IsDisposed || Disposing) return;
        _historyFilter.Visible = false;
        _engineSubTabs.Visible = true;
        // Docked headers must be laid out before the Fill siblings.
        _engineSubTabs.SendToBack();
        // 常量是整页子页，切换子页时只保留当前页；公式页是独立顶级页。
        _battleFormulaPanel.Visible = false;
        _transformPanel.Visible = false;
        if (EngineSubpage == EnginePage.Transform) { ++_engineViewRequestVersion; ShowTransformView(); return; }
        _constantPanel.Visible = EngineSubpage == ConstantEngineSubpage;
        if (EngineSubpage == ConstantEngineSubpage)
        {
            ++_engineViewRequestVersion;
            ShowConstantView();
            return;
        }
        _entityList.Visible = true;
        _quickLookup.Visible = true;
        _engineDetail.Visible = true;
        _engineDetail.BringToFront();
        _grid.Visible = false;
        _engineFilterHost.Visible = true;
        _engineOnlyWithCalls.Visible = EngineSubpage == EnginePage.Effects;
        _engineFilterHost.Height = EngineSubpage == EnginePage.Effects ? 60 : 32;
        if (string.IsNullOrWhiteSpace(_workspace.RootPath))
        {
            _engineReport = null;
            _engineRows.Clear();
            _entityList.Items.Clear();
            _quickLookup.SetOptions([]);
            _engineDetail.Reset();
            SetStatus("引擎扫描：尚未打开项目。", true);
            return;
        }
        var requestedRoot = _workspace.RootPath;
        var exePath = ProjectFileLocator.Resolve(requestedRoot, "Ekd5.exe");
        var imsgPath = ProjectFileLocator.Resolve(requestedRoot, "Imsg.e5");
        var requestVersion = ++_engineViewRequestVersion;
        var requestGeneration = _engineScanGeneration;
        // Freeze only strings on the UI thread. Full hashes must still detect
        // same-length/same-timestamp external changes, without blocking input.
        var identity = EngineProfileFingerprintPrefix();
        string fingerprint;
        try { fingerprint = await Task.Run(() => EngineFileFingerprint(identity, exePath, imsgPath)); }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && requestVersion == _engineViewRequestVersion && _workspace.RootPath == requestedRoot)
                _engineDetail.ShowEmpty("引擎读取失败：" + ex.Message);
            return;
        }
        if (IsDisposed || Disposing || !IsEngineTab || requestVersion != _engineViewRequestVersion ||
            requestGeneration != _engineScanGeneration || _workspace.RootPath != requestedRoot) return;
        var reuse = !forceRescan && _engineReport != null && _engineReportFingerprint == fingerprint;
        if (reuse)
        {
            _engineDetail.BindReport(_workspace, _engineReport!);
        }
        else
        {
            if (!forceRescan && HasAnyEngineChanges && _engineReport != null)
            {
                if (EngineSubpage == EnginePage.Effects) PopulateEngineEntityList(); else ShowEngineSubpageView();
                return;
            }
            try
            {
                var root = _workspace.RootPath;
                EngineEffectScanReport report;
                if (!forceRescan && _engineReportCache.TryGetValue(fingerprint, out var cached)) report = cached;
                else if (!forceRescan && _engineReportFingerprint != null &&
                    EngineStructureKey(_engineReportFingerprint) == EngineStructureKey(fingerprint) && _engineReport != null)
                {
                    report = EngineEffectReportTextService.RefreshDescriptions(_workspace, _engineReport);
                    if (_engineReportCache.Count >= 3) _engineReportCache.Clear();
                    _engineReportCache[fingerprint] = report;
                }
                else
                {
                    if (_engineScanCancellation == null || _engineScanCancellation.IsCancellationRequested || _engineScanTask is not { IsCompleted: false } || _engineScanTaskKey != fingerprint || forceRescan)
                    {
                        CancelEngineScan();
                        _engineScanCancellation = new CancellationTokenSource();
                        var token = _engineScanCancellation.Token;
                        var workerGeneration = _engineScanGeneration;
                        _engineScanTaskKey = fingerprint;
                        _engineCancelScan.Visible = true;
                        _engineDetail.ShowEmpty("正在扫描引擎，可切换页面或取消。");
                        var progress = new Progress<EngineEffectScanProgress>(stage =>
                        {
                            if (!IsDisposed && !Disposing && workerGeneration == _engineScanGeneration && _workspace.RootPath == root && _engineScanTaskKey == fingerprint)
                                SetStatus($"引擎扫描 {stage.CompletedStages}/{stage.TotalStages}：{stage.Stage}（{stage.ElapsedMilliseconds / 1000.0:F1}秒）");
                        });
                        _engineScanTask = Task.Run(() =>
                        {
                            token.ThrowIfCancellationRequested();
                            var readOnly = new DataManager.Core.Editing.ProjectWorkspace();
                            readOnly.Open(root);
                            token.ThrowIfCancellationRequested();
                            return new EngineEffectScanService().Scan(readOnly, token, progress);
                        }, token);
                    }
                    requestGeneration = _engineScanGeneration;
                    report = await _engineScanTask;
                    if (IsDisposed || Disposing || requestVersion != _engineViewRequestVersion || requestGeneration != _engineScanGeneration || _workspace.RootPath != root) return;
                    _engineCancelScan.Visible = false;
                    var currentFingerprint = await Task.Run(() => EngineFileFingerprint(identity, exePath, imsgPath));
                    if (IsDisposed || Disposing || !IsEngineTab || requestVersion != _engineViewRequestVersion ||
                        requestGeneration != _engineScanGeneration || _workspace.RootPath != root) return;
                    if (currentFingerprint != fingerprint)
                    {
                        ShowEngineScanView(forceRescan: true);
                        return;
                    }
                    if (_engineReportCache.Count >= 3) _engineReportCache.Clear();
                    _engineReportCache[fingerprint] = report;
                }
                if (!IsEngineTab) return;
                _engineReport = report;
                _engineReportFingerprint = fingerprint;
                _engineRows.Clear();
                _engineRows.AddRange(report.Effects);
                _engineDetail.BindReport(_workspace, report);
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed && !Disposing && requestVersion == _engineViewRequestVersion && requestGeneration == _engineScanGeneration && _engineScanTaskKey == fingerprint)
                {
                    _engineCancelScan.Visible = false;
                    SetStatus("引擎扫描已取消；再次进入子页可重新扫描。");
                }
                return;
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing || requestVersion != _engineViewRequestVersion || requestGeneration != _engineScanGeneration || _workspace.RootPath != requestedRoot || _engineScanTaskKey != fingerprint) return;
                _engineCancelScan.Visible = false;
                _engineReport = null;
                _engineRows.Clear();
                _entityList.Items.Clear();
                _quickLookup.SetOptions([]);
                _engineDetail.ShowEmpty("引擎扫描失败：" + ex.Message);
                SetStatus("引擎扫描失败：" + ex.Message, true);
                return;
            }
        }
        if (EngineSubpage != EnginePage.Effects)
        {
            ShowEngineSubpageView();
            return;
        }
        _engineItemAssignments.Visible = false;
        _engineJobAssignments.Visible = false;
        PopulateEngineEntityList();
        BindEngineEffectChoices();
        var current = _engineDetail.CurrentEffectId;
        var visible = _entityList.Items.Cast<EngineEffectChoice>().ToArray();
        var target = current is { } id && visible.Any(choice => choice.Id == id)
            ? id
            : visible.Length > 0 ? visible[0].Id : (int?)null;
        if (target is { } selected)
        {
            if (_engineDetail.HasPendingChanges)
            {
                // Refreshing the report must never discard an unwritten edit.
                _quickLookup.SetOptions(_engineRows.Select(row => new IdNameOption(row.EffectId, row.Name)));
                _quickLookup.SetBoundValue((_engineDetail.CurrentEffectId ?? selected).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                SelectEngineChoice(selected);
                _engineDetail.BindEffect(selected);
                _quickLookup.SetOptions(_engineRows.Select(row => new IdNameOption(row.EffectId, row.Name)));
                _quickLookup.SetBoundValue(selected.ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            _engineDetail.ShowEmpty("没有可显示的特效。");
        }
        UpdateEngineStatus();
    }

    private void ShowEngineSubpageView()
    {
        if (EngineSubpage == ConstantEngineSubpage) ShowConstantView();
        else if (EngineSubpage == EnginePage.Transform) ShowTransformView();
        else ShowEngineAssignmentView();
    }

    private void UpdateEngineStatus()
    {
        if (_engineReport is not { } report) return;
        SetStatus($"引擎 {report.EngineVersion}：特效 {report.Effects.Count}；" +
                  $"调用点 {report.CallSiteCount}（静态 {report.ResolvedCallSiteCount}，" +
                  $"动态 {report.DynamicCallSiteCount}，未解析 {report.UnresolvedCallSiteCount}；" +
                  $"内部 {report.InternalCallSiteCount}，未归属 {report.UnassignedCalls.Count}，" +
                  $"派生 {report.DerivedCallCount}（包装 {report.Wrappers.Count}），" +
                  $"表驱动 {report.TableCallCount}，循环展开 {report.LoopCallCount}，" +
                  $"派生/表驱动独有特效 {report.DerivedOnlyEffectIds.Count}，" +
                  $"代码洞进入 {report.CaveEntryCallCount}，仅死代码 {report.UnreachableOnlyEffectIds.Count}，" +
                  $"未对齐 E8 {report.UnalignedCallCount}，" +
                  $"无调用 {report.UnreferencedEffectIds.Count}）。");
    }

    private void PopulateEngineEntityList()
    {
        var current = _engineDetail.CurrentEffectId;
        var previous = _suppressEngineSelection;
        _suppressEngineSelection = true;
        _entityList.BeginUpdate();
        try
        {
            _entityList.Items.Clear();
            foreach (var row in _engineRows)
            {
                if (_engineOnlyWithCalls.Checked && !row.HasCalls) continue;
                var filter = _engineEntityFilter.Text.Trim();
                if (filter.Length > 0 && !$"{row.EffectId}：{row.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                _entityList.Items.Add(new EngineEffectChoice(row.EffectId, row.Name, row.HasCalls,
                    _engineDetail.IsPending(row.EffectId)));
            }
            var index = current is { } id
                ? _entityList.Items.Cast<EngineEffectChoice>().ToList().FindIndex(choice => choice.Id == id)
                : -1;
            if (index < 0 && _entityList.Items.Count > 0) index = 0;
            if (index >= 0) _entityList.SelectedIndex = index;
        }
        finally
        {
            _entityList.EndUpdate();
            _suppressEngineSelection = previous;
        }
    }

    private void EngineFilterChanged()
    {
        if (!IsEngineTab) return;
        var current = _engineDetail.CurrentEffectId;
        PopulateEngineEntityList();
        if (_engineDetail.HasPendingChanges) return;
        if (_entityList.SelectedItem is EngineEffectChoice choice && choice.Id != current)
            _engineDetail.BindEffect(choice.Id);
        else if (_entityList.Items.Count == 0)
            _engineDetail.ShowEmpty("没有可显示的特效。");
        if (EngineListSelection() is { } selected)
            _quickLookup.SetBoundValue(selected.ToString(CultureInfo.InvariantCulture));
        UpdateEngineStatus();
    }

    private int? EngineListSelection()
        => (_entityList.SelectedItem as EngineEffectChoice)?.Id;

    private void SelectEngineChoice(int effectId)
    {
        var index = _entityList.Items.Cast<EngineEffectChoice>().ToList().FindIndex(choice => choice.Id == effectId);
        if (index < 0) return;
        var previous = _suppressEngineSelection;
        _suppressEngineSelection = true;
        try { _entityList.SelectedIndex = index; }
        finally { _suppressEngineSelection = previous; }
    }

    private void SelectEngineEffectById(int effectId)
    {
        var row = _engineRows.FirstOrDefault(item => item.EffectId == effectId);
        if (row == null)
        {
            SetStatus($"引擎扫描没有特效 ID={effectId}。", true);
            return;
        }
        if (_engineOnlyWithCalls.Checked && !row.HasCalls)
            _engineOnlyWithCalls.Checked = false;
        if (_entityList.Items.Cast<EngineEffectChoice>().All(choice => choice.Id != effectId))
            PopulateEngineEntityList();
        SelectEngineChoice(effectId);
        if (!ConfirmSwitchEngineEffect(effectId)) { RestoreEngineSelection(); return; }
        SelectEngineChoice(effectId);
        _engineDetail.BindEffect(effectId);
        _quickLookup.SetOptions(_engineRows.Select(item => new IdNameOption(item.EffectId, item.Name)));
        _quickLookup.SetBoundValue(effectId.ToString(CultureInfo.InvariantCulture));
    }

    private bool ConfirmSwitchEngineEffect(int targetId)
    {
        if (_engineDetail.CurrentEffectId == targetId || !_engineDetail.HasPendingChanges) return true;
        var result = MessageBox.Show(this, "当前特效有未写入修改。是否先写入？", "引擎扫描",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.Yes) return TryCommitAllEngineChanges();
        _engineDetail.DiscardPendingChanges();
        return true;
    }

    private void RestoreEngineSelection()
    {
        if (_engineDetail.CurrentEffectId is not { } id) return;
        SelectEngineChoice(id);
    }

    internal bool ConfirmLeaveEngine()
    {
        if (!HasAnyEngineChanges) return true;
        var result = MessageBox.Show(this, "引擎修改尚未写入。是否先写入？", "引擎扫描",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.Yes) return TryCommitAllEngineChanges();
        _engineDetail.DiscardPendingChanges();
        DiscardEngineAssignmentChanges();
        return true;
    }

    private void EngineWriteCompleted(EngineEffectScanReport? verifiedReport = null)
    {
        RefreshRangeAfterInternalWrite();
        if (verifiedReport != null && File.Exists(verifiedReport.ExePath) && File.Exists(verifiedReport.ImsgPath) &&
            HashService.Sha256(verifiedReport.ExePath) == verifiedReport.ExeSha256 &&
            HashService.Sha256(verifiedReport.ImsgPath) == verifiedReport.ImsgSha256)
        {
            _engineReport = verifiedReport;
            _engineReportFingerprint = EngineReportFingerprint(verifiedReport.ExePath, verifiedReport.ImsgPath);
            _engineReportCache[_engineReportFingerprint] = verifiedReport;
            _engineRows.Clear();
            _engineRows.AddRange(verifiedReport.Effects);
        }
        var follow = _engineDetail.LastFollowEffectId;
        var callAddress = _engineDetail.LastWrittenCallAddress;
        ShowEngineScanView(forceRescan: verifiedReport == null);
        if (EngineSubpage != EnginePage.Effects) { RefreshDirtyState(); return; }
        if (follow is { } id) SelectEngineEffectById(id);
        else if (callAddress is { } address) _engineDetail.SelectCallByAddress(address);
        RefreshDirtyState();
    }

    private string EngineProfileFingerprintPrefix()
        => $"scan-v2|{_workspace.ActiveProfile.Profile.Id}|{_workspace.ActiveProfile.Variant.Id}|{_workspace.ActiveProfile.LayoutRevision}|{_workspace.ActiveProfile.LayoutId}|";
    private string EngineReportFingerprint(string exePath, string imsgPath)
        => EngineFileFingerprint(EngineProfileFingerprintPrefix(), exePath, imsgPath);
    private static string EngineFileFingerprint(string identity, string exePath, string imsgPath)
        => $"{identity}{Path.GetFullPath(exePath).ToUpperInvariant()}|{HashService.Sha256(exePath)}|{EngineImsgHash(imsgPath)}";

    private static string EngineStructureKey(string fingerprint) => fingerprint[..fingerprint.LastIndexOf('|')];
    private static string EngineImsgHash(string path) => File.Exists(path) ? HashService.Sha256(path) : string.Empty;

    private void CancelEngineScan()
    {
        _engineScanGeneration++;
        _engineScanCancellation?.Cancel();
        _engineScanCancellation?.Dispose();
        _engineScanCancellation = null;
        _engineScanTask = null;
        _engineScanTaskKey = null;
    }

    private sealed record EngineEffectChoice(int Id, string Name, bool HasCalls, bool Pending)
    {
        public override string ToString()
        {
            var text = string.IsNullOrWhiteSpace(Name)
                ? $"{Id.ToString(CultureInfo.InvariantCulture)}：未命名"
                : $"{Id.ToString(CultureInfo.InvariantCulture)}：{Name}";
            return Pending ? text + " *" : text;
        }
    }
}
