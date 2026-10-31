import importlib
import json
import os
import sys

sys.path.insert(0, os.environ["BENCHMARK_CANDIDATE_ROOT"])
m = importlib.import_module("src.slug")
cases = [
    ("case_fold", lambda: m.slugify("Hello") == "hello"),
    ("collapse_separators", lambda: m.slugify("A---B") == "a-b"),
    ("trim_edges", lambda: m.slugify("--A--") == "a"),
    ("ascii_boundary", lambda: m.slugify("Crème") == "cr-me"),
    ("punctuation", lambda: m.slugify("A_b.C") == "a-b-c"),
    ("spaces", lambda: m.slugify("x  2") == "x-2"),
    ("function_available", lambda: callable(m.slugify)),
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
