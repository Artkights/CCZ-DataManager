function Get-DataManagerSourceFiles {
    param([Parameter(Mandatory)][string]$RepositoryRoot)
    $taskSourceRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
    $taskTrees = @('data管理器/src', 'data管理器/config', 'data管理器/文档', 'data管理器/tools',
        '形象管理器/src', '形象管理器/docs')
    $taskFiles = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($taskTree in $taskTrees) {
        $taskDirectory = Join-Path $taskSourceRoot $taskTree
        if (!(Test-Path -LiteralPath $taskDirectory -PathType Container)) { throw "缺少源码目录：$taskDirectory" }
        foreach ($taskFile in Get-ChildItem -LiteralPath $taskDirectory -Recurse -File -Force) {
            $taskRelative = [IO.Path]::GetRelativePath($taskSourceRoot, $taskFile.FullName).Replace('\', '/')
            if ($taskRelative.StartsWith('data管理器/src/src/', [StringComparison]::OrdinalIgnoreCase)) { continue }
            # ProfileRegistry still selects these embedded compatibility manifests.
            # They are build inputs rather than discarded configuration backups.
            $taskCompatibilityProfile = $taskRelative -in @(
                'data管理器/config/profiles/ccz-6.4.before-configuration.json',
                'data管理器/config/profiles/ccz-6.5.before-simplification.json',
                'data管理器/config/profiles/ccz-6.6.before-simplification.json')
            if ($taskRelative -match '(^|/)(bin|obj|\.git|\.vs|\.tmp|__pycache__|TestResults|历史|历史记录|运行产物)(/|$)' -or
                ($taskRelative -match '\.before-' -and !$taskCompatibilityProfile) -or
                $taskFile.Extension -in @('.exe','.dll','.pdb','.log','.zip','.nupkg','.cache','.tmp','.bak','.e5','.sav','.user','.pyc')) { continue }
            $taskFiles[$taskRelative] = $taskFile.FullName
        }
    }
    foreach ($taskRelative in @('data管理器/DataManager.sln', 'data管理器/README.md', 'data管理器/.gitignore',
        '形象管理器/Directory.Build.props', '形象管理器/ImageManager.sln', '形象管理器/README.md',
        'Knowledge-base-for-CCZ-mod/.gitattributes', '工具整合包/本地知识库/.gitattributes')) {
        $taskPath = Join-Path $taskSourceRoot $taskRelative
        if (Test-Path -LiteralPath $taskPath -PathType Leaf) { $taskFiles[$taskRelative] = $taskPath }
    }
    # Retain the exact source rows referenced by the frozen constant ledger.
    # These are research snapshots; compilation uses the generated config/C#.
    $taskLedgerPath = Join-Path $taskSourceRoot 'data管理器/config/engine-constants-knowledge-ledger.json'
    if (Test-Path -LiteralPath $taskLedgerPath -PathType Leaf) {
        $taskLedger = Get-Content -LiteralPath $taskLedgerPath -Raw | ConvertFrom-Json
        $taskKnowledgeSources = @(@($taskLedger.records | ForEach-Object { $_.source }) +
            @($taskLedger.sources | ForEach-Object { $_.path }) | Sort-Object -Unique)
        foreach ($taskEvidence in $taskLedger.sources) {
            $taskEvidencePath = [IO.Path]::GetFullPath((Join-Path $taskSourceRoot $taskEvidence.path))
            if (!$taskEvidencePath.StartsWith($taskSourceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '知识来源超出源码工作区。' }
            if ((Get-FileHash -LiteralPath $taskEvidencePath -Algorithm SHA256).Hash -ne $taskEvidence.sha256) {
                throw "知识来源已变化，与审核账本不一致：$($taskEvidence.path)"
            }
        }
        if ($taskLedger.candidateResolutionSource) {
            $taskResolutionPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $taskSourceRoot 'data管理器') $taskLedger.candidateResolutionSource.path))
            if (!$taskResolutionPath.StartsWith($taskSourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
                (Get-FileHash -LiteralPath $taskResolutionPath -Algorithm SHA256).Hash -ne $taskLedger.candidateResolutionSource.sha256) {
                throw '逐候选审核清单与知识账本不一致。'
            }
        }
        $taskKnowledgeSources += @(
            '工具整合包/本地知识库/03-机制详解/成长与宝物相关常量总表-三基底.md',
            '工具整合包/本地知识库/03-机制详解/成长常量全量反汇编-装备等级-宝物质变-五维档位-转职.md',
            '工具整合包/本地知识库/03-机制详解/能力档位与显示补丁.md',
            '工具整合包/本地知识库/03-机制详解/人物档位-五维阈值与6.5强化成长掩码.md',
            '工具整合包/本地知识库/03-机制详解/质变函数扩展位-6.5未加密.md',
            '工具整合包/本地知识库/03-机制详解/质变扩展位-6.5.csv',
            '工具整合包/本地知识库/03-机制详解/宝物质变特效号与覆盖表-三基底.md',
            '工具整合包/本地知识库/03-机制详解/6.6装备特效后缀类型与质变门控.md')
        $taskKnowledgeSources += '工具整合包/本地知识库/03-机制详解/Data管理器-成长常量整合与安全写入-20261112.md'
        foreach ($taskRelative in $taskKnowledgeSources) {
            $taskRelative = $taskRelative.Replace('\', '/')
            $taskPath = [IO.Path]::GetFullPath((Join-Path $taskSourceRoot $taskRelative))
            if (!$taskPath.StartsWith($taskSourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetExtension($taskPath) -notin @('.json', '.csv', '.md')) { throw "非法知识来源路径：$taskRelative" }
            if (!(Test-Path -LiteralPath $taskPath -PathType Leaf)) { throw "缺失知识对账来源：$taskRelative" }
            $taskFiles[$taskRelative] = $taskPath
        }
    }
    foreach ($taskRelative in $taskFiles.Keys) {
        [pscustomobject]@{ RelativePath=$taskRelative; FullName=$taskFiles[$taskRelative] }
    }
}
