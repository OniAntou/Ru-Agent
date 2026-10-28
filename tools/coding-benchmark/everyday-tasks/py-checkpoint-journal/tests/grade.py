import importlib
import json
import os
import sys
from pathlib import Path

root = Path(os.environ["BENCHMARK_CANDIDATE_ROOT"])
sys.path.insert(0, str(root))
Journal = importlib.import_module("src.journal").Journal
restore = importlib.import_module("src.checkpoint").restore

def event(event_id, sequence, payload=None):
    return {"event_id": event_id, "sequence": sequence, "payload": payload}

def valid_sequence():
    journal = Journal()
    if journal.append(event("a", 1)) is not True or journal.append(event("b", 2)) is not True:
        return False
    try:
        Journal().append(event("bool", True))
    except ValueError:
        return True
    return False

def invalid_is_atomic():
    journal = Journal()
    journal.append(event("a", 1))
    before = journal.snapshot()
    try:
        journal.append(event("gap", 3))
    except ValueError:
        return journal.snapshot() == before
    return False

def duplicate_delivery():
    journal = Journal()
    value = event("a", 1, {"x": [1]})
    journal.append(value)
    return journal.append(event("a", 1, {"x": [1]})) is False and len(journal.snapshot()["events"]) == 1

def copied_event():
    journal = Journal()
    value = event("a", 1, {"x": [1]})
    journal.append(value)
    value["payload"]["x"].append(2)
    return journal.snapshot()["events"][0]["payload"]["x"] == [1]

def restored_order_and_copy():
    journal = Journal()
    snapshot = {"version": 1, "events": [event("a", 1, {"v": 1}), event("b", 2)]}
    restore(journal, snapshot)
    snapshot["events"][0]["payload"]["v"] = 9
    return journal.snapshot()["events"][0]["payload"]["v"] == 1

def invalid_restore_is_atomic():
    journal = Journal()
    journal.append(event("live", 1))
    before = journal.snapshot()
    try:
        restore(journal, {"version": 1, "events": [event("bad", 2)]})
    except ValueError:
        return journal.snapshot() == before
    return False

def version_and_shape():
    journal = Journal()
    try:
        restore(journal, {"version": 2, "events": []})
    except ValueError:
        return True
    return False

cases = [("sequence_validation", valid_sequence), ("duplicate_delivery", duplicate_delivery),
         ("snapshot_copy", copied_event), ("restore_order", restored_order_and_copy),
         ("atomic_restore", invalid_restore_is_atomic), ("format_version", version_and_shape),
         ("empty_checkpoint", lambda: Journal().snapshot() == {"version": 1, "events": []}),
         ("event_identity", lambda: Journal().append(event("id", 1)) is True)]
groups = []
for name, check in cases:
    try:
        assert check()
        groups.append({"name": name, "passed": True, "checks": 1, "failures": []})
    except Exception as error:
        groups.append({"name": name, "passed": False, "checks": 1, "failures": [type(error).__name__]})
print(json.dumps({"groups": groups}))
sys.exit(0 if all(group["passed"] for group in groups) else 1)
