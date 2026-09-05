<#
.SYNOPSIS
    Runs the LiveKit load test against a media node and records host CPU and network
    counters to CSV.

.DESCRIPTION
    This is the measurement half of Spike A (docs\livekit-windows-spike.md). It:

      1. downloads the official livekit-cli release (`lk`) and verifies its SHA-256
         against the release checksums.txt;
      2. starts `lk load-test` with the spike profile - by default 8 video publishers and
         100 subscribers in room "spike" for 10 minutes;
      3. samples the SFU host every -SampleIntervalSeconds while the test runs and writes
         one row per sample to a CSV: total CPU %, livekit-server process CPU % and
         working set, and network bytes in/out per second;
      4. prints a summary against the spike's pass criteria (SFU CPU < 60 %).

    Counters are read with Get-Counter using the English counter paths, falling back to
    the locale-independent Win32_PerfFormattedData_* CIM classes on a localised Windows.

    Packet loss and join success are reported by `lk` itself; its full output is written
    next to the CSV. Read both before filling in the spike results table.

.PARAMETER Url
    Signalling URL of the node under test, e.g. ws://media-01.talky.corp:7880.
    Defaults to $env:LIVEKIT_URL.

.PARAMETER ApiKey
    LiveKit API key. Defaults to $env:LIVEKIT_API_KEY.

.PARAMETER ApiSecret
    LiveKit API secret. Defaults to $env:LIVEKIT_API_SECRET. Prefer the environment
    variable so the secret does not end up in the shell history.

.PARAMETER NodeComputerName
    Host to sample counters from. Default: the local machine. Set it when the load
    generator and the SFU are different machines, which they should be.

.PARAMETER Room
    Room name. Default 'spike'.

.PARAMETER VideoPublishers
    Publishing participants. Default 8.

.PARAMETER Subscribers
    Subscribing participants. Default 100.

.PARAMETER Duration
    Test duration as accepted by lk, e.g. 10m, 30s, 1h. Default 10m.

.PARAMETER OutputDir
    Where the CSV and the lk log are written. Default <repo>\artifacts\livekit-spike.

.PARAMETER SampleIntervalSeconds
    Counter sampling interval. Default 5.

.PARAMETER CliVersion
    livekit-cli release to use, e.g. '2.18.6', or 'latest'. Pin it for a repeatable spike.

.PARAMETER SkipDownload
    Use an `lk` already on PATH or already cached.

.EXAMPLE
    $env:LIVEKIT_API_SECRET = '...'
    .\Invoke-LiveKitLoadTest.ps1 -Url ws://media-01.talky.corp:7880 -ApiKey talky `
        -NodeComputerName media-01

.EXAMPLE
    .\Invoke-LiveKitLoadTest.ps1 -WhatIf

.NOTES
    Run this from a load-generation host, never from the SFU itself: the generator's own
    CPU cost would otherwise be counted as SFU load.
    PowerShell 5.1 compatible.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Url = $env:LIVEKIT_URL,
    [string] $ApiKey = $env:LIVEKIT_API_KEY,
    [string] $ApiSecret = $env:LIVEKIT_API_SECRET,
    [string] $NodeComputerName = $env:COMPUTERNAME,
    [string] $Room = 'spike',
    [int] $VideoPublishers = 8,
    [int] $Subscribers = 100,
    [string] $Duration = '10m',
    [string] $OutputDir,
    [ValidateRange(1, 300)][int] $SampleIntervalSeconds = 5,
    [string] $CliVersion = '2.18.6',
    [switch] $SkipDownload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The CSV is read by other tools and by people on other machines: force '.' as the decimal
# separator regardless of the operator's regional settings.
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture

$CliRepo = 'livekit/livekit-cli'
$UserAgent = 'talky-deploy'
$CpuPassThreshold = 60

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $OutputDir) {
    $OutputDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'artifacts\livekit-spike'
}

function ConvertFrom-GoDuration {
    <# '10m' / '30s' / '1h' -> TimeSpan. #>
    param([Parameter(Mandatory)][string] $Value)
    if ($Value -notmatch '^(?<n>\d+)(?<u>[smh])$') {
        throw "Duration '$Value' must look like 30s, 10m or 1h."
    }
    switch ($Matches['u']) {
        's' { return [TimeSpan]::FromSeconds([int]$Matches['n']) }
        'm' { return [TimeSpan]::FromMinutes([int]$Matches['n']) }
        'h' { return [TimeSpan]::FromHours([int]$Matches['n']) }
    }
}

