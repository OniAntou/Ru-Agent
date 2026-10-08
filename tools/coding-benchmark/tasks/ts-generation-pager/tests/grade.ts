import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

type CheckGroup = { name: string; checks: number; failures: string[] };
type Page<T> = { items: T[]; nextCursor: string | null };
const candidateRoot = process.env.BENCHMARK_CANDIDATE_ROOT ?? "/app";
const { GenerationPager } = await import(pathToFileURL(resolve(candidateRoot, "src/pager.ts")).href);

class Checks {
  group: CheckGroup;
  constructor(name: string) { this.group = { name, checks: 0, failures: [] }; }
  that(condition: boolean, message: string): void {
    this.group.checks++;
    if (!condition && this.group.failures.length < 8) this.group.failures.push(message);
  }
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

async function appendCursor(c: Checks): Promise<void> {
  const seen: Array<string | null> = [];
  const pager = new GenerationPager<number>(async cursor => {
    seen.push(cursor);
    return cursor === null ? { items: [1, 2], nextCursor: "p2" } : { items: [3], nextCursor: null };
  });
  await pager.loadNext();
  const old = pager.snapshot();
  await pager.loadNext();
  const now = pager.snapshot();
  c.that(seen.length === 2 && seen[0] === null && seen[1] === "p2", "each page receives prior cursor");
  c.that(now.items.join(",") === "1,2,3" && now.cursor === null && !now.hasMore, "pages append and final cursor closes pager");
  c.that(old.items.join(",") === "1,2" && old.cursor === "p2", "prior snapshot remains a point-in-time view");
}

async function sameGenerationDedup(c: Checks): Promise<void> {
  const gate = deferred<Page<string>>();
  let calls = 0;
  const pager = new GenerationPager<string>(_ => { calls++; return gate.promise; });
  const one = pager.loadNext();
  const two = pager.loadNext();
  c.that(one === two && calls === 0, "same generation returns the shared request before loader turn");
  await Promise.resolve();
  c.that(calls === 1 && pager.snapshot().isLoading, "one loader is active");
  gate.resolve({ items: ["áo dài"], nextCursor: null });
  await Promise.all([one, two]);
  c.that(pager.snapshot().items[0] === "áo dài", "shared result is appended once");
}

async function staleGeneration(c: Checks): Promise<void> {
  const old = deferred<Page<string>>();
  const fresh = deferred<Page<string>>();
  let calls = 0;
  const pager = new GenerationPager<string>(() => ++calls === 1 ? old.promise : fresh.promise);
  const oldRequest = pager.loadNext();
  await Promise.resolve();
  const refresh = pager.refresh();
  await Promise.resolve();
  fresh.resolve({ items: ["mới"], nextCursor: null });
  await refresh;
  old.resolve({ items: ["stale"], nextCursor: null });
  await oldRequest;
  c.that(pager.snapshot().items.join(",") === "mới", "older response cannot replace refreshed data");
  c.that(!pager.snapshot().isLoading && pager.snapshot().generation === 1, "old completion cannot clear current loading state");
}

async function failureRetry(c: Checks): Promise<void> {
  let calls = 0;
  const pager = new GenerationPager<number>(async () => {
    if (++calls === 1) throw new Error("offline");
    return { items: [9], nextCursor: null };
  });
  await pager.loadNext();
  c.that(pager.snapshot().error instanceof Error && pager.snapshot().items.length === 0, "error is visible without advancing page");
  await pager.loadNext();
  c.that(calls === 2 && pager.snapshot().items[0] === 9 && pager.snapshot().error === null, "retry reuses cursor and clears error");
}

async function completionRefresh(c: Checks): Promise<void> {
  let generation = 0;
  const pager = new GenerationPager<number>(async () => ({ items: [++generation], nextCursor: null }));
  await pager.loadNext();
  await pager.loadNext();
  c.that(generation === 1 && pager.snapshot().items.join(",") === "1", "completed pager makes later load a no-op");
  await pager.refresh();
  c.that(generation === 2 && pager.snapshot().generation === 1 && pager.snapshot().items.join(",") === "2", "refresh resets and loads a new generation");
}

async function snapshotIsolation(c: Checks): Promise<void> {
  const pager = new GenerationPager<number>(async () => ({ items: [1], nextCursor: null }));
  await pager.loadNext();
  const first = pager.snapshot();
  let immutable = false;
  try { (first.items as number[]).push(2); } catch { immutable = true; }
  c.that(pager.snapshot().items.length === 1 && pager.snapshot().items[0] === 1, "snapshot array does not alias pager state");
  c.that(immutable || first.items.length === 2, "snapshot is independent or immutable");
}

const definitions: Array<[string, (c: Checks) => Promise<void>]> = [
  ["append-cursor", appendCursor], ["same-generation-dedup", sameGenerationDedup],
  ["stale-generation", staleGeneration], ["failure-retry", failureRetry],
  ["completion-refresh", completionRefresh], ["snapshot-isolation", snapshotIsolation],
];

const groups = [];
for (const [name, run] of definitions) {
  const c = new Checks(name);
  try { await run(c); } catch (error) { c.that(false, `grader case raised ${error instanceof Error ? error.name : "unknown"}`); }
  groups.push({ ...c.group, passed: c.group.failures.length === 0 });
}
console.log(JSON.stringify({ groups }));
if (groups.some(group => !group.passed)) process.exitCode = 1;
