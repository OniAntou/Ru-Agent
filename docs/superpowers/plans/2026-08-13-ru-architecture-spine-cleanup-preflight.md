# Ru Architecture Spine and Cleanup Preflight Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Do not dispatch sub-agents.

**Goal:** Establish the current Ru architecture/decision spine, repair stale CI verification, and produce a deterministic non-destructive cleanup manifest for the later legacy-removal plan.

**Architecture:** Documentation becomes the current source of truth before source movement begins. A pure cleanup-policy module classifies protected, rebuildable, legacy, and review-required paths; a separate inventory module reads filesystem/Git metadata and emits JSON, but contains no deletion capability. CI and repository contract tests keep this first slice independently useful and green.

**Tech Stack:** Python 3.13 standard library and pytest; Markdown; GitHub Actions YAML; ASP.NET Core/.NET 10 verification; PowerShell on Windows.

## Global Constraints

- Execute in an isolated `codex/ru-architecture-v2` worktree created from the current `HEAD`; leave the dirty `D:\LLM` main worktree untouched.
- Use no sub-agents.
- Do not delete, move, rewrite, stage, or commit any user-owned dirty/untracked file in this plan.
- This plan is inventory-only for local cleanup: it must expose no delete function and invoke no destructive filesystem command.
- Protect `.git`, `.worktrees`, `.venv`, `omnivoice_env`, active LFM2.5/OmniVoice assets, user settings/sessions/history/activity/memory/credentials, and every Git dirty/untracked path.
- Keep ASP.NET Core as the only public host and preserve the 38-HTTP/2-WebSocket public contract.
- Python requires `>=3.11,<3.15`; CI uses Python `3.13`; .NET targets `net10.0`.
- Keep chat, transcript, audio, credentials, tokens, request bodies, session IDs, and private VTS/voice metadata out of manifests and logs.
- Run heavyweight Python, .NET, native, and live checks sequentially on `D:\LLM`.
- Use `apply_patch` for repository edits and stage only files named by the active task.
- Do not launch RuApp, VTube Studio, a browser, or other user-facing applications.
- Do not push.

## Scope coverage

This plan implements only slice 1 of the approved umbrella design:

- current architecture/domain/decision documents;
- CI and repository contracts for the already-completed ASP.NET Core cutover;
- a pure cleanup classification policy;
- a read-only inventory CLI and count-only preflight evidence.

The following approved work is deliberately deferred to Plans 2-6: deleting
legacy Llama/local clutter, renaming `mini_gpt` to `ru_runtime`, migrating
business data to `%LOCALAPPDATA%\Ru`, replacing worker v1 with protocol v2,
replacing OmniVoice Flask with framed process I/O, and deepening WinUI voice.
This plan changes no public route and deletes zero files.

## File map

### Create

- `CONTEXT.md` — project-specific domain language and ownership vocabulary.
- `docs/README.md` — index separating current architecture/operations/validation from dated implementation records.
- `docs/architecture.md` — current process, data, ownership, and privacy flow.
- `docs/adr/0001-aspnet-core-public-host.md` — ASP.NET-only public-host decision.
- `docs/adr/0002-typed-worker-protocol-v2.md` — typed worker seam decision.
- `docs/adr/0003-omnivoice-process-isolation.md` — framed child-process decision.
- `docs/adr/0004-canonical-ru-data-location.md` — `%LOCALAPPDATA%\Ru` migration decision.
- `docs/adr/0005-retire-legacy-llama-line.md` — approved Llama 3.1/v6.x retirement decision.
- `docs/operations/verification.md` — canonical local/CI verification commands.
- `docs/validation/architecture-v2-cleanup-preflight.md` — count/size/classification-only preflight evidence.
- `tests/test_current_documentation.py` — current-document and ADR contract.
- `tests/test_ci_contract.py` — CI command contract.
- `tools/maintenance/__init__.py` — maintenance package marker.
- `tools/maintenance/cleanup_policy.py` — pure path normalization/classification policy.
- `tools/maintenance/cleanup_inventory.py` — read-only Git/filesystem inventory and JSON CLI.
- `tests/test_cleanup_policy.py` — policy tests.
- `tests/test_cleanup_inventory.py` — inventory/CLI/privacy/non-destruction tests.

### Modify

- `README.md` — add a short current-document entry point without rewriting historical sections in this slice.
- `.github/workflows/ci.yml` — remove the deleted `server` extra and add current backend/GUI/route verification.
- `.gitignore` — ignore generated cleanup manifests under `artifacts/cleanup/`.

### Generated but not committed

- `<isolated-worktree>\artifacts\cleanup\ru-architecture-v2-preflight.json` — exact local cleanup manifest for the scanned `D:\LLM` main workspace. Keeping the output in the isolated worktree avoids adding a file to dirty `main`.

---

### Task 0: Prove the isolated baseline before editing

**Files:** None.

**Interfaces:**
- Consumes: clean `HEAD` in the new worktree and the existing dirty-main status.
- Produces: a baseline that distinguishes pre-existing failures from plan regressions.

- [x] **Step 1: Confirm worktree and branch isolation**

Run from the isolated worktree:

```powershell
git status --short --branch
git branch --show-current
git -C D:\LLM status --short --branch
```

Expected: the isolated worktree is clean on `codex/ru-architecture-v2`; the
main-worktree output still contains the previously recorded user-owned dirty
and untracked files.

