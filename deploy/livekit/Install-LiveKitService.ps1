<#
.SYNOPSIS
    Registers the LiveKit SFU as a Windows service using WinSW, and opens its ports.

.DESCRIPTION
    LiveKit ships a console executable, so it is wrapped with WinSW
    (github.com/winsw/winsw), which gives it the same service behaviour as talky.exe:
    delayed automatic start, restart after 5 s / 10 s / 30 s and rolling logs.

    Steps:
      1. download WinSW-x64.exe from its official release and place it in InstallDir as
         livekit-service.exe (WinSW reads the .xml with the same base name). WinSW
         publishes no checksums file and the binary is unsigned, so its SHA-256 is
         pinned in this script and verified after the download;
      2. render deploy\livekit\livekit.xml into InstallDir\livekit-service.xml;
      3. copy the deploy\livekit\livekit.yaml template next to the binary if no
         livekit.yaml is there yet (an existing file is never overwritten - it holds the
         node's keys). The operator fills in that copy; the template in the repository
         stays unfilled;
      4. set the file permissions the service account needs (see -ServiceAccount);
      5. `livekit-service.exe install`;
      6. add the inbound firewall rules in the "LiveKit" group, before any start, so a node
         whose first start fails still has them once the operator fixes the configuration
         and starts the service:
            UDP 7882  media (SRTP/ICE)
            UDP 3478  embedded TURN
            UDP 30000-40000 TURN relay allocations (turn.relay_range_* in livekit.yaml)
            TCP 7881  media over TCP for UDP-blocked clients
            TCP 7880  signalling (reverse-proxied by talky.exe; scope it to the API nodes)
            TCP 5349  TURN over TLS
      7. unless -NoStart, `livekit-service.exe start`. The start only counts once the same
         wrapper process and its livekit-server.exe child are still running a few seconds
         after the service reported Running. If not (including when Running is never
         seen within 30 s), the script ends any SCM restart loop - it removes the recovery
         actions, stops the service until it has stayed Stopped for longer than the longest
         restart delay, and restores the recovery actions - stops orphaned processes and
         fails. The service stays registered.

    How LiveKit reports a start-up error (rejected configuration, missing TURN certificate,
    port in use): one plain line on stdout, i.e. in livekit-service.out.log; its JSON log
    (stderr) is livekit-service.err.log. Up to 1.13.9 the process then exits 0, so the
    service just stops (no event 7031, no restart). From 1.13.10 it exits 1, so the SCM
    logs event 7031 and restarts it after 5 s / 10 s / 30 s.

    Idempotent: an already installed service is reconfigured and restarted rather than
    duplicated; existing firewall rules are updated in place.

    WinSW does not tie livekit-server.exe to its own lifetime: when the wrapper dies, the
    child keeps running and keeps the ports. After the service is stopped or uninstalled
    (re-create, -Remove, a failed start) every livekit-server.exe whose executable lies
    under InstallDir and whose parent is not the running service is stopped. LiveKit
    processes started from other directories are never touched.

.PARAMETER InstallDir
    Directory holding livekit-server.exe. Default C:\Program Files\LiveKit.

.PARAMETER LogDir
    Service log directory. Default %ProgramData%\Talky\logs\livekit.

.PARAMETER ServiceAccount
    'LocalSystem' (default) or an account such as 'NT AUTHORITY\NetworkService' or a gMSA
    ('CORP\livekit$'). NetworkService is the hardened choice. For any account the script
    sets, on every run:
      - livekit.yaml (it holds the API secret): inheritance removed, SYSTEM and
        Administrators Full Control, plus Read for the service account if it is not
        LocalSystem. Ordinary users can no longer read the secret;
      - LogDir: Modify for the service account, inherited by files and subfolders, so it
        can append to and roll log files that an earlier LocalSystem run or the elevated
        `livekit-service.exe install` created (without it the wrapper dies with
        UnauthorizedAccessException on livekit-service.err.log);
      - turn.cert_file and turn.key_file named in livekit.yaml (a relative path is taken
        relative to InstallDir, as LiveKit does): Read for the service account, as one
        explicit allow entry;
      - turn.key_file only, for every account including LocalSystem: if Users,
        Authenticated Users, Everyone, ALL APPLICATION PACKAGES or ALL RESTRICTED
        APPLICATION PACKAGES have an allow entry on the private key, the file stops
        inheriting (its inherited entries are kept as explicit ones) and the allow entries
        of those groups are removed. Every other entry stays.
    WinSW itself grants a non-LocalSystem account the "Log on as a service" right at
    `install` (<allowservicelogon> in livekit.xml); a LocalSystem run grants nothing beyond
    the livekit.yaml ACL and the key-file restriction.
    When -ServiceAccount differs from the account the existing service runs as, the entries
    this script added for the previous account (Modify on LogDir, Read on the TURN files)
    are removed again. Its "Log on as a service" right is not revoked: other services may
    rely on it.

.PARAMETER WinSwVersion
    WinSW release to download. Default 2.12.0. The WinSW project publishes neither a
    checksums file nor an Authenticode signature on WinSW-x64.exe, so the expected SHA-256
    is pinned in this script (see $WinSwSha256). Changing the version without updating that
    hash is refused.

.PARAMETER WinSwSha256
    Expected SHA-256 of WinSW-x64.exe. Override only when you have independently verified a
    different release; the default covers 2.12.0.

.PARAMETER SignalRemoteAddress
    Remote addresses allowed to reach TCP 7880. Default 'LocalSubnet'. In production this
    should be the Talky API nodes only, since clients reach signalling through
    https://talky.corp/media/{node}/rtc.

.PARAMETER NoStart
    Install the service but leave it stopped.

.PARAMETER SkipFirewall
    Do not touch the firewall.

.PARAMETER Remove
    Stop, uninstall and unregister the service, stop any livekit-server.exe left running
    from InstallDir and remove the firewall rules. Files in InstallDir and LogDir stay.

.EXAMPLE
    .\Install-LiveKitService.ps1 -WhatIf

.EXAMPLE
    .\Install-LiveKitService.ps1 -SignalRemoteAddress 10.20.30.0/24

.EXAMPLE
    .\Install-LiveKitService.ps1 -Remove

.NOTES
    Requires an elevated Windows PowerShell 5.1 session (Desktop edition). PowerShell 7 is
    refused by #Requires: the ACL code uses FileInfo.GetAccessControl/SetAccessControl,
    which only .NET Framework has.
    Order on a new node: Get-LiveKit.ps1; this script with -NoStart, which copies the
    template to <InstallDir>\livekit.yaml; fill in that copy (node_ip, turn.domain,
    certificate paths, keys); run this script again without -NoStart, which grants the
    service account read on the TURN files the copy names and starts the service. Never
    fill in deploy\livekit\livekit.yaml in the repository checkout.
#>
#Requires -PSEdition Desktop
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'LiveKit'),
    [string] $LogDir = (Join-Path $env:ProgramData 'Talky\logs\livekit'),
    [string] $ServiceAccount = 'LocalSystem',
    [string] $WinSwVersion = '2.12.0',
    [string] $WinSwSha256 = '05b82d46ad331cc16bdc00de5c6332c1ef818df8ceefcd49c726553209b3a0da',
    [string[]] $SignalRemoteAddress = @('LocalSubnet'),
    [switch] $NoStart,
    [switch] $SkipFirewall,
    [switch] $Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Absolute, with backslashes: process paths (Win32_Process.ExecutablePath) are compared
