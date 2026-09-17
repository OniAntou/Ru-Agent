<#
.SYNOPSIS
    Measure cold-start time and peak memory of a published Ru executable.

.DESCRIPTION
    Launches the given executable, waits until the process is up, samples its
    memory until it settles, then reports time-to-ready and peak working set.

    This exists so a build or publish change can be judged by a number instead of
    by an impression. It is a development aid, not part of the product.

    The default readiness signal is the process reaching a stable state: alive and
    with a non-empty main window handle. For a console host, -ReadyUrl polls an
    endpoint instead, which is the honest signal for "serving requests".

.PARAMETER Path
    Full path to the executable to launch.

.PARAMETER ArgumentList
    Arguments passed to the executable.

.PARAMETER ReadyUrl
    Optional. When given, readiness is the first HTTP response from this URL,
    instead of the window-handle heuristic.

.PARAMETER SettleMs
    How long to keep sampling after readiness, to catch the post-start allocation
    peak. Default 3000.

.PARAMETER StartupTimeoutSeconds
    Give up and report a failure after this long. Default 60.

.PARAMETER Runs
    Repeat the whole measurement this many times and report every run. Default 1.

.PARAMETER WaitForExit
    Keep sampling until the process exits, instead of stopping after SettleMs. Use
    this for a process that is expected to run to completion, such as a CLI command:
    the reported alive_ms is then its total lifetime and the peak covers the run.

.PARAMETER WarmupRuns
    Runs to perform and discard before recording, to remove first-run disk-cache
    effects from the numbers. Default 0.

.EXAMPLE
    .\scripts\measure_startup.ps1 -Path gui-winui3\bin\Release\...\RuApp.exe

.NOTES
    The launched process is always terminated before the script returns, including
    on failure.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string[]]$ArgumentList = @(),

    [string]$ReadyUrl,

    [ValidateRange(0, 60000)]
    [int]$SettleMs = 3000,

    [ValidateRange(1, 600)]
    [int]$StartupTimeoutSeconds = 60,

    [ValidateRange(1, 50)]
    [int]$Runs = 1,

    [ValidateRange(0, 20)]
    [int]$WarmupRuns = 0,

    [switch]$WaitForExit,

    [string]$Output,

    [switch]$NoWrite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    throw "executable not found: $Path"
}

$resolved = (Resolve-Path -LiteralPath $Path).Path