function Get-ExpectedHash {
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

function Install-LiveKitCli {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([Parameter(Mandatory)][string] $Version)

    $cacheDir = Join-Path $env:TEMP 'livekit-cli-download'
    if ($Version -eq 'latest') {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$CliRepo/releases/latest" `
            -Headers @{ 'User-Agent' = $UserAgent } -UseBasicParsing
        $Version = ([string]$release.tag_name).TrimStart('v')
    }
    $archive = "lk_${Version}_windows_amd64.zip"
    $extractDir = Join-Path $cacheDir "extract-$Version"
    $exe = Join-Path $extractDir 'lk.exe'
    if (Test-Path -LiteralPath $exe) {
        Write-Host "cached   $exe"
        return $exe
    }
    if (-not $PSCmdlet.ShouldProcess($archive, "Download livekit-cli $Version")) { return $exe }

    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
    $baseUrl = "https://github.com/$CliRepo/releases/download/v$Version"
    $archivePath = Join-Path $cacheDir $archive
    $checksumsPath = Join-Path $cacheDir "checksums-$Version.txt"

    Invoke-WebRequest -Uri "$baseUrl/checksums.txt" -OutFile $checksumsPath -UseBasicParsing `
        -Headers @{ 'User-Agent' = $UserAgent }
    Invoke-WebRequest -Uri "$baseUrl/$archive" -OutFile $archivePath -UseBasicParsing `
        -Headers @{ 'User-Agent' = $UserAgent }

    $expected = Get-ExpectedHash -ChecksumsPath $checksumsPath -FileName $archive
    $actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
        throw "SHA-256 mismatch for ${archive}: expected $expected, got $actual. The download was deleted."
    }
    Write-Host ("verified {0} SHA-256 {1}" -f $archive, $actual)

    if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDir -Force
    if (-not (Test-Path -LiteralPath $exe)) { throw "lk.exe not found in $archive." }
    return $exe
}

function Get-NodeSample {
    <#
        One counter sample from $NodeComputerName. Returns $null if neither Get-Counter
        nor the CIM fallback produced anything.
    #>
    param([Parameter(Mandatory)][string] $ComputerName)

    $paths = @(
        '\Processor(_Total)\% Processor Time',
        '\Process(livekit-server)\% Processor Time',
        '\Process(livekit-server)\Working Set - Private',
        '\Network Interface(*)\Bytes Received/sec',
        '\Network Interface(*)\Bytes Sent/sec'
    )
    try {
        $sample = Get-Counter -Counter $paths -ComputerName $ComputerName -MaxSamples 1 -ErrorAction Stop
        $values = $sample.CounterSamples
        $pick = {
            param($suffix)
            @($values | Where-Object { $_.Path -like "*$suffix" })
        }
        $bytesIn = (& $pick 'bytes received/sec' | Measure-Object -Property CookedValue -Sum).Sum
        $bytesOut = (& $pick 'bytes sent/sec' | Measure-Object -Property CookedValue -Sum).Sum
        return [pscustomobject]@{
            Timestamp        = (Get-Date).ToString('o')
            Source           = 'Get-Counter'
            TotalCpuPercent  = [math]::Round((& $pick '% processor time' | Where-Object { $_.Path -like '*processor(_total)*' } | Select-Object -First 1 -ExpandProperty CookedValue), 2)
            LiveKitCpuPercent = [math]::Round((& $pick '% processor time' | Where-Object { $_.Path -like '*process(livekit-server)*' } | Select-Object -First 1 -ExpandProperty CookedValue), 2)
            LiveKitWorkingSetMB = [math]::Round(((& $pick 'working set - private' | Select-Object -First 1 -ExpandProperty CookedValue) / 1MB), 1)
            NetworkInMbps    = [math]::Round(($bytesIn * 8 / 1MB), 2)
            NetworkOutMbps   = [math]::Round(($bytesOut * 8 / 1MB), 2)
        }
    }
    catch {
        Write-Verbose "Get-Counter failed ($($_.Exception.Message)); using the CIM fallback."
    }

    try {
        $cimArgs = @{ ErrorAction = 'Stop' }
        if ($ComputerName -and $ComputerName -ne $env:COMPUTERNAME) { $cimArgs['ComputerName'] = $ComputerName }
        $cpu = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfOS_Processor `
            -Filter "Name='_Total'" @cimArgs
        $proc = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process `
            -Filter "Name='livekit-server'" @cimArgs | Select-Object -First 1
        $net = Get-CimInstance -ClassName Win32_PerfFormattedData_Tcpip_NetworkInterface @cimArgs
        $procCpu = 0
        $procWs = 0
        if ($proc) {
            $procCpu = [double]$proc.PercentProcessorTime
            $procWs = [math]::Round($proc.WorkingSetPrivate / 1MB, 1)
        }
        return [pscustomobject]@{
            Timestamp           = (Get-Date).ToString('o')
            Source              = 'CIM'
            TotalCpuPercent     = [double]$cpu.PercentProcessorTime
            LiveKitCpuPercent   = $procCpu
            LiveKitWorkingSetMB = $procWs
            NetworkInMbps       = [math]::Round((($net | Measure-Object -Property BytesReceivedPersec -Sum).Sum * 8 / 1MB), 2)
            NetworkOutMbps      = [math]::Round((($net | Measure-Object -Property BytesSentPersec -Sum).Sum * 8 / 1MB), 2)
        }
    }
    catch {
        Write-Warning "Could not sample counters on ${ComputerName}: $($_.Exception.Message)"
        return $null
    }
}

