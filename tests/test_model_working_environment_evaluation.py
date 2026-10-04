import hashlib
import json
import shutil
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "tests" / "fixtures" / "model-working-environment-scenarios.json"
RUNNER = ROOT / "scripts" / "evaluate_agent_workflows.ps1"


def test_workflow_oracles_are_complete_and_explicitly_unmet_at_baseline():
    document = json.loads(FIXTURE.read_text(encoding="utf-8"))
    scenarios = document["scenarios"]

    assert len(scenarios) == 12
    assert len({scenario["id"] for scenario in scenarios}) == 12
    for scenario in scenarios:
        assert scenario["input"].strip()
        assert scenario["preset"] in {"plan", "standard"}
        assert scenario["expectedFiles"]
        assert scenario["assertions"]
        assert scenario["baselineStatus"] in {"unmet", "not_run"}
        assert scenario["workspaceSha256"] == _workspace_hash(_scenario_files(document, scenario))
        for expected in scenario["expectedFiles"]:
            if expected["state"] in {"changed_to_expected", "changed_only_after_observed_failure"}:
                assert expected.get("content", "").strip()
                assert expected.get("sha256") == hashlib.sha256(expected["content"].encode()).hexdigest()
    turns = next(item for item in scenarios if item["id"] == "remember_constraint_after_nine_turns")["turns"]
    assert len(turns) == 10
    assert "public API" in turns[0]


def test_runner_validate_only_emits_no_live_or_quality_claims(tmp_path):
    powershell = _powershell()
    report = tmp_path / "report.json"
    completed = subprocess.run(
        [powershell, "-NoProfile", "-File", str(RUNNER), "-ValidateOnly", "-ReportPath", str(report)],
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )

    assert completed.returncode == 0, completed.stderr
    document = json.loads(report.read_text(encoding="utf-8-sig"))
    assert document["live_provider"] == "not_run"
    assert document["task_quality_pass_rate"] is None
    assert len(document["scenarios"]) == 12
    assert all(scenario["quality_status"] == "not_measured" for scenario in document["scenarios"])


def test_runner_rejects_a_fixture_with_a_wrong_workspace_hash(tmp_path):
    powershell = _powershell()
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    fixture["scenarios"][0]["workspaceSha256"] = "0" * 64
    invalid_fixture = tmp_path / "bad-fixture.json"
    invalid_fixture.write_text(json.dumps(fixture), encoding="utf-8")
    report = tmp_path / "report.json"
    completed = subprocess.run(
        [powershell, "-NoProfile", "-File", str(RUNNER), "-FixturePath", str(invalid_fixture), "-ReportPath", str(report)],
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )

    assert completed.returncode != 0
    assert "Workspace hash mismatch" in completed.stderr
    assert not report.exists()


def test_runner_rejects_unsafe_overlay_paths_before_creating_a_report(tmp_path):
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    fixture["scenarios"][0]["workspaceOverlay"] = [{"path": "../escape.txt", "kind": "literal", "content": "x"}]
    _run_invalid_fixture(tmp_path, fixture, "unsafe path segment")


def test_runner_rejects_duplicate_base_paths_before_hashing(tmp_path):
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    fixture["workspaceFiles"].append(dict(fixture["workspaceFiles"][0]))
    _run_invalid_fixture(tmp_path, fixture, "is duplicated")


def _run_invalid_fixture(tmp_path, fixture, expected_error):
    powershell = _powershell()
    invalid_fixture = tmp_path / "bad-fixture.json"
    invalid_fixture.write_text(json.dumps(fixture), encoding="utf-8")
    report = tmp_path / "report.json"
    completed = subprocess.run(
        [powershell, "-NoProfile", "-File", str(RUNNER), "-FixturePath", str(invalid_fixture), "-ReportPath", str(report)],
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )

    assert completed.returncode != 0
    assert expected_error in completed.stderr
    assert not report.exists()


def _workspace_hash(files):
    digest = hashlib.sha256()
    for entry in sorted(files, key=lambda item: item["path"].casefold()):
        digest.update(entry["path"].encode("utf-8"))
        digest.update(b"\0")
        digest.update(entry["content"].encode("utf-8"))
        digest.update(b"\0")
    return digest.hexdigest()


def _scenario_files(document, scenario):
    files = {entry["path"]: entry["content"] for entry in document["workspaceFiles"]}
    for entry in scenario.get("workspaceOverlay", []):
        kind = entry.get("kind", "literal")
        if kind == "literal":
            content = entry["content"]
        elif kind == "repeated_utf8":
            content = entry["line"] * entry["count"] + entry["suffix"]
        elif kind == "numbered_output":
            sentinels = entry.get("sentinels", {})
            prefix = entry.get("linePrefix", "line ")
            content = "".join(f"{sentinels.get(str(line), f'{prefix}{line}')}\n" for line in range(1, entry["count"] + 1))
        else:
            raise AssertionError(f"Unknown workspace overlay kind: {kind}")
        files[entry["path"]] = content
    return [{"path": path, "content": content} for path, content in files.items()]


def _powershell():
    executable = shutil.which("pwsh") or shutil.which("powershell")
    if executable is None:
        raise AssertionError("PowerShell is required to exercise the Windows workflow runner.")
    return executable
