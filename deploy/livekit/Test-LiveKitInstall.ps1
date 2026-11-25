<#
.SYNOPSIS
    Real-install acceptance smoke for a LiveKit media node: Get-LiveKit.ps1,
    Install-LiveKitService.ps1 (LocalSystem and NetworkService) and a small media check
    through Invoke-LiveKitLoadTest.ps1.

.DESCRIPTION
    STATUS: executed on 2026-11-25 on the lab VM "WEB" (Windows Server 2025 Datacenter,
    2 vCPU / 4 GB, domain member, self-signed lab TURN certificate), driven over
    PowerShell Direct from a clean reset; all checks passed, 144 s in total
    (docs/livekit-windows-spike.md, section 1.2). That is a functional check, not the
    Spike A hardware measurement.

    It installs and removes the LiveKit service and its firewall rules, so run it only on
    a throw-away VM, elevated, with -AcknowledgeServiceInstall. The machine must not
    already have a LiveKit service, a "LiveKit" firewall group or a livekit.yaml in
    InstallDir. It downloads LiveKit, WinSW and lk from their official GitHub releases
    (checksums verified by the scripts) unless the %TEMP% caches already hold them.

    The test reads the livekit.yaml template next to it and writes a filled copy to
    InstallDir itself (node IP, TURN domain, certificate and key paths, key "talky", an API
    secret generated on this machine); the template is never modified.
    The secret is never printed; it reaches lk only through the environment, and the
    livekit.yaml holding it is deleted at the end of leg d.

    Leg a: Get-LiveKit.ps1 (twice: the second run must say "current") -> install as
    LocalSystem -> service Running, Automatic (Delayed Start), restart after 5 s / 10 s /
    30 s with reset 86400 s, livekit-server.exe a High-priority child of the wrapper under
    InstallDir, TCP 7880/7881/5349 listening, UDP 3478 and 7882 bound by it,
    GET http://127.0.0.1:7880/ returns 200 "OK", six enabled rules in the "LiveKit"
    firewall group, livekit.yaml not readable by Users -> second installer run while
    running (new wrapper process, same checks) -> kill livekit-server.exe, recovery
    within 20 s.

    Leg b: Invoke-LiveKitLoadTest.ps1 with 1 video publisher and 4 subscribers for 30 s
    (generator on the node: a functional check, not a measurement) must exit 0, i.e. all
    4 subscribers received the track. Negative: the same script with -SkipRoomCreate
    (room.auto_create is false, so nobody can join) must exit non-zero and say that not
    every participant connected.

    Leg c: re-install as NT AUTHORITY\NetworkService over the LocalSystem install (log
    files created by LocalSystem already exist) -> after 45 s (longer than the 5 s and
    10 s restart delays) the same wrapper process is still running, both processes run as
    NETWORK SERVICE, the node answers, and the System log has no event 7031 for LiveKit
    since the leg started.

    Leg d: Install-LiveKitService.ps1 -Remove -> no service, no rules, no livekit-server.exe
    from InstallDir.

    Every check also requires that no orphaned livekit-server.exe (one whose parent is not
    the running service) exists. Prints one PASS/FAIL line per check and exits 1 on the
    first failed step.

.PARAMETER NodeIp
    Address of this node that clients reach (rtc.node_ip).

.PARAMETER TurnDomain
    TURN domain (turn.domain); must match the certificate.

.PARAMETER CertFile
    PEM certificate for TURN/TLS.

.PARAMETER KeyFile
    PEM private key for TURN/TLS.

.PARAMETER LiveKitVersion
    LiveKit release to install. Default 1.13.6.

.PARAMETER CliVersion
    lk release for the load test. Default 2.18.6.

.PARAMETER InstallDir
    Default C:\Program Files\LiveKit.

.PARAMETER WorkDir
    Where the load test writes its CSV and lk logs. Default %TEMP%\livekit-install-test.

.PARAMETER AcknowledgeServiceInstall
    Required: confirms that this machine may get the LiveKit service installed and removed.

