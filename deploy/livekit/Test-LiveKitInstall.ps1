<#
.SYNOPSIS
    Real-install acceptance smoke for a LiveKit media node: Get-LiveKit.ps1,
    Install-LiveKitService.ps1 (LocalSystem and NetworkService) and a small media check
    through Invoke-LiveKitLoadTest.ps1.

.DESCRIPTION
    STATUS: executed on 2026-11-25 on a lab VM (Windows Server 2025 Datacenter,
    2 vCPU / 4 GB, domain member, self-signed lab TURN certificate), driven over
    PowerShell Direct from a clean reset, in this form (116 checks) with LiveKit 1.13.11
    and 1.13.6: all checks passed (docs/livekit-windows-spike.md, sections 1.5 and 1.6).
    That is a functional check, not the Spike A hardware measurement.

    It installs and removes the LiveKit service and its firewall rules, so run it only on
    a throw-away VM, elevated, with -AcknowledgeServiceInstall. The machine must not
    already have a LiveKit service, a "LiveKit" firewall group or a livekit.yaml in
    InstallDir. It downloads LiveKit, WinSW and lk from their official GitHub releases
    (checksums verified by the scripts) unless the %TEMP% caches already hold them.

    The test reads the livekit.yaml template next to it and writes a filled copy to
    InstallDir itself (node IP, TURN domain, certificate and key paths, key "yanki", an API
    secret generated on this machine); the template is never modified.
    The secret is never printed; it reaches lk only through the environment, and the
    livekit.yaml holding it is deleted at the end, whether the run passed or failed, together
    with the two key copies the test puts into InstallDir (-KeyFile itself is only read).

    Leg a: Get-LiveKit.ps1 (twice: the second run must say "current") -> the key the node
    uses is a copy inside InstallDir with explicit Read entries for Users, Authenticated
    Users, Everyone and both app-package groups (asserted) -> install with -NoStart: service registered and
    Stopped, six firewall rules created -> failing start: the rules are removed and
    turn.cert_file names a missing file; the installer must fail with "did not stay up ...
    no restart pending", livekit-service.out.log must name the file, the six rules must
    exist again (created before the start), the recovery actions must be back to
    5/10/30 s, and 35 s later the service is Stopped with no LiveKit process and no event
    7031/7034 -> install as LocalSystem -> service Running, Automatic (Delayed Start),
    restart after 5 s / 10 s / 30 s with reset 86400 s, livekit-server.exe a High-priority
    child of the wrapper under InstallDir, TCP 7880/7881/5349 listening, UDP 3478 and 7882
    bound by it, GET http://127.0.0.1:7880/ returns 200 "OK", six enabled rules in the
    "LiveKit" firewall group, livekit.yaml not readable by Users / Authenticated Users /
    Everyone, none of the five groups left on the key, the default log directory with
    inheritance off and only SYSTEM and Administrators -> second installer run while
    running: LiveKit got Ctrl+C ("exit requested, shutting down" in its log, not a hard
    kill), new wrapper process, same checks -> kill livekit-server.exe, recovery within
    20 s.

    Leg b: Invoke-LiveKitLoadTest.ps1 with 1 video publisher and 4 subscribers for 30 s
    and -SkipCpuCheck (generator on the node: a functional check, not a measurement) must
    exit 0, i.e. all 4 subscribers received the track. Negative: the same script with
    -SkipRoomCreate (room.auto_create is false, so nobody can join) must exit non-zero and
    say that not every participant connected.

    Leg c: the node switches to a key moved into InstallDir from a folder without Users
    (unprotected, no broad entry visible: asserted; where Windows protects a moved file,
    as Server 2025 does, that stale DACL is written directly) -> re-install as
    NT AUTHORITY\NetworkService over the LocalSystem install (LiveKit got Ctrl+C; log
    files created by LocalSystem already exist) -> after 45 s (longer than the 5 s and
    10 s restart delays) the same wrapper process is still running, both processes run as
    NETWORK SERVICE, the node answers, the System log has no event 7031 for LiveKit since
    the leg started, and the moved key is protected with no broad entry -> re-install as
    LocalSystem (LiveKit got Ctrl+C): running as LocalSystem, no entry for NETWORK SERVICE
    left on the log directory or the TURN files, the moved key still closed.

    Leg d: Install-LiveKitService.ps1 -Remove -> LiveKit got Ctrl+C, no service, no rules,
    no livekit-server.exe from InstallDir.

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
    LiveKit release to install. Default 1.13.11, the same as Get-LiveKit.ps1's default.

