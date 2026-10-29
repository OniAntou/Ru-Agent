"""Behavioral coverage for grader bugs found in the 2026-10-29 cohort."""
import json
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(ROOT))
import benchmark
import task_io


def reference_workspace(tmp_path, suite, task_id):
    task = benchmark.find_task(suite, task_id)
    workspace = Path(benchmark.prepare(task, tmp_path / "trial")["workspace"])
    benchmark.apply_reference(task, workspace)
    return task, workspace


def test_todo_accepts_independent_readonly_dictionary(tmp_path):
    task, workspace = reference_workspace(tmp_path, "everyday", "cs-todo-summary")
    source = workspace / "src" / "TodoBoard.cs"
    source.write_text(source.read_text().replace("return result;",
        "return new System.Collections.ObjectModel.ReadOnlyDictionary<string,int>(result);"))
    assert benchmark.grade(task, workspace)["status"] == "passed"


@pytest.mark.parametrize("mutable", [False, True])
def test_atomic_snapshot_contract_accepts_noncastable_immutable_but_rejects_mutable(tmp_path, mutable):
    task, workspace = reference_workspace(tmp_path, "terminal", "cs-atomic-ledger")
    source = workspace / "src" / "AtomicLedger.cs"
    text = source.read_text()
    if mutable:
        text = text.replace("new ReadOnlyDictionary<string, long>(new Dictionary<string, long>(_balances, StringComparer.Ordinal))",
                            "new Dictionary<string, long>(_balances, StringComparer.Ordinal)")
    else:
        text = text.replace("new ReadOnlyDictionary<string, long>(", "new SnapshotMap(")
        text += '''
sealed class SnapshotMap(IReadOnlyDictionary<string, long> values) : IReadOnlyDictionary<string, long>
{
    public long this[string key] => values[key];
    public int Count => values.Count;
    public IEnumerable<string> Keys => values.Keys;
    public IEnumerable<long> Values => values.Values;
    public bool ContainsKey(string key) => values.ContainsKey(key);
    public bool TryGetValue(string key, out long value) => values.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, long>> GetEnumerator() => values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
'''
    source.write_text(text)
    result = benchmark.grade(task, workspace)
    assert result["status"] == ("failed" if mutable else "passed")
    snapshot = next(group for group in result["groups"] if group["name"] == "snapshot_isolation")
    assert snapshot["passed"] is not mutable


def test_atomic_rejecting_valid_repeated_transfer_pairs_fails_ordered_batch(tmp_path):
    task, workspace = reference_workspace(tmp_path, "terminal", "cs-atomic-ledger")
    source = workspace / "src" / "AtomicLedger.cs"
    source.write_text(source.read_text().replace("if (transfers.Count == 0)",
        "if (transfers.Count == 0 || transfers.GroupBy(t => (t.From, t.To)).Any(g => g.Count() > 1))"))
    result = benchmark.grade(task, workspace)
    assert result["status"] == "failed"
    assert next(group for group in result["groups"] if group["name"] == "ordered_batch")["passed"] is False


def test_scheduler_exception_before_first_assertion_is_valid_failed_evidence():
    task = benchmark.find_task("terminal", "py-dependency-scheduler")
    script = """
import importlib.util, sys
spec = importlib.util.spec_from_file_location('scheduler_grader_contract', sys.argv[1])
grader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(grader)
def broken_case(group):
    raise ValueError('candidate rejected a valid input')
grader.graph_atomicity = broken_case
raise SystemExit(grader.main())
"""
    result = benchmark.run_command([sys.executable, "-c", script, str(task.path / "tests" / "grade.py")],
                                   task.seed, {"BENCHMARK_CANDIDATE_ROOT": str(task.seed)}, 30)
    assert result["status"] == "completed" and result["exit_code"] == 1
    evidence = json.loads(result["stdout"])
    task_io.validate_groups(evidence, tuple(group["name"] for group in evidence["groups"]))
    group = evidence["groups"][0]
    assert group["checks"] == 1 and not group["passed"]
    assert group["failures"] == ["grader case raised ValueError"]
