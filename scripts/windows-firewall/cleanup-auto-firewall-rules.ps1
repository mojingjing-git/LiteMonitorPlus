#requires -Version 7.0
<#
.SYNOPSIS
    Reviews and optionally removes firewall rules generated for LiteMonitor test binaries.

.DESCRIPTION
    Only rules in the LiteMonitor-owned group, LiteMonitor-prefixed rules, or auto-generated
    testhost/WebProbe rules whose Program path is inside this repository are eligible.
    Use -WhatIf to inspect the exact ownership decision without changing firewall state.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiteMonitorFirewallRules.psm1') -Force
Assert-LiteMonitorFirewallAdministrator -AllowPlanMode:$WhatIfPreference

$assessment = @(Get-LiteMonitorFirewallRuleAssessment -RepoRoot $RepoRoot)
Write-Host ('Eligible rule assessment: {0} candidates' -f $assessment.Count)
Write-LiteMonitorFirewallAssessment $assessment

$owned = @($assessment | Where-Object Owned)
foreach ($item in $owned) {
    if ($PSCmdlet.ShouldProcess($item.DisplayName, 'Remove LiteMonitor-owned firewall rule')) {
        Remove-NetFirewallRule -Name $item.Name -ErrorAction Stop
    }
}

Write-Host ('Removed or planned {0} owned rules; unrelated rules were left untouched.' -f $owned.Count)
Get-NetFirewallProfile |
    Select-Object Name, Enabled, DefaultInboundAction |
    Format-Table -AutoSize