.EXAMPLE
    .\Test-LiveKitInstall.ps1 -AcknowledgeServiceInstall -NodeIp 10.0.0.16 -TurnDomain web.corp.local `
        -CertFile C:\certs\turn-cert.pem -KeyFile C:\certs\turn-key.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $NodeIp,
    [Parameter(Mandatory)][string] $TurnDomain,
    [Parameter(Mandatory)][string] $CertFile,
    [Parameter(Mandatory)][string] $KeyFile,
    [string] $LiveKitVersion = '1.13.6',
    [string] $CliVersion = '2.18.6',
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'LiveKit'),
    [string] $WorkDir = (Join-Path $env:TEMP 'livekit-install-test'),
    [switch] $AcknowledgeServiceInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$GetLiveKit = Join-Path $PSScriptRoot 'Get-LiveKit.ps1'
$Install = Join-Path $PSScriptRoot 'Install-LiveKitService.ps1'
$LoadTest = Join-Path $PSScriptRoot 'Invoke-LiveKitLoadTest.ps1'
$Template = Join-Path $PSScriptRoot 'livekit.yaml'
$ConfigPath = Join-Path $InstallDir 'livekit.yaml'
$ServerExe = Join-Path $InstallDir 'livekit-server.exe'
$LogDir = Join-Path $env:ProgramData 'Talky\logs\livekit'
$NetworkService = 'NT AUTHORITY\NetworkService'
$RootUrl = 'http://127.0.0.1:7880/'
$script:Failed = 0

function Write-Check {
    param([Parameter(Mandatory)][bool] $Condition, [Parameter(Mandatory)][string] $Message)
    if ($Condition) { Write-Host "PASS  $Message" }
    else { Write-Host "FAIL  $Message"; $script:Failed++ }
}

function Assert-Step {
    param([Parameter(Mandatory)][string] $Leg)
    if ($script:Failed -gt 0) { throw "$Leg stopped after a failed check." }
}

function Get-WrapperProcessId {
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='LiveKit'"
    if ($svc -and $svc.State -eq 'Running') { return [int]$svc.ProcessId }
    return 0
}

function Get-ServerProcess {
    $prefix = $InstallDir.TrimEnd('\') + '\'
    return @(Get-CimInstance -ClassName Win32_Process -Filter "Name='livekit-server.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
}

function Get-ProcessOwner {
    param([Parameter(Mandatory)][int] $ProcessId)
    $proc = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$ProcessId"
    if (-not $proc) { return '' }
    $owner = Invoke-CimMethod -InputObject $proc -MethodName GetOwner
    return "$($owner.Domain)\$($owner.User)"
}

function Test-Http {
    param([int] $TimeoutSeconds = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $r = Invoke-WebRequest -Uri $RootUrl -UseBasicParsing -TimeoutSec 5
            if ($r.StatusCode -eq 200 -and "$($r.Content)".Trim() -eq 'OK') { return $true }
        }
        catch { Write-Verbose $_.Exception.Message }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $false
}

# No livekit-server.exe from InstallDir except the one child of the running wrapper.
function Test-NoOrphan {
    param([Parameter(Mandatory)][string] $When)
    $wrapperPid = Get-WrapperProcessId
    $all = @(Get-ServerProcess)
    $orphans = @($all | Where-Object { $wrapperPid -eq 0 -or [int]$_.ParentProcessId -ne $wrapperPid })
    $detail = ($all | ForEach-Object { "$($_.ProcessId) (parent $($_.ParentProcessId))" }) -join ', '
    Write-Check ($orphans.Count -eq 0) "no orphaned livekit-server.exe $When (wrapper $wrapperPid; livekit-server.exe: $(if ($detail) { $detail } else { 'none' }))"
}

# The checks every healthy node passes; returns the wrapper and server process ids.
function Test-Node {
    param([Parameter(Mandatory)][string] $Account, [Parameter(Mandatory)][string] $ProcessOwner, [Parameter(Mandatory)][string] $When)
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='LiveKit'"
    Write-Check ($null -ne $svc -and $svc.State -eq 'Running') "service LiveKit running $When"
    Assert-Step "Check '$When'"
    Write-Check ($svc.StartName -eq $Account) "service account '$Account' (got '$($svc.StartName)')"
    $qc = & "$env:SystemRoot\System32\sc.exe" qc LiveKit | Out-String
    Write-Check ($qc -match 'AUTO_START\s+\(DELAYED\)') 'sc.exe qc: Automatic (Delayed Start)'
    $qf = & "$env:SystemRoot\System32\sc.exe" qfailure LiveKit | Out-String
    $delays = @([regex]::Matches($qf, 'RESTART -- Delay = (\d+)') | ForEach-Object { $_.Groups[1].Value })
    Write-Check (($delays -join '/') -eq '5000/10000/30000' -and $qf -match 'RESET_PERIOD[^:]*:\s*86400') `
        "sc.exe qfailure: restart after 5 s / 10 s / 30 s, reset 86400 s (got '$($delays -join '/')')"

    $wrapperPid = [int]$svc.ProcessId
    $children = @(Get-ServerProcess | Where-Object { [int]$_.ParentProcessId -eq $wrapperPid })
    Write-Check ($children.Count -eq 1) "exactly one livekit-server.exe from $InstallDir is a child of the wrapper ($wrapperPid) (got $($children.Count))"
    Assert-Step "Check '$When'"
    $serverPid = [int]$children[0].ProcessId
    $priority = (Get-Process -Id $serverPid).PriorityClass
    Write-Check ("$priority" -eq 'High') "livekit-server.exe ($serverPid) priority High (got $priority)"
    $owners = @((Get-ProcessOwner -ProcessId $wrapperPid), (Get-ProcessOwner -ProcessId $serverPid))
    Write-Check ($owners[0] -eq $ProcessOwner -and $owners[1] -eq $ProcessOwner) "wrapper and livekit-server.exe run as '$ProcessOwner' (got '$($owners -join "', '")')"

    Write-Check (Test-Http) "GET $RootUrl returns 200 'OK'"
    foreach ($port in 7880, 7881, 5349) {
        $listen = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $serverPid })
        Write-Check ($listen.Count -gt 0) "TCP $port listening (livekit-server.exe)"
    }
    foreach ($port in 3478, 7882) {
        $bound = @(Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $serverPid })
        $addresses = ($bound | ForEach-Object { "$($_.LocalAddress):$port" }) -join ', '
        $ok = $bound.Count -gt 0
        if ($port -eq 7882) { $ok = $ok -and @($bound | Where-Object { $_.LocalAddress -in $NodeIp, '0.0.0.0', '::' }).Count -gt 0 }
        Write-Check $ok "UDP $port bound (livekit-server.exe: $(if ($addresses) { $addresses } else { 'none' }))"
    }
    $rules = @(Get-NetFirewallRule -Group 'LiveKit' -ErrorAction SilentlyContinue)
    $enabled = @($rules | Where-Object { "$($_.Enabled)" -eq 'True' })
    Write-Check ($rules.Count -eq 6 -and $enabled.Count -eq 6) "six enabled rules in firewall group LiveKit (got $($rules.Count), $($enabled.Count) enabled)"
    Test-NoOrphan -When $When
    Assert-Step "Check '$When'"
    return [pscustomobject]@{ WrapperPid = $wrapperPid; ServerPid = $serverPid }
}

