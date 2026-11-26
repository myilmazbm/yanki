<#
.SYNOPSIS
    Downloads an official LiveKit Windows release, verifies its SHA-256 and installs
    livekit-server.exe.

.DESCRIPTION
    Downloads only from github.com/livekit/livekit/releases. The release publishes a
    checksums.txt next to the archives; the downloaded zip is hashed and compared against
    the line for that file name. A mismatch aborts before anything is extracted.

    Idempotent: re-running with the same version reuses the cached download and reports
    that the installed binary is already current, unless -Force is given.

.PARAMETER Version
    Release to install. Default 1.13.11: the newest release that passed the full
    Test-LiveKitInstall.ps1 run (docs/livekit-windows-spike.md, section 1.3); raise it only
    together with a new passing run. 'latest' resolves the newest release through the
    GitHub API and is never the default: a media node must not change version by accident.

.PARAMETER InstallDir
    Where livekit-server.exe is placed. Default C:\Program Files\LiveKit.

.PARAMETER DownloadDir
    Cache for the archive and checksums. Default %TEMP%\livekit-download.

.PARAMETER Architecture
    amd64 (default) or arm64.

.PARAMETER Force
    Re-download and reinstall even if the target version is already installed.

.EXAMPLE
    .\Get-LiveKit.ps1 -WhatIf

.EXAMPLE
    .\Get-LiveKit.ps1

.EXAMPLE
    .\Get-LiveKit.ps1 -Version latest    # explicit choice; run Test-LiveKitInstall.ps1 on a lab VM first

.NOTES
    Requires an elevated session to write into C:\Program Files.
    PowerShell 5.1 compatible. Stop the LiveKit service before upgrading in place.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Version = '1.13.11',
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'LiveKit'),
    [string] $DownloadDir = (Join-Path $env:TEMP 'livekit-download'),
    [ValidateSet('amd64', 'arm64')]
    [string] $Architecture = 'amd64',
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Repo = 'livekit/livekit'
$UserAgent = 'yanki-deploy'

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Resolve-LiveKitVersion {
    param([Parameter(Mandatory)][string] $Requested)
    if ($Requested -ne 'latest') { return $Requested.TrimStart('v') }
    Write-Verbose 'Resolving the latest release through the GitHub API.'
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" `
        -Headers @{ 'User-Agent' = $UserAgent } -UseBasicParsing
    return ([string]$release.tag_name).TrimStart('v')
}

function Get-ExpectedHash {
    <# Reads the '<sha256>  <filename>' line for $FileName out of a checksums.txt. #>
    param(
        [Parameter(Mandatory)][string] $ChecksumsPath,
        [Parameter(Mandatory)][string] $FileName
    )
    foreach ($line in (Get-Content -LiteralPath $ChecksumsPath)) {
        $parts = $line -split '\s+', 2
        if ($parts.Count -eq 2 -and $parts[1].Trim().TrimStart('*') -eq $FileName) {
            return $parts[0].Trim().ToLowerInvariant()
        }
    }
    throw "checksums.txt has no entry for $FileName."
}

# SHA-256 of a file in lower case. Not Get-FileHash: in Windows PowerShell 5.1 that is a
# script function which honours a global WhatIfPreference (powershell.exe -File ... -WhatIf)
# and then returns nothing.
function Get-Sha256 {
    [OutputType([string])]
    param([Parameter(Mandatory)][string] $Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    # Resolved the PowerShell way (location, PSDrives) like the cmdlets that wrote the file;
    # .NET alone would resolve a relative path against the process directory.
    $stream = [IO.File]::OpenRead($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
    try { return (-join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') })) }
    finally { $stream.Dispose(); $sha.Dispose() }
}

$resolved = Resolve-LiveKitVersion -Requested $Version
$archive = "livekit_${resolved}_windows_${Architecture}.zip"
$baseUrl = "https://github.com/$Repo/releases/download/v$resolved"
$exeName = 'livekit-server.exe'
$targetExe = Join-Path $InstallDir $exeName

Write-Host "LiveKit $resolved ($Architecture)"

if ((Test-Path -LiteralPath $targetExe) -and -not $Force) {
    $installed = (& $targetExe --version 2>&1 | Out-String).Trim()
    # Anchored: 1.13.1 must not count as installed when 1.13.11 is.
    if ($installed -match ('(^|\s)v?' + [regex]::Escape($resolved) + '$')) {
        Write-Host "current  $targetExe is already $installed"
        return
    }
    Write-Host "replace  $targetExe currently reports: $installed"
}

if (-not (Test-Path -LiteralPath $DownloadDir)) {
    if ($PSCmdlet.ShouldProcess($DownloadDir, 'Create download cache')) {
        New-Item -ItemType Directory -Path $DownloadDir -Force | Out-Null
    }
}

$archivePath = Join-Path $DownloadDir $archive
$checksumsPath = Join-Path $DownloadDir "checksums-$resolved.txt"

if ($PSCmdlet.ShouldProcess("$baseUrl/checksums.txt", 'Download checksums')) {
    Invoke-WebRequest -Uri "$baseUrl/checksums.txt" -OutFile $checksumsPath -UseBasicParsing `
        -Headers @{ 'User-Agent' = $UserAgent }
    Write-Host "fetched  checksums.txt"
}

if ((Test-Path -LiteralPath $archivePath) -and -not $Force) {
    Write-Host "cached   $archivePath"
}
elseif ($PSCmdlet.ShouldProcess("$baseUrl/$archive", 'Download release archive')) {
    Invoke-WebRequest -Uri "$baseUrl/$archive" -OutFile $archivePath -UseBasicParsing `
        -Headers @{ 'User-Agent' = $UserAgent }
    Write-Host ("fetched  {0} ({1:N1} MB)" -f $archive, ((Get-Item -LiteralPath $archivePath).Length / 1MB))
}

if (Test-Path -LiteralPath $archivePath) {
    $expected = Get-ExpectedHash -ChecksumsPath $checksumsPath -FileName $archive
    $actual = Get-Sha256 -Path $archivePath
    if ($expected -ne $actual) {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
        throw "SHA-256 mismatch for ${archive}: expected $expected, got $actual. The download was deleted."
    }
    Write-Host "verified SHA-256 $actual"
}

$extractDir = Join-Path $DownloadDir "extract-$resolved"
if ($PSCmdlet.ShouldProcess($extractDir, 'Extract archive')) {
    if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDir -Force
}

$sourceExe = Join-Path $extractDir $exeName
if ($PSCmdlet.ShouldProcess($InstallDir, "Install $exeName")) {
    if (-not (Test-Path -LiteralPath $sourceExe)) {
        throw "$exeName not found in $archive."
    }
    if (-not (Test-Path -LiteralPath $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }
    Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force
    $reported = (& $targetExe --version 2>&1 | Out-String).Trim()
    Write-Host ("installed {0} ({1:N1} MB) -> {2}" -f $reported, ((Get-Item -LiteralPath $targetExe).Length / 1MB), $targetExe)
    Write-Host ''
    Write-Host 'Next: run Install-LiveKitService.ps1 -NoStart (it copies the livekit.yaml template'
    Write-Host 'next to the binary), fill in node_ip, turn.domain, the certificate paths and the API'
    Write-Host 'key in that copy, then run Install-LiveKitService.ps1 again. The template in the'
    Write-Host 'repository stays unfilled.'
}
