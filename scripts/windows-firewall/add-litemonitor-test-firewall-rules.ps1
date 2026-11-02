#requires -Version 7.0
<#
.SYNOPSIS
    Builds loopback-only Windows firewall allow rules for current LiteMonitor test binaries.

.DESCRIPTION
    Rules are limited to inbound TCP/UDP on 127.0.0.1 and are grouped under
    "LiteMonitor Test Allowlist". Existing rules are replaced only when this module can prove
    LiteMonitor ownership. Use -WhatIf to inspect every intended change.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiteMonitorFirewallRules.psm1') -Force
Assert-LiteMonitorFirewallAdministrator -AllowPlanMode:$WhatIfPreference

$ruleGroup = 'LiteMonitor Test Allowlist'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$programPaths = [Collections.Generic.List[string]]::new()

Get-ChildItem -Path $repoRoot -Recurse -File -Filter 'testhost.exe' -ErrorAction SilentlyContinue |
    ForEach-Object { $programPaths.Add($_.FullName) }

$binRoot = Join-Path $repoRoot 'bin'
if (Test-Path -LiteralPath $binRoot) {
    Get-ChildItem -Path $binRoot -Recurse -File -Filter '*.exe' -ErrorAction SilentlyContinue |
        ForEach-Object { $programPaths.Add($_.FullName) }
}

$worktreeRoot = Join-Path $repoRoot '.worktrees'
if (Test-Path -LiteralPath $worktreeRoot) {
    Get-ChildItem -Path $worktreeRoot -Recurse -File -Filter 'testhost.exe' -ErrorAction SilentlyContinue |
        ForEach-Object { $programPaths.Add($_.FullName) }
}

$programPaths = @(
    $programPaths |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Sort-Object -Unique
)
Write-Host ('Prepared {0} exact executable paths.' -f $programPaths.Count)

$assessment = @(Get-LiteMonitorFirewallRuleAssessment -RepoRoot $repoRoot)
$owned = @($assessment | Where-Object Owned)
foreach ($item in $owned) {
    if ($PSCmdlet.ShouldProcess($item.DisplayName, 'Remove existing LiteMonitor-owned firewall rule')) {
        Remove-NetFirewallRule -Name $item.Name -ErrorAction Stop
    }
}

$created = 0
$planned = @()
foreach ($program in $programPaths) {
    foreach ($protocol in @('TCP', 'UDP')) {
        $programHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData(
                [Text.Encoding]::UTF8.GetBytes($program))).Substring(0, 8)
        $displayName = "LiteMonitorTest - $([IO.Path]::GetFileNameWithoutExtension($program)) - $protocol - $programHash"
        $planned += [pscustomobject]@{
            DisplayName = $displayName
            Group = $ruleGroup
            Program = $program
            Protocol = $protocol
            Ownership = 'owned-test-program'
        }
        if ($PSCmdlet.ShouldProcess($displayName, 'Create loopback-only firewall rule')) {
            New-NetFirewallRule -DisplayName $displayName `
                -Group $ruleGroup `
                -Direction Inbound `
                -Action Allow `
                -Protocol $protocol `
                -Program $program `
                -Profile Any `
                -LocalAddress '127.0.0.1' `
                -EdgeTraversalPolicy Block `
                -Description 'LiteMonitor test-local loopback rule; safe to remove with the cleanup script.' `
                -ErrorAction Stop | Out-Null
            $created++
        }
    }
}

$planned | Format-Table DisplayName, Group, Program, Protocol, Ownership -AutoSize

$staleOwned = @(
    Get-LiteMonitorFirewallRuleAssessment -RepoRoot $repoRoot |
        Where-Object {
            $_.Owned -and
            $_.Programs.Count -gt 0 -and
            @($_.Programs | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -eq $_.Programs.Count
        }
)
foreach ($item in $staleOwned) {
    if ($PSCmdlet.ShouldProcess($item.DisplayName, 'Remove stale LiteMonitor-owned firewall rule')) {
        Remove-NetFirewallRule -Name $item.Name -ErrorAction Stop
    }
}

Write-Host ('Planned creation operations: {0}; executed creations: {1}; stale owned removals: {2}.' -f
    $planned.Count, $created, $staleOwned.Count)
