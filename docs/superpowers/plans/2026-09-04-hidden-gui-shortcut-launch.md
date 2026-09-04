# Hidden GUI Shortcut Launch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every supported Ru shortcut launch the published WinUI executable directly so opening Ru never flashes a Command Prompt or PowerShell window.

**Architecture:** Add one idempotent PowerShell shortcut installer whose only responsibility is validating the published GUI path and writing exact `.lnk` targets through `WScript.Shell`. Keep `start_dev.bat` as the visible diagnostic launcher, while normal shortcuts bypass it and let the existing GUI-owned hidden Host/Worker/Broker lifecycle run unchanged.

**Tech Stack:** PowerShell 5.1, Windows Script Host COM shortcuts, pytest, WinUI 3/.NET 10.

## Global Constraints

- Canonical shortcuts target `D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe` directly.
- Shortcut working directory is `D:\LLM`; arguments are empty; window style is normal.
- Create or update desktop `Ru Desktop App.lnk` and repository `Ru Assistant.lnk`.
- Update desktop `Run MiniGPT Dev.lnk` only when it already exists; never recreate it after removal.
- Do not launch Ru while installing shortcuts and do not alter Backend, Worker, Broker, permissions, or lifecycle code.
- If the published executable is absent, exit nonzero before changing any shortcut.
- Preserve all unrelated dirty working-tree changes and stage only files named by each task.

---

### Task 1: Idempotent direct-GUI shortcut installer

**Files:**
- Create: `scripts/install_gui_shortcuts.ps1`
- Create: `tests/test_gui_shortcuts.py`

**Interfaces:**
- Consumes: published `RuApp.exe`, project root, desktop directory, and repository shortcut path.
- Produces: `scripts/install_gui_shortcuts.ps1 -ProjectRoot <string> -DesktopDirectory <string> -RepositoryShortcutPath <string>` with exit code `0` on complete update and nonzero before mutation when the executable is absent.

- [x] **Step 1: Write the failing success-path test**

Create `tests/test_gui_shortcuts.py` with Windows-only helpers that invoke the real script using temporary paths and inspect shortcuts through `WScript.Shell`:

```python
from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
INSTALLER = ROOT / "scripts" / "install_gui_shortcuts.ps1"
PUBLISH_RELATIVE_PATH = Path(
    "gui-winui3/bin/Release/net10.0-windows10.0.26100.0/"
    "win-x64/publish/RuApp.exe"
)
SHELL = shutil.which("pwsh") or shutil.which("powershell")


def _ps_literal(value: Path) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def _run_powershell(command: str) -> subprocess.CompletedProcess[str]:
    assert SHELL is not None
    return subprocess.run(
        [SHELL, "-NoProfile", "-NonInteractive", "-Command", command],
        capture_output=True,
        text=True,
        check=False,
        timeout=20,
    )


def _create_shortcut(path: Path, target: Path) -> None:
    command = (
        "$s=New-Object -ComObject WScript.Shell;"
        f"$l=$s.CreateShortcut({_ps_literal(path)});"
        f"$l.TargetPath={_ps_literal(target)};$l.Save()"
    )
    result = _run_powershell(command)
    assert result.returncode == 0, result.stdout + result.stderr


def _read_shortcut(path: Path) -> dict[str, object]:
    command = (
        "$s=New-Object -ComObject WScript.Shell;"
        f"$l=$s.CreateShortcut({_ps_literal(path)});"
        "[pscustomobject]@{TargetPath=$l.TargetPath;"
        "WorkingDirectory=$l.WorkingDirectory;Arguments=$l.Arguments;"
        "WindowStyle=$l.WindowStyle}|ConvertTo-Json -Compress"
    )
    result = _run_powershell(command)
    assert result.returncode == 0, result.stdout + result.stderr
    return json.loads(result.stdout)


def _run_installer(
    project: Path, desktop: Path, repository_shortcut: Path,
) -> subprocess.CompletedProcess[str]:
    return _run_powershell(
        f"& {_ps_literal(INSTALLER)} -ProjectRoot {_ps_literal(project)} "
        f"-DesktopDirectory {_ps_literal(desktop)} "
        f"-RepositoryShortcutPath {_ps_literal(repository_shortcut)}"
    )


@pytest.mark.skipif(os.name != "nt", reason="Windows shortcuts require WScript.Shell")
def test_installer_targets_published_gui_without_shell_launcher(tmp_path: Path) -> None:
    project = tmp_path / "checkout"
    desktop = tmp_path / "desktop"
    desktop.mkdir(parents=True)
    executable = project / PUBLISH_RELATIVE_PATH
    executable.parent.mkdir(parents=True)
    executable.write_bytes(b"placeholder")
    repository_shortcut = project / "Ru Assistant.lnk"
    _create_shortcut(desktop / "Run MiniGPT Dev.lnk", project / "start_dev.bat")

    result = _run_installer(project, desktop, repository_shortcut)

    assert result.returncode == 0, result.stdout + result.stderr
    for shortcut in [
        desktop / "Ru Desktop App.lnk",
        desktop / "Run MiniGPT Dev.lnk",
        repository_shortcut,
    ]:
        actual = _read_shortcut(shortcut)
        assert Path(actual["TargetPath"]) == executable
        assert Path(actual["WorkingDirectory"]) == project
        assert actual["Arguments"] == ""
        assert actual["WindowStyle"] == 1
```

