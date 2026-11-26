<#
.SYNOPSIS
    Checks that a Windows Server host is ready to run a Yanki node.

.DESCRIPTION
    Read-only. Runs the checks below and prints one line per check with a
    Pass / Warn / Fail result, then exits 1 if anything failed.

      OS              Windows Server 2022 (build 20348) or later, x64
      PowerShell      5.1 or later
      Elevation       running elevated (needed by Install-Yanki.ps1)
      ServiceAccount  gMSA resolvable with Test-ADServiceAccount (needs RSAT AD module)
      LogonAsService  the service account holds SeServiceLogonRight
      Sql             reachable with System.Data.SqlClient, version and edition reported
      Ports           8443 (and 8080 with -EnableHttp) are free. The cluster mesh
                      port (9443) is not checked until the mesh exists (M2).
      Certificate     thumbprint present in LocalMachine\My, has a private key, not expired
      FileShare       the SMB/DFS share is reachable and writable
      TimeSync        w32tm reports a synchronised, non-local time source
      Disk            at least 20 GB free on the data volume

    Skipped checks (no parameter supplied, module missing) report Skip and never fail
    the run.

.PARAMETER ServiceAccount
    Account the service will run as: a gMSA such as 'CORP\yanki$', or 'LocalSystem'
    for a lab. Drives the ServiceAccount and LogonAsService checks.

.PARAMETER SqlConnectionString
    Connection string used for the Sql check. Nothing is written to the database.

.PARAMETER CertificateThumbprint
    TLS certificate thumbprint expected in LocalMachine\My.

.PARAMETER FileShare
    UNC path of the SMB/DFS share used by IBlobStore, e.g. \\corp\dfs\yanki\files.

.PARAMETER DataDir
    Data directory whose volume is checked for free space. Default %ProgramData%\Yanki.

.PARAMETER HttpsPort
    Client/API port that must be free. Default 8443.

.PARAMETER HttpPort
    Plain HTTP port; only checked with -EnableHttp. Default 8080.

.PARAMETER EnableHttp
    Also require the plain HTTP port to be free.

.PARAMETER Json
    Emit the results as JSON on the success stream instead of a table.

.EXAMPLE
    .\Test-YankiPrerequisites.ps1

.EXAMPLE
    .\Test-YankiPrerequisites.ps1 -ServiceAccount 'CORP\yanki$' `
        -SqlConnectionString 'Server=sql-ag.corp;Database=Yanki;Integrated Security=true;Encrypt=true' `
        -CertificateThumbprint AABBCC... -FileShare \\corp\dfs\yanki\files

.NOTES
    Read-only: no -WhatIf because nothing is changed. PowerShell 5.1 compatible.
