<#
.SYNOPSIS
    Creates, updates or removes the Windows Firewall inbound rules a Talky node needs.

.DESCRIPTION
    Talky listens on these inbound ports:

      TCP 8443  clients and API (HTTPS, terminated by Kestrel)          always
      TCP 8080  plain HTTP, only when a load balancer terminates TLS    opt-in

    The gRPC cluster mesh port does not exist before M2; no rule is created for it.

    Every rule is named "Talky - <purpose>" and grouped under "Talky", so
    `Get-NetFirewallRule -Group Talky` shows the complete footprint and
    -Remove cleans it up without touching anything else. This script is the only
    owner of Talky firewall rules; the MSI creates none.

    The script is idempotent: an existing rule is updated in place (port, profile,
    remote address, enabled state) instead of being duplicated.

.PARAMETER HttpsPort
    Client/API port. Default 8443.

.PARAMETER HttpPort
    Plain HTTP port opened only with -EnableHttp. Default 8080.

.PARAMETER EnableHttp
    Also open the plain HTTP port. Only for deployments where a load balancer
    terminates TLS on an isolated back-end network.

.PARAMETER FirewallProfile
    Firewall profiles the rules apply to. Default 'Domain' (production nodes are
    domain-joined). Use 'Any' in a workgroup lab.

.PARAMETER Remove
    Remove every Talky firewall rule instead of creating them.

.EXAMPLE
    .\Set-TalkyFirewall.ps1 -WhatIf
    Show what would change without touching the firewall.

.EXAMPLE
    .\Set-TalkyFirewall.ps1 -EnableHttp
    Open 8443 to the domain profile and 8080 to the local subnet.

.EXAMPLE
    .\Set-TalkyFirewall.ps1 -Remove
    Remove all rules in the "Talky" group.

.NOTES
    Requires an elevated session. PowerShell 5.1 compatible.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'FirewallProfile',
    Justification = 'Read from the script scope by Set-TalkyRule.')]
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateRange(1, 65535)][int] $HttpsPort = 8443,
    [ValidateRange(1, 65535)][int] $HttpPort = 8080,
    [switch] $EnableHttp,
    [ValidateSet('Any', 'Domain', 'Private', 'Public', 'Domain,Private')]
    [string] $FirewallProfile = 'Domain',
    [switch] $Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Group = 'Talky'

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Set-TalkyRule {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Description,
        [Parameter(Mandatory)][int] $Port,
        [string[]] $RemoteAddress = @('Any')
    )

    $existing = Get-NetFirewallRule -DisplayName $Name -ErrorAction SilentlyContinue
    if ($existing) {
        if ($PSCmdlet.ShouldProcess($Name, "Update inbound rule (TCP $Port, profile $FirewallProfile)")) {
            # The group is set at creation; -Group cannot be combined with -DisplayName here.
            Set-NetFirewallRule -DisplayName $Name `
                -Description $Description -Direction Inbound -Action Allow `
                -Protocol TCP -LocalPort $Port -Profile $FirewallProfile -RemoteAddress $RemoteAddress -Enabled True
            Write-Host "updated  $Name (TCP $Port)"
        }
    }
    else {
        if ($PSCmdlet.ShouldProcess($Name, "Create inbound rule (TCP $Port, profile $FirewallProfile)")) {
            New-NetFirewallRule -DisplayName $Name `
                -Group $Group -Description $Description -Direction Inbound -Action Allow `
                -Protocol TCP -LocalPort $Port -Profile $FirewallProfile -RemoteAddress $RemoteAddress -Enabled True | Out-Null
            Write-Host "created  $Name (TCP $Port)"
        }
    }
}

function Remove-TalkyRule {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([Parameter(Mandatory)][string] $Name)
    $existing = Get-NetFirewallRule -DisplayName $Name -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Host "absent   $Name"
        return
    }
    if ($PSCmdlet.ShouldProcess($Name, 'Remove inbound rule')) {
        Remove-NetFirewallRule -DisplayName $Name
        Write-Host "removed  $Name"
    }
}

if (-not (Test-Elevated)) {
    # A dry run only reads, so it stays useful from an unelevated shell.
    if ($WhatIfPreference) {
        Write-Warning 'Not elevated. Continuing because -WhatIf was given; a real run needs an elevated session.'
    }
    else {
        throw 'Set-TalkyFirewall.ps1 must run in an elevated PowerShell session.'
    }
}

$rules = @(
    @{ Name = 'Talky - HTTPS (clients and API)'; Port = $HttpsPort; Remote = @('Any')
       Description = 'Inbound TLS traffic for Talky clients, REST API and WebSocket sessions.' }
)
if ($EnableHttp) {
    $rules += @{ Name = 'Talky - HTTP (behind load balancer)'; Port = $HttpPort; Remote = @('LocalSubnet')
                 Description = 'Plain HTTP for a TLS-terminating load balancer on an isolated back-end network.' }
}

if ($Remove) {
    # Remove every rule in the group, including ones this invocation would not have created
    # (for example an 8080 rule added earlier with -EnableHttp).
    $inGroup = @(Get-NetFirewallRule -Group $Group -ErrorAction SilentlyContinue)
    if ($inGroup.Count -eq 0) {
        Write-Host "No firewall rules in group '$Group'."
    }
    foreach ($rule in $inGroup) { Remove-TalkyRule -Name $rule.DisplayName }
    return
}

foreach ($rule in $rules) {
    Set-TalkyRule -Name $rule.Name -Description $rule.Description -Port $rule.Port -RemoteAddress $rule.Remote
}

if (-not $WhatIfPreference) {
    Write-Host ''
    Get-NetFirewallRule -Group $Group -ErrorAction SilentlyContinue |
        Select-Object DisplayName, Enabled, Profile, Direction, Action |
        Format-Table -AutoSize
}
