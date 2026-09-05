<#
.SYNOPSIS
    Registers the LiveKit SFU as a Windows service using WinSW, and opens its ports.

.DESCRIPTION
    LiveKit ships a console executable, so it is wrapped with WinSW
    (github.com/winsw/winsw), which gives it the same service behaviour as talky.exe:
    delayed automatic start, restart after 5 s / 10 s / 30 s and rolling logs.

    Steps:
      1. download WinSW-x64.exe from its official release and place it in InstallDir as
         livekit-service.exe (WinSW reads the .xml with the same base name);
      2. render deploy\livekit\livekit.xml into InstallDir\livekit-service.xml;
      3. copy deploy\livekit\livekit.yaml next to the binary if it is not there yet
         (an existing file is never overwritten - it holds the node's keys);
      4. `livekit-service.exe install` and, unless -NoStart, `start`;
      5. add the inbound firewall rules in the "LiveKit" group:
            UDP 7882  media (SRTP/ICE)
            UDP 3478  embedded TURN
            UDP 30000-40000 TURN relay allocations (turn.relay_range_* in livekit.yaml)
            TCP 7881  media over TCP for UDP-blocked clients
            TCP 7880  signalling (reverse-proxied by talky.exe; scope it to the API nodes)
            TCP 5349  TURN over TLS

    Idempotent: an already installed service is reconfigured and restarted rather than
    duplicated; existing firewall rules are updated in place.

.PARAMETER InstallDir
    Directory holding livekit-server.exe. Default C:\Program Files\LiveKit.

.PARAMETER LogDir
    Service log directory. Default %ProgramData%\Talky\logs\livekit.

.PARAMETER ServiceAccount
    'LocalSystem' (default) or an account such as 'NT AUTHORITY\NetworkService'.
    NetworkService is the hardened choice; the TURN certificate and key files must then
    be readable by it.

.PARAMETER WinSwVersion
    WinSW release to download. Default 2.12.0.

.PARAMETER SignalRemoteAddress
    Remote addresses allowed to reach TCP 7880. Default 'LocalSubnet'. In production this
    should be the Talky API nodes only, since clients reach signalling through
    https://talky.corp/media/{node}/rtc.

.PARAMETER NoStart
    Install the service but leave it stopped.

.PARAMETER SkipFirewall
    Do not touch the firewall.

.PARAMETER Remove
    Stop, uninstall and unregister the service and remove the firewall rules.

.EXAMPLE
    .\Install-LiveKitService.ps1 -WhatIf

.EXAMPLE
    .\Install-LiveKitService.ps1 -SignalRemoteAddress 10.20.30.0/24

.EXAMPLE
    .\Install-LiveKitService.ps1 -Remove

.NOTES
    Requires an elevated session. PowerShell 5.1 compatible.
    Run Get-LiveKit.ps1 first and fill in livekit.yaml (node_ip, turn.domain,
    certificate paths, keys) before starting the service.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'LiveKit'),
    [string] $LogDir = (Join-Path $env:ProgramData 'Talky\logs\livekit'),
    [string] $ServiceAccount = 'LocalSystem',
    [string] $WinSwVersion = '2.12.0',
    [string[]] $SignalRemoteAddress = @('LocalSubnet'),
    [switch] $NoStart,
    [switch] $SkipFirewall,
    [switch] $Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'LiveKit'
$FirewallGroup = 'LiveKit'
$WrapperExe = Join-Path $InstallDir 'livekit-service.exe'
$WrapperXml = Join-Path $InstallDir 'livekit-service.xml'
$ServerExe = Join-Path $InstallDir 'livekit-server.exe'
$ConfigTarget = Join-Path $InstallDir 'livekit.yaml'

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
        Set-NetFirewallRule -DisplayName $Name -Group $FirewallGroup -Description $Description `
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
            & $WrapperExe stop 2>&1 | Write-Verbose
            & $WrapperExe uninstall 2>&1 | Write-Verbose
            Write-Host "removed  service $ServiceName"
        }
    }
    else {
        Write-Host "absent   $WrapperExe"
    }
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
    $url = "https://github.com/winsw/winsw/releases/download/v$WinSwVersion/WinSW-x64.exe"
    Invoke-WebRequest -Uri $url -OutFile $WrapperExe -UseBasicParsing -Headers @{ 'User-Agent' = 'talky-deploy' }
    Write-Host ("fetched  WinSW-x64.exe {0} ({1:N1} MB)" -f $WinSwVersion, ((Get-Item -LiteralPath $WrapperExe).Length / 1MB))
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
        $xml = $xml.Replace('__SERVICE_ACCOUNT__', $ServiceAccount)
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
        Write-Warning "livekit.yaml was copied from the template. Fill in node_ip, turn.domain, the certificate paths and keys before the node serves traffic."
    }
}

# --- 4. service -----------------------------------------------------------------------

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Refresh the existing WinSW service registration')) {
        & $WrapperExe stop 2>&1 | Write-Verbose
        & $WrapperExe refresh 2>&1 | Write-Verbose
        Write-Host "refreshed service $ServiceName"
    }
}
elseif ($PSCmdlet.ShouldProcess($ServiceName, 'Install the WinSW service')) {
    & $WrapperExe install 2>&1 | Write-Verbose
    if ($LASTEXITCODE -ne 0) { throw "WinSW install failed with exit code $LASTEXITCODE." }
    Write-Host "created  service $ServiceName"
}

if ($NoStart) {
    Write-Host 'start    skipped (-NoStart).'
}
elseif ($PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
    & $WrapperExe start 2>&1 | Write-Verbose
    $started = Get-Service -Name $ServiceName
    $started.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    Write-Host "started  service $ServiceName ($($started.Status))"
}

# --- 5. firewall ----------------------------------------------------------------------

if ($SkipFirewall) {
    Write-Host 'firewall skipped (-SkipFirewall).'
}
else {
    foreach ($rule in $Ports) {
        Set-LiveKitFirewallRule -Name $rule.Name -Description $rule.Description `
            -Protocol $rule.Protocol -Port $rule.Port -RemoteAddress $rule.Remote
    }
}

Write-Host ''
Write-Host "LiveKit service ready. Logs: $LogDir"