#>
[CmdletBinding()]
param(
    [string] $ServiceAccount,
    [string] $SqlConnectionString,
    [string] $CertificateThumbprint,
    [string] $FileShare,
    [string] $DataDir = (Join-Path $env:ProgramData 'Yanki'),
    [ValidateRange(1, 65535)][int] $HttpsPort = 8443,
    [ValidateRange(1, 65535)][int] $HttpPort = 8080,
    [switch] $EnableHttp,
    [switch] $Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Results = New-Object System.Collections.Generic.List[psobject]

function Add-Result {
    param(
        [Parameter(Mandatory)][string] $Check,
        [Parameter(Mandatory)][ValidateSet('Pass', 'Warn', 'Fail', 'Skip')][string] $Status,
        [Parameter(Mandatory)][string] $Detail
    )
    $script:Results.Add([pscustomobject]@{ Check = $Check; Status = $Status; Detail = $Detail })
}

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# --- OS -------------------------------------------------------------------------------

$os = Get-CimInstance Win32_OperatingSystem
$build = [int]($os.BuildNumber)
# ProductType 3 = server, 1 = workstation.
$isServer = ($os.ProductType -eq 3)
$osDetail = "$($os.Caption) build $build ($($os.OSArchitecture))"
if (-not $isServer) {
    Add-Result -Check 'OS' -Status 'Warn' -Detail "$osDetail - client Windows: fine for a lab, production requires Windows Server 2022/2025."
}
elseif ($build -lt 20348) {
    Add-Result -Check 'OS' -Status 'Fail' -Detail "$osDetail - Yanki requires Windows Server 2022 (build 20348) or later."
}
else {
    Add-Result -Check 'OS' -Status 'Pass' -Detail $osDetail
}

if ([Environment]::Is64BitOperatingSystem) {
    Add-Result -Check 'Architecture' -Status 'Pass' -Detail 'x64'
}
else {
    Add-Result -Check 'Architecture' -Status 'Fail' -Detail 'yanki.exe is published win-x64 only.'
}

# --- Shell ---------------------------------------------------------------------------

$psv = $PSVersionTable.PSVersion
if ($psv.Major -ge 5) {
    Add-Result -Check 'PowerShell' -Status 'Pass' -Detail "$psv"
}
else {
    Add-Result -Check 'PowerShell' -Status 'Fail' -Detail "$psv - the deployment scripts need PowerShell 5.1 or later."
}

if (Test-Elevated) {
    Add-Result -Check 'Elevation' -Status 'Pass' -Detail 'Running elevated.'
}
else {
    Add-Result -Check 'Elevation' -Status 'Warn' -Detail 'Not elevated. Install-Yanki.ps1 needs an elevated session.'
}

# --- Service account ------------------------------------------------------------------

if (-not $ServiceAccount) {
    Add-Result -Check 'ServiceAccount' -Status 'Skip' -Detail 'No -ServiceAccount supplied.'
}
elseif ($ServiceAccount -in @('LocalSystem', 'NT AUTHORITY\SYSTEM', 'LocalService', 'NetworkService')) {
    Add-Result -Check 'ServiceAccount' -Status 'Warn' -Detail "$ServiceAccount - built-in account. Production nodes must run under a gMSA."
}
elseif ($ServiceAccount -notlike '*$') {
    Add-Result -Check 'ServiceAccount' -Status 'Warn' -Detail "$ServiceAccount does not end with '$' - a gMSA is written as DOMAIN\name$."
}
elseif (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
    Add-Result -Check 'ServiceAccount' -Status 'Skip' -Detail 'RSAT ActiveDirectory module not installed; cannot run Test-ADServiceAccount.'
}
else {
    try {
        Import-Module ActiveDirectory -ErrorAction Stop
        # Test-ADServiceAccount takes the SAM name without the domain prefix or trailing $.
        $sam = ($ServiceAccount -split '\\')[-1].TrimEnd('$')
        if (Test-ADServiceAccount -Identity $sam) {
            Add-Result -Check 'ServiceAccount' -Status 'Pass' -Detail "$ServiceAccount is installed on this host and its password can be retrieved."
        }
        else {
            Add-Result -Check 'ServiceAccount' -Status 'Fail' -Detail "Test-ADServiceAccount failed for $ServiceAccount. Run Install-ADServiceAccount -Identity $sam and check the gMSA's PrincipalsAllowedToRetrieveManagedPassword."
        }
    }
    catch {
        Add-Result -Check 'ServiceAccount' -Status 'Fail' -Detail "Test-ADServiceAccount error: $($_.Exception.Message)"
    }
}

# --- Log on as a service --------------------------------------------------------------

if (-not $ServiceAccount -or $ServiceAccount -eq 'LocalSystem') {
    Add-Result -Check 'LogonAsService' -Status 'Skip' -Detail 'Not applicable for LocalSystem.'
}
else {
    $seceditFile = Join-Path $env:TEMP ("yanki-secpol-" + [guid]::NewGuid().ToString('N') + '.inf')
    try {
        # Read-only export of the local security policy.
        $null = & "$env:SystemRoot\System32\secedit.exe" /export /cfg $seceditFile /areas USER_RIGHTS
        $line = Select-String -Path $seceditFile -Pattern '^SeServiceLogonRight' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $line) {
            Add-Result -Check 'LogonAsService' -Status 'Warn' -Detail 'SeServiceLogonRight not found in the local policy export.'
        }
        else {
            $sam = ($ServiceAccount -split '\\')[-1]
            $sids = @()
            try {
                $account = New-Object Security.Principal.NTAccount($ServiceAccount)
                $sids += $account.Translate([Security.Principal.SecurityIdentifier]).Value
            }
            catch {
                Write-Verbose "Could not translate $ServiceAccount to a SID: $($_.Exception.Message)"
            }
            $granted = $false
            foreach ($token in ($line.Line -split '=')[-1] -split ',') {
                $t = $token.Trim().TrimStart('*')
                if ($t -eq $ServiceAccount -or $t -eq $sam -or ($sids -contains $t)) { $granted = $true }
            }
            if ($granted) {
                Add-Result -Check 'LogonAsService' -Status 'Pass' -Detail "$ServiceAccount holds SeServiceLogonRight."
            }
            else {
                Add-Result -Check 'LogonAsService' -Status 'Warn' -Detail "$ServiceAccount does not hold 'Log on as a service' locally. Grant it by GPO or with secedit before starting the service."
            }
        }
    }
    catch {
        Add-Result -Check 'LogonAsService' -Status 'Warn' -Detail "Could not read the local security policy: $($_.Exception.Message)"
    }
    finally {
        Remove-Item -LiteralPath $seceditFile -Force -ErrorAction SilentlyContinue
    }
}

