<#
.SYNOPSIS
    Real-install acceptance smoke for a Talky node: the M0 exit criterion "a service
    installed with msiexec answers /api/v1/ping", plus the same check for the script path.

.DESCRIPTION
    STATUS: written for M0 but NOT EXECUTED YET. It installs and removes a Windows service,
    firewall rules and an event log source, so run it only on a throw-away VM, elevated,
    with -AcknowledgeServiceInstall. The machine must not already have a "Talky" service.

    Script leg: Install-Talky.ps1 (publish output, http-only lab URL on -Port, NodeId
    smoke-script) -> sc.exe qc / qfailure show delayed auto start and three restarts ->
    GET /api/v1/ping returns the node id -> a second install run leaves appsettings.json
    byte-identical and the service running -> Uninstall-Talky.ps1 -RemoveData.

    MSI leg: msiexec /i /qn exits 0 with the service stopped -> Install-Talky.ps1 -AclOnly
    and -ConfigOnly (NodeId smoke-msi) -> talky migrate -> Start-Service -> ping ->
    msiexec /x. DATADIR (%ProgramData%\Talky) is permanent and is left behind.

    Prints one PASS/FAIL line per check and exits 1 on the first failed step.

.PARAMETER SqlConnectionString
    Connection string the node uses; the database is created by `talky migrate` if the
    installing account may create it.

.PARAMETER PublishDir
    Publish output for the script leg. Default <repo>\artifacts\publish.

.PARAMETER MsiPath
    MSI for the MSI leg. Default the newest <repo>\artifacts\Talky.Server-*.msi.

.PARAMETER Port
    HTTP port of the lab URL. Default 18080.

.PARAMETER AcknowledgeServiceInstall
    Required: confirms that this machine may get a Talky service installed and removed.

