<#
.SYNOPSIS
    Registers the LiveKit SFU as a Windows service using WinSW, and opens its ports.

.DESCRIPTION
    LiveKit ships a console executable, so it is wrapped with WinSW
    (github.com/winsw/winsw), which gives it the same service behaviour as talky.exe:
    delayed automatic start, restart after 5 s / 10 s / 30 s and rolling logs.

    Steps:
      1. WinSW: an existing InstallDir\livekit-service.exe is hashed against the pinned
         SHA-256 on every run and refused on a mismatch. Otherwise WinSW-x64.exe is
         downloaded from its official release (or taken from the %TEMP% cache), copied to
         a temporary name inside InstallDir, hashed there and only then renamed to
         livekit-service.exe (WinSW reads the .xml with the same base name). WinSW
         publishes no checksums file and the binary is unsigned, so its SHA-256 is pinned
         in this script;
      2. render deploy\livekit\livekit.xml into InstallDir\livekit-service.xml and create
         LogDir if it is missing;
      3. copy the deploy\livekit\livekit.yaml template next to the binary if no
         livekit.yaml is there yet (an existing file is never overwritten - it holds the
         node's keys). The operator fills in that copy; the template in the repository
         stays unfilled;
      4. set the file permissions (see -ServiceAccount and -LogDir);
      5. add the inbound firewall rules in the "LiveKit" group for -FirewallProfile. They
         do not depend on the service, so they come before it: a node whose first start
         fails has them once the configuration is fixed, and a firewall error never
         leaves a node that was running stopped:
            UDP 7882  media (SRTP/ICE)
            UDP 3478  embedded TURN
            UDP 30000-40000 TURN relay allocations (turn.relay_range_* in livekit.yaml)
            TCP 7881  media over TCP for UDP-blocked clients
            TCP 7880  signalling (reverse-proxied by talky.exe; scope it to the API nodes)
            TCP 5349  TURN over TLS
      6. `livekit-service.exe install`. An existing registration is re-created: the old
         service is stopped (see "Stopping" below), the entries this script added for a
         previous service account are removed, then uninstall + install;
      7. unless -NoStart, `livekit-service.exe start`. The start only counts once the same
         wrapper process and its livekit-server.exe child are still running a few seconds
         after the service reported Running. If not (including when Running is never
         seen within 30 s), the script ends any SCM restart loop - it removes the recovery
         actions, stops the service until it has stayed Stopped for longer than the longest
         restart delay, and restores the recovery actions - stops orphaned processes and
         fails with a message that says which of these it could establish. The service
         stays registered.

    Stopping. WinSW sends LiveKit Ctrl+C (LiveKit logs "exit requested, shutting down" and
    drains) and kills it after stoptimeout (30 s). WinSW 2.12.0 can crash right after
    LiveKit exited ("The handle is invalid" in ProcessHelper.GetChildren, seen once on a
    lab VM); the SCM then counts a failure and would restart the service 5 s later. The
    script's own stops (re-create, -Remove) therefore remove the recovery actions first
    and accept "service Stopped, no wrapper and no LiveKit process left" as success even
    when `stopwait` reports an error; if the stop or the uninstall fails or is interrupted
    (Ctrl+C) while the service is still registered, the recovery actions are restored.
    WinSW and sc.exe are judged by their exit code only, never by what they write to stderr.

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
    Service log directory. Default %ProgramData%\Talky\logs\livekit. For the default
    directory the script owns the DACL on every run: inheritance off, SYSTEM and
    Administrators Full Control, the service account Modify (inherited by the files),
    nobody else (%ProgramData% would otherwise let every user create files there), and an
    owner other than SYSTEM or Administrators is replaced by Administrators. A default
    LogDir that is a junction or symbolic link is refused; a folder between %ProgramData%
    and it that is a link or is owned by someone other than SYSTEM, Administrators or
    TrustedInstaller is warned about. A custom LogDir
    that already exists is left as it is apart from the service account's entry; the
    script warns if Users, Authenticated Users or Everyone may create or change files in it
    or if its owner is not SYSTEM or Administrators.

.PARAMETER ServiceAccount
    'LocalSystem' (default) or an account such as 'NT AUTHORITY\NetworkService' or a gMSA
    ('CORP\livekit$'). NetworkService is the hardened choice. For any account the script
    sets, on every run:
      - livekit.yaml (it holds the API secret): inheritance removed, SYSTEM and
        Administrators Full Control, plus Read for the service account if it is not
        LocalSystem (and, on an account change, Read for the previous account until its
        service is stopped). Ordinary users can no longer read the secret;
      - LogDir: Modify for the service account, inherited by files and subfolders, so it
        can append to and roll log files that an earlier LocalSystem run or the elevated
        `livekit-service.exe install` created (without it the wrapper dies with
        UnauthorizedAccessException on livekit-service.err.log);
      - turn.cert_file and turn.key_file named in livekit.yaml: Read for the service
        account, as one explicit allow entry. A relative path is relative to InstallDir
        and a path without a drive letter (\certs\key.pem) is on InstallDir's drive, as
        LiveKit (working directory InstallDir) resolves them; the drive-relative form
        'X:name' is warned about;
      - turn.key_file only, for every account including LocalSystem, before the service
        account's Read: inheritance off (inherited entries are kept as explicit ones), then
        the allow entries of Users, Authenticated Users, Everyone, ALL APPLICATION PACKAGES
        and ALL RESTRICTED APPLICATION PACKAGES are removed; SYSTEM or Administrators get
        Full Control if no entry of theirs can read the file any more. Every other entry
        stays.
    WinSW itself grants a non-LocalSystem account the "Log on as a service" right at
    `install` (<allowservicelogon> in livekit.xml).
    When -ServiceAccount differs from the account the existing service runs as, the entries
    this script added for the previous account are removed once the old service is stopped
    and before it is uninstalled: only an explicit entry that is exactly what this script
    creates (Modify inherited on LogDir, Read on livekit.yaml and on a TURN file); any other
    entry of that account is left and named in a warning. Its "Log on as a service" right is not revoked:
    other services may rely on it. A failure of this clean-up is a warning, not an error.

.PARAMETER WinSwVersion
    WinSW release to download. Default 2.12.0. The WinSW project publishes neither a
    checksums file nor an Authenticode signature on WinSW-x64.exe, so the expected SHA-256
    is pinned in this script (see $WinSwSha256). Changing the version without updating that
    hash is refused.

.PARAMETER WinSwSha256
    Expected SHA-256 of WinSW-x64.exe. Override only when you have independently verified a
    different release; the default covers 2.12.0. An installed livekit-service.exe must
    match it on every run.

.PARAMETER SignalRemoteAddress
    Remote addresses allowed to reach TCP 7880. Default 'LocalSubnet'. In production this
    should be the Talky API nodes only, since clients reach signalling through
    https://talky.corp/media/{node}/rtc. Anything that runs lk against the node (the Spike A
    load generator) must be included too.

