import hashlib
import importlib.util
import json
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(ROOT))
spec = importlib.util.spec_from_file_location("ru_coding_cohort", ROOT / "cohort.py")
cohort = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cohort)


def pins():
    return {
        "agent": "Ru",
        "agent_version": "dev",
        "source_commit": "a" * 40,
        "source_tree_sha256": "c" * 64,
        "artifact_sha256": "d" * 64,
        "model": "model-x",
        "effort": "high",
        "protocol": "responses",
        "tool_profile": "coding_v2",
        "toolchain": "native-win-x64-dotnet10-node22",
        "deadline_seconds": 1200,
    }


def task():
    return {
        "id": "heldout/multi-file",
        "fixture_hash": "b" * 64,
        "instruction": "Repair the multi-file workflow.",
    }


def test_manifest_pins_commit_prompt_and_fixture_without_exporting_prompt_or_oracle():
    manifest = cohort.create_manifest([task()], pins(), attempts_per_task=2)
    assert manifest["schema_version"] == 2
    assert manifest["cohort"]["source_commit"] == "a" * 40
    assert manifest["tasks"] == [{
        "task_id": "heldout/multi-file",
        "task_sha256": "b" * 64,
        "fixture_sha256": "b" * 64,
        "prompt_sha256": hashlib.sha256(task()["instruction"].encode()).hexdigest(),
    }]
    assert "instruction" not in json.dumps(manifest)
    assert "solution" not in json.dumps(manifest)


def test_observation_record_separates_acceptance_completion_and_unknown_metrics():
    manifest = cohort.create_manifest([task()], pins(), attempts_per_task=1)
    record = cohort.record_attempt(
        manifest,
        "heldout/multi-file",
        1,
        status="parked",
        grader_accepted=True,
        unattended_completed=False,
        intervention=True,
        wall_seconds=75,
        trajectory={"model_steps": 4, "tool_dispatches": 7, "tool_errors": None,
                    "repeated_reads": None, "recovery_attempts": None, "compactions": None},
        usage={"input_tokens": None, "output_tokens": None, "cost": None},
    )
    report = cohort.aggregate(manifest, [record])
    assert report["grader_acceptance_rate"] == 1
    assert report["unattended_completion_rate"] == 0
    assert report["unattended_accepted_rate"] == 0
    assert report["status_counts"]["parked"] == 1
    assert report["trajectory"]["tool_dispatches"] == 7
    assert report["trajectory"]["tool_errors"] is None
    assert report["trajectory"]["repeated_reads"] is None
    assert report["usage"]["cost"] is None


def test_gui_observation_jsonl_import_keeps_missing_fields_unknown(tmp_path):
    manifest = cohort.create_manifest([task()], pins(), attempts_per_task=1)
    value = cohort.record_attempt(
        manifest, "heldout/multi-file", 1, status="infrastructure_error",
        grader_accepted=False, unattended_completed=False, intervention=False,
        wall_seconds=0.4, trajectory=None, usage=None,
    )
    path = tmp_path / "gui-observations.jsonl"
    path.write_text(json.dumps(value) + "\n", encoding="utf-8")
    report = cohort.aggregate(manifest, cohort.load_observations(path))
    assert report["status_counts"]["infrastructure_error"] == 1
    assert report["trajectory"]["model_steps"] is None
    assert report["usage"]["cost"] is None


def test_attempt_requires_pins_and_explicit_external_grader_boolean():
    manifest = cohort.create_manifest([task()], pins(), attempts_per_task=1)
    with pytest.raises(ValueError, match="grader_accepted"):
        cohort.record_attempt(
            manifest, "heldout/multi-file", 1, status="completed",
            grader_accepted="unknown", unattended_completed=True, intervention=False,
            wall_seconds=10,
        )
    with pytest.raises(ValueError, match="source_commit"):
        cohort.create_manifest([task()], {**pins(), "source_commit": "unknown"}, attempts_per_task=1)


