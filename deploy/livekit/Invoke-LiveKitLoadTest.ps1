<#
.SYNOPSIS
    Runs the LiveKit load test against a media node, records host CPU and network counters
    to CSV and fails unless every participant connected and received its tracks.

.DESCRIPTION
    This is the measurement half of Spike A (docs\livekit-windows-spike.md). It:

      1. downloads the official livekit-cli release (`lk`) and verifies its SHA-256
         against the release checksums.txt;
      2. creates the room with `lk room create` (our livekit.yaml sets
         room.auto_create: false, so a participant cannot create it by joining);
      3. starts `lk load-test` with the spike profile - by default 8 video publishers and
         100 subscribers in room "spike" for 10 minutes;
      4. samples the SFU host every -SampleIntervalSeconds while the test runs and writes
         one row per sample to a CSV: total CPU %, livekit-server process CPU % and
         working set, and network bytes in/out per second;
      5. reads lk's "Subscriber summaries" table and prints one PASS/FAIL line per check:
         every participant connected (no "could not connect", error count 0), every
         subscriber received every published track, packet loss below 1 % for every
         subscriber, and - only when the load actually ran - peak host CPU below 60 %;
      6. deletes the room again (best effort).

    The URL, key and secret are handed to lk through LIVEKIT_URL / LIVEKIT_API_KEY /
    LIVEKIT_API_SECRET in the environment of the child processes, never on a command line.
    The previous values of those variables are restored afterwards.

    Counters are read with Get-Counter using the English counter paths, falling back to
    the locale-independent Win32_PerfFormattedData_* CIM classes on a localised Windows.

    Exit code 0 when every check passed, 1 otherwise (lk itself exits 0 even when no
    participant could connect, so its exit code is not used).

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
    Subscribing participants. Default 100. At least 1: the pass criteria are read from the
    subscribers' side.

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

.PARAMETER SkipRoomCreate
    Do not create (or delete) the room; only for a node configured with
    room.auto_create: true. Against a Talky node every participant then fails with
    "not found", which Test-LiveKitInstall.ps1 uses as its negative check.

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
    [ValidateRange(1, 10000)][int] $VideoPublishers = 8,
    [ValidateRange(1, 100000)][int] $Subscribers = 100,
    [string] $Duration = '10m',
    [string] $OutputDir,
    [ValidateRange(1, 300)][int] $SampleIntervalSeconds = 5,
    [string] $CliVersion = '2.18.6',
    [switch] $SkipDownload,
    [switch] $SkipRoomCreate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The CSV is read by other tools and by people on other machines: force '.' as the decimal
# separator regardless of the operator's regional settings.
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture

$CliRepo = 'livekit/livekit-cli'
$UserAgent = 'talky-deploy'
$CpuPassThreshold = 60
$LossPassThresholdPercent = 1
$Utf8 = New-Object System.Text.UTF8Encoding($false)

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $OutputDir) {
    $OutputDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'artifacts\livekit-spike'
}