# against InstallDir as text, so 'D:/LiveKit' or '.\LiveKit' must not slip through as typed.
$InstallDir = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDir)).TrimEnd('\')
$LogDir = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogDir)).TrimEnd('\')

$ServiceName = 'LiveKit'
$FirewallGroup = 'LiveKit'
$WrapperExe = Join-Path $InstallDir 'livekit-service.exe'
$WrapperXml = Join-Path $InstallDir 'livekit-service.xml'
$ServerExe = Join-Path $InstallDir 'livekit-server.exe'
$ConfigTarget = Join-Path $InstallDir 'livekit.yaml'
$WinSwCache = Join-Path $env:TEMP 'winsw-download'
# A crash shows up a few seconds after Running (the wrapper opens its log files and
# starts livekit-server.exe only then); 8 s covers that with margin.
$StartSettleSeconds = 8
# How long `start` may take to report Running. A LiveKit that exits at once is Running for
# well under a second and is usually never seen; the failure path handles that.
$StartTimeoutSeconds = 30
# The restart ladder of livekit.xml (5 s / 10 s / 30 s, reset after 1 day) in sc.exe
# syntax; Stop-ServiceUntilQuiet removes it while it waits and restores exactly this. Keep
# both in step with livekit.xml.
$RecoveryActions = 'restart/5000/restart/10000/restart/30000'
$RecoveryResetSeconds = 86400
# After a failed start the service only counts as quiet once it stayed Stopped for longer
# than the longest restart delay, since a restart queued before the actions were removed
# may still fire.
$RestartLadderMaxSeconds = 30

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Ports = @(
    @{ Name = 'LiveKit - media (UDP 7882)'; Protocol = 'UDP'; Port = 7882; Remote = @('Any')
       Description = 'SRTP/ICE media between clients and the SFU.' }
    @{ Name = 'LiveKit - TURN (UDP 3478)'; Protocol = 'UDP'; Port = 3478; Remote = @('Any')
       Description = 'Embedded TURN relay for restricted clients.' }
    @{ Name = 'LiveKit - TURN relay range (UDP 30000-40000)'; Protocol = 'UDP'; Port = '30000-40000'; Remote = @('Any')
       Description = 'Per-allocation relay ports; must match turn.relay_range_* in livekit.yaml. Without this TURN negotiates but no media flows.' }
    @{ Name = 'LiveKit - media over TCP (TCP 7881)'; Protocol = 'TCP'; Port = 7881; Remote = @('Any')
       Description = 'Media fallback for clients whose network blocks UDP.' }
    @{ Name = 'LiveKit - TURN over TLS (TCP 5349)'; Protocol = 'TCP'; Port = 5349; Remote = @('Any')
       Description = 'TURN over TLS, the last-resort path through strict proxies.' }
    @{ Name = 'LiveKit - signalling (TCP 7880)'; Protocol = 'TCP'; Port = 7880; Remote = $SignalRemoteAddress
       Description = 'WebSocket signalling; reverse-proxied by talky.exe under /media/{node}/rtc.' }
)

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Set-LiveKitFirewallRule {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Description,
        [Parameter(Mandatory)][ValidateSet('TCP', 'UDP')][string] $Protocol,
        [Parameter(Mandatory)][string] $Port,
        [string[]] $RemoteAddress = @('Any')
    )
    $existing = Get-NetFirewallRule -DisplayName $Name -ErrorAction SilentlyContinue
    $verb = if ($existing) { 'Update' } else { 'Create' }
    if (-not $PSCmdlet.ShouldProcess($Name, "$verb inbound rule ($Protocol $Port)")) { return }
    if ($existing) {
        # The group is set at creation; -Group cannot be combined with -DisplayName here.
        Set-NetFirewallRule -DisplayName $Name -Description $Description `
            -Direction Inbound -Action Allow -Protocol $Protocol -LocalPort $Port `
            -RemoteAddress $RemoteAddress -Profile Domain -Enabled True
        Write-Host "updated  $Name"
    }
    else {
        New-NetFirewallRule -DisplayName $Name -Group $FirewallGroup -Description $Description `
            -Direction Inbound -Action Allow -Protocol $Protocol -LocalPort $Port `
            -RemoteAddress $RemoteAddress -Profile Domain -Enabled True | Out-Null
        Write-Host "created  $Name"
    }
}

