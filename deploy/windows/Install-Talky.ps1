<#
.SYNOPSIS
    Installs or upgrades a Talky node on Windows Server: folders, ACLs, configuration,
    the "Talky" Windows service, firewall rules, and a final `talky doctor` run.

.DESCRIPTION
    Idempotent. Running it again on an installed node stops the service, refreshes the
    binaries and service registration and starts it back up; nothing is duplicated. An
    existing DataDir\appsettings.json is kept unless -ConfigOnly or -Force is given, and
    the logon account of an existing service changes only when -ServiceAccount is passed.

    What it does, in order:

      1. verifies elevation, the role list and the payload;
      2. creates InstallDir, DataDir, DataDir\logs and DataDir\index;
      3. grants the service account Read & Execute on InstallDir and Modify on DataDir
         (skipped for LocalSystem, which already has both) and creates the Windows
         Event Log source "Talky" in the Application log;
      4. copies the publish output into InstallDir (service stopped first);
      5. renders deploy\windows\appsettings.template.json into DataDir\appsettings.json
         when the file does not exist yet (or with -Force), keeping a timestamped backup;
      6. applies the database schema with `talky migrate` (skip with -SkipMigrate);
      7. registers or updates the "Talky" service: DisplayName "Talky",
         Automatic (Delayed Start), recovery restart after 5 s / 10 s / 30 s;
      8. adds the firewall rules through Set-TalkyFirewall.ps1;
      9. starts the service and runs `talky doctor`.

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
    Default %ProgramData%\Talky for a new node; when the Talky service already exists, the
    --data-dir of its command line. A -DataDir that differs from that one is refused,
    except with -ConfigOnly, which only warns and renders into the given directory.

.PARAMETER ServiceAccount
    'LocalSystem' (lab) or a gMSA written as 'CORP\talky$'. Default LocalSystem for a
    new service; an existing service keeps its account unless this is passed.

.PARAMETER Roles
    One or more of api, worker, search, push-relay, all. Default all. Written to the
    configuration file, which is the single source for roles.

.PARAMETER Urls
    Kestrel endpoints, separated by ';'. Default https://+:8443, which needs
    -CertificateThumbprint. A list of http:// URLs only (for example http://+:8080) is a
    lab node and is rendered with AllowInsecureHttp=true. A mix of http and https is
    refused: for a TLS-terminating load balancer edit the rendered file by hand
    (docs/operations.md).

.PARAMETER SqlConnectionString
    Connection string written to Talky:Database:ConnectionString.

.PARAMETER CertificateThumbprint
    Thumbprint of the TLS certificate in LocalMachine\My that Kestrel binds. Required
    when -Urls contains an https URL.

.PARAMETER SourceDir
    Publish output to install from. Default <repo>\artifacts\publish, i.e. the result of
    dotnet publish server/src/Talky.Server -c Release -r win-x64 --self-contained
    -p:PublishSingleFile=true -o artifacts/publish.

.PARAMETER NodeId
    Node identity used in logs and by the cluster mesh. Default: the machine name, lowercased.

.PARAMETER FileShare
    UNC path of the SMB/DFS share used for file storage.

.PARAMETER EnableHttp
    Also open TCP 8080 in the firewall.

.PARAMETER SkipFirewall
    Do not touch the firewall.

.PARAMETER SkipDoctor
    Do not run `talky doctor` at the end.

.PARAMETER SkipMigrate
    Do not run `talky migrate`. For sites where a DBA applies the schema.

.PARAMETER NoStart
    Register the service but leave it stopped.

.PARAMETER AclOnly
    Only (re-)apply the folder ACLs and create the event log source. Used after an MSI
    install.

.PARAMETER ConfigOnly
    Only (re-)render DataDir\appsettings.json, keeping a timestamped backup of an
    existing file.

.PARAMETER Force
    Re-render an existing DataDir\appsettings.json during a full install instead of
    keeping it. A timestamped backup is kept.

