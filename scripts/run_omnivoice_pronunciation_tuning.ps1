[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path "D:\LLM\eval_results\omnivoice-pronunciation" (Get-Date -Format "yyyyMMdd-HHmmss")),
    [switch]$ConfirmRun,
    [switch]$OverwriteOutput,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$mainPython = Join-Path $projectRoot ".venv\Scripts\python.exe"
$omniPython = Join-Path $projectRoot "omnivoice_env\Scripts\python.exe"
$gwenPython = Join-Path $projectRoot "gwen_env\Scripts\python.exe"
$benchmarkScript = Join-Path $projectRoot "scripts\benchmark_tts.py"
$manifest = Join-Path $projectRoot "data\evaluations\ru_tts_benchmark_v1.jsonl"
$modelCache = Join-Path $projectRoot "models\omnivoice"
$ruAudio = Join-Path $projectRoot "tyc_talk.wav"
$ruText = Join-Path $projectRoot "reference_text.txt"
$yenNhiAudio = Join-Path $projectRoot "data\voices\gwen\yen_nhi.wav"
$configurationIds = "baseline-16", "vi-normalized-16", "vi-normalized-20", "vi-normalized-24"
$profiles = "ru", "yen_nhi"

function Assert-RunnerInputs {
    foreach ($path in @($mainPython, $omniPython, $gwenPython, $benchmarkScript, $manifest, $ruAudio, $ruText, $yenNhiAudio)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required pronunciation-tuning input is missing: $path"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $modelCache "hub") -PathType Container)) {
        throw "The existing OmniVoice model cache is missing: $modelCache"
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

function Test-OmniVoiceServiceLoaded {
    try {
        $status = Invoke-RestMethod "http://127.0.0.1:5052/status" -TimeoutSec 2
        return $status.model_loaded -eq $true
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

function Write-YenNhiReferenceText([string]$destination) {
    $encoded = & $mainPython -c "import base64; import omnivoice_tts_server as server; print(base64.b64encode(server.YEN_NHI_TEXT.encode('utf-8')).decode('ascii'))"
    if ($LASTEXITCODE -ne 0 -or -not $encoded) {
        throw "Could not load the existing Yến Nhi reference transcript."
    }
    [System.IO.File]::WriteAllBytes($destination, [Convert]::FromBase64String(($encoded | Select-Object -Last 1).Trim()))
}

function Get-AutomaticVerdict([string]$reportPath, [string]$profile, [string]$configId, [double]$gwenMedian) {
    $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    $candidateMedian = $report.summary.median_warm_first_playable_wav_seconds
    $failedRecords = [int]$report.summary.failed_records
    if ($failedRecords -ne 0) {
        $verdict = "fail: benchmark records include failures"
    } elseif ($null -eq $candidateMedian -or -not [double]::IsFinite([double]$candidateMedian)) {
        $verdict = "fail: candidate median is missing or not finite"
    } elseif ([double]$candidateMedian -gt ($gwenMedian / 2)) {
        $verdict = "fail: candidate median is slower than the required 2x Gwen threshold"
    } else {
        $verdict = "pass"
    }
    return [ordered]@{
        voice_profile = $profile
        generation_config_id = $configId
        report_path = $reportPath
        failed_records = $failedRecords
        median_warm_first_playable_wav_seconds = $candidateMedian
        automatic_speed_verdict = $verdict
    }
}

Assert-RunnerInputs
$blocker = Get-RuComputeBlocker
if ($blocker) {
    throw "Ru local model is using the GPU (PID $($blocker.ProcessId)); release it before pronunciation tuning."
}

if ($WhatIf) {
    Write-Output "Preflight only: resolved inputs under $projectRoot"
    Write-Output "Preflight only: Gwen baseline uses profile ru and $manifest"
    foreach ($profile in $profiles) {
        foreach ($configId in $configurationIds) {
            Write-Output "Preflight only: OmniVoice $profile with $configId"
        }
    }
    Write-Output "Preflight only: no directory, service, CUDA load, settings change, or WAV will be created"
    exit 0
}

if (-not $ConfirmRun) {
    throw "Pass -ConfirmRun before generating pronunciation-tuning WAVs."
}
if (Test-OmniVoiceServiceLoaded) {
    throw "OmniVoice service is loaded on port 5052; release it from the GUI before pronunciation tuning."
}
if (Test-Path -LiteralPath $OutputRoot) {
    if (-not $OverwriteOutput) {
        throw "Output already exists: $OutputRoot. Pass -OverwriteOutput to reuse it."
    }
} else {
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
}

$gwenOutput = Join-Path $OutputRoot "gwen"
$gwenReport = Join-Path $OutputRoot "gwen-report.json"
$yenNhiText = Join-Path $OutputRoot "yen_nhi_reference.txt"
$entries = @()

try {
    Start-OwnedGwenService
    & $mainPython $benchmarkScript --engine gwen --manifest $manifest --output-dir $gwenOutput --report $gwenReport --reference-audio $ruAudio --reference-text $ruText --model-cache $modelCache --warm-trials 5 --voice-profile ru
    if ($LASTEXITCODE -ne 0) {
        throw "Gwen baseline failed with exit code $LASTEXITCODE."
    }
} finally {
    Release-Gwen
}

Write-YenNhiReferenceText $yenNhiText
$gwen = Get-Content -Raw -LiteralPath $gwenReport | ConvertFrom-Json
$gwenMedian = $gwen.summary.median_warm_first_playable_wav_seconds
if ($null -eq $gwenMedian -or -not [double]::IsFinite([double]$gwenMedian)) {
    throw "Gwen baseline did not produce a finite median warm first-playable-WAV time."
}

foreach ($profile in $profiles) {
    if ($profile -eq "ru") {
        $referenceAudio = $ruAudio
        $referenceText = $ruText
    } else {
        $referenceAudio = $yenNhiAudio
        $referenceText = $yenNhiText
    }
    foreach ($configId in $configurationIds) {
        $outputDir = Join-Path $OutputRoot (Join-Path $profile $configId)
        $reportPath = Join-Path $outputDir "report.json"
        & $omniPython $benchmarkScript --engine omnivoice --manifest $manifest --output-dir $outputDir --report $reportPath --reference-audio $referenceAudio --reference-text $referenceText --model-cache $modelCache --warm-trials 5 --voice-profile $profile --omnivoice-config $configId
        if ($LASTEXITCODE -ne 0) {
            throw "OmniVoice $profile $configId failed with exit code $LASTEXITCODE."
        }
        $entries += Get-AutomaticVerdict $reportPath $profile $configId ([double]$gwenMedian
        )
    }
}

$summary = [ordered]@{
    schema_version = 1
    status = "pending_manual_review"
    gwen = [ordered]@{
        report_path = $gwenReport
        summary = $gwen.summary
    }
    omnivoice = $entries
    cleanup_command = "Invoke-RestMethod http://127.0.0.1:5051/release -Method Post -ContentType application/json -Body '{}'"
}
$summaryPath = Join-Path $OutputRoot "tuning-summary.json"
$summary | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 -LiteralPath $summaryPath
Write-Output $summaryPath
