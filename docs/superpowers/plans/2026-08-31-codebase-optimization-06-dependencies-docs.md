# Dependencies and Documentation Finalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align dependency declarations with actual consumers and bring current docs/TODO/verification in sync with delivered code. Verify sequentially with final gates.

**Tech Stack:** Python, .NET, docs, PowerShell

## Global Constraints

Inherit [master constraints](2026-08-31-codebase-optimization.md). Phase 4, final. No active environment mutation without explicit authorization, no live device, no destructive cleanup without manifest.

---

## D — Consumer-driven dependency alignment

**Files:**
- Read: `pyproject.toml`, `src/ru_runtime/voice/omnivoice_server.py`, `src/ru_runtime/voice/*`, `src/ru_runtime/*`, `scripts/*`, `tests/*`
- Modify: `pyproject.toml` only
- Verify: `pip check`, `pip install -e .[runtime,dev]` (dry-run check only, no active env mutation without auth), `dotnet build`, `docs/operations/verification.md` if needed

**Current state (2026-08-31):**
- `runtime` extra declares `ddgs>=9.0` with no found consumer in `src/ru_runtime`, `scripts`, `tests` (grep found only `.venv` hits). The managed web path uses C# `HttpClient` + DuckDuckGo directly, not Python `ddgs`.
- `voice` extra declares `torch>=2.9,<3` while setup notes still reference `2.8.0+cu128` pin. `omnivoice_server.py` uses `torch` and is active.

- [ ] **Step 1 — Inventory.**
  ```powershell
  Select-String -Pattern "ddgs|torch" -Path "D:\LLM\pyproject.toml" | Format-Table
  Get-ChildItem -Recurse -Path "D:\LLM\src","D:\LLM\scripts","D:\LLM\tests" -Filter "*.py" | Select-String -Pattern "ddgs|torch" | Format-Table
  ```
  Record every consumer file and line. Confirm `ddgs` has zero consumers outside `.venv` and `torch` has consumers in `voice/omnivoice_server.py`.

- [ ] **Step 2 — Decision and RED.**
  For `ddgs`: remove from `runtime` extra or move to optional `dev` if a test needs it; do not keep an unused runtime dependency. For `torch`: align `pyproject.toml` to one version (keep `>=2.9,<3` as active, remove obsolete `2.8.0+cu128` pin from docs/comments). Add a failing test or `pip check` assertion that proves the old declaration was inconsistent? Use `pip check` as gate, not a new unit test. Keep `httpx`, `keyring`, `ddgs` removal explicit.

- [ ] **Step 3 — GREEN and verification.**
  Edit `pyproject.toml` to:
  - Remove `ddgs` from `runtime` (or document why kept, with consumer)
  - Keep `torch>=2.9,<3` in `voice`, ensure no `2.8` pin remains
  ```powershell
  & D:\LLM\.venv\Scripts\python.exe -m pip check
  dotnet build D:\LLM\backend-dotnet\RuBackend\RuBackend.csproj -c Release --nologo
  dotnet build D:\LLM\gui-winui3\RuApp.csproj -c Release -p:Platform=x64 --nologo
  ```
  Record `pip check` output, no active env upgrade.

## Docs and final verification

**Files:**
- Read: `TODO.md`, `docs/operations/verification.md`, `docs/architecture.md`, `docs/README.md`, `CONTEXT.md`
- Modify: `TODO.md`, `docs/operations/verification.md` (and `docs/architecture.md` if behavior changed)
- Verify: Sequential final gates from `docs/operations/verification.md`

- [ ] **Step 1 — Docs sync.**
  After L/N/P/C/O/F/B/W/D are done or rejected, update `TODO.md` to mark completed slices, update `docs/operations/verification.md` with measured before/after counts (not invented percentages), update `docs/architecture.md` only for the helpers actually added. Keep old evidence in historical sections with dates, do not delete.

- [ ] **Step 2 — Sequential final gates.**
  ```powershell
  & D:\LLM\.venv\Scripts\python.exe -m ruff check D:\LLM\src D:\LLM\tests D:\LLM\scripts D:\LLM\tools
  & D:\LLM\.venv\Scripts\python.exe -m mypy D:\LLM\src
  & D:\LLM\.venv\Scripts\python.exe -m pytest -q
  & D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
  & D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output "$env:TEMP\ru-prompt-composer-harness.json"
  & D:\LLM\.venv\Scripts\python.exe -m pip check
  & D:\LLM\scripts\test_dotnet.ps1 -Configuration Release
  dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --no-restore --nologo
  cmd /c D:\LLM\build.bat
  ```
  Run sequentially, no parallel heavy suites. Record exit codes, test counts (Python 402, .NET 2,009 baseline + new), and any skips. Do not claim live voice/VTS/cloud.

## Exit

- `pip check` passes, `pyproject.toml` has consumer-aligned extras, no `2.8` pin.
- Docs/TODO reflect delivered code, old evidence dated.
- Sequential final gates recorded at delivered checkout, no generated artifacts committed.

- [ ] D completed with `pip check` evidence
- [ ] Docs/TODO synced
- [ ] Sequential final gates recorded
