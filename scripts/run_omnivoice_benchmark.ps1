[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path "D:\LLM\eval_results\omnivoice" (Get-Date -Format "yyyyMMdd-HHmmss")),
    [switch]$ConfirmModelDownload,
    [switch]$OverwriteOutput,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$mainPython = Join-Path $projectRoot ".venv\Scripts\python.exe"
$omniPython = Join-Path $projectRoot "omnivoice_env\Scripts\python.exe"
$gwenPython = Join-Path $projectRoot "gwen_env\Scripts\python.exe"
$referenceAudio = Join-Path $projectRoot "tyc_talk.wav"
$referenceText = Join-Path $projectRoot "reference_text.txt"
$manifest = Join-Path $projectRoot "data\evaluations\ru_tts_benchmark_v1.jsonl"
$modelCache = Join-Path $projectRoot "models\omnivoice"
$benchmarkScript = Join-Path $projectRoot "scripts\benchmark_tts.py"

function Assert-BenchmarkInputs {
    foreach ($path in @($mainPython, $gwenPython, $referenceAudio, $referenceText, $manifest, $benchmarkScript)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required benchmark input is missing: $path"
        }
    }
}

function Get-RuComputeBlocker {
    $gpuPids = @(& nvidia-smi --query-compute-apps=pid --format=csv,noheader,nounits 2>$null |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match '^\d+$' } |
        ForEach-Object { [int]$_ })
    if (-not $gpuPids) {
        return $null
    }
    $pattern = 'llama-server|llama-cli|mini_gpt\.transformers|transformers.*mini_gpt'
    return Get-CimInstance Win32_Process | Where-Object {
        $_.ProcessId -in $gpuPids -and $_.CommandLine -match $pattern
    } | Select-Object -First 1
}

function Test-GwenService {
    try {
        Invoke-RestMethod "http://127.0.0.1:5051/status" -TimeoutSec 2 | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Start-OwnedGwenService {
    if (Test-GwenService) {
        return
    }
    Start-Process -FilePath $gwenPython -ArgumentList (Join-Path $projectRoot "gwen_tts_server.py") -WorkingDirectory $projectRoot -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (Test-GwenService) {
            return
        }
    }
    throw "Owned Gwen service did not become ready on port 5051."
}

function Release-Gwen {
    try {
        Invoke-RestMethod "http://127.0.0.1:5051/release" -Method Post -ContentType "application/json" -Body "{}" -TimeoutSec 30 | Out-Null
    } catch {
        Write-Warning "Could not release Gwen: $($_.Exception.Message)"
    }
}

Assert-BenchmarkInputs
$blocker = Get-RuComputeBlocker
if ($blocker) {
    throw "Ru local model is using the GPU (PID $($blocker.ProcessId)); release it before running the benchmark."
}

if ($WhatIf) {
    Write-Output "Preflight only: inputs resolved under $projectRoot"
    Write-Output "Preflight only: Gwen cold plus five warm passes would write to $(Join-Path $OutputRoot 'gwen')"
    Write-Output "Preflight only: OmniVoice cold plus five warm passes would run from $omniPython and write to $(Join-Path $OutputRoot 'omnivoice')"
    Write-Output "Preflight only: no service, package install, model download, CUDA load, or output directory will be created"
    exit 0
}

if (-not $ConfirmModelDownload) {
    throw "Pass -ConfirmModelDownload before the OmniVoice candidate run."
}
if (-not (Test-Path -LiteralPath $omniPython -PathType Leaf)) {
    throw "OmniVoice environment is missing. Run scripts\setup_omnivoice_env.ps1 -ConfirmModelDownload first."
}
if (Test-Path -LiteralPath $OutputRoot) {
    if (-not $OverwriteOutput) {
        throw "Output already exists: $OutputRoot. Pass -OverwriteOutput to reuse it."
    }
} else {
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
}

$gwenOutput = Join-Path $OutputRoot "gwen"
$omniOutput = Join-Path $OutputRoot "omnivoice"
$gwenReport = Join-Path $OutputRoot "gwen-report.json"
$omniReport = Join-Path $OutputRoot "omnivoice-report.json"
$modelPreviouslyCached = Test-Path -LiteralPath (Join-Path $modelCache "hub")

try {
    Start-OwnedGwenService
    & $mainPython $benchmarkScript --engine gwen --manifest $manifest --output-dir $gwenOutput --report $gwenReport --reference-audio $referenceAudio --reference-text $referenceText --model-cache $modelCache --warm-trials 5
    if ($LASTEXITCODE -ne 0) { throw "Gwen baseline failed with exit code $LASTEXITCODE." }
} finally {
    Release-Gwen
}

& $omniPython $benchmarkScript --engine omnivoice --manifest $manifest --output-dir $omniOutput --report $omniReport --reference-audio $referenceAudio --reference-text $referenceText --model-cache $modelCache --warm-trials 5
if ($LASTEXITCODE -ne 0) { throw "OmniVoice candidate failed with exit code $LASTEXITCODE." }

$gwen = Get-Content -Raw $gwenReport | ConvertFrom-Json
$omnivoice = Get-Content -Raw $omniReport | ConvertFrom-Json
$summary = [ordered]@{
    schema_version = 1
    status = "pending_manual_review"
    model_previously_cached = $modelPreviouslyCached
    gwen_report = $gwenReport
    omnivoice_report = $omniReport
    gwen_summary = $gwen.summary
    omnivoice_summary = $omnivoice.summary
    cleanup_command = "Invoke-RestMethod http://127.0.0.1:5051/release -Method Post -ContentType application/json -Body '{}'"
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $OutputRoot "summary.json")
Write-Output (Join-Path $OutputRoot "summary.json")
