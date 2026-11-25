<#
.SYNOPSIS
    Real-install acceptance smoke for a Talky node: the M0 exit criterion "a service
    installed with msiexec answers /api/v1/ping", plus the same check for the script path.

.DESCRIPTION
    STATUS: executed on 2026-11-25 on a lab VM (Windows Server 2025 Datacenter,
    domain member, service as LocalSystem, SQL Server 2022 on another VM), driven over
    PowerShell Direct, with the upgrade sub-leg; all checks passed (docs/plan-v1.md,
    "M0 durumu"). It installs and removes a Windows service, firewall rules and an event
    log source, so run it only on a throw-away VM, elevated, with -AcknowledgeServiceInstall.
    The machine must not already have a "Talky" service or an HKLM\SOFTWARE\Talky key.

    Script leg: Install-Talky.ps1 (publish output, http-only lab URL on -Port, NodeId
    smoke-script) -> sc.exe qc / qfailure show delayed auto start and three restarts ->
    GET /api/v1/ping returns the node id -> a second install run leaves appsettings.json
    byte-identical and the service running -> Uninstall-Talky.ps1 -RemoveData.

    MSI leg: msiexec /i /qn exits 0 with the service stopped -> sc.exe qc / qfailure show
    delayed auto start and restart after 5 s / 10 s / 30 s; LocalSystem and the default
    --data-dir are registered and remembered under HKLM\SOFTWARE\Talky -> Install-Talky.ps1
    -AclOnly and -ConfigOnly (NodeId smoke-msi) -> talky migrate -> Start-Service -> ping ->
    Stop-Service -> msiexec /x (the remembered values stay).

    MSI upgrade sub-leg (only with -UpgradeMsiPath): msiexec /i of -MsiPath with
    SERVICE_ACCOUNT=NT AUTHORITY\NetworkService and DATADIR=C:\TalkySmokeData -> msiexec /i
    of -UpgradeMsiPath with neither property -> one product of the higher version, same
    account, same --data-dir, recovery policy and delayed start still set -> msiexec /x.
    The service is not started in this sub-leg.

    DATADIR (%ProgramData%\Talky, C:\TalkySmokeData) and HKLM\SOFTWARE\Talky are permanent
    and are left behind; every msiexec call uses /norestart.

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

.PARAMETER UpgradeMsiPath
    Enables the MSI upgrade sub-leg: a second MSI with a higher ProductVersion than
    -MsiPath, e.g. from Build-Msi.ps1 -Version 0.1.1 -SkipPublish. Pass -MsiPath explicitly
    as well, since its default (the newest MSI) may then be the upgrade MSI.

.PARAMETER AcknowledgeServiceInstall
    Required: confirms that this machine may get a Talky service installed and removed.

.EXAMPLE
    .\Test-TalkyInstall.ps1 -AcknowledgeServiceInstall `
        -SqlConnectionString 'Server=sql01;Database=TalkySmoke;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'

.EXAMPLE
    .\Test-TalkyInstall.ps1 -AcknowledgeServiceInstall -SqlConnectionString '...' `
        -MsiPath ..\..\artifacts\Talky.Server-0.1.0.msi -UpgradeMsiPath ..\..\artifacts\Talky.Server-0.1.1.msi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SqlConnectionString,
    [string] $PublishDir,
    [string] $MsiPath,
    [ValidateRange(1024, 65535)][int] $Port = 18080,
    [string] $UpgradeMsiPath,
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
$UpgradeDataDir = 'C:\TalkySmokeData'
$UpgradeAccount = 'NT AUTHORITY\NetworkService'
$RememberKey = 'HKLM:\SOFTWARE\Talky'
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