.PARAMETER FirewallProfile
    Firewall profiles the six rules apply to. Default 'Domain' (production nodes are
    domain members). Use 'Any' in a workgroup lab. The script warns when none of the
    machine's active networks is in the given profile, since the rules then exist but
    do not apply.

.PARAMETER NoStart
    Install the service but leave it stopped.

.PARAMETER SkipFirewall
    Do not touch the firewall.

.PARAMETER Remove
    Stop, uninstall and unregister the service, stop any livekit-server.exe left running
    from InstallDir and remove the firewall rules. Files in InstallDir and LogDir and the
    file permissions stay.

.EXAMPLE
    .\Install-LiveKitService.ps1 -WhatIf

.EXAMPLE
    .\Install-LiveKitService.ps1 -SignalRemoteAddress 10.20.30.0/24

.EXAMPLE
    .\Install-LiveKitService.ps1 -Remove

.NOTES
    Requires an elevated Windows PowerShell 5.1 session (Desktop edition). PowerShell 7 is
    refused by #Requires: the ACL code uses FileInfo.GetAccessControl/SetAccessControl,
    which only .NET Framework has. -WhatIf also works unelevated; what an unelevated session
    cannot read (livekit.yaml and LogDir once the script has restricted them) is listed at
    the end as not previewed.
    Order on a new node: Get-LiveKit.ps1; this script with -NoStart, which copies the
    template to <InstallDir>\livekit.yaml; fill in that copy (node_ip, turn.domain,
    certificate paths, keys); run this script again without -NoStart, which grants the
    service account read on the TURN files the copy names and starts the service. Never
    fill in deploy\livekit\livekit.yaml in the repository checkout.
#>
#Requires -PSEdition Desktop
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'FirewallProfile',
    Justification = 'Read from the script scope by Set-LiveKitFirewallRule.')]
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'LiveKit'),
    [string] $LogDir = (Join-Path $env:ProgramData 'Talky\logs\livekit'),
    [string] $ServiceAccount = 'LocalSystem',
    [string] $WinSwVersion = '2.12.0',
    [string] $WinSwSha256 = '05b82d46ad331cc16bdc00de5c6332c1ef818df8ceefcd49c726553209b3a0da',
    [string[]] $SignalRemoteAddress = @('LocalSubnet'),
    [ValidateSet('Any', 'Domain', 'Private', 'Public', 'Domain,Private')]
    [string] $FirewallProfile = 'Domain',
    [switch] $NoStart,
    [switch] $SkipFirewall,
    [switch] $Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Absolute, with backslashes: process paths (Win32_Process.ExecutablePath) are compared
