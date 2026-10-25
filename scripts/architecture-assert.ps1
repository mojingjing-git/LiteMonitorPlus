<#
.SYNOPSIS
    架构断言（棘轮模式）：C 组命名空间统一目标、A 组 Core 分层解耦。

.DESCRIPTION
    来源：feat/r06c-namespace 的 scripts/architecture-assert.ps1（commit 3169dc0），
    2026-10-26 按裁决摘取并适配 master 现状
    （见 docs/plans/2026-10-26-architecture-decoupling-plan.md §7.1「只摘脚本」）。

    适配要点：
    - 期望值不硬编码 0，而是与 scripts/architecture-baseline.json 基线比对：
      实际值 ≤ 基线即通过（棘轮——只拦"变差"，允许改善）。
    - A-1～A-4 的 using 前缀同时匹配新旧两套命名空间（LiteMonitor.X / LiteMonitor.src.X），
      避免在 master 现状下空转。

    收紧计划：B1/B2 落地后下调 A 组基线；B5 命名空间统一后下调 C 组并恢复目录白名单语义；
    B6 接入 CI 强制档（当前未接线，仅本地/手动运行）。

    用法：
      pwsh -NoProfile -File scripts/architecture-assert.ps1                  # 任一断言失败 => 退出码 1
      pwsh -NoProfile -File scripts/architecture-assert.ps1 -WarnOnly        # 仅告警 => 退出码 0
      pwsh -NoProfile -File scripts/architecture-assert.ps1 -UpdateBaseline  # 以当前实际值刷新基线并退出 0

    断言口径：
      C 组 —— docs/plans/2026-10-24-architecture-debt-requirements.md §3.3
      A 组 —— 同文档 §1.3

    A-6 / A-7 的"命中"口径：剔除注释与字符串字面量后，命中的是 WinForms / System.Drawing
    的**类型引用**（new / 变量声明 / 静态成员访问 / typeof / is / as）；
    形如 MetricUtils.UnitContext.Panel 的成员访问、public string Label 的成员名、
    枚举成员（enum UnitContext { Panel }）不计命中。
#>
[CmdletBinding()]
param(
    [switch] $WarnOnly,
    [switch] $UpdateBaseline,
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [string] $BaselinePath = (Join-Path $PSScriptRoot 'architecture-baseline.json')
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path -LiteralPath $Root).Path

$SkipDirs  = @('bin', 'obj', '.git', '.worktrees')
$BS        = [char]92
$LF        = [char]10

# ── 目录 → 命名空间 的历史差异白名单（需求 C4：必须显式记录，不得靠约定俗成）────────
$NamespaceWhitelist = @{
    'src/System'                   = 'LiteMonitor.SystemServices'
    'src/System/HardwareServices'  = 'LiteMonitor.SystemServices'
    'src/System/WebServer'         = 'LiteMonitor.WebServer'
    'src/UI/Settings'              = 'LiteMonitor.UI.SettingsPage'
}

# ── 类型名误判抑制：紧邻在前、且本身是类型/推断关键字 => 命中的是成员名而非类型 ─────
$TypeLikeTokens = @(
    'string','int','bool','object','double','float','decimal','char','byte','long',
    'short','uint','ulong','var','dynamic','nint','nuint','List','Dictionary','HashSet',
    'Task','Func','Action','IEnumerable','IList','ICollection','IDictionary','ValueTuple',
    'Tuple','String','StringBuilder','Nullable','Span','ReadOnlySpan','EqualityComparer'
)

$UiControlTypes  = @('Label','Panel','Control','Application','Timer')
$DrawingTypes    = @('Graphics','Brush','Pen','Image')

function Test-SkippedPath {
    # 以「相对仓库根」的路径分段判定，避免把位于 .worktrees 下的 worktree 自身全部排除
    param([string] $FullPath)
    $rel = $FullPath.Substring($Root.Length).TrimStart([char]92, [char]47)
    foreach ($seg in $rel.Split([char]92)) {
        if ($SkipDirs -contains $seg) { return $true }
    }
    return $false
}

