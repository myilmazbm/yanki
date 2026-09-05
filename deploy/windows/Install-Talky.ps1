<#
.SYNOPSIS
    Installs or upgrades a Talky node on Windows Server: folders, ACLs, configuration,
    the "Talky" Windows service, firewall rules, and a final `talky doctor` run.

.DESCRIPTION
    Idempotent. Running it again on an installed node stops the service, refreshes the
    binaries and service registration and starts it back up; nothing is duplicated.

    What it does, in order:

      1. verifies elevation, the role list and the payload;
      2. creates InstallDir, DataDir, DataDir\logs and DataDir\index;
      3. grants the service account Read & Execute on InstallDir and Modify on DataDir
         (skipped for LocalSystem, which already has both);
      4. copies the publish output into InstallDir (service stopped first);
      5. renders deploy\windows\appsettings.template.json into DataDir\appsettings.json,
         backing up any existing file;
      6. registers or updates the "Talky" service: DisplayName "Talky",
         Automatic (Delayed Start), recovery restart after 5 s / 10 s / 30 s;
      7. adds the firewall rules through Set-TalkyFirewall.ps1;
      8. starts the service and runs `talky doctor`.

    Service identity: use a gMSA (passwordless) in production, LocalSystem in a lab.
    The script deliberately accepts no password: a Talky node must never run under a
    standing user account with a stored secret. The gMSA must already be installed on
    the host (Install-ADServiceAccount) and hold "Log on as a service"; both are
    reported by Test-TalkyPrerequisites.ps1.

    Every state change honours -WhatIf.

.PARAMETER InstallDir
    Program directory. Default C:\Program Files\Talky.

.PARAMETER DataDir
    Data directory holding appsettings.json, logs and the Lucene index.
    Default %ProgramData%\Talky.

.PARAMETER ServiceAccount
    'LocalSystem' (lab) or a gMSA written as 'CORP\talky$'. Default LocalSystem.

.PARAMETER Roles
    One or more of api, worker, search, push-relay, all. Default all.

.PARAMETER Urls
    Kestrel endpoints. Default https://+:8443. Add http://+:8080 only when a load
    balancer terminates TLS on an isolated back-end network.

.PARAMETER SqlConnectionString
    Connection string written to Talky:Database:ConnectionString.

.PARAMETER CertificateThumbprint
    Thumbprint of the TLS certificate in LocalMachine\My that Kestrel binds.

.PARAMETER SourceDir
    Publish output to install from. Default <repo>\artifacts\publish, i.e. the result of
    dotnet publish server/src/Talky.Server -c Release -r win-x64 --self-contained
    -p:PublishSingleFile=true -o artifacts/publish.

.PARAMETER NodeId
    Node identity used in logs and by the cluster mesh. Default: the machine name, lowercased.

.PARAMETER MeshSeeds
    Host names or addresses of the other nodes for the gRPC full-mesh cluster bus,
    e.g. talky-01.corp,talky-02.corp. Empty for a single node.

.PARAMETER FileShare
    UNC path of the SMB/DFS share used for file storage.

.PARAMETER EnableHttp
    Also open TCP 8080 in the firewall.

.PARAMETER SkipFirewall
    Do not touch the firewall.

.PARAMETER SkipDoctor
    Do not run `talky doctor` at the end.

.PARAMETER NoStart
    Register the service but leave it stopped.

.PARAMETER AclOnly
    Only (re-)apply the folder ACLs. Useful after an MSI install that registered the
    service under a gMSA.

.PARAMETER ConfigOnly
    Only re-render DataDir\appsettings.json.

.PARAMETER Force
    Overwrite an existing appsettings.json without asking. A timestamped backup is kept
    either way.

.EXAMPLE
    .\Install-Talky.ps1 -WhatIf
    Dry run of a default single-node lab install.

