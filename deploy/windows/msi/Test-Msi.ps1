<#
.SYNOPSIS
    Validates a Talky Server MSI without installing anything on the current machine.

.DESCRIPTION
    Runs an administrative install (msiexec /a) into a throwaway folder, which extracts the
    payload and resolves the Directory table exactly as a real install would, then asserts:

      * the administrative install succeeded (exit code 0);
      * the host executable and the harvested payload were laid down, without .pdb files
        and without msalruntime.dll (excluded: no redistribution grant in its licence);
      * the MSI declares the "Talky" service with delayed automatic start;
      * the recovery policy (restart after 5 s / 10 s / 30 s, reset after a day) comes from
        the deferred, non-impersonated custom action ConfigureServiceRecovery running
        sc.exe after InstallServices, and NOT from the MsiServiceConfigFailureActions
        table, which fails on Windows Server 2025 with error 1939;
      * SERVICE_ACCOUNT and DATADIR are remembered under HKLM\SOFTWARE\Talky (a permanent
        component writes them, AppSearch reads them back) and a command-line value wins
        over the remembered one; SERVICE_ACCOUNT falls back to LocalSystem;
      * the MSI opens no firewall port (Set-TalkyFirewall.ps1 owns the firewall);
      * SERVICE_PASSWORD stays empty, hidden and is never written to the registry
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
    Assert-True ($appSettings.Count -ge 1) 'appsettings.json laid down in the install folder'
    $msalExtracted = @(Get-ChildItem -LiteralPath $WorkDir -Recurse -Filter 'msalruntime*.dll' -ErrorAction SilentlyContinue)
    Assert-True ($msalExtracted.Count -eq 0) "msalruntime.dll not laid down (no redistribution grant; actual: $($msalExtracted.Count))"
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
# SERVICE_ACCOUNT has no Property-table value: a static default would be indistinguishable
# from a command-line value. The default comes from DefaultServiceAccount (checked below).
Assert-True (-not $props.ContainsKey('SERVICE_ACCOUNT')) 'SERVICE_ACCOUNT has no static default in the Property table'
Assert-True (-not $props.ContainsKey('DATADIR')) 'DATADIR has no static value in the Property table'
Assert-True (-not $props.ContainsKey('SERVICE_PASSWORD') -or [string]::IsNullOrEmpty($props['SERVICE_PASSWORD'])) `
    'SERVICE_PASSWORD has no baked-in value (gMSA is passwordless)'
Assert-True ($props.ContainsKey('MsiHiddenProperties') -and $props['MsiHiddenProperties'] -match 'SERVICE_PASSWORD') `
    'SERVICE_PASSWORD is hidden from the install log'
$secure = @(if ($props.ContainsKey('SecureCustomProperties')) { $props['SecureCustomProperties'] -split ';' })
Assert-True ($secure -contains 'SERVICE_ACCOUNT') 'SERVICE_ACCOUNT is a secure custom property (survives elevation)'
Assert-True ($secure -contains 'DATADIR') 'DATADIR is a secure custom property (survives elevation)'
Assert-True ($props.ContainsKey('ARPHELPLINK') -and $props.ContainsKey('ARPURLINFOABOUT')) 'ARP metadata present'

# The File table stores "short|long" names; match the long name.
$msalFiles = @((Get-MsiTable -Path $MsiPath -Query 'SELECT `FileName` FROM `File`' -ColumnCount 1) |
    Where-Object { ($_[0] -split '\|')[-1] -like 'msalruntime*.dll' })
Assert-True ($msalFiles.Count -eq 0) "msalruntime.dll is not in the File table (actual: $($msalFiles.Count))"

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
    Assert-True ($service[0][6] -match '--data-dir') 'service arguments carry --data-dir'
}

$control = Get-MsiTable -Path $MsiPath -Query 'SELECT `Name`,`Event` FROM `ServiceControl`' -ColumnCount 2
Assert-True ($control.Count -ge 1 -and $control[0][0] -eq 'Talky') 'ServiceControl targets the Talky service'

# ConfigType 3 = SERVICE_CONFIG_DELAYED_AUTO_START_INFO, Argument 1 = enabled.
$cfg = Get-MsiTable -Path $MsiPath -Query 'SELECT `Name`,`ConfigType`,`Argument` FROM `MsiServiceConfig`' -ColumnCount 3
Assert-True ($cfg.Count -ge 1 -and $cfg[0][1] -eq '3' -and $cfg[0][2] -eq '1') 'delayed automatic start configured'

$tables = @((Get-MsiTable -Path $MsiPath -Query 'SELECT `Name` FROM `_Tables`' -ColumnCount 1) | ForEach-Object { $_[0] })

# --- Recovery policy -------------------------------------------------------------------
# The MsiServiceConfigFailureActions table fails on Windows Server 2025 (error 1939, access
# denied) and rolls the install back, so it must not come back.
$faRows = 0
if ($tables -contains 'MsiServiceConfigFailureActions') {
    $faRows = (Get-MsiTable -Path $MsiPath -Query 'SELECT `MsiServiceConfigFailureActions` FROM `MsiServiceConfigFailureActions`' -ColumnCount 1).Count
}
Assert-True ($faRows -eq 0) "no MsiServiceConfigFailureActions rows (actual: $faRows)"