.EXAMPLE
    .\Install-Talky.ps1 -Urls http://+:8080 -WhatIf
    Dry run of a single-node lab install without a certificate.

.EXAMPLE
    .\Install-Talky.ps1 -ServiceAccount 'CORP\talky$' `
        -Roles api,worker `
        -SqlConnectionString 'Server=sql-ag.corp;Database=Talky;Integrated Security=true;Encrypt=true;TrustServerCertificate=false' `
        -CertificateThumbprint 9A1B2C... `
        -FileShare \\corp\dfs\talky\files

.EXAMPLE
    .\Install-Talky.ps1 -AclOnly -ServiceAccount 'CORP\talky$'
    Apply the ACLs and the event log source after an MSI install.

.NOTES
    Requires an elevated session. PowerShell 5.1 compatible.
    Related: Test-TalkyPrerequisites.ps1, Set-TalkyFirewall.ps1, Uninstall-Talky.ps1.
#>
# These are read by Write-TalkyConfig from the script scope, which PSScriptAnalyzer
# cannot follow.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'SqlConnectionString',
    Justification = 'Read from the script scope by Write-TalkyConfig.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'CertificateThumbprint',
    Justification = 'Read from the script scope by Write-TalkyConfig.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'FileShare',
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
    [string] $FileShare = '',
    [switch] $EnableHttp,
    [switch] $SkipFirewall,
    [switch] $SkipMigrate,
    [switch] $SkipDoctor,
    [switch] $NoStart,
    [switch] $AclOnly,
    [switch] $ConfigOnly,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A trailing backslash (tab completion adds one) would escape the closing quote of