# against InstallDir as text, so 'D:/LiveKit' or '.\LiveKit' must not slip through as typed.
# A drive root keeps its backslash ('D:' alone would mean the current directory on D:).
function ConvertTo-NormalPath {
    param([Parameter(Mandatory)][string] $Path)
    $full = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
    if ($full -ne [IO.Path]::GetPathRoot($full)) { $full = $full.TrimEnd('\') }
    return $full
}
$IsDefaultLogDir = (ConvertTo-NormalPath $LogDir) -eq (ConvertTo-NormalPath (Join-Path $env:ProgramData 'Talky\logs\livekit'))
$InstallDir = ConvertTo-NormalPath $InstallDir
$LogDir = ConvertTo-NormalPath $LogDir

$ServiceName = 'LiveKit'
$FirewallGroup = 'LiveKit'
$WrapperExe = Join-Path $InstallDir 'livekit-service.exe'
$WrapperXml = Join-Path $InstallDir 'livekit-service.xml'
$ServerExe = Join-Path $InstallDir 'livekit-server.exe'
$ConfigTarget = Join-Path $InstallDir 'livekit.yaml'
$WinSwCache = Join-Path $env:TEMP 'winsw-download'
$Sc = Join-Path $env:SystemRoot 'System32\sc.exe'
# A crash shows up a few seconds after Running (the wrapper opens its log files and
# starts livekit-server.exe only then); 8 s covers that with margin.
$StartSettleSeconds = 8
# How long `start` may take to report Running. A LiveKit that exits at once is Running for
# well under a second and is usually never seen; the failure path handles that.
$StartTimeoutSeconds = 30
# The restart ladder of livekit.xml (5 s / 10 s / 30 s, reset after 1 day) in sc.exe
# syntax; the stop paths remove it while they wait and restore exactly this. Keep both in
# step with livekit.xml.
$RecoveryActions = 'restart/5000/restart/10000/restart/30000'
$RecoveryResetSeconds = 86400
# After a failed start the service only counts as quiet once it stayed Stopped for longer
# than the longest restart delay, since a restart queued before the actions were removed
# may still fire.
$RestartLadderMaxSeconds = 30
# Users, Authenticated Users, Everyone, ALL APPLICATION PACKAGES, ALL RESTRICTED APPLICATION PACKAGES.
$BroadSids = 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0', 'S-1-15-2-1', 'S-1-15-2-2'
$SystemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
$AdminsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')

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

# An unelevated -WhatIf cannot read livekit.yaml or LogDir once this script has
# restricted them. Such reads go through here: in that case the step is listed as not
# previewed and $Default is used; in every other case the error stands.
$script:NotPreviewed = New-Object System.Collections.Generic.List[string]
function Invoke-Read {
    param([Parameter(Mandatory)][string] $What, [Parameter(Mandatory)][scriptblock] $Read, $Default = $null)
    try { return (& $Read) }
    catch {
        if ($script:DryRunUnelevated -and ($_.Exception.GetBaseException() -is [UnauthorizedAccessException])) {
            $script:NotPreviewed.Add($What)
            return $Default
        }
        throw
    }
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
    if (-not $PSCmdlet.ShouldProcess($Name, "$verb inbound rule ($Protocol $Port, profile $FirewallProfile)")) { return }
    if ($existing) {
        # The group is set at creation; -Group cannot be combined with -DisplayName here.
        Set-NetFirewallRule -DisplayName $Name -Description $Description `
            -Direction Inbound -Action Allow -Protocol $Protocol -LocalPort $Port `
            -RemoteAddress $RemoteAddress -Profile $FirewallProfile -Enabled True -ErrorAction Stop
        Write-Host "updated  $Name (profile $FirewallProfile)"
    }
    else {
        New-NetFirewallRule -DisplayName $Name -Group $FirewallGroup -Description $Description `
            -Direction Inbound -Action Allow -Protocol $Protocol -LocalPort $Port `
            -RemoteAddress $RemoteAddress -Profile $FirewallProfile -Enabled True -ErrorAction Stop | Out-Null
        Write-Host "created  $Name (profile $FirewallProfile)"
    }
}

# Rules for a profile no active network is in exist and show Enabled, but apply to nothing.
function Test-FirewallProfileInUse {
    if ($FirewallProfile -eq 'Any') { return }
    $active = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | ForEach-Object {
            switch ("$($_.NetworkCategory)") { 'DomainAuthenticated' { 'Domain' } default { "$_" } } })
    $wanted = $FirewallProfile -split ','
    if (-not @($active | Where-Object { $_ -in $wanted })) {
        Write-Warning ("None of this machine's active networks is in the firewall profile '$FirewallProfile' " +
            "(active: $(if ($active) { ($active | Sort-Object -Unique) -join ', ' } else { 'none' })). The LiveKit rules exist but " +
            'do not apply; run again with -FirewallProfile matching the network (or Any).')
    }
}

# Every native call (WinSW, sc.exe) goes through here and is judged by its exit code only.
# In Windows PowerShell 5.1, 2>&1 under ErrorActionPreference Stop turns the first stderr
# line into a terminating error whatever the exit code (WinSW writes log4net errors there
# when it cannot append to its log), so the preference is lowered for the call. The output
# goes to the verbose stream.
function Invoke-Native {
    [OutputType([int])]
    param([Parameter(Mandatory)][string] $FilePath, [string[]] $ArgumentList = @())
    $ErrorActionPreference = 'Continue'
    & $FilePath @ArgumentList 2>&1 | ForEach-Object { Write-Verbose "$_" }
    return $LASTEXITCODE
}

# Runs one WinSW command and throws on a non-zero exit code.
function Invoke-WinSw {
    param([Parameter(Mandatory)][string] $Command)
    $code = Invoke-Native -FilePath $WrapperExe -ArgumentList $Command
    if ($code -ne 0) { throw "WinSW $Command failed with exit code $code." }
}

# SHA-256 of a file in lower case. Not Get-FileHash: in Windows PowerShell 5.1 that is a
# script function which honours a global WhatIfPreference (powershell.exe -File ... -WhatIf)
# and then returns nothing.
function Get-Sha256 {
    [OutputType([string])]
    param([Parameter(Mandatory)][string] $Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return (-join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') })) }
    finally { $stream.Dispose(); $sha.Dispose() }
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

# Processes of the given image name started from InstallDir (never those from elsewhere).
function Get-InstalledProcess {
    param([string] $Name = 'livekit-server.exe')
    $prefix = $InstallDir.TrimEnd('\') + '\'
    return @(Get-CimInstance -ClassName Win32_Process -Filter "Name='$Name'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
}

# Stops every livekit-server.exe from InstallDir that is not the child of the running
# service wrapper. A wrapper that crashes leaves its child behind, still holding the ports.
function Stop-OrphanedServer {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param()
    $wrapperPid = Get-WrapperProcessId
    foreach ($proc in (Get-InstalledProcess)) {
        if ($wrapperPid -ne 0 -and [int]$proc.ParentProcessId -eq $wrapperPid) { continue }
        if ($PSCmdlet.ShouldProcess("livekit-server.exe ($($proc.ProcessId))", 'Stop orphaned process')) {
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $proc.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
            Write-Host "stopped  orphaned livekit-server.exe ($($proc.ProcessId), parent $($proc.ParentProcessId) gone)"
        }
    }
}

# Removes ($Disable) or restores the service's recovery actions; returns $true on success.
# actions= "" means "no action"; Windows PowerShell drops a bare "" argument, hence '""'.
function Set-RecoveryAction {
    param([switch] $Disable)
    $arguments = if ($Disable) { 'failure', $ServiceName, 'reset=', '0', 'actions=', '""' }
    else { 'failure', $ServiceName, 'reset=', "$RecoveryResetSeconds", 'actions=', $RecoveryActions }
    return ((Invoke-Native -FilePath $Sc -ArgumentList $arguments) -eq 0)
}

# Restores the recovery actions if the service is still registered; warns if that fails.
function Restore-RecoveryAction {
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { return }
    if (-not (Set-RecoveryAction)) {
        Write-Warning ("sc.exe failure (restore the recovery actions) exited $LASTEXITCODE; run this script again so " +
            "the restart policy 5 s / 10 s / 30 s is set.")
    }
}

# Stops the service for a re-create or -Remove. WinSW sends LiveKit Ctrl+C and LiveKit
# drains; WinSW 2.12.0 can then crash ("The handle is invalid") right after LiveKit exited,
# which the SCM counts as a failure and answers with a restart after 5 s. The recovery
# actions are therefore removed first, and "Stopped, no wrapper and no LiveKit process
# left" is success even if stopwait reported an error. Otherwise (failure, terminating
# error, Ctrl+C) the recovery actions are restored on the way out. On success they stay
# removed: the caller restores them unless the uninstall that follows succeeds.
function Stop-ServiceForChange {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    $disabled = Set-RecoveryAction -Disable
    if (-not $disabled) { Write-Warning 'Could not remove the recovery actions before the stop; a wrapper crash during the stop could restart the service.' }
    $stopped = $false
    try {
        $code = 0
        if ($svc.Status -ne 'Stopped') { $code = Invoke-Native -FilePath $WrapperExe -ArgumentList 'stopwait' }
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            $svc.Refresh()
            if ($svc.Status -eq 'Stopped') { break }
            if ($svc.Status -in 'Running', 'StartPending') { $null = Invoke-Native -FilePath $Sc -ArgumentList 'stop', $ServiceName }
            Start-Sleep -Milliseconds 250
        }
        Stop-OrphanedServer
        $svc.Refresh()
        $wrappers = @(Get-InstalledProcess -Name 'livekit-service.exe')
        $servers = @(Get-InstalledProcess)
        if ($svc.Status -eq 'Stopped' -and $wrappers.Count -eq 0 -and $servers.Count -eq 0) {
            if ($code -ne 0) {
                Write-Warning ("WinSW stopwait reported exit code $code, but the service is Stopped and no wrapper or LiveKit " +
                    'process is left (WinSW 2.12.0 can crash right after LiveKit exited); continuing.')
            }
            Write-Host "stopped  service $ServiceName"
            $stopped = $true
            return
        }
        throw ("Service $ServiceName could not be stopped: status $($svc.Status), wrapper processes $($wrappers.Count), " +
            "livekit-server.exe processes $($servers.Count), WinSW stopwait exit code $code. Its recovery actions were restored. " +
            "Stop it with Stop-Service $ServiceName and run this script again.")
    }
    finally {
        if ($disabled -and -not $stopped) { Restore-RecoveryAction }
    }
}

# Stop-ServiceForChange, then WinSW uninstall until the SCM has deleted the service. The
# recovery actions come back if anything on the way fails or the run is interrupted while
# the service is still registered.
function Remove-ServiceForChange {
    param([scriptblock] $AfterStop = {})
    $removed = $false
    try {
        Stop-ServiceForChange
        & $AfterStop
        Invoke-WinSw uninstall
        Wait-ServiceDeleted
        $removed = $true
    }
    finally {
        if (-not $removed) { Restore-RecoveryAction }
    }
}

# Ends the SCM restart loop of a failing service and returns what it could establish:
#   Quiet      Stopped for longer than the longest restart delay with the recovery actions
#              removed, so no restart is pending;
#   Armed      Stopped for that long, but the recovery actions could not be removed;
#   NotQuiet   did not stay Stopped before the deadline;
#   Declined   the stop was declined (-Confirm); nothing was done;
#   Gone       the service is no longer registered.
# A wrapper whose LiveKit exits at once is Running for well under a second per restart and
# a stop sent outside that window is dropped, so the loop is not raced: the recovery
# actions are removed first (a restart queued before that may still fire once but cannot
# queue another), the service is stopped whenever it is seen starting or running. The
# recovery actions are restored in every case.
function Stop-ServiceUntilQuiet {
    [CmdletBinding(SupportsShouldProcess = $true)]
    [OutputType([string])]
    param()
    if (-not $PSCmdlet.ShouldProcess($ServiceName, 'Stop the service and end its restart loop')) { return 'Declined' }
    $quietSeconds = $RestartLadderMaxSeconds + 5
    $deadline = (Get-Date).AddSeconds(4 * $quietSeconds)
    try {
        $disabled = Set-RecoveryAction -Disable
        if (-not $disabled) { Write-Warning "sc.exe failure (remove the recovery actions) exited $LASTEXITCODE; a restart may still be scheduled." }
        $stoppedSince = $null
        while ((Get-Date) -lt $deadline) {
            $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if (-not $svc) { return 'Gone' }
            if ($svc.Status -eq 'Stopped') {
                if (-not $stoppedSince) { $stoppedSince = Get-Date }
                if (((Get-Date) - $stoppedSince).TotalSeconds -ge $quietSeconds) {
                    if (-not $disabled) { return 'Armed' }
                    Write-Host "stopped  service $ServiceName (stayed Stopped for $quietSeconds s; no restart pending)"
                    return 'Quiet'
                }
            }
            else {
                $stoppedSince = $null
                if ($svc.Status -in 'Running', 'StartPending', 'Paused') {
                    # sc.exe fails harmlessly while the service cannot accept a stop yet; retry.
                    $null = Invoke-Native -FilePath $Sc -ArgumentList 'stop', $ServiceName
                }
            }
            Start-Sleep -Milliseconds 250
        }
        Write-Warning "Service $ServiceName did not stay Stopped within $([int](4 * $quietSeconds)) s."
        return 'NotQuiet'
    }
    finally { Restore-RecoveryAction }
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

function Get-InheritFlag {
    param([Parameter(Mandatory)] $Item)
    if ($Item.PSIsContainer) { return [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' }
    return [Security.AccessControl.InheritanceFlags]::None
}

# Adds one allow entry for $Sid to the DACL of a file or directory; existing entries stay.
function Add-AllowRule {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][Security.Principal.SecurityIdentifier] $Sid,
        [Parameter(Mandatory)][Security.AccessControl.FileSystemRights] $Rights
    )
    $item = Get-Item -LiteralPath $Path
    $acl = $item.GetAccessControl('Access')
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($Sid, $Rights, (Get-InheritFlag $item), 'None', 'Allow')))
    # Setting the directory DACL also propagates the inheritable entry to existing files.
    $item.SetAccessControl($acl)
}

# The counterpart of Add-AllowRule: removes the explicit allow entry of $Sid only if it is
# exactly what Add-AllowRule creates (same rights plus Synchronize, same inheritance). Any
# other explicit entry of that account is left alone and named in a warning, since this
# script did not add it. Returns $true when an entry was removed.
function Remove-AllowRule {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][Security.Principal.SecurityIdentifier] $Sid,
        [Parameter(Mandatory)][Security.AccessControl.FileSystemRights] $Rights,
        [Parameter(Mandatory)][string] $AccountName
    )
    $item = Get-Item -LiteralPath $Path
    $inherit = Get-InheritFlag $item
    $expected = [Security.AccessControl.FileSystemRights]([int]$Rights -bor [int][Security.AccessControl.FileSystemRights]::Synchronize)
    $acl = $item.GetAccessControl('Access')
    $explicit = @($acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.IdentityReference.Value -eq $Sid.Value })
    $exact = @($explicit | Where-Object { $_.AccessControlType -eq 'Allow' -and $_.FileSystemRights -eq $expected -and
            $_.InheritanceFlags -eq $inherit -and $_.PropagationFlags -eq 'None' })
    $other = @($explicit | Where-Object { $exact -notcontains $_ })
    foreach ($rule in $other) {
        Write-Warning ("$Path keeps an entry for $AccountName that this script did not add: $($rule.AccessControlType) " +
            "$($rule.FileSystemRights) (inheritance $($rule.InheritanceFlags)/$($rule.PropagationFlags)). Remove it by hand if it is not needed.")
    }
    if ($exact.Count -eq 0) { return $false }
    $acl.RemoveAccessRuleSpecific((New-Object Security.AccessControl.FileSystemAccessRule($Sid, $Rights, $inherit, 'None', 'Allow')))
    $item.SetAccessControl($acl)
    return $true
}

# The TURN private key must not be readable by ordinary users. The decision does not
# depend on what the DACL shows: a key moved into its folder keeps a stale DACL without
# the folder's entries, and the next write of an unprotected DACL brings them back. So
# inheritance is turned off whenever it is still on (inherited entries become explicit
# ones), then the allow entries of the broad groups are removed; everything else stays.
# SYSTEM and Administrators get Full Control if they have no allow entry left that reads
# the file. Call it before granting the service account Read: once the DACL is protected,
# no later write can bring the folder's entries back. Returns what it changed, or ''.
function Protect-PrivateKeyFile {
    param([Parameter(Mandatory)][string] $Path)
    $item = Get-Item -LiteralPath $Path
    $acl = $item.GetAccessControl('Access')
    $changes = @()
    if (-not $acl.AreAccessRulesProtected) {
        $acl.SetAccessRuleProtection($true, $true)
        # Persist and re-read so the formerly inherited entries are explicit and removable.
        $item.SetAccessControl($acl)
        $acl = $item.GetAccessControl('Access')
        $changes += 'inheritance off'
    }
    $broad = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -in $BroadSids })
    if ($broad.Count -gt 0) {
        foreach ($sid in $BroadSids) {
            $acl.RemoveAccessRuleAll((New-Object Security.AccessControl.FileSystemAccessRule(
                (New-Object Security.Principal.SecurityIdentifier($sid)), 'Read', 'Allow')))
        }
        $item.SetAccessControl($acl)
        $changes += 'Users / Authenticated Users / Everyone / app packages removed'
        $acl = $item.GetAccessControl('Access')
    }
    $readers = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.AccessControlType -eq 'Allow' -and ([int]$_.FileSystemRights -band [int][Security.AccessControl.FileSystemRights]::ReadData) -ne 0 } |
        ForEach-Object { $_.IdentityReference.Value })
    $missing = @($SystemSid, $AdminsSid | Where-Object { $readers -notcontains $_.Value })
    if ($missing.Count -gt 0) {
        foreach ($sid in $missing) { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'Allow'))) }
        $item.SetAccessControl($acl)
        $changes += "$(($missing | ForEach-Object { if ($_.Value -eq $SystemSid.Value) { 'SYSTEM' } else { 'Administrators' } }) -join ' and ') full control added (no entry of theirs could read it)"
    }
    return ($changes -join ', ')
}

