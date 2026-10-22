#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ManifestPath = "docs/reviews/evidence/gpu-themes/nonperformance-20261022/qa/nonperformance-manifest.json",
    [string[]]$Groups,
    [string]$EvidenceRoot = "docs/reviews/evidence/gpu-themes/nonperformance-20261022/qa",
    [switch]$PlanOnly,
    [switch]$ListGroups,
    [switch]$AllowGui,
    [switch]$AllowLive,
    [switch]$RequireAll,
    [switch]$Freeze
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$script:OwnedProbeStarts = @{}

function Resolve-RepoPath {
    param([Parameter(Mandatory)][string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Assert-UnderRepo {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $repoPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must stay under the repository: $fullPath"
    }
    return $fullPath
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Write-JsonAtomic {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Value
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        [void][IO.Directory]::CreateDirectory($directory)
    }
    $temporaryPath = $Path + ".tmp"
    $json = $Value | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText($temporaryPath, $json, $utf8NoBom)
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

function Get-RelativeRepoPath {
    param([Parameter(Mandatory)][string]$Path)
    return [IO.Path]::GetRelativePath($repoRoot, $Path).Replace('\', '/')
}

function Get-WildcardRegex {
    param([Parameter(Mandatory)][string]$Pattern)

    # 按路径段构造，避免转义通配符占位符。
    # 语义对齐标准 glob：
    #   中间段的 **  -> 零层或多层目录（"a/**/b" 同时匹配 "a/b" 与 "a/x/y/b"）
    #   末段的 **   -> 任意内容，可为空，可跨目录
    #   *           -> 单层内任意字符，不含目录分隔符
    #   ?           -> 单个字符
    # 拼接规则：普通段自带尾部分隔符；** 段贡献 (?:[^/]+/)* ，可重复零次。
    $segments = @($Pattern.Replace('\', '/').Split('/'))
    $lastIndex = $segments.Count - 1
    $builder = [Text.StringBuilder]::new()
    for ($index = 0; $index -lt $segments.Count; $index++) {
        $segment = $segments[$index]
        if ($segment -eq "**") {
            if ($index -eq $lastIndex) {
                [void]$builder.Append('.*')
            }
            else {
                [void]$builder.Append('(?:[^/]+/)*')
            }
            continue
        }
        $escaped = [Regex]::Escape($segment)
        $escaped = $escaped.Replace('\*', '[^/]*').Replace('\?', '[^/]')
        [void]$builder.Append($escaped)
        if ($index -ne $lastIndex) {
            [void]$builder.Append('/')
        }
    }
    $expression = '^' + $builder.ToString() + '$'
    return [Regex]::new($expression, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
}

function Test-GlobMatch {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Pattern
    )
    return (Get-WildcardRegex $Pattern).IsMatch($RelativePath.Replace('\', '/'))
}

function Get-EntryProperty {
    param(
        [AllowNull()][object]$Entry,
        [Parameter(Mandatory)][string]$Name
    )

    if ($null -eq $Entry) {
        return $null
    }
    if ($Entry -is [hashtable]) {
        if (-not $Entry.ContainsKey($Name)) {
            return $null
        }
        return $Entry[$Name]
    }
    $property = $Entry.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-EntryList {
    param(
        [AllowNull()][object]$Entry,
        [Parameter(Mandatory)][string]$Name
    )

    $value = Get-EntryProperty -Entry $Entry -Name $Name
    if ($null -eq $value) {
        return @()
    }
    return @($value | Where-Object { $null -ne $_ })
}

function Resolve-ManualReceipt {
    <#
        .SYNOPSIS
        判定一份人工收据是否可采信。

        .DESCRIPTION
        收据必须绑定当前冻结候选（candidateId + sourceManifestHash）、
        绑定属于冻结产物集合的 artifactSha256，
        并至少给出一个带哈希的证据文件。
        证据文件路径相对仓库根，哈希必须与当前文件一致，
        因此收据无法在事后被悄悄替换。
    #>
    param(
        [Parameter(Mandatory)][object]$Entry,
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$ReceiptPath,
        [AllowNull()][object]$ArtifactMap,
        [AllowNull()][object]$ArtifactSetMap
    )

    $manual = Get-Content -LiteralPath $ReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
    $receipt = [ordered]@{
        path = Get-RelativeRepoPath $ReceiptPath
        sha256 = Get-Sha256 $ReceiptPath
    }
    $receiptArtifact = [string](Get-EntryProperty -Entry $manual -Name 'artifactSha256')
    $receiptFiles = @(Get-EntryList -Entry $manual -Name 'evidenceFiles')
    $frozenArtifactHashes = @()
    if ($null -ne $ArtifactMap) {
        $frozenArtifactHashes += @($ArtifactMap.PSObject.Properties | ForEach-Object { [string]$_.Value })
    }
    if ($null -ne $ArtifactSetMap) {
        $frozenArtifactHashes += @($ArtifactSetMap.PSObject.Properties | ForEach-Object { [string]$_.Value })
    }
    $receiptProblems = @()
    foreach ($receiptFile in $receiptFiles) {
        $relative = [string](Get-EntryProperty -Entry $receiptFile -Name 'path')
        $expected = [string](Get-EntryProperty -Entry $receiptFile -Name 'sha256')
        if ([string]::IsNullOrWhiteSpace($relative) -or [string]::IsNullOrWhiteSpace($expected)) {
            $receiptProblems += "evidenceFiles entry is missing path or sha256"
            continue
        }
        $full = Resolve-RepoPath $relative
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            $receiptProblems += "$relative (file missing)"
            continue
        }
        if ((Get-Sha256 $full) -ne $expected) {
            $receiptProblems += "$relative (sha256 mismatch)"
        }
    }

    $status = $null
    $blockingCode = $null
    $detail = $null
    if ([string](Get-EntryProperty -Entry $manual -Name 'candidateId') -ne [string]$Manifest.candidate.id -or
        [string](Get-EntryProperty -Entry $manual -Name 'sourceManifestHash') -ne [string]$Manifest.candidate.sourceManifestHash) {
        $status = "FAIL"
        $blockingCode = "VERSION_MISMATCH"
        $detail = "Manual evidence is not bound to the frozen candidate."
    }
    elseif ([string]::IsNullOrWhiteSpace($receiptArtifact) -or $receiptFiles.Count -eq 0) {
        $status = "FAIL"
        $blockingCode = "REQUIRED_MANUAL_EVIDENCE_MISSING"
        $detail = "Manual receipt must bind artifactSha256 and at least one evidence file with sha256."
    }
    elseif ($frozenArtifactHashes -notcontains $receiptArtifact) {
        $status = "FAIL"
        $blockingCode = "VERSION_MISMATCH"
        $detail = "Manual receipt artifact hash is not part of the frozen candidate artifacts."
    }
    elseif ($receiptProblems.Count -gt 0) {
        $status = "FAIL"
        $blockingCode = "EVIDENCE_ASSERTION_FAILED"
        $detail = "Manual receipt evidence files are missing or changed: $($receiptProblems -join ', ')"
    }
    elseif (@(Get-EntryList -Entry $Entry -Name 'statusesAllowed') -notcontains [string](Get-EntryProperty -Entry $manual -Name 'status')) {
        $status = "FAIL"
        $blockingCode = "EVIDENCE_ASSERTION_FAILED"
        $detail = "Manual evidence has an invalid status: $(Get-EntryProperty -Entry $manual -Name 'status')"
    }
    else {
        $status = [string]$manual.status
        $blockingCode = if ($status -eq "PASS") { $null } else { "REQUIRED_MANUAL_EVIDENCE_MISSING" }
        $detail = [string](Get-EntryProperty -Entry $manual -Name 'detail')
    }

    return [ordered]@{
        status = $status
        blockingCode = $blockingCode
        detail = $detail
        receipt = $receipt
    }
}

function Get-InputSnapshot {
    param(
        [Parameter(Mandatory)][string[]]$IncludeGlobs,
        [Parameter(Mandatory)][string[]]$ExcludeGlobs
    )

    $includeRegexes = @($IncludeGlobs | ForEach-Object { Get-WildcardRegex $_ })
    $excludeRegexes = @($ExcludeGlobs | ForEach-Object { Get-WildcardRegex $_ })
    $records = [Collections.Generic.List[object]]::new()
    foreach ($file in [IO.Directory]::EnumerateFiles($repoRoot, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = (Get-RelativeRepoPath $file)
        $normalized = $relative.Replace('\', '/')
        if (@($excludeRegexes | Where-Object { $_.IsMatch($normalized) }).Count -gt 0) {
            continue
        }
        if (@($includeRegexes | Where-Object { $_.IsMatch($normalized) }).Count -eq 0) {
            continue
        }
        $records.Add([ordered]@{
            path = $relative
            sha256 = Get-Sha256 $file
            length = ([IO.FileInfo]$file).Length
        })
    }

    $ordered = @($records | Sort-Object { $_.path })
    $builder = [Text.StringBuilder]::new()
    foreach ($record in $ordered) {
        [void]$builder.Append($record.path).Append("`0").Append($record.sha256).Append("`n")
    }
    $bytes = $utf8NoBom.GetBytes($builder.ToString())
    $hashBytes = [Security.Cryptography.SHA256]::HashData($bytes)
    return [ordered]@{
        algorithm = "SHA256(path + NUL + fileSHA256 + LF; sorted)"
        fileCount = $ordered.Count
        sha256 = [Convert]::ToHexString($hashBytes)
        files = $ordered
    }
}

function Get-SourceSnapshot {
    param([Parameter(Mandatory)][object]$Manifest)

    return Get-InputSnapshot `
        -IncludeGlobs @(Get-EntryList -Entry $Manifest.candidateInputs -Name 'includeGlobs') `
        -ExcludeGlobs @(Get-EntryList -Entry $Manifest.candidateInputs -Name 'excludeGlobs')
}

function Get-BuildInputSnapshot {
    param([Parameter(Mandatory)][object]$Manifest)

    $buildGlobs = @(Get-EntryList -Entry $Manifest.candidateInputs -Name 'buildGlobs')
    if ($buildGlobs.Count -eq 0) {
        throw "MANIFEST_INVALID: candidateInputs.buildGlobs must list the real build inputs."
    }
    return Get-InputSnapshot `
        -IncludeGlobs $buildGlobs `
        -ExcludeGlobs @(Get-EntryList -Entry $Manifest.candidateInputs -Name 'excludeGlobs')
}

function Get-ManifestCanonicalSha256 {
    param([Parameter(Mandatory)][string]$Path)

    # 规范化哈希：先把 candidate.qaManifestSha256 置空再序列化，
    # 让该字段本身不参与哈希。否则字段值永远等于不了包含自己的文件哈希，
    # 候选就无法按正常流程冻结。
    $document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
    if ($null -ne $document -and
        $document.PSObject.Properties.Name -contains "candidate" -and
        $document.candidate -is [pscustomobject] -and
        $document.candidate.PSObject.Properties.Name -contains "qaManifestSha256") {
        $document.candidate.qaManifestSha256 = $null
    }
    $json = $document | ConvertTo-Json -Depth 100 -Compress
    $bytes = $utf8NoBom.GetBytes($json)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Test-Manifest {
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$ResolvedManifestPath
    )

    $errors = [Collections.Generic.List[object]]::new()
    if ($Manifest.schemaVersion -ne 1) {
        $errors.Add([ordered]@{ code = "MANIFEST_INVALID"; detail = "schemaVersion must be 1." })
    }
    if ([string]::IsNullOrWhiteSpace($Manifest.id)) {
        $errors.Add([ordered]@{ code = "MANIFEST_INVALID"; detail = "Manifest id is required." })
    }

    $entries = @($Manifest.entries)
    $requirements = @($Manifest.requirements)
    if ($entries.Count -eq 0 -or $requirements.Count -eq 0) {
        $errors.Add([ordered]@{ code = "MANIFEST_INVALID"; detail = "Manifest must contain entries and requirements." })
    }

    $entryIds = @($entries | ForEach-Object { [string]$_.id })
    $duplicateEntryIds = @($entryIds | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
    if ($duplicateEntryIds.Count -gt 0) {
        $errors.Add([ordered]@{
            code = "MANIFEST_INVALID"
            detail = "Duplicate entry ids: $($duplicateEntryIds -join ', ')"
        })
    }
    $entryIdSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entryId in $entryIds) { [void]$entryIdSet.Add($entryId) }

    $allowedGroups = @($Manifest.executionPolicy.allowedGroups)
    foreach ($entry in $entries) {
        if ($allowedGroups -notcontains $entry.group) {
            $errors.Add([ordered]@{
                code = "MANIFEST_INVALID"
                detail = "Entry '$($entry.id)' uses unknown group '$($entry.group)'."
            })
        }
        if ($entry.kind -in @("dotnet-test", "dotnet-exe")) {
            $projectPath = Resolve-RepoPath ([string]$entry.project)
            if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
                $errors.Add([ordered]@{
                    code = "PROJECT_MISSING"
                    entryId = $entry.id
                    detail = "Project does not exist: $($entry.project)"
                })
            }
        }
        if ($entry.kind -notin @("dotnet-test", "dotnet-exe", "manual")) {
            $errors.Add([ordered]@{
                code = "MANIFEST_INVALID"
                detail = "Entry '$($entry.id)' has unsupported kind '$($entry.kind)'."
            })
        }
    }

    $useCaseCount = 0
    foreach ($requirement in $requirements) {
        foreach ($useCase in @($requirement.useCases)) {
            $useCaseCount++
            $refs = @(Get-EntryList -Entry $useCase -Name 'requiredEntries')
            if ($refs.Count -eq 0) {
                $refs = @(Get-EntryList -Entry $useCase -Name 'entries')
            }
            $refs += @(Get-EntryList -Entry $useCase -Name 'optionalEntries')
            if ($useCase.required -and $refs.Count -eq 0) {
                $errors.Add([ordered]@{
                    code = "MANIFEST_INVALID"
                    detail = "Required use case '$($useCase.id)' has no entry mapping."
                })
            }
            foreach ($entryId in $refs) {
                if (-not $entryIdSet.Contains([string]$entryId)) {
                    $errors.Add([ordered]@{
                        code = "MANIFEST_INVALID"
                        detail = "Use case '$($useCase.id)' references missing entry '$entryId'."
                    })
                }
            }
        }
    }

    if ($Manifest.executionPolicy.allowPerformance -ne $false) {
        $errors.Add([ordered]@{
            code = "MANIFEST_INVALID"
            detail = "Non-performance runner must not allow performance execution."
        })
    }

    $defaultGroups = @($Manifest.executionPolicy.defaultGroups)
    foreach ($group in $defaultGroups) {
        if ($allowedGroups -notcontains $group) {
            $errors.Add([ordered]@{
                code = "MANIFEST_INVALID"
                detail = "Default group '$group' is not in allowedGroups."
            })
        }
    }
    if ($defaultGroups -contains "gui-fixed" -or $defaultGroups -contains "live") {
        $errors.Add([ordered]@{
            code = "MANIFEST_INVALID"
            detail = "Default groups must not include GUI or live capture."
        })
    }

    return [ordered]@{
        errors = @($errors)
        entryCount = $entries.Count
        requirementCount = $requirements.Count
        useCaseCount = $useCaseCount
        manifestSha256 = Get-Sha256 $ResolvedManifestPath
        canonicalSha256 = Get-ManifestCanonicalSha256 $ResolvedManifestPath
    }
}

function Get-JsonPathValue {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Path
    )

    $current = $Value
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $current) {
            return $null
        }
        $property = $current.PSObject.Properties |
            Where-Object { $_.Name -eq $segment } |
            Select-Object -First 1
        if ($null -eq $property) {
            return $null
        }
        $current = $property.Value
    }
    return $current
}

function Test-JsonCheck {
    param(
        [AllowNull()][object]$Document,
        [Parameter(Mandatory)][object]$Check
    )

    $actual = Get-JsonPathValue -Value $Document -Path ([string]$Check.path)
    if ($Check.PSObject.Properties.Name -contains "exists") {
        return (($null -ne $actual) -eq [bool]$Check.exists)
    }
    if ($null -eq $actual) {
        return $false
    }
    if ($Check.PSObject.Properties.Name -contains "equals") {
        return ([string]$actual -eq [string]$Check.equals)
    }
    if ($Check.PSObject.Properties.Name -contains "minimum") {
        return ([double]$actual -ge [double]$Check.minimum)
    }
    if ($Check.PSObject.Properties.Name -contains "maximum") {
        return ([double]$actual -le [double]$Check.maximum)
    }
    if ($Check.PSObject.Properties.Name -contains "lengthEquals") {
        return (@($actual).Count -eq [int]$Check.lengthEquals)
    }
    return $false
}

function Get-ProbeProcesses {
    param([Parameter(Mandatory)][object]$Manifest)
    $names = @($Manifest.executionPolicy.ownProcessNames)
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName })
}

function Stop-OwnedProbeProcesses {
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.HashSet[int]]$PreExistingPids
    )

    $residue = @(Get-ProbeProcesses $Manifest | Where-Object {
        $script:OwnedProbeStarts.ContainsKey($_.Id) -and
        $_.StartTime.ToUniversalTime().Ticks -eq $script:OwnedProbeStarts[$_.Id]
    })
    foreach ($process in $residue) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        catch {
        }
    }
    return $residue
}

function Invoke-ProcessWithTimeout {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [string]$AuditScript,
        [string]$AuditOutputPath
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($startInfo)
    $script:OwnedProbeStarts[$process.Id] = $process.StartTime.ToUniversalTime().Ticks
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $auditResult = $null

    if (-not [string]::IsNullOrWhiteSpace($AuditScript) -and
        -not [string]::IsNullOrWhiteSpace($AuditOutputPath)) {
        Start-Sleep -Milliseconds 750
        if (-not $process.HasExited) {
            $auditResult = Invoke-ProcessWithTimeout `
                -FilePath (Get-Process -Id $PID).Path `
                -Arguments @(
                    "-NoProfile",
                    "-ExecutionPolicy", "Bypass",
                    "-File", $AuditScript,
                    "-TargetProcessId", [string]$process.Id,
                    "-OutputPath", $AuditOutputPath
                ) `
                -WorkingDirectory $repoRoot `
                -TimeoutSeconds 20
        }
    }

    $remainingMilliseconds = [Math]::Max(0, ($TimeoutSeconds * 1000) - $watch.ElapsedMilliseconds)
    $exited = $process.WaitForExit($remainingMilliseconds)
    $timedOut = -not $exited
    if ($timedOut) {
        try {
            $process.Kill($true)
        }
        catch {
        }
        [void]$process.WaitForExit(5000)
    }
    else {
        $process.WaitForExit()
    }
    $watch.Stop()

    $stdout = if ($stdoutTask.Wait(2000)) { $stdoutTask.GetAwaiter().GetResult() } else { "Output stream did not close." }
    $stderr = if ($stderrTask.Wait(2000)) { $stderrTask.GetAwaiter().GetResult() } else { "Error stream did not close." }
    if (-not $stdoutTask.IsCompleted -or -not $stderrTask.IsCompleted) { $timedOut = $true }
    $exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
    $process.Dispose()

    return [ordered]@{
        exitCode = $exitCode
        timedOut = $timedOut
        durationMilliseconds = $watch.ElapsedMilliseconds
        stdout = $stdout
        stderr = $stderr
        audit = $auditResult
    }
}

function Expand-Arguments {
    param(
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$EntryEvidence
    )

    return @(
        foreach ($argument in $Arguments) {
            ([string]$argument).
                Replace('${EVIDENCE}', $EntryEvidence, [StringComparison]::Ordinal).
                Replace('${REPO_ROOT}', $repoRoot, [StringComparison]::Ordinal)
        }
    )
}

function Get-EntryArtifactPath {
    param([Parameter(Mandatory)][object]$Entry)
    if ($Entry.kind -eq "dotnet-exe") {
        return Resolve-RepoPath ([string]$Entry.artifact)
    }
    if ($Entry.kind -eq "dotnet-test") {
        $projectPath = Resolve-RepoPath ([string]$Entry.project)
        $assemblyName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
        return Join-Path (Split-Path -Parent $projectPath) "bin\Release\net8.0-windows\$assemblyName.dll"
    }
    return $null
}

function Get-ArtifactSetHash {
    param(
        [Parameter(Mandatory)][string]$ArtifactPath,
        [string[]]$Globs = $null
    )

    # 只校验 apphost .exe 是不够的：.NET 真正的逻辑在旁边的 probe dll、LiteMonitor++.dll 及其资源里。
    # 默认覆盖产物目录里的全部 exe 与 dll；json/pdb 等运行期或调试产物默认不计入。
    $directory = Split-Path -Parent $ArtifactPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        return [ordered]@{ sha256 = $null; files = @() }
    }
    $patterns = if ($null -eq $Globs -or $Globs.Count -eq 0) { @("*.exe", "*.dll") } else { $Globs }
    $records = [Collections.Generic.List[string]]::new()
    $details = [Collections.Generic.List[object]]::new()
    foreach ($file in [IO.Directory]::EnumerateFiles($directory, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = [IO.Path]::GetRelativePath($directory, $file).Replace('\', '/')
        $matched = $false
        foreach ($pattern in $patterns) {
            if (Test-GlobMatch -RelativePath $relative -Pattern $pattern) {
                $matched = $true
                break
            }
        }
        if (-not $matched) {
            continue
        }
        $hash = Get-Sha256 $file
        $records.Add("$relative`0$hash")
        $details.Add([ordered]@{
            path = $relative
            sha256 = $hash
            length = ([IO.FileInfo]$file).Length
        })
    }
    $ordered = @($records | Sort-Object)
    if ($ordered.Count -eq 0) {
        return [ordered]@{ sha256 = $null; files = @() }
    }
    $builder = [Text.StringBuilder]::new()
    foreach ($record in $ordered) {
        [void]$builder.Append($record).Append("`n")
    }
    $bytes = $utf8NoBom.GetBytes($builder.ToString())
    return [ordered]@{
        sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        files = @($details | Sort-Object { $_.path })
    }
}

function Invoke-EntryBuild {
    param(
        [Parameter(Mandatory)][object]$Entry,
        [Parameter(Mandatory)][hashtable]$Cache,
        [Parameter(Mandatory)][string]$BuildRoot,
        [Parameter(Mandatory)][object]$Manifest
    )

    $projectPath = Resolve-RepoPath ([string]$Entry.project)
    $cacheKey = $projectPath.ToLowerInvariant()
    if ($Cache.ContainsKey($cacheKey)) {
        return $Cache[$cacheKey]
    }

    $safeName = ([string]$Entry.id -replace '[^A-Za-z0-9._-]', '_')
    $logPath = Join-Path $BuildRoot "$safeName.build.log"
    $buildStart = [DateTime]::UtcNow

    # 必须先 restore：--no-restore 会沿用旧的 project.assets.json，
    # 依赖解析过期时会编译出与当前源码不一致的产物（例如 CS1705 程序集版本冲突）。
    $restoreResult = Invoke-ProcessWithTimeout `
        -FilePath (Get-Command dotnet -ErrorAction Stop).Source `
        -Arguments @(
            "restore", $projectPath, "--nologo", "-v:minimal"
        ) `
        -WorkingDirectory $repoRoot `
        -TimeoutSeconds 600
    if ($restoreResult.timedOut -or $restoreResult.exitCode -ne 0) {
        $restoreLogPath = Join-Path $BuildRoot "$safeName.restore.log"
        [IO.File]::WriteAllText(
            $restoreLogPath,
            $restoreResult.stdout + "`n--- STDERR ---`n" + $restoreResult.stderr,
            $utf8NoBom)
        return [ordered]@{
            exitCode = $restoreResult.exitCode
            timedOut = $restoreResult.timedOut
            durationMilliseconds = $restoreResult.durationMilliseconds
            command = "dotnet restore `"$projectPath`""
            log = Get-RelativeRepoPath $restoreLogPath
            restoreOnly = $true
            artifactPath = $null
            artifactLength = $null
            artifactLastWriteUtc = $null
            artifactSha256 = $null
            expectedArtifactSha256 = $null
            artifactSetSha256 = $null
            expectedArtifactSetSha256 = $null
            artifactSetFiles = @()
            fresh = $false
        }
    }

    $result = Invoke-ProcessWithTimeout `
        -FilePath (Get-Command dotnet -ErrorAction Stop).Source `
        -Arguments @(
            "build", $projectPath, "-c", "Release", "--no-restore", "-t:Rebuild",
            "--nologo", "-v:minimal"
        ) `
        -WorkingDirectory $repoRoot `
        -TimeoutSeconds 600

    [IO.File]::WriteAllText(
        $logPath,
        $result.stdout + "`n--- STDERR ---`n" + $result.stderr,
        $utf8NoBom)

    $artifactPath = Get-EntryArtifactPath $Entry
    $artifactInfo = $null
    $artifactHash = $null
    $fresh = $false
    if (Test-Path -LiteralPath $artifactPath -PathType Leaf) {
        $artifactInfo = Get-Item -LiteralPath $artifactPath
        $artifactHash = Get-Sha256 $artifactPath
        $fresh = $artifactInfo.LastWriteTimeUtc -ge $buildStart.AddSeconds(-1)
    }

    $expectedHash = $null
    $artifactMap = Get-EntryProperty -Entry $Manifest.candidate -Name 'artifactSha256'
    if ($null -ne $artifactMap) {
        $expectedProperty = $artifactMap.PSObject.Properties[$Entry.id]
        if ($null -ne $expectedProperty) {
            $expectedHash = [string]$expectedProperty.Value
        }
    }
    $expectedSetHash = $null
    $artifactSetMap = Get-EntryProperty -Entry $Manifest.candidate -Name 'artifactSetSha256'
    if ($null -ne $artifactSetMap) {
        $expectedSetProperty = $artifactSetMap.PSObject.Properties[$Entry.id]
        if ($null -ne $expectedSetProperty) {
            $expectedSetHash = [string]$expectedSetProperty.Value
        }
    }

    $artifactSet = $null
    $artifactSetHash = $null
    $artifactSetFiles = @()
    if ($artifactPath -and (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        $artifactSet = Get-ArtifactSetHash `
            -ArtifactPath $artifactPath `
            -Globs @(Get-EntryList -Entry $Entry -Name 'artifactFileGlobs')
        $artifactSetHash = $artifactSet.sha256
        $artifactSetFiles = $artifactSet.files
    }

    $buildRecord = [ordered]@{
        exitCode = $result.exitCode
        timedOut = $result.timedOut
        durationMilliseconds = $result.durationMilliseconds
        command = "dotnet build `"$projectPath`" -c Release --no-restore -t:Rebuild --nologo -v:minimal"
        restoreCommand = "dotnet restore `"$projectPath`""
        restoreExitCode = $restoreResult.exitCode
        restoreDurationMilliseconds = $restoreResult.durationMilliseconds
        log = Get-RelativeRepoPath $logPath
        artifactPath = if ($artifactPath) { Get-RelativeRepoPath $artifactPath } else { $null }
        artifactLength = if ($artifactInfo) { $artifactInfo.Length } else { $null }
        artifactLastWriteUtc = if ($artifactInfo) { $artifactInfo.LastWriteTimeUtc.ToString('o') } else { $null }
        artifactSha256 = $artifactHash
        expectedArtifactSha256 = $expectedHash
        artifactSetSha256 = $artifactSetHash
        expectedArtifactSetSha256 = $expectedSetHash
        artifactSetFiles = $artifactSetFiles
        fresh = $fresh
    }
    $Cache[$cacheKey] = $buildRecord
    return $buildRecord
}

function Test-TestRunEvidence {
    param(
        [Parameter(Mandatory)][string]$EvidenceDirectory,
        [Parameter(Mandatory)][DateTime]$RunStartedUtc
    )

    $trxFiles = @(
        Get-ChildItem -LiteralPath $EvidenceDirectory -Filter *.trx -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -ge $RunStartedUtc.AddSeconds(-1) } |
            Sort-Object LastWriteTimeUtc -Descending
    )
    if ($trxFiles.Count -eq 0) {
        return [ordered]@{
            passed = $false
            code = "EVIDENCE_MISSING"
            detail = "No fresh TRX file was produced."
            total = 0
            executed = 0
            passedCount = 0
            failed = 0
            skipped = 0
            trx = $null
        }
    }

    $trx = $trxFiles[0]
    [xml]$document = [IO.File]::ReadAllText($trx.FullName, $utf8NoBom)
    $counters = $document.TestRun.ResultSummary.Counters
    $total = if ($counters.total) { [int]$counters.total } else { 0 }
    $executed = if ($counters.executed) { [int]$counters.executed } else { 0 }
    $passedCount = if ($counters.passed) { [int]$counters.passed } else { 0 }
    $failed = if ($counters.failed) { [int]$counters.failed } else { 0 }
    $notExecuted = if ($counters.notExecuted) { [int]$counters.notExecuted } else { 0 }
    $resultNodes = @($document.SelectNodes("//*[local-name()='UnitTestResult']"))
    $skippedNodes = @($resultNodes | Where-Object {
        $_.outcome -in @("NotExecuted", "Skipped", "Inconclusive")
    })
    $skipped = [Math]::Max($notExecuted, $skippedNodes.Count)

    if ($total -eq 0 -or $executed -eq 0) {
        return [ordered]@{
            passed = $false
            code = "ZERO_TESTS"
            detail = "The filtered test run executed zero tests."
            total = $total
            executed = $executed
            passedCount = $passedCount
            failed = $failed
            skipped = $skipped
            trx = Get-RelativeRepoPath $trx.FullName
        }
    }
    if ($failed -gt 0) {
        return [ordered]@{
            passed = $false
            code = "NONZERO_EXIT"
            detail = "$failed test(s) failed."
            total = $total
            executed = $executed
            passedCount = $passedCount
            failed = $failed
            skipped = $skipped
            trx = Get-RelativeRepoPath $trx.FullName
        }
    }
    if ($skipped -gt 0) {
        return [ordered]@{
            passed = $false
            code = "SKIP"
            detail = "$skipped test(s) were skipped or not executed."
            total = $total
            executed = $executed
            passedCount = $passedCount
            failed = $failed
            skipped = $skipped
            trx = Get-RelativeRepoPath $trx.FullName
        }
    }
    if ($passedCount -lt 1) {
        return [ordered]@{
            passed = $false
            code = "ZERO_TESTS"
            detail = "No test was recorded as passed."
            total = $total
            executed = $executed
            passedCount = $passedCount
            failed = $failed
            skipped = $skipped
            trx = Get-RelativeRepoPath $trx.FullName
        }
    }

    return [ordered]@{
        passed = $true
        code = $null
        detail = "$passedCount of $executed executed tests passed."
        total = $total
        executed = $executed
        passedCount = $passedCount
        failed = $failed
        skipped = $skipped
        trx = Get-RelativeRepoPath $trx.FullName
    }
}

function Test-EntryEvidence {
    param(
        [Parameter(Mandatory)][object]$Entry,
        [Parameter(Mandatory)][string]$EntryEvidence,
        [Parameter(Mandatory)][DateTime]$RunStartedUtc,
        [AllowNull()][string]$Stdout,
        [AllowNull()][object]$AuditResult
    )

    $issues = [Collections.Generic.List[object]]::new()
    $jsonFiles = [Collections.Generic.List[object]]::new()
    $allEvidenceFiles = [Collections.Generic.List[object]]::new()
    $caseCount = 0
    $jsonCheckCount = 0
    $stdoutCheckCount = 0
    $performanceExecuted = $false
    $performanceEvidence = [Collections.Generic.List[object]]::new()

    foreach ($evidenceDefinition in @(Get-EntryList -Entry $Entry -Name 'evidence')) {
        $matches = @(
            Get-ChildItem `
                -Path (Join-Path $EntryEvidence ([string]$evidenceDefinition.glob)) `
                -File `
                -ErrorAction SilentlyContinue
        )
        if ($matches.Count -eq 0) {
            $issues.Add([ordered]@{
                code = "EVIDENCE_MISSING"
                detail = "Required evidence pattern matched no file: $($evidenceDefinition.glob)"
            })
            continue
        }

        foreach ($match in $matches) {
            $allEvidenceFiles.Add($match)
            if ($match.LastWriteTimeUtc -lt $RunStartedUtc.AddSeconds(-1)) {
                $issues.Add([ordered]@{
                    code = "EVIDENCE_STALE"
                    detail = "Evidence is older than this run: $($match.FullName)"
                })
            }
            if ($evidenceDefinition.PSObject.Properties.Name -contains "minimumBytes" -and
                $match.Length -lt [long]$evidenceDefinition.minimumBytes) {
                $issues.Add([ordered]@{
                    code = "EVIDENCE_ASSERTION_FAILED"
                    detail = "Evidence is smaller than $($evidenceDefinition.minimumBytes) bytes: $($match.FullName)"
                })
            }

            if ($match.Extension -ne ".json") {
                continue
            }
            try {
                $document = [IO.File]::ReadAllText($match.FullName, $utf8NoBom) | ConvertFrom-Json -Depth 100
            }
            catch {
                $issues.Add([ordered]@{
                    code = "EVIDENCE_ASSERTION_FAILED"
                    detail = "Evidence is not valid JSON: $($match.FullName)"
                })
                continue
            }
            $jsonFiles.Add([ordered]@{ path = $match.FullName; document = $document })
            foreach ($check in @(Get-EntryList -Entry $evidenceDefinition -Name 'jsonChecks')) {
                $jsonCheckCount++
                if (-not (Test-JsonCheck -Document $document -Check $check)) {
                    $issues.Add([ordered]@{
                        code = "EVIDENCE_ASSERTION_FAILED"
                        detail = "JSON check failed in $(Get-RelativeRepoPath $match.FullName): $($check.path)"
                    })
                }
            }

            # 性能执行状态必须由证据自己声明，不能由 runner 硬编码。
            $declaredPerformance = Get-JsonPathValue -Value $document -Path "performanceExecuted"
            if ($null -ne $declaredPerformance) {
                if ([bool]$declaredPerformance) {
                    $performanceExecuted = $true
                    $performanceEvidence.Add([ordered]@{
                        path = Get-RelativeRepoPath $match.FullName
                        detail = "证据声明 performanceExecuted=true"
                    })
                }
            }
        }
    }

    foreach ($jsonFile in $jsonFiles) {
        foreach ($field in @("caseCount", "total")) {
            $value = Get-JsonPathValue -Value $jsonFile.document -Path $field
            if ($null -ne $value) {
                $caseCount += [int]$value
                break
            }
        }
    }
    if ($caseCount -eq 0 -and -not [string]::IsNullOrWhiteSpace($Stdout)) {
        $caseCount = @(
            $Stdout -split "`r?`n" |
                Where-Object { $_ -match '\[(PASS|通过)\]' }
        ).Count
    }
    foreach ($pattern in @(Get-EntryList -Entry $Entry -Name 'stdoutRegex')) {
        $stdoutCheckCount++
        if ([string]::IsNullOrWhiteSpace($Stdout) -or
            -not [Regex]::IsMatch($Stdout, [string]$pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $issues.Add([ordered]@{
                code = "EVIDENCE_ASSERTION_FAILED"
                detail = "stdout did not match required pattern: $pattern"
            })
        }
    }
    if ($caseCount -lt $jsonCheckCount) {
        $caseCount = $jsonCheckCount
    }
    if ($caseCount -lt $stdoutCheckCount) {
        $caseCount = $stdoutCheckCount
    }

    $requiredCaseCount = if ($Entry.PSObject.Properties.Name -contains "minimumCaseCount") {
        [int]$Entry.minimumCaseCount
    }
    else {
        0
    }
    if ($requiredCaseCount -gt 0 -and $caseCount -lt $requiredCaseCount) {
        $issues.Add([ordered]@{
            code = "ZERO_TESTS"
            detail = "Evidence assertion count $caseCount is below required $requiredCaseCount."
        })
    }
    if ($Entry.PSObject.Properties.Name -contains "windowAuditRequired" -and
        [bool]$Entry.windowAuditRequired) {
        if ($null -eq $AuditResult -or $null -eq $AuditResult.exitCode -or
            [int]$AuditResult.exitCode -ne 0) {
            $issues.Add([ordered]@{
                code = "EVIDENCE_MISSING"
                detail = "External window audit was not produced."
            })
        }
        else {
            $auditPath = Join-Path $EntryEvidence "window-policy-audit.json"
            if (-not (Test-Path -LiteralPath $auditPath -PathType Leaf)) {
                $issues.Add([ordered]@{
                    code = "EVIDENCE_MISSING"
                    detail = "External window audit JSON is missing."
                })
            }
            else {
                try {
                    $audit = Get-Content -LiteralPath $auditPath -Raw -Encoding UTF8 |
                        ConvertFrom-Json -Depth 30
                    $ownsForeground = Get-JsonPathValue -Value $audit -Path "targetOwnsForeground"
                    $notTopMost = Get-JsonPathValue -Value $audit -Path "allVisibleWindowsNotTopMost"
                    if ($ownsForeground -ne $false -or $notTopMost -ne $true) {
                        $issues.Add([ordered]@{
                            code = "EVIDENCE_ASSERTION_FAILED"
                            detail = "External window audit reported foreground ownership or a topmost window."
                        })
                    }
                }
                catch {
                    $issues.Add([ordered]@{
                        code = "EVIDENCE_ASSERTION_FAILED"
                        detail = "External window audit JSON could not be parsed."
                    })
                }
            }
        }
    }

    return [ordered]@{
        issues = @($issues)
        caseCount = $caseCount
        performanceExecuted = $performanceExecuted
        performanceEvidence = @($performanceEvidence)
        evidenceFiles = @(
            $allEvidenceFiles |
                Sort-Object FullName -Unique |
                ForEach-Object {
                    [ordered]@{
                        path = Get-RelativeRepoPath $_.FullName
                        sha256 = Get-Sha256 $_.FullName
                        length = $_.Length
                        lastWriteUtc = $_.LastWriteTimeUtc.ToString('o')
                    }
                }
        )
    }
}

function Get-EntryCommands {
    param(
        [Parameter(Mandatory)][object]$Entry,
        [Parameter(Mandatory)][string]$EntryEvidence
    )

    # manual 入口没有 project/artifact，必须先分流，
    # 否则在计划构造阶段就会因访问不存在的属性而抛错。
    if ($Entry.kind -eq "manual") {
        return @("Manual evidence required: $(Get-EntryProperty -Entry $Entry -Name 'evidence')")
    }

    $build = "dotnet build `"$(Resolve-RepoPath ([string]$Entry.project))`" -c Release --no-restore -t:Rebuild --nologo -v:minimal"
    if ($Entry.kind -eq "dotnet-test") {
        $test = "dotnet test `"$(Resolve-RepoPath ([string]$Entry.project))`" -c Release --no-build --no-restore"
        $filter = [string](Get-EntryProperty -Entry $Entry -Name 'filter')
        if (-not [string]::IsNullOrWhiteSpace($filter)) {
            $test += " --filter `"$filter`""
        }
        return @($build, "$test --results-directory `"$EntryEvidence`" --logger `"trx;LogFileName=results.trx`"")
    }
    if ($Entry.kind -eq "dotnet-exe") {
        $arguments = Expand-Arguments -Arguments @(Get-EntryList -Entry $Entry -Name 'arguments') -EntryEvidence $EntryEvidence
        $quoted = $arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }
        return @(
            $build,
            ("`"$(Resolve-RepoPath ([string]$Entry.artifact))`" " + ($quoted -join ' ')).Trim()
        )
    }
    return @("Manual evidence required: $(Get-EntryProperty -Entry $Entry -Name 'evidence')")
}

$resolvedManifestPath = Assert-UnderRepo -Path (Resolve-RepoPath $ManifestPath) -Label "ManifestPath"
if (-not (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf)) {
    throw "Manifest does not exist: $resolvedManifestPath"
}
$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
$manifestValidation = Test-Manifest -Manifest $manifest -ResolvedManifestPath $resolvedManifestPath
$allowedGroups = @($manifest.executionPolicy.allowedGroups)

$requestedGroups = @(
    if ($Groups -and $Groups.Count -gt 0) {
        $Groups |
            ForEach-Object { [string]$_ -split ',' } |
            ForEach-Object { $_.Trim().ToLowerInvariant() } |
            Where-Object { $_ } |
            Select-Object -Unique
    }
    else {
        @($manifest.executionPolicy.defaultGroups)
    }
)
$unknownGroups = @($requestedGroups | Where-Object { $allowedGroups -notcontains $_ })
if ($unknownGroups.Count -gt 0) {
    throw "Unknown group(s): $($unknownGroups -join ', '). Use -ListGroups."
}

if ($ListGroups) {
    [ordered]@{
        manifest = Get-RelativeRepoPath $resolvedManifestPath
        candidateStatus = $manifest.candidate.status
        defaultGroups = @($manifest.executionPolicy.defaultGroups)
        groups = @(
            foreach ($group in $allowedGroups) {
                [ordered]@{
                    id = $group
                    entryCount = @($manifest.entries | Where-Object group -eq $group).Count
                    default = @($manifest.executionPolicy.defaultGroups) -contains $group
                    requiresGui = @($manifest.entries | Where-Object {
                        $_.group -eq $group -and [bool]$_.requiresGui
                    }).Count -gt 0
                    requiresLive = @($manifest.entries | Where-Object {
                        $_.group -eq $group -and [bool]$_.requiresLive
                    }).Count -gt 0
                }
            }
        )
        deferred = @($manifest.deferred)
        validationErrors = @($manifestValidation.errors)
    } | ConvertTo-Json -Depth 12
    exit $(if ($manifestValidation.errors.Count -eq 0) { 0 } else { 1 })
}

$sourceSnapshot = Get-SourceSnapshot $manifest
$buildSnapshot = Get-BuildInputSnapshot $manifest
$runId = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ") + "-" + $PID
$resolvedEvidenceRoot = Assert-UnderRepo -Path (Resolve-RepoPath $EvidenceRoot) -Label "EvidenceRoot"
$runDirectory = Join-Path $resolvedEvidenceRoot "runs\$runId"
[void][IO.Directory]::CreateDirectory($runDirectory)
$buildRoot = Join-Path $runDirectory "build"
[void][IO.Directory]::CreateDirectory($buildRoot)

$selectedEntries = @($manifest.entries | Where-Object { $requestedGroups -contains $_.group })
$plan = [ordered]@{
    schemaVersion = 1
    task = "gpu-nonperformance-aggregate-runner"
    mode = if ($PlanOnly) { "plan" } else { "run" }
    generatedAt = (Get-Date).ToString("o")
    repository = $repoRoot
    manifest = Get-RelativeRepoPath $resolvedManifestPath
    manifestSha256 = $manifestValidation.manifestSha256
    manifestCanonicalSha256 = $manifestValidation.canonicalSha256
    manifestValidation = $manifestValidation
    candidate = [ordered]@{
        id = $manifest.candidate.id
        status = $manifest.candidate.status
        baselineCommit = $manifest.candidate.baselineCommit
        frozenSourceManifestHash = $manifest.candidate.sourceManifestHash
        currentSourceManifestHash = $sourceSnapshot.sha256
        sourceFileCount = $sourceSnapshot.fileCount
        frozenBuildInputHash = $manifest.candidate.buildInputHash
        currentBuildInputHash = $buildSnapshot.sha256
        buildInputFileCount = $buildSnapshot.fileCount
    }
    requestedGroups = @($requestedGroups)
    allowGui = [bool]$AllowGui
    allowLive = [bool]$AllowLive
    requireAll = [bool]$RequireAll
    deferred = @($manifest.deferred)
    entries = @(
        foreach ($entry in $selectedEntries) {
            $entryEvidence = Join-Path $runDirectory ("evidence\" + ([string]$entry.id -replace '[^A-Za-z0-9._-]', '_'))
            [ordered]@{
                id = $entry.id
                title = $entry.title
                kind = $entry.kind
                group = $entry.group
                requiresGui = [bool]$entry.requiresGui
                requiresLive = [bool]$entry.requiresLive
                evidenceDirectory = Get-RelativeRepoPath $entryEvidence
                commands = @(Get-EntryCommands -Entry $entry -EntryEvidence $entryEvidence)
            }
        }
    )
}

if ($PlanOnly) {
    Write-JsonAtomic -Path (Join-Path $runDirectory "plan.json") -Value $plan
    $plan | ConvertTo-Json -Depth 16
    Write-Host "Plan evidence: $runDirectory"
    exit $(if ($manifestValidation.errors.Count -eq 0) { 0 } else { 1 })
}

# 冻结候选：冻结端与校验端必须使用同一套哈希算法，所以冻结逻辑放在同一个脚本里。
# 写入顺序很重要：先写其它 candidate 字段，最后写 qaManifestSha256 ——
# 规范化哈希会把该字段置空后再算，所以这一步写入不会改变它自己。
if ($Freeze) {
    if ($manifestValidation.errors.Count -gt 0) {
        $manifestValidation | ConvertTo-Json -Depth 12
        exit 1
    }
    $freezeBuildRoot = Join-Path $runDirectory "freeze-build"
    [void][IO.Directory]::CreateDirectory($freezeBuildRoot)
    $freezeCache = @{}
    $artifactHashes = [ordered]@{}
    $artifactSetHashes = [ordered]@{}
    $frozenEntries = [Collections.Generic.List[object]]::new()
    foreach ($entry in @($manifest.entries)) {
        if ($entry.kind -eq "manual") {
            continue
        }
        $record = Invoke-EntryBuild `
            -Entry $entry `
            -Cache $freezeCache `
            -BuildRoot $freezeBuildRoot `
            -Manifest $manifest
        if ($record.timedOut -or $record.exitCode -ne 0 -or -not $record.fresh) {
            Write-Error "FREEZE_FAILED: build did not produce a fresh artifact for '$($entry.id)' (exit=$($record.exitCode) timedOut=$($record.timedOut))." -ErrorAction Continue
            exit 1
        }
        if ([string]::IsNullOrWhiteSpace([string]$record.artifactSha256) -or
            [string]::IsNullOrWhiteSpace([string]$record.artifactSetSha256)) {
            Write-Error "FREEZE_FAILED: artifact hashes are missing for '$($entry.id)'." -ErrorAction Continue
            exit 1
        }
        $artifactHashes[[string]$entry.id] = $record.artifactSha256
        $artifactSetHashes[[string]$entry.id] = $record.artifactSetSha256
        $frozenEntries.Add([ordered]@{
            id = $entry.id
            artifact = $record.artifactPath
            artifactSha256 = $record.artifactSha256
            artifactSetSha256 = $record.artifactSetSha256
            artifactFileCount = @($record.artifactSetFiles).Count
        })
    }

    $manifest.candidate.id = "frozen-" + (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
    $manifest.candidate.status = "frozen"
    $manifest.candidate.sourceManifestHash = $sourceSnapshot.sha256
    $manifest.candidate.buildInputHash = $buildSnapshot.sha256
    $manifest.candidate.artifactSha256 = $artifactHashes
    $manifest.candidate.artifactSetSha256 = $artifactSetHashes
    $manifest.candidate.frozenAt = (Get-Date).ToString("o")
    $manifest.candidate.frozenBy = "scripts/run-gpu-nonperformance.ps1 -Freeze"
    $manifest.candidate.qaManifestSha256Algorithm = "canonical-json-sha256-with-candidate.qaManifestSha256-nulled"

    # 先把收据以外的字段写回，再基于写回后的内容算规范化哈希 ——
    # 规范化哈希会把 candidate.qaManifestSha256 置空后计算，所以它对自身是稳定的，
    # 但对其余字段的取值敏感，必须在最终内容落盘之后再算。
    [IO.File]::WriteAllText(
        $resolvedManifestPath,
        ($manifest | ConvertTo-Json -Depth 100),
        $utf8NoBom)

    $canonical = Get-ManifestCanonicalSha256 $resolvedManifestPath
    $manifest.candidate.qaManifestSha256 = $canonical
    [IO.File]::WriteAllText(
        $resolvedManifestPath,
        ($manifest | ConvertTo-Json -Depth 100),
        $utf8NoBom)

    $verifyCanonical = Get-ManifestCanonicalSha256 $resolvedManifestPath
    if ($verifyCanonical -ne $canonical) {
        Write-Error "FREEZE_FAILED: canonical manifest hash is not stable after writing the receipt." -ErrorAction Continue
        exit 1
    }

    $receipt = [ordered]@{
        schemaVersion = 1
        task = "gpu-nonperformance-candidate-freeze"
        candidateId = $manifest.candidate.id
        frozenAt = $manifest.candidate.frozenAt
        manifest = Get-RelativeRepoPath $resolvedManifestPath
        manifestSha256 = Get-Sha256 $resolvedManifestPath
        manifestCanonicalSha256 = $canonical
        sourceManifestHash = $sourceSnapshot.sha256
        sourceFileCount = $sourceSnapshot.fileCount
        buildInputHash = $buildSnapshot.sha256
        buildInputFileCount = $buildSnapshot.fileCount
        entries = @($frozenEntries)
    }
    $receiptPath = Join-Path $runDirectory "freeze-receipt.json"
    Write-JsonAtomic -Path $receiptPath -Value $receipt
    $receipt | ConvertTo-Json -Depth 12
    Write-Host "Freeze receipt: $receiptPath"
    exit 0
}

if ($manifestValidation.errors.Count -gt 0) {
    $manifestValidation | ConvertTo-Json -Depth 12
    exit 1
}

# 冻结检查：源码哈希、构建输入哈希、清单规范化哈希必须各自独立提供；
# 每个可执行入口还必须提供产物哈希与产物集合哈希 ——
# 只校验 apphost .exe 证明不了 .NET 真正被加载的 dll 与资源。
$artifactMap = Get-EntryProperty -Entry $manifest.candidate -Name 'artifactSha256'
$artifactSetMap = Get-EntryProperty -Entry $manifest.candidate -Name 'artifactSetSha256'
$missingArtifactHashes = @(
    foreach ($entry in @($manifest.entries)) {
        if ($entry.kind -eq "manual") { continue }
        $hasArtifact = $false
        if ($null -ne $artifactMap) {
            $property = $artifactMap.PSObject.Properties[[string]$entry.id]
            $hasArtifact = $null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)
        }
        $hasArtifactSet = $false
        if ($null -ne $artifactSetMap) {
            $property = $artifactSetMap.PSObject.Properties[[string]$entry.id]
            $hasArtifactSet = $null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)
        }
        if (-not $hasArtifact -or -not $hasArtifactSet) {
            [pscustomobject]@{
                id = $entry.id
                hasArtifactSha256 = $hasArtifact
                hasArtifactSetSha256 = $hasArtifactSet
            }
        }
    }
)
if ($manifest.candidate.status -ne "frozen" -or
    [string]::IsNullOrWhiteSpace([string]$manifest.candidate.sourceManifestHash) -or
    [string]::IsNullOrWhiteSpace([string]$manifest.candidate.qaManifestSha256) -or
    [string]::IsNullOrWhiteSpace([string]$manifest.candidate.buildInputHash) -or
    [string]::IsNullOrWhiteSpace([string]$manifest.candidate.frozenAt) -or
    $missingArtifactHashes.Count -gt 0) {
    Write-Error "CANDIDATE_NOT_FROZEN: integration must freeze candidate id, manifest canonical hash, source hash, build-input hash, frozenAt and per-entry artifact/artifact-set hashes. Missing: $($missingArtifactHashes.id -join ', ')" -ErrorAction Continue
    exit 1
}
if ([string]$manifest.candidate.sourceManifestHash -ne $sourceSnapshot.sha256 -or
    [string]$manifest.candidate.buildInputHash -ne $buildSnapshot.sha256 -or
    [string]$manifest.candidate.qaManifestSha256 -ne $manifestValidation.canonicalSha256) {
    Write-Error "VERSION_MISMATCH: manifest, source snapshot or build-input snapshot no longer matches the frozen candidate." -ErrorAction Continue
    exit 1
}

$preExistingProbePids = [Collections.Generic.HashSet[int]]::new()
foreach ($process in Get-ProbeProcesses $manifest) {
    [void]$preExistingProbePids.Add($process.Id)
}

$buildCache = @{}
$results = [Collections.Generic.List[object]]::new()
foreach ($entry in $selectedEntries) {
    $entryDirectoryName = [string]$entry.id -replace '[^A-Za-z0-9._-]', '_'
    $entryEvidence = Join-Path $runDirectory ("evidence\" + $entryDirectoryName)
    [void][IO.Directory]::CreateDirectory($entryEvidence)
    $runStarted = [DateTime]::UtcNow
    $status = "NOT-RUN"
    $blockingCode = $null
    $detail = $null
    $buildRecord = $null
    $processResult = $null
    $evidenceRecord = $null
    $manualReceipt = $null

    try {
        # 输入漂移复核：候选目录若在运行期间被改动，必须立即阻断，
        # 不能把"变化后的构建/结果"记到旧指纹上。
        $currentSource = Get-SourceSnapshot $manifest
        $currentBuild = Get-BuildInputSnapshot $manifest
        if ($currentSource.sha256 -ne $sourceSnapshot.sha256 -or
            $currentBuild.sha256 -ne $buildSnapshot.sha256) {
            $status = "FAIL"
            $blockingCode = "VERSION_MISMATCH"
            $detail = "Source or build input drifted during the run; the frozen candidate no longer matches the working tree."
        }
        elseif ($entry.requiresGui -and -not $AllowGui) {
            $status = "BLOCKED-ENVIRONMENT"
            $blockingCode = "GUI_NOT_ALLOWED"
            $detail = "GUI group selected without -AllowGui."
        }
        elseif ($entry.requiresLive -and -not $AllowLive) {
            $status = "BLOCKED-ENVIRONMENT"
            $blockingCode = "LIVE_CAPTURE_NOT_ALLOWED"
            $detail = "Live capture selected without -AllowLive."
        }
        elseif ((Get-EntryProperty -Entry $entry -Name 'requiresManualStep') -eq $true) {
            # 旧试验台会置顶并抢焦点；外部审计只能事后报告，不能阻止已经发生的干扰，
            # 所以这类入口在满足窗口策略前不得自动运行。
            # 但人工按 manualSteps 执行完并回填收据后，收据本身是可核验的证据，
            # 此时应当按收据判定，而不是永远阻断。
            $receiptRelative = [string](Get-EntryProperty -Entry $entry -Name 'manualReceipt')
            $receiptPath = $null
            if (-not [string]::IsNullOrWhiteSpace($receiptRelative)) {
                $receiptPath = Join-Path $resolvedEvidenceRoot $receiptRelative
            }
            if ($null -eq $receiptPath -or -not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
                $status = "BLOCKED-ENVIRONMENT"
                $blockingCode = "MANUAL_STEP_REQUIRED"
                $steps = @(Get-EntryList -Entry $entry -Name 'manualSteps')
                $detail = "该入口需要人工步骤，不自动执行（避免置顶/抢焦点干扰用户桌面）。步骤：$($steps -join ' | ')"
            }
            else {
                $resolvedManual = Resolve-ManualReceipt `
                    -Entry $entry `
                    -Manifest $manifest `
                    -ReceiptPath $receiptPath `
                    -ArtifactMap $artifactMap `
                    -ArtifactSetMap $artifactSetMap
                $status = $resolvedManual.status
                $blockingCode = $resolvedManual.blockingCode
                $detail = $resolvedManual.detail
                $manualReceipt = $resolvedManual.receipt
            }
        }
        elseif ($entry.kind -eq "manual") {
            $manualPath = Join-Path $resolvedEvidenceRoot ([string](Get-EntryProperty -Entry $entry -Name 'evidence'))
            if (-not (Test-Path -LiteralPath $manualPath -PathType Leaf)) {
                $status = "BLOCKED-ENVIRONMENT"
                $blockingCode = "REQUIRED_MANUAL_EVIDENCE_MISSING"
                $detail = "Manual evidence is missing: $manualPath"
            }
            else {
                $resolvedManual = Resolve-ManualReceipt `
                    -Entry $entry `
                    -Manifest $manifest `
                    -ReceiptPath $manualPath `
                    -ArtifactMap $artifactMap `
                    -ArtifactSetMap $artifactSetMap
                $status = $resolvedManual.status
                $blockingCode = $resolvedManual.blockingCode
                $detail = $resolvedManual.detail
                $manualReceipt = $resolvedManual.receipt
            }
        }
        elseif ($entry.kind -eq "dotnet-test") {
            $buildRecord = Invoke-EntryBuild `
                -Entry $entry `
                -Cache $buildCache `
                -BuildRoot $buildRoot `
                -Manifest $manifest
            if ($buildRecord.timedOut) {
                $status = "FAIL"
                $blockingCode = "FUNCTIONAL_TIMEOUT"
                $detail = "Project build timed out."
            }
            elseif ($buildRecord.exitCode -ne 0) {
                $status = "FAIL"
                $blockingCode = "BUILD_FAILED"
                $detail = "Project build exited with $($buildRecord.exitCode)."
            }
            elseif (-not $buildRecord.fresh) {
                $status = "FAIL"
                $blockingCode = "STALE_ARTIFACT"
                $detail = "Build did not produce a fresh artifact."
            }
            elseif ($buildRecord.expectedArtifactSha256 -ne $buildRecord.artifactSha256) {
                $status = "FAIL"
                $blockingCode = "VERSION_MISMATCH"
                $detail = "Built test assembly hash does not match the frozen candidate."
            }
            elseif ($buildRecord.expectedArtifactSetSha256 -ne $buildRecord.artifactSetSha256) {
                $status = "FAIL"
                $blockingCode = "VERSION_MISMATCH"
                $detail = "Built test artifact set hash does not match the frozen candidate."
            }
            else {
                $testArguments = @(
                    "test", (Resolve-RepoPath ([string]$entry.project)),
                    "-c", "Release", "--no-build", "--no-restore", "--nologo",
                    "--results-directory", $entryEvidence,
                    "--logger", "trx;LogFileName=results.trx",
                    "--logger", "console;verbosity=minimal"
                )
                $entryFilter = [string](Get-EntryProperty -Entry $entry -Name 'filter')
                if (-not [string]::IsNullOrWhiteSpace($entryFilter)) {
                    $testArguments += @("--filter", $entryFilter)
                }
                $processResult = Invoke-ProcessWithTimeout `
                    -FilePath (Get-Command dotnet -ErrorAction Stop).Source `
                    -Arguments $testArguments `
                    -WorkingDirectory $repoRoot `
                    -TimeoutSeconds ([int]$entry.timeoutSeconds)
                [IO.File]::WriteAllText(
                    (Join-Path $entryEvidence "stdout.log"),
                    $processResult.stdout,
                    $utf8NoBom)
                [IO.File]::WriteAllText(
                    (Join-Path $entryEvidence "stderr.log"),
                    $processResult.stderr,
                    $utf8NoBom)
                $trxResult = Test-TestRunEvidence -EvidenceDirectory $entryEvidence -RunStartedUtc $runStarted
                $evidenceRecord = $trxResult
                if ($processResult.timedOut) {
                    $status = "FAIL"
                    $blockingCode = "FUNCTIONAL_TIMEOUT"
                    $detail = "Test process timed out."
                }
                elseif ($processResult.exitCode -ne 0) {
                    $status = "FAIL"
                    $blockingCode = "NONZERO_EXIT"
                    $detail = "Test process exited with $($processResult.exitCode)."
                }
                elseif (-not $trxResult.passed) {
                    $status = if ($trxResult.code -eq "SKIP") { "FAIL" } else { "FAIL" }
                    $blockingCode = $trxResult.code
                    $detail = $trxResult.detail
                }
                else {
                    $status = "PASS"
                    $detail = $trxResult.detail
                }
            }
        }
        elseif ($entry.kind -eq "dotnet-exe") {
            $buildRecord = Invoke-EntryBuild `
                -Entry $entry `
                -Cache $buildCache `
                -BuildRoot $buildRoot `
                -Manifest $manifest
            if ($buildRecord.timedOut) {
                $status = "FAIL"
                $blockingCode = "FUNCTIONAL_TIMEOUT"
                $detail = "Project build timed out."
            }
            elseif ($buildRecord.exitCode -ne 0) {
                $status = "FAIL"
                $blockingCode = "BUILD_FAILED"
                $detail = "Project build exited with $($buildRecord.exitCode)."
            }
            elseif (-not (Test-Path -LiteralPath (Resolve-RepoPath ([string]$entry.artifact)) -PathType Leaf)) {
                $status = "FAIL"
                $blockingCode = "ARTIFACT_MISSING"
                $detail = "Expected executable does not exist."
            }
            elseif (-not $buildRecord.fresh) {
                $status = "FAIL"
                $blockingCode = "STALE_ARTIFACT"
                $detail = "Build did not produce a fresh executable."
            }
            elseif ($buildRecord.expectedArtifactSha256 -ne $buildRecord.artifactSha256) {
                $status = "FAIL"
                $blockingCode = "VERSION_MISMATCH"
                $detail = "Built executable hash does not match the frozen candidate."
            }
            elseif ($buildRecord.expectedArtifactSetSha256 -ne $buildRecord.artifactSetSha256) {
                $status = "FAIL"
                $blockingCode = "VERSION_MISMATCH"
                $detail = "Built artifact set hash does not match the frozen candidate."
            }
            else {
                foreach ($copy in @(Get-EntryList -Entry $entry -Name 'copyArtifacts')) {
                    try {
                        $source = Resolve-RepoPath ([string]$copy.source)
                        $destination = Join-Path $entryEvidence ([string]$copy.destination)
                        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
                        Copy-Item -LiteralPath $source -Destination $destination -Force
                    }
                    catch {
                        # Evidence validation below reports the missing destination.
                    }
                }

                $auditScript = $null
                $auditOutputPath = $null
                if ($entry.PSObject.Properties.Name -contains "windowAuditRequired" -and
                    [bool]$entry.windowAuditRequired) {
                    $auditScript = Join-Path $repoRoot "tools\GpuUiProbe\audit-window-policy.ps1"
                    $auditOutputPath = Join-Path $entryEvidence "window-policy-audit.json"
                }
                $arguments = Expand-Arguments -Arguments @(Get-EntryList -Entry $entry -Name 'arguments') -EntryEvidence $entryEvidence
                $processResult = Invoke-ProcessWithTimeout `
                    -FilePath (Resolve-RepoPath ([string]$entry.artifact)) `
                    -Arguments $arguments `
                    -WorkingDirectory (Split-Path -Parent (Resolve-RepoPath ([string]$entry.artifact))) `
                    -TimeoutSeconds ([int]$entry.timeoutSeconds) `
                    -AuditScript $auditScript `
                    -AuditOutputPath $auditOutputPath
                [IO.File]::WriteAllText(
                    (Join-Path $entryEvidence "stdout.log"),
                    $processResult.stdout,
                    $utf8NoBom)
                [IO.File]::WriteAllText(
                    (Join-Path $entryEvidence "stderr.log"),
                    $processResult.stderr,
                    $utf8NoBom)
                foreach ($copy in @(Get-EntryList -Entry $entry -Name 'copyArtifacts')) {
                    $source = Resolve-RepoPath ([string]$copy.source)
                    $destination = Join-Path $entryEvidence ([string]$copy.destination)
                    if (Test-Path -LiteralPath $source -PathType Leaf) {
                        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
                        Copy-Item -LiteralPath $source -Destination $destination -Force
                    }
                }

                $auditResult = $processResult.audit
                $evidenceRecord = Test-EntryEvidence `
                    -Entry $entry `
                    -EntryEvidence $entryEvidence `
                    -RunStartedUtc $runStarted `
                    -Stdout $processResult.stdout `
                    -AuditResult $auditResult
                if ($processResult.timedOut) {
                    $status = "FAIL"
                    $blockingCode = "FUNCTIONAL_TIMEOUT"
                    $detail = "Executable timed out."
                }
                elseif ($processResult.exitCode -ne 0) {
                    $status = "FAIL"
                    $blockingCode = "NONZERO_EXIT"
                    $detail = "Executable exited with $($processResult.exitCode)."
                }
                elseif ($evidenceRecord.issues.Count -gt 0) {
                    $status = "FAIL"
                    $blockingCode = $evidenceRecord.issues[0].code
                    $detail = $evidenceRecord.issues[0].detail
                }
                else {
                    $status = "PASS"
                    $detail = "Required fresh evidence and assertions passed."
                }
            }
        }
    }
    catch {
        $status = "FAIL"
        $blockingCode = "EVIDENCE_ASSERTION_FAILED"
        $detail = $_.Exception.Message
    }
    finally {
        $residue = @(Stop-OwnedProbeProcesses -Manifest $manifest -PreExistingPids $preExistingProbePids)
        if ($residue.Count -gt 0) {
            $status = "FAIL"
            $blockingCode = "PROBE_RESIDUE"
            $detail = "Own probe process residue was found and stopped: $($residue.ProcessName -join ', ')"
        }
    }

    $result = [ordered]@{
        id = $entry.id
        title = $entry.title
        group = $entry.group
        kind = $entry.kind
        status = $status
        blockingCode = $blockingCode
        detail = $detail
        requiresGui = [bool]$entry.requiresGui
        requiresLive = [bool]$entry.requiresLive
        candidateId = $manifest.candidate.id
        sourceManifestHash = $manifest.candidate.sourceManifestHash
        manualReceipt = $manualReceipt
        evidenceDirectory = Get-RelativeRepoPath $entryEvidence
        build = $buildRecord
        process = if ($processResult) {
            [ordered]@{
                exitCode = $processResult.exitCode
                timedOut = $processResult.timedOut
                durationMilliseconds = $processResult.durationMilliseconds
                windowAudit = $processResult.audit
            }
        }
        else {
            $null
        }
        evidence = $evidenceRecord
    }
    $results.Add([pscustomobject]$result)
    Write-JsonAtomic -Path (Join-Path $entryEvidence "result.json") -Value $result
    Write-Host "[$status] $($entry.id) $(if ($detail) { "- $detail" })"
}

# 每个必要用例逐条核对：AND 必需入口必须全部取得有效结果。
# 只选中其中一个映射不能关闭其他必要入口；NOT-APPLICABLE 不能豁免必要项。
function Get-UseCaseEvaluation {
    param(
        [Parameter(Mandatory)][object]$Requirement,
        [Parameter(Mandatory)][object]$UseCase,
        [Parameter(Mandatory)][object[]]$EntryResults
    )

    $requiredEntries = @(Get-EntryList -Entry $UseCase -Name 'requiredEntries')
    if ($requiredEntries.Count -eq 0) {
        $requiredEntries = @(Get-EntryList -Entry $UseCase -Name 'entries')
    }
    $optionalEntries = @(Get-EntryList -Entry $UseCase -Name 'optionalEntries')

    $missing = @()
    $notApplicable = @()
    $optionalPending = @()
    foreach ($entryId in $requiredEntries) {
        $matched = @($EntryResults | Where-Object { [string]$_.id -eq [string]$entryId })
        if ($matched.Count -eq 0) {
            $missing += "$entryId (not executed)"
            continue
        }
        $result = $matched[0]
        if ([string]$result.status -eq "PASS") { continue }
        if ([string]$result.status -eq "NOT-APPLICABLE") {
            $notApplicable += "$entryId (required entry reported NOT-APPLICABLE)"
            continue
        }
        $missing += "$entryId ($($result.status))"
    }
    foreach ($entryId in $optionalEntries) {
        $matched = @($EntryResults | Where-Object { [string]$_.id -eq [string]$entryId })
        if ($matched.Count -eq 0) {
            $optionalPending += "$entryId (not executed)"
            continue
        }
        $result = $matched[0]
        if ([string]$result.status -eq "PASS") { continue }
        if ([string]$result.status -eq "NOT-APPLICABLE") { continue }
        $optionalPending += "$entryId ($($result.status))"
    }

    return [pscustomobject]@{
        requirementId = $Requirement.id
        useCaseId = $UseCase.id
        requiredEntries = $requiredEntries
        optionalEntries = $optionalEntries
        missingRequiredEntries = $missing
        notApplicableRequiredEntries = $notApplicable
        pendingOptionalEntries = $optionalPending
        satisfied = ($missing.Count -eq 0 -and $notApplicable.Count -eq 0)
    }
}

$useCaseEvaluations = @(
    foreach ($requirement in @($manifest.requirements)) {
        foreach ($useCase in @($requirement.useCases)) {
            if ($useCase.required) {
                Get-UseCaseEvaluation -Requirement $requirement -UseCase $useCase -EntryResults @($results)
            }
        }
    }
)

$performanceViolations = @(
    foreach ($result in @($results)) {
        $evidence = Get-EntryProperty -Entry $result -Name 'evidence'
        if ($null -eq $evidence) { continue }
        if ((Get-EntryProperty -Entry $evidence -Name 'performanceExecuted') -eq $true) {
            [pscustomobject]@{
                id = $result.id
                detail = @(Get-EntryProperty -Entry $evidence -Name 'performanceEvidence')
            }
        }
    }
)
$performanceExecuted = $performanceViolations.Count -gt 0

$notApplicableBlocking = @(
    $useCaseEvaluations | Where-Object { @($_.notApplicableRequiredEntries).Count -gt 0 }
)

$passCount = @($results | Where-Object status -eq "PASS").Count
$failCount = @($results | Where-Object status -eq "FAIL").Count
$blockedCount = @($results | Where-Object status -like "BLOCKED*").Count
$notApplicableCount = @($results | Where-Object status -eq "NOT-APPLICABLE").Count
$notRunCount = @($results | Where-Object status -eq "NOT-RUN").Count
$overallStatus = if ($failCount -gt 0 -or $performanceExecuted) {
    "FAIL"
}
elseif ($blockedCount -gt 0 -or $notRunCount -gt 0 -or $notApplicableBlocking.Count -gt 0) {
    "BLOCKED-ENVIRONMENT"
}
else {
    "PASS"
}

$summary = [ordered]@{
    schemaVersion = 1
    task = "gpu-nonperformance-aggregate-runner"
    status = $overallStatus
    partial = -not $RequireAll
    generatedAt = (Get-Date).ToString("o")
    candidateId = $manifest.candidate.id
    sourceManifestHash = $manifest.candidate.sourceManifestHash
    currentSourceManifestHash = $sourceSnapshot.sha256
    buildInputHash = $manifest.candidate.buildInputHash
    currentBuildInputHash = $buildSnapshot.sha256
    manifest = Get-RelativeRepoPath $resolvedManifestPath
    manifestSha256 = $manifestValidation.manifestSha256
    manifestCanonicalSha256 = $manifestValidation.canonicalSha256
    requestedGroups = @($requestedGroups)
    allowGui = [bool]$AllowGui
    allowLive = [bool]$AllowLive
    counts = [ordered]@{
        pass = $passCount
        fail = $failCount
        blocked = $blockedCount
        notApplicable = $notApplicableCount
        notRun = $notRunCount
        total = $results.Count
    }
    results = @($results)
    useCaseEvaluations = @($useCaseEvaluations)
    notApplicableRequiredUseCases = @($notApplicableBlocking)
    deferred = @($manifest.deferred)
    performanceExecuted = $performanceExecuted
    performanceViolations = @($performanceViolations)
    guiExecuted = @($results | Where-Object { (Get-EntryProperty -Entry $_ -Name 'requiresGui') -eq $true }).Count -gt 0
    liveExecuted = @($results | Where-Object { (Get-EntryProperty -Entry $_ -Name 'requiresLive') -eq $true }).Count -gt 0
}

if ($RequireAll) {
    $unsatisfiedUseCases = @($useCaseEvaluations | Where-Object { $_.satisfied -ne $true })
    $summary.unsatisfiedRequiredUseCases = $unsatisfiedUseCases
    if ($unsatisfiedUseCases.Count -gt 0) {
        $summary.status = "BLOCKED-ENVIRONMENT"
    }
}

$summaryPath = Join-Path $runDirectory "summary.json"
Write-JsonAtomic -Path $summaryPath -Value $summary
Write-JsonAtomic -Path (Join-Path $resolvedEvidenceRoot "latest-run.json") -Value $summary
$summary | ConvertTo-Json -Depth 16
Write-Host "Run evidence: $runDirectory"

if ($summary.status -eq "PASS") {
    exit 0
}
exit 1
