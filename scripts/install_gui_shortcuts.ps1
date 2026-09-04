[CmdletBinding()]
param(
    [string] $ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $DesktopDirectory = [Environment]::GetFolderPath('Desktop'),
    [string] $RepositoryShortcutPath
)

$ErrorActionPreference = 'Stop'

$project = [System.IO.Path]::GetFullPath($ProjectRoot)
$desktop = [System.IO.Path]::GetFullPath($DesktopDirectory)
if ([string]::IsNullOrWhiteSpace($RepositoryShortcutPath)) {
    $RepositoryShortcutPath = Join-Path $project 'Ru Assistant.lnk'
}
$repositoryShortcut = [System.IO.Path]::GetFullPath($RepositoryShortcutPath)
$executable = Join-Path $project 'gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe'

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    Write-Error "Published RuApp.exe was not found: $executable"
    exit 1
}

$shell = New-Object -ComObject WScript.Shell

function Set-RuShortcut([string] $Path) {
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = $project
    $shortcut.Arguments = ''
    $shortcut.WindowStyle = 1
    $shortcut.Save()
}

Set-RuShortcut (Join-Path $desktop 'Ru Desktop App.lnk')
Set-RuShortcut $repositoryShortcut

$legacy = Join-Path $desktop 'Run MiniGPT Dev.lnk'
if (Test-Path -LiteralPath $legacy -PathType Leaf) {
    Set-RuShortcut $legacy
}