# turn.cert_file / turn.key_file from livekit.yaml, unless still placeholders or empty, as
# objects with Kind ('cert_file' or 'key_file') and an absolute Path, resolved the way
# LiveKit resolves them with InstallDir as its working directory: relative to InstallDir,
# a path without a drive letter on InstallDir's drive, 'X:name' relative to InstallDir if
# X is InstallDir's drive and to X:\ otherwise (that form is warned about).
function Get-TurnFilePath {
    param([Parameter(Mandatory)][string] $ConfigPath)
    $files = @()
    $installRoot = [IO.Path]::GetPathRoot($InstallDir)
    foreach ($line in (Get-Content -LiteralPath $ConfigPath)) {
        if ($line -notmatch '^\s+(?<k>cert_file|key_file):\s*(?<v>.+?)\s*$') { continue }
        $kind = $Matches['k']
        $value = $Matches['v']
        if ($value -match '^"(?<s>(?:[^"\\]|\\.)*)"') { $value = $Matches['s'].Replace('\\', '\') }  # YAML double-quoted
        elseif ($value -match "^'(?<s>(?:[^']|'')*)'") { $value = $Matches['s'].Replace("''", "'") }  # YAML single-quoted
        else { $value = ($value -split '\s+#', 2)[0].Trim() }
        if (-not $value -or $value.StartsWith('<')) { continue }
        if ($value -match '^[\\/](?![\\/])') {
            $value = $installRoot.TrimEnd('\') + '\' + $value.TrimStart('\', '/')
        }
        elseif ($value -match '^(?<d>[A-Za-z]):(?![\\/])(?<rest>.*)$') {
            $drive = $Matches['d']; $rest = $Matches['rest']
            $resolved = if ($installRoot -like "${drive}:*") { Join-Path $InstallDir $rest } else { "${drive}:\$rest" }
            Write-Warning "$kind '$value' in livekit.yaml is drive-relative; taken as $resolved. Write an absolute path."
            $value = $resolved
        }
        elseif (-not [IO.Path]::IsPathRooted($value)) { $value = Join-Path $InstallDir $value }
        $files += [pscustomobject]@{ Kind = $kind; Path = [IO.Path]::GetFullPath($value) }
    }
    return $files
}

# LogDir permissions. The default directory gets a DACL owned by this script: inheritance
# off, SYSTEM and Administrators Full Control, $ModifySid Modify (inherited by the files),
# nobody else, owner SYSTEM or Administrators. A custom directory only gets the Modify
# entries added, with a warning when broad groups can create or change files in it or its
# owner is somebody else.
function Set-LogDirectoryAcl {
    param([Security.Principal.SecurityIdentifier[]] $ModifySid = @(), [string] $Accounts = '')
    $item = Get-Item -LiteralPath $LogDir
    $writeMask = [int]([Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership')
    $owner = $item.GetAccessControl('Owner').GetOwner([Security.Principal.SecurityIdentifier])
    $ownerOk = $owner.Value -in $SystemSid.Value, $AdminsSid.Value
    if ($IsDefaultLogDir) {
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in $SystemSid, $AdminsSid) {
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
        }
        foreach ($sid in $ModifySid) {
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
        }
        if (-not $ownerOk) { $acl.SetOwner($AdminsSid) }
        $item.SetAccessControl($acl)
        # This line describes the folder's own DACL; its parents are judged by Test-DefaultLogDirectoryPath.
        Write-Host ("acl      $LogDir (this folder: inheritance off; SYSTEM, Administrators full" + $(if ($Accounts) { "; $Accounts modify" }) +
            $(if (-not $ownerOk) { "; owner $(Format-Sid $owner) replaced by Administrators" }) +
            $(if ($logPathWarned) { '; its parent folders are not trusted, see the warning above' }) + ')')
        # Log files are owned by whoever created them: SYSTEM/Administrators, or the service
        # account that wrote them (NETWORK SERVICE, LOCAL SERVICE, a gMSA given Modify here).
        $expectedOwners = @($SystemSid.Value, $AdminsSid.Value, 'S-1-5-19', 'S-1-5-20') + @($ModifySid | ForEach-Object { $_.Value })
        foreach ($file in @(Get-ChildItem -LiteralPath $LogDir -Force)) {
            try {
                $fileAcl = $file.GetAccessControl('Owner, Access')
            }
            catch {
                Write-Warning ("Cannot read the permissions of $($file.FullName) ($($_.Exception.GetBaseException().Message)); " +
                    'someone other than this script created it. Take ownership and delete it unless you know who created it.')
                continue
            }
            $fileOwner = $fileAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value
            if ($fileAcl.AreAccessRulesProtected -or $fileOwner -notin $expectedOwners) {
                Write-Warning ("$($file.FullName) does not take its permissions from $LogDir (owner $fileOwner, inheritance " +
                    "$(if ($fileAcl.AreAccessRulesProtected) { 'off' } else { 'on' })); delete it unless you know who created it.")
            }
        }
        return
    }
    foreach ($sid in $ModifySid) { Add-AllowRule -Path $LogDir -Sid $sid -Rights 'Modify' }
    if ($Accounts) { Write-Host "acl      $LogDir ($Accounts modify, inherited by its files)" }
    $open = @($item.GetAccessControl('Access').GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -in $BroadSids[0..2] -and ([int]$_.FileSystemRights -band $writeMask) -ne 0 })
    if ($open.Count -gt 0) {
        Write-Warning ("Users, Authenticated Users or Everyone may create or change files in $LogDir " +
            "($(($open | ForEach-Object { "$(Format-Sid $_.IdentityReference): $($_.FileSystemRights)" }) -join '; ')). " +
            'Anyone who can do that can pre-create the log files and stop the service from writing them. Restrict it, or use the default LogDir.')
    }
    if (-not $ownerOk) {
        Write-Warning "$LogDir is owned by $(Format-Sid $owner), not by SYSTEM or Administrators; that owner can change its permissions."
    }
}

# An account name for a SID, or the SID itself when it does not translate (deleted
# account, foreign machine, domain not reachable).
function Format-Sid {
    [OutputType([string])]
    param([Parameter(Mandatory)][Security.Principal.SecurityIdentifier] $Sid)
    try { return $Sid.Translate([Security.Principal.NTAccount]).Value }
    catch { return $Sid.Value }
}

# The default LogDir lies under %ProgramData%, where Users may create folders. Its own DACL
# is replaced by Set-LogDirectoryAcl; what that cannot cover is checked here: a leaf that
# is a junction or symbolic link (its DACL would be set on the link, and the logs would go
# where it points) is refused; a folder between %ProgramData% and the leaf that is a link or
# is owned by someone other than SYSTEM, Administrators or TrustedInstaller is warned about,
# because its owner can move the leaf away or re-point the path. Returns $true when such a
# warning was given.
function Test-DefaultLogDirectoryPath {
    [OutputType([bool])]
    param()
    $trusted = @($SystemSid.Value, $AdminsSid.Value, 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $leaf = Get-Item -LiteralPath $LogDir -Force -ErrorAction SilentlyContinue
    if ($leaf -and ($leaf.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw ("$LogDir is a junction or symbolic link. The log folder must be a real folder: remove the link (the folder " +
            'it points to stays), then run this script again.')
    }
    $warned = $false
    $base = [Environment]::GetFolderPath('CommonApplicationData').TrimEnd('\')
    $parent = Split-Path -Parent $LogDir
    while ($parent -and $parent.Length -gt $base.Length) {
        $folder = Get-Item -LiteralPath $parent -Force -ErrorAction SilentlyContinue
        if ($folder) {
            $problem = $null
            if ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint) { $problem = 'is a junction or symbolic link' }
            else {
                try {
                    $owner = $folder.GetAccessControl('Owner').GetOwner([Security.Principal.SecurityIdentifier])
                    if ($trusted -notcontains $owner.Value) { $problem = "is owned by $(Format-Sid $owner)" }
                }
                catch { $problem = "has an owner that cannot be read ($($_.Exception.GetBaseException().Message))" }
            }
            if ($problem) {
                Write-Warning ("$parent $problem, not by SYSTEM, Administrators or TrustedInstaller: whoever controls it can move " +
                    "$LogDir away or re-point it. Make Administrators its owner (and remove the link), or use another -LogDir.")
                $warned = $true
            }
        }
        $parent = Split-Path -Parent $parent
    }
    return $warned
}

# Removes the entries this script added for a previous service account (exactly those) and
# closes the private key again. Never fatal: a failure is a warning naming what is left.
function Remove-PreviousAccountEntry {
    param(
        [Parameter(Mandatory)][string] $AccountName,
        [Parameter(Mandatory)][Security.Principal.SecurityIdentifier] $Sid,
        [object[]] $TurnFiles = @()
    )
    try {
        if ((Test-Path -LiteralPath $LogDir) -and (Remove-AllowRule -Path $LogDir -Sid $Sid -Rights 'Modify' -AccountName $AccountName)) {
            Write-Host "acl      $LogDir ($AccountName modify removed)"
        }
        if ((Test-Path -LiteralPath $ConfigTarget) -and (Remove-AllowRule -Path $ConfigTarget -Sid $Sid -Rights 'Read' -AccountName $AccountName)) {
            Write-Host "acl      $ConfigTarget ($AccountName read removed)"
        }
        foreach ($turnFile in $TurnFiles) {
            if (-not (Test-Path -LiteralPath $turnFile.Path)) { continue }
            if (Remove-AllowRule -Path $turnFile.Path -Sid $Sid -Rights 'Read' -AccountName $AccountName) {
                Write-Host "acl      $($turnFile.Path) ($AccountName read removed)"
            }
            if ($turnFile.Kind -eq 'key_file') {
                $changed = Protect-PrivateKeyFile -Path $turnFile.Path
                if ($changed) { Write-Host "acl      $($turnFile.Path) (private key: $changed)" }
            }
        }
    }
    catch {
        Write-Warning ("Could not remove the entries added for the previous service account $AccountName ($($_.Exception.Message)). " +
            "Check $LogDir, $ConfigTarget and the TURN files and remove its entries by hand.")
    }
}

# --- preconditions --------------------------------------------------------------------

$script:DryRunUnelevated = $false
if (-not (Test-Elevated)) {
    # A dry run only reads, so it stays useful from an unelevated shell.
    if ($WhatIfPreference) {
        $script:DryRunUnelevated = $true
        Write-Warning 'Not elevated. Continuing because -WhatIf was given; a real run needs an elevated session.'
    }
    else {
        throw 'Install-LiveKitService.ps1 must run in an elevated PowerShell session.'
    }
}

# --- removal path ---------------------------------------------------------------------

if ($Remove) {
    $current = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $current) {
        Write-Host "absent   service $ServiceName"
    }
    elseif (-not (Test-Path -LiteralPath $WrapperExe)) {
        throw "Service $ServiceName exists but $WrapperExe is missing; remove it with sc.exe delete $ServiceName."
    }
    elseif ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and uninstall the WinSW service')) {
        Remove-ServiceForChange
        Write-Host "removed  service $ServiceName"
    }
    Stop-OrphanedServer
    if (-not $SkipFirewall) {
        foreach ($rule in @(Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue)) {
            if ($PSCmdlet.ShouldProcess($rule.DisplayName, 'Remove inbound rule')) {
                Remove-NetFirewallRule -DisplayName $rule.DisplayName -ErrorAction Stop
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

# WinSW ships no checksums file and WinSW-x64.exe is not Authenticode signed, so the only
# integrity check available is a hash pinned here. Verify a new release by hand
# (Get-FileHash) before changing $WinSwSha256.
if (Test-Path -LiteralPath $WrapperExe) {
    # What gets executed is hashed on every run, not only when it was downloaded.
    $actual = Get-Sha256 -Path $WrapperExe
    if ($actual -ne $WinSwSha256.ToLowerInvariant()) {
        throw ("$WrapperExe does not match the pinned SHA-256 of WinSW $WinSwVersion (expected $WinSwSha256, got $actual). " +
            'If it is another WinSW release on purpose, pass -WinSwVersion and -WinSwSha256 for that release; otherwise ' +
            'delete the file and run this script again, which downloads and verifies WinSW.')
    }
    Write-Host "present  $WrapperExe (SHA-256 verified)"
}
elseif ($PSCmdlet.ShouldProcess($WrapperExe, "Download WinSW $WinSwVersion")) {
    if (-not (Test-Path -LiteralPath $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }
    # The download is cached like Get-LiveKit.ps1 does.
    $cached = Join-Path $WinSwCache "WinSW-x64-$WinSwVersion.exe"
    $verb = 'cached  '
    if (-not (Test-Path -LiteralPath $cached)) {
        New-Item -ItemType Directory -Path $WinSwCache -Force | Out-Null
        $url = "https://github.com/winsw/winsw/releases/download/v$WinSwVersion/WinSW-x64.exe"
        Invoke-WebRequest -Uri $url -OutFile $cached -UseBasicParsing -Headers @{ 'User-Agent' = 'talky-deploy' }
        $verb = 'fetched '
    }
    # Hash the bytes that will be installed, under a name nothing runs, and only then give
    # them the name WinSW is executed by.
    $staged = "$WrapperExe.new"
    Copy-Item -LiteralPath $cached -Destination $staged -Force
    $actual = Get-Sha256 -Path $staged
    if ($actual -ne $WinSwSha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $staged, $cached -Force -ErrorAction SilentlyContinue
        $left = @($staged, $cached | Where-Object { Test-Path -LiteralPath $_ })
        $state = if ($left) { "Could not delete $($left -join ' and ') (in use?); delete by hand. " } else { 'The staged copy and the cached file were deleted. ' }
        throw ("SHA-256 mismatch for WinSW-x64.exe ${WinSwVersion}: expected $WinSwSha256, got $actual. " + $state +
            "$WrapperExe was not created.")
    }
    Move-Item -LiteralPath $staged -Destination $WrapperExe
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

$logPathWarned = $false
if ($IsDefaultLogDir) { $logPathWarned = Test-DefaultLogDirectoryPath }
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

# The account the existing service runs as. Its entries are removed in step 6, once the
# old service is stopped and before it is uninstalled.
$previous = $null
$existing = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
if ($existing -and $existing.StartName -and $existing.StartName -ne 'LocalSystem') {
    try {
        $previousSid = Resolve-AccountSid -Account $existing.StartName
        if ($isLocalSystem -or $previousSid.Value -ne $accountSid.Value) {
            $previous = [pscustomobject]@{ Name = $existing.StartName; Sid = $previousSid }
        }
    }
    catch {
        Write-Warning "Could not resolve the previous service account '$($existing.StartName)'; entries added for it are left as they are."
    }
}

if ((Test-Path -LiteralPath $ConfigTarget) -and $PSCmdlet.ShouldProcess($ConfigTarget, 'Restrict to SYSTEM, Administrators and the service account')) {
    # livekit.yaml holds the API secret: no inherited Users read.
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($SystemSid, 'FullControl', 'Allow')))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($AdminsSid, 'FullControl', 'Allow')))
    $readers = @()
    if (-not $isLocalSystem) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($accountSid, 'Read', 'Allow')))
        $readers += $ServiceAccount
    }
    # The running old service keeps its Read until it is stopped in step 6, like LogDir.
    if ($previous) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($previous.Sid, 'Read', 'Allow')))
        $readers += "$($previous.Name) (until its service is stopped)"
    }
    (Get-Item -LiteralPath $ConfigTarget).SetAccessControl($acl)
    Write-Host "acl      $ConfigTarget (SYSTEM, Administrators full$(if ($readers) { "; $($readers -join ', ') read" }))"
}

