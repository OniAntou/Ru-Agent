import importlib
import json
import os
import sys

sys.path.insert(0, os.environ["BENCHMARK_CANDIDATE_ROOT"])
m = importlib.import_module("src.slots")
events = [{"start": "2026-01-01T10:30:00Z", "end": "2026-01-01T11:00:00Z"}]


def slots():
    return m.available_slots(events, "2026-01-01T10:00:00Z", "2026-01-01T12:00:00Z", 30)


def rejects(action):
    try:
        action()
    except (ValueError, TypeError):
        return True
    return False


cases = [
    (
        "conflict_filter",
        lambda: slots() == ["2026-01-01T10:00:00Z", "2026-01-01T11:00:00Z", "2026-01-01T11:30:00Z"],
    ),
    (
        "half_open_boundary",
        lambda: (
            m.available_slots([], "2026-01-01T10:00:00Z", "2026-01-01T10:30:00Z", 30)
            == ["2026-01-01T10:00:00Z"]
        ),
    ),
    ("chronological_slots", lambda: slots() == sorted(slots())),
    (
        "inclusive_window",
        lambda: (
            m.available_slots([], "2026-01-01T10:00:00Z", "2026-01-01T10:15:00Z", 15)
            == ["2026-01-01T10:00:00Z"]
        ),
    ),
    (
        "invalid_duration",
        lambda: rejects(
            lambda: m.available_slots([], "2026-01-01T10:00:00Z", "2026-01-01T12:00:00Z", 0)
        ),
    ),
    (
        "events_unchanged",
        lambda: events == [{"start": "2026-01-01T10:30:00Z", "end": "2026-01-01T11:00:00Z"}],
    ),
]
results = []
for name, check in cases:
    try:
        passed = bool(check())
        results.append(
            {
                "name": name,
                "passed": passed,
                "checks": 1,
                "failures": [] if passed else ["acceptance mismatch"],
            }
        )
    except Exception as error:
        results.append(
            {"name": name, "passed": False, "checks": 1, "failures": [type(error).__name__]}
        )
print(json.dumps({"groups": results}))
sys.exit(0 if all(group["passed"] for group in results) else 1)