# Runs one WinSW command and throws on a non-zero exit code.
function Invoke-WinSw {
    param([Parameter(Mandatory)][string] $Command)
    & $WrapperExe $Command 2>&1 | Write-Verbose
    if ($LASTEXITCODE -ne 0) { throw "WinSW $Command failed with exit code $LASTEXITCODE." }
}

# 'uninstall' only marks the service for deletion; the SCM removes it once every handle
# to it is closed, and 'install' fails while it still exists.
function Wait-ServiceDeleted {
    $deadline = (Get-Date).AddSeconds(60)
    while (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        if ((Get-Date) -ge $deadline) {
            throw ("Service $ServiceName is still registered 60 s after 'WinSW uninstall' (marked for " +
                "deletion while something holds a handle to it, e.g. services.msc). Close it and run this script again.")
        }
        Start-Sleep -Milliseconds 500
    }
}

# Process id of the running service wrapper, or 0.
function Get-WrapperProcessId {
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
    if ($svc -and $svc.State -eq 'Running') { return [int]$svc.ProcessId }
    return 0
}

# livekit-server.exe processes started from InstallDir (never those from elsewhere).
function Get-InstalledServerProcess {
    $prefix = $InstallDir.TrimEnd('\') + '\'
    return @(Get-CimInstance -ClassName Win32_Process -Filter "Name='livekit-server.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
}

# Stops every livekit-server.exe from InstallDir that is not the child of the running
# service wrapper. A wrapper that crashes leaves its child behind, still holding the ports.
function Stop-OrphanedServer {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param()
    $wrapperPid = Get-WrapperProcessId
    foreach ($proc in (Get-InstalledServerProcess)) {
        if ($wrapperPid -ne 0 -and [int]$proc.ParentProcessId -eq $wrapperPid) { continue }
        if ($PSCmdlet.ShouldProcess("livekit-server.exe ($($proc.ProcessId))", 'Stop orphaned process')) {
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $proc.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
            Write-Host "stopped  orphaned livekit-server.exe ($($proc.ProcessId), parent $($proc.ParentProcessId) gone)"
        }
    }
}

# Ends the SCM restart loop of a failing service and returns $true once it is quiet.
# A wrapper whose LiveKit exits at once is Running for well under a second per restart and
# a stop sent outside that window is dropped, so the loop is not raced: the recovery
# actions are removed first (a restart queued before that may still fire once but cannot
# queue another), the service is stopped whenever it is seen starting or running, and it
# counts as quiet after staying Stopped for longer than the longest restart delay. The
# recovery actions are restored in every case.
function Stop-ServiceUntilQuiet {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param()
    if (-not $PSCmdlet.ShouldProcess($ServiceName, 'Stop the service and end its restart loop')) { return $true }
    $sc = "$env:SystemRoot\System32\sc.exe"
    $quietSeconds = $RestartLadderMaxSeconds + 5
    $deadline = (Get-Date).AddSeconds(4 * $quietSeconds)
    try {
        # actions= "" means "no action"; Windows PowerShell drops a bare "" argument, hence '""'.
        & $sc failure $ServiceName reset= 0 actions= '""' 2>&1 | Write-Verbose
        if ($LASTEXITCODE -ne 0) { Write-Warning "sc.exe failure (remove the recovery actions) exited $LASTEXITCODE; a restart may still be scheduled." }
        $stoppedSince = $null
        while ((Get-Date) -lt $deadline) {
            $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if (-not $svc) { return $true }
            if ($svc.Status -eq 'Stopped') {
                if (-not $stoppedSince) { $stoppedSince = Get-Date }
                if (((Get-Date) - $stoppedSince).TotalSeconds -ge $quietSeconds) {
                    Write-Host "stopped  service $ServiceName (stayed Stopped for $quietSeconds s; no restart pending)"
                    return $true
                }
            }
            else {
                $stoppedSince = $null
                if ($svc.Status -in 'Running', 'StartPending', 'Paused') {
                    # sc.exe fails harmlessly while the service cannot accept a stop yet; retry.
                    & $sc stop $ServiceName 2>&1 | Write-Verbose
                }
            }
            Start-Sleep -Milliseconds 250
        }
        Write-Warning "Service $ServiceName did not stay Stopped within $([int](4 * $quietSeconds)) s."
        return $false
    }
    finally {
        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
            & $sc failure $ServiceName reset= $RecoveryResetSeconds actions= $RecoveryActions 2>&1 | Write-Verbose
            if ($LASTEXITCODE -ne 0) {
                Write-Warning ("sc.exe failure (restore the recovery actions) exited $LASTEXITCODE; run this script again so " +
                    "the restart policy 5 s / 10 s / 30 s is set.")
            }
        }
    }
}

function Resolve-AccountSid {
    param([Parameter(Mandatory)][string] $Account)
    # The built-in service accounts go by several spellings; use their well-known SIDs.
    switch -Regex ($Account) {
        '^(NT AUTHORITY\\)?(NetworkService|Network Service)$' { return New-Object Security.Principal.SecurityIdentifier('S-1-5-20') }
        '^(NT AUTHORITY\\)?(LocalService|Local Service)$' { return New-Object Security.Principal.SecurityIdentifier('S-1-5-19') }
    }
    return (New-Object Security.Principal.NTAccount($Account)).Translate([Security.Principal.SecurityIdentifier])
}

# Adds one allow entry for $Sid to the DACL of a file or directory; existing entries stay.
function Add-AllowRule {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][Security.Principal.SecurityIdentifier] $Sid,
        [Parameter(Mandatory)][Security.AccessControl.FileSystemRights] $Rights
    )
    $item = Get-Item -LiteralPath $Path
    $inherit = if ($item.PSIsContainer) { 'ContainerInherit, ObjectInherit' } else { 'None' }
    $acl = $item.GetAccessControl('Access')
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($Sid, $Rights, $inherit, 'None', 'Allow')))
    # Setting the directory DACL also propagates the inheritable entry to existing files.
    $item.SetAccessControl($acl)
}

