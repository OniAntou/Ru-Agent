[CmdletBinding()]
param(
    [string]$WorkspaceRoot = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

# One matrix at a time, machine-wide.
#
# The suite spawns real processes and drives real HTTP hosts, so it is sensitive to a busy
# machine. A concurrent run -- another matrix, or a job that publishes and starts the app --
# makes timing-sensitive tests fail for reasons that have nothing to do with the code. That
# produced a red gate three separate times in one session, and every one of those runs was
# green when repeated on an idle machine.
#
# Refusing to start is the honest answer: a gate that runs anyway and reports contention as a
# defect teaches people to ignore it.
$mutexName = "Global\ru-test-matrix-run"
$matrixMutex = New-Object System.Threading.Mutex($false, $mutexName)
$holdsMutex = $false
try {
    $holdsMutex = $matrixMutex.WaitOne(0)
}
catch [System.Threading.AbandonedMutexException] {
    # A previous run died without releasing it. The mutex is ours now.
    $holdsMutex = $true
}

if (-not $holdsMutex) {
    [Console]::Error.WriteLine(
        "Another test matrix is already running. These tests drive real processes, so a " +
        "concurrent run fails them for reasons unrelated to the code. Wait for it to finish, " +
        "or stop it, then run this again.")
    exit 75
}

if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        $scriptDir = $PWD.Path
    }
    $WorkspaceRoot = (Join-Path $scriptDir "..")
}

$workspace = (Resolve-Path -LiteralPath $WorkspaceRoot -ErrorAction Stop).Path
$projects = @(
    foreach ($relativeRoot in @("backend-dotnet", "gui-winui3.Tests")) {
        $testRoot = Join-Path $workspace $relativeRoot
        if (-not (Test-Path -LiteralPath $testRoot -PathType Container)) {
            throw "Missing test root: $testRoot"
        }
        Get-ChildItem -LiteralPath $testRoot -Filter "*Tests.csproj" -File -Recurse -ErrorAction Stop |
            Where-Object {
                $relativePath = if ($_.FullName.StartsWith($testRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    $_.FullName.Substring($testRoot.Length).TrimStart('\', '/')
                } else {
                    $_.FullName
                }
                $relativePath -notmatch '(^|[\\/])(bin|obj|out)([\\/]|$)'
            }
    }
) | Sort-Object -Property FullName

if ($projects.Count -eq 0) {
    [Console]::Error.WriteLine("No .NET test projects were found under '$workspace'.")
    exit 1
}

foreach ($project in $projects) {
    $arguments = @(
        "test"
        $project.FullName
        "--nologo"
        "--configuration"
        $Configuration
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    Write-Host "Running dotnet test: $($project.FullName)"
    try {
        & dotnet @arguments
        $exitCode = $LASTEXITCODE
    }
    catch {
        [Console]::Error.WriteLine("Unable to start dotnet for '$($project.FullName)': $($_.Exception.Message)")
        exit 1
    }

    # No retry. A failing project stops the matrix, and the first failure is the one reported.
    #
    # Retrying was tried and removed: it hides a genuinely flaky test instead of fixing it, and
    # it breaks the contract that a failing project ends the run. The contention that made
    # tests fail is prevented by the lock above rather than absorbed after the fact.
    if ($exitCode -ne 0) {
        [Console]::Error.WriteLine("dotnet test failed for '$($project.FullName)' with exit code $exitCode.")
        exit $exitCode
    }
}

$matrixMutex.ReleaseMutex()
$matrixMutex.Dispose()