function Get-AllCSharpFiles {
    $result = New-Object System.Collections.Generic.List[string]
    $topItems = @(Get-ChildItem -LiteralPath $Root -Force)
    foreach ($item in $topItems) {
        if ($item.PSIsContainer) {
            if ($SkipDirs -contains $item.Name) { continue }
            $subFiles = @(Get-ChildItem -LiteralPath $item.FullName -Recurse -Filter *.cs -File)
            foreach ($f in $subFiles) {
                if (Test-SkippedPath $f.FullName) { continue }
                $result.Add($f.FullName)
            }
        }
        elseif ($item.Extension -eq '.cs') {
            $result.Add($item.FullName)
        }
    }
    return $result
}

function Get-RelPath {
    param([string] $FullPath)
    $rel = $FullPath.Substring($Root.Length).TrimStart([char]92, [char]47)
    return $rel.Replace($BS, '/')
}

function Get-CodeOnly {
    param([string] $Text)
    $sb = New-Object System.Text.StringBuilder
    $i = 0
    $n = $Text.Length
    while ($i -lt $n) {
        $c  = $Text[$i]
        $c2 = if ($i + 1 -lt $n) { $Text[$i + 1] } else { [char]0 }
        if ($c -eq '/' -and $c2 -eq '/') {
            while ($i -lt $n -and $Text[$i] -ne $LF) { [void]$sb.Append(' '); $i++ }
            continue
        }
        if ($c -eq '/' -and $c2 -eq '*') {
            [void]$sb.Append('  '); $i += 2
            while ($i -lt $n) {
                if ($Text[$i] -eq '*' -and ($i + 1) -lt $n -and $Text[$i + 1] -eq '/') {
                    [void]$sb.Append('  '); $i += 2; break
                }
                [void]$sb.Append(' '); $i++
            }
            continue
        }
        if ($c -eq '@' -and $c2 -eq '"') {
            [void]$sb.Append('  '); $i += 2
            while ($i -lt $n) {
                if ($Text[$i] -eq '"') {
                    if (($i + 1) -lt $n -and $Text[$i + 1] -eq '"') { [void]$sb.Append('  '); $i += 2; continue }
                    [void]$sb.Append(' '); $i++; break
                }
                [void]$sb.Append(' '); $i++
            }
            continue
        }
        if ($c -eq '"') {
            [void]$sb.Append(' '); $i++
            while ($i -lt $n) {
                if ($Text[$i] -eq $BS) { [void]$sb.Append('  '); $i += 2; continue }
                if ($Text[$i] -eq '"') { [void]$sb.Append(' '); $i++; break }
                [void]$sb.Append(' '); $i++
            }
            continue
        }
        if ($c -eq "'") {
            [void]$sb.Append(' '); $i++
            while ($i -lt $n) {
                if ($Text[$i] -eq $BS) { [void]$sb.Append('  '); $i += 2; continue }
                if ($Text[$i] -eq "'") { [void]$sb.Append(' '); $i++; break }
                [void]$sb.Append(' '); $i++
            }
            continue
        }
        [void]$sb.Append($c); $i++
    }
    return $sb.ToString()
}

function Get-EnumRanges {
    param([string] $Code)
    $ranges = New-Object System.Collections.Generic.List[object]
    $rx = [regex]'(?m)\benum\s+\w+[^{]*\{'
    foreach ($m in $rx.Matches($Code)) {
        $depth = 0
        $start = $m.Index + $m.Length - 1
        for ($i = $start; $i -lt $Code.Length; $i++) {
            if ($Code[$i] -eq '{') { $depth++ }
            elseif ($Code[$i] -eq '}') {
                $depth--
                if ($depth -eq 0) { $ranges.Add(@{ Start = $start; End = $i }); break }
            }
        }
    }
    return $ranges
}