# --- preconditions ----------------------------------------------------------------------

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Test-LiveKitInstall.ps1 must run elevated.'
}
if (-not $AcknowledgeServiceInstall) {
    throw 'This script installs and removes the LiveKit service. Run it on a throw-away VM with -AcknowledgeServiceInstall.'
}
if (Get-Service -Name LiveKit -ErrorAction SilentlyContinue) { throw 'A LiveKit service already exists on this machine; use a clean VM.' }
if (Get-NetFirewallRule -Group 'LiveKit' -ErrorAction SilentlyContinue) { throw 'Firewall group LiveKit already exists; use a clean VM.' }
if (Test-Path -LiteralPath $ConfigPath) { throw "$ConfigPath already exists (a real node configuration?); use a clean VM." }
if (@(Get-ServerProcess).Count -gt 0) { throw "livekit-server.exe from $InstallDir is already running; use a clean VM." }
foreach ($file in $CertFile, $KeyFile) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Not found: $file" }
}
$CertFile = (Resolve-Path -LiteralPath $CertFile).Path
$KeyFile = (Resolve-Path -LiteralPath $KeyFile).Path
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

# 32 random bytes as hex; LiveKit wants at least 32 characters.
$bytes = New-Object byte[] 32
$rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
$rng.GetBytes($bytes)
$rng.Dispose()
$secret = -join ($bytes | ForEach-Object { $_.ToString('x2') })

