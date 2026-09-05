# Dead Runtime Scaffolding Retirement Design

**Date:** 2026-09-05  
**Status:** Approved for implementation  
**Scope:** First legacy-removal wave only

## Problem

The managed C# runtime is the only production topology, but the repository still
models a one-value `RuntimeMigrationMode`, recognizes the retired Python selector
to return a special error, and exposes a `runtime-live` Harness mode that can only
report retirement. Compatibility-named Harness and benchmark entry points also
remain even though they exercise the current ASP.NET Core backend.

This scaffolding no longer selects behavior. Keeping it enlarges startup,
verification, tests, and documentation around an option that cannot run.

## Decision

Hard-delete the dead runtime-selection and retired-live surfaces. Do not provide
aliases, compatibility shims, fallback commands, or a replacement error code.
The Host always starts the managed runtime.

The supported Harness modes become exactly:

- `backend-deterministic` (default)
- `gui-deterministic`

Rename current tools to describe what they do:

- `scripts/aspnetcore_compatibility_harness.py` -> `scripts/ru_harness.py`
- `scripts/benchmark_aspnetcore_compat.py` -> `scripts/benchmark_backend.py`

Remove helper functions whose only consumers are compatibility-only tests. Keep
the deterministic worker server switch because the active isolated Harness uses
it internally.

## Production boundary

Remove `RuntimeMigrationMode`, its parser and special exception, the `Mode`
member of `WorkerBrokerStartupOptions`, the `RuBackend:RuntimeMigrationMode`
configuration read, and the startup parser in `RuBackend/Program.cs`.
`WorkerBrokerStartupOptions` continues validating credentials, ports, provider
bootstrap, and Broker executable inputs.

## Explicitly retained

- `WorkerV1Server`, `ManagedWorkerV1RequestRouter`, and Worker v1 protocol:
  these currently own active device, voice, VTS, speech, and observation routes.
- OmniVoice's isolated Python provider process.
- Legacy history, memory, credential, settings, and policy migrations. Removing
  them requires a separate data-compatibility decision and migration inventory.
- Historical plans/specifications. They remain dated records; current docs stop
  presenting retired commands as runnable interfaces.

## Verification

- A source guard proves the old files, symbols, CLI modes, and compatibility
  entry-point names are absent outside historical documents.
- Hosting tests compile and pass with the mode-free startup options.
- Python Harness tests pass against the renamed deterministic-only commands.
- Current documentation and `TODO.md` describe the managed-only topology.
- Run the full Python suite, full .NET test matrix, route matrix, deterministic
  backend Harness, release build, and `git diff --check` sequentially.

