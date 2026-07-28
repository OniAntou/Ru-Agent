[CmdletBinding()]
param(
    [switch]$BootstrapVcpkg
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer was not found.'
}

$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw 'Visual Studio Desktop development with C++ is required. Install it manually, then rerun this script.'
}

$cmake = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (-not (Test-Path -LiteralPath $cmake)) {
    throw 'The Visual Studio CMake component is required. Add it in Visual Studio Installer, then rerun this script.'
}

$vcpkgRoot = if ($env:VCPKG_ROOT) {
    $env:VCPKG_ROOT
} else {
    Join-Path $PSScriptRoot '..\.tmp\vcpkg'
}
$vcpkgExecutable = Join-Path $vcpkgRoot 'vcpkg.exe'
if (-not (Test-Path -LiteralPath $vcpkgExecutable)) {
    if (-not $BootstrapVcpkg) {
        throw 'Set VCPKG_ROOT or rerun with -BootstrapVcpkg to create vcpkg in .tmp.'
    }

    if (-not (Test-Path -LiteralPath $vcpkgRoot)) {
        git clone https://github.com/microsoft/vcpkg $vcpkgRoot
        if ($LASTEXITCODE -ne 0) {
            throw "git clone vcpkg failed with exit code $LASTEXITCODE."
        }
    }

    $bootstrap = Join-Path $vcpkgRoot 'bootstrap-vcpkg.bat'
    & $bootstrap
    if ($LASTEXITCODE -ne 0) {
        throw "vcpkg bootstrap failed with exit code $LASTEXITCODE."
    }
}
$env:VCPKG_ROOT = (Resolve-Path -LiteralPath $vcpkgRoot).Path

$source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\tools\ru_desktop_overlay')).Path
$build = Join-Path $source 'out\build'
$toolchain = Join-Path $env:VCPKG_ROOT 'scripts\buildsystems\vcpkg.cmake'
if (-not (Test-Path -LiteralPath $toolchain)) {
    throw "vcpkg toolchain was not found: $toolchain"
}

& $cmake -S $source -B $build "-DCMAKE_TOOLCHAIN_FILE=$toolchain"
if ($LASTEXITCODE -ne 0) {
    throw "CMake configure failed with exit code $LASTEXITCODE."
}

& $cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) {
    throw "CMake build failed with exit code $LASTEXITCODE."
}