# Removes from the DACL exactly the rights of one allow entry this script adds (the
# counterpart of Add-AllowRule); other rights of that account stay.
function Remove-AllowRule {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][Security.Principal.SecurityIdentifier] $Sid,
        [Parameter(Mandatory)][Security.AccessControl.FileSystemRights] $Rights
    )
    $item = Get-Item -LiteralPath $Path
    $inherit = if ($item.PSIsContainer) { 'ContainerInherit, ObjectInherit' } else { 'None' }
    $acl = $item.GetAccessControl('Access')
    # RemoveAccessRule also returns $true when nothing matched; look first.
    $present = @($acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -eq $Sid.Value -and ($_.FileSystemRights -band $Rights) -ne 0 })
    if ($present.Count -eq 0) { return $false }
    [void]$acl.RemoveAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($Sid, $Rights, $inherit, 'None', 'Allow')))
    $item.SetAccessControl($acl)
    return $true
}

# The TURN private key must not be readable by ordinary users. If Users, Authenticated
# Users, Everyone or the two AppContainer groups (ALL APPLICATION PACKAGES, ALL RESTRICTED
# APPLICATION PACKAGES; Program Files grants both read) have an allow entry, stop
# inheritance (keeping the inherited entries as explicit ones) and remove those groups'
# allow entries; everything else stays. Returns $true when the DACL was changed.
function Protect-PrivateKeyFile {
    param([Parameter(Mandatory)][string] $Path)
    $broad = 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0', 'S-1-15-2-1', 'S-1-15-2-2'
    $item = Get-Item -LiteralPath $Path
    $acl = $item.GetAccessControl('Access')
    $grants = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -in $broad })
    if ($grants.Count -eq 0) { return $false }
    $acl.SetAccessRuleProtection($true, $true)
    # Persist and re-read so the formerly inherited entries are explicit and removable.
    $item.SetAccessControl($acl)
    $acl = $item.GetAccessControl('Access')
    foreach ($sid in $broad) {
        $acl.RemoveAccessRuleAll((New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($sid)), 'Read', 'Allow')))
    }
    $item.SetAccessControl($acl)
    return $true
}

