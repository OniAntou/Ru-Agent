import { createCoalescer } from "../candidate/src/coalescer.ts";

const groups = [];
async function check(name, fn) {
  try { await fn(); groups.push({ name, passed: true, checks: 1, failures: [] }); }
  catch (error) { groups.push({ name, passed: false, checks: 1, failures: [error?.name ?? "Error"] }); }
}
function assert(value) { if (!value) throw new Error("assertion failed"); }
function deferred() { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; }

await check("shared_inflight", async () => {
  let calls = 0; const gate = deferred(); const queue = createCoalescer(() => { calls++; return gate.promise; });
  const first = queue.run("a"); const second = queue.run("a");
  await Promise.resolve(); assert(calls === 1);
  gate.resolve(7); assert(await first === 7 && await second === 7); assert(calls === 1);
});
await check("retry_after_rejection", async () => {
  let calls = 0; const queue = createCoalescer(() => { calls++; return calls === 1 ? Promise.reject(new Error("fail")) : 9; });
  try { await queue.run("a"); } catch {}
  assert(await queue.run("a") === 9 && calls === 2);
});
await check("clear_generation", async () => {
  const old = deferred(); const fresh = deferred(); let calls = 0;
  const queue = createCoalescer(() => (++calls === 1 ? old.promise : fresh.promise));
  const first = queue.run("a"); queue.clear("a"); const second = queue.run("a");
  await Promise.resolve(); assert(calls === 2 && queue.size() === 1);
  old.resolve("old"); assert(await first === "old"); await Promise.resolve(); assert(queue.size() === 1);
  fresh.resolve("new"); assert(await second === "new"); await Promise.resolve(); assert(queue.size() === 0);
});
await check("abort_isolated", async () => {
  const gate = deferred(); let calls = 0; const queue = createCoalescer(() => { calls++; return gate.promise; });
  const controller = new AbortController(); const cancelled = queue.run("a", controller.signal); const other = queue.run("a");
  await Promise.resolve(); assert(calls === 1);
  controller.abort(); let aborted = false;
  try { await cancelled; } catch (error) { aborted = error.name === "AbortError"; }
  gate.resolve(4); assert(aborted && await other === 4);
});
await check("pre_aborted", async () => {
  const controller = new AbortController(); controller.abort(); const queue = createCoalescer(async () => 1);
  try { await queue.run("a", controller.signal); } catch (error) { assert(error.name === "AbortError"); return; }
  throw new Error("expected abort");
});
await check("settled_cleanup", async () => {
  const gate = deferred(); const queue = createCoalescer(() => gate.promise); assert(queue.size() === 0);
  const pending = queue.run("a"); await Promise.resolve(); assert(queue.size() === 1);
  gate.resolve(2); assert(await pending === 2); await Promise.resolve(); assert(queue.size() === 0);
});
await check("key_identity", async () => {
  let calls = 0; const queue = createCoalescer(async () => ++calls);
  assert(await queue.run("a") === 1); assert(await queue.run("a") === 2);
});
await check("loader_receives_key", async () => {
  const queue = createCoalescer(async key => key.toUpperCase()); assert(await queue.run("a") === "A");
});
console.log(JSON.stringify({ groups }));
if (groups.some(group => !group.passed)) process.exitCode = 1;
