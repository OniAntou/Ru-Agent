<#
.SYNOPSIS
    Measure the machine-resource cost of a running Ru process tree.

.DESCRIPTION
    Samples CPU, memory, and (when a driver exposes it) GPU utilization for one
    process and every descendant, then reports p50/p95/p99 per metric.

    Resource cost is the number that matters for a desktop agent: a coding agent
    that streams fast but holds a gigabyte of resident memory and a spinning CPU is
    slower for the user than one that idles near zero. This script exists so that
    cost is measured rather than assumed.

    The target is selected by name or pid. Sampling runs for a bounded duration and
    the target is never terminated.

    Process enumeration goes through the ToolHelp snapshot API. A CIM query for the
    same information costs hundreds of milliseconds per call, which drifts the
    sampling interval and silently produces fewer samples than the duration asks for.

.PARAMETER Name
    Process name (without .exe) whose tree is measured, e.g. RuBackend.

.PARAMETER Id
    Root process id to measure instead of a name.

.PARAMETER DurationSeconds
    How long to sample. Default 30.

.PARAMETER IntervalMs
    Delay between samples. Default 1000.

.PARAMETER Output
    Path for the JSON report. Required unless -NoWrite.

.PARAMETER MaxCpuPercent
    Gate: exit 2 when the tree's p95 CPU percentage exceeds this.

.PARAMETER MaxWorkingSetMb
    Gate: exit 2 when the tree's p95 working set exceeds this many MiB.

.PARAMETER WarmupSeconds
    Sampling is skipped for this long after start, so startup allocations do not
    dominate a steady-state budget. Default 0.

.PARAMETER GpuEverySamples
    Read GPU counters once per this many CPU/memory samples, reusing the last
    reading in between. A single GPU Engine read enumerates every engine instance on
    the machine and was measured at 1.6 seconds on a 16-core box, so at the default
    of 10 the CPU/memory cadence stays within about 20% of the requested interval
    while GPU still gets a reading every 10 seconds. Raise it for a longer run, or
    set it to 1 when GPU resolution matters more than CPU sample count.