.EXAMPLE
    .\Test-TalkyInstall.ps1 -AcknowledgeServiceInstall `
        -SqlConnectionString 'Server=sql01;Database=TalkySmoke;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SqlConnectionString,
    [string] $PublishDir,
    [string] $MsiPath,
    [ValidateRange(1024, 65535)][int] $Port = 18080,
    [switch] $AcknowledgeServiceInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Install = Join-Path $PSScriptRoot 'Install-Talky.ps1'
$Uninstall = Join-Path $PSScriptRoot 'Uninstall-Talky.ps1'
$Url = "http://+:$Port"
$PingUrl = "http://127.0.0.1:$Port/api/v1/ping"
$ScriptDataDir = Join-Path $env:ProgramData 'TalkySmoke'
$MsiDataDir = Join-Path $env:ProgramData 'Talky'
$script:Failed = 0

function Write-Check {
    param([Parameter(Mandatory)][bool] $Condition, [Parameter(Mandatory)][string] $Message)
    if ($Condition) { Write-Host "PASS  $Message" }
    else { Write-Host "FAIL  $Message"; $script:Failed++ }
}

function Assert-Step {
    param([Parameter(Mandatory)][string] $Leg)
    if ($script:Failed -gt 0) { throw "$Leg leg stopped after a failed check." }
}

function Test-Ping {
    param([Parameter(Mandatory)][string] $NodeId)
    $nodeSeen = $null
    for ($i = 0; $i -lt 30 -and -not $nodeSeen; $i++) {
        try { $nodeSeen = (Invoke-RestMethod -Uri $PingUrl -TimeoutSec 5).nodeId }
        catch { Start-Sleep -Seconds 2 }
    }
    Write-Check ($nodeSeen -eq $NodeId) "GET $PingUrl returns nodeId '$NodeId' (got '$nodeSeen')"
}

function Test-Running {
    param([Parameter(Mandatory)][string] $When)
    $svc = Get-Service -Name Talky -ErrorAction SilentlyContinue
    Write-Check ($null -ne $svc -and $svc.Status -eq 'Running') "service Talky is running $When"
}

function Invoke-Msiexec {
    param([Parameter(Mandatory)][string[]] $Arguments)
    return (Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $Arguments -Wait -PassThru).ExitCode
}

# --- preconditions ----------------------------------------------------------------------

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Test-TalkyInstall.ps1 must run elevated.'
}
if (-not $AcknowledgeServiceInstall) {
    throw 'This script installs and removes the Talky service. Run it on a throw-away VM with -AcknowledgeServiceInstall.'
}
if (Get-Service -Name Talky -ErrorAction SilentlyContinue) {
    throw 'A Talky service already exists on this machine; use a clean VM.'
}
if (-not $PublishDir) { $PublishDir = Join-Path $RepoRoot 'artifacts\publish' }
if (-not $MsiPath) {
    $MsiPath = Get-ChildItem -Path (Join-Path $RepoRoot 'artifacts') -Filter 'Talky.Server-*.msi' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $MsiPath -or -not (Test-Path -LiteralPath $MsiPath)) { throw 'No MSI found; run msi\Build-Msi.ps1 or pass -MsiPath.' }

try {
    # --- script leg ---------------------------------------------------------------------
    Write-Host "--- script leg (Install-Talky.ps1, DataDir $ScriptDataDir) ---"
    $installArgs = @{
        SourceDir = $PublishDir; DataDir = $ScriptDataDir; Urls = $Url; NodeId = 'smoke-script'
        SqlConnectionString = $SqlConnectionString; Confirm = $false
    }
    & $Install @installArgs
    Test-Running 'after the first install'

    $qc = & "$env:SystemRoot\System32\sc.exe" qc Talky | Out-String
    Write-Check ($qc -match 'AUTO_START\s+\(DELAYED\)') 'sc.exe qc: Automatic (Delayed Start)'
    Write-Check ($qc -notmatch '--role') 'sc.exe qc: no --role on the command line'
    $qf = & "$env:SystemRoot\System32\sc.exe" qfailure Talky | Out-String
    Write-Check (([regex]::Matches($qf, 'RESTART')).Count -eq 3 -and $qf -match '5000' -and $qf -match '10000' -and $qf -match '30000') `
        'sc.exe qfailure: restart after 5 s / 10 s / 30 s'
    Test-Ping -NodeId 'smoke-script'
    Assert-Step 'script'

    $configPath = Join-Path $ScriptDataDir 'appsettings.json'
    $hashBefore = (Get-FileHash -LiteralPath $configPath).Hash
    & $Install @installArgs
    Write-Check ((Get-FileHash -LiteralPath $configPath).Hash -eq $hashBefore) 'second install run leaves appsettings.json byte-identical'
    Test-Running 'after the second install'
    Test-Ping -NodeId 'smoke-script'
    Assert-Step 'script'

    & $Uninstall -DataDir $ScriptDataDir -RemoveData -Confirm:$false
    Write-Check ($null -eq (Get-Service -Name Talky -ErrorAction SilentlyContinue)) 'Uninstall-Talky.ps1 removed the service'
    Assert-Step 'script'

    # --- MSI leg ------------------------------------------------------------------------
    Write-Host "--- MSI leg ($MsiPath) ---"
    $log = Join-Path $env:TEMP 'talky-smoke-msi-install.log'
    # /norestart: with /qn msiexec would otherwise reboot the VM by itself if it decides one is needed.
    $code = Invoke-Msiexec @('/i', "`"$MsiPath`"", '/qn', '/norestart', '/l*v', "`"$log`"")
    Write-Check ($code -eq 0) "msiexec /i /qn exits 0 (got $code; log $log)"
    $svc = Get-Service -Name Talky -ErrorAction SilentlyContinue
    Write-Check ($null -ne $svc -and $svc.Status -eq 'Stopped') 'service Talky registered and stopped after msiexec'
    Assert-Step 'MSI'

    & $Install -AclOnly -Confirm:$false
    & $Install -ConfigOnly -Urls $Url -NodeId 'smoke-msi' -SqlConnectionString $SqlConnectionString -Confirm:$false
    $exe = Join-Path $env:ProgramFiles 'Talky\talky.exe'
    & $exe migrate --data-dir $MsiDataDir
    Write-Check ($LASTEXITCODE -eq 0) "talky migrate exits 0 (got $LASTEXITCODE)"
    Assert-Step 'MSI'

    Start-Service -Name Talky
    Test-Running 'after Start-Service'
    Test-Ping -NodeId 'smoke-msi'
    Assert-Step 'MSI'

    # The MSI's own service stop waits at most 30 s, less than the node's drain
    # (DrainSeconds, 30 by default, plus shutdown); Stop-Service waits it out.
    Stop-Service -Name Talky
    $code = Invoke-Msiexec @('/x', "`"$MsiPath`"", '/qn', '/norestart')
    Write-Check ($code -eq 0) "msiexec /x /qn exits 0 (got $code)"
    Write-Check ($null -eq (Get-Service -Name Talky -ErrorAction SilentlyContinue)) 'msiexec /x removed the service'
}
catch {
    Write-Host "FAIL  $($_.Exception.Message)"
    $script:Failed++
}

Write-Host ''
if ($script:Failed -gt 0) {
    Write-Host "Install smoke FAILED ($($script:Failed) failure(s)). Logs: $ScriptDataDir\logs, $MsiDataDir\logs, Application event log (source Talky)."
    exit 1
}
Write-Host 'Install smoke PASSED.'
