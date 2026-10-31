import importlib
import json
import os
import sys
from pathlib import Path

root = Path(os.environ["BENCHMARK_CANDIDATE_ROOT"])
sys.path.insert(0, str(root))
records = importlib.import_module("src.records")
reconcile_module = importlib.import_module("src.reconcile")

def check_headers():
    rows = records.parse_rows([" Record ID , UPDATED AT , Name ", " A-1 , 2026-01-01 , Ada "])
    return rows == [{"record_id": "A-1", "updated_at": "2026-01-01", "name": "Ada"}]

def check_blanks():
    return records.parse_rows(["", "  ", "record id,name", "x,A", ""]) == [{"record_id": "x", "name": "A"}]

def check_bad_rows():
    try:
        records.parse_rows(["id,name", "one"])
    except ValueError:
        return True
    return False

def check_missing_header():
    try:
        records.parse_rows(["", "  "])
    except ValueError:
        return True
    return False

def check_conflicts():
    rows = [{"record_id": " A ", "updated_at": "2", "value": "old"},
            {"record_id": "a", "updated_at": "10", "value": "new"},
            {"record_id": "A", "updated_at": "2", "value": "tie"}]
    # The contract compares timestamp strings, so "2" sorts after "10".
    # Assert this independently of the later-input tie rule.
    return (reconcile_module.reconcile(rows[:2])[0]["value"] == "old"
            and reconcile_module.reconcile(rows)[0]["value"] == "tie")

def check_order_and_empty():
    rows = [{"record_id": "b", "updated_at": "1"}, {"record_id": "a", "updated_at": "1"},
            {"record_id": "B", "updated_at": "2"}, {"record_id": "  ", "updated_at": "9"}]
    return [row["record_id"] for row in reconcile_module.reconcile(rows)] == ["B", "a"]

def check_iterable():
    rows = [{"record_id": "x", "updated_at": "1"}, {"record_id": " X ", "updated_at": "2"}]
    result = reconcile_module.reconcile(iter(rows))
    return len(result) == 1 and result[0]["updated_at"] == "2"

def check_unchanged():
    rows = [{"record_id": " x ", "updated_at": "1"}]
    before = [dict(row) for row in rows]
    result = reconcile_module.reconcile(rows)
    result[0]["record_id"] = "changed"
    return rows == before

cases = [
    ("header_normalization", check_headers), ("blank_rows", check_blanks),
    ("normalized_keys", lambda: len(reconcile_module.reconcile([
        {"record_id": " X ", "updated_at": "1"}, {"record_id": "x", "updated_at": "2"}])) == 1),
    ("conflict_policy", check_conflicts), ("stable_order", check_order_and_empty),
    ("iterable_support", check_iterable), ("input_unchanged", check_unchanged),
    ("existing_record_id", lambda: records.record_id({"record_id": "  X "}) == "  X "),
    ("row_width_validation", check_bad_rows),
    ("header_required", check_missing_header),
]
groups = []
for name, check in cases:
    try:
        assert check()
        groups.append({"name": name, "passed": True, "checks": 1, "failures": []})
    except Exception as error:
        groups.append({"name": name, "passed": False, "checks": 1, "failures": [type(error).__name__]})
print(json.dumps({"groups": groups}))
sys.exit(0 if all(group["passed"] for group in groups) else 1)
