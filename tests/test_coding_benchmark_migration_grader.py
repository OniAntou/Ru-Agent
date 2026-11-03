"""The migration grader enforces behavior without prescribing an exception class."""
import importlib
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(ROOT))
benchmark = importlib.import_module("benchmark")


@pytest.mark.parametrize(
    "error_type,mutates,empty_message,silent,accepted",
    [
        ("ValueError", False, False, False, True),
        ("RuntimeError", False, False, False, True),
        ("MigrationError", False, False, False, True),
        ("ValueError", True, False, False, False),
        ("RuntimeError", True, False, False, False),
        ("RuntimeError", False, True, False, False),
        ("RuntimeError", False, False, True, False),
    ],
)
def test_future_version_requires_clear_error_and_no_mutation(
    tmp_path, error_type, mutates, empty_message, silent, accepted
):
    task = benchmark.find_task("swe", "py-migration-rollback")
    workspace = Path(benchmark.prepare(task, tmp_path / "trial")["workspace"])
    benchmark.apply_reference(task, workspace)
    source = workspace / "src" / "migrate.py"
    text = source.read_text(encoding="utf-8")
    original = next(line for line in text.splitlines() if "raise ValueError(" in line)
    message = '""' if empty_message else '"database version is newer than supported"'
    replacement = "        return []" if silent else f"        raise {error_type}({message})"
    if mutates:
        replacement = '        connection.execute("PRAGMA user_version = 1")\n' + replacement
    text = text.replace(original, replacement, 1)
    if error_type == "MigrationError":
        text += "\n\nclass MigrationError(Exception):\n    pass\n"
    source.write_text(text, encoding="utf-8")

    result = benchmark.grade(task, workspace)
    group = next(item for item in result["groups"] if item["name"] == "future_schema_version_fails_without_mutation")
    assert group["passed"] is accepted
    assert result["status"] == ("passed" if accepted else "failed")