.PARAMETER CliVersion
    lk release for the load test. Default 2.18.6.

.PARAMETER InstallDir
    Default C:\Program Files\LiveKit.

.PARAMETER WorkDir
    Where the load test writes its CSV and lk logs. Default %TEMP%\livekit-install-test.

.PARAMETER AcknowledgeServiceInstall
    Required: confirms that this machine may get the LiveKit service installed and removed.

.EXAMPLE
    .\Test-LiveKitInstall.ps1 -AcknowledgeServiceInstall -NodeIp 192.0.2.16 -TurnDomain media-01.corp.local `
        -CertFile C:\certs\turn-cert.pem -KeyFile C:\certs\turn-key.pem

.NOTES
    Windows PowerShell 5.1 (Desktop edition) only, like Install-LiveKitService.ps1: the ACL
    checks use FileInfo.GetAccessControl, which only .NET Framework has.
#>
#Requires -PSEdition Desktop
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $NodeIp,
    [Parameter(Mandatory)][string] $TurnDomain,
    [Parameter(Mandatory)][string] $CertFile,
    [Parameter(Mandatory)][string] $KeyFile,
    [string] $LiveKitVersion = '1.13.11',
    [string] $CliVersion = '2.18.6',
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'LiveKit'),
    [string] $WorkDir = (Join-Path $env:TEMP 'livekit-install-test'),
    [switch] $AcknowledgeServiceInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Absolute with backslashes, as Install-LiveKitService.ps1 does: process paths are compared as text.
$InstallDir = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDir))
if ($InstallDir -ne [IO.Path]::GetPathRoot($InstallDir)) { $InstallDir = $InstallDir.TrimEnd('\') }
$GetLiveKit = Join-Path $PSScriptRoot 'Get-LiveKit.ps1'
$Install = Join-Path $PSScriptRoot 'Install-LiveKitService.ps1'
$LoadTest = Join-Path $PSScriptRoot 'Invoke-LiveKitLoadTest.ps1'
$Template = Join-Path $PSScriptRoot 'livekit.yaml'
$ConfigPath = Join-Path $InstallDir 'livekit.yaml'
$ServerExe = Join-Path $InstallDir 'livekit-server.exe'
$LogDir = Join-Path $env:ProgramData 'Yanki\logs\livekit'
$ErrLog = Join-Path $LogDir 'livekit-service.err.log'
$OutLog = Join-Path $LogDir 'livekit-service.out.log'
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

# Allow entries (explicit or inherited) on a file or directory for the given SIDs.
function Get-AllowEntry {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string[]] $Sid)
    return @((Get-Item -LiteralPath $Path).GetAccessControl('Access').GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -in $Sid })
}
$BroadGroups = 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0'   # Users, Authenticated Users, Everyone
$AppPackages = 'S-1-15-2-1', 'S-1-15-2-2'             # ALL (RESTRICTED) APPLICATION PACKAGES

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

# Writes InstallDir\livekit.yaml from the template with this run's values. YAML
# double-quoted strings escape '\'.
function Write-NodeConfig {
    param([Parameter(Mandatory)][string] $Cert, [Parameter(Mandatory)][string] $Key)
    $quote = { param($p) '"' + $p.Replace('\', '\\') + '"' }
    $yaml = [IO.File]::ReadAllText($Template)
    $yaml = $yaml.Replace('"<10.20.30.41>"', "`"$NodeIp`"").Replace('"<media-01.yanki.corp>"', "`"$TurnDomain`"")
    $yaml = $yaml.Replace('"<C:\\Program Files\\LiveKit\\turn-cert.pem>"', (& $quote $Cert))
    $yaml = $yaml.Replace('"<C:\\Program Files\\LiveKit\\turn-key.pem>"', (& $quote $Key))
    $yaml = $yaml.Replace('"<yanki>": "<api-secret-at-least-32-chars>"', "`"yanki`": `"$secret`"")
    [IO.File]::WriteAllText($ConfigPath, $yaml, (New-Object System.Text.UTF8Encoding($false)))
    return @($yaml -split "`r?`n" | Where-Object { $_ -notmatch '^\s*#' -and $_ -match '"<[^"]*>"' }).Count
}