# --- preconditions --------------------------------------------------------------------

if (-not $Url) { throw 'Pass -Url or set $env:LIVEKIT_URL, e.g. ws://media-01.talky.corp:7880.' }
if (-not $ApiKey) { throw 'Pass -ApiKey or set $env:LIVEKIT_API_KEY.' }
if (-not $ApiSecret) { throw 'Pass -ApiSecret or set $env:LIVEKIT_API_SECRET.' }

$span = ConvertFrom-GoDuration -Value $Duration

$lk = if ($SkipDownload) {
    $onPath = Get-Command lk -ErrorAction SilentlyContinue
    if (-not $onPath) { throw '-SkipDownload was given but lk is not on PATH.' }
    $onPath.Source
}
else {
    Install-LiveKitCli -Version $CliVersion
}

if (-not (Test-Path -LiteralPath $OutputDir)) {
    if ($PSCmdlet.ShouldProcess($OutputDir, 'Create output directory')) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csvPath = Join-Path $OutputDir "livekit-load-$stamp.csv"
$logPath = Join-Path $OutputDir "livekit-load-$stamp.log"

$lkArgs = @(
    'load-test',
    '--url', $Url,
    '--api-key', $ApiKey,
    '--api-secret', $ApiSecret,
    '--room', $Room,
    '--video-publishers', $VideoPublishers,
    '--subscribers', $Subscribers,
    '--duration', $Duration
)

# The secret is never echoed.
Write-Host "Load test    $Room on $Url"
Write-Host "  publishers $VideoPublishers video, subscribers $Subscribers, duration $Duration"
Write-Host "  sampling   $NodeComputerName every $SampleIntervalSeconds s"
Write-Host "  csv        $csvPath"
Write-Host "  log        $logPath"
Write-Host ''

if (-not $PSCmdlet.ShouldProcess($Url, "Run lk load-test for $Duration and sample counters")) {
    Write-Host 'Dry run: nothing was started.'
    return
}

$samples = New-Object System.Collections.Generic.List[psobject]
$process = Start-Process -FilePath $lk -ArgumentList $lkArgs -PassThru -NoNewWindow `
    -RedirectStandardOutput $logPath -RedirectStandardError "$logPath.err"

try {
    $deadline = (Get-Date).Add($span).AddMinutes(2)   # lk stops on its own; this is a guard
    while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
        $sample = Get-NodeSample -ComputerName $NodeComputerName
        if ($sample) {
            $samples.Add($sample)
            $sample | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Append
        }
        Start-Sleep -Seconds $SampleIntervalSeconds
    }
    if (-not $process.HasExited) {
        Write-Warning "lk did not exit within $Duration + 2 min; stopping it."
        Stop-Process -Id $process.Id -Force
    }
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
}

# --- summary --------------------------------------------------------------------------

Write-Host ''
Write-Host '--- lk output (tail) ---'
Get-Content -LiteralPath $logPath -Tail 30 -ErrorAction SilentlyContinue
if ((Test-Path -LiteralPath "$logPath.err") -and (Get-Item -LiteralPath "$logPath.err").Length -gt 0) {
    Write-Host '--- lk stderr (tail) ---'
    Get-Content -LiteralPath "$logPath.err" -Tail 20
}

if ($samples.Count -eq 0) {
    Write-Warning 'No counter samples were collected; check -NodeComputerName and remote registry/WMI access.'
    return
}

$cpuStats = $samples | Measure-Object -Property TotalCpuPercent -Average -Maximum
$lkCpuStats = $samples | Measure-Object -Property LiveKitCpuPercent -Average -Maximum
$inStats = $samples | Measure-Object -Property NetworkInMbps -Maximum
$outStats = $samples | Measure-Object -Property NetworkOutMbps -Maximum

Write-Host ''
Write-Host '--- host counters ---'
Write-Host ("  samples          {0} every {1} s" -f $samples.Count, $SampleIntervalSeconds)
Write-Host ("  total CPU        avg {0:N1} %, peak {1:N1} %" -f $cpuStats.Average, $cpuStats.Maximum)
Write-Host ("  livekit-server   avg {0:N1} %, peak {1:N1} % of one core-equivalent" -f $lkCpuStats.Average, $lkCpuStats.Maximum)
Write-Host ("  network peak     {0:N1} Mbps in, {1:N1} Mbps out" -f $inStats.Maximum, $outStats.Maximum)
Write-Host ''
if ($cpuStats.Maximum -lt $CpuPassThreshold) {
    Write-Host ("PASS  peak host CPU {0:N1} % is below the {1} % spike threshold." -f $cpuStats.Maximum, $CpuPassThreshold)
}
else {
    Write-Host ("FAIL  peak host CPU {0:N1} % reached the {1} % spike threshold." -f $cpuStats.Maximum, $CpuPassThreshold)
}
Write-Host 'Packet loss and join success come from the lk output above; record both in docs\livekit-windows-spike.md.'