function Get-TypeUsageHits {
    param([string] $Code, [string[]] $Words, $EnumRanges)
    $hits = New-Object System.Collections.Generic.List[int]
    foreach ($w in $Words) {
        $rx = New-Object System.Text.RegularExpressions.Regex ('\b' + [regex]::Escape($w) + '\b')
        foreach ($m in $rx.Matches($Code)) {
            $idx = $m.Index
            $inEnum = $false
            foreach ($r in $EnumRanges) {
                if ($idx -gt $r.Start -and $idx -lt $r.End) { $inEnum = $true; break }
            }
            if ($inEnum) { continue }
            $p = $idx - 1
            while ($p -ge 0 -and [char]::IsWhiteSpace($Code[$p])) { $p-- }
            if ($p -ge 0 -and $Code[$p] -eq '.') { continue }
            $q = $p
            while ($q -ge 0 -and [char]::IsLetterOrDigit($Code[$q])) { $q-- }
            $prevWord = ''
            if ($q -lt $p) { $prevWord = $Code.Substring($q + 1, $p - $q) }
            if ($TypeLikeTokens -contains $prevWord) { continue }
            $rest = $Code.Substring($idx + $w.Length)
            if ($rest -match '^\s*(get|set|init)\b') { continue }
            if ($rest -match '^\s*=(?!=)') { continue }
            $hits.Add($idx)
        }
    }
    return $hits
}

function Get-ExpectedNamespace {
    param([string] $RelDir)
    $dir = $RelDir.Replace('/', '/')
    if ($NamespaceWhitelist.ContainsKey($dir)) { return $NamespaceWhitelist[$dir] }
    $parts = $dir.Split('/')
    if ($parts[0] -ne 'src' -or $parts.Length -lt 2) { return $null }
    $segs = @($parts[1..($parts.Length - 1)])
    if ($segs[0] -eq 'System') { $segs[0] = 'SystemServices' }
    return 'LiteMonitor.' + ($segs -join '.')
}

# ── 断言结果容器 ──────────────────────────────────────────────────────────────
$Results = New-Object System.Collections.Generic.List[object]

function Add-Assertion {
    param([string] $Id, [string] $Group, [string] $Title, [int] $Actual, [int] $Expected, [string] $Detail = '')
    $ok = ($Actual -eq $Expected)
    $Results.Add(@{
        Id = $Id; Group = $Group; Title = $Title; Actual = $Actual
        Expected = $Expected; Ok = $ok; Detail = $Detail
    })
}

$files = Get-AllCSharpFiles
Write-Host ("扫描文件: " + $files.Count + " 个 .cs（根目录 " + $Root + "）")

# ── C-1 / C-2：命名空间前缀与裸命名空间 ────────────────────────────────────────
$c1 = 0; $c2 = 0; $c4 = 0; $c3Files = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
    $text = [System.IO.File]::ReadAllText($f)
    $nsMatch = [regex]::Match($text, '(?m)^\s*namespace\s+([\w.]+)')
    if (-not $nsMatch.Success) { continue }
    $ns = $nsMatch.Groups[1].Value
    if ($ns -like 'LiteMonitor.src.*') {
        $c1++
        $pubCount = ([regex]::Matches($text, '(?m)^\s*public\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|unsafe\s+)*(class|struct|interface|enum|record|delegate)\b')).Count
        $c4 += $pubCount
    }
    if ($ns -eq 'LiteMonitor') { $c2++ }
    $rel = Get-RelPath $f
    if ($rel.StartsWith('src/')) {
        $relDir = $rel.Substring(0, $rel.LastIndexOf('/'))
        $expected = Get-ExpectedNamespace $relDir
        if ($expected -and $ns -ne $expected) { $c3Files.Add("$rel => $ns (期望 $expected)") }
    }
}
Add-Assertion 'C-1' 'C' '全仓 namespace LiteMonitor.src. 命中数' $c1 0
Add-Assertion 'C-2' 'C' '全仓裸 namespace LiteMonitor 命中数' $c2 0
Add-Assertion 'C-3' 'C' 'src/ 下目录与声明命名空间不一致（白名单外）' $c3Files.Count 0 ($c3Files -join '; ')
Add-Assertion 'C-4' 'C' 'LiteMonitor.src.* 下的 public 类型数' $c4 0

