---
status: in-progress
date: 2026-10-29
---

# Coding Quality Implementation Plan

> For agentic workers: execute task by task using `superpowers:executing-plans`
> for native execution, or `superpowers:subagent-driven-development` if selected.
> Do not commit or push without the user's request. Subagents must use Luna.

**Goal:** Improve correct patches and unattended completion, semantic navigation,
long-context continuity and reproducible coding-agent evaluation.

**Architecture:** Incremental changes to the existing .NET Host/Harness/Broker.
Keep one execution authority and one durable evidence source. Introduce semantic
tools through an opt-in versioned profile; preserve existing run pins.

**Tech stack:** .NET 10, C#, SQLite, stdio JSON-RPC/LSP, Python benchmark tooling.

**Spec:** The user-approved design is restated under Approved specification below.
The original visualization artifact is absent in this execution environment;
this checked-in open plan preserves its requirements for implementation.

## Approved specification

Improve all six areas identified in the review: verification/recovery, file-tool
usability, semantic code navigation, context continuity, backend maintainability
and performance, and external coding evaluation. Work directly on `dev`, including
previously uncommitted files, as explicitly authorized. No desktop control.

Consolidate verification lifecycle decisions and make outstanding-check guidance
actionable without automatic test equivalence or erasing failures. Preserve the
existing explicit supersession repairs. Improve file/terminal error guidance and
contract-oriented testing workflow without silently rewriting tool arguments.

Add opt-in C#/TypeScript language-server diagnostics, symbols, definitions and
references through Broker, with configured installed executables, bounded framing,
timeouts, cancellation, process-tree cleanup, document identity and named failures.
No silent server downloads. Record the new subsystem in an ADR.

Test constraints and receipt evidence across compaction/restart. Preserve original
output and provenance while making omitted diagnostics accessible. Measure before
changing checkpoint limits, excerpts, tool defaults or concurrency bounds.

Reduce responsibility coupling in the affected coordinator, retry and scheduling
code. Measure preparation/provider/tool/persistence/recovery separately when the
evidence supports it. Compare subagent and single-agent work rather than assuming
delegation helps. Extend existing benchmark/cohort tooling, not a second runner.

Use two evaluation tracks: matched-model harness comparisons where supported and
native-product comparisons. Add multi-file/repository-shaped tasks and explicit
TypeScript type-check requirements. Preserve fixture fingerprints, independent
graders and original historical scores. Unknown usage/cost stays unknown.

## Global constraints

- One authority: `IToolGateway` to `BrokerExecutionRuntime`; no Host tool adapter.
- Provider/model/protocol/tool-profile selections stay pinned for each run.
- Failed, pending, negative and unknown-freshness checks cannot be auto-retired.
- Same cwd and exit zero do not prove semantic equivalence between checks.
- Existing tool-profile inventories remain unchanged; no silent default switch.
- Use installed tooling; missing compilers/servers/competitor clients are unavailable.
- No new paid subscriptions, desktop control, commit, push or policy widening.
- Preserve historical benchmark cohorts; corrections get independent provenance.
- Run one process/loopback matrix at a time and retain first-failure evidence.

## Review focus

1. Cleanup after a passing probe must not force recreating/deleting it forever;
   explicit replacement must retain history and freshness (Task 1).
2. Provider-call IDs may repeat; diagnostics must identify the exact obligation
   without using ambiguous aliases (Task 1).
3. An LSP response for an older file cannot be presented as current diagnostics,
   and cancellation/crash must release owned resources (Tasks 4-5).
4. A critical constraint or middle-of-log failure can be lost across repeated
   compaction; original evidence must remain inspectable (Task 6).
5. A correct candidate from an interrupted run is not unattended success; cohort
   identity mismatch cannot be averaged away (Tasks 3 and 8).

## Task 1: Verification lifecycle and actionable guidance

Files: `backend-dotnet/RuAgent.Harness.Core/Context/RunVerificationEvidence*.cs`,
new `Context/VerificationObligationPolicy.cs` and
`Context/VerificationEvidenceGuidance.cs`, `Prompting/AgentRecoveryNotes.cs`;
tests in `RuAgent.Harness.Core.Tests/Prompting/VerificationSupersessionTests.cs`
and `RuAgent.Harness.Persistence.Tests/ModelContextSourceTests.VerificationSupersession.cs`.

Interfaces: `VerificationObligationPolicy.IsOutstanding(VerificationCheckEvidence,
IReadOnlySet<string>)` and `CanBeSuperseded(VerificationCheckEvidence)` own pure
obligation decisions. `VerificationEvidenceGuidance.Describe(...)` owns bounded
model-facing command/status/cwd/stable-ID guidance. Original receipts remain owned
by the existing reducer and store.