- [x] **Step 2: Run the focused test and verify RED**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_gui_shortcuts.py::test_installer_targets_published_gui_without_shell_launcher
```

Expected: FAIL because `scripts/install_gui_shortcuts.ps1` does not exist.

- [x] **Step 3: Write the failing fail-before-mutation test**

Add a test that creates one sentinel shortcut targeting `sentinel.exe`, omits the published GUI, invokes the installer, and asserts a nonzero exit plus the unchanged target:

```python
@pytest.mark.skipif(os.name != "nt", reason="Windows shortcuts require WScript.Shell")
def test_missing_publish_leaves_existing_shortcuts_unchanged(tmp_path: Path) -> None:
    project = tmp_path / "checkout"
    desktop = tmp_path / "desktop"
    desktop.mkdir(parents=True)
    repository_shortcut = project / "Ru Assistant.lnk"
    sentinel = project / "sentinel.exe"
    sentinel.parent.mkdir(parents=True)
    sentinel.write_bytes(b"placeholder")
    _create_shortcut(repository_shortcut, sentinel)

    result = _run_installer(project, desktop, repository_shortcut)

    assert result.returncode != 0
    assert Path(_read_shortcut(repository_shortcut)["TargetPath"]) == sentinel
    assert not (desktop / "Ru Desktop App.lnk").exists()
```

- [x] **Step 4: Implement the minimal installer**

Create `scripts/install_gui_shortcuts.ps1` with explicit testable parameters, resolve all paths before mutation, fail if `RuApp.exe` is absent, and use a local `Set-RuShortcut` function:

```powershell
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
    if (-not (Test-Path -LiteralPath $parent)) {
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
```

- [x] **Step 5: Run focused tests and verify GREEN**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_gui_shortcuts.py
```

Expected: both shortcut tests pass and no GUI process starts.

- [x] **Step 6: Commit the installer slice**

```powershell
git add -- scripts/install_gui_shortcuts.ps1 tests/test_gui_shortcuts.py
git commit -m "fix(launcher): open GUI shortcuts without console flash"
```

---

### Task 2: Install real shortcuts, update current docs, and smoke test

**Files:**
- Modify: `README.md:90-105`
- Modify: `docs/operations/verification.md:390-405`
- Modify: `docs/superpowers/plans/2026-08-30-python-runtime-phase-3-retirement.md:80-92` only if its wording claims the old shortcut target is current rather than historical evidence.
- Runtime update: `%USERPROFILE%\Desktop\Ru Desktop App.lnk`
- Runtime update: `%USERPROFILE%\Desktop\Run MiniGPT Dev.lnk` only if present
- Runtime update: `D:\LLM\Ru Assistant.lnk`

**Interfaces:**
- Consumes: the installer from Task 1 and the current self-contained Release publish.
- Produces: current operator docs and real shortcuts that consistently use direct GUI launch; `start_dev.bat` remains documented as the diagnostic launcher.

- [x] **Step 1: Update active operator documentation**

Replace current claims that shortcuts target `start_dev.bat` with this contract:

```markdown
Normal Windows shortcuts target the published `RuApp.exe` directly so launch
does not open a console window. Run `start_dev.bat` manually when visible
artifact checks and early-startup diagnostics are needed.
```

Leave clearly historical evidence unchanged. Update the Phase 3 plan only if
its statement is written as a current operator instruction.

- [x] **Step 2: Run documentation and launcher tests**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_gui_shortcuts.py tests\test_gui_launcher.py tests\test_current_documentation.py
```

Expected: all selected tests pass.

- [x] **Step 3: Install the real shortcuts**

Run:

```powershell
& D:\LLM\scripts\install_gui_shortcuts.ps1 -ProjectRoot D:\LLM
```

Expected: exit code `0`; canonical shortcuts exist and the legacy shortcut is
updated only if it existed before the run.

- [x] **Step 4: Inspect exact real shortcut properties**

Use `WScript.Shell.CreateShortcut` to read each supported existing shortcut and
assert:

```text
TargetPath       = D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe
WorkingDirectory = D:\LLM
Arguments        =
WindowStyle      = 1
```

- [x] **Step 5: Run live no-console smoke test**

Snapshot current `cmd`, `powershell`, and `pwsh` process IDs. Launch `Ru Desktop
App.lnk`, wait up to 20 seconds for the `RuApp` main window, and compare process
IDs during startup. Assert no new shell process appeared, close Ru through its
main window, wait up to 15 seconds, and assert the GUI-owned `RuBackend`,
`RuAgent.Runtime.Worker`, and `RuAgent.Broker` process tree exits. Do not kill
pre-existing shell processes.

- [x] **Step 6: Run final verification sequentially**

Run:

```powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --verbosity minimal
dotnet test backend-dotnet\RuAgent.Client.Tests\RuAgent.Client.Tests.csproj --no-restore --verbosity minimal
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_gui_shortcuts.py tests\test_gui_launcher.py tests\test_current_documentation.py
git diff --check
```

Expected: every command exits `0`, no test failure, and no whitespace error.

- [x] **Step 7: Commit only this task's owned files**

```powershell
git add -- README.md docs/operations/verification.md docs/superpowers/plans/2026-08-30-python-runtime-phase-3-retirement.md
git diff --cached --stat
git commit -m "docs: document console-free GUI launch"
```

If the historical Phase 3 plan required no edit, omit it from `git add`. Never
stage any unrelated pre-existing change.
