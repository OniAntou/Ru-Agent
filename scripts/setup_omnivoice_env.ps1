[CmdletBinding()]
param(
    [string]$ModelCache = "D:\LLM\models\omnivoice",
    [switch]$ConfirmModelDownload,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePython = Join-Path $projectRoot "gwen_env\Scripts\python.exe"
$environmentPath = Join-Path $projectRoot "omnivoice_env"
$environmentPython = Join-Path $environmentPath "Scripts\python.exe"

if (-not (Test-Path -LiteralPath $sourcePython -PathType Leaf)) {
    throw "Python 3.11 source is missing: $sourcePython"
}

if ($WhatIf) {
    Write-Output "Preflight only: would create $environmentPath"
    Write-Output "Preflight only: would confine OmniVoice cache to $ModelCache"
    Write-Output "Preflight only: would install CUDA Torch and OmniVoice only after -ConfirmModelDownload"
    exit 0
}

if (-not $ConfirmModelDownload) {
    throw "Pass -ConfirmModelDownload before installing OmniVoice or downloading its dependencies."
}

New-Item -ItemType Directory -Path $ModelCache -Force | Out-Null
if (-not (Test-Path -LiteralPath $environmentPython -PathType Leaf)) {
    & $sourcePython -m venv $environmentPath
}

$env:HF_HOME = $ModelCache
$env:HF_HUB_CACHE = Join-Path $ModelCache "hub"
& $environmentPython -m pip install --upgrade pip
& $environmentPython -m pip install "torch==2.8.0+cu128" --index-url "https://download.pytorch.org/whl/cu128"
& $environmentPython -m pip install omnivoice
& $environmentPython -m pip install -e $projectRoot --no-deps

& $environmentPython -c "import os, omnivoice, torch; print('omnivoice=' + getattr(omnivoice, '__version__', 'unknown')); print('torch=' + torch.__version__); print('cuda=' + str(torch.cuda.is_available())); print('HF_HOME=' + os.environ['HF_HOME'])"
