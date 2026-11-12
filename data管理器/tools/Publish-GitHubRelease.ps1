[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$NotesPath
)
$ErrorActionPreference = 'Stop'
$releaseApiBase = 'https://api.github.com/repos/Artkights/CCZ-DataManager'
$releaseTag = 'v' + $Version
$ReleaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
$releaseBuild = Get-Content -LiteralPath (Join-Path $ReleaseDirectory '构建与源码包记录.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($releaseBuild.Version -ne $Version -or $releaseBuild.SourceCommit -ne $SourceCommit -or
    !$releaseBuild.SourceIndependentBuildPassed -or !$releaseBuild.SourceArchiveMatchesWorkspace) {
    throw '发布版本、源码提交或源码包独立构建记录不一致。'
}
$releaseAssets = @(
    [pscustomobject]@{ LocalName='data管理器.exe'; RemoteName=('DataManager-' + $Version + '.exe'); Label='data管理器.exe' }
    [pscustomobject]@{ LocalName=('DataManager-' + $Version + '-win-x64.zip'); RemoteName=('DataManager-' + $Version + '-win-x64.zip'); Label='运行包' }
    [pscustomobject]@{ LocalName=('Data管理器及共享组件源码-' + $Version + '.zip'); RemoteName=('DataManager-source-' + $Version + '.zip'); Label='Data管理器及共享组件源码' }
    [pscustomobject]@{ LocalName='构建与源码包记录.json'; RemoteName=('build-manifest-' + $Version + '.json'); Label='构建与源码包记录' }
    [pscustomobject]@{ LocalName='验证记录.json'; RemoteName=('verification-' + $Version + '.json'); Label='验证记录' }
    [pscustomobject]@{ LocalName='SHA256SUMS.txt'; RemoteName='SHA256SUMS.txt'; Label='SHA256SUMS.txt' }
)
if (@($releaseAssets | Group-Object RemoteName | Where-Object Count -GT 1).Count) { throw '远程发布附件名称重复。' }
foreach ($releaseAsset in $releaseAssets) {
    $releaseAssetPath = Join-Path $ReleaseDirectory $releaseAsset.LocalName
    if (!(Test-Path -LiteralPath $releaseAssetPath -PathType Leaf)) {
        throw ('缺少发布附件：' + $releaseAsset.LocalName)
    }
    $releaseAsset | Add-Member -NotePropertyName Path -NotePropertyValue $releaseAssetPath
    $releaseAsset | Add-Member -NotePropertyName Size -NotePropertyValue (Get-Item -LiteralPath $releaseAssetPath).Length
    $releaseAsset | Add-Member -NotePropertyName Digest -NotePropertyValue ('sha256:' + (Get-FileHash -LiteralPath $releaseAssetPath -Algorithm SHA256).Hash.ToLowerInvariant())
}
function Assert-ReleaseAsset($Asset, $Expected, [string]$Context) {
    if ($Asset.name -cne $Expected.RemoteName -or $Asset.state -cne 'uploaded' -or
        $Asset.size -ne $Expected.Size -or $Asset.digest -cne $Expected.Digest) {
        throw ($Context + '：' + $Expected.LocalName + ' -> ' + $Expected.RemoteName)
    }
}
$releaseVerification = Get-Content -LiteralPath (Join-Path $ReleaseDirectory '验证记录.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($releaseVerification.Version -ne $Version -or $releaseVerification.SourceCommit -ne $SourceCommit -or
    $releaseVerification.Passed -isnot [bool] -or !$releaseVerification.Passed) {
    throw '本版回归验证尚未通过，或版本、源码提交与本次发布不一致。'
}
function Get-ReleaseRequiredProperty($Object, [string]$Name, [string]$Context) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        throw "$Context 缺少字段 $Name。"
    }
    return $Object.PSObject.Properties[$Name].Value
}
function Get-ReleaseBoolean($Object, [string]$Name, [string]$Context) {
    $releaseValue = Get-ReleaseRequiredProperty $Object $Name $Context
    if ($releaseValue -isnot [bool]) { throw "$Context 的 $Name 必须是 JSON 布尔值。" }
    return $releaseValue
}
function Get-ReleasePerformanceReference($Record, [string]$Context) {
    $releaseReference = Get-ReleaseRequiredProperty $Record 'PerformanceReport' $Context
    $releaseRelative = Get-ReleaseRequiredProperty $releaseReference 'Path' "$Context.PerformanceReport"
    if ($releaseRelative -isnot [string] -or [string]::IsNullOrWhiteSpace($releaseRelative) -or
        [IO.Path]::IsPathRooted($releaseRelative)) {
        throw "$Context.PerformanceReport.Path 必须是发布目录内 JSON 文件的相对路径。"
    }
    $releaseReportPath = [IO.Path]::GetFullPath((Join-Path $ReleaseDirectory $releaseRelative))
    $releaseDirectoryPrefix = $ReleaseDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$releaseReportPath.StartsWith($releaseDirectoryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetExtension($releaseReportPath) -ine '.json' -or
        !(Test-Path -LiteralPath $releaseReportPath -PathType Leaf)) {
        throw "$Context.PerformanceReport.Path 超出发布目录或不是已有 JSON 文件。"
    }
    # A junction/symlink below the release root must not redirect this reference elsewhere.
    $releaseReportItem = Get-Item -LiteralPath $releaseReportPath -Force
    for ($releaseAncestor = $releaseReportItem; $null -ne $releaseAncestor -and
        !$releaseAncestor.FullName.TrimEnd('\', '/').Equals($ReleaseDirectory.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase);
        $releaseAncestor = if ($releaseAncestor -is [IO.DirectoryInfo]) { $releaseAncestor.Parent } else { $releaseAncestor.Directory }) {
        if (($releaseAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Context.PerformanceReport.Path 不能经过发布目录内的链接或联接点。"
        }
    }
    $releaseSha = Get-ReleaseRequiredProperty $releaseReference 'Sha256' "$Context.PerformanceReport"
    if ($releaseSha -isnot [string] -or $releaseSha -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-FileHash -LiteralPath $releaseReportPath -Algorithm SHA256).Hash -ine $releaseSha) {
        throw "$Context.PerformanceReport.Sha256 与所引用的正式比较报告不一致。"
    }
    $releasePerformancePassed = Get-ReleaseBoolean $releaseReference 'PerformanceGatesPassed' "$Context.PerformanceReport"
    $releaseMemoryPassed = Get-ReleaseBoolean $releaseReference 'MemorySoftTargetPassed' "$Context.PerformanceReport"
    if (!$releasePerformancePassed) { throw "$Context 记录的性能硬门槛尚未通过。" }
    return [pscustomobject]@{ Path=$releaseReportPath; Sha256=$releaseSha.ToUpperInvariant()
        PerformanceGatesPassed=$releasePerformancePassed; MemorySoftTargetPassed=$releaseMemoryPassed }
}
$releasePerformance = Get-ReleaseRequiredProperty $releaseVerification 'Performance' '验证记录'
if (!(Get-ReleaseBoolean $releasePerformance 'Passed' '验证记录.Performance')) {
    throw '本版性能硬门槛尚未通过，不能发布。'
}
$releaseBuildPerformance = Get-ReleasePerformanceReference $releaseBuild '构建与源码包记录'
$releaseVerifiedPerformance = Get-ReleasePerformanceReference $releaseVerification '验证记录'
if ($releaseBuildPerformance.Path -ine $releaseVerifiedPerformance.Path -or
    $releaseBuildPerformance.Sha256 -ine $releaseVerifiedPerformance.Sha256 -or
    $releaseBuildPerformance.MemorySoftTargetPassed -ne $releaseVerifiedPerformance.MemorySoftTargetPassed) {
    throw '构建记录与验证记录没有引用同一份、同一结论的正式性能报告。'
}
$releaseComparison = Get-Content -LiteralPath $releaseBuildPerformance.Path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 64
if ((Get-ReleaseRequiredProperty $releaseComparison 'Schema' '正式性能报告') -cne 'ccz.runtime-performance-comparison.v1') {
    throw '正式性能报告 Schema 不受本次发布流程支持。'
}
$releaseComparisonFlags = Get-ReleaseRequiredProperty $releaseComparison 'Flags' '正式性能报告'
if (!(Get-ReleaseBoolean $releaseComparisonFlags 'AllPerformanceGatesPassed' '正式性能报告.Flags') -or
    !(Get-ReleaseBoolean $releaseComparisonFlags 'FixedActualDpiAndWindowAcrossRuns' '正式性能报告.Flags') -or
    (Get-ReleaseBoolean $releaseComparisonFlags 'EvidenceProblemsPresent' '正式性能报告.Flags') -or
    @(Get-ReleaseRequiredProperty $releaseComparison 'EvidenceProblems' '正式性能报告').Count -ne 0) {
    throw '正式性能报告的门槛、证据完整性或显示环境一致性尚未通过。'
}
$releaseComparisonMemory = Get-ReleaseBoolean $releaseComparisonFlags 'AllMemorySoftTargetsPassed' '正式性能报告.Flags'
if ($releaseComparisonMemory -ne $releaseBuildPerformance.MemorySoftTargetPassed -or
    (Get-ReleaseBoolean $releaseComparisonFlags 'AllPerformanceAndMemoryTargetsPassed' '正式性能报告.Flags') -ne $releaseComparisonMemory) {
    throw '正式性能报告与构建、验证记录的内存软目标结论不一致。'
}
$releaseComparisonProfiles = @(Get-ReleaseRequiredProperty $releaseComparison 'Profiles' '正式性能报告')
$releaseComparisonRuns = @(Get-ReleaseRequiredProperty $releaseComparison 'Runs' '正式性能报告')
if ($releaseComparisonProfiles.Count -ne 3 -or $releaseComparisonRuns.Count -ne 18) {
    throw '正式性能报告必须包含三基底、每侧三次独立运行的完整记录。'
}
foreach ($releaseProfile in @('6.4', '6.5', '6.6')) {
    $releaseProfileRows = @($releaseComparisonProfiles | Where-Object Profile -CEQ $releaseProfile)
    if ($releaseProfileRows.Count -ne 1 -or
        !(Get-ReleaseBoolean $releaseProfileRows[0].Flags 'PerformancePassed' "正式性能报告.$releaseProfile.Flags")) {
        throw "正式性能报告的 $releaseProfile 基底性能门槛尚未通过。"
    }
    foreach ($releaseSide in @('baseline', 'candidate')) {
        foreach ($releaseRun in 1..3) {
            $releaseRunRows = @($releaseComparisonRuns | Where-Object {
                $_.Profile -ceq $releaseProfile -and $_.Side -ceq $releaseSide -and $_.Run -eq $releaseRun
            })
            if ($releaseRunRows.Count -ne 1 -or
                !(Get-ReleaseBoolean $releaseRunRows[0] 'EvidenceComplete' "正式性能报告.$releaseSide/$releaseProfile/$releaseRun")) {
                throw "正式性能报告缺少有效且唯一的 $releaseSide/$releaseProfile/$releaseRun 运行。"
            }
            if ($releaseSide -ceq 'candidate') {
                $releaseRunVersion = Get-ReleaseRequiredProperty $releaseRunRows[0] 'Version' '候选性能运行'
                if ($releaseRunVersion -isnot [string] -or $releaseRunVersion.Split('+')[0] -cne $Version) {
                    throw '正式性能报告中的候选程序版本与本次发布不一致。'
                }
            }
        }
    }
}
# The current comparison schema records candidate versions, but has no required commit field.
# The two release records bind its exact SHA256 to their common SourceCommit. Validate any
# explicit provenance fields too when an exporter adds them, without inventing that evidence.
foreach ($releaseProvenance in @('Version', 'SourceCommit')) {
    if ($null -ne $releaseComparison.PSObject.Properties[$releaseProvenance]) {
        $releaseExpected = if ($releaseProvenance -ceq 'Version') { $Version } else { $SourceCommit }
        if ($releaseComparison.PSObject.Properties[$releaseProvenance].Value -cne $releaseExpected) {
            throw "正式性能报告的 $releaseProvenance 与本次发布不一致。"
        }
    }
}
Write-Output ('正式性能硬门槛已通过；约 1 GiB 内存软目标：' + $(if ($releaseComparisonMemory) { '通过' } else { '超出，按独立软目标如实记录' }))
$releaseExpectedHashes = @{
    'data管理器.exe' = $releaseBuild.ExecutableSHA256
    ('Data管理器及共享组件源码-' + $Version + '.zip') = $releaseBuild.SourceArchiveSHA256
}
foreach ($releaseAssetName in $releaseExpectedHashes.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory $releaseAssetName) -Algorithm SHA256).Hash -ne $releaseExpectedHashes[$releaseAssetName]) {
        throw "发布附件与构建记录不一致：$releaseAssetName"
    }
}
$releaseCredentialLines = "protocol=https`nhost=github.com`n`n" | git -c credential.interactive=never credential fill 2>$null
$releaseCredentialMap = @{}
foreach ($releaseCredentialLine in $releaseCredentialLines) {
    if ($releaseCredentialLine -match '^([^=]+)=(.*)$') { $releaseCredentialMap[$matches[1]] = $matches[2] }
}
if (!$releaseCredentialMap['password']) { throw '无法读取 GitHub 发布凭证。' }
$releaseHeaders = @{ Authorization=('Bearer ' + $releaseCredentialMap['password']); Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
$releaseCredentialMap.Clear()
$releaseCredentialLines = $null
try {
    $releaseRemoteMain = Invoke-RestMethod -Uri ($releaseApiBase + '/git/ref/heads/main') -Headers $releaseHeaders
    if ($releaseRemoteMain.object.sha -ne $SourceCommit) { throw '远程 main 与本次发布源码提交不一致。' }
    $releaseRemoteTag = (Invoke-RestMethod -Uri ($releaseApiBase + '/git/ref/tags/' + $releaseTag) -Headers $releaseHeaders).object
    if ($releaseRemoteTag.type -eq 'tag') { $releaseRemoteTag = (Invoke-RestMethod -Uri $releaseRemoteTag.url -Headers $releaseHeaders).object }
    if ($releaseRemoteTag.sha -ne $SourceCommit) { throw '版本标签与本次发布源码提交不一致。' }
    $releaseObject = $null
    try { $releaseObject = Invoke-RestMethod -Uri ($releaseApiBase + '/releases/tags/' + $releaseTag) -Headers $releaseHeaders }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
    $releaseNotes = Get-Content -LiteralPath $NotesPath -Raw -Encoding UTF8
    $releaseTitle = 'Data 管理器 ' + $Version + ' · 布局与读取性能优化'
    if (!$releaseObject) {
        $releasePayload = @{ tag_name=$releaseTag; target_commitish=$SourceCommit; name=$releaseTitle; body=$releaseNotes; draft=$true; prerelease=$false } | ConvertTo-Json -Depth 4
        $releaseObject = Invoke-RestMethod -Method Post -Uri ($releaseApiBase + '/releases') -Headers $releaseHeaders -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($releasePayload))
    }
    if (!$releaseObject.draft) { throw '该版本已经正式发布，请使用新版本号。' }
    $releaseUploadBase = $releaseObject.upload_url -replace '\{.*$', ''
    foreach ($releaseAsset in $releaseAssets) {
        $releaseUploaded = @($releaseObject.assets | Where-Object { $_.name -ceq $releaseAsset.RemoteName })
        if ($releaseUploaded.Count) {
            if ($releaseUploaded.Count -ne 1) { throw ('现有草稿附件名称重复：' + $releaseAsset.RemoteName) }
            Assert-ReleaseAsset $releaseUploaded[0] $releaseAsset '现有草稿附件不一致'
            Write-Output ('已核对草稿附件：' + $releaseAsset.RemoteName)
            continue
        }
        $releaseUpload = Invoke-RestMethod -Method Post -Uri ($releaseUploadBase + '?name=' + [uri]::EscapeDataString($releaseAsset.RemoteName) + '&label=' + [uri]::EscapeDataString($releaseAsset.Label)) -Headers $releaseHeaders -ContentType 'application/octet-stream' -InFile $releaseAsset.Path
        Assert-ReleaseAsset $releaseUpload $releaseAsset '上传附件复核失败'
        Write-Output ('已上传并校验：' + $releaseAsset.LocalName + ' -> ' + $releaseAsset.RemoteName)
    }
    $releaseCurrent = Invoke-RestMethod -Uri ($releaseApiBase + '/releases/' + $releaseObject.id) -Headers $releaseHeaders
    if (@($releaseCurrent.assets).Count -ne $releaseAssets.Count) { throw '发布附件数量不一致。' }
    foreach ($releaseAsset in $releaseAssets) {
        $releaseAssetRow = @($releaseCurrent.assets | Where-Object { $_.name -ceq $releaseAsset.RemoteName })
        if ($releaseAssetRow.Count -ne 1) { throw ('发布附件数量或名称不一致：' + $releaseAsset.RemoteName) }
        Assert-ReleaseAsset $releaseAssetRow[0] $releaseAsset '发布附件不一致'
    }
    $releasePublishPayload = @{ name=$releaseTitle; body=$releaseNotes; draft=$false; prerelease=$false; make_latest='true' } | ConvertTo-Json
    $releasePublished = Invoke-RestMethod -Method Patch -Uri ($releaseApiBase + '/releases/' + $releaseObject.id) -Headers $releaseHeaders -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($releasePublishPayload))
    if ($releasePublished.id -ne $releaseObject.id -or $releasePublished.tag_name -cne $releaseTag -or
        $releasePublished.draft -or $releasePublished.prerelease) { throw '正式发布回执未对应本次版本。' }
    $releasePublished | Select-Object id,tag_name,html_url,draft,prerelease,published_at | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReleaseDirectory 'GitHub发布回执.json') -Encoding UTF8
    $releaseLatest = Invoke-RestMethod -Uri ($releaseApiBase + '/releases/latest') -Headers $releaseHeaders
    if ($releaseLatest.id -ne $releasePublished.id -or $releaseLatest.tag_name -cne $releaseTag -or
        $releaseLatest.draft -or $releaseLatest.prerelease) { throw '已发布版本尚未成为 GitHub 最新 Release，请核对发布状态。' }
    Write-Output ('已正式发布：' + $releasePublished.html_url)
} finally { $releaseHeaders = $null }
