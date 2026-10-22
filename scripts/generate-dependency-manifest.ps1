#requires -Version 7.0
<#
.SYNOPSIS
    第三方依赖清单：核对（护栏）与生成（LM-024）。

.DESCRIPTION
    NOTICE 里的第三方组件清单过去是纯手工维护的，于是出现过两类漂移：

      1. 版本号与实际解析依赖不符（NOTICE 写 System.Text.Json 9.0.10，
         实际 LiteMonitor.csproj 引用的是 10.0.3）；
      2. 漏列真实随包分发的依赖（TraceEvent / Vortice.Direct2D1 / Vortice.Direct3D11）。

    本脚本把"声明"变成"由实际解析推导"，让漂移在 CI 里可被发现：

      1. 真值来源：`dotnet list package --include-transitive`（读**实际解析**结果，不是手抄）。
         该命令隐式触发还原；若因离线/网络失败，回退到 obj/project.assets.json
         （同样是还原期的解析产物），并在输出中标注真值来源。
      2. 许可证与来源：从 NuGet 全局缓存的 .nuspec 读真实 SPDX 表达式
         （<license type="expression">MIT</license>）与 projectUrl / repository url。
      3. 交叉核对 NOTICE 中形如 "N) 名称 版本" 的声明行，输出三类判定：
           漏列（实际直接依赖未声明） / 版本不符（声明版本 ≠ 实际解析版本） /
           多声明（NOTICE 声明了但实际不再依赖，只提示不自动删）。
      4. NOTICE 分「直接依赖」段与「传递依赖」段（由 "----" 分隔线 + 含 "传递依赖"
         的章节标题切分）。对**实际随包分发**的传递依赖再做一遍同样的核对：
         未在传递依赖段声明 ⇒ 漏列；声明版本 ≠ 实际解析版本 ⇒ 版本不符。
         两类判定分别汇总（SUMMARY / SUMMARY-TRANSITIVE），任一不通过都非零退出。
      5. 「随包分发」的判定**不能**用"包里有没有 lib/ 目录"这种粗判据：NuGet 会
         把与 .NET 共享框架同名的程序集（Microsoft.Win32.Registry、
         System.Security.AccessControl、System.Security.Principal.Windows、
         System.Reflection.TypeExtensions、System.Runtime.CompilerServices.Unsafe）
         判定为"框架提供"，从而**不**复制进发布包；粗判据会把它们误算成随包分发。
         现在的判据见 Get-DistributionMap 的注释，与实际 publish 产物逐条核对过。

    两种模式：

      -Mode Check  护栏模式（默认）。存在漏列或版本不符 ⇒ 退出码非 0；全部一致 ⇒ 0。
                   输出为逐条人可读判定 + 一行汇总（MISSING=… MISMATCH=… EXTRA=…）。
      -Mode Emit   生成依赖清单文件（JSON / Markdown）。默认写到系统临时目录；
                   只有显式 -OutPath 指向某个路径时才写该路径，脚本本身
                   不会把任何内容写进仓库受版本控制的文件。

    另含一节「包内更新器」检查。resources\LiteMonitor++.Updater.exe 是**预构建二进制**，
    被 LiteMonitor.csproj 以 <Content> 复制进主程序产物 ⇒ "解决方案编译通过"**不能**证明
    包内更新器由本次源码构建。本节用"产物内嵌的构建提交 SHA vs 当前源码树"做**内容级**判定，
    不依赖字节级可复现性（.NET 发布产物并非字节确定性：PE 时间戳 / MVID 每次构建都会变，
    朴素做法是"重新 publish 再比 SHA-256"，不可靠）。

.PARAMETER Mode
    Check（护栏，默认）或 Emit（生成清单）。

.PARAMETER OutPath
    Emit 模式的输出文件路径。留空时写到系统临时目录（不会写进仓库）。

.PARAMETER Format
    Emit 模式的输出格式：Json（默认）或 Markdown。

.PARAMETER RepoRoot
    仓库根目录。留空时取本脚本所在目录的上一级。

.PARAMETER ProjectPath
    作为核对基准的**产品**工程（相对仓库根）。默认 LiteMonitor.csproj。

.PARAMETER NoticePath
    NOTICE 文件路径（相对仓库根）。默认 NOTICE。

.PARAMETER ScanProjects
    额外扫描（仅报告、不参与护栏判定）的工程列表。其直接依赖若**随包分发**且未被
    NOTICE 声明，仍会计入 MISSING —— 因为那同样属于许可证声明不完整。

.EXAMPLE
    pwsh -NoProfile -File scripts/generate-dependency-manifest.ps1 -Mode Check

.EXAMPLE
    pwsh -NoProfile -File scripts/generate-dependency-manifest.ps1 -Mode Emit -OutPath "$env:TEMP\litemonitor-deps.json"

.NOTES
    退出码：
      0 = 全部一致（护栏通过）
      1 = 存在漏列 / 版本不符（护栏触发）
      2 = 参数错误或脚本自身失败
      3 = 环境受阻（dotnet 不可用、工程无法还原且无 assets 回退、
                   随包分发判定降级导致传递依赖结论不可信等）
#>
[CmdletBinding()]
param(
    [ValidateSet('Check', 'Emit')]
    [string]$Mode = 'Check',

    [string]$OutPath,

    [ValidateSet('Json', 'Markdown')]
    [string]$Format = 'Json',

    [string]$RepoRoot,

    [string]$ProjectPath = 'LiteMonitor.csproj',

    [string]$NoticePath = 'NOTICE',

    [string[]]$ScanProjects = @(
        'LiteMonitor.Updater/LiteMonitor.Updater.csproj',
        'tests/LiteMonitor.GpuNonperformance.Tests/LiteMonitor.GpuNonperformance.Tests.csproj'
    )
)

$ErrorActionPreference = 'Stop'

$script:ExitOk = 0
$script:ExitPolicy = 1
$script:ExitUsage = 2
$script:ExitEnv = 3

# NOTICE 声明行：形如 "1) LibreHardwareMonitorLib 0.9.6"
# 非 NuGet 条目（PawnIO / PresentMon / 网络测速服务器）因为没有纯版本号而不会命中。
$script:NoticeDeclPattern = '^\s*(\d+)\)\s+(\S+)\s+([0-9][0-9A-Za-z.\-+]*)\s*$'

# NOTICE 的章节分隔线，以及"传递依赖"章节的识别标记。
# NOTICE 用 "----" 分隔线切章节；章节标题行里含下面这个标记的即"传递依赖"段。
$script:NoticeSectionSeparatorPattern = '^-{10,}\s*$'
$script:NoticeTransitiveMarker = '传递依赖'

# 发布包的运行时标识（RID）。pack-release.ps1 用 `-r win-x64` 发布，
# NuGet 的 RID 回退链是 win-x64 → win → 任意，因此这两个 RID 的资产才会进包。
$script:ReleaseRids = @('win', 'win-x64')

$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)


#region 基础设施

function Write-Report {
    param([Parameter(ValueFromPipeline = $true)][string]$Line = '')
    process { Write-Output $Line }
}

function Get-Prop {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $Default }
    if ($null -eq $p.Value) { return $Default }
    return $p.Value
}

function Resolve-RepoRoot {
    param([string]$Given)
    if ($Given) {
        if ([System.IO.Path]::IsPathRooted($Given)) {
            $full = [System.IO.Path]::GetFullPath($Given)
        } else {
            $full = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Given))
        }
        if (-not (Test-Path -LiteralPath $full)) {
            throw "仓库根不存在：$full"
        }
        return (Resolve-Path -LiteralPath $full).Path
    }
    # scripts/ 的上一级
    $here = Split-Path -Parent $PSCommandPath
    $root = Split-Path -Parent $here
    if (-not (Test-Path -LiteralPath (Join-Path $root 'LiteMonitor.csproj'))) {
        throw "无法从脚本位置推断仓库根（$root 下没有 LiteMonitor.csproj），请用 -RepoRoot 显式指定。"
    }
    return (Resolve-Path -LiteralPath $root).Path
}