# ── A 组：Core 分层解耦 ───────────────────────────────────────────────────────
$coreRoot = Join-Path $Root 'src/Core'
$coreFiles = @()
if (Test-Path -LiteralPath $coreRoot) {
    $coreItems = @(Get-ChildItem -LiteralPath $coreRoot -Recurse -Filter *.cs -File)
    foreach ($f in $coreItems) {
        if (-not (Test-SkippedPath $f.FullName)) { $coreFiles += $f.FullName }
    }
}
Write-Host ("扫描 src/Core: " + $coreFiles.Count + " 个 .cs")

function Get-UsingCount {
    param([string[]] $Paths, [string] $Prefix)
    $count = 0; $detail = New-Object System.Collections.Generic.List[string]
    $rx = New-Object System.Text.RegularExpressions.Regex ('(?m)^\s*using\s+' + [regex]::Escape($Prefix) + '\b')
    foreach ($f in $Paths) {
        $text = [System.IO.File]::ReadAllText($f)
        $m = $rx.Matches($text)
        if ($m.Count -gt 0) {
            $count += $m.Count
            $detail.Add(((Get-RelPath $f)) + " x" + $m.Count)
        }
    }
    return @{ Count = $count; Detail = ($detail -join '; ') }
}

$corePaths = @($coreFiles)
function Get-UsingCount2 {
    # 新旧两套命名空间前缀各查一遍再合并（master 现状为 LiteMonitor.src.*，B5 统一后为 LiteMonitor.*）
    param([string[]] $Paths, [string] $Ns1, [string] $Ns2)
    $r1 = Get-UsingCount $Paths $Ns1
    $r2 = Get-UsingCount $Paths $Ns2
    $d = @($r1.Detail, $r2.Detail) | Where-Object { $_ }
    return @{ Count = ($r1.Count + $r2.Count); Detail = ($d -join '; ') }
}
$a1 = Get-UsingCount2 $corePaths 'LiteMonitor.UI' 'LiteMonitor.src.UI'
$a2a = Get-UsingCount2 $corePaths 'LiteMonitor.SystemServices' 'LiteMonitor.src.SystemServices'
$a2b = Get-UsingCount2 $corePaths 'LiteMonitor.WebServer' 'LiteMonitor.src.WebServer'
$a3 = Get-UsingCount2 $corePaths 'LiteMonitor.Plugins' 'LiteMonitor.src.Plugins'
$a4 = Get-UsingCount2 $corePaths 'LiteMonitor.Rendering' 'LiteMonitor.src.Rendering'
$a5 = Get-UsingCount $corePaths 'System.Windows.Forms'
Add-Assertion 'A-1' 'A' 'src/Core 中 using LiteMonitor(.src)?.UI* 命中数' $a1.Count 0 $a1.Detail
Add-Assertion 'A-2' 'A' 'src/Core 中 using LiteMonitor(.src)?.SystemServices*/WebServer* 命中数' ($a2a.Count + $a2b.Count) 0 ('SystemServices: ' + $a2a.Detail + ' | WebServer: ' + $a2b.Detail)
Add-Assertion 'A-3' 'A' 'src/Core 中 using LiteMonitor(.src)?.Plugins* 命中数' $a3.Count 0 $a3.Detail
Add-Assertion 'A-4' 'A' 'src/Core 中 using LiteMonitor(.src)?.Rendering* 命中数' $a4.Count 0 $a4.Detail
Add-Assertion 'A-5' 'A' 'src/Core 中 using System.Windows.Forms 命中数' $a5.Count 0 $a5.Detail