# Text appended to a log file since $From (the wrapper keeps it open, so share it).
function Read-LogSince {
    param([Parameter(Mandatory)][string] $Path, [long] $From = 0)
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try { [void]$fs.Seek([Math]::Min($From, $fs.Length), 'Begin'); return (New-Object IO.StreamReader($fs)).ReadToEnd() }
    finally { $fs.Dispose() }
}
function Get-LogLength { param([string] $Path) if (Test-Path -LiteralPath $Path) { (Get-Item -LiteralPath $Path).Length } else { 0 } }

# A stop of a running node must reach LiveKit as Ctrl+C: LiveKit then logs this line and
# drains. A hard kill (WinSW stopping conhost.exe first) leaves no line at all.
function Test-GracefulStop {
    param([Parameter(Mandatory)][long] $From, [Parameter(Mandatory)][string] $When)
    $text = Read-LogSince -Path $ErrLog -From $From
    Write-Check ($text -match '"msg":"exit requested, shutting down"') "LiveKit got Ctrl+C and shut down cleanly $When ('exit requested, shutting down' in livekit-service.err.log)"
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
$keyA = $null
$keyB = $null
try {
    # --- leg a: Get-LiveKit + install as LocalSystem ------------------------------------
    Write-Host '--- leg a: Get-LiveKit.ps1 + Install-LiveKitService.ps1 as LocalSystem ---'
    & $GetLiveKit -Version $LiveKitVersion -InstallDir $InstallDir
    $reported = (& $ServerExe --version 2>&1 | Out-String).Trim()
    Write-Check ($reported -match ('(^|\s)v?' + [regex]::Escape($LiveKitVersion) + '$')) "livekit-server.exe reports $LiveKitVersion (got '$reported')"
    $again = & $GetLiveKit -Version $LiveKitVersion -InstallDir $InstallDir 6>&1 | Out-String
    Write-Check ($again -match '(?m)^current\s') 'second Get-LiveKit.ps1 run reports the binary as current'
    Assert-Step 'Leg a'

    # The key the node uses in legs a and b is a copy inside InstallDir with an explicit Read
    # entry for each of the five groups the key check names (whatever InstallDir passes on), so
    # the check cannot pass unless the installer removes them. The finally block deletes it.
    $keyA = Join-Path $InstallDir 'turn-key-test.pem'
    Copy-Item -LiteralPath $KeyFile -Destination $keyA -Force
    $keyAcl = (Get-Item -LiteralPath $keyA).GetAccessControl('Access')
    foreach ($sid in $BroadGroups + $AppPackages) {
        $keyAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'Read', 'Allow')))
    }
    (Get-Item -LiteralPath $keyA).SetAccessControl($keyAcl)
    $present = @(Get-AllowEntry -Path $keyA -Sid ($BroadGroups + $AppPackages) | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
    Write-Check ($present.Count -eq 5) "precondition: the test key $keyA grants Users, Authenticated Users, Everyone and both app-package groups ($($present.Count) of 5)"
    $left = Write-NodeConfig -Cert $CertFile -Key $keyA
    Write-Check ($left -eq 0) "livekit.yaml filled from the template, no placeholder left ($left left)"
    Assert-Step 'Leg a'

    # -NoStart first, as on a new node.
    & $Install -InstallDir $InstallDir -NoStart -Confirm:$false
    $svc = Get-Service -Name LiveKit -ErrorAction SilentlyContinue
    $rules = @(Get-NetFirewallRule -Group 'LiveKit' -ErrorAction SilentlyContinue)
    Write-Check ($null -ne $svc -and $svc.Status -eq 'Stopped' -and $rules.Count -eq 6) `
        "install -NoStart: service registered and Stopped, six firewall rules created (status $(if ($svc) { $svc.Status } else { 'absent' }), $($rules.Count) rules)"
    Assert-Step 'Leg a'

    # Failing start on the fresh node: the only automated guard of "rules before the start"
    # (they are removed first, so the failing run must create them itself) and of the
    # failed-start path ending the SCM restart loop.
    Write-Host '--- leg a: failing start (turn.cert_file names a missing file) ---'
    Get-NetFirewallRule -Group 'LiveKit' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop
    $null = Write-NodeConfig -Cert (Join-Path $InstallDir 'missing-turn-cert.pem') -Key $keyA
    $outFrom = Get-LogLength $OutLog
    $failure = ''
    $failStart = Get-Date
    try { & $Install -InstallDir $InstallDir -Confirm:$false }
    catch { $failure = $_.Exception.Message }
    $returned = Get-Date
    Write-Check ($failure -match 'did not stay up' -and $failure -match 'no restart pending') `
        ("installer fails with 'did not stay up ... no restart pending' after {0:N0} s (got: '{1}')" -f ($returned - $failStart).TotalSeconds, $(if ($failure.Length -gt 160) { $failure.Substring(0, 160) + '...' } else { $failure }))
    Write-Check ((Read-LogSince -Path $OutLog -From $outFrom) -match 'missing-turn-cert\.pem') 'livekit-service.out.log names the missing certificate (the reason the failure message points to)'
    $rules = @(Get-NetFirewallRule -Group 'LiveKit' -ErrorAction SilentlyContinue)
    Write-Check ($rules.Count -eq 6) "six firewall rules exist although the start failed: created before the start ($($rules.Count) rules)"
    $qf = & "$env:SystemRoot\System32\sc.exe" qfailure LiveKit | Out-String
    $delays = @([regex]::Matches($qf, 'RESTART -- Delay = (\d+)') | ForEach-Object { $_.Groups[1].Value })
    Write-Check (($delays -join '/') -eq '5000/10000/30000') "recovery actions restored after the failed start (got '$($delays -join '/')')"
    Write-Host 'wait     35 s (longer than the longest restart delay, 30 s)'
    Start-Sleep -Seconds 35
    $svc = Get-Service -Name LiveKit -ErrorAction SilentlyContinue
    $procs = @(Get-ServerProcess) + @(Get-Process -Name 'livekit-service' -ErrorAction SilentlyContinue)
    $restarts = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; Id = 7031, 7034; StartTime = $returned } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'LiveKit' })
    Write-Check ($null -ne $svc -and $svc.Status -eq 'Stopped' -and $procs.Count -eq 0 -and $restarts.Count -eq 0) `
        "35 s after the failed start: service registered and Stopped, no LiveKit process, no event 7031/7034 since the installer returned (status $(if ($svc) { $svc.Status } else { 'absent' }), processes $($procs.Count), events $($restarts.Count))"
    $null = Write-NodeConfig -Cert $CertFile -Key $keyA
    Assert-Step 'Leg a'

    Write-Host '--- leg a: install as LocalSystem ---'
    & $Install -InstallDir $InstallDir -Confirm:$false
    $first = Test-Node -Account 'LocalSystem' -ProcessOwner 'NT AUTHORITY\SYSTEM' -When 'after the first install'
    $acl = (Get-Item -LiteralPath $ConfigPath).GetAccessControl('Access')
    $usersAce = @(Get-AllowEntry -Path $ConfigPath -Sid $BroadGroups)
    Write-Check ($acl.AreAccessRulesProtected -and $usersAce.Count -eq 0) 'livekit.yaml (holds the secret): inheritance off, no access for Users / Authenticated Users / Everyone'
    $keyAce = @(Get-AllowEntry -Path $keyA -Sid ($BroadGroups + $AppPackages))
    Write-Check ($keyAce.Count -eq 0) "TURN private key: the 5 groups present before the install are gone ($($keyAce.Count) entries left)"
    $logAcl = (Get-Item -LiteralPath $LogDir).GetAccessControl('Access')
    $logSids = @($logAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
    Write-Check ($logAcl.AreAccessRulesProtected -and @($logSids | Where-Object { $_ -notin 'S-1-5-18', 'S-1-5-32-544' }).Count -eq 0) `
        "$LogDir (default): inheritance off, only SYSTEM and Administrators (got $($logSids -join ', '))"

    $errFrom = Get-LogLength $ErrLog
    & $Install -InstallDir $InstallDir -Confirm:$false
    Test-GracefulStop -From $errFrom -When 'when the installer re-created the running service'
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
    $env:LIVEKIT_API_KEY = 'yanki'
    $env:LIVEKIT_API_SECRET = $secret
    # -SkipCpuCheck: the spike's CPU threshold means nothing with the generator on a small VM.
    & $LoadTest -Room smoke -VideoPublishers 1 -Subscribers 4 -Duration 30s -OutputDir $WorkDir -CliVersion $CliVersion -SkipCpuCheck -Confirm:$false
    Write-Check ($LASTEXITCODE -eq 0) "load test passed: 4/4 subscribers received the track (exit $LASTEXITCODE)"
    Assert-Step 'Leg b'

    # Negative: nobody can join a room that was not created, so the script must fail. Its
    # output is indented so that its FAIL lines are not mistaken for this test's.
    $negative = & $LoadTest -Room smoke-negative -SkipRoomCreate -VideoPublishers 1 -Subscribers 1 -Duration 5s `
        -OutputDir $WorkDir -CliVersion $CliVersion -Confirm:$false *>&1 | ForEach-Object { "$_" }
    $negativeExit = $LASTEXITCODE
    $negative | ForEach-Object { Write-Host "  | $_" }
    $negativeText = $negative -join "`n"
    Write-Check ($negativeExit -ne 0 -and $negativeText -match '(?m)^FAIL\s+every participant connected' -and $negativeText -notmatch '(?m)^(PASS|FAIL)\s+peak host CPU') `
        "load test into a room that does not exist fails: participants not connected, CPU not judged (exit $negativeExit)"
    Assert-Step 'Leg b'

    # --- leg c: NetworkService over the LocalSystem install ------------------------------
    Write-Host "--- leg c: re-install as $NetworkService (log files from the LocalSystem run exist; moved key) ---"
    # A key moved into InstallDir from a folder without Users keeps its old DACL: no broad
    # entry is visible, but the next write of an unprotected DACL re-applies Program Files'
    # Users and app-package entries. The NetworkService run writes it (Read for the account).
    $staging = Join-Path $WorkDir 'key-staging'
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $keyB = Join-Path $InstallDir 'turn-key-moved.pem'
    [IO.File]::WriteAllBytes((Join-Path $staging 'turn-key-moved.pem'), [IO.File]::ReadAllBytes($KeyFile))
    Move-Item -LiteralPath (Join-Path $staging 'turn-key-moved.pem') -Destination $keyB -Force
    if ((Get-Item -LiteralPath $keyB).GetAccessControl('Access').AreAccessRulesProtected) {
        # Some Windows builds (Server 2025 in the lab) turn a moved file's inherited entries
        # into explicit ones and protect the DACL, so the move alone does not leave the
        # stale state. Write that state as a move leaves it elsewhere: unprotected, the
        # staging folder's entries flagged inherited. SetFileSecurity stores the DACL as
        # given; the .NET and SetNamedSecurityInfo paths would re-apply the parent's entries.
        Add-Type -Namespace YankiTest -Name Native -MemberDefinition @'
[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool SetFileSecurity(string fileName, int securityInformation, byte[] securityDescriptor);
'@
        $sd = New-Object Security.AccessControl.RawSecurityDescriptor('D:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)')
        $bytes = New-Object byte[] $sd.BinaryLength
        $sd.GetBinaryForm($bytes, 0)
        if (-not [YankiTest.Native]::SetFileSecurity($keyB, 4, $bytes)) { throw "SetFileSecurity failed ($([Runtime.InteropServices.Marshal]::GetLastWin32Error()))." }
        Write-Host "moved    $keyB (this Windows protected the moved file; stale unprotected DACL written directly)"
    }
    $movedAcl = (Get-Item -LiteralPath $keyB).GetAccessControl('Access')
    $parentInherits = @((Get-Item -LiteralPath $InstallDir).GetAccessControl('Access').GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' -and ($_.InheritanceFlags -band [Security.AccessControl.InheritanceFlags]::ObjectInherit) })
    Write-Check (-not $movedAcl.AreAccessRulesProtected -and @(Get-AllowEntry -Path $keyB -Sid ($BroadGroups + $AppPackages)).Count -eq 0 -and $parentInherits.Count -gt 0) `
        'precondition: moved key not protected, no broad entry visible, InstallDir passes Users on to files'
    $null = Write-NodeConfig -Cert $CertFile -Key $keyB
    Assert-Step 'Leg c'
    $legStart = (Get-Date).AddSeconds(-1)
    $errFrom = Get-LogLength $ErrLog
    & $Install -InstallDir $InstallDir -ServiceAccount $NetworkService -Confirm:$false
    Test-GracefulStop -From $errFrom -When 'when the installer stopped the LocalSystem service to switch to NetworkService'
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
    $keyBAce = @(Get-AllowEntry -Path $keyB -Sid ($BroadGroups + $AppPackages))
    Write-Check ((Get-Item -LiteralPath $keyB).GetAccessControl('Access').AreAccessRulesProtected -and $keyBAce.Count -eq 0) `
        "moved TURN private key after the NetworkService install: inheritance off, no broad entry ($($keyBAce.Count) entries)"
    Assert-Step 'Leg c'

    # Back to LocalSystem: the entries added for NETWORK SERVICE must go again.
    $errFrom = Get-LogLength $ErrLog
    & $Install -InstallDir $InstallDir -Confirm:$false
    Test-GracefulStop -From $errFrom -When 'when the installer stopped the NetworkService service to go back to LocalSystem'
    $null = Test-Node -Account 'LocalSystem' -ProcessOwner 'NT AUTHORITY\SYSTEM' -When 'after going back to LocalSystem'
    foreach ($path in $LogDir, $CertFile, $keyB) {
        $left = @(Get-AllowEntry -Path $path -Sid 'S-1-5-20')
        Write-Check ($left.Count -eq 0) "no entry for NETWORK SERVICE left on $path after going back to LocalSystem ($($left.Count) left)"
    }
    $keyBAce = @(Get-AllowEntry -Path $keyB -Sid ($BroadGroups + $AppPackages))
    Write-Check ($keyBAce.Count -eq 0) "moved TURN private key still closed after the clean-up ($($keyBAce.Count) entries)"
    Assert-Step 'Leg c'

    # --- leg d: removal -------------------------------------------------------------------
    Write-Host '--- leg d: Install-LiveKitService.ps1 -Remove ---'
    $errFrom = Get-LogLength $ErrLog
    & $Install -InstallDir $InstallDir -Remove -Confirm:$false
    Test-GracefulStop -From $errFrom -When 'when -Remove stopped the running service'
    Write-Check ($null -eq (Get-Service -Name LiveKit -ErrorAction SilentlyContinue)) '-Remove: no LiveKit service'
    Write-Check (@(Get-NetFirewallRule -Group 'LiveKit' -ErrorAction SilentlyContinue).Count -eq 0) '-Remove: no rules in firewall group LiveKit'
    Write-Check (@(Get-ServerProcess).Count -eq 0) "-Remove: no livekit-server.exe from $InstallDir running"
    Write-Check (@(Get-Process -Name 'livekit-service' -ErrorAction SilentlyContinue).Count -eq 0) '-Remove: no livekit-service.exe running'
}
catch {
    Write-Host "FAIL  $($_.Exception.Message)"
    $script:Failed++
}
finally {
    foreach ($name in 'LIVEKIT_URL', 'LIVEKIT_API_KEY', 'LIVEKIT_API_SECRET') { Remove-Item -Path "Env:\$name" -ErrorAction SilentlyContinue }
    $secret = $null
    # The generated secret and the key copies (one of them opened to everyone until the first
    # install closes it) go in every exit path.
    $copies = @($ConfigPath, $keyA, $keyB | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($copies) {
        Remove-Item -LiteralPath $copies -Force
        Write-Host "removed  $($copies -join ', ') (this run's generated secret and the test's key copies)"
    }
}

Write-Host ''
Write-Host ("Duration {0:N0} s. Logs: $LogDir (livekit-service.err.log is LiveKit's own log), load test output in $WorkDir." -f ((Get-Date) - $started).TotalSeconds)
if ($script:Failed -gt 0) {
    Write-Host "LiveKit install smoke FAILED ($($script:Failed) failure(s)). See also the System (Service Control Manager) and Application (.NET Runtime) event logs."
    exit 1
}
Write-Host 'LiveKit install smoke PASSED.'
exit 0
