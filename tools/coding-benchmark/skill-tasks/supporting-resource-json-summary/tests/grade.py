import importlib
import json
import os
import sys

sys.path.insert(0, os.environ["BENCHMARK_CANDIDATE_ROOT"])
m = importlib.import_module("src.summary")
rows = [
    {"region": " ne ", "amount_minor": 125},
    {"region": "Northeast", "amount_minor": -25},
    {"region": "s", "amount_minor": 0},
]
cases = [
    ("aliases", lambda: m.summarize(rows)["by_region"] == {"NORTH_EAST": 2, "SOUTH": 1}),
    ("region_case_and_space", lambda: "NORTH_EAST" in m.summarize(rows)["by_region"]),
    ("record_count", lambda: m.summarize(rows)["count"] == 3),
    ("minor_unit_sum", lambda: m.summarize(rows)["total_minor"] == 100),
    (
        "unknown_region",
        lambda: m.summarize([{"region": "west", "amount_minor": 4}])["by_region"] == {"WEST": 1},
    ),
    ("input_unchanged", lambda: unchanged()),
    ("empty_summary", lambda: m.summarize([]) == {"count": 0, "total_minor": 0, "by_region": {}}),
]


def unchanged():
    original = [{"region": " ne ", "amount_minor": 125}]
    before = [dict(record) for record in original]
    m.summarize(original)
    return original == before


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