function Resolve-RepoPath {
    param([string]$Root, [string]$Relative)
    if ([System.IO.Path]::IsPathRooted($Relative)) { return $Relative }
    return (Join-Path $Root ($Relative -replace '/', '\'))
}

function Get-NuGetGlobalPackagesRoot {
    if ($env:NUGET_PACKAGES) { return ($env:NUGET_PACKAGES.TrimEnd('\', '/')) }
    try {
        $out = & dotnet nuget locals global-packages --list 2>$null
        if ($LASTEXITCODE -eq 0 -and $out) {
            $line = ($out | Select-Object -First 1)
            if ($line -match ':\s*(.+?)\s*$') { return ($Matches[1].TrimEnd('\', '/')) }
        }
    } catch {
        # 落回默认位置
    }
    return (Join-Path $env:USERPROFILE '.nuget\packages')
}

function Test-PackageHasRuntimeAsset {
    <#
        判断包是否提供**运行时资产**（即会随包分发）。
        规则：lib/<tfm>/*.dll 或 runtimes/<rid>/lib/<tfm>/*.dll。
    #>
    param([string]$PackageDir)
    if (-not (Test-Path -LiteralPath $PackageDir)) { return $false }

    $lib = Join-Path $PackageDir 'lib'
    if (Test-Path -LiteralPath $lib) {
        foreach ($tfm in (Get-ChildItem -LiteralPath $lib -Directory -ErrorAction SilentlyContinue)) {
            if (@(Get-ChildItem -LiteralPath $tfm.FullName -Filter '*.dll' -File -ErrorAction SilentlyContinue).Count -gt 0) { return $true }
        }
    }

    $runtimes = Join-Path $PackageDir 'runtimes'
    if (Test-Path -LiteralPath $runtimes) {
        foreach ($rid in (Get-ChildItem -LiteralPath $runtimes -Directory -ErrorAction SilentlyContinue)) {
            $rlib = Join-Path $rid.FullName 'lib'
            if (-not (Test-Path -LiteralPath $rlib)) { continue }
            foreach ($tfm in (Get-ChildItem -LiteralPath $rlib -Directory -ErrorAction SilentlyContinue)) {
                if (@(Get-ChildItem -LiteralPath $tfm.FullName -Filter '*.dll' -File -ErrorAction SilentlyContinue).Count -gt 0) { return $true }
            }
        }
    }
    return $false
}

function Get-LicenseLabelFromText {
    <#
        从许可证文件正文里识别常见的 SPDX 标识（仅用于**报告展示**，
        NOTICE 里的协议文字是人工核对后写死的，不依赖本函数）。
    #>
    param([string]$Text)

    if (-not $Text) { return $null }
    if ($Text -match 'Apache License,?\s*Version 2\.0') { return 'Apache-2.0' }
    if ($Text -match 'Mozilla Public License Version 2\.0') { return 'MPL-2.0' }
    if ($Text -match 'Mozilla Public License Version 1\.1') { return 'MPL-1.1' }
    if ($Text -match 'GNU GENERAL PUBLIC LICENSE\s+Version 3') { return 'GPL-3.0-only' }
    if ($Text -match 'GNU GENERAL PUBLIC LICENSE\s+Version 2') { return 'GPL-2.0-only' }
    if ($Text -match 'Permission is hereby granted, free of charge') { return 'MIT' }
    if ($Text -match 'Redistribution and use in source and binary forms') { return 'BSD-3-Clause' }
    if ($Text -match 'Permission to use, copy, modify, and/or distribute this software') { return 'ISC' }
    if ($Text -match 'released into the public domain') { return 'Unlicense' }
    return $null
}

function Get-PackageFacts {
    <#
        从 NuGet 全局缓存的 .nuspec 读真实许可证表达式与来源地址。
        显式 UTF-8 读取；不使用 Get-Content 的默认编码。
    #>
    param(
        [string]$GlobalPackagesRoot,
        [string]$Id,
        [string]$Version
    )

    $result = [ordered]@{
        Id              = $Id
        Version         = $Version
        LicenseType     = $null
        License         = $null
        LicenseFile     = $null
        LicenseFileText = $null
        LicenseUrl      = $null
        ProjectUrl      = $null
        RepositoryUrl   = $null
        Authors         = $null
        NuspecPath      = $null
        NuspecFound     = $false
        HasRuntimeAsset = $false
        Error           = $null
    }

    $dir = Join-Path (Join-Path $GlobalPackagesRoot $Id.ToLowerInvariant()) $Version
    if (-not (Test-Path -LiteralPath $dir)) {
        $result.Error = "NuGet 缓存中不存在 $Id/$Version"
        return [pscustomobject]$result
    }

    $nuspec = Join-Path $dir ("{0}.nuspec" -f $Id.ToLowerInvariant())
    if (-not (Test-Path -LiteralPath $nuspec)) {
        $result.Error = "未找到 .nuspec：$nuspec"
        return [pscustomobject]$result
    }

    $result.NuspecPath = $nuspec
    $result.NuspecFound = $true
    $result.HasRuntimeAsset = Test-PackageHasRuntimeAsset -PackageDir $dir

    try {
        $raw = [System.IO.File]::ReadAllText($nuspec, [System.Text.UTF8Encoding]::new($false))
        [xml]$xml = $raw
        $meta = $xml.package.metadata

        $lic = $meta.license
        if ($null -ne $lic) {
            $result.LicenseType = $lic.type
            $text = [string]$lic.'#text'
            if ($text) { $text = $text.Trim() }
            if (($lic.type -eq 'file') -and $text) {
                $licenseFile = Join-Path $dir $text
                $result.LicenseFile = $text
                if (Test-Path -LiteralPath $licenseFile) {
                    $full = (Get-Content -LiteralPath $licenseFile -Raw -Encoding UTF8) -replace '\s+', ' '
                    $full = $full.Trim()
                    $result.LicenseFileText = $full
                    $label = Get-LicenseLabelFromText -Text $full
                    $result.License = if ($label) { "{0}（包内 {1}）" -f $label, $text } else { "文件许可证（包内 {0}）" -f $text }
                } else {
                    $result.License = "FILE:$text"
                }
            } else {
                $result.License = $text
            }
        }

        $result.LicenseUrl = [string]$meta.licenseUrl
        if (-not $result.License -and $result.LicenseUrl) {
            # 有些包（如 Mono.Posix.NETStandard）只给 licenseUrl，没有 SPDX 表达式
            $result.License = "未声明 SPDX 表达式（licenseUrl: {0}）" -f $result.LicenseUrl
        }
        $result.ProjectUrl = [string]$meta.projectUrl

        $result.Authors = [string]$meta.authors

        $repo = $meta.repository
        if ($null -ne $repo) {
            $repoUrl = [string]$repo.url
            if ($repoUrl) { $result.RepositoryUrl = $repoUrl }
        }
        if (-not $result.RepositoryUrl -and $result.ProjectUrl) {
            $result.RepositoryUrl = $result.ProjectUrl
        }
    } catch {
        $result.Error = "解析 .nuspec 失败：$($_.Exception.Message)"
    }

    return [pscustomobject]$result
}

function Get-PackageReferenceMetadata {
    <#
        读 csproj 里的 PackageReference 元数据（ExcludeAssets / PrivateAssets），
        用于判断"是否随包分发"。
    #>
    param([string]$ProjectFile)

    $map = @{}
    if (-not (Test-Path -LiteralPath $ProjectFile)) { return $map }
    try {
        [xml]$xml = [System.IO.File]::ReadAllText($ProjectFile, [System.Text.UTF8Encoding]::new($false))
        foreach ($group in $xml.Project.ItemGroup) {
            foreach ($ref in $group.PackageReference) {
                $id = [string]$ref.Include
                if (-not $id) { continue }
                $map[$id] = [pscustomobject]@{
                    ExcludeAssets = [string]$ref.ExcludeAssets
                    PrivateAssets = [string]$ref.PrivateAssets
                    Version       = [string]$ref.Version
                }
            }
        }
    } catch {
        # csproj 读不了时按"未知"处理：仍然用包资产规则判断分发
    }
    return $map
}

function Test-IsRedistributed {
    param(
        [bool]$HasRuntimeAsset,
        $RefMeta
    )
    if (-not $HasRuntimeAsset) { return $false }
    if ($null -ne $RefMeta) {
        $exclude = [string]$RefMeta.ExcludeAssets
        $private = [string]$RefMeta.PrivateAssets
        if ($exclude -match 'runtime') { return $false }
        if ($private -eq 'all') { return $false }
    }
    return $true
}

function ConvertTo-JsonBlock {
    <#
        从可能夹带非 JSON 噪声（stderr 文本等）的输出里截出 JSON 主体。
    #>
    param([string]$Text)
    if (-not $Text) { return $null }
    $start = $Text.IndexOf('{')
    $end = $Text.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) { return $null }
    return $Text.Substring($start, ($end - $start + 1))
}

#endregion

#region 依赖解析

function Get-ResolvedPackages {
    <#
        返回某工程实际解析出的包：
          TopLevel  : @([pscustomobject]@{ Id; RequestedVersion; ResolvedVersion; AutoReferenced })
          Transitive: @([pscustomobject]@{ Id; ResolvedVersion })
        真值优先用 dotnet list package；失败回退 obj/project.assets.json。
    #>
    param(
        [string]$Root,
        [string]$ProjectFile
    )

    $dotnetOk = $true
    try {
        $null = & dotnet --version 2>&1
        if ($LASTEXITCODE -ne 0) { $dotnetOk = $false }
    } catch {
        $dotnetOk = $false
    }

    if ($dotnetOk) {
        try {
            $raw = & dotnet list $ProjectFile package --include-transitive --format json 2>&1 |
                ForEach-Object { if ($_ -is [string]) { $_ } else { $_.ToString() } }
            if ($LASTEXITCODE -eq 0) {
                $text = ConvertTo-JsonBlock -Text ($raw -join "`n")
                if (-not $text) { throw 'dotnet list package 未返回可解析的 JSON' }
                $json = $text | ConvertFrom-Json
                $proj = $json.projects | Where-Object { $_.path -eq $ProjectFile } | Select-Object -First 1
                if ($null -eq $proj) { $proj = $json.projects | Select-Object -First 1 }
                $frameworks = @()
                foreach ($fw in $proj.frameworks) {
                    $tl = @()
                    foreach ($p in $fw.topLevelPackages) {
                        $tl += [pscustomobject]@{
                            Id               = [string]$p.id
                            RequestedVersion = [string]$p.requestedVersion
                            ResolvedVersion  = [string]$p.resolvedVersion
                            AutoReferenced   = ([string](Get-Prop $p 'autoReferenced' 'false')) -eq 'true'
                        }
                    }
                    $tr = @()
                    foreach ($p in $fw.transitivePackages) {
                        $tr += [pscustomobject]@{
                            Id              = [string]$p.id
                            ResolvedVersion = [string]$p.resolvedVersion
                        }
                    }
                    $frameworks += [pscustomobject]@{
                        Framework  = [string]$fw.framework
                        TopLevel   = $tl
                        Transitive = $tr
                    }
                }
                return [pscustomobject]@{
                    Source     = 'dotnet list package --include-transitive'
                    Frameworks = $frameworks
                    Degraded   = $false
                    Note       = $null
                }
            }
        } catch {
            # 落到 assets 回退
        }
    }

    # 回退：obj/project.assets.json
    $assets = Join-Path (Split-Path -Parent $ProjectFile) 'obj\project.assets.json'
    if (-not (Test-Path -LiteralPath $assets)) {
        throw "无法解析 $ProjectFile 的依赖：dotnet list package 失败，且没有可回退的 $assets"
    }
    $a = ([System.IO.File]::ReadAllText($assets, [System.Text.UTF8Encoding]::new($false))) | ConvertFrom-Json

    # libraries 的键形如 "Id/Version"；先建立 Id -> 解析版本 的映射
    $libMap = @{}
    foreach ($libProp in $a.libraries.PSObject.Properties) {
        $id = $libProp.Name
        $ver = [string](Get-Prop $libProp.Value 'version' '')
        if ($id -match '^(?<i>[^/]+)/(?<v>.+)$') { $id = $Matches['i']; $ver = $Matches['v'] }
        $libMap[$id] = $ver
    }

    $frameworks = @()
    foreach ($tfmProp in $a.project.frameworks.PSObject.Properties) {
        $tfm = $tfmProp.Name
        $direct = $tfmProp.Value.dependencies
        $tl = @()
        foreach ($d in $direct.PSObject.Properties) {
            $requested = [string](Get-Prop $d.Value 'version' '')
            $resolved = $requested
            if ($libMap.ContainsKey($d.Name)) { $resolved = $libMap[$d.Name] }
            $tl += [pscustomobject]@{
                Id               = $d.Name
                RequestedVersion = $requested
                ResolvedVersion  = $resolved
                AutoReferenced   = $false
            }
        }
        $tr = @()
        foreach ($libProp in $a.libraries.PSObject.Properties) {
            $id = $libProp.Name
            $ver = [string](Get-Prop $libProp.Value 'version' '')
            if ($id -match '^(?<i>[^/]+)/(?<v>.+)$') { $id = $Matches['i']; $ver = $Matches['v'] }
            $isTop = $false
            foreach ($t in $tl) { if ($t.Id -eq $id) { $isTop = $true } }
            if ($isTop) { continue }
            $tr += [pscustomobject]@{
                Id              = $id
                ResolvedVersion = $ver
            }
        }
        $frameworks += [pscustomobject]@{
            Framework  = $tfm
            TopLevel   = $tl
            Transitive = $tr
        }
    }
    return [pscustomobject]@{
        Source     = 'obj/project.assets.json (dotnet list package 不可用时的回退)'
        Frameworks = $frameworks
        Degraded   = $true
        Note       = '已回退到 project.assets.json；建议修复还原环境后复跑以获得 dotnet list package 的结果'
    }
}

#endregion

#region NOTICE 解析

function Get-NoticeSectionStart {
    <#
        NOTICE 用 "----" 分隔线切分章节。返回「传递依赖」章节分隔线所在的行号（0 基）；
        找不到则返回 -1（此时全部声明都按「直接依赖」处理）。
        识别规则：某个分隔线之后 3 行内出现含「传递依赖」的标题行。
    #>
    param([string[]]$Lines)

    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -notmatch $script:NoticeSectionSeparatorPattern) { continue }
        $limit = [Math]::Min($i + 3, $Lines.Count - 1)
        for ($j = $i + 1; $j -le $limit; $j++) {
            if ($Lines[$j] -match $script:NoticeTransitiveMarker) { return $i }
        }
    }
    return -1
}

function Get-NoticeDeclarations {
    param([string]$NoticeFile)

    if (-not (Test-Path -LiteralPath $NoticeFile)) {
        throw "找不到 NOTICE 文件：$NoticeFile"
    }
    # 显式 UTF-8 读取
    $text = [System.IO.File]::ReadAllText($NoticeFile, [System.Text.UTF8Encoding]::new($false))
    $lines = $text -split "`r`n|`n|`r"

    $sectionStart = Get-NoticeSectionStart -Lines $lines

    $decls = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $m = [regex]::Match($line, $script:NoticeDeclPattern)
        if ($m.Success) {
            $decls += [pscustomobject]@{
                Number  = [int]$m.Groups[1].Value
                Name    = $m.Groups[2].Value
                Version = $m.Groups[3].Value
                Line    = ($i + 1)
                Raw     = $line
                # 传递依赖段里的声明行；分隔线之前的一律算直接依赖
                Section = if ($sectionStart -ge 0 -and $i -gt $sectionStart) { 'Transitive' } else { 'Direct' }
            }
        }
    }
    return [pscustomobject]@{
        Declarations  = $decls
        SectionStart  = $sectionStart
        HasTransitive = ($sectionStart -ge 0)
    }
}

function Test-VersionEqual {
    <#
        版本号比较。NuGet 版本的大小写不敏感（预发布标签 "-beta" 与 "-Beta" 等价），
        因此这里做 OrdinalIgnoreCase 比较，且只比较归一化后的字面量 ——
        不做 SemVer 排序比较，避免把 "2.4.2-beta" 之类的预发布串解析错。
    #>
    param([string]$A, [string]$B)
    if ($null -eq $A -or $null -eq $B) { return $false }
    $x = $A.Trim()
    $y = $B.Trim()
    if ($x.Length -eq 0 -and $y.Length -eq 0) { return $true }
    return [string]::Equals($x, $y, [System.StringComparison]::OrdinalIgnoreCase)
}

#endregion

#region 随包分发判定

function Get-AssetsGraph {
    <#
        读 obj/project.assets.json（还原期的解析产物），给出**实际会被复制进发布目录**
        的资产清单，以及包之间的依赖关系。

        返回：Source / Path / Tfm / TargetKey / HasRid / Libraries

        优先选带 RID 的目标（"<tfm>/win-x64"），其次选不带 RID 的目标（"<tfm>"）。
        带 RID 的目标里资产已经按 RID 解析完毕；不带 RID 的目标里 assets 会列出
        所有 RID 的资产，由调用方按 $script:ReleaseRids 过滤。
    #>
    param([string]$ProjectFile)

    $assetsPath = Join-Path (Split-Path -Parent $ProjectFile) 'obj\project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) { return $null }

    try {
        $a = ([System.IO.File]::ReadAllText($assetsPath, [System.Text.UTF8Encoding]::new($false))) | ConvertFrom-Json
    } catch {
        return $null
    }
    if ($null -eq $a -or $null -eq $a.targets) { return $null }

    $targetKeys = @($a.targets.PSObject.Properties | ForEach-Object { $_.Name })
    $ridKey = $targetKeys | Where-Object { $_ -match '/win-x64$' } | Select-Object -First 1
    $plainKey = $targetKeys | Where-Object { $_ -notmatch '/' } | Select-Object -First 1
    $chosen = if ($ridKey) { $ridKey } else { $plainKey }
    if (-not $chosen) { return $null }

    $libraries = [ordered]@{}
    foreach ($lp in $a.targets.PSObject.Properties[$chosen].Value.PSObject.Properties) {
        $id = $lp.Name
        $ver = ''
        if ($id -match '^(?<i>[^/]+)/(?<v>.+)$') { $id = $Matches['i']; $ver = $Matches['v'] }
        $rt = @(); if ($lp.Value.runtime) { $rt = @($lp.Value.runtime.PSObject.Properties | ForEach-Object { $_.Name }) }
        $rtg = @(); if ($lp.Value.runtimeTargets) { $rtg = @($lp.Value.runtimeTargets.PSObject.Properties | ForEach-Object { $_.Name }) }
        $nat = @(); if ($lp.Value.native) { $nat = @($lp.Value.native.PSObject.Properties | ForEach-Object { $_.Name }) }
        $deps = @(); if ($lp.Value.dependencies) { $deps = @($lp.Value.dependencies.PSObject.Properties | ForEach-Object { $_.Name }) }
        $libraries[$id] = [pscustomobject]@{
            Id             = $id
            Version        = $ver
            Runtime        = $rt
            RuntimeTargets = $rtg
            Native         = $nat
            Dependencies   = $deps
        }
    }

    $tfm = if ($chosen -match '/') { ($chosen -split '/')[0] } else { $chosen }
    return [pscustomobject]@{
        Source    = 'obj/project.assets.json'
        Path      = $assetsPath
        Tfm       = $tfm
        TargetKey = $chosen
        HasRid    = [bool]($chosen -match '/')
        Libraries = $libraries
    }
}

function Get-TargetingPackAssemblyMap {
    <#
        取"目标框架（targeting pack）"提供的程序集及其程序集版本：
          <dotnetRoot>\packs\Microsoft.NETCore.App.Ref\<ver>\ref\<tfm>\*.dll
          <dotnetRoot>\packs\Microsoft.WindowsDesktop.App.Ref\<ver>\ref\<tfm>\*.dll

        这些程序集由 .NET 共享框架提供 —— 当某个 NuGet 包提供的同名程序集版本
        **不高于**框架版本时，NuGet/MSBuild 会判定"框架提供"，该文件**不会**被
        复制进发布目录。这是"随包分发"判定的关键一环。
    #>
    param([string]$TargetFramework)

    $map = @{}
    $sources = @()
    try {
        $dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
        # dotnet.exe -> <dotnetRoot>\dotnet.exe，dotnetRoot 就是它所在的目录
        $dotnetRoot = Split-Path -Parent $dotnetExe
        $packs = Join-Path $dotnetRoot 'packs'
    } catch {
        return [pscustomobject]@{ Map = $map; Sources = @(); Found = $false }
    }

    foreach ($pack in @('Microsoft.NETCore.App.Ref', 'Microsoft.WindowsDesktop.App.Ref')) {
        $dir = Join-Path $packs $pack
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $cands = @(Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue |
                Sort-Object -Property @{ Expression = { try { [version](($_.Name -split '-')[0]) } catch { [version]'0.0' } } } -Descending)
        foreach ($v in $cands) {
            $ref = Join-Path $v.FullName (Join-Path 'ref' $TargetFramework)
            if (-not (Test-Path -LiteralPath $ref)) { continue }
            foreach ($f in (Get-ChildItem -LiteralPath $ref -File -Filter '*.dll' -ErrorAction SilentlyContinue)) {
                try {
                    $an = [System.Reflection.AssemblyName]::GetAssemblyName($f.FullName)
                    if (-not $map.ContainsKey($an.Name)) { $map[$an.Name] = $an.Version }
                } catch {
                    # 非托管或无法读取元数据的文件：忽略
                }
            }
            $sources += $ref
            break
        }
    }
    return [pscustomobject]@{ Map = $map; Sources = $sources; Found = ($sources.Count -gt 0) }
}

function Get-DistributionMap {
    <#
        判定"哪些包的文件会真的随发布包分发"。

        判据（与 NuGet / MSBuild 的实际行为一致，已用
        `dotnet publish -c Release -r win-x64 --self-contained false` 的真实产物
        逐条核对过）：
          1. 资产来自还原图的 runtime / runtimeTargets / native；
          2. 文件名不是 "_._"（NuGet 的空占位符，代表该 TFM 没有实现）；
          3. runtimes/<rid>/... 里的 rid 必须命中发布包的 RID（win-x64 / win）；
          4. 托管程序集若被目标框架（targeting pack）以**不低于**的版本提供，
             则该文件不会进入发布目录（NuGet 判定为"框架提供"）。

        第 4 条是关键：没有它，Microsoft.Win32.Registry、System.Security.AccessControl、
        System.Security.Principal.Windows、System.Reflection.TypeExtensions、
        System.Runtime.CompilerServices.Unsafe 会被误判成随包分发。
    #>
    param(
        [pscustomobject]$AssetsGraph,
        [string]$GlobalPackagesRoot,
        [hashtable]$FrameworkAssemblies
    )

    $result = @{}
    if ($null -eq $AssetsGraph) { return $result }

    foreach ($id in $AssetsGraph.Libraries.Keys) {
        $lib = $AssetsGraph.Libraries[$id]
        $pkgDir = Join-Path (Join-Path $GlobalPackagesRoot $id.ToLowerInvariant()) $lib.Version

        $candidates = @()
        foreach ($c in @($lib.Runtime, $lib.RuntimeTargets, $lib.Native)) { $candidates += @($c) }

        $kept = @()
        $sawPlaceholder = $false
        $sawOtherRid = $false
        $sawFramework = $false
        foreach ($asset in $candidates) {
            if (-not $asset) { continue }
            $leaf = Split-Path -Leaf $asset
            if ($leaf -eq '_._') { $sawPlaceholder = $true; continue }          # 空占位符

            if (-not $AssetsGraph.HasRid -and $asset -match '(?i)^runtimes/(?<rid>[^/]+)/') {
                # 未按 RID 还原的图：只有发布包的 RID 才会进包
                if ($script:ReleaseRids -notcontains $Matches['rid'].ToLowerInvariant()) {
                    $sawOtherRid = $true
                    continue
                }
            }

            if ($leaf -match '\.dll$' -and $asset -notmatch '(?i)^runtimes/[^/]+/native/') {
                $full = Join-Path $pkgDir ($asset -replace '/', '\')
                $pkgVersion = $null
                try { $pkgVersion = [System.Reflection.AssemblyName]::GetAssemblyName($full).Version } catch { $pkgVersion = $null }
                if ($pkgVersion) {
                    $simple = [System.IO.Path]::GetFileNameWithoutExtension($leaf)
                    if ($FrameworkAssemblies.ContainsKey($simple) -and $FrameworkAssemblies[$simple] -ge $pkgVersion) {
                        $sawFramework = $true
                        continue    # 框架提供 → 不随包分发
                    }
                }
            }

            $kept += $asset
        }

        $reason = '随包分发'
        if ($kept.Count -eq 0) {
            if ($sawFramework) {
                $reason = '同名程序集由 .NET 共享框架提供（版本不低于包内版本），不随包分发'
            } elseif ($sawOtherRid) {
                $reason = '只提供非 Windows RID（win-x64 / win）的原生文件，不随包分发'
            } elseif ($sawPlaceholder) {
                $reason = '该目标框架下只有空占位符（_._），仅编译期引用，不随包分发'
            } else {
                $reason = '无运行库资产（仅编译期引用），不随包分发'
            }
        }

        $result[$id] = [pscustomobject]@{
            Id          = $id
            Version     = $lib.Version
            Distributed = ($kept.Count -gt 0)
            Files       = $kept
            Reason      = $reason
        }
    }
    return $result
}

function Get-DirectAncestors {
    <#
        沿依赖图回溯，给出某个包是**由哪些直接依赖**带入的（用于 NOTICE 的"引入"字段）。
    #>
    param(
        [pscustomobject]$AssetsGraph,
        [string[]]$DirectIds,
        [string]$Id
    )

    if ($null -eq $AssetsGraph) { return @() }
    $parents = @{}
    foreach ($lid in $AssetsGraph.Libraries.Keys) {
        foreach ($d in $AssetsGraph.Libraries[$lid].Dependencies) {
            if (-not $parents.ContainsKey($d)) { $parents[$d] = @() }
            $parents[$d] += $lid
        }
    }

    $seen = @{}
    $found = New-Object System.Collections.Generic.List[string]
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($Id)
    while ($stack.Count -gt 0) {
        $cur = $stack.Pop()
        if ($seen.ContainsKey($cur)) { continue }
        $seen[$cur] = $true
        if ($DirectIds -contains $cur) {
            if (-not $found.Contains($cur)) { $found.Add($cur) }
            continue
        }
        if ($parents.ContainsKey($cur)) {
            foreach ($p in $parents[$cur]) { $stack.Push($p) }
        }
    }
    return @($found | Sort-Object)
}

#endregion


#region 包内更新器检查

function Get-GitState {
    param([string]$Root)
    $state = [ordered]@{
        Available = $false
        HeadSha   = $null
        Error     = $null
    }
    try {
        $null = & git -C $Root rev-parse --is-inside-work-tree 2>&1
        if ($LASTEXITCODE -eq 0) {
            $state.Available = $true
            $state.HeadSha = ((& git -C $Root rev-parse HEAD 2>$null) | Select-Object -First 1)
        } else {
            $state.Error = '当前目录不是 git 工作树'
        }
    } catch {
        $state.Error = "git 不可用：$($_.Exception.Message)"
    }
    return [pscustomobject]$state
}

function Get-UpdaterCheck {
    <#
        「包内更新器」检查项。

        判定依据（不依赖字节级可复现性）：
          * 产物内嵌的构建提交 SHA（FileVersionInfo.ProductVersion 的 "+<40 hex>" 后缀，
            来自 SDK 的 InformationalVersion = $(Version)+$(SourceRevisionId)）。
          * `git diff <sha> -- LiteMonitor.Updater/` 是否为空 —— 这是**内容级**判定：
            为空即"产物对应的源码内容与当前工作树完全一致"。
          * 若不一致，则比较各差异文件的最后写入时间与产物写入时间：
              差异文件比产物新 ⇒ 一定不一致（需重建）；
              产物比差异文件新 ⇒ 无法在不做重建的前提下证明内容一致 ⇒ NOT_VERIFIED。
    #>
    param([string]$Root)

    $relExe = 'resources/LiteMonitor++.Updater.exe'
    $exe = Resolve-RepoPath -Root $Root -Relative $relExe

    $result = [ordered]@{
        ExePath              = $relExe
        Exists               = $false
        SizeBytes            = $null
        LastWriteTimeUtc     = $null
        Sha256               = $null
        FileVersion          = $null
        ProductVersion       = $null
        EmbeddedCommitSha    = $null
        BuiltFromCommitKnown = $false
        BuiltFromCommitInfo  = $null
        LatestSourceWriteUtc = $null
        SourceFilesChanged   = @()
        NewerThanArtifact    = @()
        CopyAsContent        = $false
        CopyAsContentLines   = @()
        CsprojPath           = 'LiteMonitor.csproj'
        Verdict              = 'NOT_VERIFIED'
        VerdictText          = 'NOT_VERIFIED（无法判定，原因待填）'
        Reason               = $null
        Facts                = @()
    }

    # --- csproj 是否以 <Content> 复制该文件（"编译通过 ≠ 产物一致" 的事实） ---
    $csproj = Resolve-RepoPath -Root $Root -Relative 'LiteMonitor.csproj'
    if (Test-Path -LiteralPath $csproj) {
        $csLines = [System.IO.File]::ReadAllLines($csproj, [System.Text.UTF8Encoding]::new($false))
        for ($i = 0; $i -lt $csLines.Count; $i++) {
            if ($csLines[$i] -match 'Content\s+Include\s*=\s*"resources\\LiteMonitor\+\+\.Updater\.exe"') {
                $result.CopyAsContent = $true
                for ($j = $i; $j -lt [Math]::Min($i + 4, $csLines.Count); $j++) {
                    $result.CopyAsContentLines += ($j + 1)
                }
                break
            }
        }
    }

    if (-not (Test-Path -LiteralPath $exe)) {
        $result.VerdictText = 'NOT_VERIFIED（无法判定：产物文件不存在）'
        $result.Reason = "$relExe 不存在，无法核对"
        return [pscustomobject]$result
    }

    $item = Get-Item -LiteralPath $exe
    $result.Exists = $true
    $result.SizeBytes = $item.Length
    $result.LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
    $result.Sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash

    try {
        $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
        $result.FileVersion = $vi.FileVersion
        $result.ProductVersion = $vi.ProductVersion
        if ($vi.ProductVersion -and $vi.ProductVersion -match '\+([0-9a-fA-F]{40})$') {
            $result.EmbeddedCommitSha = $Matches[1].ToLowerInvariant()
        }
    } catch {
        $result.Facts += "读取文件版本信息失败：$($_.Exception.Message)"
    }

    # --- 相关源码的最后写入时间 ---
    $updaterDir = Resolve-RepoPath -Root $Root -Relative 'LiteMonitor.Updater'
    $srcFiles = @()
    if (Test-Path -LiteralPath $updaterDir) {
        $srcFiles = Get-ChildItem -LiteralPath $updaterDir -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Extension -in @('.cs', '.csproj') -and
                $_.FullName -notmatch '\\(obj|bin)\\' -and
                $_.FullName -notmatch '\\Properties\\AssemblyInfo\.cs$'
            }
    }
    if ($srcFiles.Count -gt 0) {
        $latest = $srcFiles | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        $result.LatestSourceWriteUtc = $latest.LastWriteTimeUtc.ToString('o')
    }

    $git = Get-GitState -Root $Root

    # --- 内容级判定 ---
    if ($result.EmbeddedCommitSha -and $git.Available) {
        $sha = $result.EmbeddedCommitSha
        $type = ((& git -C $Root cat-file -t $sha 2>$null) | Select-Object -First 1)
        if ($type -eq 'commit') {
            $result.BuiltFromCommitKnown = $true
            $info = ((& git -C $Root log -1 --format='%h %ci %s' $sha 2>$null) | Select-Object -First 1)
            $result.BuiltFromCommitInfo = $info

            $changed = @()
            try {
                $changed = @((& git -C $Root diff --name-only $sha -- 'LiteMonitor.Updater/' 2>$null) |
                    Where-Object { $_ -and $_.Trim() })
            } catch {
                $changed = @()
            }
            $result.SourceFilesChanged = $changed

            if ($changed.Count -eq 0) {
                $result.Verdict = 'CONSISTENT'
                $result.VerdictText = '与本次源码构建一致'
                $result.Reason = ("产物内嵌构建提交 {0}；`git diff {0} -- LiteMonitor.Updater/` 为空，" +
                    '即 LiteMonitor.Updater/ 目录的当前内容与产物构建时的内容逐字节一致。') -f $sha
                return [pscustomobject]$result
            }

            # 有差异：比较差异文件的写入时间与产物写入时间
            $newer = @()
            foreach ($rel in $changed) {
                $f = Resolve-RepoPath -Root $Root -Relative $rel
                if (Test-Path -LiteralPath $f) {
                    if ((Get-Item -LiteralPath $f).LastWriteTimeUtc -gt $item.LastWriteTimeUtc) {
                        $newer += $rel
                    }
                }
            }
            $result.NewerThanArtifact = $newer

            if ($newer.Count -gt 0) {
                $result.Verdict = 'STALE'
                $result.VerdictText = '不一致（需重建）'
                $result.Reason = ("产物内嵌构建提交 {0}，但下列源码文件在该产物写盘之后被修改过：" +
                    ($newer -join ', ') + '。产物不可能包含这些改动，需重新构建。') -f $sha
            } else {
                $result.Verdict = 'NOT_VERIFIED'
                $result.VerdictText = 'NOT_VERIFIED（无法判定：产物与当前源码树存在差异，但无法证明内容一致）'
                $result.Reason = ("产物内嵌构建提交 {0}；当前 LiteMonitor.Updater/ 工作树与该提交存在差异（{1}），" +
                    '但差异文件的写入时间早于产物写盘时间。.NET 发布产物不是字节确定性的，' +
                    '无法在不做重新构建的前提下判定产物内容是否已包含这些改动 —— 需重建后复跑本检查。') -f $sha, ($changed -join ', ')
            }
            return [pscustomobject]$result
        }
    }

    # --- 兜底：无内嵌提交 SHA 或非 git 仓库 ---
    if ($result.LatestSourceWriteUtc) {
        $latestSrc = [datetime]::Parse($result.LatestSourceWriteUtc).ToUniversalTime()
        if ($latestSrc -gt $item.LastWriteTimeUtc) {
            $result.Verdict = 'STALE'
            $result.VerdictText = '不一致（需重建）'
            $result.Reason = ("未找到产物内嵌的构建提交 SHA，且相关源码（最后修改 {0}）比产物（{1}）更新，" +
                '产物不可能包含这些改动。') -f $result.LatestSourceWriteUtc, $result.LastWriteTimeUtc
            return [pscustomobject]$result
        }
    }

    $result.Verdict = 'NOT_VERIFIED'
    $result.VerdictText = 'NOT_VERIFIED（无法判定：缺少可验证的构建来源证据）'
    $result.Reason = '无法从产物中提取内嵌的构建提交 SHA（且/或当前目录不是 git 工作树），' +
        '仅凭时间戳无法可靠判定产物是否由当前源码构建。'
    return [pscustomobject]$result
}

#endregion

#region 主流程

$root = $null
try {
    $root = Resolve-RepoRoot -Given $RepoRoot
} catch {
    Write-Output "ERROR: $($_.Exception.Message)"
    exit $script:ExitUsage
}

$noticeFile = Resolve-RepoPath -Root $root -Relative $NoticePath
$productProject = Resolve-RepoPath -Root $root -Relative $ProjectPath

Write-Report "LiteMonitor++ 第三方依赖清单核对"
Write-Report ("仓库根       : {0}" -f $root)
Write-Report ("模式         : {0}" -f $Mode)
Write-Report ("基准工程     : {0}" -f $ProjectPath)
Write-Report ("NOTICE       : {0}" -f $NoticePath)
Write-Report ''

# --- NOTICE 声明 ---
$noticeParsed = $null
try {
    $noticeParsed = Get-NoticeDeclarations -NoticeFile $noticeFile
} catch {
    Write-Output "ERROR: $($_.Exception.Message)"
    exit $script:ExitUsage
}
$declarations = @($noticeParsed.Declarations)

# 直接依赖段 / 传递依赖段分开建索引。两段不能混用：否则"多声明(EXTRA)"会把
# 传递依赖误判成"已经不再依赖"，而漏列/版本不符也会串段。
$declMap = @{}          # 直接依赖段
$transDeclMap = @{}     # 传递依赖段
foreach ($d in $declarations) {
    if ($d.Section -eq 'Transitive') { $transDeclMap[$d.Name] = $d } else { $declMap[$d.Name] = $d }
}

Write-Report '== NOTICE 声明（形如 "N) 名称 版本"）=='
if ($declarations.Count -eq 0) {
    Write-Report '  （未解析到任何声明行）'
} else {
    foreach ($d in ($declarations | Sort-Object Number)) {
        $suffix = if ($d.Section -eq 'Transitive') { '，传递依赖' } else { '' }
        Write-Report ("  {0,2}) {1} {2}    (NOTICE:{3}{4})" -f $d.Number, $d.Name, $d.Version, $d.Line, $suffix)
    }
}
if (-not $noticeParsed.HasTransitive) {
    Write-Report '  ! NOTICE 中未找到「传递依赖」章节 —— 传递依赖披露检查必然不通过'
}
Write-Report ''


# --- 实际解析依赖 ---
$globalPackages = Get-NuGetGlobalPackagesRoot
Write-Report ("NuGet 全局缓存: {0}" -f $globalPackages)
Write-Report ''

$refMeta = Get-PackageReferenceMetadata -ProjectFile $productProject

$packages = @()
$resolved = $null
try {
    $resolved = Get-ResolvedPackages -Root $root -ProjectFile $productProject
} catch {
    Write-Output "ERROR: $($_.Exception.Message)"
    exit $script:ExitEnv
}

Write-Report ("真值来源     : {0}" -f $resolved.Source)
if ($resolved.Degraded) {
    Write-Report ("               ! {0}" -f $resolved.Note)
}
Write-Report ''

$topLevel = @()
foreach ($fw in $resolved.Frameworks) { $topLevel += $fw.TopLevel }

# --- 随包分发判定（还原图 + 目标框架冲突消解） ---
$assetsGraph = Get-AssetsGraph -ProjectFile $productProject
$frameworkAssemblies = @{}
$targetingPack = $null
$distMap = @{}
$distDegraded = $false
$distReason = $null

if ($null -eq $assetsGraph) {
    $distDegraded = $true
    $distReason = "读不到 {0}，无法判定随包分发" -f (Join-Path (Split-Path -Parent $productProject) 'obj\project.assets.json')
} else {
    # 目标框架 ref 目录名：net8.0-windows7.0 -> net8.0
    $refTfm = ($assetsGraph.Tfm -split '-')[0]
    $targetingPack = Get-TargetingPackAssemblyMap -TargetFramework $refTfm
    if (-not $targetingPack.Found) {
        $distDegraded = $true
        $distReason = "找不到 $refTfm 的目标框架清单（targeting pack），无法消解与 .NET 共享框架同名的程序集"
    } else {
        $frameworkAssemblies = $targetingPack.Map
        $distMap = Get-DistributionMap -AssetsGraph $assetsGraph -GlobalPackagesRoot $globalPackages -FrameworkAssemblies $frameworkAssemblies
    }
}

Write-Report ("随包分发判据 : {0}" -f $(if ($distDegraded) { '降级（见下方说明）' } else { '还原图 + 目标框架冲突消解' }))
if (-not $distDegraded) {
    Write-Report ("               还原图目标: {0}（TFM={1}）" -f $assetsGraph.TargetKey, $assetsGraph.Tfm)
    Write-Report ("               目标框架包: {0}" -f (@($targetingPack.Sources) -join ', '))
} else {
    Write-Report ("               ! {0}" -f $distReason)
}
Write-Report ''

# 基准工程自身的直接依赖
$rows = @()
foreach ($p in ($topLevel | Sort-Object Id -Unique)) {
    $facts = Get-PackageFacts -GlobalPackagesRoot $globalPackages -Id $p.Id -Version $p.ResolvedVersion
    $meta = $null
    if ($refMeta.ContainsKey($p.Id)) { $meta = $refMeta[$p.Id] }

    # 随包分发：以还原图判定为准；降级时退回"包内是否有运行库资产"的粗判据
    $hasRuntime = $facts.HasRuntimeAsset
    if ($distMap.ContainsKey($p.Id)) {
        $hasRuntime = $distMap[$p.Id].Distributed
    } elseif ($distDegraded) {
        $hasRuntime = $facts.HasRuntimeAsset
    } else {
        $hasRuntime = $false
    }
    $redist = Test-IsRedistributed -HasRuntimeAsset $hasRuntime -RefMeta $meta

    $status = 'OK'
    $detail = ''
    if (-not $declMap.ContainsKey($p.Id)) {
        $status = 'MISSING'
        $detail = 'NOTICE 未声明该直接依赖'
    } elseif (-not (Test-VersionEqual -A $declMap[$p.Id].Version -B $p.ResolvedVersion)) {
        $status = 'MISMATCH'
        $detail = ("NOTICE 声明 {0}，实际解析 {1}" -f $declMap[$p.Id].Version, $p.ResolvedVersion)
    }

    $rows += [pscustomobject]@{
        Package           = $p.Id
        ResolvedVersion   = $p.ResolvedVersion
        License           = $facts.License
        LicenseType       = $facts.LicenseType
        SourceUrl         = $facts.RepositoryUrl
        ProjectUrl        = $facts.ProjectUrl
        Authors           = $facts.Authors
        Redistributed     = $redist
        AutoReferenced    = $p.AutoReferenced
        Gated             = (-not $p.AutoReferenced) -and $redist
        Status            = $status
        Detail            = $detail
        NoticeLine        = if ($declMap.ContainsKey($p.Id)) { $declMap[$p.Id].Line } else { $null }
        NoticeVersion     = if ($declMap.ContainsKey($p.Id)) { $declMap[$p.Id].Version } else { $null }
        NuspecPath        = $facts.NuspecPath
        NuspecError       = $facts.Error
        Project           = $ProjectPath
    }
}

# 额外扫描工程：其直接依赖若随包分发且未声明，同样计入漏列
$scanRows = @()
foreach ($rel in $ScanProjects) {
    $pj = Resolve-RepoPath -Root $root -Relative $rel
    if (-not (Test-Path -LiteralPath $pj)) { continue }
    try {
        $r = Get-ResolvedPackages -Root $root -ProjectFile $pj
    } catch {
        continue
    }
    $rMeta = Get-PackageReferenceMetadata -ProjectFile $pj
    $tl = @()
    foreach ($fw in $r.Frameworks) { $tl += $fw.TopLevel }
    foreach ($p in ($tl | Sort-Object Id -Unique)) {
        $facts = Get-PackageFacts -GlobalPackagesRoot $globalPackages -Id $p.Id -Version $p.ResolvedVersion
        $meta = $null
        if ($rMeta.ContainsKey($p.Id)) { $meta = $rMeta[$p.Id] }
        $redist = Test-IsRedistributed -HasRuntimeAsset $facts.HasRuntimeAsset -RefMeta $meta

        $status = 'OK'
        if (-not $p.AutoReferenced -and $redist) {
            if (-not $declMap.ContainsKey($p.Id)) {
                $status = 'MISSING'
            } elseif ($declMap[$p.Id].Version -ne $p.ResolvedVersion) {
                $status = 'MISMATCH'
            }
        }
        $scanRows += [pscustomobject]@{
            Package         = $p.Id
            ResolvedVersion = $p.ResolvedVersion
            License         = $facts.License
            Redistributed   = $redist
            AutoReferenced  = $p.AutoReferenced
            Gated           = (-not $p.AutoReferenced) -and $redist
            Status          = $status
            Project         = $rel
        }
    }
}

Write-Report '== 逐条判定 =='
foreach ($r in $rows) {
    $mark = switch ($r.Status) {
        'OK' { '一致  ' }
        'MISSING' { '漏列  ' }
        'MISMATCH' { '版本不符' }
        default { $r.Status }
    }
    $line = ("  [{0}] {1} {2}  许可证={3}  分发={4}" -f $mark, $r.Package, $r.ResolvedVersion,
        $(if ($r.License) { $r.License } else { '未知' }), $(if ($r.Redistributed) { '是' } else { '否' }))
    if ($r.Status -ne 'OK') { $line += "  ← {0}" -f $r.Detail }
    Write-Report $line
    if ($r.NuspecError) { Write-Report ("           ! {0}" -f $r.NuspecError) }
}
Write-Report ''

# --- 传递依赖：只针对"实际随包分发"的组件做披露核对 ---
$directIds = @($rows | ForEach-Object { $_.Package })
$transPkgMap = @{}
foreach ($fw in $resolved.Frameworks) {
    foreach ($t in $fw.Transitive) {
        if ($directIds -contains $t.Id) { continue }
        if (-not $transPkgMap.ContainsKey($t.Id)) {
            $transPkgMap[$t.Id] = [pscustomobject]@{ Id = $t.Id; Version = $t.ResolvedVersion }
        }
    }
}

$transRows = @()      # 随包分发 ⇒ 必须在 NOTICE「传递依赖」段披露
$buildOnlyRows = @()   # 不随包分发 ⇒ 不需要披露
foreach ($tid in ($transPkgMap.Keys | Sort-Object)) {
    $info = $transPkgMap[$tid]
    $facts = Get-PackageFacts -GlobalPackagesRoot $globalPackages -Id $tid -Version $info.Version
    $distributed = $false
    if ($distMap.ContainsKey($tid)) { $distributed = $distMap[$tid].Distributed }
    $parents = @(Get-DirectAncestors -AssetsGraph $assetsGraph -DirectIds $directIds -Id $tid)

    $status = 'OK'
    $detail = ''
    if ($distributed) {
        if (-not $transDeclMap.ContainsKey($tid)) {
            $status = 'MISSING'
            $detail = 'NOTICE「传递依赖」段未声明该组件'
        } elseif (-not (Test-VersionEqual -A $transDeclMap[$tid].Version -B $info.Version)) {
            $status = 'MISMATCH'
            $detail = ("NOTICE 声明 {0}，实际解析 {1}" -f $transDeclMap[$tid].Version, $info.Version)
        }
    }

    $row = [pscustomobject]@{
        Package         = $tid
        ResolvedVersion = $info.Version
        License         = $facts.License
        LicenseType     = $facts.LicenseType
        SourceUrl       = $facts.RepositoryUrl
        ProjectUrl      = $facts.ProjectUrl
        Authors         = $facts.Authors
        Distributed     = $distributed
        IntroducedBy    = ($parents -join ', ')
        Status          = $status
        Detail          = $detail
        NoticeLine      = if ($transDeclMap.ContainsKey($tid)) { $transDeclMap[$tid].Line } else { $null }
        NoticeVersion   = if ($transDeclMap.ContainsKey($tid)) { $transDeclMap[$tid].Version } else { $null }
        NuspecPath      = $facts.NuspecPath
        NuspecError     = $facts.Error
    }
    if ($distributed) { $transRows += $row } else { $buildOnlyRows += $row }
}

Write-Report '== 传递依赖判定（仅"实际随包分发"的组件）=='
if ($transRows.Count -eq 0) {
    Write-Report '  （没有随包分发的传递依赖）'
} else {
    foreach ($r in $transRows) {
        $mark = switch ($r.Status) {
            'OK' { '一致  ' }
            'MISSING' { '漏列  ' }
            'MISMATCH' { '版本不符' }
            default { $r.Status }
        }
        $line = ("  [{0}] {1} {2}  许可证={3}  引入={4}" -f $mark, $r.Package, $r.ResolvedVersion,
            $(if ($r.License) { $r.License } else { '未知' }), $(if ($r.IntroducedBy) { $r.IntroducedBy } else { '未知' }))
        if ($r.Status -ne 'OK') { $line += "  ← {0}" -f $r.Detail }
        Write-Report $line
        if ($r.NuspecError) { Write-Report ("           ! {0}" -f $r.NuspecError) }
    }
}

if ($buildOnlyRows.Count -gt 0) {
    Write-Report ''
    Write-Report '== 仅构建期 / 不随包分发（无需在 NOTICE 中披露）=='
    foreach ($r in $buildOnlyRows) {
        $reason = '无运行库资产（仅编译期引用）'
        if ($distMap.ContainsKey($r.Package)) { $reason = $distMap[$r.Package].Reason }
        Write-Report ("  [不分发] {0} {1}  {2}" -f $r.Package, $r.ResolvedVersion, $reason)
    }
}
Write-Report ''

if ($scanRows.Count -gt 0) {
    Write-Report ''
    Write-Report '== 附加扫描工程（仅报告，开发/工具依赖不计入护栏基线）=='
    foreach ($r in $scanRows) {
        $mark = switch ($r.Status) {
            'OK' { '一致  ' }
            'MISSING' { '漏列  ' }
            'MISMATCH' { '版本不符' }
            default { $r.Status }
        }
        Write-Report ("  [{0}] {1} {2}  许可证={3}  分发={4}  自动引用={5}  ({6})" -f $mark, $r.Package,
            $r.ResolvedVersion, $(if ($r.License) { $r.License } else { '未知' }),
            $(if ($r.Redistributed) { '是' } else { '否' }), $r.AutoReferenced, $r.Project)
    }
}
Write-Report ''

# --- 汇总 ---
$missing = @($rows | Where-Object { $_.Status -eq 'MISSING' })
$mismatch = @($rows | Where-Object { $_.Status -eq 'MISMATCH' })
$scanMissing = @($scanRows | Where-Object { $_.Status -eq 'MISSING' })
$scanMismatch = @($scanRows | Where-Object { $_.Status -eq 'MISMATCH' })

$declaredNames = @($declMap.Keys)
# "多声明" 的判定基准 = 所有工程里**随包分发**的受管直接依赖（产品工程 + 附加扫描工程）
$actualGated = @(
    ($rows | Where-Object { $_.Gated } | ForEach-Object { $_.Package })
    + ($scanRows | Where-Object { $_.Gated } | ForEach-Object { $_.Package })
)
$extra = @()
foreach ($n in $declaredNames) {
    if ($actualGated -notcontains $n) {
        $extra += $declMap[$n]
    }
}

$missingAll = @($missing) + @($scanMissing)
$mismatchAll = @($mismatch) + @($scanMismatch)

# 传递依赖侧的判定：只针对随包分发的组件
$transMissing = @($transRows | Where-Object { $_.Status -eq 'MISSING' })
$transMismatch = @($transRows | Where-Object { $_.Status -eq 'MISMATCH' })
$distributedTransIds = @($transRows | ForEach-Object { $_.Package })
$transExtra = @()
foreach ($n in $transDeclMap.Keys) {
    if ($distributedTransIds -notcontains $n) { $transExtra += $transDeclMap[$n] }
}

Write-Report '== 汇总 =='
if ($missingAll.Count -gt 0) {
    Write-Report '漏列（实际直接依赖未在 NOTICE 中声明）：'
    foreach ($m in $missingAll) { Write-Report ("  - {0} {1}  ({2})" -f $m.Package, $m.ResolvedVersion, $m.Project) }
}
if ($mismatchAll.Count -gt 0) {
    Write-Report '版本不符（NOTICE 声明版本 ≠ 实际解析版本）：'
    foreach ($m in $mismatchAll) {
        Write-Report ("  - {0}：声明 {1}，实际 {2}  ({3})" -f $m.Package, $m.NoticeVersion, $m.ResolvedVersion, $m.Project)
    }
}
if ($extra.Count -gt 0) {
    Write-Report '多声明（NOTICE 声明了但实际不再作为直接依赖 —— 仅提示，请人工判断，勿自动删除）：'
    foreach ($e in $extra) {
        Write-Report ("  - {0} {1}  (NOTICE:{2})" -f $e.Name, $e.Version, $e.Line)
    }
}
Write-Report ("SUMMARY MISSING={0} MISMATCH={1} EXTRA={2}" -f $missingAll.Count, $mismatchAll.Count, $extra.Count)

if ($transMissing.Count -gt 0) {
    Write-Report '传递依赖漏列（随包分发但 NOTICE「传递依赖」段未声明）：'
    foreach ($m in $transMissing) {
        Write-Report ("  - {0} {1}  引入={2}" -f $m.Package, $m.ResolvedVersion, $m.IntroducedBy)
    }
}
if ($transMismatch.Count -gt 0) {
    Write-Report '传递依赖版本不符（NOTICE 声明版本 ≠ 实际解析版本）：'
    foreach ($m in $transMismatch) {
        Write-Report ("  - {0}：声明 {1}，实际 {2}" -f $m.Package, $m.NoticeVersion, $m.ResolvedVersion)
    }
}
if ($transExtra.Count -gt 0) {
    Write-Report '传递依赖多声明（NOTICE 声明了但已不再随包分发 —— 仅提示，请人工判断，勿自动删除）：'
    foreach ($e in $transExtra) {
        Write-Report ("  - {0} {1}  (NOTICE:{2})" -f $e.Name, $e.Version, $e.Line)
    }
}
Write-Report ("SUMMARY-TRANSITIVE MISSING={0} MISMATCH={1} EXTRA={2}" -f $transMissing.Count, $transMismatch.Count, $transExtra.Count)
Write-Report ''


# --- 包内更新器检查项 ---
Write-Report '== 包内更新器 =='
$updater = Get-UpdaterCheck -Root $root
Write-Report ("  产物        : {0}" -f $updater.ExePath)
if ($updater.Exists) {
    Write-Report ("  存在        : 是")
    Write-Report ("  大小        : {0} 字节" -f $updater.SizeBytes)
    Write-Report ("  最后写入    : {0}" -f $updater.LastWriteTimeUtc)
    Write-Report ("  SHA-256     : {0}" -f $updater.Sha256)
    Write-Report ("  FileVersion : {0}" -f $updater.FileVersion)
    Write-Report ("  ProductVersion: {0}" -f $updater.ProductVersion)
} else {
    Write-Report '  存在        : 否'
}
Write-Report ("  内嵌构建提交: {0}" -f $(if ($updater.BuiltFromCommitKnown) { $updater.EmbeddedCommitSha } else { '（无 / 不可解析）' }))
if ($updater.BuiltFromCommitInfo) { Write-Report ("  构建提交信息: {0}" -f $updater.BuiltFromCommitInfo) }
Write-Report ("  相关源码最后修改: {0}" -f $(if ($updater.LatestSourceWriteUtc) { $updater.LatestSourceWriteUtc } else { '未知' }))
Write-Report ("  csproj 以 Content 复制: {0}{1}" -f $updater.CopyAsContent,
    $(if ($updater.CopyAsContent) { "（LiteMonitor.csproj:{0}）" -f ($updater.CopyAsContentLines -join ',') } else { '' }))
Write-Report ("  判定        : {0}" -f $updater.VerdictText)
Write-Report ("  依据        : {0}" -f $updater.Reason)
Write-Report ''

# --- Emit ---
if ($Mode -eq 'Emit') {
    $target = $OutPath
    if (-not $target) {
        $ext = if ($Format -eq 'Json') { 'json' } else { 'md' }
        $target = Join-Path ([System.IO.Path]::GetTempPath()) ("litemonitor-dependency-manifest.{0}" -f $ext)
    } elseif (-not [System.IO.Path]::IsPathRooted($target)) {
        $target = Join-Path (Get-Location).Path $target
    }

    $manifest = [ordered]@{
        schemaVersion   = 1
        generatedAtUtc  = [DateTime]::UtcNow.ToString('o')
        repoRoot        = $root
        mode            = 'Emit'
        truthSource     = $resolved.Source
        truthDegraded   = $resolved.Degraded
        gateProject     = $ProjectPath
        noticePath      = $NoticePath
        noticeHasTransitiveSection = $noticeParsed.HasTransitive
        noticeSectionStartLine     = if ($noticeParsed.HasTransitive) { $noticeParsed.SectionStart + 1 } else { $null }
        distributionSource   = $(if ($distDegraded) { 'degraded' } else { 'project.assets.json + targeting-pack conflict resolution' })
        distributionDegraded = $distDegraded
        distributionNote     = $distReason
        noticeDeclarations = @($declarations | ForEach-Object {
                [ordered]@{ number = $_.Number; name = $_.Name; version = $_.Version; noticeLine = $_.Line; section = $_.Section }
            })
        packages        = @($rows | ForEach-Object {
                [ordered]@{
                    id                  = $_.Package
                    resolvedVersion     = $_.ResolvedVersion
                    licenseExpression  = $_.License
                    licenseType         = $_.LicenseType
                    sourceUrl           = $_.SourceUrl
                    projectUrl          = $_.ProjectUrl
                    authors             = $_.Authors
                    redistributed       = $_.Redistributed
                    autoReferenced      = $_.AutoReferenced
                    gated               = $_.Gated
                    noticeStatus        = switch ($_.Status) {
                        'OK' { '一致' }
                        'MISSING' { '漏列' }
                        'MISMATCH' { '版本不符' }
                        default { $_.Status }
                    }
                    noticeVersion       = $_.NoticeVersion
                    noticeLine          = $_.NoticeLine
                    nuspecPath          = $_.NuspecPath
                    project             = $_.Project
                }
            })
        scannedProjects   = @($scanRows | ForEach-Object {
                [ordered]@{
                    id              = $_.Package
                    resolvedVersion = $_.ResolvedVersion
                    licenseExpression = $_.License
                    redistributed   = $_.Redistributed
                    autoReferenced  = $_.AutoReferenced
                    gated           = $_.Gated
                    noticeStatus    = switch ($_.Status) {
                        'OK' { '一致' }
                        'MISSING' { '漏列' }
                        'MISMATCH' { '版本不符' }
                        default { $_.Status }
                    }
                    project         = $_.Project
                }
            })
        transitivePackages = @($transRows | ForEach-Object {
                [ordered]@{
                    id                  = $_.Package
                    resolvedVersion     = $_.ResolvedVersion
                    licenseExpression  = $_.License
                    licenseType         = $_.LicenseType
                    sourceUrl           = $_.SourceUrl
                    projectUrl          = $_.ProjectUrl
                    authors             = $_.Authors
                    redistributed       = $_.Distributed
                    introducedBy        = $_.IntroducedBy
                    noticeStatus        = switch ($_.Status) {
                        'OK' { '一致' }
                        'MISSING' { '漏列' }
                        'MISMATCH' { '版本不符' }
                        default { $_.Status }
                    }
                    noticeVersion       = $_.NoticeVersion
                    noticeLine          = $_.NoticeLine
                    nuspecPath          = $_.NuspecPath
                }
            })
        buildOnlyPackages = @($buildOnlyRows | ForEach-Object {
                [ordered]@{
                    id                  = $_.Package
                    resolvedVersion     = $_.ResolvedVersion
                    licenseExpression  = $_.License
                    redistributed       = $false
                    introducedBy        = $_.IntroducedBy
                }
            })
        summary           = [ordered]@{
            MISSING  = $missingAll.Count
            MISMATCH = $mismatchAll.Count
            EXTRA    = $extra.Count
            TRANSITIVE_MISSING  = $transMissing.Count
            TRANSITIVE_MISMATCH = $transMismatch.Count
            TRANSITIVE_EXTRA    = $transExtra.Count
        }
        updaterCheck     = [ordered]@{
            exePath              = $updater.ExePath
            exists               = $updater.Exists
            sizeBytes            = $updater.SizeBytes
            lastWriteTimeUtc     = $updater.LastWriteTimeUtc
            sha256               = $updater.Sha256
            fileVersion          = $updater.FileVersion
            productVersion       = $updater.ProductVersion
            embeddedCommitSha    = $updater.EmbeddedCommitSha
            builtFromCommitInfo  = $updater.BuiltFromCommitInfo
            latestSourceWriteUtc = $updater.LatestSourceWriteUtc
            copiedAsContent      = $updater.CopyAsContent
            copiedAsContentLines = @($updater.CopyAsContentLines)
            verdict              = $updater.Verdict
            verdictText          = $updater.VerdictText
            reason               = $updater.Reason
        }
    }

    $dir = Split-Path -Parent $target
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    if ($Format -eq 'Json') {
        $json = $manifest | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText($target, $json, $script:Utf8NoBom)
    } else {
        $md = @()
        $md += '# LiteMonitor++ 第三方依赖清单'
        $md += ''
        $md += ("- 生成时间（UTC）：{0}" -f $manifest.generatedAtUtc)
        $md += ("- 真值来源：{0}" -f $manifest.truthSource)
        $md += ("- 基准工程：{0}" -f $ProjectPath)
        $md += ''
        $md += '| 包 | 解析版本 | 许可证 | 来源 | 随包分发 | NOTICE 状态 |'
        $md += '| --- | --- | --- | --- | --- | --- |'
        foreach ($p in $manifest.packages) {
            $md += ("| {0} | {1} | {2} | {3} | {4} | {5} |" -f $p.id, $p.resolvedVersion,
                $p.licenseExpression, $p.sourceUrl, $(if ($p.redistributed) { '是' } else { '否' }), $p.noticeStatus)
        }
        $md += ''
        $md += ("SUMMARY MISSING={0} MISMATCH={1} EXTRA={2}" -f $manifest.summary.MISSING, $manifest.summary.MISMATCH, $manifest.summary.EXTRA)
        $md += ''
        $md += '## 传递依赖（实际随包分发）'
        $md += ''
        $md += '| 包 | 解析版本 | 许可证 | 来源 | 由哪个直接依赖带入 | NOTICE 状态 |'
        $md += '| --- | --- | --- | --- | --- | --- |'
        foreach ($p in $manifest.transitivePackages) {
            $md += ("| {0} | {1} | {2} | {3} | {4} | {5} |" -f $p.id, $p.resolvedVersion,
                $p.licenseExpression, $p.sourceUrl, $p.introducedBy, $p.noticeStatus)
        }
        $md += ''
        $md += ("SUMMARY-TRANSITIVE MISSING={0} MISMATCH={1} EXTRA={2}" -f $manifest.summary.TRANSITIVE_MISSING, $manifest.summary.TRANSITIVE_MISMATCH, $manifest.summary.TRANSITIVE_EXTRA)
        $md += ''
        $md += '## 仅构建期 / 不随包分发'
        $md += ''
        $md += '| 包 | 解析版本 | 许可证 | 由哪个直接依赖带入 |'
        $md += '| --- | --- | --- | --- |'
        foreach ($p in $manifest.buildOnlyPackages) {
            $md += ("| {0} | {1} | {2} | {3} |" -f $p.id, $p.resolvedVersion, $p.licenseExpression, $p.introducedBy)
        }
        $md += ''
        $md += '## 包内更新器'

        $md += ''
        $md += ("- 判定：**{0}**" -f $manifest.updaterCheck.verdictText)
        $md += ("- 依据：{0}" -f $manifest.updaterCheck.reason)
        [System.IO.File]::WriteAllText($target, ($md -join "`n"), $script:Utf8NoBom)
    }

    Write-Report ("清单已生成: {0}" -f $target)
    Write-Report ''
}

if ($missingAll.Count -gt 0 -or $mismatchAll.Count -gt 0) {
    Write-Report 'RESULT: FAIL（存在漏列 / 版本不符）'
    exit $script:ExitPolicy
}

if ($transMissing.Count -gt 0 -or $transMismatch.Count -gt 0) {
    if ($distDegraded) {
        # 判定来源降级时，"漏列/版本不符"的结论不可信 —— 报环境受阻而不是护栏失败
        Write-Report 'RESULT: BLOCKED（随包分发判定降级，传递依赖结论不可信；请修复还原/目标框架环境后复跑）'
        exit $script:ExitEnv
    }
    Write-Report 'RESULT: FAIL（传递依赖存在漏列 / 版本不符）'
    exit $script:ExitPolicy
}

if ($distDegraded) {
    Write-Report 'RESULT: BLOCKED（随包分发判定降级，传递依赖披露检查未能完整执行）'
    exit $script:ExitEnv
}

Write-Report 'RESULT: PASS（NOTICE 声明与实际解析依赖一致）'
exit $script:ExitOk

#endregion