# turn.cert_file / turn.key_file from livekit.yaml, unless still placeholders or empty, as
# objects with Kind ('cert_file' or 'key_file') and an absolute Path. LiveKit runs with
# InstallDir as its working directory, so a relative path is relative to InstallDir.
function Get-TurnFilePath {
    param([Parameter(Mandatory)][string] $ConfigPath)
    $files = @()
    foreach ($line in (Get-Content -LiteralPath $ConfigPath)) {
        if ($line -notmatch '^\s+(?<k>cert_file|key_file):\s*(?<v>.+?)\s*$') { continue }
        $kind = $Matches['k']
        $value = $Matches['v']
        if ($value -match '^"(?<s>(?:[^"\\]|\\.)*)"') { $value = $Matches['s'].Replace('\\', '\') }  # YAML double-quoted
        elseif ($value -match "^'(?<s>(?:[^']|'')*)'") { $value = $Matches['s'].Replace("''", "'") }  # YAML single-quoted
        else { $value = ($value -split '\s+#', 2)[0].Trim() }
        if (-not $value -or $value.StartsWith('<')) { continue }
        if (-not [IO.Path]::IsPathRooted($value)) { $value = Join-Path $InstallDir $value }
        $files += [pscustomobject]@{ Kind = $kind; Path = [IO.Path]::GetFullPath($value) }
    }
    return $files
}

# --- preconditions --------------------------------------------------------------------

if (-not (Test-Elevated)) {
    # A dry run only reads, so it stays useful from an unelevated shell.
    if ($WhatIfPreference) {
        Write-Warning 'Not elevated. Continuing because -WhatIf was given; a real run needs an elevated session.'
    }
    else {
        throw 'Install-LiveKitService.ps1 must run in an elevated PowerShell session.'
    }
}

# --- removal path ---------------------------------------------------------------------

if ($Remove) {
    if (Test-Path -LiteralPath $WrapperExe) {
        if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and uninstall the WinSW service')) {
            $current = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if ($current) {
                # 'stop' only requests the stop; 'stopwait' returns once the service is Stopped.
                if ($current.Status -ne 'Stopped') { Invoke-WinSw stopwait }
                Invoke-WinSw uninstall
                Wait-ServiceDeleted
                Write-Host "removed  service $ServiceName"
            }
            else {
                Write-Host "absent   service $ServiceName"
            }
        }
    }
    else {
        Write-Host "absent   $WrapperExe"
    }
    Stop-OrphanedServer
    if (-not $SkipFirewall) {
        foreach ($rule in @(Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue)) {
            if ($PSCmdlet.ShouldProcess($rule.DisplayName, 'Remove inbound rule')) {
                Remove-NetFirewallRule -DisplayName $rule.DisplayName
                Write-Host "removed  $($rule.DisplayName)"
            }
        }
    }
    Write-Host ''
    Write-Host "Removed. livekit-server.exe and livekit.yaml were left in $InstallDir."
    return
}

if (-not (Test-Path -LiteralPath $ServerExe) -and -not $WhatIfPreference) {
    throw "livekit-server.exe not found in $InstallDir. Run Get-LiveKit.ps1 first."
}

# --- 1. WinSW ------------------------------------------------------------------------

if ((Test-Path -LiteralPath $WrapperExe)) {
    Write-Host "present  $WrapperExe"
}
elseif ($PSCmdlet.ShouldProcess($WrapperExe, "Download WinSW $WinSwVersion")) {
    if (-not (Test-Path -LiteralPath $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }
    # The download is cached like Get-LiveKit.ps1 does; the hash is checked on every use.
    $cached = Join-Path $WinSwCache "WinSW-x64-$WinSwVersion.exe"
    $verb = 'cached  '
    if (-not (Test-Path -LiteralPath $cached)) {
        New-Item -ItemType Directory -Path $WinSwCache -Force | Out-Null
        $url = "https://github.com/winsw/winsw/releases/download/v$WinSwVersion/WinSW-x64.exe"
        Invoke-WebRequest -Uri $url -OutFile $cached -UseBasicParsing -Headers @{ 'User-Agent' = 'talky-deploy' }
        $verb = 'fetched '
    }
    # WinSW ships no checksums file and WinSW-x64.exe is not Authenticode signed, so the
    # only integrity check available is a hash pinned here. Verify a new release by hand
    # (Get-FileHash) before changing $WinSwSha256. The installed copy is what gets hashed:
    # a file swapped in the cache after a check could otherwise still be installed.
    Copy-Item -LiteralPath $cached -Destination $WrapperExe
    $actual = (Get-FileHash -LiteralPath $WrapperExe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $WinSwSha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $WrapperExe, $cached -Force -ErrorAction SilentlyContinue
        throw "SHA-256 mismatch for WinSW-x64.exe ${WinSwVersion}: expected $WinSwSha256, got $actual. The installed copy and the cached file were deleted."
    }
    Write-Host ("{0} WinSW-x64.exe {1} ({2:N1} MB), SHA-256 verified" -f $verb, $WinSwVersion, ((Get-Item -LiteralPath $WrapperExe).Length / 1MB))
}

# --- 2. wrapper configuration ---------------------------------------------------------

$xmlTemplate = Join-Path $PSScriptRoot 'livekit.xml'
if (-not (Test-Path -LiteralPath $xmlTemplate)) {
    throw "Wrapper template not found: $xmlTemplate"
}

if ($PSCmdlet.ShouldProcess($WrapperXml, 'Write WinSW configuration')) {
    $xml = Get-Content -LiteralPath $xmlTemplate -Raw
    $xml = $xml.Replace('__LOGDIR__', $LogDir)
    if ($ServiceAccount -eq 'LocalSystem') {
        # WinSW already runs as LocalSystem; an explicit block only adds a failure mode.
        $xml = [regex]::Replace($xml, '(?s)\s*<serviceaccount>.*?</serviceaccount>', '')
    }
    else {
        # WinSW 2.12.0 takes the account as <domain> and <user>.
        $accountParts = $ServiceAccount.Split('\')
        if ($accountParts.Count -ne 2) {
            throw "ServiceAccount must be 'LocalSystem' or DOMAIN\user, e.g. 'NT AUTHORITY\NetworkService'."
        }
        $xml = $xml.Replace('__SERVICE_DOMAIN__', [Security.SecurityElement]::Escape($accountParts[0]))
        $xml = $xml.Replace('__SERVICE_USER__', [Security.SecurityElement]::Escape($accountParts[1]))
    }
    $null = [xml]$xml  # fail before writing if the substitution broke the document
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($WrapperXml, $xml, $utf8)
    Write-Host "config   $WrapperXml (account: $ServiceAccount)"
}

if (-not (Test-Path -LiteralPath $LogDir)) {
    if ($PSCmdlet.ShouldProcess($LogDir, 'Create log directory')) {
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
        Write-Host "created  $LogDir"
    }
}

# --- 3. livekit.yaml ------------------------------------------------------------------

if (Test-Path -LiteralPath $ConfigTarget) {
    Write-Host "kept     $ConfigTarget (existing node configuration is never overwritten)"
}
else {
    $configTemplate = Join-Path $PSScriptRoot 'livekit.yaml'
    if ($PSCmdlet.ShouldProcess($ConfigTarget, "Copy the template from $configTemplate")) {
        Copy-Item -LiteralPath $configTemplate -Destination $ConfigTarget
        Write-Warning "livekit.yaml was copied from the template to $ConfigTarget. Fill in node_ip, turn.domain, the certificate paths and keys in that copy (not in the template in the repository), then run this script again."
    }
}

# --- 4. permissions -------------------------------------------------------------------

$isLocalSystem = $ServiceAccount -eq 'LocalSystem'
$accountSid = if ($isLocalSystem) { $null } else { Resolve-AccountSid -Account $ServiceAccount }

if ((Test-Path -LiteralPath $ConfigTarget) -and $PSCmdlet.ShouldProcess($ConfigTarget, 'Restrict to SYSTEM, Administrators and the service account')) {
    # livekit.yaml holds the API secret: no inherited Users read.
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')), 'FullControl', 'Allow')))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')), 'FullControl', 'Allow')))
    if (-not $isLocalSystem) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($accountSid, 'Read', 'Allow')))
    }
    (Get-Item -LiteralPath $ConfigTarget).SetAccessControl($acl)
    $readers = if ($isLocalSystem) { '' } else { "; $ServiceAccount read" }
    Write-Host "acl      $ConfigTarget (SYSTEM, Administrators full$readers)"
}