if (Test-Path -LiteralPath $LogDir) {
    # The running old service keeps its Modify until it is stopped in step 6.
    $modify = @(); $names = @()
    if (-not $isLocalSystem) { $modify += $accountSid; $names += $ServiceAccount }
    if ($previous) { $modify += $previous.Sid; $names += "$($previous.Name) (until its service is stopped)" }
    if ($PSCmdlet.ShouldProcess($LogDir, "Set permissions ($(if ($IsDefaultLogDir) { 'owned DACL' } else { 'add the service account' }))")) {
        Set-LogDirectoryAcl -ModifySid $modify -Accounts ($names -join ', ')
    }
    else {
        $null = Invoke-Read -What "the current permissions of $LogDir" -Read { (Get-Item -LiteralPath $LogDir).GetAccessControl('Access') }
    }
}

$turnFiles = @()
if (Test-Path -LiteralPath $ConfigTarget) {
    $turnFiles = @(Invoke-Read -What 'the TURN certificate and key named in livekit.yaml (their permissions)' -Default @() -Read { Get-TurnFilePath -ConfigPath $ConfigTarget })
}
foreach ($turnFile in $turnFiles) {
    if (-not (Test-Path -LiteralPath $turnFile.Path)) {
        Write-Warning "TURN file $($turnFile.Path) ($($turnFile.Kind) in livekit.yaml) does not exist; LiveKit will not start."
        continue
    }
    # The key is closed before the account's grant, so it is never open between two writes
    # and a failed grant leaves it closed; step 6 closes it again after its clean-up.
    if ($turnFile.Kind -eq 'key_file' -and $PSCmdlet.ShouldProcess($turnFile.Path, 'Turn inheritance off and remove Users, Authenticated Users, Everyone and app packages')) {
        $changed = Protect-PrivateKeyFile -Path $turnFile.Path
        Write-Host "acl      $($turnFile.Path) (private key: $(if ($changed) { $changed } else { 'already closed' }))"
    }
    if (-not $isLocalSystem -and $PSCmdlet.ShouldProcess($turnFile.Path, "Grant $ServiceAccount Read")) {
        Add-AllowRule -Path $turnFile.Path -Sid $accountSid -Rights 'Read'
        Write-Host "acl      $($turnFile.Path) ($ServiceAccount read)"
    }
}