- [ ] Add failing guidance tests: stale check exposes stable ID, canonical cwd
  and replacement eligibility; failed/pending/negative checks expose their actual
  next action, without suggesting supersession. Cap listed checks at eight.
- [ ] Run the new tests and record expected assertion failures.
- [ ] Extract decisions and guidance from evidence projection without a new store.
  Keep explicit supersession and incremental/cold replay behavior identical.
- [ ] Run Core/Persistence suites and existing cleanup, ambiguity and freshness
  cases; update `docs/architecture/context-management.md` and `harness.md`.

## Task 2: Tool repairs and invariant-oriented coding workflow

Files: `RuAgent.Broker/Tools/Files/FileEditAdapter.cs`,
`RuAgent.Protocol/AgentToolDefinitionCatalog.Coding.cs`,
`AgentToolPromptGuidance.cs`, `RuAgent.Harness.Core/Prompting/RuAgentPromptBlocks.cs`;
tests in `RuAgent.Broker.Tests` file-edit coverage and Protocol catalog coverage.

Interfaces: retain all existing write/edit schemas and strict matching semantics.
Improve bounded error hints using observed match/newline/encoding information.

- [ ] Add behavioral cases for failed create followed by intentional edit,
  ambiguous matches, CRLF/LF and encoded source text. Assert files remain intact
  after rejection and the hint identifies a usable repair.
- [ ] Run the cases; retain already-covered behavior and change only demonstrated
  missing guidance. Do not add fuzzy edits or silent overwrite.
- [ ] Consolidate duplicate guidance and keep stateful-test advice concise:
  cancellation, stale generations, isolated snapshots and atomic rejection.
- [ ] Run affected Broker/Protocol/Core tests; update agent-tools operations docs.

## Task 3: Controlled comparisons using the existing benchmark

Files: new `tools/coding-benchmark/comparison.py`, existing `cohort.py`,
`evaluation.py`, `benchmark.py`; new `tests/test_coding_benchmark_comparison.py`.

Interface: `compare_cohorts(left_manifest, left_records, right_manifest,
right_records, *, track)` validates existing cohort records and returns JSON.
`track` is `matched-model` or `native-product`. Match task/prompt/fixture identities,
toolchain, deadlines and attempt matrices. Matched-model also matches provider,
model, effort and protocol; native-product explicitly reports their differences.

- [ ] Write failing tests rejecting mismatched fixture/prompt/deadline/toolchain,
  missing attempts and duplicate attempts. Verify native-product differences are
  labelled, and interrupted-but-correct outcomes remain separate from acceptance.
- [ ] Implement on top of existing record validation; report paired success,
  completion, failures, steps and wall time. Preserve unknown cost/usage.
- [ ] Expose `compare-cohorts` CLI reading JSON inputs and writing a report.
- [ ] Run comparison/cohort/evaluation tests and document exact CLI examples.

## Task 4: Broker-owned LSP transport and configuration

Files: new `RuAgent.Broker/Tools/LanguageServers/` containing
`LanguageServerConfiguration.cs`, `LspConnection.cs`, `LspFraming.cs`,
`LanguageServerHost.cs`; new `RuAgent.Broker.Tests/LanguageServers/` tests;
new `docs/adr/0037-language-server-tools.md` (verify next free ADR number).

Interfaces: `LspConnection.RequestAsync(string method, JsonObject parameters,
CancellationToken)` returns `Task<JsonNode?>`; notifications use a separate
`NotifyAsync`. `LanguageServerHost` owns per-canonical-workspace/language sessions
and implements `IAsyncDisposable`. Configuration is user-owned `~/.ru/lsp.json`
with explicit language, executable and argv; no workspace auto-install scripts.

- [ ] Write fake-server tests for fragmented Content-Length framing, multiple
  frames, oversized/invalid length, out-of-order replies and cancellation.
- [ ] Implement bounded transport: 4 MiB message cap, 120-second request ceiling,
  cancellation notification and bounded shutdown, using existing process-tree
  ownership. Test server exit, initialization failure and cleanup.
- [ ] Validate duplicate language configs, invalid argv, missing executables and
  unsupported negotiated methods. Return named failures without fallback servers.
- [ ] Document defaults/limits and launch policy in the ADR; test explicit allowed
  and denied launch behavior through Broker before exposing a model-visible tool.

## Task 5: Semantic tool contract and end-to-end dispatch

