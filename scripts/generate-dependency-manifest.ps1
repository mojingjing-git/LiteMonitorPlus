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
      3 = 环境受阻（dotnet 不可用、工程无法还原且无 assets 回退等）
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
                if (Test-Path -LiteralPath $licenseFile) {
                    $result.License = (Get-Content -LiteralPath $licenseFile -Raw -Encoding UTF8) -replace '\s+', ' '
                    $result.License = $result.License.Trim()
                } else {
                    $result.License = "FILE:$text"
                }
            } else {
                $result.License = $text
            }
        }

        $result.LicenseUrl = [string]$meta.licenseUrl
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

function Get-NoticeDeclarations {
    param([string]$NoticeFile)

    if (-not (Test-Path -LiteralPath $NoticeFile)) {
        throw "找不到 NOTICE 文件：$NoticeFile"
    }
    # 显式 UTF-8 读取
    $text = [System.IO.File]::ReadAllText($NoticeFile, [System.Text.UTF8Encoding]::new($false))
    $lines = $text -split "`r`n|`n|`r"

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
            }
        }
    }
    return $decls
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
$declarations = @()
try {
    $declarations = Get-NoticeDeclarations -NoticeFile $noticeFile
} catch {
    Write-Output "ERROR: $($_.Exception.Message)"
    exit $script:ExitUsage
}

$declMap = @{}
foreach ($d in $declarations) {
    $declMap[$d.Name] = $d
}

Write-Report '== NOTICE 声明（形如 "N) 名称 版本"）=='
if ($declarations.Count -eq 0) {
    Write-Report '  （未解析到任何声明行）'
} else {
    foreach ($d in ($declarations | Sort-Object Number)) {
        Write-Report ("  {0,2}) {1} {2}    (NOTICE:{3})" -f $d.Number, $d.Name, $d.Version, $d.Line)
    }
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

# 基准工程自身的直接依赖
$rows = @()
foreach ($p in ($topLevel | Sort-Object Id -Unique)) {
    $facts = Get-PackageFacts -GlobalPackagesRoot $globalPackages -Id $p.Id -Version $p.ResolvedVersion
    $meta = $null
    if ($refMeta.ContainsKey($p.Id)) { $meta = $refMeta[$p.Id] }
    $redist = Test-IsRedistributed -HasRuntimeAsset $facts.HasRuntimeAsset -RefMeta $meta

    $status = 'OK'
    $detail = ''
    if (-not $declMap.ContainsKey($p.Id)) {
        $status = 'MISSING'
        $detail = 'NOTICE 未声明该直接依赖'
    } elseif ($declMap[$p.Id].Version -ne $p.ResolvedVersion) {
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
        noticeDeclarations = @($declarations | ForEach-Object {
                [ordered]@{ number = $_.Number; name = $_.Name; version = $_.Version; noticeLine = $_.Line }
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
        summary           = [ordered]@{
            MISSING  = $missingAll.Count
            MISMATCH = $mismatchAll.Count
            EXTRA    = $extra.Count
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

Write-Report 'RESULT: PASS（NOTICE 声明与实际解析依赖一致）'
exit $script:ExitOk

#endregion
