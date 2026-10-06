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

    assert len(scenarios) == 11
    assert len({scenario["id"] for scenario in scenarios}) == 11
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
    assert len(document["scenarios"]) == 11
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


def test_scripted_edit_oracle_requires_exact_ordered_completions_and_single_terminal(tmp_path):
    powershell = _powershell()
    module = str(ROOT / "scripts" / "WorkflowEvaluationOracle.psm1").replace("'", "''")
    script = tmp_path / "exercise-oracle.ps1"
    script.write_text(
        f"""
$ErrorActionPreference = 'Stop'
Import-Module '{module}' -Force
$scenario = [pscustomobject]@{{ id = 'recovery'; assertions = @(
    [pscustomobject]@{{ kind = 'first_edit_error'; equals = 'edit_no_match' }},
    [pscustomobject]@{{ kind = 'successful_recovery_edit_count'; equals = 1 }},
    [pscustomobject]@{{ kind = 'final_status'; equals = 'completed' }}
) }}
$failure = [pscustomobject]@{{ event_type = 'tool.execution_completed'; sequence = 4; safe_summary = 'edit_no_match' }}
$success = [pscustomobject]@{{ event_type = 'tool.execution_completed'; sequence = 8; safe_summary = 'ok' }}
$completed = [pscustomobject]@{{ event_type = 'run.state_changed'; sequence = 9; status = 'completed' }}
$good = @($failure, $success, $completed)
$metrics = Get-ScriptedEditMetrics $scenario $good
if ($metrics.tool_calls -ne 2 -or $metrics.failed_tool_calls -ne 1) {{ throw 'The exact valid trace did not pass.' }}

function Assert-Rejected($events, $case) {{
    try {{ Get-ScriptedEditMetrics $scenario $events | Out-Null }}
    catch {{ if ($_.Exception.Message.StartsWith('Oracle failure:')) {{ return }}; throw }}
    throw "The oracle accepted invalid trace: $case"
}}

$reordered = @(
    [pscustomobject]@{{ event_type = 'tool.execution_completed'; sequence = 4; safe_summary = 'ok' }},
    [pscustomobject]@{{ event_type = 'tool.execution_completed'; sequence = 8; safe_summary = 'edit_no_match' }},
    $completed
)
Assert-Rejected $reordered 'reordered completions'
$extra = @($failure, $success,
    [pscustomobject]@{{ event_type = 'tool.execution_completed'; sequence = 9; safe_summary = 'ok' }},
    [pscustomobject]@{{ event_type = 'run.state_changed'; sequence = 10; status = 'completed' }}
)
Assert-Rejected $extra 'extra completion'
$duplicateTerminal = @($good, [pscustomobject]@{{ event_type = 'run.state_changed'; sequence = 10; status = 'completed' }})
Assert-Rejected $duplicateTerminal 'duplicate terminal'
Write-Output 'exact, reordered, extra, and duplicate-terminal oracle checks passed'
""",
        encoding="utf-8",
    )
    completed = subprocess.run(
        [powershell, "-NoProfile", "-File", str(script)],
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )

    assert completed.returncode == 0, completed.stderr
    assert "exact, reordered, extra, and duplicate-terminal oracle checks passed" in completed.stdout


def test_scripted_paging_oracle_requires_enough_calls_and_one_later_completion(tmp_path):
    powershell = _powershell()
    module = str(ROOT / "scripts" / "WorkflowEvaluationOracle.psm1").replace("'", "''")
    script = tmp_path / "exercise-paging-oracle.ps1"
    script.write_text(
        f"""
$ErrorActionPreference = 'Stop'
Import-Module '{module}' -Force
$scenario = [pscustomobject]@{{ id = 'paging'; assertions = @(
    [pscustomobject]@{{ kind = 'minimum_tool_calls'; equals = 3 }},
    [pscustomobject]@{{ kind = 'expected_utf8_sentinel_seen'; equals = $true; path = 'docs/page.txt'; line = 3; sentinel = 'PAGE-SENTINEL' }},
    [pscustomobject]@{{ kind = 'final_status'; equals = 'completed' }}
) }}
$first = [pscustomobject]@{{ event_type = 'tool.execution_completed'; sequence = 2; safe_summary = 'ok' }}
$second = [pscustomobject]@{{ event_type = 'tool.execution_completed'; sequence = 4; safe_summary = 'succeeded' }}
$third = [pscustomobject]@{{ event_type = 'tool.execution_completed'; sequence = 6; safe_summary = 'ok' }}
$done = [pscustomobject]@{{ event_type = 'run.state_changed'; sequence = 7; status = 'completed' }}
$payloads = @(
    [pscustomobject]@{{ event_type = 'tool.execution_completed'; content = '{{"path":"docs/page.txt","offset":0,"content":"head","truncated":true}}' }},
    [pscustomobject]@{{ event_type = 'tool.execution_completed'; content = '{{"path":"docs/page.txt","offset":4,"content":"PAGE-SENTINEL tail","truncated":false}}' }},
    [pscustomobject]@{{ event_type = 'tool.execution_completed'; content = '{{"path":"docs/page.txt","offset":23,"content":"tail","truncated":false}}' }}
)
$metrics = Get-ScriptedPagingMetrics $scenario @($first, $second, $third, $done) $payloads $null
if ($metrics.tool_calls -ne 3 -or $metrics.failed_tool_calls -ne 0) {{ throw 'The exact paging trace did not pass.' }}
foreach ($invalid in @(
    ,@($first, $second, $done),
    ,@($first, $second, $third, $done, [pscustomobject]@{{ event_type = 'run.state_changed'; sequence = 8; status = 'completed' }})
)) {{
    try {{ Get-ScriptedPagingMetrics $scenario $invalid $payloads $null | Out-Null }}
    catch {{ if ($_.Exception.Message.StartsWith('Oracle failure:')) {{ continue }}; throw }}
    throw 'The paging oracle accepted an incomplete or duplicate-terminal trace.'
}}
Write-Output 'paging minimum and single-terminal oracle checks passed'
""",
        encoding="utf-8",
    )
    completed = subprocess.run(
        [powershell, "-NoProfile", "-File", str(script)],
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )

    assert completed.returncode == 0, completed.stderr
    assert "paging minimum and single-terminal oracle checks passed" in completed.stdout


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
        elif kind == "sparse_search_output":
            sentinels = entry.get("sentinels", {})
            prefix = entry.get("linePrefix", "line ")
            match_every = entry["matchEvery"]
            lines = []
            for line in range(1, entry["count"] + 1):
                if line % match_every == match_every - 1:
                    lines.append(sentinels.get(str(line), f"{prefix}{line}"))
                else:
                    lines.append(f"unrelated note {line}")
            content = "".join(f"{line}\n" for line in lines)
        else:
            raise AssertionError(f"Unknown workspace overlay kind: {kind}")
        files[entry["path"]] = content
    return [{"path": path, "content": content} for path, content in files.items()]


def _powershell():
    executable = shutil.which("pwsh") or shutil.which("powershell")
    if executable is None:
        raise AssertionError("PowerShell is required to exercise the Windows workflow runner.")
    return executable
