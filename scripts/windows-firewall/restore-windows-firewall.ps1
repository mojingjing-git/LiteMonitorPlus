#requires -Version 7.0
<#
.SYNOPSIS
    Restores the firewall profile state captured by disable-windows-firewall.ps1.

.DESCRIPTION
    Only LiteMonitor-owned rules are removed. If the validated backup exists, all captured
    profile fields are restored and the backup is retired after a successful restore.
    Without a backup, the conservative fallback enables all profiles with inbound Block.
    Use -WhatIf to inspect the restore without changing firewall state.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiteMonitorFirewallRules.psm1') -Force
Assert-LiteMonitorFirewallAdministrator -AllowPlanMode:$WhatIfPreference

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$backup = Join-Path $env:LOCALAPPDATA 'LiteMonitor++\windows-firewall\firewall-state-backup.json'
$state = $null

if (Test-Path -LiteralPath $backup -PathType Leaf) {
    $state = Get-Content -LiteralPath $backup -Raw -Encoding utf8 | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$state.CapturedAt) -or
        $null -eq $state.Profiles -or @($state.Profiles).Count -ne 3) {
        throw "Firewall backup is invalid; refusing to guess the previous state: $backup"
    }

    foreach ($profile in $state.Profiles) {
        if ([string]$profile.Name -notin @('Domain', 'Private', 'Public')) {
            throw "Firewall backup contains an unknown profile: $($profile.Name)"
        }
        if ($PSCmdlet.ShouldProcess([string]$profile.Name, 'Restore captured firewall profile state')) {
            $arguments = @{
                Profile = [string]$profile.Name
                Enabled = [bool]::Parse([string]$profile.Enabled)
                DefaultInboundAction = [string]$profile.DefaultInboundAction
                DefaultOutboundAction = [string]$profile.DefaultOutboundAction
                LogFileName = [string]$profile.LogFileName
                LogBlocked = [bool]::Parse([string]$profile.LogBlocked)
                LogAllowed = [bool]::Parse([string]$profile.LogAllowed)
            }
            Set-NetFirewallProfile @arguments -ErrorAction Stop
        }
    }
}
else {
    Write-Warning 'No validated firewall backup exists; using the conservative three-profile fallback.'
    foreach ($name in @('Domain', 'Private', 'Public')) {
        if ($PSCmdlet.ShouldProcess($name, 'Enable firewall profile with inbound Block')) {
            Set-NetFirewallProfile -Profile $name -Enabled True -DefaultInboundAction Block -ErrorAction Stop
        }
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

if ($state -and $PSCmdlet.ShouldProcess($backup, 'Retire restored firewall backup')) {
    Remove-Item -LiteralPath $backup -Force
}

Get-NetFirewallProfile |
    Select-Object Name, Enabled, DefaultInboundAction, DefaultOutboundAction |
    Format-Table -AutoSize
