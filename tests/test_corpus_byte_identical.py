from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]

VERSIONS = ["v6_1", "v6_2", "v6_2_fresh", "v6_3"]


@pytest.mark.parametrize("version", VERSIONS)
def test_corpus_rebuilds_refactor_neutral(version: str) -> None:
    baseline = ROOT / "scripts" / "corpus_baseline_hashes.json"
    if not baseline.is_file():
        pytest.skip("corpus baseline hashes file missing")
    result = subprocess.run(
        [sys.executable, "scripts/verify_corpus_byte_identical.py", "--version", version],
        cwd=ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert result.returncode == 0, f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
