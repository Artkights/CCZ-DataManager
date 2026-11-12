#requires -Version 7.0
<#
Aggregates the final, serial baseline/candidate runs made by RuntimePerformanceTests.
The reviewed benchmark Select method sets the intended remembered business/physical
view before the single native tab event. It does not call a smoke navigation handler
after that event, and does not activate an intermediate remembered physical table.
Only the original Samples entries are pooled; reported per-run percentiles are never
averaged or treated as raw observations. Missing evidence always fails completeness.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [string]$OutputDirectory,
    [switch]$FailOnGateFailure
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (!$OutputDirectory) { $OutputDirectory = $EvidenceDirectory }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$profiles = @('6.4', '6.5', '6.6')
$expectedCycles = 30
$expectedScenarios = @('人物', '兵种系', '兵种', '宝物', '策略', '商店', '拓展数据',
    'extend/Strategies', 'extend/Effects', 'physical/persons', 'physical/strategies',
    '__resources', '__ranges/0', '__ranges/1', '__ranges/2', '__formulas')
$extensionScenarios = @('__resources', '__ranges/0', '__ranges/1', '__ranges/2', '__formulas')
$softMemoryBytes = 1GB
$problems = [Collections.Generic.List[string]]::new()
$loaded = [Collections.Generic.List[object]]::new()
$runReports = [Collections.Generic.List[object]]::new()
$scenarioReports = [Collections.Generic.List[object]]::new()
$profileReports = [Collections.Generic.List[object]]::new()