.EXAMPLE
    .\scripts\measure_resources.ps1 -Name RuBackend -DurationSeconds 30 `
        -Output artifacts\resources\backend.json -MaxWorkingSetMb 250

.NOTES
    GPU utilisation is reported with gpu_supported=false when the Windows GPU
    Engine counters are unavailable. That is a statement about the machine, not a
    passing measurement.
#>
[CmdletBinding(DefaultParameterSetName = 'ByName')]
param(
    [Parameter(ParameterSetName = 'ByName', Mandatory = $true)]
    [string]$Name,

    [Parameter(ParameterSetName = 'ById', Mandatory = $true)]
    [int]$Id,

    [ValidateRange(1, 86400)]
    [int]$DurationSeconds = 30,

    [ValidateRange(50, 60000)]
    [int]$IntervalMs = 1000,

    [string]$Output,

    [switch]$NoWrite,

    [double]$MaxCpuPercent = -1,

    [double]$MaxWorkingSetMb = -1,

    [ValidateRange(0, 3600)]
    [int]$WarmupSeconds = 0,

    [ValidateRange(1, 1000)]
    [int]$GpuEverySamples = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Process enumeration through ToolHelp. One native call returns the whole table,
# where a CIM query costs hundreds of milliseconds and drifts the sample cadence.
if (-not ('Ru.ToolHelp' -as [type])) {
    Add-Type -Namespace Ru -Name ToolHelp -MemberDefinition @'
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct PROCESSENTRY32W {
    public uint dwSize;
    public uint cntUsage;
    public uint th32ProcessID;
    public System.IntPtr th32DefaultHeapID;
    public uint th32ModuleID;
    public uint cntThreads;
    public uint th32ParentProcessID;
    public int pcPriClassBase;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szExeFile;
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern System.IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool Process32FirstW(System.IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool Process32NextW(System.IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool CloseHandle(System.IntPtr hObject);

const uint TH32CS_SNAPPROCESS = 0x00000002;

/// Returns pid -> parent pid for every live process, or an empty map on failure.
public static System.Collections.Generic.Dictionary<int, int> SnapshotParents() {
    var map = new System.Collections.Generic.Dictionary<int, int>();
    System.IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == new System.IntPtr(-1)) { return map; }
    try {
        var entry = new PROCESSENTRY32W();
        entry.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PROCESSENTRY32W));
        if (!Process32FirstW(snapshot, ref entry)) { return map; }
        do {
            map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
        } while (Process32NextW(snapshot, ref entry));
    } finally {
        CloseHandle(snapshot);
    }
    return map;
}
'@
}

function Get-Percentile {
    param(
        [Parameter(Mandatory = $true)][double[]]$Values,
        [Parameter(Mandatory = $true)][double]$Percentile
    )

    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 1) { return [math]::Round($sorted[0], 3) }

    $position = ($sorted.Count - 1) * $Percentile / 100.0
    $lower = [int][math]::Floor($position)
    $upper = [math]::Min($lower + 1, $sorted.Count - 1)
    $fraction = $position - $lower
    return [math]::Round($sorted[$lower] + ($sorted[$upper] - $sorted[$lower]) * $fraction, 3)
}

function Get-Summary {
    param([Parameter(Mandatory = $true)][double[]]$Values)

    if ($Values.Count -eq 0) {
        return [ordered]@{ sample_count = 0; p50 = $null; p95 = $null; p99 = $null; max = $null }
    }

    return [ordered]@{
        sample_count = $Values.Count
        p50          = Get-Percentile -Values $Values -Percentile 50
        p95          = Get-Percentile -Values $Values -Percentile 95
        p99          = Get-Percentile -Values $Values -Percentile 99
        max          = [math]::Round(($Values | Measure-Object -Maximum).Maximum, 3)
    }
}

function Resolve-RootProcess {
    param([string]$ProcessName, [int]$ProcessId)

    if ($ProcessId -gt 0) {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if (-not $process) { throw "no process with id $ProcessId" }
        return $process
    }

    # The oldest match is the parent of any sibling instances, so its tree covers them.
    $candidates = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Sort-Object -Property StartTime)
    if ($candidates.Count -eq 0) {
        throw "no running process named '$ProcessName'. Start the runtime first, or pass -Id."
    }

    return $candidates[0]
}

function Get-DescendantIds {
    param([int]$RootId, [hashtable]$Parents)

    $ids = [System.Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($RootId)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($entry in @($Parents.GetEnumerator())) {
            if ($ids.Contains([int]$entry.Value) -and -not $ids.Contains([int]$entry.Key)) {
                [void]$ids.Add([int]$entry.Key)
                $changed = $true
            }
        }
    }

    return @($ids)
}

function Initialize-GpuCounters {
    $result = [ordered]@{ source = $null; supported = $false; reason = $null }

    try {
        $null = Get-Counter -ListSet 'GPU Engine' -ErrorAction Stop
        $result.source = 'windows_gpu_engine'
        $result.supported = $true
        return $result
    }
    catch {
        $result.reason = "GPU Engine counters unavailable: $($_.Exception.Message)"
    }

    return $result
}

function Get-GpuSample {
    param([int[]]$ProcessIds, [string]$Source)

    if ($Source -ne 'windows_gpu_engine' -or $ProcessIds.Count -eq 0) { return $null }

    try {
        $sample = Get-Counter -Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop
    }
    catch {
        return $null
    }

    $total = 0.0
    foreach ($entry in $sample.CounterSamples) {
        if ($entry.CookedValue -le 0) { continue }
        # Instance names look like pid_1234_luid_0x..._phys_0_eng_0_engtype_3D
        $match = [regex]::Match($entry.InstanceName, '^pid_(\d+)_')
        if (-not $match.Success) { continue }
        if ($ProcessIds -contains [int]$match.Groups[1].Value) {
            $total += $entry.CookedValue
        }
    }

    return [math]::Round($total, 3)
}

function Read-TreeCounters {
    param([int[]]$ProcessIds)

    $workingSet = 0.0
    $privateBytes = 0.0
    $cpuSeconds = @{}
    $live = [System.Collections.Generic.List[int]]::new()

    foreach ($processId in $ProcessIds) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if (-not $process) { continue }

        # HasExited throws on a process reaped between enumeration and read.
        try { if ($process.HasExited) { continue } } catch { continue }

        [void]$live.Add($processId)
        $cpuSeconds[$processId] = $process.TotalProcessorTime.TotalSeconds
        $workingSet += $process.WorkingSet64 / 1MB
        $privateBytes += $process.PrivateMemorySize64 / 1MB
    }

    return [ordered]@{
        ids         = @($live)
        cpu_seconds = $cpuSeconds
        working_set = $workingSet
        private_mb  = $privateBytes
    }
}

$root = Resolve-RootProcess -ProcessName $Name -ProcessId $Id
$rootId = $root.Id
$logicalProcessors = [Environment]::ProcessorCount
if ($logicalProcessors -lt 1) { $logicalProcessors = 1 }

$gpu = Initialize-GpuCounters

Write-Host "Measuring '$($root.ProcessName)' (pid $rootId) for ${DurationSeconds}s at ${IntervalMs}ms."
Write-Host "Logical processors: $logicalProcessors. GPU source: $(if ($gpu.supported) { $gpu.source } else { 'none' })."

if ($WarmupSeconds -gt 0) {
    Write-Host "Warming up for ${WarmupSeconds}s before sampling."
    Start-Sleep -Seconds $WarmupSeconds
}

$cpuSamples = [System.Collections.Generic.List[double]]::new()
$workingSetSamples = [System.Collections.Generic.List[double]]::new()
$privateBytesSamples = [System.Collections.Generic.List[double]]::new()
$gpuSamples = [System.Collections.Generic.List[double]]::new()
$processCountSamples = [System.Collections.Generic.List[double]]::new()

# Prime the CPU counters, then discard this reading. Without it the first recorded
# sample has no predecessor to subtract and would report 0% CPU.
$parents = [Ru.ToolHelp]::SnapshotParents()
$primed = Read-TreeCounters -ProcessIds (Get-DescendantIds -RootId $rootId -Parents $parents)
$previousCpu = $primed.cpu_seconds
$previousAt = [datetime]::UtcNow
$deadline = $previousAt.AddSeconds($DurationSeconds)
$sampleIndex = 0
$lastGpu = $null
$gpuReads = 0

while ([datetime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds $IntervalMs
    $now = [datetime]::UtcNow
    $elapsedSeconds = ($now - $previousAt).TotalSeconds
    if ($elapsedSeconds -le 0) { continue }

    $parents = [Ru.ToolHelp]::SnapshotParents()
    $counters = Read-TreeCounters -ProcessIds (Get-DescendantIds -RootId $rootId -Parents $parents)

    $totalCpuSeconds = 0.0
    foreach ($entry in @($counters.cpu_seconds.GetEnumerator())) {
        if ($previousCpu.ContainsKey($entry.Key)) {
            $totalCpuSeconds += [math]::Max(0, $entry.Value - $previousCpu[$entry.Key])
        }
    }

    $previousCpu = $counters.cpu_seconds
    $previousAt = $now

    # Normalised across every logical processor: 100% means the tree saturated the
    # whole machine, which is the ceiling a user can observe.
    $cpuPercent = ($totalCpuSeconds / $elapsedSeconds) * 100.0 / $logicalProcessors
    $cpuSamples.Add([math]::Round($cpuPercent, 3))
    $workingSetSamples.Add([math]::Round($counters.working_set, 3))
    $privateBytesSamples.Add([math]::Round($counters.private_mb, 3))
    $processCountSamples.Add($counters.ids.Count)

    # GPU counters are machine-wide and expensive to enumerate, so they are read on
    # a slower cadence and held between reads. GPU Engine reports a utilisation
    # percentage, which is already an average over its own interval.
    if ($gpu.source -eq 'windows_gpu_engine') {
        if (($sampleIndex % $GpuEverySamples) -eq 0) {
            $lastGpu = Get-GpuSample -ProcessIds @($counters.ids) -Source $gpu.source
            $gpuReads++
        }

        if ($null -ne $lastGpu) { $gpuSamples.Add($lastGpu) }
    }

    $sampleIndex++
}

$exitCode = 0
$gates = [ordered]@{}
$cpuSummary = Get-Summary -Values @($cpuSamples)
$workingSetSummary = Get-Summary -Values @($workingSetSamples)

if ($MaxCpuPercent -ge 0) {
    $passed = $null -ne $cpuSummary.p95 -and $cpuSummary.p95 -le $MaxCpuPercent
    $gates['max_cpu_percent'] = [ordered]@{ limit = $MaxCpuPercent; observed_p95 = $cpuSummary.p95; passed = $passed }
    if (-not $passed) { $exitCode = 2 }
}

if ($MaxWorkingSetMb -ge 0) {
    $passed = $null -ne $workingSetSummary.p95 -and $workingSetSummary.p95 -le $MaxWorkingSetMb
    $gates['max_working_set_mb'] = [ordered]@{ limit = $MaxWorkingSetMb; observed_p95 = $workingSetSummary.p95; passed = $passed }
    if (-not $passed) { $exitCode = 2 }
}

$report = [ordered]@{
    schema_version   = 1
    measured_at      = (Get-Date).ToUniversalTime().ToString('o')
    target           = [ordered]@{
        name       = $root.ProcessName
        root_pid   = $rootId
        started_at = $root.StartTime.ToUniversalTime().ToString('o')
    }
    machine          = [ordered]@{
        logical_processors = $logicalProcessors
        os                 = [System.Environment]::OSVersion.VersionString
    }
    sampling         = [ordered]@{
        duration_seconds  = $DurationSeconds
        interval_ms       = $IntervalMs
        warmup_seconds    = $WarmupSeconds
        gpu_every_samples = $GpuEverySamples
    }
    cpu_percent      = $cpuSummary
    working_set_mb   = $workingSetSummary
    private_bytes_mb = Get-Summary -Values @($privateBytesSamples)
    process_count    = Get-Summary -Values @($processCountSamples)
    gpu = [ordered]@{
        supported       = $gpu.supported
        source          = $gpu.source
        reason          = $gpu.reason
        reads           = $gpuReads
        utilization_pct = Get-Summary -Values @($gpuSamples)
    }
    gates            = $gates
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
if ($exitCode -ne 0) { Write-Host "Resource gate failed." }

exit $exitCode