# --data-dir "<DataDir>" on the service command line.
$DataDir = $DataDir.TrimEnd('\')

$ServiceName = 'Talky'
$ServiceDisplayName = 'Talky'
$ServiceDescription = 'Talky server node (chat, calls, meetings, files).'

# Returns the --data-dir value of a service command line, or $null when there is none.
# Both writers quote it: this script ("<DataDir>") and the MSI ("<DATADIR>\.", the trailing
# dot keeps the backslash from escaping the closing quote).
function Get-TalkyDataDirFromCommandLine {
    param([AllowEmptyString()][AllowNull()][string] $CommandLine)
    if ("$CommandLine" -notmatch '--data-dir\s+"([^"]+)"') { return $null }
    $value = $Matches[1]
    if ($value.EndsWith('\.')) { $value = $value.Substring(0, $value.Length - 2) }
    $value = $value.TrimEnd('\')
    if (-not $value) { return $null }
    return $value
}

# An installed node keeps its data directory: without -DataDir it is taken from the existing
# service, and a -DataDir that points elsewhere is refused instead of silently installing a
# second configuration next to the real one.
$installedService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
if ($installedService) {
    $installedDataDir = Get-TalkyDataDirFromCommandLine $installedService.PathName
    if (-not $installedDataDir) {
        throw ("Cannot read --data-dir ""..."" from the existing $ServiceName service command line: " +
            "$($installedService.PathName). This run would use DataDir $DataDir. Fix the service " +
            "command line or remove the service (Uninstall-Talky.ps1) and run again.")
    }
    if ($PSBoundParameters.ContainsKey('DataDir')) {
        if ($DataDir -ne $installedDataDir -and $ConfigOnly) {
            # -ConfigOnly never touches the service, so rendering elsewhere (a preview) is allowed.
            Write-Warning ("-DataDir $DataDir differs from the data directory of the existing $ServiceName " +
                "service, $installedDataDir. -ConfigOnly renders into $DataDir; the service keeps reading $installedDataDir.")
        }
        elseif ($DataDir -ne $installedDataDir) {
            throw ("-DataDir $DataDir differs from the data directory of the existing $ServiceName " +
                "service, $installedDataDir. Pass -DataDir $installedDataDir, or remove the service first " +
                "(Uninstall-Talky.ps1) to move the node.")
        }
    }
    else {
        $DataDir = $installedDataDir
    }
}

$TemplatePath = Join-Path $PSScriptRoot 'appsettings.template.json'
$FirewallScript = Join-Path $PSScriptRoot 'Set-TalkyFirewall.ps1'
$LogsDir = Join-Path $DataDir 'logs'
$IndexDir = Join-Path $DataDir 'index'
$ConfigPath = Join-Path $DataDir 'appsettings.json'
$ExeName = 'talky.exe'

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

# Changes the service through the SCM (Win32_Service.Change), honouring -WhatIf. Used for
# values that sc.exe would need embedded quotes or an empty argument for, which Windows
# PowerShell 5.1 and PowerShell 7 pass to native commands differently.
function Set-TalkyService {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)][hashtable] $Change,
        [Parameter(Mandatory)][string] $Action
    )
    if (-not $PSCmdlet.ShouldProcess($ServiceName, $Action)) { return }
    $result = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" |
        Invoke-CimMethod -MethodName Change -Arguments $Change
    if ($null -eq $result -or $result.ReturnValue -ne 0) {
        $code = if ($null -eq $result) { 'no result' } else { $result.ReturnValue }
        throw "Win32_Service.Change ($Action) failed: $code."
    }
    Write-Host "service  $Action"
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

    # Talky:Server:Urls is a JSON array; the template carries the placeholder between the brackets.
    $urlsJson = ($Urls | ForEach-Object { '"' + (ConvertTo-JsonScalar $_) + '"' }) -join ', '

    $rendered = Get-Content -LiteralPath $TemplatePath -Raw
    $rendered = $rendered.Replace('__URLS__', $urlsJson)
    $rendered = $rendered.Replace('__SQL_CONNECTION_STRING__', (ConvertTo-JsonScalar $SqlConnectionString))
    $rendered = $rendered.Replace('__CERT_THUMBPRINT__', (ConvertTo-JsonScalar $CertificateThumbprint))
    $rendered = $rendered.Replace('__ROLES__', (ConvertTo-JsonScalar ($Roles -join ',')))
    $rendered = $rendered.Replace('__DATA_DIR__', (ConvertTo-JsonScalar $DataDir))
    $rendered = $rendered.Replace('__NODE_ID__', (ConvertTo-JsonScalar $NodeId))
    if ($FileShare) {
        $rendered = $rendered -replace '"ShareUnc":\s*""', ('"ShareUnc": "' + (ConvertTo-JsonScalar $FileShare) + '"')
    }
    if ($httpOnly) {
        # Lab node without TLS (checked before anything was written). Without a thumbprint the
        # certificate section is never used, but it must still validate.
        Write-Warning 'Only http:// URLs: rendering AllowInsecureHttp=true (lab use only).'
        $rendered = $rendered -replace '"AllowInsecureHttp":\s*false', '"AllowInsecureHttp": true'
        if (-not $CertificateThumbprint) {
            $rendered = $rendered -replace '"Source":\s*"Store"', '"Source": "DevCert"'
        }
    }

    # Fail before writing rather than leaving a node with unparsable configuration.
    try { $null = $rendered | ConvertFrom-Json }
    catch { throw "Rendered configuration is not valid JSON: $($_.Exception.Message)" }
    if ($rendered -match '__[A-Z_]+__') {
        throw "Rendered configuration still contains placeholders: $($Matches[0])"
    }

    if (Test-Path -LiteralPath $Destination) {
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

# An existing appsettings.json is the node's configuration: keep it (hand edits such as
# KnownProxies or PublicUrl included) unless the operator asks for a re-render.
$configExists = Test-Path -LiteralPath $ConfigPath
$renderConfig = (-not $AclOnly) -and ($ConfigOnly -or $Force -or -not $configExists)

$httpsUrls = @($Urls | Where-Object { $_ -like 'https://*' })
$httpOnly = ($Urls.Count -gt 0) -and ($httpsUrls.Count -eq 0)
if ($renderConfig) {
    # Stop before anything is written rather than render a configuration that cannot start.
    if ($httpsUrls.Count -gt 0 -and -not $CertificateThumbprint) {
        throw ("-Urls contains an https URL but no -CertificateThumbprint was given. Pass " +
            "-CertificateThumbprint <thumbprint of a LocalMachine\My certificate>, or use an http-only " +
            "lab URL list such as -Urls http://+:8080.")
    }
    if ($httpsUrls.Count -gt 0 -and $httpsUrls.Count -ne $Urls.Count) {
        throw ("-Urls mixes http and https. The installer renders AllowInsecureHttp=true only for an " +
            "http-only lab node; for a TLS-terminating load balancer set Urls, AllowInsecureHttp and " +
            "KnownProxies in appsettings.json by hand (docs/operations.md).")
    }
}

Write-Host "Talky install"
Write-Host "  InstallDir      $InstallDir"
Write-Host "  DataDir         $DataDir"
Write-Host "  ServiceAccount  $ServiceAccount"
# These come from the parameters, so they are shown only when they are going to be written.
if ($renderConfig) {
    Write-Host "  Roles           $($Roles -join ',')"
    Write-Host "  Urls            $($Urls -join ';')"
    Write-Host "  NodeId          $NodeId"
}
Write-Host ''

# --- 1. folders -----------------------------------------------------------------------

if (-not $ConfigOnly) {
    foreach ($dir in @($InstallDir, $DataDir, $LogsDir, $IndexDir)) {
        New-TalkyDirectory -Path $dir
    }
}

# --- 2. ACLs and event log source -----------------------------------------------------

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

    # The host does not create its event log source (a gMSA cannot), so the installer does.
    if ($PSCmdlet.ShouldProcess("Application log", "Create event log source $ServiceName if missing")) {
        if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
            Write-Host "exists   event log source $ServiceName"
        }
        else {
            [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, 'Application')
            Write-Host "created  event log source $ServiceName (Application log)"
        }
    }
}