function Read-Property($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}
function Read-Number($Value) {
    if ($null -eq $Value) { return $null }
    try {
        $number = [Convert]::ToDouble($Value, [Globalization.CultureInfo]::InvariantCulture)
        if ([double]::IsNaN($number) -or [double]::IsInfinity($number) -or $number -lt 0) { return $null }
        return $number
    } catch { return $null }
}
function Get-Statistics([object[]]$Values) {
    $sorted = @($Values | ForEach-Object { $n = Read-Number $_; if ($null -ne $n) { $n } } | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $p50 = [Math]::Clamp([int][Math]::Ceiling($sorted.Count * .5) - 1, 0, $sorted.Count - 1)
    $p95 = [Math]::Clamp([int][Math]::Ceiling($sorted.Count * .95) - 1, 0, $sorted.Count - 1)
    return [pscustomobject][ordered]@{ Count = $sorted.Count; P50Ms = $sorted[$p50]; P95Ms = $sorted[$p95]; MaximumMs = $sorted[-1] }
}
function Get-Ratio($Candidate, $Baseline) {
    if ($null -eq $Candidate -or $null -eq $Baseline -or $Baseline -le 0) { return $null }
    return $Candidate / $Baseline
}
function Format-Number($Number, [string]$Format = 'F2') {
    if ($null -eq $Number) { return '缺少证据' }
    return ([double]$Number).ToString($Format, [Globalization.CultureInfo]::InvariantCulture)
}
function Format-Pass($Passed) { if ($null -eq $Passed) { return '不适用' }; if ($Passed) { return '通过' }; return '未通过' }

foreach ($profile in $profiles) {
    foreach ($side in @('baseline', 'candidate')) {
        foreach ($run in 1..3) {
            $path = Join-Path $EvidenceDirectory "$side-final-$profile-$run/runtime-performance.json"
            $errors = [Collections.Generic.List[string]]::new()
            $data = $null
            $hash = $null
            if (!(Test-Path -LiteralPath $path -PathType Leaf)) { $errors.Add('缺少证据文件') }
            else {
                try {
                    $data = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 64
                    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
                } catch { $errors.Add('无法解析 JSON：' + $_.Exception.Message) }
            }
            $raw = @(Read-Property $data 'Samples' @())
            if ((Read-Property $data 'Schema') -ne 'ccz.runtime-performance.v2') { $errors.Add('Schema 不是 ccz.runtime-performance.v2') }
            if ((Read-Property $data 'Cycles') -ne $expectedCycles) { $errors.Add('Cycles 不是 30') }
            if ($raw.Count -ne $expectedCycles * $expectedScenarios.Count) { $errors.Add("原始样本数应为 480，实际 $($raw.Count)") }
            foreach ($scenario in $expectedScenarios) {
                $rows = @($raw | Where-Object { (Read-Property $_ 'Scenario') -eq $scenario })
                if ($rows.Count -ne $expectedCycles) { $errors.Add("$scenario 原始样本数应为 30，实际 $($rows.Count)") }
                $cycles = @($rows | ForEach-Object { Read-Property $_ 'Cycle' } | Sort-Object -Unique)
                if (($cycles -join ',') -ne ((1..$expectedCycles) -join ',')) { $errors.Add("$scenario 的 Cycle 没有恰好覆盖 1..30") }
            }
            foreach ($sample in $raw) {
                $name = Read-Property $sample 'Scenario'
                if ($name -notin $expectedScenarios) { $errors.Add("未知场景：$name") }
                $select = Read-Number (Read-Property $sample 'SelectMs')
                $ready = Read-Number (Read-Property $sample 'ReadyMs')
                if ($null -eq $select -or $null -eq $ready -or $ready -lt $select) { $errors.Add("$name 含无效 SelectMs/ReadyMs") }
            }
            $heartbeat = Read-Property $data 'Heartbeat'
            $heartbeatP95 = Read-Number (Read-Property $heartbeat 'P95Ms')
            if ($null -eq $heartbeatP95 -or (Read-Property $heartbeat 'Samples' 0) -le 0) { $errors.Add('缺少有效心跳计量') }
            $cold = Read-Number (Read-Property $data 'ColdInteractiveMs')
            if ($null -eq $cold) { $errors.Add('缺少有效冷启动计量') }
            $dpi = Read-Number (Read-Property $data 'ActualDpi')
            if ($null -eq $dpi -or $dpi -le 0 -or !(Read-Property $data 'Window')) { $errors.Add('缺少实际 DPI 或窗口尺寸') }
            $memory = @(Read-Property $data 'Memory' @())
            $stableMemory = @($memory | Where-Object { (Read-Property $_ 'Cycle' -1) -ge 5 })
            if ($stableMemory.Count -lt 6 -or @($stableMemory | Where-Object { (Read-Property $_ 'Cycle') -eq 30 }).Count -ne 1) {
                $errors.Add('缺少 5..30 轮自然运行内存样本')
            }
            foreach ($sample in $memory) {
                if ($null -eq (Read-Number (Read-Property $sample 'PrivateBytes'))) { $errors.Add('含无效专用内存计量') }
            }
            $private = @($stableMemory | ForEach-Object { Read-Number (Read-Property $_ 'PrivateBytes') } | Where-Object { $null -ne $_ })
            $peak = if ($private.Count) { ($private | Measure-Object -Maximum).Maximum } else { $null }
            $notes = [string](Read-Property $data 'Notes' '')
            if (!$notes.Contains('Identical UI event harness') -or !$notes.Contains('no duplicate smoke handler')) { $errors.Add('缺少同流程单事件 benchmark 声明') }
            $issues = @($errors | Select-Object -Unique)
            foreach ($issue in $issues) { $problems.Add("$side/$profile/run$run：$issue") }
            $runReport = [pscustomobject][ordered]@{
                Profile = $profile; Side = $side; Run = $run; EvidencePath = $path; Sha256 = $hash
                Version = Read-Property $data 'Version'; Source = Read-Property $data 'Source'
                ActualDpi = $dpi; Window = Read-Property $data 'Window'; Cycles = Read-Property $data 'Cycles'
                RawSampleCount = $raw.Count; EvidenceComplete = ($issues.Count -eq 0); Problems = $issues
                ColdInteractiveMs = $cold; FirstVisibleImageMs = Read-Number (Read-Property $data 'FirstVisibleImageMs')
                Heartbeat = [ordered]@{ P95Ms = $heartbeatP95; MaximumMs = Read-Property $heartbeat 'MaximumMs'
                    Samples = Read-Property $heartbeat 'Samples'; Meets100MsTarget = ($null -ne $heartbeatP95 -and $heartbeatP95 -le 100) }
                NaturalStableMemory = [ordered]@{ PeakPrivateBytes = $peak; PeakPrivateMiB = if ($null -ne $peak) { $peak / 1MB } else { $null }
                    MeetsSoft1GiBTarget = ($null -ne $peak -and $peak -le $softMemoryBytes)
                    EndMinusFirstPrivateBytes = if ($private.Count -ge 2) { $private[-1] - $private[0] } else { $null }
                    Samples = $memory }
                QueryMeasurements = @(Read-Property $data 'Queries' @()); Notes = $notes
            }
            $runReports.Add($runReport)
            $loaded.Add([pscustomobject]@{ Profile = $profile; Side = $side; Run = $run; Data = $data; Report = $runReport })
        }
    }

    $baselineRuns = @($loaded | Where-Object { $_.Profile -eq $profile -and $_.Side -eq 'baseline' })
    $candidateRuns = @($loaded | Where-Object { $_.Profile -eq $profile -and $_.Side -eq 'candidate' })
    $pairConsistency = @($baselineRuns + $candidateRuns | Where-Object { !$_.Report.EvidenceComplete }).Count -eq 0
    foreach ($run in 1..3) {
        $before = ($baselineRuns | Where-Object Run -eq $run).Report
        $after = ($candidateRuns | Where-Object Run -eq $run).Report
        if ($before.ActualDpi -ne $after.ActualDpi -or $before.Window -ne $after.Window -or $before.Source -ne $after.Source -or $before.Notes -ne $after.Notes) {
            $pairConsistency = $false
            $problems.Add("$profile/run$run：旧版与候选版的来源、实际 DPI、窗口尺寸或流程说明不一致")
        }
    }
    $profileRuns = @($baselineRuns + $candidateRuns)
    foreach ($property in @('ActualDpi', 'Window', 'Source', 'Notes')) {
        if (@($profileRuns | ForEach-Object { Read-Property $_.Report $property } | Sort-Object -Unique).Count -ne 1) {
            $pairConsistency = $false
            $problems.Add("$profile：六次运行的 $property 不一致，不能合并为同环境/同流程样本")
        }
    }
    foreach ($sideRuns in @(@($baselineRuns), @($candidateRuns))) {
        if (@($sideRuns | ForEach-Object { $_.Report.Version } | Sort-Object -Unique).Count -ne 1) {
            $pairConsistency = $false
            $problems.Add("$profile：同一侧的三次运行版本不一致")
        }
    }
    foreach ($scenario in $expectedScenarios) {
        $beforeRaw = @($baselineRuns | ForEach-Object { @(Read-Property $_.Data 'Samples' @()) } | Where-Object { (Read-Property $_ 'Scenario') -eq $scenario })
        $afterRaw = @($candidateRuns | ForEach-Object { @(Read-Property $_.Data 'Samples' @()) } | Where-Object { (Read-Property $_ 'Scenario') -eq $scenario })
        $before = Get-Statistics @($beforeRaw | ForEach-Object { Read-Property $_ 'ReadyMs' })
        $after = Get-Statistics @($afterRaw | ForEach-Object { Read-Property $_ 'ReadyMs' })
        $isExtension = $scenario -in $extensionScenarios
        $valid = $null -ne $before -and $null -ne $after -and $before.Count -eq 90 -and $after.Count -eq 90
        $ratio = if ($valid) { Get-Ratio $after.P95Ms $before.P95Ms } else { $null }
        $slow = $valid -and $before.P95Ms -gt 200
        $absolute = if ($isExtension) { $null } else { $valid -and $after.P95Ms -le 200 }
        $regression = if (!$isExtension) { $null } else { $valid -and (($null -ne $ratio -and $ratio -le 1.1) -or ($before.P95Ms -eq 0 -and $after.P95Ms -eq 0)) }
        $improvement = if (!$slow) { $null } else { $null -ne $ratio -and $ratio -le .6 }
        $passed = $valid -and ($isExtension -or $absolute) -and (!$isExtension -or $regression) -and (!$slow -or $improvement)
        $scenarioReports.Add([pscustomobject][ordered]@{
            Profile = $profile; Scenario = $scenario; Category = if ($isExtension) { '资源/范围/公式' } else { '常用业务/物理表' }
            BaselineReady = $before; CandidateReady = $after
            BaselineSelect = Get-Statistics @($beforeRaw | ForEach-Object { Read-Property $_ 'SelectMs' })
            CandidateSelect = Get-Statistics @($afterRaw | ForEach-Object { Read-Property $_ 'SelectMs' })
            P95CandidateToBaselineRatio = $ratio; P95ImprovementPercent = if ($null -ne $ratio) { (1 - $ratio) * 100 } else { $null }
            Flags = [ordered]@{ NinetySamplesPerSide = $valid; CandidateP95AtMost200Ms = $absolute
                ExtensionRegressionAtMost10Percent = $regression; BaselineP95Over200Ms = $slow
                SlowScenarioImprovementAtLeast40Percent = $improvement; Passed = $passed }
        })
    }
    $beforeCold = Get-Statistics @($baselineRuns | ForEach-Object { $_.Report.ColdInteractiveMs })
    $afterCold = Get-Statistics @($candidateRuns | ForEach-Object { $_.Report.ColdInteractiveMs })
    $coldRatio = if ($null -ne $beforeCold -and $null -ne $afterCold) { Get-Ratio $afterCold.P50Ms $beforeCold.P50Ms } else { $null }
    $complete = @($baselineRuns + $candidateRuns | Where-Object { !$_.Report.EvidenceComplete }).Count -eq 0
    $scenarioPass = @($scenarioReports | Where-Object { $_.Profile -eq $profile -and !$_.Flags.Passed }).Count -eq 0
    $heartbeatPass = @($candidateRuns | Where-Object { !$_.Report.Heartbeat.Meets100MsTarget }).Count -eq 0
    $coldPass = $null -ne $coldRatio -and $coldRatio -le 1.1 -and $beforeCold.Count -eq 3 -and $afterCold.Count -eq 3
    $memoryPass = @($candidateRuns | Where-Object { !$_.Report.NaturalStableMemory.MeetsSoft1GiBTarget }).Count -eq 0
    $profileReports.Add([pscustomobject][ordered]@{
        Profile = $profile; BaselineCold = $beforeCold; CandidateCold = $afterCold; ColdMedianRatio = $coldRatio
        Flags = [ordered]@{ CompleteEvidence = $complete; MatchingRunEnvironmentAndMethod = $pairConsistency
            ScenarioPerformancePassed = $scenarioPass; EveryCandidateRunHeartbeatP95AtMost100Ms = $heartbeatPass
            ColdMedianRegressionAtMost10Percent = $coldPass; MemorySoftTargetPassed = $memoryPass
            PerformancePassed = ($complete -and $pairConsistency -and $scenarioPass -and $heartbeatPass -and $coldPass) }
    })
}

$limitations = @(
    '统计直接合并每场景三次独立运行的 90 条 Samples，使用 nearest-rank（ceil(N×P)-1）；不平均三次 P95。ReadyMs 为门槛依据，SelectMs 单列。',
    'RuntimePerformanceTests.Select 在唯一真实标签事件前设置预期业务/物理视图；仅在目标确实不同时选择表或分类。没有额外 smoke 导航调用，也没有中间物理表激活。旧版与候选版必须使用同一份修正后的 harness。',
    '每次候选运行的心跳 P95 分别要求 ≤100 ms；旧版心跳作为对照逐次列出。原始心跳队列未写入 JSON，不能把不同运行的 P95 合并成总体 P95。',
    '冷启动比较三次 ColdInteractiveMs 的中位数；文件复制、源码构建、截图和强制 GC 不在计时内。首次全局可见图片时间单列，不能替代逐页图片完成验收。',
    '实际运行 DPI 与窗口尺寸来自每份原始报告。100%/125%/150% 字体模拟属于单独布局验收，不能证明真实显示器 DPI 切换，也不应混入本次固定窗口运行性能统计。',
    '原始 JSON 的 FirstEntries 保留逐场景首次进入耗时，热切换统计只合并 Samples；逐页图片完成时间、完整 CPU/机器指纹或字体比例未纳入当前报告。聚合主机信息仅描述生成报告的机器，不能独立证明所有输入都来自该机器。',
    '内存以自然运行第 5..30 轮的峰值专用内存与 1 GiB 软目标比较，单独给出结论；30 轮记录不能代替 100 轮平台期、句柄及反复开关项目/旧工作区回收验收。没有把强制 GC 数值混入自然运行数据。',
    '查询耗时保留每次运行的原始条件、数量与耗时，未将一次查询伪装成 90 条样本。动画样本、真实 DPI 和游戏内实战的覆盖应以独立验证记录为准。'
)
$fixedEnvironment = @($runReports | Where-Object { $_.ActualDpi -gt 0 -and ![string]::IsNullOrWhiteSpace($_.Window) }).Count -eq 18 -and
    @($runReports | ForEach-Object { $_.ActualDpi } | Sort-Object -Unique).Count -eq 1 -and
    @($runReports | ForEach-Object { $_.Window } | Sort-Object -Unique).Count -eq 1
if (!$fixedEnvironment) { $problems.Add('18 次运行的实际 DPI 或窗口尺寸不一致，不能视为同一固定显示环境。') }
$allPerformance = @($profileReports | Where-Object { !$_.Flags.PerformancePassed }).Count -eq 0 -and $fixedEnvironment
$allMemory = @($profileReports | Where-Object { !$_.Flags.MemorySoftTargetPassed }).Count -eq 0
$report = [ordered]@{
    Schema = 'ccz.runtime-performance-comparison.v1'; GeneratedAt = [DateTimeOffset]::Now.ToString('O')
    EvidenceDirectory = $EvidenceDirectory
    AggregationHost = [ordered]@{ MachineName = [Environment]::MachineName; OS = [Environment]::OSVersion.VersionString
        ProcessorCount = [Environment]::ProcessorCount; ProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        PowerShell = $PSVersionTable.PSVersion.ToString(); Note = '聚合主机信息，不是原始 benchmark 机器遥测。' }
    Thresholds = [ordered]@{ ExpectedRunsPerSidePerProfile = 3; ExpectedRawSamplesPerScenarioPerSide = 90
        BusinessAndPhysicalP95Ms = 200; CandidateHeartbeatP95MsPerRun = 100
        SlowBaselineThresholdMs = 200; MinimumSlowScenarioImprovementPercent = 40
        ExtensionMaximumRegressionPercent = 10; ColdMedianMaximumRegressionPercent = 10; SoftPrivateMemoryBytes = $softMemoryBytes }
    Flags = [ordered]@{ AllPerformanceGatesPassed = $allPerformance; AllMemorySoftTargetsPassed = $allMemory
        AllPerformanceAndMemoryTargetsPassed = ($allPerformance -and $allMemory)
        FixedActualDpiAndWindowAcrossRuns = $fixedEnvironment
        EvidenceProblemsPresent = ($problems.Count -gt 0) }
    Profiles = @($profileReports); Scenarios = @($scenarioReports); Runs = @($runReports)
    EvidenceProblems = @($problems); MethodAndLimitations = $limitations
}
$jsonPath = Join-Path $OutputDirectory 'performance-comparison.json'
$markdownPath = Join-Path $OutputDirectory 'performance-comparison.md'
$report | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $jsonPath -Encoding utf8NoBOM

$markdown = [Collections.Generic.List[string]]::new()
$markdown.Add('# 布局与读取性能优化：旧版与候选版对比')
$markdown.Add('')
$markdown.Add("生成时间：$($report.GeneratedAt)。证据目录：$EvidenceDirectory。")
$markdown.Add('')
$markdown.Add("性能门槛：**$(Format-Pass $allPerformance)**；约 1 GiB 内存软目标：**$(Format-Pass $allMemory)**。缺失、无效或不一致的证据会阻止性能通过。")
$markdown.Add('')
$markdown.Add('## 基底结论')
$markdown.Add('')
$markdown.Add('| 基底 | 证据完整 | 同环境/流程 | 场景门槛 | 每次候选心跳 | 冷启动旧/新中位数 ms | 冷启动比值 | 性能结论 | 内存软目标 |')
$markdown.Add('|---|---|---|---|---|---|---|---|---|')
foreach ($profile in $profileReports) {
    $markdown.Add("| $($profile.Profile) | $(Format-Pass $profile.Flags.CompleteEvidence) | $(Format-Pass $profile.Flags.MatchingRunEnvironmentAndMethod) | $(Format-Pass $profile.Flags.ScenarioPerformancePassed) | $(Format-Pass $profile.Flags.EveryCandidateRunHeartbeatP95AtMost100Ms) | $(Format-Number (Read-Property $profile.BaselineCold 'P50Ms')) / $(Format-Number (Read-Property $profile.CandidateCold 'P50Ms')) | $(Format-Number $profile.ColdMedianRatio 'F3') | $(Format-Pass $profile.Flags.PerformancePassed) | $(Format-Pass $profile.Flags.MemorySoftTargetPassed) |")
}
foreach ($profile in $profiles) {
    $markdown.Add(''); $markdown.Add("## $profile 热切换（每侧每场景 90 条原始样本）"); $markdown.Add('')
    $markdown.Add('| 场景 | 旧 P50/P95/max ms | 新 P50/P95/max ms | P95 改善 | ≤200 ms | 扩展退化≤10% | 旧慢场景改善≥40% | 结论 |')
    $markdown.Add('|---|---|---|---|---|---|---|---|')
    foreach ($scenario in @($scenarioReports | Where-Object Profile -eq $profile)) {
        $before = $scenario.BaselineReady; $after = $scenario.CandidateReady
        $markdown.Add("| $($scenario.Scenario) | $(Format-Number (Read-Property $before 'P50Ms')) / $(Format-Number (Read-Property $before 'P95Ms')) / $(Format-Number (Read-Property $before 'MaximumMs')) | $(Format-Number (Read-Property $after 'P50Ms')) / $(Format-Number (Read-Property $after 'P95Ms')) / $(Format-Number (Read-Property $after 'MaximumMs')) | $(Format-Number $scenario.P95ImprovementPercent)% | $(Format-Pass $scenario.Flags.CandidateP95AtMost200Ms) | $(Format-Pass $scenario.Flags.ExtensionRegressionAtMost10Percent) | $(Format-Pass $scenario.Flags.SlowScenarioImprovementAtLeast40Percent) | $(Format-Pass $scenario.Flags.Passed) |")
    }
}
$markdown.Add(''); $markdown.Add('## 每次运行的环境、心跳与自然内存'); $markdown.Add('')
$markdown.Add('| 基底/版本/轮次 | 实际 DPI | 窗口 | 心跳 P95/max ms | 心跳≤100 ms | 第5..30轮峰值 MiB | 末次减首次 MiB | GDI/USER 末次 | 证据完整 |')
$markdown.Add('|---|---|---|---|---|---|---|---|---|')
foreach ($run in $runReports) {
    $memory = $run.NaturalStableMemory
    $samples = @($memory.Samples)
    $last = if ($samples.Count) { $samples[-1] } else { $null }
    $delta = if ($null -ne $memory.EndMinusFirstPrivateBytes) { $memory.EndMinusFirstPrivateBytes / 1MB } else { $null }
    $markdown.Add("| $($run.Profile)/$($run.Side)/$($run.Run) ($($run.Version)) | $($run.ActualDpi) | $($run.Window) | $(Format-Number $run.Heartbeat.P95Ms) / $(Format-Number $run.Heartbeat.MaximumMs) | $(Format-Pass $run.Heartbeat.Meets100MsTarget) | $(Format-Number $memory.PeakPrivateMiB) | $(Format-Number $delta) | $(Read-Property $last 'Gdi') / $(Read-Property $last 'User') | $(Format-Pass $run.EvidenceComplete) |")
}
$markdown.Add(''); $markdown.Add('旧版心跳标志用于对照；性能总门槛要求三次候选运行全部通过。内存软目标独立判断，峰值超标不会被性能通过结论隐藏。')
$markdown.Add(''); $markdown.Add('## 方法、机器信息与验收限制'); $markdown.Add('')
foreach ($limitation in $limitations) { $markdown.Add('- ' + $limitation) }
$markdown.Add('')
$markdown.Add("聚合主机：$($report.AggregationHost.MachineName)，$($report.AggregationHost.OS)，$($report.AggregationHost.ProcessorCount) 逻辑处理器，$($report.AggregationHost.ProcessArchitecture)，PowerShell $($report.AggregationHost.PowerShell)。")
$markdown.Add(''); $markdown.Add('## 证据问题'); $markdown.Add('')
if (!$problems.Count) { $markdown.Add('没有发现缺失文件、原始样本数或运行环境一致性问题。') }
else { foreach ($problem in $problems) { $markdown.Add('- ' + $problem) } }
$markdown.Add(''); $markdown.Add('完整 JSON 包含各场景 SelectMs 分布、各次查询原始计量、每份输入路径与 SHA256，以及全部布尔门槛标志。未通过项应继续定位，不能据此报告成通过。')
$markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8NoBOM
Write-Output "PERFORMANCE_COMPARISON performancePassed=$allPerformance memorySoftTargetPassed=$allMemory problems=$($problems.Count)"
Write-Output $jsonPath
Write-Output $markdownPath
if ($FailOnGateFailure -and !$allPerformance) { throw '性能证据或门槛未通过；完整比较报告已经写入。' }
