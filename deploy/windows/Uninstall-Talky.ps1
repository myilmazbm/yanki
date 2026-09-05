<#
.SYNOPSIS
    Removes a Talky node: the "Talky" service, the program files and the firewall rules.

.DESCRIPTION
    Idempotent - anything already gone is reported and skipped.

    Data is kept by default. DataDir holds appsettings.json, the logs under
    %ProgramData%\Talky\logs and the Lucene index; none of it is recreated by a
    reinstall, so it is only removed when -RemoveData is given. Nothing in SQL Server
    and nothing on the SMB/DFS file share is touched, ever.

    Order: stop the service, delete it, remove the firewall rules, remove InstallDir,
    then optionally remove DataDir.

.PARAMETER InstallDir
    Program directory to remove. Default C:\Program Files\Talky.

.PARAMETER DataDir
    Data directory. Kept unless -RemoveData. Default %ProgramData%\Talky.

.PARAMETER RemoveData
    Also delete DataDir, including logs and the search index. Irreversible.

.PARAMETER KeepFirewall
    Leave the Talky firewall rules in place (useful when reinstalling immediately).

.EXAMPLE
    .\Uninstall-Talky.ps1 -WhatIf

.EXAMPLE
    .\Uninstall-Talky.ps1 -RemoveData -Confirm:$false

.NOTES
    Requires an elevated session. PowerShell 5.1 compatible.
    An MSI-installed node is removed with `msiexec /x` instead; this script is for
    installations made by Install-Talky.ps1.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'Talky'),
    [string] $DataDir = (Join-Path $env:ProgramData 'Talky'),
    [switch] $RemoveData,
    [switch] $KeepFirewall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'Talky'
$FirewallScript = Join-Path $PSScriptRoot 'Set-TalkyFirewall.ps1'

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) {
    # A dry run only reads, so it stays useful from an unelevated shell.
    if ($WhatIfPreference) {
        Write-Warning 'Not elevated. Continuing because -WhatIf was given; a real run needs an elevated session.'
    }
    else {
        throw 'Uninstall-Talky.ps1 must run in an elevated PowerShell session.'
    }
}

# --- service --------------------------------------------------------------------------

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "absent   service $ServiceName"
}
else {
    if ($service.Status -ne 'Stopped') {
        if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop service')) {
            Stop-Service -Name $ServiceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
            Write-Host "stopped  service $ServiceName"
        }
    }
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Delete service')) {
        $output = & "$env:SystemRoot\System32\sc.exe" delete $ServiceName 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe delete $ServiceName failed with exit code ${LASTEXITCODE}: $output"
        }
        Write-Host "deleted  service $ServiceName"
    }
}

# --- firewall -------------------------------------------------------------------------

if ($KeepFirewall) {
    Write-Host 'firewall kept (-KeepFirewall).'
}
elseif (-not (Test-Path -LiteralPath $FirewallScript)) {
    Write-Warning 'Set-TalkyFirewall.ps1 not found next to this script; firewall rules left in place.'
}
else {
    $firewallArgs = @{ Remove = $true }
    if ($WhatIfPreference) { $firewallArgs['WhatIf'] = $true }
    & $FirewallScript @firewallArgs
}

# --- program files --------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $InstallDir)) {
    Write-Host "absent   $InstallDir"
}
elseif ($PSCmdlet.ShouldProcess($InstallDir, 'Remove program directory')) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "removed  $InstallDir"
}

# --- data -----------------------------------------------------------------------------

if (-not $RemoveData) {
    Write-Host "kept     $DataDir (pass -RemoveData to delete configuration, logs and the search index)"
}
elseif (-not (Test-Path -LiteralPath $DataDir)) {
    Write-Host "absent   $DataDir"
}
elseif ($PSCmdlet.ShouldProcess($DataDir, 'Remove data directory including logs and search index')) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
    Write-Host "removed  $DataDir"
}

Write-Host ''
Write-Host 'Uninstall complete. SQL Server databases and the file share were not touched.'