def test_matched_cohorts_require_identical_task_fixture_prompt_and_matrix():
    baseline = cohort.create_manifest([task()], pins(), attempts_per_task=1)
    candidate_pins = {**pins(), "source_commit": "e" * 40,
                      "source_tree_sha256": "1" * 64, "artifact_sha256": "f" * 64}
    candidate = cohort.create_manifest([task()], candidate_pins, attempts_per_task=1)
    left = cohort.record_attempt(baseline, "heldout/multi-file", 1, status="completed",
        grader_accepted=False, unattended_completed=True, intervention=False, wall_seconds=30)
    right = cohort.record_attempt(candidate, "heldout/multi-file", 1, status="completed",
        grader_accepted=True, unattended_completed=True, intervention=False, wall_seconds=20)
    result = cohort.compare_cohorts([
        {"label": "baseline", "manifest": baseline, "records": [left]},
        {"label": "candidate", "manifest": candidate, "records": [right]},
    ])
    assert result["track"] == "harness"
    assert result["cohorts"][0]["report"]["grader_acceptance_rate"] == 0
    assert result["cohorts"][1]["report"]["grader_acceptance_rate"] == 1

    altered = json.loads(json.dumps(candidate))
    altered["tasks"][0]["prompt_sha256"] = "1" * 64
    altered_record = cohort.record_attempt(altered, "heldout/multi-file", 1, status="completed",
        grader_accepted=True, unattended_completed=True, intervention=False, wall_seconds=20)
    with pytest.raises(ValueError, match="identical task"):
        cohort.compare_cohorts([
            {"label": "baseline", "manifest": baseline, "records": [left]},
            {"label": "candidate", "manifest": altered, "records": [altered_record]},
        ])


def test_matched_model_requires_provider_match_and_reports_paired_outcomes():
    baseline_pins = {**pins(), "provider_identity": "provider-a"}
    candidate_pins = {**baseline_pins, "source_commit": "e" * 40,
                      "source_tree_sha256": "1" * 64, "artifact_sha256": "f" * 64}
    baseline = cohort.create_manifest([task()], baseline_pins, attempts_per_task=2)
    candidate = cohort.create_manifest([task()], candidate_pins, attempts_per_task=2)
    left = [
        cohort.record_attempt(baseline, task()["id"], 1, status="completed", grader_accepted=True,
            unattended_completed=True, intervention=False, wall_seconds=10,
            usage={"input_tokens": 100, "output_tokens": 10, "cost": None}),
        cohort.record_attempt(baseline, task()["id"], 2, status="interrupted", grader_accepted=True,
            unattended_completed=False, intervention=False, wall_seconds=12),
    ]
    right = [
        cohort.record_attempt(candidate, task()["id"], 1, status="completed", grader_accepted=True,
            unattended_completed=True, intervention=False, wall_seconds=8,
            usage={"input_tokens": None, "output_tokens": None, "cost": None}),
        cohort.record_attempt(candidate, task()["id"], 2, status="interrupted", grader_accepted=True,
            unattended_completed=False, intervention=False, wall_seconds=14),
    ]
    result = cohort.compare_cohorts([
        {"label": "baseline", "manifest": baseline, "records": left},
        {"label": "candidate", "manifest": candidate, "records": right},
    ], track="matched-model")
    assert result["comparable"] is True
    assert result["paired_outcomes"] == {
        "candidate": "candidate",
        "attempts": 2,
        "correctness": {"both_accepted": 2, "baseline_only": 0, "candidate_only": 0,
                        "both_rejected": 0, "unknown": 0},
        "unattended_completion": {"both_completed": 1, "baseline_only": 0,
                                   "candidate_only": 0, "neither_completed": 1},
        "correct_but_not_completed": {"baseline": 1, "candidate": 1},
    }
    assert result["cohorts"][0]["report"]["usage"]["input_tokens"] is None


def test_compare_cohorts_marks_incomplete_attempt_matrices_not_comparable():
    baseline = cohort.create_manifest([task()], pins(), attempts_per_task=2)
    candidate_pins = {**pins(), "source_commit": "e" * 40,
                      "source_tree_sha256": "1" * 64, "artifact_sha256": "f" * 64}
    candidate = cohort.create_manifest([task()], candidate_pins, attempts_per_task=2)
    left = cohort.record_attempt(baseline, task()["id"], 1, status="completed", grader_accepted=True,
        unattended_completed=True, intervention=False, wall_seconds=10)
    right = cohort.record_attempt(candidate, task()["id"], 1, status="completed", grader_accepted=True,
        unattended_completed=True, intervention=False, wall_seconds=8)
    result = cohort.compare_cohorts([
        {"label": "baseline", "manifest": baseline, "records": [left]},
        {"label": "candidate", "manifest": candidate, "records": [right]},
    ], track="matched-model")
    assert result["comparable"] is False
    assert result["paired_outcomes"] is None
    assert result["incomparability_reasons"] == ["incomplete attempt matrix"]