$customActions = @{}
foreach ($row in (Get-MsiTable -Path $MsiPath -Query 'SELECT `Action`,`Type`,`Source`,`Target` FROM `CustomAction`' -ColumnCount 4)) {
    $customActions[$row[0]] = $row
}
$executeSeq = @{}
foreach ($row in (Get-MsiTable -Path $MsiPath -Query 'SELECT `Action`,`Condition`,`Sequence` FROM `InstallExecuteSequence`' -ColumnCount 3)) {
    $executeSeq[$row[0]] = $row
}
$uiSeq = @{}
foreach ($row in (Get-MsiTable -Path $MsiPath -Query 'SELECT `Action`,`Condition`,`Sequence` FROM `InstallUISequence`' -ColumnCount 3)) {
    $uiSeq[$row[0]] = $row
}
function Get-Seq {
    param([hashtable] $Table, [string] $Action)
    if ($Table.ContainsKey($Action)) { return [int]$Table[$Action][2] }
    return -1
}

# Same policy Install-Talky.ps1 sets with sc.exe on the script path.
$recoveryTarget = '"[System64Folder]sc.exe" failure Talky reset= 86400 actions= restart/5000/restart/10000/restart/30000'
$ca = if ($customActions.ContainsKey('ConfigureServiceRecovery')) { $customActions['ConfigureServiceRecovery'] } else { $null }
Assert-True ($null -ne $ca) 'custom action ConfigureServiceRecovery exists'
if ($ca) {
    # 3106 = 34 (exe, path in Target, working dir from Directory) + 0x400 deferred + 0x800 no
    # impersonation; no 0x40 bit, so a non-zero sc.exe exit fails the install.
    Assert-True ($ca[1] -eq '3106') "ConfigureServiceRecovery is type 34, deferred, not impersonated, return checked (Type 3106; actual: $($ca[1]))"
    Assert-True ($ca[2] -eq 'System64Folder') "ConfigureServiceRecovery runs in System64Folder (actual: $($ca[2]))"
    Assert-True ($ca[3] -ceq $recoveryTarget) "ConfigureServiceRecovery command line is: $recoveryTarget (actual: $($ca[3]))"
}
$caSeq = Get-Seq $executeSeq 'ConfigureServiceRecovery'
$installServicesSeq = Get-Seq $executeSeq 'InstallServices'
$finalizeSeq = Get-Seq $executeSeq 'InstallFinalize'
Assert-True ($caSeq -gt $installServicesSeq -and $installServicesSeq -gt 0 -and $caSeq -lt $finalizeSeq) `
    "ConfigureServiceRecovery runs after InstallServices and before InstallFinalize (sequence $caSeq; InstallServices $installServicesSeq)"
Assert-True ($caSeq -gt 0 -and $executeSeq['ConfigureServiceRecovery'][1] -ceq 'NOT REMOVE~="ALL"') `
    'ConfigureServiceRecovery is conditioned NOT REMOVE~="ALL" (install, repair, upgrade; not uninstall)'
Assert-True (-not $uiSeq.ContainsKey('ConfigureServiceRecovery')) 'ConfigureServiceRecovery is not in the UI sequence'

# --- Remembered SERVICE_ACCOUNT / DATADIR ------------------------------------------------
$registry = Get-MsiTable -Path $MsiPath -Query 'SELECT `Root`,`Key`,`Name`,`Value`,`Component_` FROM `Registry`' -ColumnCount 5
function Find-RegistryRow {
    param([string] $Name)
    # The leading comma keeps a one-row result an array of rows instead of unrolling it.
    return , @($registry | Where-Object { $_[0] -eq '2' -and $_[1] -eq 'SOFTWARE\Talky' -and $_[2] -eq $Name })
}
$regAccount = Find-RegistryRow 'ServiceAccount'
$regDataDir = Find-RegistryRow 'DataDir'
Assert-True ($regAccount.Count -eq 1 -and $regAccount[0][3] -eq '[SERVICE_ACCOUNT]') 'HKLM\SOFTWARE\Talky\ServiceAccount is written from [SERVICE_ACCOUNT]'
Assert-True ($regDataDir.Count -eq 1 -and $regDataDir[0][3] -eq '[DATADIR]') 'HKLM\SOFTWARE\Talky\DataDir is written from [DATADIR]'
Assert-True (@($registry | Where-Object { $_[3] -match 'SERVICE_PASSWORD' }).Count -eq 0) 'SERVICE_PASSWORD is never written to the registry'