$a6Hits = 0; $a6Detail = New-Object System.Collections.Generic.List[string]
$a7Hits = 0; $a7Detail = New-Object System.Collections.Generic.List[string]
foreach ($f in $coreFiles) {
    $text = [System.IO.File]::ReadAllText($f)
    $code = Get-CodeOnly $text
    $enumRanges = Get-EnumRanges $code
    $h6 = Get-TypeUsageHits $code $UiControlTypes $enumRanges
    $h7 = Get-TypeUsageHits $code $DrawingTypes $enumRanges
    if ($h6.Count -gt 0) { $a6Hits += $h6.Count; $a6Detail.Add(((Get-RelPath $f)) + " x" + $h6.Count) }
    if ($h7.Count -gt 0) { $a7Hits += $h7.Count; $a7Detail.Add(((Get-RelPath $f)) + " x" + $h7.Count) }
}
Add-Assertion 'A-6' 'A' 'src/Core 中 WinForms UI 控件类型引用数' $a6Hits 0 ($a6Detail -join '; ')
Add-Assertion 'A-7' 'A' 'src/Core 中 System.Drawing 绘图类型引用数' $a7Hits 0 ($a7Detail -join '; ')

$a8Files = @('src/Core/UIUtils.cs','src/Core/Actions/AppActions.cs','src/Core/Actions/WebActions.cs')
$a8Present = @()
foreach ($rel in $a8Files) {
    if (Test-Path -LiteralPath (Join-Path $Root $rel.Replace('/', $BS))) { $a8Present += $rel }
}
Add-Assertion 'A-8' 'A' 'src/Core 中三个应迁出文件的存在数' $a8Present.Count 0 ($a8Present -join '; ')

# ── 输出 ──────────────────────────────────────────────────────────────────────
# ── 基线（棘轮）───────────────────────────────────────────────────────────────
if ($UpdateBaseline) {
    $obj = [ordered]@{
        generatedAt = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        note        = '棘轮基线：断言期望=此处记录值；实际值变大即失败，变小（改善）通过。随 B1/B2/B5 落地逐项下调，B6 收敛为 0 后本文件退役。'
        counts      = [ordered]@{}
    }
    foreach ($r in $Results) { $obj.counts[$r.Id] = $r.Actual }
    [System.IO.File]::WriteAllText($BaselinePath, ($obj | ConvertTo-Json))
    Write-Host ("基线已写入: " + $BaselinePath + "（" + $Results.Count + " 项）——确认差异后随脚本一起提交。")
    exit 0
}
$baseline = $null
if (Test-Path -LiteralPath $BaselinePath) {
    try { $baseline = (Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json).counts } catch { $baseline = $null }
    if ($baseline) { Write-Host ("棘轮基线生效: " + $BaselinePath) }
}
if ($baseline) {
    foreach ($r in $Results) {
        $prop = $baseline.PSObject.Properties[$r.Id]
        if ($prop) {
            $r.Expected = [int]$prop.Value
            $r.Ok = ($r.Actual -le $r.Expected)
        }
    }
}

Write-Host ''
$failed = 0
foreach ($r in $Results) {
    $mark = if ($r.Ok) { '[PASS]' } else { $failed++; '[FAIL]' }
    $line = "$mark $($r.Id) $($r.Title) = $($r.Actual)（期望 $($r.Expected)）"
    Write-Host $line
    if (-not $r.Ok -and $r.Detail) {
        $d = $r.Detail
        if ($d.Length -gt 400) { $d = $d.Substring(0, 400) + ' …(截断，共 ' + $r.Detail.Length + ' 字符)' }
        Write-Host ("        " + $d)
    }
}
Write-Host ''
Write-Host ("C 组断言: " + (@($Results | Where-Object { $_.Group -eq 'C' -and $_.Ok }).Count) + "/" + (@($Results | Where-Object { $_.Group -eq 'C' }).Count) + " 通过")
Write-Host ("A 组断言: " + (@($Results | Where-Object { $_.Group -eq 'A' -and $_.Ok }).Count) + "/" + (@($Results | Where-Object { $_.Group -eq 'A' }).Count) + " 通过")
Write-Host ("失败断言: " + $failed)

if ($failed -eq 0) {
    Write-Host '架构断言全部通过。'
    exit 0
}
if ($WarnOnly) {
    Write-Host '警告档：存在未通过的架构断言（不阻断 CI），须在对应 B 阶段内转绿。'
    exit 0
}
Write-Host '架构断言失败。'
exit 1
