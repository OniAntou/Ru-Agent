# Documentation Refresh and Retirement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Ru's current documentation match the Agent-only runtime and remove documents that are explicitly superseded or duplicate.

**Status:** Historical implementation record. The initial documentation
refresh and retirement completed on 2026-08-29; later current-contract updates
are recorded in the dated follow-up plans and the operations documentation.

**Architecture:** Keep a small current-documentation spine (README, domain vocabulary, architecture, runtime, tools, verification, route contract, and ADRs). Treat dated plans/specs as historical records unless they describe remaining work; mark those records clearly, and delete only files whose own status says superseded or whose content is fully replaced by a focused current contract.

**Tech Stack:** Markdown, PowerShell/ripgrep inventory checks, Python documentation-contract tests, Git.

## Global Constraints

- Agent is the only generation mode and text/voice use authenticated Agent API/SSE.
- The current Worker v2 model-visible list is exactly the ten runtime capabilities in `AgentRuntimeCoordinatorFactory.RuntimeCapabilities`.
- `system.volume` is a deterministic direct-action path, not a Worker v2 model selection capability.
- Full Access changes policy fallbacks but does not create an OS-enforced filesystem/network sandbox.
- Preserve useful historical decision records; do not delete active future work or ADRs.
- Keep links valid and do not place private payloads, commands, prompts, or outputs in current docs.

---

### Task 1: Inventory and classify documentation

**Files:**
- Read: `docs/**/*.md`, `README.md`, `CONTEXT.md`
- Read: `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs`, `backend-dotnet/RuBackend/Harness/Prompting/RuPromptContextProvider.cs`, `backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs`
- Test: `tests/test_current_documentation.py`

- [x] **Step 1: Establish the clean baseline.**
Run `git status --short --branch`, `rg --files docs`, and `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q`. Record the current route/tool assertions before editing.

- [x] **Step 2: Build the retirement manifest.**
Classify explicit superseded files separately from historical records and active future plans. The deletion set is limited to the ASCII meteor design, the superseded volume design and plan, and the superseded combined Agent-only/OpenCode prompt design.

---

### Task 2: Refresh the current documentation spine

**Files:**
- Modify: `README.md`
- Modify: `CONTEXT.md`
- Modify: `docs/README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `docs/operations/verification.md`
- Modify: `docs/adr/0007-mode-capability-profiles.md`

- [x] **Step 1: Rewrite the capability truth.**
Document the exact ten Worker v2 model-visible tools: `files.list`, `files.read`, `files.glob`, `files.search`, `files.write`, `files.edit`, `files.delete`, `files.restore`, `terminal.run`, and `terminal.shell`. State that both Standard and Full Access receive the same advertisement today; Standard keeps stricter policy decisions and Full Access only broadens fallbacks. Move `system.volume` to the deterministic direct-action section and keep Broker-internal `system.*`, `app.list`, and `notes.create` out of the model list.

- [x] **Step 2: Remove stale transport/mode claims.**
Ensure current docs say there is one Agent generation path, no public Chat transport, no composer workspace editor, no automatic Chat fallback, and that the only public WebSocket is `/voice/stream`.

- [x] **Step 3: Keep security and acceptance boundaries explicit.**
Retain the current prompt-composer receipt/privacy rules, approval/trust behavior, terminal current-user boundary, and the separate future OS-enforced sandbox project.

---

### Task 3: Mark historical records and remove superseded files

**Files:**
- Modify: `docs/superpowers/specs/2026-08-20-logging-system-upgrade-design.md`
- Modify: `docs/superpowers/specs/2026-08-20-responsive-winui-layout-design.md`
- Modify: `docs/superpowers/specs/2026-08-21-ru-subtle-shell-composer-rounding-design.md`
- Modify: `docs/superpowers/specs/2026-08-23-mode-capability-profiles-design.md`
- Modify: `docs/superpowers/specs/2026-08-24-ru-opencode-pi-coding-loop-parity-design.md`
- Modify: `docs/superpowers/specs/2026-08-28-chat-dropdown-scrollbar-design.md`
- Modify: `docs/superpowers/specs/2026-08-28-chat-shell-composer-redesign-design.md`
- Modify: `docs/superpowers/specs/2026-08-28-chat-welcome-stability-design.md`
- Modify: `docs/superpowers/specs/2026-08-28-ru-conversation-agent-reliability-design.md`
- Modify: `docs/superpowers/specs/2026-08-29-ru-agent-only-chat-removal-design.md`
- Modify: `docs/superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity.md`
- Modify: `docs/superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity-01-harness-foundation.md`
- Modify: `docs/superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity-02-policy-file-tools.md`
- Modify: `docs/superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity-03-terminal-git.md`
- Modify: `docs/superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity-04-desktop-cutover.md`
- Delete: `docs/superpowers/specs/2026-08-21-ascii-meteor-empty-chat-design.md`
- Delete: `docs/superpowers/specs/2026-08-22-ru-volume-tool-vertical-slice-design.md`
- Delete: `docs/superpowers/plans/2026-08-22-ru-volume-tool-vertical-slice.md`
- Delete: `docs/superpowers/specs/2026-08-29-ru-agent-only-opencode-prompt-composer-design.md`

- [x] **Step 1: Add historical/superseded status banners.**
Mark implemented dated records as historical implementation records, mark the parity program as partially shipped with managed Git and real-model acceptance remaining, and mark Agent-only removal as implemented. Do not rewrite their original evidence.

- [x] **Step 2: Remove only the four explicit superseded documents.**
Delete those exact files and remove all current-index/test links. Replace prose references with a historical “superseded proposal removed; see the replacement” note where a retained record needs context.

---

### Task 4: Update documentation contracts and verify

**Files:**
- Modify: `tests/test_current_documentation.py`
- Modify: `docs/README.md`
- Create: `docs/superpowers/plans/2026-08-29-documentation-refresh-and-retirement.md`

- [x] **Step 1: Adjust contract tests for the new matrix and retirement manifest.**
Assert that the deleted paths are absent, current docs advertise the ten-tool list and describe the deterministic volume path, and no current document claims Standard hides terminal or that Chat is a generation transport.

- [x] **Step 2: Run focused checks.**
Run `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q`, a relative-markdown-link scan, stale-claim scans, and `git diff --check`.

- [x] **Step 3: Run the documentation-safe verification set.**
Run `D:\LLM\.venv\Scripts\python.exe -m pytest -q` and `D:\LLM\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py` sequentially. Inspect `git diff --stat` and the staged diff before committing.

- [x] **Step 4: Commit the refresh.**
The refresh was included in the subsequent runtime/documentation commits; this
plan remains as the historical record of its scope and verification.

## Follow-up cleanup (2026-08-31)

The superseded three-mode capability proposal was no longer needed after the
Agent-only runtime and ADR 0007 became authoritative. These two historical
records were removed after explicit approval:

- `docs/superpowers/plans/2026-08-23-mode-capability-profiles.md`
- `docs/superpowers/specs/2026-08-23-mode-capability-profiles-design.md`

The remaining dated plans/specs are retained as historical decision records or
explicit future work; protected data, active runtime assets, and unrelated
dirty paths were not part of this cleanup.
