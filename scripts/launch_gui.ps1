[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [string] $WorkingDirectory,

    [int] $StartupGracePeriodMilliseconds = 2000
)

$ErrorActionPreference = 'Stop'

try {
    $resolvedPath = [System.IO.Path]::GetFullPath($ExecutablePath)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "GUI executable was not found: $resolvedPath"
    }

    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $resolvedWorkingDirectory = Split-Path -Parent $resolvedPath
    }
    else {
        $resolvedWorkingDirectory = [System.IO.Path]::GetFullPath($WorkingDirectory)
        if (-not (Test-Path -LiteralPath $resolvedWorkingDirectory -PathType Container)) {
            throw "GUI working directory was not found: $resolvedWorkingDirectory"
        }
    }

    $process = Start-Process -FilePath $resolvedPath -WorkingDirectory $resolvedWorkingDirectory -PassThru
    Start-Sleep -Milliseconds $StartupGracePeriodMilliseconds

    if ($process.HasExited -and $process.ExitCode -ne 0) {
        throw "RuApp exited during startup with code $($process.ExitCode)."
    }
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}

exit 0
