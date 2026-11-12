#requires -Version 7.0
<#
Runs the runtime-optimization regression suites strictly in the reviewed order.
This runner does not build, benchmark, parallelize tests, or certify latency targets.
Run it only after the serial baseline/candidate performance process has released the
machine. Every native invocation uses ArgumentList so paths remain literal arguments.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [string]$EvidenceDirectory,
    [string]$TestDll
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if (!$EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $RepositoryRoot ('data管理器/验证/布局与读取性能-1.2026.1113.1/regressions-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (!$TestDll) { $TestDll = Join-Path $RepositoryRoot 'data管理器/src/DataManager.Tests/bin/Release/net8.0-windows/DataManager.Tests.dll' }
$TestDll = [IO.Path]::GetFullPath($TestDll)
if (!(Test-Path -LiteralPath $TestDll -PathType Leaf)) { throw "缺少已构建的 Tests DLL：$TestDll" }
$programPath = Join-Path $RepositoryRoot 'data管理器/src/DataManager.Tests/Program.cs'
if (!(Test-Path -LiteralPath $programPath -PathType Leaf)) { throw "缺少规范测试入口：$programPath" }
$runtimeDirectory = [IO.Path]::GetDirectoryName($TestDll)
$dotnetPath = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$basePaths = [ordered]@{
    '6.4' = Join-Path $RepositoryRoot '基底/洗隋'
    '6.5' = Join-Path $RepositoryRoot '基底/加强版6.5未加密版'
    '6.6' = Join-Path $RepositoryRoot '基底/复刻版6.6山贼'
}
foreach ($path in $basePaths.Values) {
    if (!(Test-Path -LiteralPath $path -PathType Container)) { throw "缺少回归基底：$path" }
}
$summaryPath = Join-Path $EvidenceDirectory 'runtime-optimization-regressions.json'
if (Test-Path -LiteralPath $summaryPath) { throw "回归证据已存在，请指定新的 EvidenceDirectory：$summaryPath" }
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null

function Get-FileEvidence([string]$Path, [string]$RelativeTo) {
    $item = Get-Item -LiteralPath $Path
    return [pscustomobject][ordered]@{
        Path = [IO.Path]::GetRelativePath($RelativeTo, $item.FullName).Replace('\', '/')
        Bytes = $item.Length; Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
}
function Get-SourceEvidence {
    $result = [Collections.Generic.List[object]]::new()
    foreach ($tree in @('data管理器/src', 'data管理器/config', 'data管理器/tools', '形象管理器/src')) {
        $directory = Join-Path $RepositoryRoot $tree
        if (!(Test-Path -LiteralPath $directory -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($directory, $file.FullName).Replace('\', '/')
            if ($relative -match '(^|/)(bin|obj|src|\.git)(/|$)' -or $relative.StartsWith('.')) { continue }
            if ($file.Extension -notin @('.cs', '.csproj', '.sln', '.props', '.targets', '.json', '.ps1', '.resx', '.ico', '.png')) { continue }
            $result.Add((Get-FileEvidence $file.FullName $RepositoryRoot))
        }
    }
    return @($result | Sort-Object Path)
}
function Get-RuntimeEvidence {
    return @(Get-ChildItem -LiteralPath $runtimeDirectory -File |
        Where-Object { $_.Extension -in @('.dll', '.exe', '.json') } | Sort-Object Name |
        ForEach-Object { Get-FileEvidence $_.FullName $runtimeDirectory })
}
function Compare-Evidence([object[]]$Before, [object[]]$After) {
    $left = @{}; $right = @{}
    foreach ($file in $Before) { $left[$file.Path] = $file.Sha256 }
    foreach ($file in $After) { $right[$file.Path] = $file.Sha256 }
    return @(@($left.Keys) + @($right.Keys) | Sort-Object -Unique | Where-Object {
        !$left.ContainsKey($_) -or !$right.ContainsKey($_) -or $left[$_] -ne $right[$_]
    })
}

$plan = [Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [string]$Flag, [string[]]$Parameters, [string]$SuccessMarker, [string]$Profile = '') {
    $plan.Add([pscustomobject][ordered]@{ Sequence = $plan.Count + 1; Name = $Name; Flag = $Flag
        Parameters = @($Parameters); Profile = $Profile; SuccessMarker = $SuccessMarker })
}
Add-Case 'query-cache' '--query-cache' @() '查询读取缓存：'
foreach ($profile in $basePaths.Keys) { Add-Case "source-file-read-session-$profile" '--source-file-read-session' @($basePaths[$profile]) 'SOURCE_FILE_READ_SESSION_TESTS_OK' $profile }
foreach ($profile in $basePaths.Keys) { Add-Case "record-query-core-$profile" '--record-query-core' @($basePaths[$profile]) '属性查询 Core：' $profile }
Add-Case 'record-presentation' '--record-presentation' @() '记录展示标签：'
Add-Case 'record-query-bindings-6.5' '--record-query-bindings' @($basePaths['6.5']) 'RECORD_QUERY_BINDING_TESTS_OK' '6.5'
foreach ($profile in $basePaths.Keys) { Add-Case "record-query-ui-$profile" '--record-query-ui' @($basePaths[$profile]) 'RECORD_QUERY_UI_TESTS_OK' $profile }
Add-Case 'core-functional-regression-6.5' '--core-functional-regression' @($basePaths['6.5']) 'CORE_FUNCTIONAL_REGRESSION_TESTS_OK' '6.5'
Add-Case 'csv-format-only-6.5' '--csv-format-only' @($basePaths['6.5']) 'CSV_FORMAT_ONLY_IMPORT_TESTS_OK' '6.5'
Add-Case 'query-pipeline-6.5' '--query-pipeline' @($basePaths['6.5']) 'RECORD_QUERY_PIPELINE_OK' '6.5'
$sessionOutput = Join-Path $EvidenceDirectory 'artifacts/runtime-session-lifecycle-6.5'
Add-Case 'runtime-session-lifecycle-6.5' '--runtime-session-lifecycle' @($basePaths['6.5'], $sessionOutput) 'RUNTIME_SESSION_LIFECYCLE_OK' '6.5'
$headerOutput = Join-Path $EvidenceDirectory 'artifacts/responsive-header-layout-6.5'
Add-Case 'responsive-header-layout-6.5' '--responsive-header-layout' @($basePaths['6.5'], $headerOutput) 'RESPONSIVE_HEADER_LAYOUT_TESTS_OK' '6.5'
# PerformanceSelfAuditTests writes a JSON file; ProjectLifecycleOptimizationTests
# takes the repository root and locates its two native fixtures internally.
$auditOutput = Join-Path $EvidenceDirectory 'artifacts/performance-self-audit-6.5.json'
Add-Case 'performance-self-audit-6.5' '--performance-self-audit' @($basePaths['6.5'], $auditOutput) 'PERFORMANCE_SELF_AUDIT_TESTS_OK' '6.5'
$projectOutput = Join-Path $EvidenceDirectory 'artifacts/project-lifecycle-optimization'
Add-Case 'project-lifecycle-optimization' '--project-lifecycle-optimization' @($RepositoryRoot, $projectOutput) 'PROJECT_LIFECYCLE_OPTIMIZATION_OK' '6.5/6.6'
Add-Case 'extend-hidden-return' '--extend-hidden-return' @() 'EXTEND_HIDDEN_RETURN_OK'
Add-Case 'extend-pager-lifecycle' '--extend-pager-lifecycle' @() 'EXTEND_PAGER_LIFECYCLE_OK'
Add-Case 'business-preview-owner-lifecycle' '--business-preview-owner-lifecycle' @() 'BUSINESS_PREVIEW_OWNER_LIFECYCLE_OK'
Add-Case 'projected-column-reuse-6.5' '--projected-column-reuse' @($basePaths['6.5']) 'PROJECTED_COLUMN_REUSE_OK' '6.5'
Add-Case 'image-resource-optimization' '--image-resource-optimization' @() 'IMAGE_RESOURCE_OPTIMIZATION_TESTS_OK'
Add-Case 'resource-infrastructure' '--resource-infrastructure' @() 'RESOURCE_INFRASTRUCTURE_TESTS_OK'
Add-Case 'resource-previews-6.5' '--resource-previews' @($basePaths['6.5']) 'RESOURCE_PREVIEW_TESTS_OK' '6.5'

$programText = Get-Content -LiteralPath $programPath -Raw -Encoding UTF8
foreach ($case in $plan) {
    if (!$programText.Contains('"' + $case.Flag + '"')) { throw "规范 Program.cs 未声明测试 flag：$($case.Flag)" }
}
$sourceBefore = @(Get-SourceEvidence)
$runtimeBefore = @(Get-RuntimeEvidence)
$sourceManifestPath = Join-Path $EvidenceDirectory 'source-hashes-before.json'
$runtimeManifestPath = Join-Path $EvidenceDirectory 'runtime-hashes-before.json'
$sourceBefore | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $sourceManifestPath -Encoding utf8NoBOM
$runtimeBefore | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $runtimeManifestPath -Encoding utf8NoBOM
$results = [Collections.Generic.List[object]]::new()
$summary = [ordered]@{
    Schema = 'ccz.runtime-optimization-regressions.v1'; StartedAt = [DateTimeOffset]::Now.ToString('O'); CompletedAt = $null
    Status = 'Running'; AllRegressionTestsPassed = $false; PerformanceTargetsCertified = $false
    RepositoryRoot = $RepositoryRoot; EvidenceDirectory = $EvidenceDirectory; TestDll = $TestDll
    TestDllSha256 = (Get-FileHash -LiteralPath $TestDll -Algorithm SHA256).Hash
    Dotnet = $dotnetPath; ActualFixtures = $basePaths; PlannedCount = $plan.Count
    SourceManifest = $sourceManifestPath; SourceManifestSha256 = (Get-FileHash -LiteralPath $sourceManifestPath -Algorithm SHA256).Hash
    RuntimeManifest = $runtimeManifestPath; RuntimeManifestSha256 = (Get-FileHash -LiteralPath $runtimeManifestPath -Algorithm SHA256).Hash
    SourceFilesUnchanged = $null; SourceDifferences = @(); RuntimeFilesUnchanged = $null; RuntimeDifferences = @()
    Failure = $null; Plan = @($plan); Results = @()
    Notes = @(
        '每个测试进程严格顺序执行，失败即停止；没有构建，也没有启动 runtime-performance 计时。',
        '成功需要退出码为 0 且输出对应成功标记，防止旧 DLL 对未知 flag 落入默认测试路径后误报。',
        '源码清单仅覆盖规范 src、配置、工具及形象管理器共享源码；排除 bin/obj、历史 src/src 和独立解压源码。',
        '本回归结论不能替代三基底各三次性能、真实显示器 DPI、动画样本或游戏内实战验收。',
        'performance-self-audit 输出为 JSON 文件；project-lifecycle-optimization 输入为仓库根目录而非某一个基底。'
    )
}
function Save-Summary {
    $summary.Results = @($results)
    $summary | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $summaryPath -Encoding utf8NoBOM
}
Save-Summary
$caughtFailure = $null
try {
    foreach ($case in $plan) {
        $caseDirectory = Join-Path $EvidenceDirectory ('{0:D2}-{1}' -f $case.Sequence, $case.Name)
        New-Item -ItemType Directory -Force -Path $caseDirectory | Out-Null
        $logPath = Join-Path $caseDirectory 'test.log'
        $resultPath = Join-Path $caseDirectory 'result.json'
        $startedAt = [DateTimeOffset]::Now
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $result = [ordered]@{
            Sequence = $case.Sequence; Name = $case.Name; Flag = $case.Flag; Profile = $case.Profile
            Executable = $dotnetPath; Arguments = @($TestDll, $case.Flag) + @($case.Parameters)
            WorkingDirectory = $RepositoryRoot; StartedAt = $startedAt.ToString('O'); CompletedAt = $null; ElapsedMs = $null
            ExitCode = $null; ExpectedSuccessMarker = $case.SuccessMarker; SuccessMarkerObserved = $false; Passed = $false
            TestDllSha256 = (Get-FileHash -LiteralPath $TestDll -Algorithm SHA256).Hash
            SourceManifestSha256 = $summary.SourceManifestSha256
            LogPath = $logPath; LogBytes = $null; LogSha256 = $null; Artifacts = @(); Failure = $null
        }
        Write-Output ('REGRESSION_START {0}/{1} {2}' -f $case.Sequence, $plan.Count, $case.Name)
        try {
            if ($result.TestDllSha256 -ne $summary.TestDllSha256) { throw 'Tests DLL 在本次串行回归期间发生变化。' }
            $startInfo = [Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = $dotnetPath; $startInfo.WorkingDirectory = $RepositoryRoot
            $startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true
            $startInfo.RedirectStandardOutput = $true; $startInfo.RedirectStandardError = $true
            $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
            $startInfo.ArgumentList.Add($TestDll); $startInfo.ArgumentList.Add($case.Flag)
            foreach ($argument in $case.Parameters) { $startInfo.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::new()
            try {
                $process.StartInfo = $startInfo
                if (!$process.Start()) { throw '无法启动测试进程。' }
                $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
                $standardErrorTask = $process.StandardError.ReadToEndAsync()
                $process.WaitForExit()
                $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
                $standardError = $standardErrorTask.GetAwaiter().GetResult()
                $result.ExitCode = $process.ExitCode
            } finally { $process.Dispose() }
            $combined = $standardOutput + [Environment]::NewLine + '--- STDERR ---' + [Environment]::NewLine + $standardError
            [IO.File]::WriteAllText($logPath, $combined, [Text.UTF8Encoding]::new($false))
            $result.SuccessMarkerObserved = $standardOutput.Contains($case.SuccessMarker)
            $result.Passed = $result.ExitCode -eq 0 -and $result.SuccessMarkerObserved
            $expectedArtifact = switch ($case.Flag) {
                '--runtime-session-lifecycle' { Join-Path $sessionOutput 'runtime-session-lifecycle.json' }
                '--responsive-header-layout' { Join-Path $headerOutput 'responsive-header-layout.json' }
                '--performance-self-audit' { $auditOutput }
                '--project-lifecycle-optimization' { Join-Path $projectOutput 'project-lifecycle.json' }
                default { $null }
            }
            if ($null -ne $expectedArtifact -and !(Test-Path -LiteralPath $expectedArtifact -PathType Leaf)) {
                $result.Passed = $false
                throw "测试没有生成声明的 JSON 证据：$expectedArtifact"
            }
            if (!$result.Passed) { throw "退出码=$($result.ExitCode)，成功标记=$($result.SuccessMarkerObserved)；参见 $logPath" }
        } catch {
            $result.Failure = $_.Exception.Message
            if (!(Test-Path -LiteralPath $logPath -PathType Leaf)) {
                [IO.File]::WriteAllText($logPath, $result.Failure, [Text.UTF8Encoding]::new($false))
            }
        } finally {
            $watch.Stop(); $result.ElapsedMs = $watch.Elapsed.TotalMilliseconds
            $result.CompletedAt = [DateTimeOffset]::Now.ToString('O')
            if (Test-Path -LiteralPath $logPath -PathType Leaf) {
                $result.LogBytes = (Get-Item -LiteralPath $logPath).Length
                $result.LogSha256 = (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash
            }
            $artifactPaths = @($case.Parameters | Where-Object { $_.StartsWith((Join-Path $EvidenceDirectory 'artifacts'), [StringComparison]::OrdinalIgnoreCase) })
            $artifacts = [Collections.Generic.List[object]]::new()
            foreach ($path in $artifactPaths) {
                if (Test-Path -LiteralPath $path -PathType Leaf) { $artifacts.Add((Get-FileEvidence $path $EvidenceDirectory)) }
                elseif (Test-Path -LiteralPath $path -PathType Container) {
                    foreach ($file in Get-ChildItem -LiteralPath $path -File -Recurse | Sort-Object FullName) {
                        $artifacts.Add((Get-FileEvidence $file.FullName $EvidenceDirectory))
                    }
                }
            }
            $result.Artifacts = @($artifacts)
            $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resultPath -Encoding utf8NoBOM
            $results.Add([pscustomobject]$result)
            Save-Summary
        }
        if (!$result.Passed) { throw "回归失败并停止后续测试：$($case.Name)。$($result.Failure)" }
        Write-Output ('REGRESSION_OK {0}/{1} {2} exit={3} elapsedMs={4:F1}' -f $case.Sequence, $plan.Count, $case.Name, $result.ExitCode, $result.ElapsedMs)
    }
    $summary.Status = 'Passed'
    $summary.AllRegressionTestsPassed = $true
} catch {
    $caughtFailure = $_
    $summary.Status = 'Failed'
    $summary.Failure = $_.Exception.Message
} finally {
    $summary.CompletedAt = [DateTimeOffset]::Now.ToString('O')
    try {
        $sourceAfter = @(Get-SourceEvidence)
        $runtimeAfter = @(Get-RuntimeEvidence)
        $sourceAfter | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'source-hashes-after.json') -Encoding utf8NoBOM
        $runtimeAfter | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'runtime-hashes-after.json') -Encoding utf8NoBOM
        $summary.SourceDifferences = @(Compare-Evidence $sourceBefore $sourceAfter)
        $summary.RuntimeDifferences = @(Compare-Evidence $runtimeBefore $runtimeAfter)
        $summary.SourceFilesUnchanged = $summary.SourceDifferences.Count -eq 0
        $summary.RuntimeFilesUnchanged = $summary.RuntimeDifferences.Count -eq 0
        if (!$summary.SourceFilesUnchanged -or !$summary.RuntimeFilesUnchanged) {
            $summary.Status = 'Failed'; $summary.AllRegressionTestsPassed = $false
            $summary.Failure = '回归期间源码或测试运行依赖发生变化；逐项结果保留，整轮证据未通过一致性检查。'
        }
    } catch {
        $summary.Status = 'Failed'; $summary.AllRegressionTestsPassed = $false
        $summary.Failure = '无法完成源码/运行依赖的最终哈希检查：' + $_.Exception.Message
    }
    Save-Summary
}
Write-Output "RUNTIME_OPTIMIZATION_REGRESSIONS status=$($summary.Status) completed=$($results.Count)/$($plan.Count) performanceTargetsCertified=False"
Write-Output $summaryPath
if ($null -ne $caughtFailure) { throw $caughtFailure }
if (!$summary.AllRegressionTestsPassed) { throw $summary.Failure }