.EXAMPLE
    .\Install-Talky.ps1 -ServiceAccount 'CORP\talky$' `
        -Roles api,worker `
        -SqlConnectionString 'Server=sql-ag.corp;Database=Talky;Integrated Security=true;Encrypt=true;TrustServerCertificate=false' `
        -CertificateThumbprint 9A1B2C... `
        -MeshSeeds talky-01.corp,talky-02.corp,talky-03.corp `
        -FileShare \\corp\dfs\talky\files

.EXAMPLE
    .\Install-Talky.ps1 -AclOnly -ServiceAccount 'CORP\talky$'
    Re-apply the ACLs after an MSI install.

.NOTES
    Requires an elevated session. PowerShell 5.1 compatible.
    Related: Test-TalkyPrerequisites.ps1, Set-TalkyFirewall.ps1, Uninstall-Talky.ps1.
#>
# These four are read by Write-TalkyConfig from the script scope, which PSScriptAnalyzer
# cannot follow.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'SqlConnectionString',
    Justification = 'Read from the script scope by Write-TalkyConfig.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'CertificateThumbprint',
    Justification = 'Read from the script scope by Write-TalkyConfig.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'FileShare',
    Justification = 'Read from the script scope by Write-TalkyConfig.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Force',
    Justification = 'Read from the script scope by Write-TalkyConfig.')]
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'Talky'),
    [string] $DataDir = (Join-Path $env:ProgramData 'Talky'),
    [string] $ServiceAccount = 'LocalSystem',
    [string[]] $Roles = @('all'),
    [string[]] $Urls = @('https://+:8443'),
    [string] $SqlConnectionString = 'Server=localhost;Database=Talky;Integrated Security=true;Encrypt=true;TrustServerCertificate=true',
    [string] $CertificateThumbprint = '',
    [string] $SourceDir,
    [string] $NodeId = $env:COMPUTERNAME.ToLowerInvariant(),
    [string[]] $MeshSeeds = @(),
    [string] $FileShare = '',
    [switch] $EnableHttp,
    [switch] $SkipFirewall,
    [switch] $SkipDoctor,
    [switch] $NoStart,
    [switch] $AclOnly,
    [switch] $ConfigOnly,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Accept "a,b" / "a;b" as well as real arrays: when the script is launched with -File from
# cmd/bash, PowerShell passes list parameters as a single string.
$Urls = @($Urls | ForEach-Object { $_ -split '[,;]' } | Where-Object { $_ -ne '' })
$Roles = @($Roles | ForEach-Object { $_ -split '[,;]' } | Where-Object { $_ -ne '' })
$MeshSeeds = @($MeshSeeds | ForEach-Object { $_ -split '[,;]' } | Where-Object { $_ -ne '' })

$ServiceName = 'Talky'
$ServiceDisplayName = 'Talky'
$ServiceDescription = 'Talky server node (chat, calls, meetings, files).'
$TemplatePath = Join-Path $PSScriptRoot 'appsettings.template.json'
$FirewallScript = Join-Path $PSScriptRoot 'Set-TalkyFirewall.ps1'
$LogsDir = Join-Path $DataDir 'logs'
$IndexDir = Join-Path $DataDir 'index'

# --- helpers --------------------------------------------------------------------------

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Runs sc.exe and throws on a non-zero exit code, honouring -WhatIf.
function Invoke-Sc {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $Target,
        [Parameter(Mandatory)][string] $Action
    )
    if (-not $PSCmdlet.ShouldProcess($Target, $Action)) { return }
    $output = & "$env:SystemRoot\System32\sc.exe" @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($Arguments -join ' ') failed with exit code ${LASTEXITCODE}: $output"
    }
    Write-Verbose $output.Trim()
}

function New-TalkyDirectory {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([Parameter(Mandatory)][string] $Path)
    if (Test-Path -LiteralPath $Path) {
        Write-Host "exists   $Path"
        return
    }
    if (-not $PSCmdlet.ShouldProcess($Path, 'Create directory')) { return }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    Write-Host "created  $Path"
}

# Adds (or refreshes) a single inheriting ACE for $Identity on $Path. Existing ACEs for the
# same identity are removed first, so re-running does not pile up entries. Inheritance from
# the parent is left alone.
function Grant-TalkyAccess {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Identity,
        [Parameter(Mandatory)][ValidateSet('ReadAndExecute', 'Modify')][string] $Rights
    )

    if (-not $PSCmdlet.ShouldProcess($Path, "Grant $Rights to $Identity")) { return }
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Verbose "skip ACL, missing path: $Path"
        return
    }

    $acl = Get-Acl -LiteralPath $Path
    $stale = @($acl.Access | Where-Object {
            $_.IdentityReference.Value -eq $Identity -and -not $_.IsInherited
        })
    foreach ($ace in $stale) { $null = $acl.RemoveAccessRule($ace) }

    $rule = New-Object Security.AccessControl.FileSystemAccessRule(
        $Identity,
        [Security.AccessControl.FileSystemRights]$Rights,
        ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
         [Security.AccessControl.InheritanceFlags]::ObjectInherit),
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
    Write-Host "acl      $Identity : $Rights on $Path"
}

function ConvertTo-JsonScalar {
    <# Escapes a value for embedding in a JSON string literal. #>
    param([AllowEmptyString()][string] $Value)
    return $Value.Replace('\', '\\').Replace('"', '\"')
}

function Write-TalkyConfig {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)][string] $TemplatePath,
        [Parameter(Mandatory)][string] $Destination
    )

    if (-not (Test-Path -LiteralPath $TemplatePath)) {
        throw "Configuration template not found: $TemplatePath"
    }

    $seedsJson = ($MeshSeeds | ForEach-Object { '"' + (ConvertTo-JsonScalar $_) + '"' }) -join ', '
    # Talky:Server:Urls is a JSON array; the template carries the placeholder between the brackets.
    $urlsJson = ($Urls | ForEach-Object { '"' + (ConvertTo-JsonScalar $_) + '"' }) -join ', '

    $rendered = Get-Content -LiteralPath $TemplatePath -Raw
    $rendered = $rendered.Replace('__URLS__', $urlsJson)
    $rendered = $rendered.Replace('__SQL_CONNECTION_STRING__', (ConvertTo-JsonScalar $SqlConnectionString))
    $rendered = $rendered.Replace('__CERT_THUMBPRINT__', (ConvertTo-JsonScalar $CertificateThumbprint))
    $rendered = $rendered.Replace('__ROLES__', (ConvertTo-JsonScalar ($Roles -join ',')))
    $rendered = $rendered.Replace('__DATA_DIR__', (ConvertTo-JsonScalar $DataDir))
    $rendered = $rendered.Replace('__NODE_ID__', (ConvertTo-JsonScalar $NodeId))
    $rendered = $rendered -replace '"Seeds":\s*\[\]', ('"Seeds": [' + $seedsJson + ']')
    if ($FileShare) {
        $rendered = $rendered -replace '"ShareUnc":\s*""', ('"ShareUnc": "' + (ConvertTo-JsonScalar $FileShare) + '"')
    }
    if (-not $CertificateThumbprint) {
        # Lab mode: no certificate in the store. The ASP.NET Core development certificate is only
        # accepted with AllowInsecureHttp; production nodes must supply -CertificateThumbprint.
        Write-Warning 'No -CertificateThumbprint given: rendering Certificate.Source=DevCert with AllowInsecureHttp=true (lab use only).'
        $rendered = $rendered -replace '"Source":\s*"Store"', '"Source": "DevCert"'
        $rendered = $rendered -replace '"AllowInsecureHttp":\s*false', '"AllowInsecureHttp": true'
    }

    # Fail before writing rather than leaving a node with unparsable configuration.
    try { $null = $rendered | ConvertFrom-Json }
    catch { throw "Rendered configuration is not valid JSON: $($_.Exception.Message)" }
    if ($rendered -match '__[A-Z_]+__') {
        throw "Rendered configuration still contains placeholders: $($Matches[0])"
    }

    if ((Test-Path -LiteralPath $Destination) -and -not $Force) {
        $backup = "$Destination.$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
        if ($PSCmdlet.ShouldProcess($Destination, "Back up to $(Split-Path -Leaf $backup) and rewrite")) {
            Copy-Item -LiteralPath $Destination -Destination $backup -Force
            Write-Host "backup   $backup"
        }
    }

    if ($PSCmdlet.ShouldProcess($Destination, 'Write appsettings.json')) {
        # UTF-8 without BOM: Microsoft.Extensions.Configuration.Json copes with a BOM, but
        # the file is also read by operators and by git.
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [IO.File]::WriteAllText($Destination, $rendered, $utf8)
        Write-Host "config   $Destination"
    }
}

function Get-TalkyExecutable {
    param([Parameter(Mandatory)][string] $Directory)
    foreach ($name in @('talky.exe', 'Talky.Server.exe')) {
        $candidate = Join-Path $Directory $name
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

# --- preconditions --------------------------------------------------------------------

if (-not (Test-Elevated)) {
    # A dry run only reads, and -ConfigOnly only writes inside DataDir, so both stay
    # usable from an unelevated shell (rendering config into a staging folder, for example).
    if ($WhatIfPreference) {
        Write-Warning 'Not elevated. Continuing because -WhatIf was given; a real install needs an elevated session.'
    }
    elseif ($ConfigOnly) {
        Write-Warning 'Not elevated. -ConfigOnly only writes inside DataDir; the write fails if that path needs administrator rights.'
    }
    else {
        throw 'Install-Talky.ps1 must run in an elevated PowerShell session.'
    }
}
if ($AclOnly -and $ConfigOnly) {
    throw 'Use either -AclOnly or -ConfigOnly, not both.'
}

# Accept both -Roles api,worker (from a PowerShell session) and -Roles "api,worker"
# (powershell.exe -File, which cannot split arrays itself).
function Split-List {
    param([string[]] $Value, [string] $Separators = ',;')
    return @($Value |
        ForEach-Object { $_ -split "[$Separators]" } |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ })
}

$Roles = Split-List -Value $Roles -Separators ','
$MeshSeeds = Split-List -Value $MeshSeeds -Separators ','
$Urls = Split-List -Value $Urls -Separators ';'

$validRoles = @('api', 'worker', 'search', 'push-relay', 'all')
foreach ($role in $Roles) {
    if ($validRoles -notcontains $role) {
        throw "Unknown role '$role'. Valid roles: $($validRoles -join ', ')."
    }
}
if ($Roles.Count -eq 0) { throw 'At least one role is required.' }
if ($Roles -contains 'all' -and $Roles.Count -gt 1) {
    throw "Role 'all' cannot be combined with other roles."
}
if ($ServiceAccount -ne 'LocalSystem' -and $ServiceAccount -notmatch '\\') {
    throw "ServiceAccount must be 'LocalSystem' or a domain account such as 'CORP\talky$'."
}

$isGmsa = ($ServiceAccount -ne 'LocalSystem')

Write-Host "Talky install"
Write-Host "  InstallDir      $InstallDir"
Write-Host "  DataDir         $DataDir"
Write-Host "  ServiceAccount  $ServiceAccount"
Write-Host "  Roles           $($Roles -join ',')"
Write-Host "  Urls            $($Urls -join ';')"
Write-Host "  NodeId          $NodeId"
Write-Host ''

# --- 1. folders -----------------------------------------------------------------------

if (-not $ConfigOnly) {
    foreach ($dir in @($InstallDir, $DataDir, $LogsDir, $IndexDir)) {
        New-TalkyDirectory -Path $dir
    }
}

# --- 2. ACLs --------------------------------------------------------------------------

if (-not $ConfigOnly) {
    if ($isGmsa) {
        Grant-TalkyAccess -Path $InstallDir -Identity $ServiceAccount -Rights ReadAndExecute
        Grant-TalkyAccess -Path $DataDir -Identity $ServiceAccount -Rights Modify
        # logs and index inherit from DataDir, but they are granted explicitly so the node
        # keeps working if an operator later breaks inheritance on one of them.
        Grant-TalkyAccess -Path $LogsDir -Identity $ServiceAccount -Rights Modify
        Grant-TalkyAccess -Path $IndexDir -Identity $ServiceAccount -Rights Modify
    }
    else {
        Write-Host 'acl      skipped: LocalSystem already has full access.'
    }
}

if ($AclOnly) {
    Write-Host ''
    Write-Host 'ACLs applied (-AclOnly). Nothing else was changed.'
    return
}

# --- 3. payload -----------------------------------------------------------------------

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if (-not $ConfigOnly) {
    if (-not $SourceDir) {
        $SourceDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'artifacts\publish'
    }
    if (-not (Test-Path -LiteralPath $SourceDir)) {
        throw "Payload not found: $SourceDir. Publish first, or pass -SourceDir."
    }
    if (-not (Get-TalkyExecutable -Directory $SourceDir)) {
        throw "No talky.exe in $SourceDir."
    }

    if ($service -and $service.Status -ne 'Stopped') {
        if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop service before replacing binaries')) {
            Stop-Service -Name $ServiceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
            Write-Host "stopped  service $ServiceName"
        }
    }

    if ($PSCmdlet.ShouldProcess($InstallDir, "Copy payload from $SourceDir")) {
        Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force
        Write-Host "copied   $SourceDir -> $InstallDir"
    }
}

# --- 4. configuration -----------------------------------------------------------------

Write-TalkyConfig -TemplatePath $TemplatePath -Destination (Join-Path $DataDir 'appsettings.json')

if ($ConfigOnly) {
    Write-Host ''
    Write-Host 'Configuration written (-ConfigOnly). Restart the service to pick it up.'
    return
}

# --- 5. service -----------------------------------------------------------------------

$exePath = Get-TalkyExecutable -Directory $InstallDir
if (-not $exePath -and -not $WhatIfPreference) {
    throw "No talky.exe in $InstallDir after the copy."
}
if (-not $exePath) { $exePath = Join-Path $InstallDir 'talky.exe' }

$binaryPath = '"{0}" --role {1} --data-dir "{2}"' -f $exePath, ($Roles -join ','), $DataDir

if ($service) {
    Invoke-Sc -Target $ServiceName -Action "Update binary path to: $binaryPath" `
        -Arguments @('config', $ServiceName, "binPath= $binaryPath")
}
else {
    if ($PSCmdlet.ShouldProcess($ServiceName, "Create service running: $binaryPath")) {
        New-Service -Name $ServiceName -BinaryPathName $binaryPath `
            -DisplayName $ServiceDisplayName -Description $ServiceDescription `
            -StartupType Automatic | Out-Null
        Write-Host "created  service $ServiceName"
    }
}