function Invoke-OneRun {
    param([bool]$Record)

    $process = $null
    $peakWorkingSet = 0.0
    $peakPrivate = 0.0
    $readyMs = $null
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $resolved
        $startInfo.UseShellExecute = $false
        if ($ArgumentList.Count -gt 0) {
            # Arguments rather than ArgumentList: the latter does not exist on the
            # .NET Framework that Windows PowerShell 5.1 hosts this script in.
            $startInfo.Arguments = ($ArgumentList | ForEach-Object {
                if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
            }) -join ' '
        }

        $process = [System.Diagnostics.Process]::Start($startInfo)
        $deadline = [datetime]::UtcNow.AddSeconds($StartupTimeoutSeconds)

        while ([datetime]::UtcNow -lt $deadline) {
            if ($process.HasExited) { break }

            if ($null -eq $readyMs) {
                $isReady = if ($ReadyUrl) {
                    try {
                        $response = Invoke-WebRequest -Uri $ReadyUrl -TimeoutSec 2 -SkipHttpErrorCheck -ErrorAction Stop
                        $null -ne $response
                    }
                    catch { $false }
                }
                else {
                    # A WinUI window exists once the shell has laid out; before that
                    # the handle is zero.
                    try { $process.Refresh(); $process.MainWindowHandle -ne [IntPtr]::Zero }
                    catch { $false }
                }

                if ($isReady) { $readyMs = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1) }
            }

            try {
                $process.Refresh()
                $workingSet = $process.WorkingSet64 / 1MB
                $privateBytes = $process.PrivateMemorySize64 / 1MB
                if ($workingSet -gt $peakWorkingSet) { $peakWorkingSet = $workingSet }
                if ($privateBytes -gt $peakPrivate) { $peakPrivate = $privateBytes }
            }
            catch { }

            Start-Sleep -Milliseconds 25
        }

        # Keep sampling briefly: the first layout and first data load land after the
        # window exists, and that is usually the real peak.
        if ($null -ne $readyMs -and -not $WaitForExit) {
            $settleUntil = [datetime]::UtcNow.AddMilliseconds($SettleMs)
            while ([datetime]::UtcNow -lt $settleUntil -and -not $process.HasExited) {
                try {
                    $process.Refresh()
                    $workingSet = $process.WorkingSet64 / 1MB
                    $privateBytes = $process.PrivateMemorySize64 / 1MB
                    if ($workingSet -gt $peakWorkingSet) { $peakWorkingSet = $workingSet }
                    if ($privateBytes -gt $peakPrivate) { $peakPrivate = $privateBytes }
                }
                catch { }
                Start-Sleep -Milliseconds 25
            }
        }

        # For a process that is expected to run to completion, keep sampling until it
        # exits so the reported peak covers the whole run rather than a window of it.
        if ($WaitForExit -and -not $process.HasExited) {
            $exitUntil = [datetime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
            while ([datetime]::UtcNow -lt $exitUntil -and -not $process.HasExited) {
                try {
                    $process.Refresh()
                    $workingSet = $process.WorkingSet64 / 1MB
                    $privateBytes = $process.PrivateMemorySize64 / 1MB
                    if ($workingSet -gt $peakWorkingSet) { $peakWorkingSet = $workingSet }
                    if ($privateBytes -gt $peakPrivate) { $peakPrivate = $privateBytes }
                }
                catch { }
                Start-Sleep -Milliseconds 10
            }
        }

        $stopwatch.Stop()

        if (-not $Record) { return $null }

        return [ordered]@{
            ready_ms                = $readyMs
            alive_ms                = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
            exited_early            = $process.HasExited
            peak_working_set_mb     = [math]::Round($peakWorkingSet, 1)
            peak_private_bytes_mb   = [math]::Round($peakPrivate, 1)
        }
    }
    finally {
        if ($null -ne $process) {
            try {
                if (-not $process.HasExited) { $process.Kill($true) }
                $process.WaitForExit(5000) | Out-Null
            }
            catch { }
            $process.Dispose()
        }
    }
}

for ($i = 0; $i -lt $WarmupRuns; $i++) {
    Write-Host "Warmup run $($i + 1)/$WarmupRuns (discarded)."
    Invoke-OneRun -Record $false | Out-Null
}

$measurements = [System.Collections.Generic.List[object]]::new()
for ($i = 0; $i -lt $Runs; $i++) {
    Write-Host "Measured run $($i + 1)/$Runs."
    $measurements.Add((Invoke-OneRun -Record $true))
}

$readyValues = @($measurements | Where-Object { $null -ne $_.ready_ms } | ForEach-Object { [double]$_.ready_ms })
$peakValues = @($measurements | ForEach-Object { [double]$_.peak_working_set_mb })
$privateValues = @($measurements | ForEach-Object { [double]$_.peak_private_bytes_mb })

function Get-Summary {
    param([double[]]$Values)

    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $middle = [int][math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2 -eq 0) {
        ($sorted[$middle - 1] + $sorted[$middle]) / 2
    } else { $sorted[$middle] }

    return [ordered]@{
        samples = $Values.Count
        min     = [math]::Round($sorted[0], 1)
        median  = [math]::Round($median, 1)
        max     = [math]::Round($sorted[-1], 1)
    }
}

$report = [ordered]@{
    schema_version    = 1
    measured_at       = (Get-Date).ToUniversalTime().ToString('o')
    executable        = $resolved
    executable_bytes  = (Get-Item -LiteralPath $resolved).Length
    readiness_signal  = if ($ReadyUrl) { "http:$ReadyUrl" } else { 'main_window_handle' }
    warmup_runs       = $WarmupRuns
    runs              = $measurements
    summary           = [ordered]@{
        ready_ms              = Get-Summary -Values $readyValues
        peak_working_set_mb   = Get-Summary -Values $peakValues
        peak_private_bytes_mb = Get-Summary -Values $privateValues
    }
}

$json = $report | ConvertTo-Json -Depth 6

if ($Output) {
    $directory = Split-Path -Parent $Output
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $json | Set-Content -LiteralPath $Output -Encoding UTF8
    Write-Host "Report written to $Output"
}

if (-not $NoWrite) { Write-Host $json }
