import importlib.util
import sys
from pathlib import Path

import pytest

TOOL_ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(TOOL_ROOT))
spec = importlib.util.spec_from_file_location("ru_coding_benchmark", TOOL_ROOT / "benchmark.py")
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)
task_io = importlib.import_module("task_io")


def test_task_registry_has_separate_multilingual_terminal_and_swe_suites():
    terminal = benchmark.tasks("terminal")
    swe = benchmark.tasks("swe")
    assert len(terminal) == 6
    assert len(swe) == 3
    assert {task.language for task in terminal} == {"csharp", "python", "typescript"}
    assert {task.language for task in swe} == {"csharp", "python", "typescript"}
    assert all(task.fail_to_pass and task.pass_to_pass for task in swe)


def test_everyday_suite_has_two_focused_tasks_per_language():
    from collections import Counter
    everyday = benchmark.tasks("everyday")
    assert Counter(task.language for task in everyday) == {"csharp": 2, "python": 2, "typescript": 2}
    assert all(task.fail_to_pass and task.pass_to_pass for task in everyday)


def test_agent_instruction_discloses_final_workspace_constraints():
    for suite in ("terminal", "swe", "everyday"):
        for task in benchmark.tasks(suite):
            original = (task.path / ("instruction.md" if suite == "terminal" else "issue.md")).read_text(encoding="utf-8")
            assert task.instruction.startswith(original)
            assert all(pattern in task.instruction for pattern in task.editable_globs)
            assert "All other existing files must remain unchanged" in task.instruction
            assert "Remove scratch files" in task.instruction
            assert "scratch checks inside the workspace" in task.instruction


def test_prepare_hides_verifier_and_reference_and_records_git_base(tmp_path):
    import hashlib
    task = benchmark.tasks("terminal")[0]
    trial = benchmark.prepare(task, tmp_path / "trial")
    root = Path(trial["workspace"])
    assert len(trial["base_commit"]) == 40
    assert trial["instruction_sha256"] == hashlib.sha256(task.instruction.encode("utf-8")).hexdigest()
    assert (root / ".git").is_dir()
    assert not (root / "solution").exists()
    assert not (root / "tests" / "grade.py").exists()
    assert not (root / "task.toml").exists()
    with pytest.raises(ValueError, match="exists"):
        benchmark.prepare(task, tmp_path / "trial")


def test_candidate_integrity_rejects_modified_immutable_files_and_extra_files(tmp_path):
    task = benchmark.tasks("terminal")[0]
    trial = benchmark.prepare(task, tmp_path / "trial")
    root = Path(trial["workspace"])
    original = (root / "README.md").read_bytes()
    (root / "README.md").write_text("changed verifier contract", encoding="utf-8")
    with pytest.raises(ValueError, match="immutable"):
        task_io.check_integrity(task, root)
    (root / "README.md").write_bytes(original)
    (root / "outside.txt").write_text("unexpected", encoding="utf-8")
    with pytest.raises(ValueError, match="unexpected"):
        task_io.check_integrity(task, root)


def test_grader_result_requires_complete_unique_groups_with_real_checks():
    valid = {"groups": [{"name": "a", "passed": True, "checks": 2, "failures": []}]}
    assert task_io.validate_groups(valid, ("a",)) == valid["groups"]
    for invalid in (
        [],
        {"groups": [None]},
        {"groups": []},
        {"groups": valid["groups"] * 2},
        {"groups": [{"name": "a", "passed": True, "checks": 0, "failures": []}]},
        {"groups": [{"name": "a", "passed": True, "checks": 1, "failures": ["failure"]}]},
    ):
        with pytest.raises(ValueError):
            task_io.validate_groups(invalid, ("a",))