if ($AclOnly) {
    Write-Host ''
    Write-Host 'ACLs and event log source applied (-AclOnly). Nothing else was changed.'
    return
}

# --- 3. payload -----------------------------------------------------------------------

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$exePath = Join-Path $InstallDir $ExeName

if (-not $ConfigOnly) {
    if (-not $SourceDir) {
        $SourceDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'artifacts\publish'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir $ExeName))) {
        throw "No $ExeName in $SourceDir. Publish first, or pass -SourceDir."
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

if ($renderConfig) {
    Write-TalkyConfig -TemplatePath $TemplatePath -Destination $ConfigPath
}
else {
    Write-Host "config   kept existing $ConfigPath (re-render with -ConfigOnly or -Force)"
    # Captured first: inside the Where-Object script block $PSBoundParameters is its own.
    $bound = $PSBoundParameters
    $notApplied = @('Urls', 'SqlConnectionString', 'CertificateThumbprint', 'Roles', 'NodeId', 'FileShare' |
        Where-Object { $bound.ContainsKey($_) })
    if ($notApplied.Count -gt 0) {
        Write-Warning ("Not applied, because the existing $ConfigPath was kept: " +
            "$(($notApplied | ForEach-Object { "-$_" }) -join ', '). Add -Force to re-render it from these parameters (a backup is kept).")
    }
}

if ($ConfigOnly) {
    Write-Host ''
    Write-Host 'Configuration written (-ConfigOnly). Restart the service to pick it up.'
    return
}

# --- 5. schema ------------------------------------------------------------------------

if ($SkipMigrate) {
    Write-Host 'migrate  skipped (-SkipMigrate).'
}
elseif ($PSCmdlet.ShouldProcess($exePath, 'Run talky migrate (apply the database schema)')) {
    Write-Host ''
    Write-Host '--- talky migrate ---'
    & $exePath migrate --data-dir $DataDir
    if ($LASTEXITCODE -ne 0) {
        throw "talky migrate exited with $LASTEXITCODE; the service was not started. See $LogsDir, or pass -SkipMigrate when a DBA applies the schema."
    }
}

# --- 6. service -----------------------------------------------------------------------

$binaryPath = '"{0}" --data-dir "{1}"' -f $exePath, $DataDir

if ($service) {
    Set-TalkyService -Change @{ PathName = $binaryPath } -Action "Update binary path to: $binaryPath"
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
    -Arguments @('config', $ServiceName, 'start=', 'delayed-auto')

Invoke-Sc -Target $ServiceName -Action 'Set service description' `
    -Arguments @('description', $ServiceName, $ServiceDescription)

if ($service -and -not $PSBoundParameters.ContainsKey('ServiceAccount')) {
    Write-Host 'account  unchanged (pass -ServiceAccount to change the logon account)'
}
else {
    # A gMSA is passwordless and LocalSystem has none: both take an empty password.
    Set-TalkyService -Change @{ StartName = $ServiceAccount; StartPassword = '' } `
        -Action "Set logon account to $ServiceAccount"
}

# Recovery: restart after 5 s, then 10 s, then 30 s; the failure count resets daily.
Invoke-Sc -Target $ServiceName -Action 'Configure recovery actions (restart 5 s / 10 s / 30 s)' `
    -Arguments @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/10000/restart/30000')

# --- 7. firewall ----------------------------------------------------------------------

if ($SkipFirewall) {
    Write-Host 'firewall skipped (-SkipFirewall).'
}
elseif (-not (Test-Path -LiteralPath $FirewallScript)) {
    Write-Warning "Set-TalkyFirewall.ps1 not found next to this script; firewall not configured."
}
else {
    $firewallArgs = @{}
    if ($EnableHttp) { $firewallArgs['EnableHttp'] = $true }
    if ($WhatIfPreference) { $firewallArgs['WhatIf'] = $true }
    & $FirewallScript @firewallArgs
    # The firewall step opens 8443 (and 8080 with -EnableHttp); it does not read -Urls.
    $closedHttpUrls = @($Urls | Where-Object { $_ -like 'http://*' -and -not ($EnableHttp -and $_ -match ':8080/?$') })
    # Only when -Urls was written: on a kept config the 'not applied' warning covers -Urls.
    if ($renderConfig -and $httpOnly -and $closedHttpUrls.Count -gt 0) {
        Write-Warning ("-Urls $($closedHttpUrls -join ';') is not opened in the firewall, so other machines cannot " +
            "reach this node. Open it with: & '$FirewallScript' -EnableHttp -HttpPort <port of that URL>")
    }
}

# --- 8. start and doctor --------------------------------------------------------------

$startError = $null
if ($NoStart) {
    Write-Host 'start    skipped (-NoStart).'
}
elseif ($PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
    try {
        Start-Service -Name $ServiceName
        $started = Get-Service -Name $ServiceName
        $started.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
        # Running is reported when the host starts, before Kestrel binds and the module
        # options are validated; a node that fails there exits a moment later. Require the
        # same process to be still running after a short settle (a recovery restart would
        # be a new process).
        $processId = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'").ProcessId
        Start-Sleep -Seconds 5
        $started.Refresh()
        $processIdNow = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'").ProcessId
        if ($started.Status -ne 'Running' -or $processIdNow -ne $processId) {
            throw "it reported Running but did not stay up (status $($started.Status) 5 s later)."
        }
        Write-Host "started  service $ServiceName ($($started.Status))"
    }
    catch {
        # Keep going so doctor can say why; the install still fails below.
        $startError = $_.Exception.Message
        Write-Warning "Service $ServiceName did not start: $startError"
    }
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

if ($startError) {
    throw "Service $ServiceName did not start: $startError Logs: $LogsDir (and the Application event log, source $ServiceName)."
}

Write-Host ''
Write-Host 'Install complete.'
