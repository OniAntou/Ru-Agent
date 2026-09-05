[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [Parameter(Mandatory = $true)]
    [string] $DriverProject,

    [ValidateRange(1, 300)]
    [double] $TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"

function Resolve-ExistingFile([string] $PathValue, [string] $Label) {
    $resolved = [System.IO.Path]::GetFullPath($PathValue)
    if (-not [System.IO.File]::Exists($resolved)) {
        throw "$Label does not exist: $resolved"
    }

    return $resolved
}

function Get-PythonExecutable([string] $ProjectRoot) {
    $candidate = Join-Path $ProjectRoot ".venv\Scripts\python.exe"
    if ([System.IO.File]::Exists($candidate)) {
        return $candidate
    }

    return "python"
}

$resolvedApp = Resolve-ExistingFile $AppPath "RuApp executable"
$resolvedDriver = Resolve-ExistingFile $DriverProject "GUI Harness project"
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) ".."))

$existing = @(Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($resolvedApp)) -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {
    throw "An existing RuApp instance is running; close it before the interactive Harness."
}

$python = Get-PythonExecutable $projectRoot
$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ru-gui-harness-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$scenarioArtifacts = [System.Collections.Generic.List[object]]::new()
$scenarios = @("turn", "stop", "cancel", "stale-session")

try {
    foreach ($scenario in $scenarios) {
        $scenarioOutput = Join-Path $runRoot "$scenario.json"
        $pythonArgs = @(
            (Join-Path $projectRoot "scripts\ru_harness.py"),
            "--project-root", $projectRoot,
            "--mode", "gui-deterministic",
            "--app-path", $resolvedApp,
            "--driver-project", $resolvedDriver,
            "--scenario", $scenario,
            "--timeout-seconds", $TimeoutSeconds,
            "--output", $scenarioOutput
        )
        & $python @pythonArgs
        $pythonExit = $LASTEXITCODE
        if (-not [System.IO.File]::Exists($scenarioOutput)) {
            throw "GUI Harness did not write an artifact for scenario $scenario (exit $pythonExit)."
        }

        $artifact = Get-Content -LiteralPath $scenarioOutput -Raw | ConvertFrom-Json
        $scenarioArtifacts.Add($artifact)
    }

    $allChecks = [System.Collections.Generic.List[object]]::new()
    $allErrors = [System.Collections.Generic.List[object]]::new()
    $guiScenarioComponents = [ordered]@{}
    $cleanup = [ordered]@{
        children_terminated = $true
        ports_closed = $true
        session_descriptor_removed = $true
        fixture_root_removed = $true
        temporary_root_removed = $true
    }
    $cleanupKeys = @($cleanup.Keys)
    $duration = 0.0
    $first = $scenarioArtifacts[0]
    for ($index = 0; $index -lt $scenarioArtifacts.Count; $index++) {
        $artifact = $scenarioArtifacts[$index]
        $duration += [double]$artifact.duration_ms
        foreach ($check in $artifact.checks) {
            $allChecks.Add($check) | Out-Null
        }
        foreach ($error in $artifact.errors) {
            $allErrors.Add($error) | Out-Null
        }
        foreach ($key in $cleanupKeys) {
            $cleanup[$key] = [bool]$cleanup[$key] -and [bool]$artifact.cleanup.$key
        }
        $guiScenarioComponents[$scenarios[$index]] = $artifact.components.gui
    }

    $guiLaunched = @($scenarioArtifacts | Where-Object { -not [bool]$_.components.gui.launched }).Count -eq 0
    $guiClosed = @($scenarioArtifacts | Where-Object { -not [bool]$_.components.gui.closed }).Count -eq 0
    $failed = @($allChecks | Where-Object {
        $_.status -in @("failed", "blocked_by_startup", "cleanup_failure")
    }).Count + $allErrors.Count
    if (-not $guiLaunched -or -not $guiClosed) {
        $failed = [Math]::Max(1, $failed)
    }
    $guiPassed = $failed -eq 0 -and $guiLaunched -and $guiClosed
    $final = [ordered]@{
        schema_version = 2
        harness_version = "vnext-1"
        commit = [string]$first.commit
        mode = "full-deterministic"
        started_at = [string]$first.started_at
        duration_ms = [Math]::Round($duration, 3)
        components = [ordered]@{
            worker = $first.components.worker
            edge = $first.components.edge
            gui = [ordered]@{
                status = if ($guiPassed) { "passed" } else { "failed" }
                launched = $guiLaunched
                closed = $guiClosed
                scenarios = $guiScenarioComponents
            }
        }
        checks = $allChecks
        coverage = $first.coverage
        cleanup = $cleanup
        errors = $allErrors
        summary = [ordered]@{
            status = if ($failed -eq 0) { "passed" } else { "failed" }
            failed_checks = $failed
        }
    }

    $outputParent = Split-Path -Parent $resolvedOutput
    if (-not [System.IO.Directory]::Exists($outputParent)) {
        New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
    }
    $json = $final | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($resolvedOutput, $json + [Environment]::NewLine)

    if ($failed -ne 0) {
        exit 1
    }
}
finally {
    if ([System.IO.Directory]::Exists($runRoot)) {
        $resolvedRunRoot = [System.IO.Path]::GetFullPath($runRoot)
        if ($resolvedRunRoot.StartsWith([System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()), [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
