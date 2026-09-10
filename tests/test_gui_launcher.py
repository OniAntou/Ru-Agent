"""Exercise the startup probe with its process-launch boundary replaced."""

from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]


@pytest.mark.parametrize(
    ("has_exited", "exit_code", "expected_result"),
    [(True, 0, 0), (True, 27, 1), (False, 27, 0)],
)
def test_startup_probe_rejects_only_an_early_nonzero_exit(
    tmp_path: Path, has_exited: bool, exit_code: int, expected_result: int,
) -> None:
    # The real helper runs, but Start-Process never launches an executable.
    executable = tmp_path / "RuApp.exe"
    executable.write_bytes(b"test-only placeholder")
    helper = ROOT / "scripts/launch_gui.ps1"
    def quote(value: Path) -> str:
        return "'" + str(value).replace("'", "''") + "'"

    # The harness object must expose ExitCode as a plain property: the
    # production script reads $process.ExitCode right after $process.HasExited.
    command = (
        "function Start-Process { param($FilePath, $WorkingDirectory, [switch]$PassThru) "
        f"[pscustomobject]@{{ HasExited = ${str(has_exited).lower()}; ExitCode = {exit_code} }} }}; "
        f"& {quote(helper)} -ExecutablePath {quote(executable)} "
        f"-WorkingDirectory {quote(tmp_path)} -StartupGracePeriodMilliseconds 0"
    )
    shell = shutil.which("pwsh") or shutil.which("powershell")
    assert shell is not None
    result = subprocess.run(
        [shell, "-NoProfile", "-NonInteractive", "-Command", command],
        capture_output=True, text=True, check=False, timeout=20,
    )
    assert result.returncode == expected_result, result.stdout + result.stderr
