[CmdletBinding()]
param(
    [switch]$BootstrapVcpkg
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    $vswhere = (Get-Command vswhere.exe -ErrorAction SilentlyContinue)?.Source
}
if (-not $vswhere -or -not (Test-Path -LiteralPath $vswhere)) {
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
} elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\.tmp\vcpkg\vcpkg.exe')) {
    Join-Path $PSScriptRoot '..\.tmp\vcpkg'
} elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..\.tmp\vcpkg\vcpkg.exe')) {
    Join-Path $PSScriptRoot '..\..\..\.tmp\vcpkg'
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

$cache = Join-Path $build 'CMakeCache.txt'
$cmakeConfigureArgs = @(
    '-S', $source,
    '-B', $build,
    '-DVCPKG_MANIFEST_MODE=ON',
    '-DRU_BUILD_NATIVE_PROBES=OFF'
)

if (-not (Test-Path -LiteralPath $cache)) {
    # CMake consumes CMAKE_TOOLCHAIN_FILE only during the first configure.
    $cmakeConfigureArgs += "-DCMAKE_TOOLCHAIN_FILE:FILEPATH=$toolchain"
} else {
    $cachedToolchain = Select-String -LiteralPath $cache -Pattern '^CMAKE_TOOLCHAIN_FILE:(?:FILEPATH|UNINITIALIZED)=(.+)$' |
        Select-Object -First 1
    if (-not $cachedToolchain) {
        throw 'Native build cache does not record a vcpkg toolchain. Delete tools/ru_desktop_overlay/out/build and rerun this script.'
    }

    $cachedToolchainPath = $cachedToolchain.Matches[0].Groups[1].Value.Trim()
    if ([IO.Path]::GetFullPath($cachedToolchainPath) -ne [IO.Path]::GetFullPath($toolchain)) {
        throw "Native build cache uses a different vcpkg toolchain: $cachedToolchainPath. Delete tools/ru_desktop_overlay/out/build and rerun this script with the active toolchain: $toolchain"
    }
}

& $cmake @cmakeConfigureArgs
if ($LASTEXITCODE -ne 0) {
    throw "CMake configure failed with exit code $LASTEXITCODE."
}

& $cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) {
    throw "CMake build failed with exit code $LASTEXITCODE."
}

& $cmake --build $build --config Release --target RUN_TESTS
if ($LASTEXITCODE -ne 0) {
    throw "Native overlay tests failed with exit code $LASTEXITCODE."
}