$turnFiles = if (Test-Path -LiteralPath $ConfigTarget) { @(Get-TurnFilePath -ConfigPath $ConfigTarget) } else { @() }
# The account the existing service runs as; its entries are removed after the re-create.
$previous = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
foreach ($turnFile in $turnFiles) {
    if (-not (Test-Path -LiteralPath $turnFile.Path)) {
        Write-Warning "TURN file $($turnFile.Path) ($($turnFile.Kind) in livekit.yaml) does not exist; LiveKit will not start."
        continue
    }
    if ($turnFile.Kind -eq 'key_file' -and $PSCmdlet.ShouldProcess($turnFile.Path, 'Remove read access of Users, Authenticated Users, Everyone and app packages')) {
        if (Protect-PrivateKeyFile -Path $turnFile.Path) {
            Write-Host "acl      $($turnFile.Path) (private key: Users / Authenticated Users / Everyone / app packages removed, inheritance off)"
        }
    }
    if (-not $isLocalSystem -and $PSCmdlet.ShouldProcess($turnFile.Path, "Grant $ServiceAccount Read")) {
        Add-AllowRule -Path $turnFile.Path -Sid $accountSid -Rights 'Read'
        Write-Host "acl      $($turnFile.Path) ($ServiceAccount read)"
    }
}
if (-not $isLocalSystem -and (Test-Path -LiteralPath $LogDir) -and $PSCmdlet.ShouldProcess($LogDir, "Grant $ServiceAccount Modify (inherited)")) {
    Add-AllowRule -Path $LogDir -Sid $accountSid -Rights 'Modify'
    Write-Host "acl      $LogDir ($ServiceAccount modify, inherited by its files)"
}