Write-Host "LiveKit install smoke on ${env:COMPUTERNAME}: LiveKit $LiveKitVersion, lk $CliVersion, node $NodeIp, TURN $TurnDomain"
$started = Get-Date
try {
    # --- leg a: Get-LiveKit + install as LocalSystem ------------------------------------
    Write-Host '--- leg a: Get-LiveKit.ps1 + Install-LiveKitService.ps1 as LocalSystem ---'
    & $GetLiveKit -Version $LiveKitVersion -InstallDir $InstallDir
    $reported = (& $ServerExe --version 2>&1 | Out-String).Trim()
    Write-Check ($reported -match [regex]::Escape($LiveKitVersion)) "livekit-server.exe reports $LiveKitVersion (got '$reported')"
    $again = & $GetLiveKit -Version $LiveKitVersion -InstallDir $InstallDir 6>&1 | Out-String
    Write-Check ($again -match '(?m)^current\s') 'second Get-LiveKit.ps1 run reports the binary as current'
    Assert-Step 'Leg a'

    # Fill the template's placeholders. YAML double-quoted strings escape '\'.
    $yamlPath = { param($p) '"' + $p.Replace('\', '\\') + '"' }
    $yaml = [IO.File]::ReadAllText($Template)
    $yaml = $yaml.Replace('"<10.20.30.41>"', "`"$NodeIp`"").Replace('"<media-01.talky.corp>"', "`"$TurnDomain`"")
    $yaml = $yaml.Replace('"<C:\\Program Files\\LiveKit\\turn-cert.pem>"', (& $yamlPath $CertFile))
    $yaml = $yaml.Replace('"<C:\\Program Files\\LiveKit\\turn-key.pem>"', (& $yamlPath $KeyFile))
    $yaml = $yaml.Replace('"<talky>": "<api-secret-at-least-32-chars>"', "`"talky`": `"$secret`"")
    $left = @($yaml -split "`r?`n" | Where-Object { $_ -notmatch '^\s*#' -and $_ -match '"<[^"]*>"' })
    Write-Check ($left.Count -eq 0) "livekit.yaml filled from the template, no placeholder left ($($left.Count) left)"
    [IO.File]::WriteAllText($ConfigPath, $yaml, (New-Object System.Text.UTF8Encoding($false)))
    Assert-Step 'Leg a'

    & $Install -InstallDir $InstallDir -Confirm:$false
    $first = Test-Node -Account 'LocalSystem' -ProcessOwner 'NT AUTHORITY\SYSTEM' -When 'after the first install'
    $acl = (Get-Item -LiteralPath $ConfigPath).GetAccessControl('Access')
    $usersAce = @($acl.Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -in 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0' })
    Write-Check ($acl.AreAccessRulesProtected -and $usersAce.Count -eq 0) 'livekit.yaml (holds the secret): inheritance off, no access for Users / Authenticated Users / Everyone'

    & $Install -InstallDir $InstallDir -Confirm:$false
    $second = Test-Node -Account 'LocalSystem' -ProcessOwner 'NT AUTHORITY\SYSTEM' -When 'after a second installer run while running'
    Write-Check ($second.WrapperPid -ne $first.WrapperPid) "second run re-created the service (wrapper $($first.WrapperPid) -> $($second.WrapperPid))"
    Assert-Step 'Leg a'

    $killedAt = Get-Date
    Stop-Process -Id $second.ServerPid -Force
    $recovered = $false
    while (-not $recovered -and ((Get-Date) - $killedAt).TotalSeconds -lt 20) {
        Start-Sleep -Milliseconds 500
        $wrapperPid = Get-WrapperProcessId
        $child = @(Get-ServerProcess | Where-Object { [int]$_.ParentProcessId -eq $wrapperPid -and [int]$_.ProcessId -ne $second.ServerPid })
        if ($wrapperPid -ne 0 -and $child.Count -eq 1) { $recovered = Test-Http -TimeoutSeconds 2 }
    }
    Write-Check $recovered ("killed livekit-server.exe ({0}); service back and answering after {1:N1} s (limit 20 s)" -f $second.ServerPid, ((Get-Date) - $killedAt).TotalSeconds)
    Assert-Step 'Leg a'
    $null = Test-Node -Account 'LocalSystem' -ProcessOwner 'NT AUTHORITY\SYSTEM' -When 'after the recovery restart'

    # --- leg b: media through the node ---------------------------------------------------
    Write-Host '--- leg b: Invoke-LiveKitLoadTest.ps1 (1 publisher, 4 subscribers, 30 s, generator on the node) ---'
    $env:LIVEKIT_URL = 'ws://127.0.0.1:7880'
    $env:LIVEKIT_API_KEY = 'talky'
    $env:LIVEKIT_API_SECRET = $secret
    & $LoadTest -Room smoke -VideoPublishers 1 -Subscribers 4 -Duration 30s -OutputDir $WorkDir -CliVersion $CliVersion -Confirm:$false
    Write-Check ($LASTEXITCODE -eq 0) "load test passed: 4/4 subscribers received the track (exit $LASTEXITCODE)"
    Assert-Step 'Leg b'

    # Negative: nobody can join a room that was not created, so the script must fail. Its
    # output is indented so that its FAIL lines are not mistaken for this test's.
    $negative = & $LoadTest -Room smoke-negative -SkipRoomCreate -VideoPublishers 1 -Subscribers 1 -Duration 5s `
        -OutputDir $WorkDir -CliVersion $CliVersion -Confirm:$false *>&1 | ForEach-Object { "$_" }
    $negativeExit = $LASTEXITCODE
    $negative | ForEach-Object { Write-Host "  | $_" }
    $negativeText = $negative -join "`n"
    Write-Check ($negativeExit -ne 0 -and $negativeText -match '(?m)^FAIL\s+every participant connected' -and $negativeText -notmatch '(?m)^PASS\s+peak host CPU') `
        "load test into a room that does not exist fails: participants not connected, CPU not judged (exit $negativeExit)"
    Assert-Step 'Leg b'

    # --- leg c: NetworkService over the LocalSystem install ------------------------------
    Write-Host "--- leg c: re-install as $NetworkService (log files from the LocalSystem run exist) ---"
    $legStart = (Get-Date).AddSeconds(-1)
    & $Install -InstallDir $InstallDir -ServiceAccount $NetworkService -Confirm:$false
    $afterInstall = Get-WrapperProcessId
    Write-Host 'wait     45 s (longer than the 5 s and 10 s restart delays)'
    Start-Sleep -Seconds 45
    $ns = Test-Node -Account $NetworkService -ProcessOwner 'NT AUTHORITY\NETWORK SERVICE' -When "45 s after the $NetworkService install"
    Write-Check ($ns.WrapperPid -eq $afterInstall) "same wrapper process for 45 s, no restart ($afterInstall -> $($ns.WrapperPid))"
    $crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; Id = 7031; StartTime = $legStart } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'LiveKit' })
    Write-Check ($crashes.Count -eq 0) "System log: no event 7031 for LiveKit since the leg started (got $($crashes.Count))"
    $dotnet = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime'; StartTime = $legStart } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'livekit-service' })
    Write-Check ($dotnet.Count -eq 0) "Application log: no .NET Runtime error for livekit-service.exe since the leg started (got $($dotnet.Count))"
    $logAce = @((Get-Item -LiteralPath $LogDir).GetAccessControl('Access').Access | Where-Object {
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-20' -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify })
    Write-Check ($logAce.Count -gt 0) "$LogDir grants NETWORK SERVICE Modify"
    Assert-Step 'Leg c'

    # --- leg d: removal -------------------------------------------------------------------
    Write-Host '--- leg d: Install-LiveKitService.ps1 -Remove ---'
    & $Install -InstallDir $InstallDir -Remove -Confirm:$false
    Write-Check ($null -eq (Get-Service -Name LiveKit -ErrorAction SilentlyContinue)) '-Remove: no LiveKit service'
    Write-Check (@(Get-NetFirewallRule -Group 'LiveKit' -ErrorAction SilentlyContinue).Count -eq 0) '-Remove: no rules in firewall group LiveKit'
    Write-Check (@(Get-ServerProcess).Count -eq 0) "-Remove: no livekit-server.exe from $InstallDir running"
    Write-Check (@(Get-Process -Name 'livekit-service' -ErrorAction SilentlyContinue).Count -eq 0) '-Remove: no livekit-service.exe running'
    Remove-Item -LiteralPath $ConfigPath -Force
    Write-Host "removed  $ConfigPath (it held this run's generated secret)"
}
catch {
    Write-Host "FAIL  $($_.Exception.Message)"
    $script:Failed++
}
finally {
    foreach ($name in 'LIVEKIT_URL', 'LIVEKIT_API_KEY', 'LIVEKIT_API_SECRET') { Remove-Item -Path "Env:\$name" -ErrorAction SilentlyContinue }
    $secret = $null
}

Write-Host ''
Write-Host ("Duration {0:N0} s. Logs: $LogDir (livekit-service.err.log is LiveKit's own log), load test output in $WorkDir." -f ((Get-Date) - $started).TotalSeconds)
if ($script:Failed -gt 0) {
    Write-Host "LiveKit install smoke FAILED ($($script:Failed) failure(s)). See also the System (Service Control Manager) and Application (.NET Runtime) event logs."
    exit 1
}
Write-Host 'LiveKit install smoke PASSED.'
exit 0
