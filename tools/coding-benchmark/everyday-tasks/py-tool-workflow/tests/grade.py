import importlib
import json
import os
import runpy
import sys
from pathlib import Path

candidate = Path(os.environ["BENCHMARK_CANDIDATE_ROOT"])
sys.path.insert(0, str(candidate))
prices = importlib.import_module("src.prices")


def report():
    return runpy.run_path(str(candidate / "src/reports/giá báo cáo.py"))["summarize"]


def uses_existing_helpers():
    total, normalize = prices.line_total, prices.normalize_currency
    try:
        prices.line_total = lambda _: 313
        prices.normalize_currency = lambda _: "CUSTOM"
        return report()([{"currency": "x", "price": 1}]) == {"CUSTOM": 313}
    finally:
        prices.line_total, prices.normalize_currency = total, normalize


def grouped_without_mutation():
    rows = [{"currency": " usd ", "price": 5, "quantity": 2}, {"currency": "vnd", "price": 7},
            {"currency": "USD", "price": -2, "quantity": 3}]
    original = [dict(row) for row in rows]
    return report()(rows) == {"USD": 4, "VND": 7} and rows == original


cases = [
    ("line_totals", lambda: all(prices.line_total(row) == expected for row, expected in [
        ({"price": 5, "quantity": 3}, 15), ({"price": 7, "quantity": 0}, 0),
        ({"price": -3, "quantity": 2}, -6), ({"price": 7}, 7)])),
    ("currency_normalization", lambda: prices.normalize_currency("  uSd\t") == "USD" and prices.normalize_currency("") == ""),
    ("report_created", uses_existing_helpers),
    ("grouped_totals", grouped_without_mutation),
    ("iterable_and_empty", lambda: report()(iter([])) == {} and list(report()(iter([
        {"currency": "b", "price": 0}, {"currency": "a", "price": 2}])).keys()) == ["B", "A"]),
    ("existing_label", lambda: prices.currency_label(" usd ") == "Currency:  usd "),
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
