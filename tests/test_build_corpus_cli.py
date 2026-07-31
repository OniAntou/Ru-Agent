from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PYTHON = sys.executable
BUILD_CLI = "scripts/build_corpus.py"


def _run(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [PYTHON, BUILD_CLI, *args],
        cwd=ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )


def test_cli_requires_version() -> None:
    result = _run()
    assert result.returncode == 2
    assert "--version" in result.stderr


def test_cli_unknown_version_rejected() -> None:
    result = _run("--version", "v6_9")
    assert result.returncode == 2


def test_cli_v6_3_audit_only_writes_nothing(tmp_path: Path) -> None:
    result = _run("--version", "v6_3", "--audit-only", "--output-dir", str(tmp_path))
    assert result.returncode == 0
    payload = json.loads(result.stdout)
    assert payload["report"]["authored_total_examples"] == 180
    assert not (tmp_path / "train.jsonl").exists()


def test_cli_v6_1_audit_dumps_held_out(tmp_path: Path) -> None:
    result = _run("--version", "v6_1", "--audit")
    assert result.returncode == 0
    payload = json.loads(result.stdout)
    assert payload["report"]["total_examples"] == 2400
    assert len(payload["held_out"]) == 360


def test_cli_fresh_missing_files_exits_1(tmp_path: Path) -> None:
    result = _run(
        "--version",
        "v6_2_fresh",
        "--approved-file",
        str(tmp_path / "missing-approved.jsonl"),
        "--memory-file",
        str(tmp_path / "missing-memory.json"),
        "--audit-only",
    )
    assert result.returncode == 0  # audit-only bỏ qua check file
    result = _run(
        "--version",
        "v6_2_fresh",
        "--approved-file",
        str(tmp_path / "missing-approved.jsonl"),
        "--memory-file",
        str(tmp_path / "missing-memory.json"),
    )
    assert result.returncode == 1
    assert "approved-file not found" in result.stderr
