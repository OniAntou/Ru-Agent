import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const candidateRoot = process.env.BENCHMARK_CANDIDATE_ROOT ?? "/app";
const { TransactionStore } = await import(pathToFileURL(resolve(candidateRoot, "src/store.ts")).href);
type Mutation = { op: "set"; key: string; value: string } | { op: "delete"; key: string };
type Group = { name: string; checks: number; failures: string[] };

class Checks {
  result: Group;
  constructor(name: string) { this.result = { name, checks: 0, failures: [] }; }
  that(ok: boolean, message: string): void {
    this.result.checks++;
    if (!ok && this.result.failures.length < 8) this.result.failures.push(message);
  }
}

async function orderedBatch(c: Checks): Promise<void> {
  const store = new TransactionStore({ a: "10", b: "0", c: "0" });
  const result = store.transact("route", 0, [
    { op: "set", key: "a", value: "3" }, { op: "set", key: "b", value: "7" },
    { op: "delete", key: "b" }, { op: "set", key: "c", value: "5" },
  ]);
  c.that(result.code === "ok" && result.version === 1, "ordered batch commits once");
  c.that(result.snapshot.values.a === "3" && !("b" in result.snapshot.values) && result.snapshot.values.c === "5", "later operations observe earlier operations");
  c.that(store.snapshot().version === 1, "batch advances one version");
}

async function atomicRejection(c: Checks): Promise<void> {
  const store = new TransactionStore({ stable: "yes" });
  const before = store.snapshot();
  const invalidBodies: unknown[] = [[], [{ op: "set", key: "", value: "x" }],
    [{ op: "set", key: "ok", value: "x" }, { op: "bogus", key: "bad" }],
    [{ op: "set", key: "ok", value: 2 }]];
  for (let index = 0; index < invalidBodies.length; index++) {
    const result = store.transact(`invalid-${index}`, 0, invalidBodies[index] as Mutation[]);
    c.that(result.code === "invalid", `invalid batch ${index} rejected`);
    c.that(store.snapshot().version === before.version && store.snapshot().values.stable === "yes", `invalid batch ${index} is atomic`);
  }
  c.that(store.transact("invalid-0", 0, [{ op: "set", key: "later", value: "ok" }]).code === "ok", "rejected ID was not reserved");
}

async function replayBinding(c: Checks): Promise<void> {
  const store = new TransactionStore({ left: "4", right: "0" });
  const body = [{ op: "set", key: "left", value: "3" }, { op: "set", key: "right", value: "1" }] as const;
  const first = store.transact("same", 0, body);
  const replay = store.transact("same", -1, body.map(item => ({ ...item })));
  c.that(first.code === "ok" && replay.code === "ok", "same ordered body replays despite stale version");
  c.that(replay.version === first.version && replay.snapshot.values.left === "3", "replay returns original committed snapshot");
  c.that(store.transact("same", 1, [...body].reverse()).code === "idempotency_conflict", "request identity includes operation order");
  c.that(store.transact("same", 1, [{ op: "set", key: "left", value: "2" }]).code === "idempotency_conflict", "request identity includes operation values");
}

async function versionConflict(c: Checks): Promise<void> {
  const store = new TransactionStore({ n: "0" });
  store.transact("v1", 0, [{ op: "set", key: "n", value: "1" }]);
  const stale = store.transact("stale", 0, [{ op: "set", key: "n", value: "2" }]);
  c.that(stale.code === "version_conflict", "stale expected version rejected");
  c.that(stale.version === 1 && stale.snapshot.values.n === "1", "conflict returns current snapshot");
  c.that(store.transact("bad-version", 1.5, [{ op: "set", key: "n", value: "3" }]).code === "version_conflict", "non-integer version rejected");
}

async function generatedBatches(c: Checks): Promise<void> {
  const store = new TransactionStore();
  const model: Record<string, string> = {};
  for (let batch = 0; batch < 24; batch++) {
    const operations: Mutation[] = [];
    for (let item = 0; item < 4; item++) {
      const key = `k${(batch * 7 + item * 3) % 11}`;
      if ((batch + item) % 5 === 0) {
        operations.push({ op: "delete", key });
        delete model[key];
      } else {
        const value = `v${batch}:${item}`;
        operations.push({ op: "set", key, value });
        model[key] = value;
      }
    }
    const result = store.transact(`batch-${batch}`, batch, operations);
    c.that(result.code === "ok" && result.version === batch + 1, `generated batch ${batch} commits`);
    c.that(JSON.stringify(Object.keys(result.snapshot.values).sort().map(key => [key, result.snapshot.values[key]]))
      === JSON.stringify(Object.keys(model).sort().map(key => [key, model[key]])), `generated state ${batch} matches ordered model`);
  }
}

async function snapshotIsolation(c: Checks): Promise<void> {
  const store = new TransactionStore({ item: "original" });
  const old = store.snapshot();
  let rejected = false;
  try { (old.values as Record<string, string>).item = "tampered"; } catch { rejected = true; }
  c.that(rejected && old.values.item === "original", "snapshot values are immutable");
  store.transact("update", 0, [{ op: "set", key: "item", value: "new" }]);
  c.that(old.version === 0 && old.values.item === "original", "prior snapshot remains isolated from writes");
  c.that(Object.isFrozen(old) && Object.isFrozen(old.values), "snapshot wrapper and values are frozen");
}

async function concurrentLinearizability(c: Checks): Promise<void> {
  const store = new TransactionStore({ count: "0" });
  const contenders = Array.from({ length: 32 }, (_, index) => Promise.resolve().then(() =>
    store.transact(`writer-${index}`, 0, [{ op: "set", key: "count", value: String(index + 1) }])));
  const results = await Promise.all(contenders);
  c.that(results.filter(result => result.code === "ok").length === 1, "one same-version writer commits");
  c.that(results.filter(result => result.code === "version_conflict").length === 31, "other writers observe conflict");
  c.that(store.snapshot().version === 1 && store.snapshot().values.count !== "0", "final state is one committed transaction");
}

const cases: Array<[string, (c: Checks) => Promise<void>]> = [
  ["ordered-batch", orderedBatch], ["atomic-rejection", atomicRejection], ["replay-binding", replayBinding],
  ["version-conflict", versionConflict], ["generated-batches", generatedBatches],
  ["snapshot-isolation", snapshotIsolation], ["concurrent-linearizability", concurrentLinearizability],
];
const groups = [];
for (const [name, test] of cases) {
  const checks = new Checks(name);
  try { await test(checks); } catch (error) { checks.result.failures.push(`grader case raised ${error instanceof Error ? error.name : "unknown"}`); }
  groups.push({ ...checks.result, passed: checks.result.failures.length === 0 });
}
console.log(JSON.stringify({ groups }));
if (groups.some(group => !group.passed)) process.exitCode = 1;
