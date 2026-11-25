<#
.SYNOPSIS
    Runs PSScriptAnalyzer over the PowerShell scripts under deploy\ and scripts\.

.DESCRIPTION
    Used by CI and locally before committing changes under deploy\. Fails (exit 1) on any
    Error or Warning diagnostic. Information-level rules are reported but do not fail the run.
    A missing analyzer module is a warning and exit 0 locally, but exit 1 when $env:CI is
    'true'.

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
    Roots to scan. Defaults to <repo>\deploy and <repo>\scripts (not the whole repository,
    which would include generated shims under clients\node_modules).

.PARAMETER Severity
    Severities that fail the run. Default Error, Warning.

.EXAMPLE
    .\scripts\Invoke-ScriptAnalyzer.ps1

.NOTES
    Read-only. PowerShell 5.1 compatible.
#>
[CmdletBinding()]
param(
    [string[]] $Path,
    [ValidateSet('Error', 'Warning', 'Information')]
    [string[]] $Severity = @('Error', 'Warning')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $Path = @((Join-Path $repoRoot 'deploy'), (Join-Path $repoRoot 'scripts'))
}

if (-not (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
    # Locally a missing analyzer is a hint; in CI it would make the check pass vacuously.
    if ($env:CI -eq 'true') {
        Write-Host 'PSScriptAnalyzer is not installed; failing because CI is true.'
        exit 1
    }
    Write-Warning 'PSScriptAnalyzer is not installed. Run: Install-Module PSScriptAnalyzer -Scope CurrentUser -Force'
    exit 0
}
Import-Module PSScriptAnalyzer -ErrorAction Stop

$excludedRules = @(
    'PSAvoidUsingWriteHost',
    'PSUseShouldProcessForStateChangingFunctions'
)

$results = @($Path | ForEach-Object { Invoke-ScriptAnalyzer -Path $_ -Recurse -ExcludeRule $excludedRules })

if ($results.Count -eq 0) {
    Write-Host "PSScriptAnalyzer: clean ($($Path -join ', '))."
    exit 0
}

$results |
    Sort-Object Severity, ScriptName, Line |
    Format-Table Severity, ScriptName, Line, RuleName, Message -AutoSize -Wrap

$blocking = @($results | Where-Object { $Severity -contains [string]$_.Severity })
Write-Host ''
Write-Host ("PSScriptAnalyzer: {0} diagnostics, {1} blocking." -f $results.Count, $blocking.Count)
if ($blocking.Count -gt 0) { exit 1 }
