param([switch]$ConfirmModelDownload)

$ErrorActionPreference = "Stop"

if (-not $ConfirmModelDownload) {
    throw "Pass -ConfirmModelDownload to download the pinned 1.55 GB Qwen vision assets."
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$manifestPath = Join-Path $PSScriptRoot "vision_model_manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$modelDir = [IO.Path]::GetFullPath((Join-Path $repoRoot "models\vision\qwen3-vl-2b-instruct"))
$modelDirPrefix = $modelDir.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
New-Item -ItemType Directory -Path $modelDir -Force | Out-Null

foreach ($file in $manifest.files) {
    if ($file.name -notmatch '^[A-Za-z0-9._-]+\.gguf$') {
        throw "Manifest contains an unsafe model filename."
    }
    $target = [IO.Path]::GetFullPath((Join-Path $modelDir $file.name))
    if (-not $target.StartsWith($modelDirPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest model path escapes the pinned model directory."
    }
    $partial = "$target.partial"
    if (Test-Path -LiteralPath $target) {
        $item = Get-Item -LiteralPath $target
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($item.Length -eq [int64]$file.size -and $hash -eq $file.sha256) {
            Write-Output "Verified $($file.name)"
            continue
        }
        throw "Existing model artifact failed validation: $target"
    }

    $url = "https://huggingface.co/$($manifest.repository)/resolve/$($manifest.revision)/$($file.name)?download=true"
    Write-Output "Downloading $($file.name)..."
    Invoke-WebRequest -Uri $url -OutFile $partial
    $item = Get-Item -LiteralPath $partial
    $hash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($item.Length -ne [int64]$file.size -or $hash -ne $file.sha256) {
        Remove-Item -LiteralPath $partial -Force
        throw "Downloaded artifact failed size/hash validation: $($file.name)"
    }
    Move-Item -LiteralPath $partial -Destination $target
    Write-Output "Verified $($file.name)"
}

Write-Output "Pinned Qwen3-VL assets are ready in $modelDir"