@pytest.mark.parametrize("field, value, error", [
    ("task_sha256", "1" * 64, "identical task"),
    ("fixture_sha256", "1" * 64, "identical task"),
    ("prompt_sha256", "1" * 64, "identical task"),
    ("toolchain", "different-toolchain", "same deadline and toolchain"),
    ("deadline_seconds", 900, "same deadline and toolchain"),
])
def test_matched_comparison_rejects_task_and_runtime_mismatches(field, value, error):
    baseline = cohort.create_manifest([task()], pins())
    candidate_pins = {**pins(), "source_commit": "e" * 40,
                      "source_tree_sha256": "1" * 64, "artifact_sha256": "f" * 64}
    candidate = cohort.create_manifest([task()], candidate_pins)
    if field in {"task_sha256", "fixture_sha256", "prompt_sha256"}:
        candidate["tasks"][0][field] = value
    else:
        candidate["cohort"][field] = value
    left = cohort.record_attempt(baseline, task()["id"], 1, status="completed", grader_accepted=True,
        unattended_completed=True, intervention=False, wall_seconds=1)
    right = cohort.record_attempt(candidate, task()["id"], 1, status="completed", grader_accepted=True,
        unattended_completed=True, intervention=False, wall_seconds=1)
    with pytest.raises(ValueError, match=error):
        cohort.compare_cohorts([
            {"label": "baseline", "manifest": baseline, "records": [left]},
            {"label": "candidate", "manifest": candidate, "records": [right]},
        ], track="matched-model")


def test_compare_cohorts_rejects_duplicate_attempts():
    manifest = cohort.create_manifest([task()], pins())
    record = cohort.record_attempt(manifest, task()["id"], 1, status="completed", grader_accepted=True,
        unattended_completed=True, intervention=False, wall_seconds=1)
    with pytest.raises(ValueError, match="unexpected or duplicate attempt"):
        cohort.compare_cohorts([
            {"label": "baseline", "manifest": manifest, "records": [record, record]},
            {"label": "candidate", "manifest": manifest, "records": [record]},
        ], track="matched-model")


def test_matched_model_rejects_provider_identity_mismatch_when_supplied():
    first_pins = {**pins(), "provider_identity": "provider-a"}
    second_pins = {**pins(), "provider_identity": "provider-b", "source_commit": "e" * 40,
                   "source_tree_sha256": "1" * 64, "artifact_sha256": "f" * 64}
    first = cohort.create_manifest([task()], first_pins)
    second = cohort.create_manifest([task()], second_pins)
    records = [cohort.record_attempt(manifest, task()["id"], 1, status="completed",
        grader_accepted=True, unattended_completed=True, intervention=False, wall_seconds=1)
        for manifest in (first, second)]
    with pytest.raises(ValueError, match="provider identity"):
        cohort.compare_cohorts([
            {"label": "first", "manifest": first, "records": [records[0]]},
            {"label": "second", "manifest": second, "records": [records[1]]},
        ], track="matched-model")
    product = cohort.compare_cohorts([
        {"label": "first", "manifest": first, "records": [records[0]]},
        {"label": "second", "manifest": second, "records": [records[1]]},
    ], track="native-product")
    assert product["comparable"] is True
    assert product["configuration_differences"] == [{
        "field": "provider_identity",
        "values": [{"cohort": "first", "value": "provider-a"},
                   {"cohort": "second", "value": "provider-b"}],
    }]


