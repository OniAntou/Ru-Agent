from __future__ import annotations

import json
from pathlib import Path

from check_shared_workspace_detection import run_gate


def test_shared_workspace_fixture_covers_external_and_conflict_paths() -> None:
    root = Path(__file__).resolve().parents[1]
    fixture = root / "tests" / "fixtures" / "shared-workspace-scenarios.json"
    summary = run_gate(fixture)
    assert summary["failed"] == 0
    assert summary["scenarios"] >= 10
    names = {item["name"] for item in summary["results"]}
    assert {"ru_plus_codex", "ru_plus_opencode", "binary_overlap", "restart_recovery"} <= names


def test_fixture_never_assigns_external_identity() -> None:
    fixture = Path(__file__).resolve().parent / "fixtures" / "shared-workspace-scenarios.json"
    scenarios = json.loads(fixture.read_text(encoding="utf-8"))
    external = [item for item in scenarios if item["writer"] == "unknown_external"]
    assert external
    assert all(item["writer"] == "unknown_external" for item in external)
