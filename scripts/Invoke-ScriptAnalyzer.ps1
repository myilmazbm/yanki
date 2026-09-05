<#
.SYNOPSIS
    Runs PSScriptAnalyzer over every PowerShell script in the repository.

.DESCRIPTION
    Used by CI and locally before committing changes under deploy\. Fails (exit 1) on any
    Error or Warning diagnostic. Information-level rules are reported but do not fail the run.

    Install the analyzer first if it is missing:
        Install-Module PSScriptAnalyzer -Scope CurrentUser -Force

    Suppressed rules and why:
      PSAvoidUsingWriteHost      the deployment scripts are operator-facing console tools;
                                 their progress output is the point, not pipeline data.
      PSUseShouldProcessForStateChangingFunctions
                                 the scripts declare SupportsShouldProcess at script level
                                 and their helper functions call $PSCmdlet.ShouldProcess
                                 from that scope, which the rule cannot see.

.PARAMETER Path
    Root to scan. Defaults to the repository root.

.PARAMETER Severity
    Severities that fail the run. Default Error, Warning.

.EXAMPLE
    .\scripts\Invoke-ScriptAnalyzer.ps1

.NOTES
    Read-only. PowerShell 5.1 compatible.
#>
[CmdletBinding()]
param(
    [string] $Path,
    [ValidateSet('Error', 'Warning', 'Information')]
    [string[]] $Severity = @('Error', 'Warning')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Path) { $Path = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }

if (-not (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
    Write-Warning 'PSScriptAnalyzer is not installed. Run: Install-Module PSScriptAnalyzer -Scope CurrentUser -Force'
    exit 0
}
Import-Module PSScriptAnalyzer -ErrorAction Stop

$excludedRules = @(
    'PSAvoidUsingWriteHost',
    'PSUseShouldProcessForStateChangingFunctions'
)

$results = @(Invoke-ScriptAnalyzer -Path $Path -Recurse -ExcludeRule $excludedRules)

if ($results.Count -eq 0) {
    Write-Host "PSScriptAnalyzer: clean ($Path)."
    exit 0
}

$results |
    Sort-Object Severity, ScriptName, Line |
    Format-Table Severity, ScriptName, Line, RuleName, Message -AutoSize -Wrap

$blocking = @($results | Where-Object { $Severity -contains [string]$_.Severity })
Write-Host ''
Write-Host ("PSScriptAnalyzer: {0} diagnostics, {1} blocking." -f $results.Count, $blocking.Count)
if ($blocking.Count -gt 0) { exit 1 }