# --- 5. service -----------------------------------------------------------------------

# WinSW 2.12.0 has no `refresh`: an existing registration is re-created with
# uninstall + install so the rendered XML takes effect.
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Re-create the existing WinSW service registration (uninstall + install)')) {
        # 'stop' only requests the stop; 'stopwait' returns once the service is Stopped.
        if ($existingService.Status -ne 'Stopped') { Invoke-WinSw stopwait }
        Invoke-WinSw uninstall
        Wait-ServiceDeleted
        Stop-OrphanedServer
        Invoke-WinSw install
        Write-Host "recreated service $ServiceName"
    }
}
elseif ($PSCmdlet.ShouldProcess($ServiceName, 'Install the WinSW service')) {
    Stop-OrphanedServer
    Invoke-WinSw install
    Write-Host "created  service $ServiceName"
}

# Entries this script added for the previous service account are removed when
# -ServiceAccount names a different one (exactly those rights; other entries stay). Only now,
# after the re-create: the old service must not lose its log access while it still runs.
if ($previous -and $previous.StartName -and $previous.StartName -ne 'LocalSystem') {
    $previousSid = $null
    try { $previousSid = Resolve-AccountSid -Account $previous.StartName }
    catch { Write-Warning "Could not resolve the previous service account '$($previous.StartName)'; entries added for it are left as they are." }
    if ($previousSid -and ($isLocalSystem -or $previousSid.Value -ne $accountSid.Value) -and
        $PSCmdlet.ShouldProcess($previous.StartName, 'Remove the entries this script added for the previous service account')) {
        if ((Test-Path -LiteralPath $LogDir) -and (Remove-AllowRule -Path $LogDir -Sid $previousSid -Rights 'Modify')) {
            Write-Host "acl      $LogDir ($($previous.StartName) modify removed)"
        }
        foreach ($turnFile in $turnFiles) {
            if ((Test-Path -LiteralPath $turnFile.Path) -and (Remove-AllowRule -Path $turnFile.Path -Sid $previousSid -Rights 'Read')) {
                Write-Host "acl      $($turnFile.Path) ($($previous.StartName) read removed)"
            }
        }
    }
}

