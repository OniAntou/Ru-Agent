# Dependency Alignment and Documentation Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove only proven unused declarations, make OmniVoice install metadata consistent, and publish truthful optimization/verification status.

**Architecture:** Keep the managed production runtime and isolated Python provider. Use consumer evidence rather than name-based deletion; install declarations must describe a verified environment, not force an unreviewed upgrade.

**Tech Stack:** Python package metadata, PowerShell, current .NET/Python runners, Markdown.

## Global Constraints

> Before executing, read the master plan's **Resume gate after concurrent implementation**.
> Several planned modules already exist in `e6fa40c`; inspect current code and
> verify missing acceptance evidence rather than recreating or replacing them.

Inherit [master constraints](2026-08-31-codebase-optimization.md). Rollout phase 4. No package install/uninstall, model downloads, active environment upgrades or deletion without an exact approved manifest.

---

## D1 — Consumer inventory and dependency decision

**Files:**

- Modify after evidence: `pyproject.toml`, `scripts/setup_omnivoice_env.ps1`.
- Read: `src/ru_runtime/voice/omnivoice_server.py` and its imports; retained source/scripts/tools/tests and CI/build declarations.
- Create: `docs/operations/python-dependency-ownership.md` (compact current consumer/version table).
- Add if needed: `tests/test_omnivoice_setup.py` for actual setup-script behavior.
- Existing tests: `tests/test_omnivoice_tts_server.py`, `tests/test_omnivoice_tuning.py`, `tests/test_python_runtime_boundary.py`.

**Interfaces:** No new runtime/generation interface. Keep setup `-WhatIf` and explicit install/download confirmation behavior. Inventory metadata only; do not load Torch/CUDA/models just to read versions.

- [ ] **Step 1 — Inventory exact consumers.**

```powershell
rg -n "ddgs|DDGS|duckduckgo_search|httpx|keyring|torch|torchaudio|omnivoice" src scripts tools tests pyproject.toml .github
git ls-files src scripts tools tests
& .venv\Scripts\python.exe -m pip check
if (Test-Path -LiteralPath omnivoice_env\Scripts\python.exe) {
    & omnivoice_env\Scripts\python.exe -m pip show torch torchaudio omnivoice flask
}
```

Record declaration, production/tooling/test consumers, dynamic entry points, active environment, proposed action and proof. Inspect dynamic imports/package-data/documented external entry points before saying unused.

Audit leads, not deletion permission:

- ddgs is declared without a found Python import; managed web tools have separate ownership.
- httpx has benchmark/Harness/acceptance consumers; do not delete because production HTTP is C#.
- voice extra requires Torch >=2.9; setup pins 2.8.0+cu128 and matching torchaudio.
- Some Python coordinators appear test-only; no blanket Python removal. OmniVoice, speech/memory tooling and package assets stay protected.

- [ ] **Step 2 — Resolve version authority without live mutation.**

Read installed OmniVoice requirements and supported Python/CUDA setup. Choose one compatible source for installation versions backed by evidence. If local metadata cannot establish compatibility, record the exact question and ask before downloads/upgrade; do not guess either Torch version is correct.

A conservative valid outcome is aligning metadata to the already verified provider environment, with reasons recorded, rather than upgrading it. Do not incorrectly constrain installations that do not request voice extras.

If introducing constraints/lock data, setup must consume it instead of retaining an independent version list. Validate resolved requirements against installed metadata offline with packaging specifier semantics, including CUDA local tags; string equality alone is not compatibility proof.

- [ ] **Step 3 — Behavioral RED/GREEN for setup.**

Run the actual PowerShell script with `-WhatIf` in an isolated repository-shaped fixture. Capture exit/output and sentinel paths using a fake Python process boundary, never real pip. Assert no environment/cache/model directory created and no installer invoked. Missing confirmation must fail before mutation.

For confirmed-path test coverage, use only a fixture executable recording package arguments; assert resolved manifest and matched torch/torchaudio build are consumed. A wrong package argument must break the test. Do not execute a confirmed install in the real checkout.

Read-only real preflight:

```powershell
& scripts/setup_omnivoice_env.ps1 -WhatIf
if ($LASTEXITCODE -ne 0) { throw 'OmniVoice preflight failed.' }
```

- [ ] **Step 4 — Scoped cleanup and validation.**

Present exact file-deletion targets with consumer proof and Git recovery, then wait for approval. Proven unused dependency declarations may be removed without uninstalling installed packages. Leave unresolved candidates in the inventory.

