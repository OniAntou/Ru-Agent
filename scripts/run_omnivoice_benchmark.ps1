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
$referenceAudio = Join-Path $projectRoot "tyc_talk.wav"
$referenceText = Join-Path $projectRoot "reference_text.txt"
$manifest = Join-Path $projectRoot "data\evaluations\ru_tts_quality_latency_v2.jsonl"
$modelCache = Join-Path $projectRoot "models\omnivoice"
$benchmarkScript = Join-Path $projectRoot "scripts\benchmark_tts.py"

function Assert-BenchmarkInputs {
    foreach ($path in @($mainPython, $omniPython, $referenceAudio, $referenceText, $manifest, $benchmarkScript)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required OmniVoice benchmark input is missing: $path"
        }
    }
}

Assert-BenchmarkInputs

if ($WhatIf) {
    Write-Output "Preflight only: OmniVoice cold plus five warm passes would run from $omniPython"
    Write-Output "Preflight only: output would be written to $OutputRoot"
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

$omniOutput = Join-Path $OutputRoot "omnivoice"
$omniReport = Join-Path $OutputRoot "omnivoice-report.json"
$modelPreviouslyCached = Test-Path -LiteralPath (Join-Path $modelCache "hub")

& $omniPython $benchmarkScript --engine omnivoice --manifest $manifest --output-dir $omniOutput --report $omniReport --reference-audio $referenceAudio --reference-text $referenceText --model-cache $modelCache --warm-trials 5
if ($LASTEXITCODE -ne 0) { throw "OmniVoice candidate failed with exit code $LASTEXITCODE." }

$omnivoice = Get-Content -Raw $omniReport | ConvertFrom-Json
$summary = [ordered]@{
    schema_version = 2
    status = "pending_manual_review"
    model_previously_cached = $modelPreviouslyCached
    omnivoice_report = $omniReport
    omnivoice_summary = $omnivoice.summary
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $OutputRoot "summary.json")
Write-Output (Join-Path $OutputRoot "summary.json")
