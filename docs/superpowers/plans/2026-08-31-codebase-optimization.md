# Codebase Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce verified runtime/UI/I/O overhead and lifecycle risk while preserving Ru's product behavior, data, and execution authority.

**Architecture:** Optimize existing ownership boundaries in small, independently verified slices. Keep Public Host persistence, private Broker authority, managed Worker protocols, and isolated OmniVoice intact. Consolidate responsibilities only after behavior tests protect their seams.

**Tech Stack:** Windows/PowerShell, .NET 10/C#, ASP.NET Core, WinUI 3, Microsoft.Data.Sqlite, C++20/CMake, Python 3.11–3.14 tooling.

**Status:** Roadmap approved by the user on 2026-08-31. This task is a documentation-only handoff. Concurrent work has already added implementations in `e6fa40c`; the next agent must reconcile existing code/evidence before filling gaps, not restart the roadmap blindly.

## Global Constraints

- Work in the actual `D:\LLM` checkout, or an explicitly chosen isolated worktree based on its current changes.
- Preserve unrelated dirty work. Do not reset, bulk-stage, commit, push, merge, or create a PR without explicit user authorization.
- Keep the current GUI design, features, cloud model/provider choices, and conversation behavior.
- Keep 56 HTTP method/path pairs plus the single public `/voice/stream` WebSocket; Agent text remains HTTP/SSE.
- Keep managed C# Runtime Worker v1/v2, their authentication, error/sequence/cancellation contracts, and Public Host ownership of Worker/Broker children.
- Keep Broker policy, session/run binding, workspace trust, approval consumption, privacy, and protected-path checks.
- Keep audio playback independent of rendering/subtitle work; preserve playback-before-subtitle ordering.
- Do not launch or control RuApp, overlay, VTube Studio, microphone, speaker, or real desktop/device endpoints automatically.
- Do not call paid/live cloud providers, download models, upgrade/uninstall an active environment, or change active credentials.
- Protect models, OmniVoice assets/environment, app data, `data/conversations`, credentials, and active temporary paths.
- No blanket source/test/doc deletion. Inventory consumers and present exact deletion targets before destructive cleanup.
- Run heavyweight Windows suites sequentially. Simple mechanical/doc changes need only proportionate verification.
- If subagents are used, use Luna (`gpt-5.6-luna`); do not dispatch overlapping writers.
- Check project documents after every implementation slice; update current truth, keep historical evidence explicitly dated.
- OS-enforced sandboxing, new tools/features, provider migration, regex search, parallel traversal, and transport rewrites are outside this optimization.

---

## Entry point and baseline

Read this file first, then the indicated subplan; subplans inherit the Global Constraints above. Paths in subplans are repository-relative to `D:\LLM`. Types labeled **new** describe the original design: some now exist after concurrent implementation. Inspect their actual signatures before editing; do not add duplicate modules.

### Resume gate after concurrent implementation

During handoff preparation the checkout moved to `main` (observed HEAD
`c425d0d`), containing `e6fa40c` (`refactor(optimization): phase 1-2 diagnostics,
native, io, gui`). Additional GUI/protocol edits are dirty and belong to another
task. Do not switch branches, undo those changes, or treat the original clean
baseline below as the current state.

- [ ] Compare `git show --stat e6fa40c`, current source, tests and TODO against
  L/N/P/C/O/F acceptance criteria. Preserve implemented modules; add only missing
  behavior/evidence after verification.
- [ ] Recheck all reported test results at the intended delivered revision when
  implementation work resumes; this planning task ran documentation checks only.
- [ ] Preserve the recorded B/W rejection in subplan 05. Do not automatically
  reopen those extractions merely because detailed candidate steps remain here.
- [ ] `ddgs` is now absent from pyproject, but the Torch extra/setup conflict
  remains visible (`>=2.9` versus `2.8.0+cu128`). Complete D1's compatibility
  investigation without claiming that old-environment pip check resolves it.
