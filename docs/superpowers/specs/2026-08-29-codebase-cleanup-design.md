# Ru Codebase Cleanup and Organization

**Date:** 2026-08-29  
**Status:** Approved for implementation  
**Scope:** Remove dead source and rebuildable outputs, then make the repository
layout and ignore rules reflect the active Agent-only runtime.

## Decision summary

The cleanup keeps the existing runtime seams and domain directories intact.
The Public Host, Agent Broker, Runtime Worker, GUI, and native overlay already
have separate ownership; moving active files would create path and namespace
churn without increasing depth or locality. Organization therefore means
removing disconnected entrypoints and generated mass, and making the ignore
rules describe the disposable outputs explicitly.

The approved source deletion set is:

- `src/ru_runtime/policy/safety_patterns.py` — tracked module with no import,
  test, or runtime reference.
- `run_tests.py` — obsolete convenience runner named for an old review round;
  no repository caller exists and the supported command is `python -m pytest`.
- `scripts/probe_gemini_vision.py` — disconnected probe for the retired Gemini
  Live WebSocket flow; the active observation module uses the supported HTTP
  adapter and this script's `requests` dependency is not declared.

The approved generated-output deletion set is the exact set currently present
in the read-only cleanup inventory:

- `.pytest_cache`, `.mypy_cache`, `.ruff_cache`;
- every current `bin` and `obj` directory under `backend-dotnet/*`,
  `gui-winui3`, `gui-winui3.Tests`, and `gui-winui3.Harness`;
- every current Python `__pycache__` directory;
- `src/ru_runtime.egg-info`;
- `tools/ru_desktop_overlay/out`.

These outputs are rebuildable and are not tracked. The cleanup does not include
models, `.venv`, `omnivoice_env`, application data, logs, `.tmp`, package
caches, active voice assets, or any pre-existing dirty path.

## Organization rules

1. Keep the active source topology unchanged:
   `backend-dotnet/RuAgent.*` remains split by Core, Persistence, Hosting,
   Client, CLI, Broker, Connectors, and Protocol; `backend-dotnet/RuBackend`
   remains the Public Host; `src/ru_runtime` remains split by domain package;
   `scripts/harness` remains the deterministic Harness seam; GUI and native
   overlay remain separate projects.
2. Add explicit `.mypy_cache/` and `.ruff_cache/` ignore rules and remove the
   duplicated `.venv/` rule. Keep the existing protected-data and model rules.
3. Do not introduce a generic `common`, `shared`, or `helpers` directory and do
   not move a module solely to make the tree look shorter.
4. Do not delete historical ADRs or the current documentation spine. Inspect
   all documentation after the cleanup and update only stale references caused
   by the approved deletions.

## Safety and verification

Before deletion, confirm the manifest paths still exist, are not filesystem
links, are not Git-dirty, and that no build/test process is running. Delete only
the approved individual paths. A missing or newly dirty path is skipped and
reported rather than widened into a recursive cleanup.

After deletion:

- run repository-wide reference scans for the three removed source files;
- run cleanup-policy and documentation tests;
- run the full Python suite with `D:\LLM\.venv\Scripts\python.exe -m pytest -q`;
- run .NET test projects sequentially, then the route-contract checker;
- run `git diff --check` and regenerate the read-only cleanup inventory;
- verify that protected paths and all pre-existing dirty paths remain present.

The tracked deletions are recoverable from Git. Generated outputs are recreated
by the existing build and test commands; no user data is moved or overwritten.