Files: new Protocol `LanguageServerToolContracts.cs` and definitions partial;
`AgentToolProfiles.cs`, Broker `Tools/ToolCatalog.cs`, new
`Tools/ToolExecutionService.LanguageServers.cs`, existing binding/composition and
policy catalogs; tests in Protocol, Broker, provider-profile and Host integration.

Interface: new `code_intelligence` v1 tool with `diagnostics`, `symbols`,
`definition`, `references` operations; new opt-in `coding_semantic_v1` profile
extends the coding inventory. Inputs use workspace paths and zero-based LSP
positions. Results include content hash/version, locations and truncation.

- [ ] Write failing inventory tests: old profiles do not include the new tool;
  new profile survives request projection and restart with its exact pin.
- [ ] Implement schema validation and Broker manifest/executor/digest agreement.
  Launch authorization remains Broker-owned; do not advertise this as a pure read
  that starts a process without evaluating launch requirements.
- [ ] Synchronize opened documents by version/content; reject stale diagnostics.
  Bound results to 128 entries and disclose truncation. Test external URI/path
  validation, server crashes and edits arriving during a request.
- [ ] Run installed real-server smoke checks when available; absence is reported
  separately. Update CONTEXT, agent-tools, Broker/providers docs and CHANGELOG.

## Task 6: Context evidence and diagnostic visibility

Files: `RuAgent.Harness.Core/Context/ToolResultExcerpt.cs`, new
`Context/ToolDiagnosticExcerpt.cs`, existing compaction checkpoint/replay tests,
Persistence context replay tests and terminal output-spool tests.

Interfaces: keep `ToolResultExcerpt.Project(string)` stable for legacy callers.
Diagnostic selection is a bounded supplementary projection with provenance and
omission markers; original output remains accessible through existing cursors.

- [ ] Add failing cases with essential compiler/test diagnostics in the omitted
  middle, Unicode boundaries and huge single lines. Ensure bounded output,
  deterministic projection and intact structured JSON/paging results.
- [ ] Add compaction-then-restart cases preserving user constraints, decisions,
  outstanding checks and linked original output through real context assembly.
- [ ] Implement only measured projection gaps; keep extraction outside coordinator
  and leave checkpoint/excerpt defaults unchanged absent comparison evidence.
- [ ] Run Core/Persistence and spool tests; record deterministic continuity versus
  separately measured model adherence in context-management documentation.

## Task 7: Performance, concurrency and responsibility extraction

Files: affected `RuBackend/Harness/AgentRuntimeLoopService*.cs`,
`Harness/ModelRunner/InProcessModelStepRunner*.cs`, existing Harness diagnostics,
runtime/subagent tests, `tools/agent-performance/` and its operating docs.

Interfaces: retain scheduling/retry public behavior; extract pure provider retry
decisions and worker-admission responsibility where touched. Reuse durable IDs
and existing phase timing instead of adding a parallel tracing pipeline.

- [ ] Add deterministic tests for a long-running tool alongside runnable sessions,
  cancellation while queued, completed worker cleanup and subagent result apply.
- [ ] Retain model preparation/provider-attempt/tool/persistence/recovery phases
  only when measured; test missing intervals, overlap and unknown metrics.
- [ ] Run `scripts/benchmark_agent_loop.ps1` sequentially and retain its 16/32/60
  step samples. Change worker limits only if this evidence demonstrates a problem.
- [ ] Compare single-agent versus delegated workloads on correctness and aggregate
  model work; report latency as secondary. Document remaining unmeasured cases.

## Task 8: Repository-shaped tasks and integrated acceptance

Files: new `tools/coding-benchmark/repository-tasks/` fixtures and task loader
support; `grading.py`, `task_io.py`, `benchmark.py`; new repository-task tests;
`tools/coding-benchmark/README.md`, current capability docs and dated verification.

Interfaces: versioned opt-in `repository` suite, excluded from existing `all`
until explicitly integrated. Per-task editable-path declarations admit only
needed project files; old fixture admission rules remain intact.

