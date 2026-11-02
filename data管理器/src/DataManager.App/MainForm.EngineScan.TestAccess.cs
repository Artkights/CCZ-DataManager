using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class MainForm
{
    internal EngineEffectScanReport? EngineReportForLifecycleTest => _engineReport;
    internal EngineEffectAssignmentCatalog? EngineCatalogForLifecycleTest => _engineAssignmentCatalog;
    internal Task<EngineEffectScanReport>? EngineTaskForLifecycleTest => _engineScanTask;
    internal int EngineGenerationForLifecycleTest => _engineScanGeneration;
    internal void RefreshEngineForLifecycleTest(bool forceRescan = false) => ShowEngineScanView(forceRescan);
    internal void CancelEngineForLifecycleTest() => CancelEngineScan();

    /// <summary>Exercises the production await/publication path with a controllable worker completion.</summary>
    internal void HoldEngineWorkerForLifecycleTest(Task<EngineEffectScanReport> worker)
    {
        CancelEngineScan();
        var exePath = ProjectFileLocator.Resolve(_workspace.RootPath, "Ekd5.exe");
        var imsgPath = ProjectFileLocator.Resolve(_workspace.RootPath, "Imsg.e5");
        var key = EngineReportFingerprint(exePath, imsgPath);
        _engineReportCache.Remove(key);
        _engineReport = null;
        _engineReportFingerprint = null;
        _engineScanCancellation = new CancellationTokenSource();
        _engineScanTask = worker;
        _engineScanTaskKey = key;
        ShowEngineScanView();
    }
}
