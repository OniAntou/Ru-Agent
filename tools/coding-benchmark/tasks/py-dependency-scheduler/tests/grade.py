import importlib
import json
import os
import sys
from pathlib import Path

ROOT = Path(os.environ.get("BENCHMARK_CANDIDATE_ROOT", "/app"))
sys.path.insert(0, str(ROOT))
for name in ("src.scheduler", "src.models"):
    sys.modules.pop(name, None)
Job = importlib.import_module("src.models").Job
Scheduler = importlib.import_module("src.scheduler").Scheduler


class Group:
    def __init__(self, name):
        self.name, self.checks, self.failures = name, 0, []

    def check(self, condition, label):
        self.checks += 1
        if not condition and len(self.failures) < 8:
            self.failures.append(label)


def raises_value_error(fn):
    try:
        fn()
        return False
    except ValueError:
        return True


def graph_atomicity(g):
    s = Scheduler(2)
    s.replace([Job("root"), Job("leaf", ("root",), 2)])
    s.start("root")
    before = (s.history, s.state("root"), s.state("leaf"), s.ready())
    invalid_graphs = [
        [Job("x"), Job("x")], [Job("x", ("missing",))], [Job("x", ("x",))],
        [Job("x", ("y",)), Job("y", ("x",))], [Job("x", max_attempts=True)],
        [Job("")], [Job("x", depends_on=("x",))],
    ]
    for i, graph in enumerate(invalid_graphs):
        g.check(raises_value_error(lambda graph=graph: s.replace(graph)), f"invalid graph {i} rejected")
        g.check((s.history, s.state("root"), s.state("leaf"), s.ready()) == before, f"failed replace {i} is atomic")


def deterministic_readiness(g):
    jobs = [Job("z", ("m",)), Job("m", ("a",)), Job("b"), Job("a")]
    for ordered in (jobs, list(reversed(jobs)), [jobs[i] for i in (1, 3, 0, 2)]):
        s = Scheduler(8)
        s.replace(ordered)
        g.check(s.ready() == ("a", "b"), "roots sorted regardless of input order")
        s.start("a")
        s.succeed("a")
        g.check(s.ready() == ("b", "m"), "only dependency-ready jobs returned")
        s.start("m")
        s.succeed("m")
        g.check(s.ready() == ("b", "z"), "multi-level dependency unlocks deterministically")


def parallel_capacity(g):
    s = Scheduler(2)
    s.replace([Job("c"), Job("a"), Job("b")])
    g.check(s.ready() == ("a", "b"), "ready list limited to available slots")
    g.check(s.start("b") == 1, "ready job starts")
    g.check(s.ready() == ("a",), "running job consumes one slot")
    g.check(raises_value_error(lambda: s.start("c")), "job outside current capacity cannot start")
    s.succeed("b")
    g.check(s.ready() == ("a", "c"), "completed job releases capacity")


def transition_integrity(g):
    s = Scheduler(2)
    s.replace([Job("root"), Job("child", ("root",))])
    g.check(raises_value_error(lambda: s.succeed("root")), "non-running job cannot succeed")
    s.start("root")
    s.succeed("root")
    s.start("child")
    s.succeed("child")
    expected = (("root", "pending", "running", 1), ("root", "running", "succeeded", 1),
                ("child", "pending", "running", 1), ("child", "running", "succeeded", 1))
    g.check(s.history == expected, "each actual transition recorded once with attempt")
    old = s.history
    g.check(isinstance(old, tuple), "history is read-only tuple")
    try:
        old[0] = None
        mutable = True
    except TypeError:
        mutable = False
    g.check(not mutable and s.history == expected, "history cannot be mutated by caller")
    g.check(raises_value_error(lambda: s.state("missing")), "unknown job state is rejected")
    before_replace = s.history
    g.check(raises_value_error(lambda: s.replace(None)) and s.history == before_replace, "non-iterable replacement is atomic")


def retry_budget(g):
    s = Scheduler(1)
    s.replace([Job("fragile", max_attempts=2)])
    g.check(s.retry("fragile") is False, "pending job does not accept retry")
    g.check(s.start("fragile") == 1 and s.fail("fragile") == "failed", "first failure is terminal until retry")
    g.check(s.retry("fragile") is True and s.state("fragile") == "pending", "retry reopens job within budget")
    g.check(s.start("fragile") == 2 and s.fail("fragile") == "failed", "second start consumes final attempt")
    before = s.history
    g.check(s.retry("fragile") is False and s.history == before, "exhausted retry is a no-op")
    g.check(s.ready() == (), "exhausted job never becomes ready")


def cancellation_propagation(g):
    s = Scheduler(3)
    s.replace([Job("source"), Job("active", ("source",)), Job("pending", ("source",)), Job("leaf", ("pending",))])
    s.start("source")
    s.succeed("source")
    s.start("active")
    s.start("pending")
    s.fail("pending")
    g.check(s.state("leaf") == "blocked", "failed dependency blocks pending descendants")
    g.check(s.state("active") == "running", "running descendant is not rewritten")
    g.check("leaf" not in s.ready(), "blocked work never becomes ready")
    s2 = Scheduler(2)
    s2.replace([Job("cancel"), Job("child", ("cancel",)), Job("leaf", ("child",))])
    g.check(s2.cancel("cancel") == "cancelled", "pending job can be cancelled")
    g.check(s2.state("child") == "blocked" and s2.state("leaf") == "blocked", "cancellation blocks transitively")
    before = s2.history
    g.check(raises_value_error(lambda: s2.cancel("cancel")) and s2.history == before, "terminal cancellation rejected without transition")


def main():
    specs = [("graph-atomicity", graph_atomicity), ("deterministic-readiness", deterministic_readiness),
             ("parallel-capacity", parallel_capacity), ("transition-integrity", transition_integrity),
             ("retry-budget", retry_budget), ("cancellation-propagation", cancellation_propagation)]
    results = []
    for name, fn in specs:
        group = Group(name)
        try:
            fn(group)
        except Exception as exc:
            group.check(False, f"grader case raised {type(exc).__name__}")
        results.append({"name": name, "passed": not group.failures, "checks": group.checks, "failures": group.failures})
    print(json.dumps({"groups": results}, separators=(",", ":")))
    return 0 if all(result["passed"] for result in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