- [ ] Add three multi-file tasks (C#, Python, TypeScript) covering dependencies,
  atomic migration and cross-file typing. Assert seed fails and reference passes
  independent regression/preservation groups. Missing `tsc` is unavailable.
- [ ] Verify patch admission rejects grader edits/links and accepts declared source
  and project changes; fingerprint every fixture and effective prompt.
- [ ] Build Host/CLI Release outputs and run full .NET matrix plus affected Python,
  documentation, encoding and route guards, one process matrix at a time.
- [ ] Run an isolated pinned Ru cohort: one attempt per 20 original tasks, then
  targeted repeats with explicit counts. Preserve failures and complete cleanup.
- [ ] Inspect available competitor CLI contracts and access before adapter work.
  Run matched/native comparisons where genuinely available; report unavailable
  access without pretending fixtures are competitor measurements.
- [ ] Record correctness, completion, provider/intervention outcomes, tool errors
  and provenance. Review all affected docs; archive the open plan on completion.

## Plan self-review and handoff

Each approved design area maps to a task; Review focus maps to behavioral tests.
No score target is promised. Existing metrics and cohort validation are reused.
Protocol/process changes receive full integration gates. Routine internal details
may be settled during execution with decisions recorded alongside evidence.

Recommended execution: Native, directly on the authorized working tree; one final
independent Luna review of the implementation. Alternatively use Luna implementer
and reviewer agents per task. Await the user's plan review and execution choice
before product-code changes, as required by `writing-plans`.

## Execution ledger

- Task 1: guidance tests RED (missing cwd/action), GREEN Core 784/784. Shared obligation policy extracted; no automatic supersession.
- Task 6: middle-diagnostic tests RED, GREEN Core 784/784. Head/tail caps unchanged; supplementary diagnostics are bounded and explicitly incomplete.
- Environment: first build failed missing YamlDotNet cache; restore succeeded. Intermediate Protocol compile failure during LSP integration fixed before running behavior tests.
- Ruling: reuse existing compare-cohorts instead of a new comparison runner; code inspection showed the planned new CLI already exists. Cost if wrong: comparison extension rework.
- Execution: Luna owns LSP and benchmark foundations; root owns verification/context and integration. No commits.

- Task 2: occurrence-hint regression RED, GREEN 26 Broker file mutation/multiedit tests. No automatic all-match replacement.
- Task 7: retry-timing regression RED, GREEN Backend34 runner/scheduling tests. Extracted pure retry policy; bounds unchanged.
- Task 6 review: long-line diagnostic marker could fall outside the first 300 chars. Regression RED, fixed bounded window around marker, Core785 GREEN. Cold/warm compaction verification replay test GREEN.
- Tasks 3/8 offline: benchmark58 tests GREEN; C#/Python new repository oracle seed FAIL/reference PASS; TypeScript unavailable without tsc.
- Task 4: LspConnection exit race (reader ended before request registration -> 120 s hang/lsp_timeout instead of lsp_server_unavailable). Regression RED (test hung 2 min); fixed with fenced terminal-error handshake; reader-finished test added; LanguageServers 18/18 x3.
- Task 5: catalog never composed: code_intelligence manifest used adapter language_server missing from ToolRegistry TrustedAdapters (233 Broker failures invalid_tool_manifest; Host tool catalog unusable). Registry regression RED; allowlist entry per ADR 0037 (External risk, approval unchanged); binding stub and Protocol ExpectedOrder updated (29 tools). Broker 1383, Protocol 201 GREEN.
- Task 1/2 follow-up: benchmark triage of 11 failed Kilo runs (spec-clause misses dominant; one run finished with no check after edits; text-only replies under outstanding checks). Added one-time unverified_source_edit hold (cap 1, never a failure path) and code-aware unverified-note reason plus repair-or-[BLOCKED] line. Core RED 3 flow tests -> GREEN 817; family e2e test updated for the extra held step.
- Task 8: coalescer and pager graders over-asserted synchronous loader timing (calls === 0); replaced with exactly-one-loader-call dedup asserts; 4-way oracle per task; historical scores unchanged; fingerprints changed.
- Verification 2026-10-30: full .NET matrix first run stopped at TgSquareWordmarkRasterTests allocation assertion (recurring flake, unrelated); rerun 13/13 projects, 9,162 tests GREEN; Python 268 passed, 1 skipped.
- Requirement checklist: one todo_write item per explicit requirement, closed only after a check; one-time open_todos and completion_review holds on non-empty final replies, never failure paths. Core 843 (26 new) and RuBackend 1558 GREEN; matrix GREEN except recurring wordmark flake; no live measurement by user choice.
- 2026-10-30 ledger: three uncommitted refactors landed in the working tree: the tool descriptor table (ADR 0038), one ordered internal completion policy (harness.md), and advisory verification per ADR 0039, which removes the obligation, supersession, freshness and repair families. Docs updated; no commit.
- 2026-10-30 remembered approvals: the Broker builds each remember plan from the approved PolicyContext (`ApprovalRememberPolicy`); the Host only saves and remembers what it is returned, and derives nothing. Identity-less MCP, computer and language-server allows are ignored. `RememberedApprovalIdentityTests` RED 12 of 20, GREEN 22 of 22. Docs updated; no commit.
