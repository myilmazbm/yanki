<#
.SYNOPSIS
    Publishes the Talky host and builds the per-machine Talky Server MSI with WiX v6.

.DESCRIPTION
    Runs `dotnet publish` for server/src/Talky.Server (win-x64, self-contained, single file),
    restores the local WiX dotnet tool, makes sure the required WiX extensions are present and
    then compiles deploy/windows/msi/Talky.Server.wxs into artifacts/Talky.Server-<version>.msi.

    Everything is written under <repo>/artifacts, which is git-ignored.

.PARAMETER Version
    MSI ProductVersion. Defaults to VersionPrefix from server/Directory.Build.props.
    Must be <major>.<minor>.<build> with major/minor <= 255 and build <= 65535.

.PARAMETER PublishDir
    Directory harvested into the MSI. Defaults to <repo>/artifacts/publish.

.PARAMETER OutputDir
    Where the .msi is written. Defaults to <repo>/artifacts.

.PARAMETER SkipPublish
    Reuse an existing publish output instead of running `dotnet publish` again.

.EXAMPLE
    .\Build-Msi.ps1
    Publish and build artifacts\Talky.Server-0.1.0.msi.

.EXAMPLE
    .\Build-Msi.ps1 -Version 1.2.3 -SkipPublish
    Re-package an already published tree under a different version.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $PublishDir,

    [string] $OutputDir,

    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$WxsPath = Join-Path $PSScriptRoot 'Talky.Server.wxs'
$TemplateDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# WiX extensions are resolved from the global extension cache; keep the versions in step
# with .config/dotnet-tools.json.
$WixVersion = '6.0.2'
$WixExtensions = @('WixToolset.Firewall.wixext')

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments,
        [string] $WorkingDirectory = $RepoRoot
    )
    Write-Verbose "$FilePath $($Arguments -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

if (-not $Version) {
    $propsPath = Join-Path $RepoRoot 'server\Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "Cannot infer version: $propsPath not found. Pass -Version explicitly."
    }
    $props = [xml](Get-Content -LiteralPath $propsPath -Raw)
    $node = $props.SelectSingleNode('//PropertyGroup/VersionPrefix')
    if (-not $node) { throw "VersionPrefix not found in $propsPath. Pass -Version explicitly." }
    $Version = $node.InnerText.Trim()
    Write-Verbose "Version inferred from Directory.Build.props: $Version"
}

if (-not $PublishDir) { $PublishDir = Join-Path $RepoRoot 'artifacts\publish' }
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'artifacts' }

if (-not $SkipPublish) {
    if ($PSCmdlet.ShouldProcess($PublishDir, 'dotnet publish Talky.Server (win-x64, self-contained, single file)')) {
        Invoke-Native dotnet @(
            'publish', 'server/src/Talky.Server',
            '-c', 'Release',
            '-r', 'win-x64',
            '--self-contained',
            '-p:PublishSingleFile=true',
            "-p:Version=$Version",
            '-o', $PublishDir,
            '-m:1', '-p:NodeReuse=false'
        )
    }
}

if (-not (Test-Path -LiteralPath $PublishDir)) {
    throw "Publish directory not found: $PublishDir. Run without -SkipPublish first."
}
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path

# The host assembly is renamed to talky.exe; tolerate the pre-rename name so packaging
# can be exercised before that change lands.
$exe = @('talky.exe', 'Talky.Server.exe') |
    ForEach-Object { Join-Path $PublishDir $_ } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $exe) {
    throw "No host executable (talky.exe or Talky.Server.exe) found in $PublishDir."
}
$exeName = Split-Path -Leaf $exe
Write-Verbose "Host executable: $exeName"

if (-not (Test-Path -LiteralPath $OutputDir)) {
    if ($PSCmdlet.ShouldProcess($OutputDir, 'Create output directory')) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }
}
$msiPath = Join-Path $OutputDir "Talky.Server-$Version.msi"

if ($PSCmdlet.ShouldProcess('wix', 'dotnet tool restore')) {
    Invoke-Native dotnet @('tool', 'restore')
}

foreach ($ext in $WixExtensions) {
    if ($PSCmdlet.ShouldProcess("$ext/$WixVersion", 'wix extension add -g')) {
        # Idempotent: adding an already cached extension is a no-op.
        Invoke-Native dotnet @('wix', 'extension', 'add', '-g', "$ext/$WixVersion")
    }
}

# WIX1149 warns that the MSI ServiceConfig / ServiceConfigFailureActions tables are
# documented as unreliable. We keep them anyway because the WixToolset.Util alternative
# cannot express graded recovery delays (5 s / 10 s / 30 s), and operations.md has a
# post-install `sc.exe qfailure Talky` verification step.
$wixArgs = @(
    'wix', 'build',
    '-arch', 'x64',
    '-nologo',
    '-sw1149',
    '-d', "PublishDir=$PublishDir",
    '-d', "TemplateDir=$TemplateDir",
    '-d', "ExeName=$exeName",
    '-d', "ProductVersion=$Version"
)
foreach ($ext in $WixExtensions) { $wixArgs += @('-ext', $ext) }
$wixArgs += @('-out', $msiPath, $WxsPath)

if ($PSCmdlet.ShouldProcess($msiPath, 'wix build')) {
    Invoke-Native dotnet $wixArgs
    $item = Get-Item -LiteralPath $msiPath
    Write-Host ("MSI: {0} ({1:N1} MB)" -f $item.FullName, ($item.Length / 1MB))
    Write-Output $item.FullName
}