# Automatic (Delayed Start): give AD, DNS and SQL time to come up after a reboot.
Invoke-Sc -Target $ServiceName -Action 'Set start type to Automatic (Delayed Start)' `
    -Arguments @('config', $ServiceName, 'start= delayed-auto')

Invoke-Sc -Target $ServiceName -Action 'Set service description' `
    -Arguments @('description', $ServiceName, $ServiceDescription)

if ($isGmsa) {
    # A gMSA is passwordless: password= must be passed as an empty value.
    Invoke-Sc -Target $ServiceName -Action "Set logon account to $ServiceAccount (passwordless gMSA)" `
        -Arguments @('config', $ServiceName, "obj= $ServiceAccount", 'password= ')
}
else {
    Invoke-Sc -Target $ServiceName -Action 'Set logon account to LocalSystem' `
        -Arguments @('config', $ServiceName, 'obj= LocalSystem')
}

# Recovery: restart after 5 s, then 10 s, then 30 s; the failure count resets daily.
Invoke-Sc -Target $ServiceName -Action 'Configure recovery actions (restart 5 s / 10 s / 30 s)' `
    -Arguments @('failure', $ServiceName, 'reset= 86400', 'actions= restart/5000/restart/10000/restart/30000')

# --- 6. firewall ----------------------------------------------------------------------

if ($SkipFirewall) {
    Write-Host 'firewall skipped (-SkipFirewall).'
}
elseif (-not (Test-Path -LiteralPath $FirewallScript)) {
    Write-Warning "Set-TalkyFirewall.ps1 not found next to this script; firewall not configured."
}
else {
    $firewallArgs = @{}
    if ($EnableHttp) { $firewallArgs['EnableHttp'] = $true }
    if ($MeshSeeds.Count -eq 0) { $firewallArgs['SkipMesh'] = $true }
    if ($WhatIfPreference) { $firewallArgs['WhatIf'] = $true }
    & $FirewallScript @firewallArgs
}

# --- 7. start and doctor --------------------------------------------------------------

if ($NoStart) {
    Write-Host 'start    skipped (-NoStart).'
}
elseif ($PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
    Start-Service -Name $ServiceName
    $started = Get-Service -Name $ServiceName
    $started.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    Write-Host "started  service $ServiceName ($($started.Status))"
}

if ($SkipDoctor) {
    Write-Host 'doctor   skipped (-SkipDoctor).'
}
elseif ($PSCmdlet.ShouldProcess($exePath, 'Run talky doctor')) {
    Write-Host ''
    Write-Host "--- talky doctor ---"
    & $exePath doctor --data-dir $DataDir
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "talky doctor exited with $LASTEXITCODE. The node is installed but not healthy; see $LogsDir."
    }
}

Write-Host ''
Write-Host 'Install complete.'