- [x] **Step 2: Run the clean-HEAD baseline sequentially**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet restore backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --nologo
dotnet restore gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
```

Observed clean-HEAD baseline: 397 Python tests passed and two legacy corpus
manifest hashes failed; 219 backend tests passed; 94 WinUI tests passed; route
contract reported `38 HTTP routes, 2 WebSocket routes`. Investigation proved
that only `base_adapter` absolute checkout paths differ and all corpus split
hashes are identical. Task 2 adds the path-invariant regression gate; it does
not change corpus rows.

---

### Task 1: Add the architecture and decision spine

**Files:**
- Create: `CONTEXT.md`
- Create: `docs/README.md`
- Create: `docs/architecture.md`
- Create: `docs/adr/0001-aspnet-core-public-host.md`
- Create: `docs/adr/0002-typed-worker-protocol-v2.md`
- Create: `docs/adr/0003-omnivoice-process-isolation.md`
- Create: `docs/adr/0004-canonical-ru-data-location.md`
- Create: `docs/adr/0005-retire-legacy-llama-line.md`
- Create: `docs/operations/verification.md`
- Modify: `README.md`
- Test: `tests/test_current_documentation.py`

**Interfaces:**
- Consumes: approved umbrella spec `docs/superpowers/specs/2026-08-13-ru-architecture-v2-refactor-design.md`; public contract `backend-dotnet/public-route-contract.json`.
- Produces: stable document paths and project terms consumed by every later architecture-v2 plan.

- [x] **Step 1: Write the failing documentation contract**

Create `tests/test_current_documentation.py`:

```python
from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def _text(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def test_current_architecture_spine_exists_and_is_linked() -> None:
    required = (
        "CONTEXT.md",
        "docs/README.md",
        "docs/architecture.md",
        "docs/operations/verification.md",
        "docs/adr/0001-aspnet-core-public-host.md",
        "docs/adr/0002-typed-worker-protocol-v2.md",
        "docs/adr/0003-omnivoice-process-isolation.md",
        "docs/adr/0004-canonical-ru-data-location.md",
        "docs/adr/0005-retire-legacy-llama-line.md",
    )
    assert all((ROOT / relative).is_file() for relative in required)

    readme = _text("README.md")
    assert "docs/architecture.md" in readme
    assert "CONTEXT.md" in readme


def test_context_uses_approved_ru_domain_language() -> None:
    context = _text("CONTEXT.md")
    for term in (
        "**Public Host**:",
        "**Runtime Worker**:",
        "**Generation Kernel**:",
        "**Voice Engine**:",
        "**Public Contract**:",
        "**Worker Contract**:",
        "**Protected User Data**:",
        "**Legacy Llama Assets**:",
    ):
        assert term in context


def test_adrs_are_accepted_and_concise() -> None:
    adrs = sorted((ROOT / "docs" / "adr").glob("*.md"))
    assert [path.name[:4] for path in adrs] == ["0001", "0002", "0003", "0004", "0005"]
    for path in adrs:
        text = path.read_text(encoding="utf-8")
        assert "status: accepted" in text.casefold()
        assert len(text.splitlines()) <= 40


def test_architecture_records_current_ownership_and_privacy() -> None:
    architecture = _text("docs/architecture.md")
    assert "ASP.NET Core" in architecture
    assert "38 HTTP" in architecture
    assert "2 WebSocket" in architecture
    assert "typed worker protocol v2" in architecture
    assert "chat text" in architecture
    assert "private VTS" in architecture
```

- [x] **Step 2: Run the test and verify the RED state**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_current_documentation.py
```

Expected: FAIL because `CONTEXT.md`, `docs/architecture.md`, ADRs, and the documentation index do not exist.

- [x] **Step 3: Create the project language document**

Create `CONTEXT.md` using the approved format:

```markdown
# Ru Assistant

Ru is a local Vietnamese-English assistant whose desktop, public host, runtime processes, persistent conversation state, voice, and VTube embodiment are maintained in this repository.

## Language

**Public Host**:
The ASP.NET Core process that exclusively owns RuApp's authenticated HTTP and WebSocket contract.
_Avoid_: Edge, proxy, FastAPI backend

**Runtime Worker**:
The authenticated loopback Python process that contains model or hardware implementations retained in Python.
_Avoid_: Python backend, upstream server

**Generation Kernel**:
The module that owns model selection, lifecycle, cancellation, GPU ownership, and generation across local and hosted model adapters.
_Avoid_: ModelRunner globals, backend core

**Voice Engine**:
The isolated OmniVoice child process running inside `omnivoice_env` and returning native WAV audio over a private framed process interface.
_Avoid_: TTS web server, Flask voice server

**Public Contract**:
The stable authenticated surface of 38 HTTP method/path pairs plus `/chat` and `/voice/stream`.
_Avoid_: Compatibility routes

**Worker Contract**:
The versioned typed commands, events, ordering, cancellation, limits, errors, and capabilities exchanged with the Runtime Worker.
_Avoid_: Runtime HTTP relay

**Protected User Data**:
Settings, sessions, history, activity, structured memory, credentials, and reviewed examples used by the active product.
_Avoid_: Cleanup candidates, generated data

**Legacy Llama Assets**:
Source, configs, scripts, datasets, checkpoints, evaluations, and documents whose only supported purpose is Llama 3.1 or a v6.x adapter.
_Avoid_: Active LFM2.5 assets
```

- [x] **Step 4: Create concise accepted ADRs**

Create each ADR with YAML status and one decision paragraph. Use these exact decisions:

```markdown
---
status: accepted
---

# ASP.NET Core is Ru's only public host

Ru keeps the existing authenticated 38-HTTP/2-WebSocket contract in ASP.NET Core. Python runtime processes remain private implementation adapters and may not expose a public HTTP fallback, because dual public ownership previously added latency, route drift, and ambiguous lifecycle ownership.
```

```markdown
---
status: accepted
---

# Use typed worker protocol v2

The private worker seam uses versioned typed domain commands over authenticated loopback WebSocket. It does not carry HTTP methods, paths, content types, or status codes; this keeps public response mapping in ASP.NET Core and removes the migration-era pseudo-HTTP interface.
```

```markdown
---
status: accepted
---

# Isolate OmniVoice behind a framed process

OmniVoice remains in `omnivoice_env`, but its owned child process communicates through bounded stdin/stdout control and binary frames instead of Flask on port 5052. This preserves dependency isolation while removing an unnecessary web seam and network listener.
```

```markdown
---
status: accepted
---

# Store current Ru business data under LocalAppData

The canonical Windows location for settings, sessions, history, and activity is `%LOCALAPPDATA%\Ru`. Migration from `~\.minigpt` must copy, validate, hash, atomically activate, and prove readability before legacy data or credentials are removed.
```

```markdown
---
status: accepted
---

# Retire the legacy Llama line

Ru's supported local model is LFM2.5. Llama 3.1/v6.x-only source and local assets are removed after a path/reference/protection manifest because retaining an unsupported training line obscures the active runtime and produces misleading operations documentation.
```

- [x] **Step 5: Create current architecture, documentation index, and verification runbook**

`docs/architecture.md` must contain:

```text
RuApp -> ASP.NET Core public contract -> typed worker protocol v2 -> Runtime Worker
Runtime Worker -> model adapters / Voice Engine / STT / VTS / observation
```

It must state current ownership, process startup/shutdown, protected data, binary audio framing, and metadata privacy. Mark protocol v2 and framed OmniVoice as accepted target decisions that are implemented by later plans; do not claim they already run.

`docs/README.md` must list:

- Current: `architecture.md`, `operations/verification.md`, route contract, decommission validation, ADRs.
- Active design: the architecture-v2 umbrella spec and this implementation plan.
- Historical implementation records: `docs/superpowers/`, explicitly non-authoritative until later cleanup.

`docs/operations/verification.md` must contain exact current commands:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
dotnet list D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj package --vulnerable --include-transitive
cmd /c D:\LLM\build.bat
```

Document that heavyweight commands run sequentially and that automated audio frames do not replace listening/microphone/VTS acceptance.

- [x] **Step 6: Add current-document links to the root README**

Immediately after the opening description, add:

```markdown
Current system documentation:

- [Architecture](docs/architecture.md)
- [Domain language](CONTEXT.md)
- [Operations and verification](docs/operations/verification.md)
- [Architecture decisions](docs/adr/)
```

Do not rewrite the remaining README in this task; legacy-section removal belongs to Plan 2.

- [x] **Step 7: Run the documentation contract**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_current_documentation.py
```

Expected: all tests PASS.

- [x] **Step 8: Commit the architecture spine**

```powershell
git add CONTEXT.md README.md docs/README.md docs/architecture.md docs/adr docs/operations/verification.md tests/test_current_documentation.py
git diff --cached --check
git commit -m "docs: add Ru architecture decision spine"
```

Expected staged scope: only the files listed above.

---

### Task 2: Repair CI and make current verification explicit

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `scripts/verify_corpus_byte_identical.py`
- Modify: `scripts/corpus_baseline_hashes.json`
- Modify: `tests/test_corpus_byte_identical.py`
- Test: `tests/test_ci_contract.py`

**Interfaces:**
- Consumes: current project paths and commands recorded in `docs/operations/verification.md`.
- Produces: a checkout-portable corpus gate and CI command contract used until later plans remove legacy corpus verification.

- [x] **Step 1: Write the failing CI contract test**

Create `tests/test_ci_contract.py`:

```python
from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_ci_uses_current_python_extras_and_route_contract() -> None:
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    assert ".[dev,runtime,gguf]" in workflow
    assert "dev,server" not in workflow
    assert "scripts/check_aspnetcore_route_matrix.py" in workflow
    assert "python -m pytest -q" in workflow
    assert "pip check" in workflow


def test_ci_runs_both_dotnet_test_projects() -> None:
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    assert "backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj" in workflow
    assert "gui-winui3.Tests/gui-winui3.Tests.csproj" in workflow
    assert "dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64" in workflow
```

- [x] **Step 2: Run the test and verify the RED state**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_ci_contract.py
```

Expected: FAIL because CI still references `server` and does not run the route or .NET test contracts.

- [x] **Step 3: Write the failing checkout-portability regression test**

In `tests/test_corpus_byte_identical.py`, import `json` and
`_sha256_manifest` from `verify_corpus_byte_identical`, then add:

```python
def _write_manifest(path: Path, base_adapter: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(
            {
                "dataset_id": "fixture",
                "base_adapter": base_adapter,
                "total_examples": 1,
            },
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


def test_manifest_hash_is_independent_of_checkout_root(tmp_path: Path) -> None:
    first = tmp_path / "first" / "manifest.json"
    second = tmp_path / "second" / "manifest.json"
    _write_manifest(first, r"C:\first-checkout\checkpoints\same-adapter")
    _write_manifest(second, r"D:\second-checkout\checkpoints\same-adapter")

    assert _sha256_manifest(first) == _sha256_manifest(second)


def test_manifest_hash_still_detects_a_different_adapter(tmp_path: Path) -> None:
    first = tmp_path / "first" / "manifest.json"
    second = tmp_path / "second" / "manifest.json"
    _write_manifest(first, r"C:\checkout\checkpoints\adapter-a")
    _write_manifest(second, r"C:\checkout\checkpoints\adapter-b")

    assert _sha256_manifest(first) != _sha256_manifest(second)
```

The mutation caught by the first test is reintroducing the checkout prefix into
the hash. The second prevents over-normalizing away the adapter identity.

- [x] **Step 4: Run the new tests and verify the RED state**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q `
  tests\test_corpus_byte_identical.py::test_manifest_hash_is_independent_of_checkout_root `
  tests\test_corpus_byte_identical.py::test_manifest_hash_still_detects_a_different_adapter
```

Expected: collection FAIL because `_sha256_manifest` does not exist.

- [x] **Step 5: Canonicalize manifest metadata before hashing**

In `scripts/verify_corpus_byte_identical.py`, add `_sha256_manifest(path)`:

1. Parse the manifest JSON.
2. Require `base_adapter` to be a string when present.
3. Replace both `\` and `/` separators, retain the final checkpoint directory
   name, and canonicalize the field to `checkpoints/<name>`.
4. Hash `json.dumps(report, ensure_ascii=False, sort_keys=True,
   separators=(",", ":")).encode("utf-8")`.
5. When no `base_adapter` is present, retain `_sha256_file(path)` so v6.1/v6.2
   baselines do not change.

Use `_sha256_manifest` only for `manifest.json` inside `_collect_hashes`.
Update the two path-bearing baseline values to these independently calculated
canonical hashes:

```json
"v6_2_fresh/manifest.json": "e6ff2e144271c72e2c84837818b269f7dbf50147a8bb6be5379feb611d47b6c4",
"v6_3/manifest.json": "7212341f566560987cbe660eacd24e5d9c4bb4b59094ec41ccba54dedaac9944"
```

The slash-separated keys above identify the two nested values to replace; do
not alter the JSON schema or the split-file hashes.

- [x] **Step 6: Run the full legacy corpus gate**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_corpus_byte_identical.py
& D:\LLM\.venv\Scripts\python.exe scripts\verify_corpus_byte_identical.py
```

Expected: six tests pass and all four versions report PASS from any checkout
path. No corpus file or row changes.

- [x] **Step 7: Update the Python CI job**

Change the install line to:

```yaml
- run: pip install -e ".[dev,runtime,gguf]"
```

After pytest, add:

```yaml
- run: python scripts/check_aspnetcore_route_matrix.py
```

Keep `verify_corpus_byte_identical.py` for this plan; Plan 2 removes it in the same commit that removes the legacy corpus code.

- [x] **Step 8: Replace the WinUI-only CI job with current .NET verification**

Keep the job name `dotnet` and use these commands in order:

```yaml
- run: dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --nologo
- run: dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --nologo
- run: dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64
```

- [x] **Step 9: Run the CI contract test**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_ci_contract.py
```

Expected: all tests PASS.

- [x] **Step 10: Run current decommission and route guards**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_backend_decommission.py tests\test_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
```

Expected: tests pass and checker reports `38 HTTP routes, 2 WebSocket routes`.

- [x] **Step 11: Commit the CI repair**

```powershell
git add .github/workflows/ci.yml scripts/verify_corpus_byte_identical.py scripts/corpus_baseline_hashes.json tests/test_corpus_byte_identical.py tests/test_ci_contract.py docs/superpowers/plans/2026-08-13-ru-architecture-spine-cleanup-preflight.md
git diff --cached --check
git commit -m "ci: verify current ASP.NET Core architecture"
```

---

### Task 3: Define a pure cleanup classification policy

**Files:**
- Create: `tools/maintenance/__init__.py`
- Create: `tools/maintenance/cleanup_policy.py`
- Test: `tests/test_cleanup_policy.py`

**Interfaces:**
- Consumes: repository-relative path strings and a set of Git dirty paths.
- Produces: `CleanupDecision` through `classify_path(relative_path, *, tracked, dirty_paths)`; no filesystem writes.

- [x] **Step 1: Write failing cleanup-policy tests**

Create `tests/test_cleanup_policy.py`:

```python
from __future__ import annotations

import pytest

from tools.maintenance.cleanup_policy import (
    CleanupCategory,
    classify_path,
    normalize_relative_path,
)


@pytest.mark.parametrize(
    "value",
    ("", ".", "../outside", "/outside", r"\\server\share", "D:/LLM/models"),
)
def test_normalize_relative_path_rejects_broad_or_external_paths(value: str) -> None:
    with pytest.raises(ValueError):
        normalize_relative_path(value)


@pytest.mark.parametrize(
    "relative",
    (
        ".git",
        ".worktrees/task",
        ".venv/Lib/site-packages/example.py",
        "omnivoice_env/python.exe",
        "models/lfm2.5-2.6b-hf/config.json",
        "models/gguf/LFM2.5-2.6B-Q4_K_M.gguf",
        "models/omnivoice/model.bin",
        "checkpoints/ru-lfm2.5-2.6b-v1/adapter.json",
        "data/conversations/ru_v1/train.jsonl",
        "data/conversations/approved.jsonl",
        "data/conversations/memory.db",
        "data/memory.db",
        ".minigpt/settings.json",
        "docs/superpowers/specs/2026-08-13-ru-architecture-v2-refactor-design.md",
        "docs/superpowers/plans/2026-08-13-ru-architecture-spine-cleanup-preflight.md",
    ),
)
def test_active_runtime_user_data_and_current_design_are_protected(relative: str) -> None:
    decision = classify_path(relative, tracked=False, dirty_paths=frozenset())
    assert decision.category is CleanupCategory.PROTECTED


def test_dirty_path_is_protected_before_legacy_classification() -> None:
    relative = "src/mini_gpt/llama3_1_training.py"
    decision = classify_path(relative, tracked=True, dirty_paths=frozenset({relative}))
    assert decision.category is CleanupCategory.PROTECTED
    assert decision.reason == "git_dirty_or_untracked"


def test_candidate_root_is_protected_when_it_contains_a_dirty_path() -> None:
    relative = "data/conversations/ru_v6_2"
    dirty = frozenset({"data/conversations/ru_v6_2/private-change.jsonl"})
    decision = classify_path(relative, tracked=False, dirty_paths=dirty)
    assert decision.category is CleanupCategory.PROTECTED
    assert decision.reason == "git_dirty_or_untracked"


@pytest.mark.parametrize(
    "relative",
    (
        "configs/ru_llama3_1_v6_3.json",
        "src/mini_gpt/ru_v6_3_corpus.py",
        "checkpoints/ru-hard-evaluation-v1",
        "checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh",
        "data/conversations/ru_v6_2_fresh",
        "eval_results/ru_llama3_1_v6_style.json",
    ),
)
def test_llama_only_paths_are_classified_as_legacy(relative: str) -> None:
    decision = classify_path(relative, tracked=relative.startswith(("configs/", "src/")), dirty_paths=frozenset())
    assert decision.category is CleanupCategory.LEGACY_LLAMA
    assert decision.recoverability in {"git", "permanent"}


@pytest.mark.parametrize(
    "relative",
    (
        ".venv311",
        "tmp_verify_fresh2",
        "gui-winui3/bin",
        "backend-dotnet/RuBackend/obj",
        "gui-winui3/pri_staging",
        "tools/ru_desktop_overlay/out",
        "tests/__pycache__",
        "rustup-init.exe",
        "resources.pri.xml",
    ),
)
def test_generated_paths_are_rebuildable(relative: str) -> None:
    decision = classify_path(relative, tracked=False, dirty_paths=frozenset())
    assert decision.category is CleanupCategory.REBUILDABLE


def test_vcpkg_requires_review_while_overlay_build_references_it() -> None:
    decision = classify_path(".tmp/vcpkg", tracked=False, dirty_paths=frozenset())
    assert decision.category is CleanupCategory.REVIEW_REQUIRED


def test_backup_with_non_git_source_requires_review() -> None:
    decision = classify_path("gui-winui3.bak", tracked=False, dirty_paths=frozenset())
    assert decision.category is CleanupCategory.REVIEW_REQUIRED
    assert decision.reason == "contains_non_git_backup"


def test_runtime_logs_require_review_before_permanent_cleanup() -> None:
    decision = classify_path("logs", tracked=False, dirty_paths=frozenset())
    assert decision.category is CleanupCategory.REVIEW_REQUIRED
    assert decision.reason == "runtime_logs_may_contain_private_data"


def test_dated_implementation_record_requires_review() -> None:
    relative = "docs/superpowers/plans/2026-08-10-ru-aspnetcore-backend-migration.md"
    decision = classify_path(relative, tracked=True, dirty_paths=frozenset())
    assert decision.category is CleanupCategory.REVIEW_REQUIRED
    assert decision.reason == "dated_implementation_record"
```

- [x] **Step 2: Run the tests and verify the RED state**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_cleanup_policy.py
```

Expected: collection FAIL because `tools.maintenance.cleanup_policy` does not exist.

- [x] **Step 3: Implement exact types and path normalization**

Create `tools/maintenance/__init__.py` as an empty package marker.

Create `tools/maintenance/cleanup_policy.py` with this complete implementation:

```python
from __future__ import annotations

import re
from dataclasses import dataclass
from enum import Enum
from pathlib import PurePosixPath


class CleanupCategory(str, Enum):
    PROTECTED = "protected"
    REBUILDABLE = "rebuildable"
    LEGACY_LLAMA = "legacy_llama"
    REVIEW_REQUIRED = "review_required"


@dataclass(frozen=True, slots=True)
class CleanupDecision:
    category: CleanupCategory
    reason: str
    recoverability: str


_PROTECTED_ROOTS = (
    ".git",
    ".worktrees",
    ".venv",
    "omnivoice_env",
    ".minigpt",
    "models/lfm2.5-2.6b-hf",
    "models/gguf",
    "models/omnivoice",
    "models/vision",
    "checkpoints/ru-lfm2.5-2.6b-v1",
    "data/conversations/ru_v1",
    "data/conversations/approved.jsonl",
    "data/conversations/memory.db",
    "data/conversations/memory.db-wal",
    "data/conversations/memory.db-shm",
    "data/conversations/memory_texts.json",
    "data/memory.db",
    "data/memory.db-wal",
    "data/memory.db-shm",
    "data/gui_settings.json",
    "data/voices",
    "eval_results/lfm2_5_v1.json",
    "eval_results/omnivoice",
    "eval_results/omnivoice-pronunciation",
    "reference_text.txt",
    "tyc_talk.wav",
    "vts_token.txt",
    "docs/superpowers/specs/2026-08-13-ru-architecture-v2-refactor-design.md",
    "docs/superpowers/plans/2026-08-13-ru-architecture-spine-cleanup-preflight.md",
)

_REBUILDABLE_ROOTS = (
    ".venv311",
    "gwen_env",
    "pytest_v.log",
    "resources.pri.xml",
    "rustup-init.exe",
    "gui-winui3/pri_staging",
    "tools/ru_desktop_overlay/out",
)

_LEGACY_ROOTS = (
    "checkpoints/ru-hard-evaluation-v1",
)

_REVIEW_REASONS = {
    "gui-winui3.bak": "contains_non_git_backup",
    "logs": "runtime_logs_may_contain_private_data",
    "eval_results/judge": "mixed_evaluation_outputs",
    "scripts/test_inject.py": "one_off_runtime_probe",
    "scripts/test_keys.py": "one_off_runtime_probe",
    "scripts/test_keys_raw.py": "one_off_runtime_probe",
    "scripts/test_post_settings.py": "one_off_runtime_probe",
    "scripts/test_rest_key.py": "one_off_runtime_probe",
    "scripts/test_rest_key2.py": "one_off_runtime_probe",
    "scripts/test_settings.py": "one_off_runtime_probe",
    "scripts/test_tts.py": "one_off_runtime_probe",
    "src/mini_gpt/reference_text.txt": "duplicate_reference_text",
    "tyc_calm.wav": "unused_voice_reference_candidate",
}

_LEGACY_NAME = re.compile(
    r"(?:llama[_-]?3(?:[._-]?1)?|ru[_-]?v6(?:[._-]?[123])?|v6[_-]?[123])",
    re.IGNORECASE,
)


def normalize_relative_path(value: str) -> str:
    raw = value.replace("\\", "/")
    if raw.startswith("/") or re.match(r"^[A-Za-z]:", raw):
        raise ValueError("cleanup path must not be absolute or include a drive")
    normalized = raw.strip("/")
    path = PurePosixPath(normalized)
    if not normalized or normalized == "." or path.is_absolute() or ".." in path.parts:
        raise ValueError("cleanup path must be a narrow repository-relative path")
    return path.as_posix()


def _at_or_below(value: str, root: str) -> bool:
    return value == root or value.startswith(f"{root}/")


def _overlaps(left: str, right: str) -> bool:
    return _at_or_below(left, right) or _at_or_below(right, left)


def classify_path(
    relative_path: str,
    *,
    tracked: bool,
    dirty_paths: frozenset[str],
) -> CleanupDecision:
    relative = normalize_relative_path(relative_path)
    normalized_dirty = tuple(normalize_relative_path(path) for path in dirty_paths)

    if any(_overlaps(relative, dirty) for dirty in normalized_dirty):
        return CleanupDecision(
            CleanupCategory.PROTECTED,
            "git_dirty_or_untracked",
            "preserve",
        )

    if any(_at_or_below(relative, root) for root in _PROTECTED_ROOTS):
        return CleanupDecision(
            CleanupCategory.PROTECTED,
            "active_runtime_or_user_data",
            "preserve",
        )

    if _at_or_below(relative, ".tmp/vcpkg"):
        return CleanupDecision(
            CleanupCategory.REVIEW_REQUIRED,
            "active_overlay_build_reference",
            "review",
        )

    for root, reason in _REVIEW_REASONS.items():
        if _at_or_below(relative, root):
            return CleanupDecision(CleanupCategory.REVIEW_REQUIRED, reason, "review")

    if relative.startswith(("docs/superpowers/plans/", "docs/superpowers/specs/")):
        return CleanupDecision(
            CleanupCategory.REVIEW_REQUIRED,
            "dated_implementation_record",
            "review",
        )

    parts = PurePosixPath(relative).parts
    if (
        any(_at_or_below(relative, root) for root in _REBUILDABLE_ROOTS)
        or parts[0].startswith("tmp_verify_fresh")
        or any(part in {"bin", "obj", "__pycache__", ".pytest_cache"} for part in parts)
        or any(part.endswith(".egg-info") for part in parts)
    ):
        return CleanupDecision(CleanupCategory.REBUILDABLE, "generated_output", "rebuild")

    if any(_at_or_below(relative, root) for root in _LEGACY_ROOTS) or _LEGACY_NAME.search(
        relative
    ):
        recoverability = "git" if tracked else "permanent"
        return CleanupDecision(
            CleanupCategory.LEGACY_LLAMA,
            "legacy_llama_name",
            recoverability,
        )

    return CleanupDecision(CleanupCategory.REVIEW_REQUIRED, "unclassified", "review")
```

The dirty-path overlap check deliberately protects both a dirty leaf and any
candidate directory that contains it. Do not weaken this to one-way prefix
matching during implementation.

- [x] **Step 4: Run policy tests**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_cleanup_policy.py
```

Expected: all tests PASS.

- [x] **Step 5: Commit the pure cleanup policy**

```powershell
git add tools/maintenance/__init__.py tools/maintenance/cleanup_policy.py tests/test_cleanup_policy.py
git diff --cached --check
git commit -m "feat: define protected cleanup policy"
```

---

### Task 4: Build a read-only cleanup inventory CLI

**Files:**
- Create: `tools/maintenance/cleanup_inventory.py`
- Create: `tests/test_cleanup_inventory.py`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: `classify_path`, a workspace root, Git tracked/dirty metadata, and optional output path.
- Produces: `build_manifest(workspace_root: Path) -> dict[str, object]`; CLI exit `0` after atomically writing count/metadata-only JSON.

- [x] **Step 1: Write failing inventory tests**

Create `tests/test_cleanup_inventory.py`:

```python
from __future__ import annotations

import json
from pathlib import Path

import pytest

from tools.maintenance.cleanup_inventory import (
    build_entry,
    build_manifest,
    main,
    write_manifest,
)


def test_build_entry_records_metadata_without_contents(tmp_path: Path) -> None:
    candidate = tmp_path / "tmp_verify_fresh"
    candidate.mkdir()
    (candidate / "train.jsonl").write_text("private training row", encoding="utf-8")

    entry = build_entry(
        tmp_path,
        "tmp_verify_fresh",
        tracked=False,
        dirty_paths=frozenset(),
    )

    assert entry["relative_path"] == "tmp_verify_fresh"
    assert entry["classification"] == "rebuildable"
    assert entry["file_count"] == 1
    assert entry["size_bytes"] == len("private training row".encode("utf-8"))
    assert "private training row" not in json.dumps(entry)


def test_manifest_keeps_dirty_legacy_path_protected(tmp_path: Path, monkeypatch) -> None:
    path = tmp_path / "src" / "mini_gpt" / "llama3_1_training.py"
    path.parent.mkdir(parents=True)
    path.write_text("changed", encoding="utf-8")

    monkeypatch.setattr(
        "tools.maintenance.cleanup_inventory.git_paths",
        lambda _root, *args: ("src/mini_gpt/llama3_1_training.py",),
    )
    monkeypatch.setattr(
        "tools.maintenance.cleanup_inventory.git_dirty_paths",
        lambda _root: frozenset({"src/mini_gpt/llama3_1_training.py"}),
    )

    manifest = build_manifest(tmp_path)
    entry = next(value for value in manifest["entries"] if value["relative_path"].endswith("llama3_1_training.py"))
    assert entry["classification"] == "protected"


def test_manifest_has_stable_schema_and_sorted_entries(tmp_path: Path, monkeypatch) -> None:
    (tmp_path / ".venv311").mkdir()
    (tmp_path / "rustup-init.exe").write_bytes(b"installer")
    monkeypatch.setattr("tools.maintenance.cleanup_inventory.git_paths", lambda _root, *args: ())
    monkeypatch.setattr("tools.maintenance.cleanup_inventory.git_dirty_paths", lambda _root: frozenset())

    manifest = build_manifest(tmp_path)
    assert manifest["schema_version"] == 1
    assert manifest["workspace_root"] == str(tmp_path.resolve())
    relative = [entry["relative_path"] for entry in manifest["entries"]]
    assert relative == sorted(relative, key=str.casefold)
    assert set(manifest["summary"]) == {"entry_count", "file_count", "size_bytes", "by_classification"}


def test_write_manifest_is_atomic_and_json_round_trips(tmp_path: Path) -> None:
    output = tmp_path / "artifacts" / "cleanup" / "manifest.json"
    manifest = {"schema_version": 1, "entries": [], "summary": {}}
    write_manifest(manifest, output)
    assert json.loads(output.read_text(encoding="utf-8")) == manifest
    assert not output.with_suffix(".json.tmp").exists()


def test_inventory_source_exposes_no_delete_capability() -> None:
    root = Path(__file__).resolve().parents[1]
    source = (root / "tools" / "maintenance" / "cleanup_inventory.py").read_text(encoding="utf-8")
    forbidden = ("unlink(", "rmtree(", "remove(", "removedirs(", "Delete(", "Remove-Item")
    assert not any(value in source for value in forbidden)


def test_cli_rejects_output_outside_its_checkout(tmp_path: Path) -> None:
    output = tmp_path / "artifacts" / "cleanup" / "manifest.json"
    with pytest.raises(SystemExit) as error:
        main([
            "--workspace-root",
            str(tmp_path.resolve()),
            "--output",
            str(output.resolve()),
        ])
    assert error.value.code == 2
```

- [x] **Step 2: Run tests and verify the RED state**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_cleanup_inventory.py
```

Expected: collection FAIL because `cleanup_inventory.py` does not exist.

- [x] **Step 3: Implement the read-only inventory module**

Create `tools/maintenance/cleanup_inventory.py` with the following exact
contracts and algorithms:

- `git_paths(root, *arguments)` invokes `git` with
  `subprocess.run(["git", *arguments], cwd=root, shell=False, check=True,
  capture_output=True, text=True, encoding="utf-8")`. It parses NUL-delimited
  output when present, otherwise line-delimited output, normalizes each path,
  and returns a tuple sorted by `str.casefold`.
- `git_dirty_paths(root)` invokes
  `git status --porcelain=v1 -z --untracked-files=all`. Parse the two status
  columns and path field; for rename/copy records consume and retain the second
  NUL-delimited path too. Return the normalized union as a `frozenset`.
- `build_entry(workspace_root, relative_path, *, tracked, dirty_paths)` applies
  `classify_path` and emits only metadata. A directory candidate is considered
  dirty when either it contains a dirty path or is contained by one. A
  symlink/junction always becomes `review_required` with reason
  `filesystem_link` and is never traversed.
- `discover_candidate_paths(workspace_root, tracked_paths)` returns the sorted,
  de-duplicated, ancestor-collapsed union described below.
- `build_manifest(workspace_root)` calls `git ls-files -z`, reads dirty paths,
  builds every entry, and returns schema version `1`, resolved workspace root,
  scanned Git commit, dirty-path count, sorted entries, and aggregate summary.
- `write_manifest(manifest, output)` creates only the output parent, writes
  UTF-8 JSON to `output.with_suffix(".json.tmp")` using `ensure_ascii=False`,
  `indent=2`, `sort_keys=True`, and a trailing newline, then atomically calls
  `Path.replace(output)`.
- `main(argv=None)` accepts required absolute `--workspace-root` and `--output`
  arguments, validates them, writes the manifest, prints only the aggregate
  summary, and returns `0`.

Candidate discovery is the union of:

- tracked paths that classify as `legacy_llama` when evaluated with an empty
  dirty set, so a dirty Llama file is still inventoried and later reclassified
  as protected;
- exact existing local roots `.venv311`, `gwen_env`, `gui-winui3.bak`,
  `gui-winui3/pri_staging`, `tools/ru_desktop_overlay/out`, `logs`,
  `.pytest_cache`, `pytest_v.log`, `rustup-init.exe`, `resources.pri.xml`,
  `.tmp/vcpkg`, `tmp_verify_fresh`, `tmp_verify_fresh2`,
  `tmp_verify_fresh3`, `eval_results/judge`, `tyc_calm.wav`,
  `src/mini_gpt/reference_text.txt`, and the one-off `scripts/test_*.py`
  paths enumerated in `_REVIEW_REASONS`;
- every tracked Markdown file in `docs/superpowers/plans` and
  `docs/superpowers/specs` except the current architecture-v2 umbrella spec and
  this Plan 1, classified as a dated implementation record for Plan 2 review;
- all current `bin`, `obj`, `__pycache__`, `.pytest_cache`, and `*.egg-info`
  directories found by a bounded `os.scandir` traversal;
- matching Llama/v6.x roots below `checkpoints`, `models`,
  `data/conversations`, and `eval_results`.

The bounded traversal prunes `.git`, `.worktrees`, `.venv`, `omnivoice_env`,
`.venv311`, `gwen_env`, `gui-winui3.bak`, `gui-winui3/pri_staging`,
`tools/ru_desktop_overlay/out`, `logs`, `.tmp`, `checkpoints`, `models`,
`data/conversations`, and `eval_results`. The four legacy-search roots are
scanned separately: stop descending when a path is protected, explicitly
review-required, or legacy, and add review/legacy candidates only. Never follow
symlinks or Windows junctions. Collapse descendants whenever an ancestor is
already a candidate to prevent double-counted bytes.

Metadata scanning uses an explicit `os.scandir` stack with
`follow_symlinks=False`, prunes links/junctions, and catches `OSError`. Record
only the exception class name in optional `scan_error`; never record error text
or file contents. Every entry contains `absolute_path`, `relative_path`,
`classification`, `reason`, `recoverability`, `exists`, `tracked`,
`dirty_or_untracked`, `file_count`, `size_bytes`, `modified_utc`, and
`active_reference`. `active_reference` is exactly one of `protected`,
`git_dirty_or_untracked`, `tracked_legacy_name`, `active_build_reference`, or
`none`.

The summary contains `entry_count`, `file_count`, `size_bytes`, and
`by_classification`. The CLI rejects relative paths, a drive-root workspace,
non-directories, non-JSON output, and output outside
`Path(__file__).resolve().parents[2] / "artifacts" / "cleanup"`. This permits
scanning dirty `D:\LLM` while writing only to the isolated checkout. The module
must contain no deletion function or destructive operation.

- [x] **Step 4: Ignore generated cleanup artifacts**

Append to `.gitignore`:

```gitignore
artifacts/cleanup/
```

- [x] **Step 5: Run focused tests**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_cleanup_policy.py tests\test_cleanup_inventory.py
```

Expected: all tests PASS.

- [x] **Step 6: Compile the maintenance modules**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m py_compile tools\maintenance\cleanup_policy.py tools\maintenance\cleanup_inventory.py
```

Expected: exit code `0` with no output.

- [x] **Step 7: Commit the inventory tool**

```powershell
git add .gitignore tools/maintenance/__init__.py tools/maintenance/cleanup_policy.py tools/maintenance/cleanup_inventory.py tests/test_cleanup_policy.py tests/test_cleanup_inventory.py
git diff --cached --check
git commit -m "feat: add read-only cleanup preflight"
```

If `tools/maintenance/__init__.py`, `cleanup_policy.py`, or its test was already committed by Task 3, `git add` simply leaves those unchanged; inspect staged scope before committing.

---

### Task 5: Generate preflight evidence and run the plan acceptance gate

**Files:**
- Generate, do not commit: `<isolated-worktree>\artifacts\cleanup\ru-architecture-v2-preflight.json`
- Create: `docs/validation/architecture-v2-cleanup-preflight.md`

**Interfaces:**
- Consumes: `cleanup_inventory.py` and the actual `D:\LLM` workspace, including ignored assets and dirty paths.
- Produces: exact machine-readable manifest plus a non-sensitive tracked summary that Plan 2 uses as its deletion input.

- [ ] **Step 1: Generate the actual-workspace manifest**

From the isolated worktree, run the tool with the main workspace as the scan root:

```powershell
$refactorRoot = (Get-Location).Path
$manifestPath = Join-Path $refactorRoot 'artifacts\cleanup\ru-architecture-v2-preflight.json'
& D:\LLM\.venv\Scripts\python.exe -m tools.maintenance.cleanup_inventory `
  --workspace-root D:\LLM `
  --output $manifestPath
```

Expected: exit code `0`; only the ignored worktree artifact is written and no
file in `D:\LLM` is moved, deleted, or created.

- [ ] **Step 2: Validate manifest privacy, schema, and protected paths**

Run this read-only validation:

```powershell
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema_version -ne 1) { throw 'Unexpected cleanup manifest schema.' }
if (@($manifest.entries).Count -ne $manifest.summary.entry_count) { throw 'Cleanup entry count drift.' }
$protected = @($manifest.entries | Where-Object classification -eq 'protected')
if ($protected.Count -eq 0) { throw 'No protected paths were recorded.' }
$raw = Get-Content -LiteralPath $manifestPath -Raw
foreach ($forbidden in @('Authorization: Bearer','api_key":"','chat text','audio_b64')) {
    if ($raw.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) { throw "Private manifest content: $forbidden" }
}
$manifest.summary | ConvertTo-Json -Depth 5
```

Expected: summary only, with protected paths present and no private content markers.

- [ ] **Step 3: Record count-only preflight evidence**

Create `docs/validation/architecture-v2-cleanup-preflight.md` containing:

- date and Git commit scanned;
- manifest relative path and schema version;
- count and byte total by classification;
- explicit protected groups;
- exact review-required roots, especially `.tmp/vcpkg`, `gui-winui3.bak`,
  mixed evaluation directories, one-off probes, and dated implementation records;
- dirty/untracked path count, not contents;
- exact legacy root paths approved for Plan 2;
- statement that this plan deleted zero files;
- statement that ignored legacy deletion in Plan 2 is permanent and re-runs active-reference/protected-path checks immediately before deletion.

Do not include prompts, dataset rows, credentials, tokens, audio, VTS metadata, or user file contents.

- [ ] **Step 4: Run all new repository tests**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q `
  tests\test_current_documentation.py `
  tests\test_ci_contract.py `
  tests\test_cleanup_policy.py `
  tests\test_cleanup_inventory.py `
  tests\test_backend_decommission.py `
  tests\test_aspnetcore_route_matrix.py
```

Expected: all tests PASS.

- [ ] **Step 5: Run the full Python suite**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
```

Expected: zero failures. The existing third-party `speech_recognition`/`aifc` deprecation warning may remain and is reported separately.

- [ ] **Step 6: Run backend tests**

```powershell
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --nologo
```

Expected baseline: 219 tests pass, zero fail.

- [ ] **Step 7: Run WinUI tests**

```powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo
```

Expected baseline: 94 tests pass, zero fail.

- [ ] **Step 8: Run route, dependency, and whitespace gates**

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe -m pip check
dotnet list backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj package --vulnerable --include-transitive
git diff --check
```

Expected: 38 HTTP + 2 WebSocket routes, no broken Python requirements, no vulnerable NuGet packages, and no whitespace errors.

- [ ] **Step 9: Run the self-contained Release build**

```powershell
cmd /c build.bat
```

Expected: exit code `0`; published `RuBackend.exe` and `RuApp.exe` exist. Do not launch them in this plan.

- [ ] **Step 10: Commit preflight evidence**

```powershell
git add docs/validation/architecture-v2-cleanup-preflight.md
git diff --cached --check
git commit -m "docs: record architecture v2 cleanup preflight"
```

Do not stage the ignored JSON manifest or any main-worktree file.

- [ ] **Step 11: Verify final branch scope**

```powershell
git status --short --branch
git log --oneline --decorate -5
$branchBase = git merge-base HEAD main
git diff "$branchBase..HEAD" --name-status
git -C D:\LLM status --short --branch
```

Expected: isolated refactor worktree clean; commits contain only architecture documents, CI, maintenance policy/inventory, tests, `.gitignore`, and count-only validation. The original `D:\LLM` dirty paths remain unchanged.

## Plan completion output

Report:

- worktree and branch path;
- commit IDs for each task;
- test/build/route/dependency results;
- manifest classification counts and total bytes only;
- protected and review-required groups;
- confirmation that zero files were deleted;
- documents reviewed/created;
- any difference from the expected 401/219/94 baseline;
- no push performed.