# --- 5. firewall ----------------------------------------------------------------------

# Before the service: the rules do not depend on it, a node whose first start fails must
# have them once the configuration is fixed, and a firewall error must not leave a node
# that was running stopped.
if ($SkipFirewall) {
    Write-Host 'firewall skipped (-SkipFirewall).'
}
else {
    # The NetSecurity cmdlets do not see this script's ErrorActionPreference; each call
    # passes -ErrorAction Stop, so a rejected rule ends the run here.
    foreach ($rule in $Ports) {
        try {
            Set-LiveKitFirewallRule -Name $rule.Name -Description $rule.Description `
                -Protocol $rule.Protocol -Port $rule.Port -RemoteAddress $rule.Remote
        }
        catch {
            throw "Firewall rule '$($rule.Name)' could not be set: $($_.Exception.Message.Trim()) The LiveKit service was not stopped or re-created."
        }
    }
    Test-FirewallProfileInUse
}

# --- 6. service -----------------------------------------------------------------------

# WinSW 2.12.0 has no `refresh`: an existing registration is re-created with
# uninstall + install so the rendered XML takes effect. Nothing that can fail sits between
# the uninstall and the start apart from the service operations themselves.
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Re-create the existing WinSW service registration (stop, uninstall, install)')) {
        Remove-ServiceForChange -AfterStop {
            if ($previous) { Remove-PreviousAccountEntry -AccountName $previous.Name -Sid $previous.Sid -TurnFiles $turnFiles }
        }
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
    $child = @(Get-InstalledProcess | Where-Object { [int]$_.ParentProcessId -eq $wrapperPidNow })
    if ($started.Status -ne 'Running' -or $wrapperPid -eq 0 -or $wrapperPidNow -ne $wrapperPid -or $child.Count -eq 0) {
        $quiet = Stop-ServiceUntilQuiet
        Stop-OrphanedServer
        $state = switch ($quiet) {
            'Quiet' { 'The service was left registered and stopped, with no restart pending; fix the cause, then run this script ' +
                "again or Start-Service $ServiceName." }
            'Armed' { "The service was seen Stopped for $($RestartLadderMaxSeconds + 5) s, but its recovery actions could not be " +
                "removed, so a restart may still come: check Get-Service $ServiceName and stop it again if it runs, fix the " +
                'cause and run this script again.' }
            'Declined' { 'Stopping the service was declined, so nothing was stopped: it is registered and may still be ' +
                "restarting (LiveKit 1.13.10+). Stop it with Stop-Service $ServiceName, fix the cause and run this script again." }
            'Gone' { 'The service is no longer registered (it was deleted during this run); fix the cause and run this script again.' }
            default { "The service is still registered and may still be restarting: stop it with Stop-Service $ServiceName, " +
                'fix the cause and run this script again.' }
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
if ($script:NotPreviewed.Count -gt 0) {
    Write-Warning ("Not previewed (an unelevated session cannot read them): $($script:NotPreviewed -join '; '). " +
        'Run -WhatIf elevated to see those steps.')
}
Write-Host "LiveKit service ready. Logs: $LogDir"
