[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$Destination = [IO.Path]::GetFullPath($Destination)
if ($RepositoryRoot -eq $Destination) { throw '源码与GitHub检出必须分开。' }
if (!(Test-Path -LiteralPath (Join-Path $Destination '.git'))) { throw '目标不是现有 Git 检出。' }
$taskRemote = (& git -C $Destination remote get-url origin)
if ($LASTEXITCODE -ne 0 -or $taskRemote -notmatch 'github\.com[:/]Artkights/CCZ-DataManager(?:\.git)?$') {
    throw "目标必须为 Artkights/CCZ-DataManager；实际：$taskRemote"
}
$taskStatus = @(& git -C $Destination status --porcelain)
if ($LASTEXITCODE -ne 0 -or $taskStatus.Count -gt 0) { throw '目标检出包含未提交改动，请使用干净检出。' }
. (Join-Path $PSScriptRoot 'Get-SourcePackageFiles.ps1')
$taskFiles = @(Get-DataManagerSourceFiles -RepositoryRoot $RepositoryRoot)
$taskSourceSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($taskFile in $taskFiles) { [void]$taskSourceSet.Add($taskFile.RelativePath) }
# Only remove tracked files retired from the active source/config/tool trees.
# Historical documentation and knowledge-base entries remain available.
$taskTracked = @(& git -C $Destination -c core.quotePath=false ls-files -- 'data管理器/src' 'data管理器/config' 'data管理器/tools' '形象管理器/src')
if ($LASTEXITCODE -ne 0) { throw '无法读取目标仓库跟踪清单。' }
foreach ($taskRelative in $taskTracked) {
    if ($taskSourceSet.Contains($taskRelative)) { continue }
    $taskTarget = [IO.Path]::GetFullPath((Join-Path $Destination $taskRelative))
    if (!$taskTarget.StartsWith($Destination.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '跟踪路径超出目标仓库。' }
    if (Test-Path -LiteralPath $taskTarget -PathType Leaf) { Remove-Item -LiteralPath $taskTarget }
}
foreach ($taskFile in $taskFiles) {
    $taskTarget = Join-Path $Destination $taskFile.RelativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskTarget) | Out-Null
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget -Force
}
foreach ($taskFile in $taskFiles) {
    if ((Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $Destination $taskFile.RelativePath) -Algorithm SHA256).Hash) {
        throw "同步复核失败：$($taskFile.RelativePath)"
    }
}
Write-Output "已同步 $($taskFiles.Count) 个源码输入；尚未暂存、提交或推送。"
& git -C $Destination diff --stat