$rememberComponent = if ($regAccount.Count -eq 1) { $regAccount[0][4] } else { '' }
Assert-True ($regDataDir.Count -eq 1 -and $regDataDir[0][4] -eq $rememberComponent) 'both remembered values belong to one component'
$comp = @((Get-MsiTable -Path $MsiPath -Query 'SELECT `Component`,`Attributes` FROM `Component`' -ColumnCount 2) |
    Where-Object { $_[0] -eq $rememberComponent })
if ($comp.Count -eq 1) {
    $attr = [int]$comp[0][1]
    # 0x10 msidbComponentAttributesPermanent, 0x100 msidbComponentAttributes64bit.
    Assert-True (($attr -band 0x10) -ne 0) "remembered-values component $rememberComponent is permanent (Attributes $attr)"
    Assert-True (($attr -band 0x100) -ne 0) "remembered-values component $rememberComponent writes the 64-bit registry view"
}
else {
    Assert-True $false "remembered-values component found (got '$rememberComponent')"
}

$appSearch = @{}
foreach ($row in (Get-MsiTable -Path $MsiPath -Query 'SELECT `Property`,`Signature_` FROM `AppSearch`' -ColumnCount 2)) { $appSearch[$row[0]] = $row[1] }
$locators = @{}
foreach ($row in (Get-MsiTable -Path $MsiPath -Query 'SELECT `Signature_`,`Root`,`Key`,`Name`,`Type` FROM `RegLocator`' -ColumnCount 5)) { $locators[$row[0]] = $row }
foreach ($pair in @(@('SERVICE_ACCOUNT', 'ServiceAccount'), @('DATADIR', 'DataDir'))) {
    $prop = $pair[0]; $valueName = $pair[1]
    $loc = if ($appSearch.ContainsKey($prop) -and $locators.ContainsKey($appSearch[$prop])) { $locators[$appSearch[$prop]] } else { $null }
    # Type 18 = msidbLocatorTypeRawValue (2) + msidbLocatorType64bit (16).
    Assert-True ($null -ne $loc -and $loc[1] -eq '2' -and $loc[2] -eq 'SOFTWARE\Talky' -and $loc[3] -eq $valueName -and $loc[4] -eq '18') `
        "AppSearch reads $prop from HKLM\SOFTWARE\Talky\$valueName (64-bit, raw)"
}

# Command line beats the remembered value; LocalSystem is the default of last resort.
$expectedSetters = @(
    @('SaveCmdLineServiceAccount', 'CMDLINE_SERVICE_ACCOUNT', '[SERVICE_ACCOUNT]', 'SERVICE_ACCOUNT', 'before'),
    @('SaveCmdLineDataDir', 'CMDLINE_DATADIR', '[DATADIR]', 'DATADIR', 'before'),
    @('RestoreCmdLineServiceAccount', 'SERVICE_ACCOUNT', '[CMDLINE_SERVICE_ACCOUNT]', 'CMDLINE_SERVICE_ACCOUNT', 'after'),
    @('RestoreCmdLineDataDir', 'DATADIR', '[CMDLINE_DATADIR]', 'CMDLINE_DATADIR', 'after'),
    @('DefaultServiceAccount', 'SERVICE_ACCOUNT', 'LocalSystem', 'NOT SERVICE_ACCOUNT', 'after')
)
foreach ($e in $expectedSetters) {
    $id = $e[0]
    $c = if ($customActions.ContainsKey($id)) { $customActions[$id] } else { $null }
    # Type 51 = set property, immediate, no execution-scheduling bits (runs in both sequences).
    Assert-True ($null -ne $c -and $c[1] -eq '51' -and $c[2] -eq $e[1] -and $c[3] -ceq $e[2]) "$id sets $($e[1]) = $($e[2]) (type 51)"
    foreach ($seqName in 'InstallExecuteSequence', 'InstallUISequence') {
        $table = if ($seqName -eq 'InstallUISequence') { $uiSeq } else { $executeSeq }
        $s = Get-Seq $table $id
        $appSearchSeq = Get-Seq $table 'AppSearch'
        $positionOk = if ($e[4] -eq 'before') { $s -gt 0 -and $s -lt $appSearchSeq } else { $s -gt $appSearchSeq -and $s -lt (Get-Seq $table 'CostFinalize') }
        Assert-True ($positionOk -and $table[$id][1] -ceq $e[3]) "$seqName`: $id runs $($e[4]) AppSearch with condition '$($e[3])' (sequence $s)"
    }
}
foreach ($seqName in 'InstallExecuteSequence', 'InstallUISequence') {
    $table = if ($seqName -eq 'InstallUISequence') { $uiSeq } else { $executeSeq }
    Assert-True ((Get-Seq $table 'DefaultServiceAccount') -gt (Get-Seq $table 'RestoreCmdLineServiceAccount')) `
        "$seqName`: the LocalSystem default is applied after the command-line value is restored"
}

# The firewall has one owner, Set-TalkyFirewall.ps1: the MSI must carry no firewall table.
$fwTables = @($tables | Where-Object { $_ -match 'FirewallException$' })
Assert-True ($fwTables.Count -eq 0) 'the MSI opens no firewall port'

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