# --- SQL Server -----------------------------------------------------------------------

if (-not $SqlConnectionString) {
    Add-Result -Check 'Sql' -Status 'Skip' -Detail 'No -SqlConnectionString supplied.'
}
else {
    $connection = $null
    try {
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($SqlConnectionString)
        if ($builder.ConnectTimeout -gt 15 -or $builder.ConnectTimeout -eq 0) { $builder.ConnectTimeout = 15 }
        $connection = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString)
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(64))"
        $reader = $command.ExecuteReader()
        $null = $reader.Read()
        $major = [int]$reader.GetValue(0)
        $edition = [string]$reader.GetValue(1)
        $level = [string]$reader.GetValue(2)
        $reader.Close()
        $detail = "connected to $($builder.DataSource), major version $major, $edition, $level"
        # SQL Server 2022 = 16, 2025 = 17.
        if ($major -ge 17) { Add-Result -Check 'Sql' -Status 'Pass' -Detail $detail }
        elseif ($major -ge 16) { Add-Result -Check 'Sql' -Status 'Warn' -Detail "$detail - Yanki targets SQL Server 2025 (major 17)." }
        else { Add-Result -Check 'Sql' -Status 'Fail' -Detail "$detail - SQL Server 2022 or later required." }
    }
    catch {
        Add-Result -Check 'Sql' -Status 'Fail' -Detail "Cannot reach SQL Server: $($_.Exception.Message)"
    }
    finally {
        if ($connection -and $connection.State -ne 'Closed') { $connection.Close() }
    }
}

# --- Ports ----------------------------------------------------------------------------

$portsToCheck = @(
    @{ Port = $HttpsPort; Purpose = 'clients and API' }
)
if ($EnableHttp) { $portsToCheck += @{ Port = $HttpPort; Purpose = 'plain HTTP' } }

foreach ($entry in $portsToCheck) {
    $listener = Get-NetTCPConnection -LocalPort $entry.Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($listener) {
        $owner = (Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue)
        $ownerName = if ($owner) { "$($owner.ProcessName) (pid $($owner.Id))" } else { "pid $($listener.OwningProcess)" }
        if ($ownerName -match 'yanki') {
            Add-Result -Check "Port $($entry.Port)" -Status 'Warn' -Detail "already held by $ownerName - an existing Yanki node; upgrades stop the service first."
        }
        else {
            Add-Result -Check "Port $($entry.Port)" -Status 'Fail' -Detail "TCP $($entry.Port) ($($entry.Purpose)) is in use by $ownerName."
        }
    }
    else {
        Add-Result -Check "Port $($entry.Port)" -Status 'Pass' -Detail "TCP $($entry.Port) ($($entry.Purpose)) is free."
    }
}

# --- TLS certificate ------------------------------------------------------------------

