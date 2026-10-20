[CmdletBinding()]
param([string]$ResultsDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repo ('artifacts\terminal-input-' + [guid]::NewGuid().ToString('N'))
}
$resultsRoot = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
$mutex = New-Object System.Threading.Mutex($false, 'Global\ru-test-matrix-run')
$owned = $false
try { $owned = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $owned = $true }
if (-not $owned) { $mutex.Dispose(); Write-Error 'Another test matrix is running.' -ErrorAction Continue; exit 75 }
try {
    $project = Join-Path $repo 'tools\terminal-input-probe\TerminalInputProbe.csproj'
    & dotnet build $project -c Release --nologo *> (Join-Path $resultsRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $resultsRoot 'build.log') -Tail 30; exit $LASTEXITCODE }
    $exe = Join-Path $repo 'tools\terminal-input-probe\bin\Release\net10.0-windows\TerminalInputProbe.exe'
    $results = @()
    foreach ($scenario in @('control','bmp','emoji','fragmented','navigation','typing')) {
        $evidence = Join-Path $resultsRoot $scenario
        & $exe $evidence $scenario *> (Join-Path $resultsRoot ($scenario + '.log'))
        $code = $LASTEXITCODE
        $results += [PSCustomObject]@{ scenario=$scenario; exitCode=$code }
        $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultsRoot 'results.json') -Encoding UTF8
        Write-Output "terminal-input $scenario exit=$code"
        if ($code -ne 0) { Get-Content (Join-Path $resultsRoot ($scenario + '.log')) -Tail 12; exit $code }
    }
}
finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
