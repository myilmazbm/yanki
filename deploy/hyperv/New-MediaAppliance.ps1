<#
.SYNOPSIS
    Skeleton: builds an Ubuntu-based LiveKit media appliance as a Hyper-V VM.

.DESCRIPTION
    Fallback for Spike A. If LiveKit on Windows Server fails the spike criteria in
    docs\livekit-windows-spike.md, media moves to a small Linux appliance running on the
    customer's existing Hyper-V hosts. That keeps the promise of a small footprint: the
    customer still manages one hypervisor they already own, and the appliance is a sealed
    black box - no Linux administration, no extra product to license.

    The VM is a Generation 2 (UEFI, Secure Boot off for the Ubuntu image) machine booted
    from the official Ubuntu Server cloud image, seeded with cloud-init through a NoCloud
    ISO built from cloud-init\user-data and a generated meta-data. cloud-init installs
    livekit-server from the official release and writes the same livekit.yaml the Windows
    node uses.

    STATUS: skeleton. The steps are laid out, validated for parameters and safe to run
    with -WhatIf, but the image download and the seed-ISO build are marked TODO because
    they need a decision this milestone has not taken (which Ubuntu LTS, and whether the
    image is mirrored internally). Do not run it against production Hyper-V yet.

.PARAMETER Name
    VM name. Default talky-media-01.

.PARAMETER SwitchName
    Hyper-V virtual switch the VM attaches to. Must be an external switch on the media VLAN.

.PARAMETER ProcessorCount
    vCPUs. Default 16, matching the sizing for a media node.

.PARAMETER MemoryGB
    Static memory. Default 32. Dynamic memory is deliberately not used: media workloads
    are latency sensitive and ballooning causes jitter.

.PARAMETER VhdPath
    Where the VM disk is created. Default C:\HyperV\<Name>\<Name>.vhdx.

.PARAMETER IpAddress
    Static address for the appliance in CIDR form, e.g. 10.20.30.41/24. LiveKit's node_ip
    must be reachable by clients, so DHCP is not appropriate.

.PARAMETER Gateway
    Default gateway.

.PARAMETER DnsServers
    DNS servers.

.PARAMETER LiveKitVersion
    LiveKit release the appliance installs. Default 1.13.6.

.EXAMPLE
    .\New-MediaAppliance.ps1 -SwitchName 'Media vSwitch' -IpAddress 10.20.30.41/24 `
        -Gateway 10.20.30.1 -DnsServers 10.20.10.10,10.20.10.11 -WhatIf

.NOTES
    Requires an elevated session on a Hyper-V host with the Hyper-V PowerShell module.
    PowerShell 5.1 compatible.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Name = 'talky-media-01',
    [Parameter(Mandatory)][string] $SwitchName,
    [ValidateRange(2, 64)][int] $ProcessorCount = 16,
    [ValidateRange(4, 512)][int] $MemoryGB = 32,
    [string] $VhdPath,
    [Parameter(Mandatory)][string] $IpAddress,
    [Parameter(Mandatory)][string] $Gateway,
    [string[]] $DnsServers = @(),
    [string] $LiveKitVersion = '1.13.6'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) {
    if ($WhatIfPreference) {
        Write-Warning 'Not elevated. Continuing because -WhatIf was given.'
    }
    else {
        throw 'New-MediaAppliance.ps1 must run in an elevated PowerShell session on the Hyper-V host.'
    }
}

if (-not (Get-Module -ListAvailable -Name Hyper-V)) {
    throw 'The Hyper-V PowerShell module is not available on this host.'
}

if ($IpAddress -notmatch '^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$') {
    throw "IpAddress must be in CIDR form, e.g. 10.20.30.41/24."
}

if (-not $VhdPath) { $VhdPath = "C:\HyperV\$Name\$Name.vhdx" }
$vmDir = Split-Path -Parent $VhdPath
$seedIso = Join-Path $vmDir 'seed.iso'
$userDataTemplate = Join-Path $PSScriptRoot 'cloud-init\user-data'

Write-Host "Media appliance $Name"
Write-Host "  switch      $SwitchName"
Write-Host "  cpu/memory  $ProcessorCount vCPU / $MemoryGB GB (static)"
Write-Host "  disk        $VhdPath"
Write-Host "  address     $IpAddress via $Gateway"
Write-Host ("  dns         {0}" -f $(if ($DnsServers.Count) { $DnsServers -join ', ' } else { '(none - set -DnsServers)' }))
Write-Host "  livekit     $LiveKitVersion"
Write-Host ''

if (Get-VM -Name $Name -ErrorAction SilentlyContinue) {
    throw "A VM named $Name already exists. Remove it first, or pass a different -Name."
}

if (-not (Test-Path -LiteralPath $vmDir)) {
    if ($PSCmdlet.ShouldProcess($vmDir, 'Create VM directory')) {
        New-Item -ItemType Directory -Path $vmDir -Force | Out-Null
    }
}

# --- 1. base image ---------------------------------------------------------------------
# TODO(M1): decide the Ubuntu LTS and whether the cloud image is mirrored internally,
# then download <ubuntu>-server-cloudimg-amd64.vhdx (or convert the .img with qemu-img),
# verify it against the published SHA256SUMS, and copy it to $VhdPath.
if ($PSCmdlet.ShouldProcess($VhdPath, 'Provision the Ubuntu cloud image (TODO)')) {
    Write-Warning 'Base image provisioning is not implemented yet; see the TODO in this script.'
}

# --- 2. cloud-init seed ------------------------------------------------------------------
# TODO(M1): render $userDataTemplate (substituting the address, gateway, DNS, LiveKit
# version and the node's keys), write a matching meta-data file and build a NoCloud ISO
# labelled CIDATA. Windows has no mkisofs, so either ship a small ISO writer or build the
# seed on the appliance's first boot from a second VHDX formatted as FAT32 with that label.
if (-not (Test-Path -LiteralPath $userDataTemplate)) {
    throw "cloud-init template not found: $userDataTemplate"
}
if ($PSCmdlet.ShouldProcess($seedIso, 'Build the cloud-init NoCloud seed (TODO)')) {
    Write-Warning 'Seed ISO generation is not implemented yet; see the TODO in this script.'
}

# --- 3. the VM itself --------------------------------------------------------------------

if ($PSCmdlet.ShouldProcess($Name, "Create Generation 2 VM with $ProcessorCount vCPU and $MemoryGB GB")) {
    New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) `
        -VHDPath $VhdPath -SwitchName $SwitchName -Path $vmDir | Out-Null
    Set-VM -Name $Name -ProcessorCount $ProcessorCount -StaticMemory `
        -AutomaticStartAction Start -AutomaticStopAction Shutdown -CheckpointType Disabled
    # The Ubuntu cloud image is not signed for the Windows UEFI CA.
    Set-VMFirmware -VMName $Name -EnableSecureBoot Off
    Write-Host "created  VM $Name"
}

if ($PSCmdlet.ShouldProcess($Name, 'Attach the cloud-init seed ISO')) {
    Add-VMDvdDrive -VMName $Name -Path $seedIso
    Write-Host "attached $seedIso"
}

Write-Host ''
Write-Host 'Skeleton finished. Remaining work is marked TODO(M1) in this script.'
Write-Host 'Once the VM boots, verify with: lk load-test against ws://<ip>:7880 and the'
Write-Host 'pass criteria in docs\livekit-windows-spike.md.'
