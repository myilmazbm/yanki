<#
.SYNOPSIS
    Validates a Talky Server MSI without installing anything on the current machine.

.DESCRIPTION
    Runs an administrative install (msiexec /a) into a throwaway folder, which extracts the
    payload and resolves the Directory table exactly as a real install would, then asserts:

      * the administrative install succeeded (exit code 0);
      * the host executable and the harvested payload were laid down, without .pdb files;
      * the MSI declares the "Talky" service with delayed automatic start and the
        5 s / 10 s / 30 s recovery actions;
      * the firewall exceptions for TCP 8443 and TCP 9443 are present and 8080 is not;
      * SERVICE_ACCOUNT defaults to LocalSystem and SERVICE_PASSWORD stays empty and hidden
        (gMSA accounts are passwordless);
      * UpgradeCode and ARP metadata are what operations expects.

    No service is registered and nothing is written outside the temp folder.

.PARAMETER MsiPath
    MSI to validate. Defaults to the newest artifacts\Talky.Server-*.msi.

.PARAMETER WorkDir
    Extraction folder. Defaults to a new folder under $env:TEMP; removed afterwards
    unless -KeepWorkDir is given.

.PARAMETER KeepWorkDir
    Leave the extracted tree in place for inspection.

.EXAMPLE
    .\Test-Msi.ps1 -Verbose

.EXAMPLE
    .\Test-Msi.ps1 -MsiPath D:\drop\Talky.Server-1.0.0.msi -KeepWorkDir
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $MsiPath,
    [string] $WorkDir,
    [switch] $KeepWorkDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path

if (-not $MsiPath) {
    $candidate = Get-ChildItem -Path (Join-Path $RepoRoot 'artifacts') -Filter 'Talky.Server-*.msi' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) { throw 'No artifacts\Talky.Server-*.msi found. Run Build-Msi.ps1 first.' }
    $MsiPath = $candidate.FullName
}
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path

if (-not $WorkDir) {
    $WorkDir = Join-Path $env:TEMP ('talky-msi-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
}

$script:Failures = New-Object System.Collections.Generic.List[string]

function Assert-True {
    param([Parameter(Mandatory)][bool] $Condition, [Parameter(Mandatory)][string] $Message)
    if ($Condition) {
        Write-Host "  [ok]   $Message"
    }
    else {
        Write-Host "  [FAIL] $Message"
        $script:Failures.Add($Message)
    }
}

function Get-MsiTable {
    <#
        Reads an MSI table through WindowsInstaller.Installer COM. Returns an array of
        string arrays, one per row. Opens the database read-only; never modifies the MSI.
    #>
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Query,
        [Parameter(Mandatory)][int] $ColumnCount
    )
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
    try {
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Query))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        $rows = @()
        while ($true) {
            $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
            if (-not $record) { break }
            $row = @()
            for ($i = 1; $i -le $ColumnCount; $i++) {
                $row += [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @($i))
            }
            $rows += , $row
        }
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        return , $rows
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

Write-Host "MSI:     $MsiPath ($('{0:N1}' -f ((Get-Item -LiteralPath $MsiPath).Length / 1MB)) MB)"
Write-Host "WorkDir: $WorkDir"

# --- Administrative install: extract, never register anything ---

