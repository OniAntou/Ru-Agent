"""Contract guards for the resource measurement script.

Most of these do not run the script. They assert the interface the README and any
CI gate depend on, so a rename or a dropped parameter fails here instead of
silently breaking the documented command. One smoke test runs the script against
a live process, so "verified" means executed rather than grepped.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "measure_resources.ps1"


def _source() -> str:
    assert SCRIPT.exists(), f"{SCRIPT.relative_to(ROOT)} must exist"
    return SCRIPT.read_text(encoding="utf-8")


def test_script_declares_the_documented_parameters() -> None:
    source = _source()

    for parameter in (
        "$Name",
        "$Id",
        "$DurationSeconds",
        "$IntervalMs",
        "$Output",
        "$MaxCpuPercent",
        "$MaxWorkingSetMb",
        "$WarmupSeconds",
        "$GpuEverySamples",
    ):
        assert parameter in source, f"{parameter} is part of the documented interface"


def test_script_reports_the_three_resource_dimensions() -> None:
    source = _source()

    # The report shape the README documents.
    for field in (
        "cpu_percent",
        "working_set_mb",
        "private_bytes_mb",
        "process_count",
        "utilization_pct",
        "gpu",
        "gates",
    ):
        assert field in source, f"report field {field} is documented"


def test_script_gate_exit_code_is_two() -> None:
    source = _source()

    # Documented contract: a breached limit exits 2, not 1, so callers can tell a
    # measurement failure from a gate failure.
    assert "$exitCode = 2" in source
    assert "exit $exitCode" in source


def test_script_never_terminates_the_target() -> None:
    source = _source()

    for forbidden in ("Stop-Process", "Kill(", ".Kill()"):
        assert forbidden not in source, (
            f"{forbidden} would let the measurement change what it measures"
        )


def test_script_measures_a_live_process_end_to_end(tmp_path: Path) -> None:
    if os.name != "nt":
        pytest.skip("the measurement script uses the ToolHelp snapshot API")
    shell = shutil.which("pwsh") or shutil.which("powershell")
    assert shell, "a PowerShell host is required to run the measurement script"
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    output = tmp_path / "resources.json"
    try:
        result = subprocess.run(
            [
                shell, "-NoProfile", "-NonInteractive", "-File", str(SCRIPT),
                "-Id", str(child.pid), "-DurationSeconds", "1", "-IntervalMs", "200",
                "-Output", str(output), "-NoWrite",
            ],
            capture_output=True, text=True, check=False, timeout=120,
        )
    finally:
        child.terminate()
        child.wait(timeout=30)

    assert result.returncode == 0, result.stdout + result.stderr
    report = json.loads(output.read_text(encoding="utf-8-sig"))
    assert report["schema_version"] == 1
    assert report["target"]["root_pid"] == child.pid
    assert report["cpu_percent"]["sample_count"] >= 1
    for field in ("working_set_mb", "private_bytes_mb", "process_count", "gpu", "gates"):
        assert field in report


def test_retired_latency_benchmark_is_gone() -> None:
    """The latency benchmark was removed in favour of resource measurement.

    It measured HTTP round-trip time, which describes the network rather than the
    agent's footprint. Nothing should reintroduce it under the old name.
    """
    assert not (ROOT / "scripts" / "benchmark_backend.py").exists()
    assert not (ROOT / "docs" / "verification" / "logging-benchmark-2026-08-20.md").exists()

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    assert "benchmark_backend.py" not in readme
    assert "live-compatibility-benchmark" not in readme
