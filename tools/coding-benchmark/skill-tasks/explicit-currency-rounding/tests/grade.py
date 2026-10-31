import importlib
import json
import os
import sys

sys.path.insert(0, os.environ["BENCHMARK_CANDIDATE_ROOT"])
m = importlib.import_module("src.amounts")
rows = [{"unit_minor": 125, "quantity": 2}, {"unit_minor": 50, "quantity": 1}]


def rejects(action):
    try:
        action()
    except (ValueError, TypeError):
        return True
    return False


def input_unchanged():
    example = [{"unit_minor": 125, "quantity": 2}, {"unit_minor": 50, "quantity": 1}]
    before = [dict(row) for row in example]
    m.invoice_total(example)
    return example == before


cases = [
    ("exact_total", lambda: m.invoice_total(rows) == "3.00"),
    ("precision_format", lambda: m.invoice_total([{"unit_minor": 1, "quantity": 1}], 3) == "0.010"),
    (
        "reject_negative",
        lambda: rejects(lambda: m.invoice_total([{"unit_minor": 1, "quantity": -1}])),
    ),
    (
        "reject_boolean",
        lambda: rejects(lambda: m.invoice_total([{"unit_minor": True, "quantity": 1}])),
    ),
    ("precision_bounds", lambda: rejects(lambda: m.invoice_total([], 7))),
    ("input_unchanged", input_unchanged),
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