- [ ] Reconcile current docs and remaining measurement/acceptance gaps before
  marking the overall optimization complete.

Unchecked boxes below are acceptance requirements to reconcile, not proof that
the corresponding code is absent. This note overrides the original starting
order where a task has already been implemented or explicitly rejected.

Audit baseline: branch `codex/test-suite-cleanup`, commit `d32b0a09a94810b4348efd9b53420b135d73e7f9`, clean before these planning documents. Revalidate rather than assuming it is still current:

```powershell
Set-Location -LiteralPath D:\LLM
git status --short --branch
git rev-parse HEAD
git diff --stat
Get-Content CONTEXT.md
Get-Content TODO.md
Get-Content docs/operations/verification.md
```

The preceding cleanup recorded 402 Python and 2,009 .NET tests passing. Those are historical baseline numbers, NOT proof of this optimization. No before/after performance measurements were collected during the audit.

The audit HTML was a temporary review aid; it is not required to execute this durable plan.

## Approved design and evidence

| ID | Observation at baseline | Change direction | Evidence strength |
| --- | --- | --- | --- |
| L | Diagnostic writer cancels, waits 2 seconds, then disposes gates even if task is still active; final drain uses no cancellation. | One owned shutdown path with truthful timeout/quiescence outcome. | Code-confirmed risk; isolated reproducer required. |
| N | Native tests use `assert`; generated Release project defines `NDEBUG`. RGBA frames traverse three transforms. | Always-on pure tests first, then byte-equivalent fused transform. | Disabled checks confirmed; speed gain unmeasured. |
| P | Diagnostics starts polling at initialization while hidden; sessions timer can overlap requests. | Visibility/lifetime ownership and single-flight refresh. | Code-confirmed repeated/overlapping work. |
| C | Controller dispatches each delta to UI and deduplicates; ChatView deduplicates again, concatenates text and forces layout per delta. | Own reply state once; coalesce rendering without delaying voice. | Code-confirmed hot path; measured gain required. |
| O | Idle outbox dispatch probes transitions then opens another logical SQLite connection and write transaction for claim. | One work-selection boundary, read-only idle path. | Code-confirmed work; pooling is already enabled. |
| F | Host and Broker search implementations probe binary prefix, then reopen/read bounded text. | One bounded stream per eligible file; preserve owner-specific limits. | Prefix re-read, NOT two complete file reads. |
| B | Broker transport and execution modules spread lease/token/scope/schema/adapter orchestration. | Consolidate ownership, retain defense-in-depth. | Architecture candidate; repeated checks are not proven redundant. |
| W | Worker v1/v2 repeat connection cleanup mechanics but have distinct framing/concurrency. | Share only equivalent lifetime mechanics. | Architecture candidate; throughput benefit unproven. |
| D | Runtime extra contains ddgs without a found consumer; Torch extra >=2.9 conflicts with setup pin 2.8.0+cu128. | Consumer-driven dependency alignment and current docs. | Declaration conflict confirmed; compatible version not yet selected. |

The 584-file source/tool inventory was a breadth survey, not a claim that every line was deeply reviewed. Memory retrieval scoring, voice buffering, CLI startup, and other unmeasured paths are profiling candidates only; do not invent refactors to claim total coverage.

## Execution order

The six subplans are responsibility-based handoffs; the four approved rollout phases determine when their tasks run.

