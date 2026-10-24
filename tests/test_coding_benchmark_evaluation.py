import importlib.util
import json
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(ROOT))
spec = importlib.util.spec_from_file_location("ru_coding_evaluation", ROOT / "evaluation.py")
evaluation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evaluation)


def manifest():
    return {
        "schema_version": 1,
        "cohort": {"agent": "Ru", "agent_version": "1.2.3", "model": "model-x",
                   "effort": "high", "protocol": "responses", "toolchain": "dotnet-10",
                   "deadline_seconds": 1200},
        "attempts_per_task": 2,
        "tasks": [{"task_id": "heldout-multi-file", "task_sha256": "a" * 64}],
    }


def record(attempt_number, **changes):
    value = {**manifest()["cohort"], "task_id": "heldout-multi-file", "task_sha256": "a" * 64,
             "attempt": attempt_number, "status": "completed", "correct": True, "intervention": False,
             "wall_seconds": attempt_number * 10, "model_attempts": 2, "tool_attempts": 9,
             "input_tokens": None, "output_tokens": None}
    return {**value, **changes}


def test_comparison_reports_autonomy_separately_and_keeps_unknown_usage_unknown():
    records = [record(1), record(2, status="timeout", correct=False, intervention=True)]
    result = evaluation.compare(manifest(), records)
    assert result["complete"] is True
    assert result["correctness_rate"] == .5
    assert result["observed_correctness_rate"] == .5
    assert result["autonomous_completion_rate"] == .5
    assert result["status_counts"]["timeout"] == 1
    assert result["interventions"] == 1
    assert result["latency_seconds"] == {"p50": 15, "p95": 19.5}
    assert result["usage"]["model_attempts"] == 4
    assert result["usage"]["input_tokens"] is None
    assert result["usage_complete"]["input_tokens"] is False


def test_incomplete_matrix_is_explicit_and_autonomy_uses_expected_denominator():
    result = evaluation.compare(manifest(), [record(1)])
    assert result["complete"] is False
    assert result["missing_trials"] == [{"task_id": "heldout-multi-file", "attempt": 2}]
    assert result["correctness_rate"] == .5
    assert result["observed_correctness_rate"] == 1
    assert result["autonomous_completion_rate"] == .5


@pytest.mark.parametrize("change, message", [
    ({"model": "other"}, "incompatible cohort"),
    ({"task_sha256": "b" * 64}, "fingerprint mismatch"),
    ({"attempt": 3}, "unexpected or duplicate"),
    ({"intervention": 0}, "intervention must be boolean"),
    ({"wall_seconds": float("nan")}, "finite and nonnegative"),
])
def test_comparison_rejects_unfair_or_invalid_trials(change, message):
    with pytest.raises(ValueError, match=message):
        evaluation.compare(manifest(), [record(1, **change)])


def test_manifest_validation_requires_complete_pins_and_unique_fingerprinted_tasks(tmp_path):
    value = manifest()
    path = tmp_path / "manifest.json"
    path.write_text(json.dumps(value), encoding="utf-8")
    assert evaluation.load_manifest(path) == value
    value["cohort"].pop("protocol")
    path.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(ValueError, match="must pin"):
        evaluation.load_manifest(path)


def test_manifest_deadline_missing_is_a_validation_error(tmp_path):
    value = manifest()
    del value["cohort"]["deadline_seconds"]
    path = tmp_path / "manifest.json"
    path.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(ValueError, match="deadline_seconds"):
        evaluation.load_manifest(path)


def test_private_task_store_digest_excludes_reference_solution_and_detects_drift(tmp_path):
    task = tmp_path / "private" / "heldout-multi-file"
    (task / "repository" / "src").mkdir(parents=True)
    (task / "tests").mkdir()
    (task / "solution" / "src").mkdir(parents=True)
    (task / "issue.md").write_text("Change three files", encoding="utf-8")
    (task / "repository" / "src" / "a.py").write_text("starter", encoding="utf-8")
    (task / "tests" / "grade.py").write_text("assert True", encoding="utf-8")
    (task / "solution" / "src" / "a.py").write_text("answer", encoding="utf-8")
    value = manifest()
    value["tasks"][0]["task_sha256"] = evaluation.task_definition_fingerprint(task)
    path = tmp_path / "manifest.json"
    path.write_text(json.dumps(value), encoding="utf-8")
    assert evaluation.load_manifest(path, tmp_path / "private") == value
    (task / "tests" / "grade.py").write_text("assert False", encoding="utf-8")
    with pytest.raises(ValueError, match="fingerprint mismatch"):
        evaluation.load_manifest(path, tmp_path / "private")


def test_compare_cohorts_enforces_harness_and_labels_product_track():
    base = manifest()
    other = json.loads(json.dumps(base))
    other["cohort"]["agent"] = "OtherAgent"
    other["cohort"]["model"] = "model-y"
    entries = [{"label": "Ru", "manifest": base, "records": [record(1), record(2)]},
               {"label": "Other", "manifest": other,
                "records": [record(1, agent="OtherAgent", model="model-y"),
                            record(2, agent="OtherAgent", model="model-y")]}]
    with pytest.raises(ValueError, match="matched model"):
        evaluation.compare_cohorts(entries, "harness")
    report = evaluation.compare_cohorts(entries, "product")
    assert report["track"] == "product"
    assert "model or protocol may differ" in report["comparability"]


def test_compare_cohorts_rejects_different_deadline_or_task_matrix():
    base = manifest()
    other = json.loads(json.dumps(base))
    other["cohort"]["deadline_seconds"] += 1
    entries = [{"label": "a", "manifest": base, "records": []},
               {"label": "b", "manifest": other, "records": []}]
    with pytest.raises(ValueError, match="same deadline"):
        evaluation.compare_cohorts(entries, "product")
    other = json.loads(json.dumps(base))
    other["tasks"][0]["task_sha256"] = "b" * 64
    entries[1]["manifest"] = other
    with pytest.raises(ValueError, match="identical task hashes"):
        evaluation.compare_cohorts(entries, "product")


def test_missing_usage_fields_remain_unknown_and_bool_attempt_is_rejected():
    value = record(1)
    del value["input_tokens"]
    result = evaluation.compare(manifest(), [value])
    assert result["usage"]["input_tokens"] is None
    with pytest.raises(ValueError, match="unexpected or duplicate"):
        evaluation.compare(manifest(), [record(True)])


def test_jsonl_prediction_import_preserves_null_usage(tmp_path):
    path = tmp_path / "predictions.jsonl"
    path.write_text(json.dumps(record(1)) + "\n", encoding="utf-8")
    assert evaluation.load_records(path)[0]["input_tokens"] is None


@pytest.mark.parametrize("value", [None, [], {"status": {}}, {"task_id": [], "attempt": 1}])
def test_malformed_prediction_records_produce_named_validation_errors(value):
    with pytest.raises(ValueError):
        evaluation.compare(manifest(), [value])
