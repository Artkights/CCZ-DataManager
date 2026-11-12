[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [string]$Version,
    [string]$SourceCommit,
    [string]$OutputDirectory,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$taskProject = Join-Path $RepositoryRoot 'data管理器/src/DataManager.App/DataManager.App.csproj'
[xml]$taskProjectXml = Get-Content -LiteralPath $taskProject -Raw
$taskSourceVersion = [string]$taskProjectXml.Project.PropertyGroup.Version
if (!$Version) { $Version = $taskSourceVersion }
if ($Version -ne $taskSourceVersion) { throw "源码版本 $taskSourceVersion 与打包版本 $Version 不一致。" }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $RepositoryRoot ('data管理器/发布/' + $Version) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$taskArchivePath = Join-Path $OutputDirectory ('Data管理器及共享组件源码-' + $Version + '.zip')
if (Test-Path -LiteralPath $taskArchivePath) { throw "源码包已存在，避免覆盖已有交付：$taskArchivePath" }
if (!$SkipPublish -and (Test-Path -LiteralPath (Join-Path $OutputDirectory 'data管理器.exe'))) {
    throw '目标已有发布程序，请使用新的发布目录，或明确使用 SkipPublish 打包已验证程序。'
}
. (Join-Path $PSScriptRoot 'Get-SourcePackageFiles.ps1')
$taskTemp = Join-Path $RepositoryRoot ('data管理器/.tmp/release-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskTemp | Out-Null
$taskPreviousTemp = $env:TEMP; $taskPreviousTmp = $env:TMP
$env:TEMP = $taskTemp; $env:TMP = $taskTemp
try {
    if (!$SkipPublish) {
        & dotnet publish $taskProject -c Release -p:PublishProfile=SingleFileWinX64 -p:NuGetAudit=false -p:UseSharedCompilation=false -o $OutputDirectory --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Release 发布失败。' }
    }
    $taskExe = Join-Path $OutputDirectory 'data管理器.exe'
    if (!(Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw "缺少本次发布程序：$taskExe" }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($taskExe).ProductVersion.Split('+')[0] -ne $Version) {
        throw '发布程序版本与本次源码不一致。'
    }
    $taskFiles = @(Get-DataManagerSourceFiles -RepositoryRoot $RepositoryRoot)
    $taskManifest = [ordered]@{
        Version=$Version; SourceCommit=$SourceCommit; CreatedAt=[DateTimeOffset]::Now.ToString('O'); FileCount=$taskFiles.Count
        Files=@($taskFiles | ForEach-Object { [ordered]@{
            Path=$_.RelativePath; Bytes=(Get-Item -LiteralPath $_.FullName).Length
            Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        } })
    }
    Add-Type -AssemblyName System.IO.Compression
    $taskArchive = [IO.Compression.ZipFile]::Open($taskArchivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($taskFile in $taskFiles) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskArchive, $taskFile.FullName,
                $taskFile.RelativePath, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        $taskEntry = $taskArchive.CreateEntry('源码文件清单.json')
        $taskWriter = [IO.StreamWriter]::new($taskEntry.Open(), [Text.UTF8Encoding]::new($false))
        try { $taskWriter.Write(($taskManifest | ConvertTo-Json -Depth 6)) } finally { $taskWriter.Dispose() }
    } finally { $taskArchive.Dispose() }
    $taskExtract = Join-Path $taskTemp '源码独立构建'
    [IO.Compression.ZipFile]::ExtractToDirectory($taskArchivePath, $taskExtract)
    $taskIndependentProjects = @(
        'data管理器/src/DataManager.App/DataManager.App.csproj',
        'data管理器/src/DataManager.Tests/DataManager.Tests.csproj',
        'data管理器/src/TransformManager.App/TransformManager.App.csproj',
        'data管理器/src/TierManager.App/TierManager.App.csproj',
        'data管理器/src/EngineGrowth.Core.Tests/EngineGrowth.Core.Tests.csproj')
    foreach ($taskRelative in $taskIndependentProjects) {
        & dotnet build (Join-Path $taskExtract $taskRelative) -c Release -p:NuGetAudit=false -p:UseSharedCompilation=false --nologo
        if ($LASTEXITCODE -ne 0) { throw "源码包独立构建失败：$taskRelative；保留 $taskTemp" }
    }
    foreach ($taskFile in $taskFiles) {
        if ((Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash -ne
            ($taskManifest.Files | Where-Object Path -EQ $taskFile.RelativePath).Sha256) { throw '打包期间源码发生变化。' }
    }
    $taskRecord = [ordered]@{
        Version=$Version; SourceCommit=$SourceCommit; CreatedAt=[DateTimeOffset]::Now.ToString('O'); SourceIndependentBuildPassed=$true
        IndependentBuildProjects=$taskIndependentProjects
        SourceArchiveFiles=$taskFiles.Count; SourceArchiveMatchesWorkspace=$true
        ExecutableSHA256=(Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash
        SourceArchiveSHA256=(Get-FileHash -LiteralPath $taskArchivePath -Algorithm SHA256).Hash
        Runtime='Windows x64 / .NET 8 Windows Desktop Runtime x64'; SelfContained=$false
        VerificationWorkspace=$taskTemp
    }
    $taskRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory '构建与源码包记录.json') -Encoding utf8
    $taskRecord
} finally { $env:TEMP=$taskPreviousTemp; $env:TMP=$taskPreviousTmp }
