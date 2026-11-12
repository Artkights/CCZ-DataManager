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
if ($releaseVerification.Version -ne $Version -or !$releaseVerification.Passed) { throw '本版回归验证尚未通过。' }
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
    $releaseTitle = 'Data 管理器 ' + $Version + ' · 三组属性筛选'
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