def test_swe_patch_is_applied_to_fresh_base_and_rejects_test_edits(tmp_path):
    task = benchmark.find_task("swe", "py-migration-rollback")
    trial = benchmark.prepare(task, tmp_path / "author")
    workspace = Path(trial["workspace"])
    benchmark.apply_reference(task, workspace)
    patch = tmp_path / "fix.patch"
    benchmark.export_patch(task, workspace, patch)
    assert b"\r\n" not in patch.read_bytes()
    result = benchmark.grade_patch(task, patch, tmp_path / "evaluate")
    assert result["status"] == "passed"
    assert all(result["FAIL_TO_PASS"].values()) and all(result["PASS_TO_PASS"].values())
    assert result["base_commit"] == trial["base_commit"]
    benchmark.git(workspace, "restore", "src")
    (workspace / "README.md").write_text("fake grading instructions", encoding="utf-8")
    patch.write_text(benchmark.git(workspace, "diff", "HEAD"), encoding="utf-8", newline="")
    with pytest.raises(ValueError, match="outside"):
        benchmark.grade_patch(task, patch, tmp_path / "rejected")


def test_swe_patch_round_trip_ignores_staged_interpreter_cache(tmp_path):
    task = benchmark.find_task("swe", "py-migration-rollback")
    trial = benchmark.prepare(task, tmp_path / "author")
    workspace = Path(trial["workspace"])
    benchmark.apply_reference(task, workspace)
    cache = workspace / "src" / "__pycache__" / "migrate.cpython-test.pyc"
    cache.parent.mkdir()
    cache.write_bytes(b"\x00generated interpreter cache")
    benchmark.git(workspace, "add", "--", "src/__pycache__")
    patch = tmp_path / "fix.patch"
    benchmark.export_patch(task, workspace, patch)
    result = benchmark.grade_patch(task, patch, tmp_path / "evaluate")
    assert result["status"] == "passed"
    assert "__pycache__" not in patch.read_text(encoding="utf-8")


def test_everyday_patch_round_trip_uses_the_same_integrity_checks(tmp_path):
    task = benchmark.find_task("everyday", "py-stable-dedupe")
    trial = benchmark.prepare(task, tmp_path / "author")
    workspace = Path(trial["workspace"])
    benchmark.apply_reference(task, workspace)
    patch = tmp_path / "feature.patch"
    benchmark.export_patch(task, workspace, patch)
    result = benchmark.grade_patch(task, patch, tmp_path / "evaluate")
    assert result["reward"] == 1
    assert all(result["FAIL_TO_PASS"].values()) and all(result["PASS_TO_PASS"].values())


def test_swe_export_has_real_git_base_and_optional_reference_patch(tmp_path):
    import json

    output = tmp_path / "instances.jsonl"
    result = benchmark.export_swe(output, True)
    records = [json.loads(line) for line in output.read_text(encoding="utf-8").splitlines()]
    assert result["exported"] == len(records) == 3
    assert all(len(record["base_commit"]) == 40 for record in records)
    assert all(record["patch"].startswith("diff --git") and record["test_patch"] for record in records)


def test_bounded_process_timeout_and_output_limit(tmp_path):
    from execution import run_command

    timeout = run_command([sys.executable, "-c", "import time; time.sleep(10)"], tmp_path, {}, 0.1)
    noisy = run_command([sys.executable, "-c", "print('x' * 200000)"], tmp_path, {}, 5)
    assert timeout["status"] == "timed_out"
    assert noisy["status"] == "output_limit"
    assert len(noisy["stdout"].encode("utf-8")) <= 65536


def test_unknown_task_and_live_call_without_authorization_are_rejected(tmp_path):
    with pytest.raises(ValueError, match="unknown"):
        benchmark.find_task("terminal", "../escape")
    with pytest.raises(ValueError, match="allow-live"):
        benchmark.run_live(None, tmp_path, None, False, 1)
    with pytest.raises(ValueError, match="trust-workspace"):
        benchmark.run_live(None, tmp_path, None, True, 1)


def test_private_task_root_reuses_prepare_and_trusted_grader_without_copying_oracle(tmp_path):
    import shutil
    task = next(task for task in benchmark.tasks("swe") if task.language == "python")
    private = tmp_path / "private"
    shutil.copytree(task.path, private / task.id)
    selected = benchmark.tasks("swe", private)
    assert len(selected) == 1
    trial = benchmark.prepare(selected[0], tmp_path / "trial")
    workspace = Path(trial["workspace"])
    assert not (workspace / "solution").exists()
    assert not (workspace / "tests" / "grade.py").exists()
    assert benchmark.grade(selected[0], workspace)["reward"] == 0
    benchmark.apply_reference(selected[0], workspace)
    assert benchmark.grade(selected[0], workspace)["reward"] == 1