if ($PSCmdlet.ShouldProcess($WorkDir, 'Administrative install (msiexec /a) extraction')) {
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    $log = Join-Path $WorkDir 'admin-install.log'
    $proc = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" `
        -ArgumentList @('/a', "`"$MsiPath`"", '/qn', "TARGETDIR=`"$WorkDir`"", '/l*v', "`"$log`"") `
        -Wait -PassThru -NoNewWindow
    Assert-True ($proc.ExitCode -eq 0) "msiexec /a exited with 0 (actual: $($proc.ExitCode); log: $log)"

    # An administrative install lays the tree out under the short names from the Directory
    # table (PFiles64\Talky, CommApp\Talky), so locate the payload instead of hard-coding it.
    $exe = Get-ChildItem -LiteralPath $WorkDir -Recurse -Filter '*.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    Assert-True ($null -ne $exe) 'host executable extracted'
    if ($exe) {
        Write-Host ('         host: {0} ({1:N1} MB) in {2}' -f $exe.Name, ($exe.Length / 1MB), $exe.DirectoryName)
        Assert-True (@(Get-ChildItem -LiteralPath $exe.DirectoryName -Filter '*.pdb').Count -eq 0) 'no .pdb files shipped'
    }
    $appSettings = @(Get-ChildItem -LiteralPath $WorkDir -Recurse -Filter 'appsettings.json' -ErrorAction SilentlyContinue)
    Assert-True ($appSettings.Count -ge 2) 'appsettings.json laid down in both the install folder and DATADIR'
}

# --- Metadata assertions (read straight from the MSI, no extraction required) ---

$props = @{}
foreach ($row in (Get-MsiTable -Path $MsiPath -Query 'SELECT `Property`,`Value` FROM `Property`' -ColumnCount 2)) {
    $props[$row[0]] = $row[1]
}

Assert-True ($props.ContainsKey('UpgradeCode') -and $props['UpgradeCode'] -eq '{21199CEE-DB76-4FBA-A333-D90BE768217D}') `
    'UpgradeCode is the stable Talky Server upgrade code'
Assert-True ($props.ContainsKey('ProductVersion')) `
    ('ProductVersion present: ' + $(if ($props.ContainsKey('ProductVersion')) { $props['ProductVersion'] } else { 'MISSING' }))
Assert-True ($props.ContainsKey('ALLUSERS') -and $props['ALLUSERS'] -eq '1') 'per-machine install (ALLUSERS=1)'
Assert-True ($props.ContainsKey('SERVICE_ACCOUNT') -and $props['SERVICE_ACCOUNT'] -eq 'LocalSystem') `
    'SERVICE_ACCOUNT defaults to LocalSystem'
Assert-True (-not $props.ContainsKey('SERVICE_PASSWORD') -or [string]::IsNullOrEmpty($props['SERVICE_PASSWORD'])) `
    'SERVICE_PASSWORD has no baked-in value (gMSA is passwordless)'
Assert-True ($props.ContainsKey('MsiHiddenProperties') -and $props['MsiHiddenProperties'] -match 'SERVICE_PASSWORD') `
    'SERVICE_PASSWORD is hidden from the install log'
Assert-True ($props.ContainsKey('SecureCustomProperties') -and $props['SecureCustomProperties'] -match 'SERVICE_ACCOUNT') `
    'SERVICE_ACCOUNT is a secure custom property (survives elevation)'
Assert-True ($props.ContainsKey('ARPHELPLINK') -and $props.ContainsKey('ARPURLINFOABOUT')) 'ARP metadata present'

$upgrade = Get-MsiTable -Path $MsiPath -Query 'SELECT `UpgradeCode`,`ActionProperty` FROM `Upgrade`' -ColumnCount 2
Assert-True ($upgrade.Count -ge 1) 'MajorUpgrade rows present'

$service = Get-MsiTable -Path $MsiPath `
    -Query 'SELECT `Name`,`DisplayName`,`ServiceType`,`StartType`,`StartName`,`Password`,`Arguments` FROM `ServiceInstall`' `
    -ColumnCount 7
Assert-True ($service.Count -eq 1) 'exactly one ServiceInstall row'
if ($service.Count -eq 1) {
    Assert-True ($service[0][0] -eq 'Talky') 'service name is "Talky"'
    Assert-True ($service[0][1] -eq 'Talky') 'service display name is "Talky"'
    Assert-True ($service[0][2] -eq '16') 'service type is ownProcess (16)'
    Assert-True ($service[0][3] -eq '2') 'service start type is auto (2)'
    Assert-True ($service[0][4] -eq '[SERVICE_ACCOUNT]') 'service account comes from SERVICE_ACCOUNT'
    Assert-True ($service[0][5] -eq '[SERVICE_PASSWORD]') 'service password comes from SERVICE_PASSWORD'
    Assert-True ($service[0][6] -match '--role') 'service arguments carry --role'
    Assert-True ($service[0][6] -match '--data-dir') 'service arguments carry --data-dir'
}

$control = Get-MsiTable -Path $MsiPath -Query 'SELECT `Name`,`Event` FROM `ServiceControl`' -ColumnCount 2
Assert-True ($control.Count -ge 1 -and $control[0][0] -eq 'Talky') 'ServiceControl targets the Talky service'

# ConfigType 3 = SERVICE_CONFIG_DELAYED_AUTO_START_INFO, Argument 1 = enabled.
$cfg = Get-MsiTable -Path $MsiPath -Query 'SELECT `Name`,`ConfigType`,`Argument` FROM `MsiServiceConfig`' -ColumnCount 3
Assert-True ($cfg.Count -ge 1 -and $cfg[0][1] -eq '3' -and $cfg[0][2] -eq '1') 'delayed automatic start configured'

$fa = Get-MsiTable -Path $MsiPath `
    -Query 'SELECT `Name`,`ResetPeriod`,`Actions`,`DelayActions` FROM `MsiServiceConfigFailureActions`' -ColumnCount 4
Assert-True ($fa.Count -ge 1) 'failure actions configured'
if ($fa.Count -ge 1) {
    Assert-True ($fa[0][2] -eq 'restart[~]restart[~]restart') "recovery actions are restart x3 (actual: $($fa[0][2]))"
    Assert-True ($fa[0][3] -eq '5000[~]10000[~]30000') "recovery delays are 5 s / 10 s / 30 s (actual: $($fa[0][3]))"
    Assert-True ($fa[0][1] -eq '86400') 'failure count resets after one day'
}

# The firewall extension prefixes its table with the WiX schema major version, which does not
# always track the tool version, so discover the table name instead of hard-coding it.
$fwTable = $null
foreach ($row in (Get-MsiTable -Path $MsiPath -Query 'SELECT `Name` FROM `_Tables`' -ColumnCount 1)) {
    if ($row[0] -match '^Wix\d+FirewallException$') { $fwTable = $row[0]; break }
}
Assert-True ($null -ne $fwTable) 'firewall extension table present'
$ports = @()
if ($fwTable) {
    foreach ($row in (Get-MsiTable -Path $MsiPath -Query "SELECT ``Name``,``Port``,``Protocol`` FROM ``$fwTable``" -ColumnCount 3)) {
        $ports += $row[1]
    }
}
Assert-True ($ports -contains '8443') 'firewall exception for TCP 8443'
Assert-True ($ports -contains '9443') 'firewall exception for TCP 9443'
Assert-True (-not ($ports -contains '8080')) 'TCP 8080 is NOT opened by the MSI (opt-in only)'

if (-not $KeepWorkDir -and (Test-Path -LiteralPath $WorkDir)) {
    if ($PSCmdlet.ShouldProcess($WorkDir, 'Remove extraction folder')) {
        Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
if ($script:Failures.Count -gt 0) {
    Write-Host "FAILED: $($script:Failures.Count) check(s)."
    $script:Failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host 'All MSI checks passed.'
