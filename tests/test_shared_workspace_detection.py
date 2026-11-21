from __future__ import annotations

import json
from pathlib import Path

from check_shared_workspace_detection import KNOWN_EVENTS, main, run_gate


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
    named_rivals = [item for item in scenarios if "codex" in item["name"] or "opencode" in item["name"]]
    assert named_rivals
    # A scenario that names another agent must still classify its writer as an
    # unknown external process: Ru never attributes the identity it cannot prove.
    assert all(item["writer"] == "unknown_external" for item in named_rivals)


def test_gate_reports_real_failures(tmp_path: Path) -> None:
    broken = tmp_path / "broken.json"
    broken.write_text(
        json.dumps([{"path": "a.txt", "writer": "unknown_external"}]),
        encoding="utf-8",
    )

    summary = run_gate(broken)

    assert summary["failed"] == 1
    assert summary["scenarios"] == 1

    # Automation reads the exit code, not the JSON: a failed scenario must not
    # exit 0.
    assert main(["--fixture", str(broken)]) == 1


def test_fixture_event_vocabulary_stays_known_and_keeps_the_binary_conflict(
    tmp_path: Path,
) -> None:
    fixture = Path(__file__).resolve().parent / "fixtures" / "shared-workspace-scenarios.json"
    scenarios = json.loads(fixture.read_text(encoding="utf-8"))
    unknown = sorted({item["expected"] for item in scenarios} - KNOWN_EVENTS)
    assert unknown == []
    binary = [item for item in scenarios if item["name"] == "binary_overlap"]
    assert binary
    assert binary[0]["expected"] == "concurrent_binary_conflict"

    # An event name outside the vocabulary is a scenario failure, not a pass.
    broken = tmp_path / "shared-workspace-unknown-event.json"
    broken.write_text(
        json.dumps([{"name": "bad", "writer": "unknown_external", "path": "a.txt",
                     "expected": "not_an_event"}]),
        encoding="utf-8",
    )
    assert run_gate(broken)["failed"] == 1