if (-not $CertificateThumbprint) {
    Add-Result -Check 'Certificate' -Status 'Skip' -Detail 'No -CertificateThumbprint supplied.'
}
else {
    $thumb = ($CertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = Get-ChildItem -Path Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
    if (-not $cert) {
        Add-Result -Check 'Certificate' -Status 'Fail' -Detail "No certificate with thumbprint $thumb in LocalMachine\My."
    }
    elseif (-not $cert.HasPrivateKey) {
        Add-Result -Check 'Certificate' -Status 'Fail' -Detail "Certificate $thumb has no private key; Kestrel cannot use it."
    }
    elseif ($cert.NotAfter -lt (Get-Date)) {
        Add-Result -Check 'Certificate' -Status 'Fail' -Detail "Certificate $thumb expired on $($cert.NotAfter.ToString('yyyy-MM-dd'))."
    }
    elseif ($cert.NotAfter -lt (Get-Date).AddDays(30)) {
        Add-Result -Check 'Certificate' -Status 'Warn' -Detail "$($cert.Subject) expires on $($cert.NotAfter.ToString('yyyy-MM-dd')) (less than 30 days)."
    }
    else {
        Add-Result -Check 'Certificate' -Status 'Pass' -Detail "$($cert.Subject), valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))."
    }
}

# --- File share -----------------------------------------------------------------------

if (-not $FileShare) {
    Add-Result -Check 'FileShare' -Status 'Skip' -Detail 'No -FileShare supplied.'
}
elseif (-not (Test-Path -LiteralPath $FileShare)) {
    Add-Result -Check 'FileShare' -Status 'Fail' -Detail "$FileShare is not reachable from this host."
}
else {
    $probe = Join-Path $FileShare ('.yanki-probe-' + [guid]::NewGuid().ToString('N'))
    try {
        Set-Content -LiteralPath $probe -Value 'yanki' -ErrorAction Stop
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        Add-Result -Check 'FileShare' -Status 'Pass' -Detail "$FileShare is reachable and writable by the current identity."
    }
    catch {
        Add-Result -Check 'FileShare' -Status 'Warn' -Detail "$FileShare is reachable but not writable by the current identity (the service account may still be): $($_.Exception.Message)"
    }
}

# --- Time synchronisation -------------------------------------------------------------

try {
    $w32 = & "$env:SystemRoot\System32\w32tm.exe" /query /status 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Add-Result -Check 'TimeSync' -Status 'Warn' -Detail 'w32tm /query /status failed; check the Windows Time service.'
    }
    elseif ($w32 -match 'Local CMOS Clock' -or $w32 -match 'Free-running System Clock') {
        # Only a hard failure on a domain member: Kerberos rejects a clock skew above 5 minutes.
        $domainJoined = (Get-CimInstance Win32_ComputerSystem).PartOfDomain
        $status = if ($domainJoined) { 'Fail' } else { 'Warn' }
        Add-Result -Check 'TimeSync' -Status $status -Detail 'Time source is the local clock. Kerberos (and readable log correlation across nodes) needs a domain time source.'
    }
    else {
        $source = ($w32 -split "`n" | Where-Object { $_ -match '^\s*Source:' } | Select-Object -First 1)
        Add-Result -Check 'TimeSync' -Status 'Pass' -Detail (($source -replace '\s+', ' ').Trim())
    }
}
catch {
    Add-Result -Check 'TimeSync' -Status 'Warn' -Detail "Could not query the time service: $($_.Exception.Message)"
}

# --- Disk space -----------------------------------------------------------------------

try {
    $root = [IO.Path]::GetPathRoot((Resolve-Path -LiteralPath (Split-Path -Parent $DataDir) -ErrorAction SilentlyContinue |
                Select-Object -First 1 -ExpandProperty Path -ErrorAction SilentlyContinue))
    if (-not $root) { $root = [IO.Path]::GetPathRoot($DataDir) }
    $drive = Get-PSDrive -Name $root.TrimEnd(':\/') -ErrorAction Stop
    $freeGb = [math]::Round($drive.Free / 1GB, 1)
    if ($freeGb -lt 20) {
        Add-Result -Check 'Disk' -Status 'Fail' -Detail "$root has $freeGb GB free; logs, the Lucene index and SQL backups need at least 20 GB."
    }
    elseif ($freeGb -lt 100) {
        Add-Result -Check 'Disk' -Status 'Warn' -Detail "$root has $freeGb GB free; a search node wants an NVMe volume with far more."
    }
    else {
        Add-Result -Check 'Disk' -Status 'Pass' -Detail "$root has $freeGb GB free."
    }
}
catch {
    Add-Result -Check 'Disk' -Status 'Warn' -Detail "Could not determine free space for $DataDir : $($_.Exception.Message)"
}

# --- Report ---------------------------------------------------------------------------

if ($Json) {
    $script:Results | ConvertTo-Json -Depth 3
}
else {
    $script:Results | Format-Table -AutoSize -Wrap
    $failed = @($script:Results | Where-Object { $_.Status -eq 'Fail' })
    $warned = @($script:Results | Where-Object { $_.Status -eq 'Warn' })
    Write-Host ''
    Write-Host ("{0} checks: {1} pass, {2} warn, {3} fail." -f `
            $script:Results.Count,
        @($script:Results | Where-Object { $_.Status -eq 'Pass' }).Count,
        $warned.Count, $failed.Count)
}

if (@($script:Results | Where-Object { $_.Status -eq 'Fail' }).Count -gt 0) { exit 1 }