def test_compare_cohorts_cli_accepts_new_track_names_and_writes_paired_report(tmp_path):
    manifests, records = [], []
    for commit in ("a", "e"):
        cohort_pins = {**pins(), "source_commit": commit * 40,
                       "source_tree_sha256": ("c" if commit == "a" else "1") * 64,
                       "artifact_sha256": ("d" if commit == "a" else "f") * 64}
        manifest = cohort.create_manifest([task()], cohort_pins)
        record = cohort.record_attempt(manifest, task()["id"], 1, status="completed",
            grader_accepted=True, unattended_completed=True, intervention=False, wall_seconds=2)
        manifests.append(manifest)
        records.append(record)
        index = len(manifests)
        (tmp_path / f"manifest-{index}.json").write_text(json.dumps(manifest), encoding="utf-8")
        (tmp_path / f"records-{index}.jsonl").write_text(json.dumps(record) + "\n", encoding="utf-8")
    index_path = tmp_path / "cohorts.json"
    index_path.write_text(json.dumps([
        {"label": "baseline", "manifest": "manifest-1.json", "predictions": "records-1.jsonl"},
        {"label": "candidate", "manifest": "manifest-2.json", "predictions": "records-2.jsonl"},
    ]), encoding="utf-8")
    report_path = tmp_path / "report.json"
    command = [sys.executable, str(ROOT / "benchmark.py"), "compare-cohorts", "--cohorts",
               str(index_path), "--track", "matched-model", "--report", str(report_path)]
    completed = subprocess.run(command, cwd=ROOT.parents[1], capture_output=True, text=True, check=False)
    assert completed.returncode == 0, completed.stderr
    report = json.loads(report_path.read_text(encoding="utf-8"))
    assert report["track"] == "matched-model"
    assert report["paired_outcomes"]["correctness"]["both_accepted"] == 1


def test_rpc_record_keeps_partial_trajectory_and_usage_unknown():
    manifest = cohort.create_manifest([task()], pins(), attempts_per_task=1)
    record = cohort.record_ru_attempt(
        manifest, "heldout/multi-file", 1,
        {"status": "completed", "run_id": "run_1", "run_status": "completed",
         "provider_model": "model-x", "provider_protocol": "responses", "tool_profile": "coding_v2",
         "elapsed_seconds": 12.5, "events_complete": False, "usage_complete": False,
         "tool_metrics": {"complete": False, "observed_model_steps": 3,
                          "observed_tool_dispatches": 4, "observed_tool_receipts": 2,
                          "observed_compactions": None},
         "token_usage": {"input_tokens": 8, "output_tokens": 2}},
        {"status": "passed", "reward": 1, "groups": []}, base_commit="b" * 40,
    )
    assert record["trajectory"]["model_steps"] is None
    assert record["trajectory"]["compactions"] is None
    assert record["usage"]["input_tokens"] is None
    assert record["grader_accepted"] is True
    with pytest.raises(ValueError, match="pinned cohort"):
        cohort.record_ru_attempt(
            manifest, "heldout/multi-file", 1,
            {"status": "completed", "provider_model": "other", "provider_protocol": "responses",
             "tool_profile": "coding_v2", "elapsed_seconds": 1},
            {"reward": 1}, base_commit="b" * 40,
        )


def test_unknown_grader_outcome_does_not_become_a_failed_attempt():
    manifest = cohort.create_manifest([task()], pins(), attempts_per_task=1)
    record = cohort.record_attempt(manifest, "heldout/multi-file", 1, status="infrastructure_error",
        grader_accepted=None, unattended_completed=False, intervention=False, wall_seconds=0.5)
    report = cohort.aggregate(manifest, [record])
    assert report["grader_evidence_complete"] is False
    assert report["grader_acceptance_rate"] is None
    assert report["observed_grader_acceptance_rate"] is None
    assert report["status_counts"]["infrastructure_error"] == 1


def test_skill_expectations_never_become_observed_selection():
    skill_task = {**task(), "skill_scenario": "implicit", "expected_skill_selection": "required"}
    manifest = cohort.create_manifest([skill_task], pins(), attempts_per_task=1)
    record = cohort.record_attempt(manifest, "heldout/multi-file", 1, status="completed",
        grader_accepted=True, unattended_completed=True, intervention=False, wall_seconds=20)
    assert manifest["tasks"][0]["expected_skill_selection"] == "required"
    assert record["skill_eval"]["observed_skill_selection"] == "unknown"
