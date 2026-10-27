import importlib.util
import sys
from pathlib import Path

import pytest

TOOL_ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(TOOL_ROOT))
spec = importlib.util.spec_from_file_location("ru_skill_benchmark", TOOL_ROOT / "benchmark.py")
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


def test_skill_suite_covers_selection_and_negative_scenarios():
    suite = benchmark.tasks("skills")
    by_scenario = {task.skill_scenario: task for task in suite}
    assert set(by_scenario) == {
        "explicit", "implicit", "negative_unrelated", "negative_manual_only", "supporting_resource"
    }
    assert by_scenario["explicit"].expected_skill_selection == "required"
    assert by_scenario["implicit"].expected_skill_selection == "required"
    assert by_scenario["supporting_resource"].expected_skill_selection == "required"
    assert by_scenario["negative_unrelated"].expected_skill_selection == "none"
    assert by_scenario["negative_manual_only"].expected_skill_selection == "none"
    assert all(task.language == "python" for task in suite)


def test_skill_suite_is_explicitly_opt_in_and_records_unknown_observation(tmp_path):
    assert "skills" not in {task.suite for task in benchmark.tasks("terminal") + benchmark.tasks("swe") + benchmark.tasks("everyday")}
    task = benchmark.find_task("skills", "explicit-currency-rounding")
    trial = benchmark.prepare(task, tmp_path / "trial")
    assert trial["skill_eval"] == {
        "scenario": "explicit",
        "expected_skill_selection": "required",
        "observed_skill_selection": "unknown",
    }
    assert trial["instruction_sha256"]
    assert (Path(trial["workspace"]) / ".agents/skills/currency-rounding/SKILL.md").is_file()
    assert not (Path(trial["workspace"]) / "tests/grade.py").exists()
    assert not (Path(trial["workspace"]) / "solution").exists()


@pytest.mark.parametrize("task_id", [
    "explicit-currency-rounding",
    "implicit-calendar-slots",
    "negative-unrelated-slug",
    "negative-manual-only-csv",
    "supporting-resource-json-summary",
])
def test_skill_task_starter_fails_and_reference_passes(tmp_path, task_id):
    task = benchmark.find_task("skills", task_id)
    trial = benchmark.prepare(task, tmp_path / task_id)
    workspace = Path(trial["workspace"])
    baseline = benchmark.grade(task, workspace)
    assert baseline["status"] == "failed"
    assert all(not passed for passed in baseline["FAIL_TO_PASS"].values())
    assert all(baseline["PASS_TO_PASS"].values())
    benchmark.apply_reference(task, workspace)
    reference = benchmark.grade(task, workspace)
    assert reference["status"] == "passed"
    assert all(reference["FAIL_TO_PASS"].values())
    assert all(reference["PASS_TO_PASS"].values())


def test_skill_eval_metadata_is_in_fixture_hash(tmp_path):
    import shutil

    task = benchmark.find_task("skills", "negative-manual-only-csv")
    assert task.skill_scenario == "negative_manual_only"
    assert task.expected_skill_selection == "none"
    copied = tmp_path / task.id
    shutil.copytree(task.path, copied)
    definition = copied / "instance.toml"
    definition.write_text(
        definition.read_text(encoding="utf-8").replace(
            'skill_scenario = "negative_manual_only"',
            'skill_scenario = "negative_unrelated"',
        ),
        encoding="utf-8",
    )
    changed = benchmark.tasks("skills", tmp_path)[0]
    assert changed.fixture_hash != task.fixture_hash
