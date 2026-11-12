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
$releaseAssets = @('data管理器.exe', ('DataManager-' + $Version + '-win-x64.zip'),
    ('Data管理器及共享组件源码-' + $Version + '.zip'), '构建与源码包记录.json', '验证记录.json', 'SHA256SUMS.txt')
foreach ($releaseAssetName in $releaseAssets) {
    if (!(Test-Path -LiteralPath (Join-Path $ReleaseDirectory $releaseAssetName) -PathType Leaf)) {
        throw "缺少发布附件：$releaseAssetName"
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
    if (!$releaseObject) {
        $releasePayload = @{ tag_name=$releaseTag; target_commitish=$SourceCommit; name=('Data 管理器 ' + $Version + ' · CSV 格式导入'); body=$releaseNotes; draft=$true; prerelease=$false } | ConvertTo-Json -Depth 4
        $releaseObject = Invoke-RestMethod -Method Post -Uri ($releaseApiBase + '/releases') -Headers $releaseHeaders -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($releasePayload))
    }
    if (!$releaseObject.draft) { throw '该版本已经正式发布，请使用新版本号。' }
    $releaseUploadBase = $releaseObject.upload_url -replace '\{.*$', ''
    foreach ($releaseAssetName in $releaseAssets) {
        $releaseAssetPath = Join-Path $ReleaseDirectory $releaseAssetName
        $releaseAssetHash = (Get-FileHash -LiteralPath $releaseAssetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $releaseUploaded = @($releaseObject.assets | Where-Object name -EQ $releaseAssetName)
        if ($releaseUploaded.Count) {
            if ($releaseUploaded.Count -ne 1 -or $releaseUploaded[0].size -ne (Get-Item -LiteralPath $releaseAssetPath).Length -or
                $releaseUploaded[0].digest -ne ('sha256:' + $releaseAssetHash)) { throw "现有草稿附件不一致：$releaseAssetName" }
            Write-Output "已核对草稿附件：$releaseAssetName"
            continue
        }
        $releaseUpload = Invoke-RestMethod -Method Post -Uri ($releaseUploadBase + '?name=' + [uri]::EscapeDataString($releaseAssetName)) -Headers $releaseHeaders -ContentType 'application/octet-stream' -InFile $releaseAssetPath
        if ($releaseUpload.state -ne 'uploaded' -or $releaseUpload.size -ne (Get-Item -LiteralPath $releaseAssetPath).Length -or
            $releaseUpload.digest -ne ('sha256:' + $releaseAssetHash)) { throw "上传附件复核失败：$releaseAssetName" }
        Write-Output "已上传并校验：$releaseAssetName"
    }
    $releaseCurrent = Invoke-RestMethod -Uri ($releaseApiBase + '/releases/' + $releaseObject.id) -Headers $releaseHeaders
    if (@($releaseCurrent.assets).Count -ne $releaseAssets.Count) { throw '发布附件数量不一致。' }
    foreach ($releaseAssetName in $releaseAssets) {
        $releaseAssetPath = Join-Path $ReleaseDirectory $releaseAssetName
        $releaseAssetRow = @($releaseCurrent.assets | Where-Object name -EQ $releaseAssetName)
        $releaseAssetHash = (Get-FileHash -LiteralPath $releaseAssetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($releaseAssetRow.Count -ne 1 -or $releaseAssetRow[0].size -ne (Get-Item -LiteralPath $releaseAssetPath).Length -or
            $releaseAssetRow[0].digest -ne ('sha256:' + $releaseAssetHash)) { throw "发布附件不一致：$releaseAssetName" }
    }
    $releasePublishPayload = @{ draft=$false; prerelease=$false; make_latest='true' } | ConvertTo-Json
    $releasePublished = Invoke-RestMethod -Method Patch -Uri ($releaseApiBase + '/releases/' + $releaseObject.id) -Headers $releaseHeaders -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($releasePublishPayload))
    $releasePublished | Select-Object id,tag_name,html_url,draft,prerelease,published_at | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReleaseDirectory 'GitHub发布回执.json') -Encoding UTF8
    Write-Output ('已正式发布：' + $releasePublished.html_url)
} finally { $releaseHeaders = $null }
