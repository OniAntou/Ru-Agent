import importlib
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(os.environ["BENCHMARK_CANDIDATE_ROOT"])))
planner = importlib.import_module("src.planner")
steps = importlib.import_module("src.steps")
Step = steps.Step
groups = []
def check(name, test):
    try:
        assert test()
        groups.append({"name": name, "passed": True, "checks": 1, "failures": []})
    except Exception as error:
        groups.append({"name": name, "passed": False, "checks": 1, "failures": [type(error).__name__ + ": " + str(error)]})
def raises_value_error(call):
    try:
        call()
        return False
    except ValueError:
        return True
check("stable_dependency_order", lambda: [item.id for item in planner.order_steps([Step("c", ("a",)), Step("a"), Step("b")])] == ["a", "c", "b"])
check("unknown_dependency_rejected", lambda: raises_value_error(lambda: planner.order_steps([Step("a", ("missing",))])))
check("cycle_rejected", lambda: raises_value_error(lambda: planner.order_steps([Step("a", ("b",)), Step("b", ("a",))])))
check("duplicate_id_rejected", lambda: raises_value_error(lambda: planner.order_steps([Step("a"), Step("a")])))
def input_unchanged():
    source = [Step("b", ("a",)), Step("a")]
    before = list(source)
    result = planner.order_steps(source)
    return source == before and source[0].dependencies == ("a",) and result is not source
check("input_not_mutated", input_unchanged)
check("empty_plan", lambda: planner.order_steps([]) == [])
print(json.dumps({"groups": groups}))
sys.exit(0 if all(group["passed"] for group in groups) else 1)