| Rollout phase | Executable tasks | Exit checkpoint |
| --- | --- | --- |
| 1 — Reliability and measurement foundation | [01 diagnostics](2026-08-31-codebase-optimization-01-diagnostics.md), task N1 of [02 native](2026-08-31-codebase-optimization-02-native.md) | Logger RED/GREEN evidence, Release check failure detected, no live devices. |
| 2 — Actual overhead | [03 GUI](2026-08-31-codebase-optimization-03-gui.md), [04 I/O](2026-08-31-codebase-optimization-04-io.md), N2 of [02 native](2026-08-31-codebase-optimization-02-native.md) | Same output/contracts; measured work counts before/after. |
| 3 — Module ownership | [05 lifetime/authority](2026-08-31-codebase-optimization-05-lifetimes.md) | Policy/cancel/recovery regressions pass; no check removed for speed. |
| 4 — Dependencies, documents, final verification | [06 dependencies/docs](2026-08-31-codebase-optimization-06-dependencies-docs.md) | Consumer manifest, consistent install declarations, current docs, sequential final gates. |

Default next-agent starting point: the resume gate above, then verify phase 1's existing implementation and report missing evidence/next ready tasks. The roadmap authorizes subsequent phases but does not turn rejected candidates, unknown compatibility decisions, device tests, or destructive cleanup into permission.

## Per-task workflow

- [ ] Read the task's existing implementations and consumers before editing; inspect concurrent diffs again.
- [ ] Record the specific bug/overhead and the production mutation that a regression should catch.
- [ ] Run existing focused baseline checks; collect counters before changing hot paths.
- [ ] Add a failing behavioral regression, observe the intended failure, then make the minimal change.
- [ ] Run focused GREEN and affected neighboring contracts. Record command, exit code, test count, and any skips.
- [ ] Measure with identical fixtures/build configuration and no parallel heavy processes.
- [ ] Inspect the diff for privacy, lifecycle, permission, data-loss, and scope regressions.
- [ ] Update the relevant current docs and this task's evidence; do not mark future work complete.
- [ ] Report the slice and pause only for a genuine blocker or newly required authority; do not auto-commit.

For a required new test seam, first extract the real I/O boundary without changing behavior and run characterization tests. A missing symbol/compiler error is not a reproduction of the underlying runtime bug. Never keep only a fake pipeline or a source-string test as proof.

## Evidence and performance policy

Use deterministic fixtures and real implementations with fakes only at external I/O, clocks, dispatch, and device boundaries. Keep temporary roots isolated using `RU_DATA_ROOT` where appropriate. No tests against user sessions.

Measure work counts as primary evidence: accepted/dropped events, outstanding tasks/handles, logical SQLite opens/transactions, bytes read, stream opens, render/layout calls, text allocations, and pixel passes. Distinguish physical I/O from pooled logical connections.

For timing, record build/commit, workload size, warmup, iterations, median and p95; use at least 5 warmups and 30 measured iterations for opt-in synthetic benchmarks. An apparent improvement smaller than run-to-run noise is inconclusive. No invented percentage target or live-quality acceptance claim.

Store only fixture IDs, counts, durations, hashes of synthetic outputs, and bounded reason codes in evidence. No prompts, replies, tokens, device names, user paths, or content.

## Stop conditions

Stop the affected task, preserve completed changes, and request direction if it requires a public contract/schema migration, live device/cloud access, a security check removal, dependency downloads/upgrades not already authorized, destructive cleanup outside an approved manifest, or cannot establish a safe shutdown/cancellation contract. Other independent read-only/planned work may proceed.

A risky candidate rejected after measurement is a documented outcome, not an excuse to implement it anyway.

## Handoff checklist

- [ ] L and N1 completed with actual RED/GREEN evidence.
- [ ] P/C/O/F/N2 completed or individually rejected with measured reason.
- [ ] B/W decisions reconciled: preserve the recorded rejection, or verify every invariant if explicitly reopened; no speculative Broker cancel fix bundled in.
- [ ] D completed or compatibility decision explicitly blocked; no active environment mutation.
- [ ] Current docs/TODO agree with implementation; old evidence remains historical.
- [ ] Sequential final gates from subplan 06 recorded at the delivered checkout.
- [ ] Diff contains only intended source/tests/docs; no generated artifacts or secrets.
- [ ] Report what remains manual/unmeasured; commit/push only on separate request.
