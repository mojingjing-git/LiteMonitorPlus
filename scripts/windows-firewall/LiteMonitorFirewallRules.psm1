Set-StrictMode -Version Latest

$script:DefaultOwnedGroup = 'LiteMonitor Test Allowlist'

function Test-LiteMonitorFirewallAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-LiteMonitorFirewallAdministrator {
    param([switch] $AllowPlanMode)

    if ($AllowPlanMode -or (Test-LiteMonitorFirewallAdministrator)) { return }
    throw 'This operation requires an elevated PowerShell session. Use -WhatIf for a read-only plan.'
}

function Test-LiteMonitorPathWithinRoot {
    param(
        [AllowNull()][string] $Path,
        [Parameter(Mandatory)][string] $Root
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -in @('Any', '*')) { return $false }

    try {
        $fullPath = [IO.Path]::GetFullPath($Path)
        $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        return $fullPath.Equals($fullRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $fullPath.StartsWith(
                $fullRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Get-LiteMonitorFirewallRuleAssessment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RepoRoot,
        [string] $OwnedGroup = $script:DefaultOwnedGroup
    )

    $candidates = @(
        Get-NetFirewallRule -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Group -eq $OwnedGroup -or
                $_.DisplayName -match '^(LiteMonitorTest -|testhost(?:\.exe)?$|WebProbe(?:\.exe)?$)'
            }
    )

    foreach ($rule in $candidates) {
        $programs = @(
            Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue |
                ForEach-Object { [string]$_.Program } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Sort-Object -Unique
        )

        $inRepo = @($programs | Where-Object { Test-LiteMonitorPathWithinRoot -Path $_ -Root $RepoRoot })
        $groupOwned = [string]$rule.Group -eq $OwnedGroup
        $prefixOwned = [string]$rule.DisplayName -like 'LiteMonitorTest - *' -and $inRepo.Count -gt 0
        $autoOwned = [string]$rule.DisplayName -match '^(testhost(?:\.exe)?|WebProbe(?:\.exe)?)$' -and $inRepo.Count -gt 0
        $owned = $groupOwned -or $prefixOwned -or $autoOwned
        $reason = if ($groupOwned) {
            'owned-group'
        }
        elseif ($prefixOwned) {
            'owned-prefix-and-repository-program'
        }
        elseif ($autoOwned) {
            'auto-rule-for-repository-program'
        }
        else {
            'not-owned'
        }

        [pscustomobject]@{
            Rule = $rule
            Name = [string]$rule.Name
            DisplayName = [string]$rule.DisplayName
            Group = [string]$rule.Group
            Programs = $programs
            ProgramDisplay = if ($programs.Count -eq 0) { '<none>' } else { $programs -join '; ' }
            Owned = $owned
            Reason = $reason
        }
    }
}

function Write-LiteMonitorFirewallAssessment {
    param([Parameter(Mandatory)] $Assessment)

    foreach ($item in $Assessment) {
        Write-Host ('{0} [{1}] {2} -> {3}' -f
            $(if ($item.Owned) { 'OWNED' } else { 'KEEP ' }),
            $item.Reason,
            $item.DisplayName,
            $item.ProgramDisplay)
    }
}

Export-ModuleMember -Function @(
    'Assert-LiteMonitorFirewallAdministrator',
    'Get-LiteMonitorFirewallRuleAssessment',
    'Test-LiteMonitorFirewallAdministrator',
    'Test-LiteMonitorPathWithinRoot',
    'Write-LiteMonitorFirewallAssessment'
)