```powershell
& .venv\Scripts\python.exe -m pytest -q tests/test_omnivoice_tts_server.py tests/test_omnivoice_tuning.py tests/test_python_runtime_boundary.py
& .venv\Scripts\python.exe -m ruff check src tests scripts tools
& .venv\Scripts\python.exe -m mypy src
& .venv\Scripts\python.exe -m pip check
```

Run new setup tests too, checking every exit code. Fresh-environment resolver/install proof needs separately authorized dependency access. Old-environment pip check is not clean-install proof.

## D2 — Current truth, historical records, final gates

**Files:**

- Review/update: `CONTEXT.md`, `TODO.md`, `docs/README.md`, `docs/architecture.md`, `docs/operations/verification.md`, `docs/operations/agent-tools.md`.
- Correct observed current ownership: `docs/adr/0001-aspnet-core-public-host.md`, `docs/adr/0006-structured-logging-and-activity-boundaries.md`.
- Reconcile against: `docs/adr/0003-omnivoice-process-isolation.md`.
- Update evidence/checklists in this plan set.
- Existing integrity tests: `tests/test_current_documentation.py`.

- [ ] **Step 1 — Reconcile observed contradictions.**

ADR-0001 still says RuApp starts Worker/Broker alongside Host; current managed Public Host owns those children. Correct actual ownership while retaining decision history/date context.

ADR-0006 describes Python Worker activity/diagnostic paths. Trace current managed ingestion/writer consumers before updating; do not blindly substitute C# into a different outbox architecture.

Architecture explicitly labels framed Voice Engine a future target, while ADR-0003 supersedes an earlier framed-stdio description. Clarify current HTTP behavior and the source/status of any future decision. This does not authorize changing transport or silently revoking an approved future design.

- [ ] **Step 2 — Retire docs responsibly.**

Classify current contract, implemented checkpoint, future design and historical evidence. Preserve dated old test/route counts; link superseded records to replacements. Delete only an approved obsolete/duplicate manifest after fixing inbound links, not merely old documents or unchecked historical boxes.

Close TODO items only for implemented/measured behavior. Keep live first-audio, native device acceptance, new features and OS sandbox separate.

- [ ] **Step 3 — Sequential final verification.**

Run from the intended delivered checkout; stop each gate on nonzero exit. Empty filtered suites are not passes.

```powershell
& .venv\Scripts\python.exe -m pytest -q
if ($LASTEXITCODE -ne 0) { throw 'Python tests failed.' }
& .venv\Scripts\python.exe -m ruff check src tests scripts tools
if ($LASTEXITCODE -ne 0) { throw 'Ruff failed.' }
& .venv\Scripts\python.exe -m mypy src
if ($LASTEXITCODE -ne 0) { throw 'Mypy failed.' }
& .venv\Scripts\python.exe -m pip check
if ($LASTEXITCODE -ne 0) { throw 'Dependency check failed.' }
& .venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
if ($LASTEXITCODE -ne 0) { throw 'Public contract failed.' }
& scripts/test_dotnet.ps1 -Configuration Release
if ($LASTEXITCODE -ne 0) { throw '.NET matrix failed.' }
dotnet build gui-winui3.Harness/RuApp.Harness.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'GUI Harness build failed.' }
& .venv\Scripts\python.exe -m pytest -q tests/test_current_documentation.py
if ($LASTEXITCODE -ne 0) { throw 'Documentation integrity failed.' }
git diff --check
if ($LASTEXITCODE -ne 0) { throw 'Diff whitespace check failed.' }
git status --short --branch
```

Also run pure Release native tests from subplan 02 with probes disabled. If product publish is required, inspect build.bat and build without starting start_dev.bat. Do not label an old GUI publish as fresh.

Backend-deterministic Harness is optional synthetic process evidence after checking isolated roots/no real device/provider. GUI Harness opens WinUI and remains user-run. No live cloud/voice/VTS acceptance is included.

- [ ] **Step 4 — Evidence and handoff.**

Append actual dated commands/results, work-count comparisons, rejected candidates, skipped gates and reasons to verification docs. Separate contract/adapter/pure native tests from real-device evidence.

Report source/test/doc changes, approved deletions and recovery, and outstanding manual work. Preserve uncommitted scoped changes for review; no commit/push without a separate request.

**Exit:** Declarations/docs match delivered code; every speed claim has measurements; unverified compatibility/native outcomes remain open.
