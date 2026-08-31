[CmdletBinding()]
param(
    [string]$WorkspaceRoot = (Join-Path $PSScriptRoot ".."),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$workspace = (Resolve-Path -LiteralPath $WorkspaceRoot -ErrorAction Stop).Path
$projects = @(
    foreach ($relativeRoot in @("backend-dotnet", "gui-winui3.Tests")) {
        $testRoot = Join-Path $workspace $relativeRoot
        if (-not (Test-Path -LiteralPath $testRoot -PathType Container)) {
            throw "Missing test root: $testRoot"
        }
        Get-ChildItem -LiteralPath $testRoot -Filter "*Tests.csproj" -File -Recurse -ErrorAction Stop |
            Where-Object {
                $relativePath = [IO.Path]::GetRelativePath($testRoot, $_.FullName)
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
    if ($exitCode -ne 0) {
        [Console]::Error.WriteLine("dotnet test failed for '$($project.FullName)' with exit code $exitCode.")
        exit $exitCode
    }
}
