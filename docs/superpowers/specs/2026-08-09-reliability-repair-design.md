# Reliability repair design

**Date:** 2026-08-09
**Status:** Implemented and locally verified

## Scope

Repair the verified regressions introduced or left exposed by the current
working-tree changes without discarding unrelated edits:

1. A failed Settings backend/model load must leave persisted settings unchanged,
   and concurrent backend changes must not interleave on the mutable runner.
2. Starting llama.cpp must never terminate an unowned Ru process and must fail
   quickly if an unowned listener wins the port race after spawn.
3. A VoiceStream timeout must retain its model lock until the underlying worker
   actually finishes.
4. Repeated web-search timeouts must not create an unbounded number of worker
   threads.
5. Ru-generated UI error messages must not contain emoji in any ChatView
   presentation source.
6. Voice documentation must describe the native-speed implementation and keep
   rejected DSP proposals clearly historical.
7. Clearing structured memory must not close a store while a core write or
   retrieval is using it.

## Decisions

- Build and validate a candidate Settings object before persisting its runtime
  fields. Serialize the complete select/load/persist/rollback transition with
  a reentrant lock because `ModelRunner` stores the selected backend as mutable
  instance state. Persist backend/model/runtime configuration only after the
  requested backend successfully loads; preserve independent preferences such
  as web search when that load fails, then restore the previous runner state.
  Stage backend credentials for the load and write them only after it succeeds.
- Treat an occupied llama.cpp loopback port as an unowned process. Fail with a
  clear error instead of scanning for and killing another process. Probe with
  a bind check before validation and immediately before each spawn. After a
  spawn, require `/v1/models` to accept the child's random bearer key and reject
  a second random bearer; any missing 401/403 rejection is an immediate
  unowned-port conflict rather than a full startup timeout.
- Shield `asyncio.to_thread` tasks from timeout cancellation and track an
  actual-thread completion future. A timeout is reported promptly, but request
  cleanup and the optional model lock are deferred until that worker ends.
- Keep one bounded search executor and use bounded in-flight admission. A
  busy search returns promptly while timed-out running calls occupy their
  existing capacity until they finish. Shutdown holds that executor reference
  until its active futures finish, so a replacement pool cannot overlap it.
- Remove the known hard-coded emoji from ChatView error output and guard the
  XAML and code-behind with a focused GUI test covering emoji presentation and
  composition code points.
- Use a reentrant core memory-store lock across writes and reads so clear,
  close, and artifact deletion cannot race an in-flight store operation.

## Acceptance evidence

- Focused tests fail before each fix and pass afterward.
- Full Python regression suite, WinUI tests, and the release GUI build pass.
- Documentation makes no unsupported claim of live listening acceptance.

## Verification record

- Python: 458 passed (2026-08-09).
- WinUI xUnit: 89 passed (2026-08-09).
- Release build: `cmd /c build.bat` produced the self-contained `RuApp.exe`.
- `git diff --check` reported no whitespace errors.
