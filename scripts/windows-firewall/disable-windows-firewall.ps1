#requires -Version 7.0
<#
.SYNOPSIS
    Captures a one-time firewall backup and disables the Windows firewall profiles for local tests.

.DESCRIPTION
    This is a high-impact manual fallback. Prefer the loopback allow-list script.
    The first invocation creates a validated backup. Later invocations refuse to overwrite it.
    Use -WhatIf to inspect the action without writing the backup or changing firewall state.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiteMonitorFirewallRules.psm1') -Force
Assert-LiteMonitorFirewallAdministrator -AllowPlanMode:$WhatIfPreference

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$backup = Join-Path $env:LOCALAPPDATA 'LiteMonitor++\windows-firewall\firewall-state-backup.json'

function Read-ValidatedBackup {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $state = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$state.CapturedAt) -or
        $null -eq $state.Profiles -or @($state.Profiles).Count -ne 3) {
        throw "Existing firewall backup is invalid; refusing to overwrite it: $Path"
    }
    foreach ($profile in $state.Profiles) {
        if ([string]$profile.Name -notin @('Domain', 'Private', 'Public')) {
            throw "Existing firewall backup contains an unknown profile: $Path"
        }
        if ([string]$profile.Enabled -notin @('True', 'False')) {
            throw "Existing firewall backup contains an invalid Enabled value: $Path"
        }
    }
    return $state
}

if (Test-Path -LiteralPath $backup) {
    $existing = Read-ValidatedBackup -Path $backup
    $anyEnabled = @($existing.Profiles | Where-Object { [string]$_.Enabled -eq 'True' }).Count -gt 0
    if (-not $anyEnabled) {
        throw "Firewall is already disabled according to the existing backup. Run restore-windows-firewall.ps1 first: $backup"
    }
    Write-Host "A valid pre-disable backup already exists and will be preserved: $backup"
}
else {
    $profiles = @(Get-NetFirewallProfile)
    if ($profiles.Count -ne 3) { throw "Expected three Windows firewall profiles, found $($profiles.Count)." }
    $state = @{
        CapturedAt = (Get-Date).ToString('o')
        Note = 'Windows firewall state captured before LiteMonitor test fallback'
        Profiles = @($profiles | ForEach-Object {
            [pscustomobject]@{
                Name = [string]$_.Name
                Enabled = [string]$_.Enabled
                DefaultInboundAction = [string]$_.DefaultInboundAction
                DefaultOutboundAction = [string]$_.DefaultOutboundAction
                LogFileName = [string]$_.LogFileName
                LogBlocked = [string]$_.LogBlocked
                LogAllowed = [string]$_.LogAllowed
            }
        })
    }
    if ($PSCmdlet.ShouldProcess($backup, 'Create one-time firewall backup')) {
        New-Item -ItemType Directory -Force -Path (Split-Path $backup -Parent) | Out-Null
        $temp = $backup + '.tmp'
        $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temp -Encoding utf8
        [void](Read-ValidatedBackup -Path $temp)
        Move-Item -LiteralPath $temp -Destination $backup -Force
        Write-Host "Created firewall backup: $backup"
    }
}

$owned = @(Get-LiteMonitorFirewallRuleAssessment -RepoRoot $repo | Where-Object Owned)
Write-Host ('Owned firewall rules eligible for removal: {0}' -f $owned.Count)
Write-LiteMonitorFirewallAssessment $owned
foreach ($item in $owned) {
    if ($PSCmdlet.ShouldProcess($item.DisplayName, 'Remove LiteMonitor-owned firewall rule')) {
        Remove-NetFirewallRule -Name $item.Name -ErrorAction Stop
    }
}

foreach ($profile in @('Domain', 'Private', 'Public')) {
    if ($PSCmdlet.ShouldProcess($profile, 'Disable Windows firewall profile')) {
        Set-NetFirewallProfile -Profile $profile -Enabled False -DefaultInboundAction Block -ErrorAction Stop
    }
}

Write-Host 'Restore with restore-windows-firewall.ps1. The saved profile snapshot is not overwritten.'
