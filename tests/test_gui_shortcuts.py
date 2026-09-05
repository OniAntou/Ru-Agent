"""Verify that Ru's Windows shortcuts launch the GUI without a shell."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path
from typing import cast

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
    return cast(dict[str, object], json.loads(result.stdout))


def _run_installer(
    project: Path,
    desktop: Path,
    repository_shortcut: Path,
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
        assert Path(str(actual["TargetPath"])) == executable
        assert Path(str(actual["WorkingDirectory"])) == project
        assert actual["Arguments"] == ""
        assert actual["WindowStyle"] == 1


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
    assert "Published RuApp.exe was not found" in result.stderr
    assert Path(str(_read_shortcut(repository_shortcut)["TargetPath"])) == sentinel
    assert not (desktop / "Ru Desktop App.lnk").exists()