$script:Failed = 0
function Write-Check {
    param([Parameter(Mandatory)][bool] $Condition, [Parameter(Mandatory)][string] $Message)
    if ($Condition) { Write-Host "PASS  $Message" }
    else { Write-Host "FAIL  $Message"; $script:Failed++ }
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

# Runs a short lk command (room create/delete) and returns its exit code and output.
# The output is not printed: `lk room create` echoes the room including its TURN password.
function Invoke-LkCommand {
    param([Parameter(Mandatory)][string[]] $Arguments)
    $out = Join-Path $OutputDir ("lk-cmd-{0}.out" -f [guid]::NewGuid().ToString('n'))
    $err = "$out.err"
    try {
        $proc = Start-Process -FilePath $lk -ArgumentList $Arguments -PassThru -NoNewWindow -Wait `
            -RedirectStandardOutput $out -RedirectStandardError $err
        $text = ''
        foreach ($f in $out, $err) {
            if (Test-Path -LiteralPath $f) { $text += [IO.File]::ReadAllText($f, $Utf8) }
        }
        return [pscustomobject]@{ ExitCode = $proc.ExitCode; Output = $text }
    }
    finally {
        Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue
    }
}

# Reads lk load-test's "Subscriber summaries" table (box drawing, or '|' in plain output).
function Read-LkSummary {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)
    # U+2502 BOX DRAWINGS LIGHT VERTICAL; written as a code point so this file stays ASCII
    # (Windows PowerShell reads a BOM-less script in the ANSI code page).
    $bar = [char]0x2502
    $lines = $Text -split "`r?`n"
    $connectFailures = @($lines | Where-Object { $_ -match '^could not connect' })
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^Subscriber summaries') { $start = $i; break } }
    $rows = @()
    $header = $null
    if ($start -ge 0) {
        for ($i = $start + 1; $i -lt $lines.Count; $i++) {
            $line = $lines[$i].Trim()
            if ($line -eq '') { if ($header) { break } else { continue } }
            if ($line -notmatch "^[$bar|]") { continue }   # border lines
            $cells = @($line.Trim($bar, '|') -split "[$bar|]" | ForEach-Object { $_.Trim() })
            if (-not $header) { $header = $cells; continue }
            $row = @{}
            for ($c = 0; $c -lt [Math]::Min($header.Count, $cells.Count); $c++) { $row[$header[$c]] = $cells[$c] }
            $rows += [pscustomobject]$row
        }
    }
    $total = $null
    $subs = @()
    foreach ($row in $rows) {
        if (-not ($row.PSObject.Properties['Tester'] -and $row.PSObject.Properties['Tracks'])) { continue }
        $received = -1; $expected = -1
        if ($row.Tracks -match '^(\d+)/(\d+)$') { $received = [int]$Matches[1]; $expected = [int]$Matches[2] }
        $loss = $null
        if ($row.PSObject.Properties['Total Pkt. Loss'] -and $row.'Total Pkt. Loss' -match '\((?<p>[\d.]+)%\)') {
            $loss = [double]$Matches['p']
        }
        $errorText = if ($row.PSObject.Properties['Error']) { $row.Error } else { '' }
        $entry = [pscustomobject]@{ Tester = $row.Tester; Received = $received; Expected = $expected; LossPercent = $loss; Error = $errorText }
        if ($row.Tester -eq 'Total') { $total = $entry } elseif ($row.Tester -like 'Sub *') { $subs += $entry }
    }
    return [pscustomobject]@{ Found = ($null -ne $total); Total = $total; Subscribers = $subs; ConnectFailures = $connectFailures }
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

if (-not $PSCmdlet.ShouldProcess($Url, "Create room $Room, run lk load-test for $Duration and sample counters")) {
    Write-Host 'Dry run: nothing was started.'
    return
}

$savedEnv = @{}
foreach ($name in 'LIVEKIT_URL', 'LIVEKIT_API_KEY', 'LIVEKIT_API_SECRET') {
    $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$samples = New-Object System.Collections.Generic.List[psobject]
$loadRan = $false
$roomCreated = $false
try {
    # lk reads these; child processes inherit them, so nothing secret is on a command line.
    $env:LIVEKIT_URL = $Url
    $env:LIVEKIT_API_KEY = $ApiKey
    $env:LIVEKIT_API_SECRET = $ApiSecret

    if ($SkipRoomCreate) {
        Write-Host "room     $Room not created (-SkipRoomCreate)"
    }
    else {
        $created = Invoke-LkCommand -Arguments @('room', 'create', $Room)
        $roomCreated = $created.ExitCode -eq 0
        $reason = (@($created.Output -split "`r?`n" | Where-Object { $_ -match 'error' }) -join ' ').Trim()
        Write-Check $roomCreated "lk room create $Room (exit $($created.ExitCode)$(if ($reason) { ": $reason" }))"
    }

    if ($SkipRoomCreate -or $roomCreated) {
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
            $loadRan = $process.HasExited
            Write-Check $loadRan "lk load-test finished within $Duration + 2 min"
        }
        finally {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        }
    }

    if ($roomCreated) {
        $deleted = Invoke-LkCommand -Arguments @('room', 'delete', $Room)
        if ($deleted.ExitCode -eq 0) { Write-Host "room     $Room deleted" }
        else { Write-Warning "lk room delete $Room exited $($deleted.ExitCode); the node closes it after room.empty_timeout." }
    }
}
finally {
    foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name], 'Process') }
}

# --- summary --------------------------------------------------------------------------

$lkText = ''
if (Test-Path -LiteralPath $logPath) {
    # lk writes UTF-8 (its tables use box-drawing characters); Windows PowerShell would
    # otherwise read the file in the ANSI code page.
    $lkText = [IO.File]::ReadAllText($logPath, $Utf8)
    Write-Host ''
    Write-Host 'lk output (tail):'
    $lkLines = @($lkText.TrimEnd() -split "`r?`n")
    $lkLines | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" }
}
if ((Test-Path -LiteralPath "$logPath.err") -and (Get-Item -LiteralPath "$logPath.err").Length -gt 0) {
    Write-Host 'lk stderr (tail):'
    @([IO.File]::ReadAllText("$logPath.err", $Utf8).TrimEnd() -split "`r?`n") | Select-Object -Last 20 | ForEach-Object { Write-Host "  $_" }
}
Write-Host ''

$loadOk = $false
if ($loadRan) {
    $summary = Read-LkSummary -Text $lkText
    $expectedTracks = $Subscribers * $VideoPublishers
    Write-Check $summary.Found 'lk printed its subscriber summary table'
    if ($summary.Found) {
        $errorCount = 0
        if (-not [int]::TryParse($summary.Total.Error, [ref]$errorCount)) { $errorCount = if ($summary.Total.Error -eq '-') { 0 } else { -1 } }
        $connected = $summary.ConnectFailures.Count -eq 0 -and $errorCount -eq 0
        Write-Check $connected ("every participant connected ({0} 'could not connect', subscriber errors {1})" -f $summary.ConnectFailures.Count, $summary.Total.Error)
        foreach ($line in ($summary.ConnectFailures | Select-Object -First 3)) { Write-Host "      $line" }

        $short = @($summary.Subscribers | Where-Object { $_.Received -ne $VideoPublishers -or $_.Expected -ne $VideoPublishers })
        $tracksOk = $summary.Subscribers.Count -eq $Subscribers -and $short.Count -eq 0 -and
            $summary.Total.Received -eq $expectedTracks -and $summary.Total.Expected -eq $expectedTracks
        Write-Check $tracksOk ("every subscriber received its tracks: {0}/{1} total, expected {2} ({3} of {4} subscriber rows short)" -f
            $summary.Total.Received, $summary.Total.Expected, $expectedTracks, $short.Count, $summary.Subscribers.Count)

        $losses = @($summary.Subscribers | Where-Object { $null -ne $_.LossPercent } | ForEach-Object { $_.LossPercent })
        $worst = if ($losses.Count -gt 0) { ($losses | Measure-Object -Maximum).Maximum } else { $null }
        $lossOk = $losses.Count -eq $summary.Subscribers.Count -and $losses.Count -gt 0 -and $worst -lt $LossPassThresholdPercent
        Write-Check $lossOk ("packet loss below {0} % for every subscriber (worst {1})" -f $LossPassThresholdPercent,
            $(if ($null -ne $worst) { "$worst %" } else { 'not reported' }))
        $loadOk = $connected -and $tracksOk
    }
}

if ($samples.Count -gt 0) {
    $cpuStats = $samples | Measure-Object -Property TotalCpuPercent -Average -Maximum
    $lkCpuStats = $samples | Measure-Object -Property LiveKitCpuPercent -Average -Maximum
    $inStats = $samples | Measure-Object -Property NetworkInMbps -Maximum
    $outStats = $samples | Measure-Object -Property NetworkOutMbps -Maximum
    Write-Host ''
    Write-Host 'host counters:'
    Write-Host ("  samples          {0} every {1} s" -f $samples.Count, $SampleIntervalSeconds)
    Write-Host ("  total CPU        avg {0:N1} %, peak {1:N1} %" -f $cpuStats.Average, $cpuStats.Maximum)
    Write-Host ("  livekit-server   avg {0:N1} %, peak {1:N1} % of one core-equivalent" -f $lkCpuStats.Average, $lkCpuStats.Maximum)
    Write-Host ("  network peak     {0:N1} Mbps in, {1:N1} Mbps out" -f $inStats.Maximum, $outStats.Maximum)
    Write-Host ''
    if ($loadOk) {
        Write-Check ($cpuStats.Maximum -lt $CpuPassThreshold) ("peak host CPU {0:N1} % is below the {1} % spike threshold" -f $cpuStats.Maximum, $CpuPassThreshold)
    }
    else {
        Write-Host 'CPU      not judged: the load did not run as configured, so the counters say nothing about the SFU.'
    }
}
elseif ($loadRan) {
    Write-Check $false 'counter samples collected (check -NodeComputerName and remote registry/WMI access)'
}

Write-Host ''
if ($script:Failed -gt 0) {
    Write-Host "Load test FAILED ($($script:Failed) failed check(s)). lk log: $logPath"
    exit 1
}
Write-Host 'Load test PASSED. Record the numbers in docs\livekit-windows-spike.md.'
exit 0
