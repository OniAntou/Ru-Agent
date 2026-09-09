[CmdletBinding()]
param(
    [string]$ModelCache = "D:\LLM\models\omnivoice",
    [string]$SourcePython = "",
    [switch]$ConfirmModelDownload,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePython = if ($SourcePython.Trim()) {
    $SourcePython
} else {
    Join-Path $projectRoot ".venv\Scripts\python.exe"
}
$environmentPath = Join-Path $projectRoot "omnivoice_env"
$environmentPython = Join-Path $environmentPath "Scripts\python.exe"
$pipCache = Join-Path $projectRoot "pip-cache\omnivoice"
$temporaryDirectory = Join-Path $projectRoot ".tmp\omnivoice-install"

if (-not (Test-Path -LiteralPath $sourcePython -PathType Leaf)) {
    $pythonCommand = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($pythonCommand) {
        $sourcePython = $pythonCommand.Source
    } else {
        throw "Python source is missing: $sourcePython. Pass -SourcePython with a Python 3.11+ executable."
    }
}

if ($WhatIf) {
    Write-Output "Preflight only: would create $environmentPath"
    Write-Output "Preflight only: would confine OmniVoice cache to $ModelCache"
    Write-Output "Preflight only: would confine pip cache to $pipCache"
    Write-Output "Preflight only: would confine installer temporary files to $temporaryDirectory"
    Write-Output "Preflight only: would install CUDA Torch and OmniVoice only after -ConfirmModelDownload"
    exit 0
}

if (-not $ConfirmModelDownload) {
    throw "Pass -ConfirmModelDownload before installing OmniVoice or downloading its dependencies."
}

New-Item -ItemType Directory -Path $ModelCache -Force | Out-Null
New-Item -ItemType Directory -Path $pipCache -Force | Out-Null
New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $environmentPython -PathType Leaf)) {
    & $sourcePython -m venv $environmentPath
}

$env:HF_HOME = $ModelCache
$env:HF_HUB_CACHE = Join-Path $ModelCache "hub"
$env:PIP_CACHE_DIR = $pipCache
$env:TEMP = $temporaryDirectory
$env:TMP = $temporaryDirectory
& $environmentPython -m pip install --cache-dir $pipCache --upgrade pip
if ($LASTEXITCODE -ne 0) { throw "Could not upgrade pip in $environmentPath." }
& $environmentPython -m pip install --cache-dir $pipCache "torch==2.8.0+cu128" --index-url "https://download.pytorch.org/whl/cu128"
if ($LASTEXITCODE -ne 0) { throw "Could not install CUDA Torch in $environmentPath." }
& $environmentPython -m pip install --cache-dir $pipCache "torchaudio==2.8.0+cu128" --index-url "https://download.pytorch.org/whl/cu128"
if ($LASTEXITCODE -ne 0) { throw "Could not install matching CUDA torchaudio in $environmentPath." }
& $environmentPython -m pip install --cache-dir $pipCache omnivoice flask numpy soundfile
if ($LASTEXITCODE -ne 0) { throw "Could not install OmniVoice and its loopback service in $environmentPath." }
& $environmentPython -m pip install --cache-dir $pipCache -e $projectRoot --no-deps
if ($LASTEXITCODE -ne 0) { throw "Could not install Ru benchmark sources in $environmentPath." }

& $environmentPython -c "import flask, os, omnivoice, torch, numpy, soundfile; print('flask=' + flask.__version__); print('omnivoice=' + getattr(omnivoice, '__version__', 'unknown')); print('torch=' + torch.__version__); print('numpy=' + numpy.__version__); print('soundfile=' + getattr(soundfile, '__version__', 'unknown')); print('cuda=' + str(torch.cuda.is_available())); print('HF_HOME=' + os.environ['HF_HOME'])"
