[CmdletBinding()]
param(
    [ValidateRange(1, 5)][int]$Samples = 3,
    [ValidateSet(16, 32, 60)][int[]]$Steps = @(16, 32, 60),
    [ValidateRange(1, 60000)][double]$MaxWarmStepP95Ms = 2000,
    [switch]$CompareCache,
    [string]$OutputDirectory = ""
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspace ('artifacts/agent-performance/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$matrixMutex = $null
$ownsMutex = $false
try {
    try {
        $matrixMutex = [System.Threading.Mutex]::new($false, 'Global\ru-test-matrix-run')
        $ownsMutex = $matrixMutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] { $ownsMutex = $true }
    catch [System.UnauthorizedAccessException] {
        Write-Error 'Cannot open the machine-wide test/benchmark lock.' -ErrorAction Continue
        exit 75
    }
    if (-not $ownsMutex) { Write-Error 'Another test/benchmark matrix owns the machine-wide lock.' -ErrorAction Continue; exit 75 }
    $project = Join-Path $workspace 'tools/agent-performance/AgentPerformance.csproj'
    & dotnet build $project --configuration Release --nologo *> (Join-Path $outputPath 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Benchmark build failed; see $outputPath/build.log" }
    $binary = Join-Path $workspace 'tools/agent-performance/bin/Release/net10.0-windows/AgentPerformance.dll'
    function Percentile([double[]]$Values, [double]$Fraction) {
        $sorted = @($Values | Sort-Object)
        return $sorted[[Math]::Max(0, [int][Math]::Ceiling($sorted.Count * $Fraction) - 1)]
    }
    $results = @()
    $modes = if ($CompareCache) { @('cached', 'uncached') } else { @('cached') }
    foreach ($stepCount in $Steps) {
        for ($sample = 1; $sample -le $Samples; $sample++) {
            # Alternate order for the comparison; do not always warm the machine for one mode.
            $sampleModes = @($modes)
            if ($sample % 2 -eq 0) { [array]::Reverse($sampleModes) }
            foreach ($mode in $sampleModes) {
                $sampleLog = Join-Path $outputPath "$stepCount-$mode-$sample.jsonl"
                $arguments = @($binary, "--steps=$stepCount", "--restart-at=$([int]($stepCount / 2))")
                if ($mode -eq 'uncached') { $arguments += '--no-cache' }
                # JSONL is stdout-only; provider diagnostics belong to stderr.
                $diagnosticLog = Join-Path $outputPath "$stepCount-$mode-$sample.stderr.log"
                & dotnet @arguments > $sampleLog 2> $diagnosticLog
                if ($LASTEXITCODE -ne 0) { throw "Benchmark process failed; see $sampleLog" }
                $rows = @(Get-Content -LiteralPath $sampleLog | ForEach-Object { $_ | ConvertFrom-Json })
                if ($rows.Count -ne $stepCount -or @($rows | Where-Object { $_.error -ne $null -or $_.state -ne 'Running' }).Count -ne 0) {
                    throw "Incomplete/failed workload; see $sampleLog"
                }
                $prefix = @($rows | Where-Object { $_.ms.PSObject.Properties.Name -contains 'prefix.break' })
                if ($prefix.Count -ne $stepCount - 1 -or @($prefix | Where-Object { $_.ms.'prefix.break' -ne 0 -or $_.ms.'prefix.envelope.break' -ne 0 -or $_.ms.'prefix.reused_message_fraction' -ne 1 }).Count -ne 0) {
                    throw "Request prefix changed unexpectedly; see $sampleLog"
                }
                $warm = @($rows | Select-Object -Skip 4 | Where-Object { $_.step -ne ([int]($stepCount / 2) + 1) })
                $hits = ($rows | ForEach-Object { $_.ms.'cache.hit' } | Measure-Object -Sum).Sum
                $projectionHits = ($rows | ForEach-Object { $_.ms.'projection.hit' } | Measure-Object -Sum).Sum
                if ($mode -eq 'cached' -and $projectionHits -le 0) { throw "Active context projection was not exercised; see $sampleLog" }
                $results += [pscustomobject]@{
                    Steps = $stepCount; Mode = $mode; Sample = $sample; CacheHits = $hits
                    ProjectionHits = $projectionHits
                    WarmStepP50Ms = Percentile @($warm.ms.total) 0.50
                    WarmStepP95Ms = Percentile @($warm.ms.total) 0.95
                    WarmContextP50Ms = Percentile @($warm.ms.'phase.ContextRead') 0.50
                    WarmContextP95Ms = Percentile @($warm.ms.'phase.ContextRead') 0.95
                    ColdStartMs = $rows[0].ms.total
                    ReconstructedSourceMs = $rows[[int]($stepCount / 2)].ms.total
                    PrefixComparisons = $prefix.Count; PrefixBreaks = 0
                }
            }
        }
    }
    $report = [pscustomobject]@{
        CreatedAt = [DateTimeOffset]::UtcNow.ToString('o')
        OS = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        Processors = [Environment]::ProcessorCount
        MaxWarmStepP95Ms = $MaxWarmStepP95Ms
        Results = $results
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'report.json') -Encoding utf8
    $results | Format-Table -AutoSize
    if (@($results | Where-Object { $_.WarmStepP95Ms -gt $MaxWarmStepP95Ms }).Count -ne 0) {
        throw "Host step latency budget exceeded; see $outputPath/report.json"
    }
}
finally {
    if ($ownsMutex) { $matrixMutex.ReleaseMutex() }
    if ($null -ne $matrixMutex) { $matrixMutex.Dispose() }
}