# Delayed auto start and the 5 s / 10 s / 30 s restart policy, as sc.exe reports them.
function Test-ServiceStartAndRecovery {
    param([Parameter(Mandatory)][string] $When)
    $qc = & "$env:SystemRoot\System32\sc.exe" qc Talky | Out-String
    Write-Check ($qc -match 'AUTO_START\s+\(DELAYED\)') "sc.exe qc: Automatic (Delayed Start) $When"
    $qf = & "$env:SystemRoot\System32\sc.exe" qfailure Talky | Out-String
    $delays = @([regex]::Matches($qf, 'RESTART -- Delay = (\d+)') | ForEach-Object { $_.Groups[1].Value })
    Write-Check (($delays -join '/') -eq '5000/10000/30000' -and $qf -match 'RESET_PERIOD[^:]*:\s*86400') `
        "sc.exe qfailure: restart after 5 s / 10 s / 30 s, reset 86400 s $When (got '$($delays -join '/')')"
}

# Logon account and the --data-dir argument of the registered service.
function Get-TalkyServiceIdentity {
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='Talky'"
    if (-not $svc) { return $null }
    $dataDir = if ($svc.PathName -match '--data-dir\s+"([^"]*)"') { $Matches[1] } else { '' }
    return [pscustomobject]@{ Account = $svc.StartName; DataDir = $dataDir; PathName = $svc.PathName }
}

# DisplayVersion values of the installed "Talky Server" products (ARP entries).
function Get-TalkyProductVersion {
    return @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Talky Server' } |
        ForEach-Object { $_.DisplayVersion })
}

function Get-MsiProductVersion {
    param([Parameter(Mandatory)][string] $Path)
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
    try {
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database,
            @("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        $value = [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        return $value
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
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
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
if (Test-Path -LiteralPath $RememberKey) {
    throw "$RememberKey exists (values remembered by an earlier MSI install); use a clean VM."
}
if ($UpgradeMsiPath) {
    if (-not (Test-Path -LiteralPath $UpgradeMsiPath)) { throw "Upgrade MSI not found: $UpgradeMsiPath" }
    $UpgradeMsiPath = (Resolve-Path -LiteralPath $UpgradeMsiPath).Path
    $baseVersion = [version](Get-MsiProductVersion -Path $MsiPath)
    $upgradeVersion = [version](Get-MsiProductVersion -Path $UpgradeMsiPath)
    if ($upgradeVersion -le $baseVersion) {
        throw "-UpgradeMsiPath must be a higher version than -MsiPath ($upgradeVersion <= $baseVersion); build it with Build-Msi.ps1 -Version <higher> -SkipPublish."
    }
    if (Test-Path -LiteralPath $UpgradeDataDir) { throw "$UpgradeDataDir already exists; use a clean VM." }
}

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
    Test-ServiceStartAndRecovery -When 'after msiexec /i'
    $id = Get-TalkyServiceIdentity
    $expectedDataDir = "$MsiDataDir\."
    Write-Check ($id.Account -eq 'LocalSystem' -and $id.DataDir -eq $expectedDataDir) `
        "default identity: LocalSystem, --data-dir '$expectedDataDir' (got '$($id.Account)', '$($id.DataDir)')"
    $remembered = Get-ItemProperty -LiteralPath $RememberKey -ErrorAction SilentlyContinue
    Write-Check ($null -ne $remembered -and $remembered.ServiceAccount -eq 'LocalSystem' -and $remembered.DataDir -eq "$MsiDataDir\") `
        "$RememberKey remembers LocalSystem and $MsiDataDir\"
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
    Write-Check (Test-Path -LiteralPath $RememberKey) "msiexec /x keeps $RememberKey (permanent, like DATADIR)"
    Assert-Step 'MSI'

    # --- MSI upgrade sub-leg (optional) -------------------------------------------------
    # The upgrade passes neither SERVICE_ACCOUNT nor DATADIR: the remembered values must
    # survive the major upgrade. The service is not started in this sub-leg.
    if ($UpgradeMsiPath) {
        Write-Host "--- MSI upgrade sub-leg ($MsiPath -> $UpgradeMsiPath) ---"
        $log = Join-Path $env:TEMP 'talky-smoke-msi-upgrade-base.log'
        $code = Invoke-Msiexec @('/i', "`"$MsiPath`"", "SERVICE_ACCOUNT=`"$UpgradeAccount`"", "DATADIR=`"$UpgradeDataDir`"",
            '/qn', '/norestart', '/l*v', "`"$log`"")
        Write-Check ($code -eq 0) "msiexec /i base with SERVICE_ACCOUNT='$UpgradeAccount' DATADIR='$UpgradeDataDir' exits 0 (got $code; log $log)"
        Assert-Step 'MSI upgrade'
        $expectedDataDir = "$UpgradeDataDir\."
        $before = Get-TalkyServiceIdentity
        Write-Check ($null -ne $before -and $before.Account -eq $UpgradeAccount -and $before.DataDir -eq $expectedDataDir) `
            "base install: account '$UpgradeAccount', --data-dir '$expectedDataDir' (got '$($before.Account)', '$($before.DataDir)')"
        Test-ServiceStartAndRecovery -When 'after the base install'
        Assert-Step 'MSI upgrade'

        $log = Join-Path $env:TEMP 'talky-smoke-msi-upgrade.log'
        $code = Invoke-Msiexec @('/i', "`"$UpgradeMsiPath`"", '/qn', '/norestart', '/l*v', "`"$log`"")
        Write-Check ($code -eq 0) "msiexec /i upgrade without SERVICE_ACCOUNT/DATADIR exits 0 (got $code; log $log)"
        Assert-Step 'MSI upgrade'
        $versions = @(Get-TalkyProductVersion)
        Write-Check ($versions.Count -eq 1 -and $versions[0] -eq $upgradeVersion.ToString()) `
            "exactly one Talky Server product installed, version $upgradeVersion (got '$($versions -join ', ')')"
        $after = Get-TalkyServiceIdentity
        Write-Check ($null -ne $after -and $after.Account -eq $UpgradeAccount) `
            "upgrade keeps the service account '$UpgradeAccount' (got '$($after.Account)')"
        Write-Check ($null -ne $after -and $after.DataDir -eq $expectedDataDir) `
            "upgrade keeps --data-dir '$expectedDataDir' (got '$($after.DataDir)'; $($after.PathName))"
        Test-ServiceStartAndRecovery -When 'after the upgrade'
        $svc = Get-Service -Name Talky -ErrorAction SilentlyContinue
        Write-Check ($null -ne $svc -and $svc.Status -eq 'Stopped') 'service Talky stopped after the upgrade (the MSI never starts it)'
        Assert-Step 'MSI upgrade'

        $code = Invoke-Msiexec @('/x', "`"$UpgradeMsiPath`"", '/qn', '/norestart')
        Write-Check ($code -eq 0) "msiexec /x upgrade /qn exits 0 (got $code)"
        Write-Check ($null -eq (Get-Service -Name Talky -ErrorAction SilentlyContinue)) 'msiexec /x removed the upgraded service'
        Write-Check (@(Get-TalkyProductVersion).Count -eq 0) 'no Talky Server product left installed'
    }
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