# --- 6. firewall ----------------------------------------------------------------------

# Before the start: the rules do not depend on the service state, and a node whose first
# start fails must have them once the configuration is fixed and the service started.
if ($SkipFirewall) {
    Write-Host 'firewall skipped (-SkipFirewall).'
}
else {
    foreach ($rule in $Ports) {
        Set-LiveKitFirewallRule -Name $rule.Name -Description $rule.Description `
            -Protocol $rule.Protocol -Port $rule.Port -RemoteAddress $rule.Remote
    }
}

# --- 7. start -------------------------------------------------------------------------

if ($NoStart) {
    Write-Host 'start    skipped (-NoStart).'
}
elseif ($PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
    Invoke-WinSw start
    $started = Get-Service -Name $ServiceName
    try {
        # A healthy wrapper reports Running within about a second of `start`.
        $started.WaitForStatus('Running', [TimeSpan]::FromSeconds($StartTimeoutSeconds))
    }
    catch {
        # A LiveKit that exits at once leaves the service Running for well under a second,
        # which the 250 ms poll can miss; the check below then takes the failure path.
        if ($_.Exception.GetBaseException() -isnot [System.ServiceProcess.TimeoutException]) { throw }
        Write-Verbose "Running was not observed within $StartTimeoutSeconds s."
    }
    # Running is reported as soon as the wrapper starts, before it opens its log files and
    # launches livekit-server.exe. A wrapper that cannot write its logs dies a moment later
    # (.NET unhandled exception, event 7031, SCM restart). A LiveKit that rejects its
    # configuration exits at once: up to 1.13.9 with code 0, so the service just stops;
    # from 1.13.10 with code 1, so the SCM restarts it. Require the same wrapper process and
    # a livekit-server.exe child to be still running after a settle (a recovery restart
    # would be a new process).
    $wrapperPid = Get-WrapperProcessId
    Start-Sleep -Seconds $StartSettleSeconds
    $started.Refresh()
    $wrapperPidNow = Get-WrapperProcessId
    $child = @(Get-InstalledServerProcess | Where-Object { [int]$_.ParentProcessId -eq $wrapperPidNow })
    if ($started.Status -ne 'Running' -or $wrapperPid -eq 0 -or $wrapperPidNow -ne $wrapperPid -or $child.Count -eq 0) {
        $quiet = Stop-ServiceUntilQuiet
        Stop-OrphanedServer
        $state = if ($quiet) {
            'The service was left registered and stopped, with no restart pending; fix the cause, then run this script again ' +
            "or Start-Service $ServiceName."
        }
        else {
            "The service is still registered and may still be restarting: stop it with Stop-Service $ServiceName, fix the cause " +
            'and run this script again.'
        }
        throw ("Service $ServiceName did not stay up (status $($started.Status) $StartSettleSeconds s after the start, " +
            "wrapper process $wrapperPid -> $wrapperPidNow, livekit-server.exe children: $($child.Count)). " +
            "Look in $LogDir`: a start-up error of LiveKit (configuration, TURN certificate, port in use) is one plain line " +
            'in livekit-service.out.log; its JSON log is livekit-service.err.log; livekit-service.wrapper.log is WinSW''s. ' +
            'A crash of the wrapper itself is in the Application event log (source .NET Runtime, livekit-service.exe); ' +
            'restarts are event 7031 in the System log (source Service Control Manager). ' + $state)
    }
    Write-Host "started  service $ServiceName ($($started.Status), wrapper $wrapperPid, livekit-server.exe $($child[0].ProcessId), stable for $StartSettleSeconds s)"
}

Write-Host ''
Write-Host "LiveKit service ready. Logs: $LogDir"
