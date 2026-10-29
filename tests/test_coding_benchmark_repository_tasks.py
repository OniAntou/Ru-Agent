import importlib.util
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(ROOT))
spec = importlib.util.spec_from_file_location("ru_coding_repository_benchmark", ROOT / "benchmark.py")
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)
task_io = sys.modules["task_io"]


def test_repository_suite_is_opt_in_and_has_three_independent_multifile_tasks():
    repository = benchmark.tasks("repository")
    included_by_default = benchmark.tasks("terminal") + benchmark.tasks("swe") + benchmark.tasks("everyday")
    assert len(repository) == 3
    assert {task.language for task in repository} == {"csharp", "python", "typescript"}
    assert not ({task.id for task in repository} & {task.id for task in included_by_default})
    assert all(len(task.editable_globs) >= 2 for task in repository)


def test_repository_seed_fails_reference_passes_and_type_check_is_never_faked():
    for task in benchmark.tasks("repository"):
        seed = benchmark.grade(task, task.seed)
        assert seed["status"] in {"failed", "unavailable"}
        if task.language == "typescript" and not __import__("shutil").which("tsc"):
            assert seed["status"] == "unavailable"
            assert seed["error"] == "typescript_compiler_unavailable"
            continue
        assert seed["status"] == "failed"
        with __import__("tempfile").TemporaryDirectory(prefix="repository-oracle-") as temporary:
            workspace = Path(temporary) / "workspace"
            import shutil
            shutil.copytree(task.seed, workspace)
            benchmark.apply_reference(task, workspace)
            reference = benchmark.grade(task, workspace)
        assert reference["status"] == "passed", (task.id, reference)
        assert all(group["passed"] for group in reference["groups"])


def test_repository_path_admission_allows_declared_project_files_and_rejects_grader_edits(tmp_path):
    task = next(task for task in benchmark.tasks("repository") if task.language == "python")
    trial = benchmark.prepare(task, tmp_path / "trial")
    workspace = Path(trial["workspace"])
    project_path = next(pattern for pattern in task.editable_globs if pattern.endswith(".toml"))
    project = workspace / project_path
    project.write_text(project.read_text(encoding="utf-8") + "\n# authorized project edit\n", encoding="utf-8")
    task_io.check_integrity(task, workspace)
    patch = tmp_path / "project.patch"
    benchmark.export_patch(task, workspace, patch)
    assert "pyproject.toml" in patch.read_text(encoding="utf-8")
    admitted = benchmark.grade_patch(task, patch, tmp_path / "evaluation")
    assert admitted["status"] == "failed"  # The task solution is absent; admission succeeded.

    forged_patch = tmp_path / "grader.patch"
    forged_patch.write_text(
        "diff --git a/tests/grade.py b/tests/grade.py\n--- a/tests/grade.py\n+++ b/tests/grade.py\n"
        "@@ -1 +1 @@\n-# grader\n+# forged\n", encoding="utf-8")
    with pytest.raises(ValueError, match="outside allowed sources"):
        benchmark.grade_patch(task, forged_patch, tmp_path / "rejected")
    (workspace / "tests").mkdir()
    (workspace / "tests" / "grade.py").write_text("print('forged')", encoding="utf-8")
    with pytest.raises(ValueError, match="unexpected"):
        task_io.check_integrity(task, workspace)


def test_repository_linked_candidate_entries_are_rejected(tmp_path):
    task = next(task for task in benchmark.tasks("repository") if task.language == "python")
    trial = benchmark.prepare(task, tmp_path / "trial")
    workspace = Path(trial["workspace"])
    (workspace / "src" / "linked.py").symlink_to(task.seed / "src" / "service.py")
    with pytest.raises(ValueError, match="linked fixture"):
        task_io.check_integrity(task, workspace)
