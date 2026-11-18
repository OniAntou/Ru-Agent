# Current-checkout deep review — 2026-11-18

Snapshot: `dev`, HEAD `d1a89ed2` ("fix(installer): install into folders named Ru
Agent Desktop and CLI"), working tree clean at audit end. During the audit the
branch moved from `952efbab` to `d1a89ed2` (four committed changes: composer
wrapping, modal scrim, provider-token context meters, installer folder rename).
All findings below were re-checked against `d1a89ed2`. The review itself
modified nothing; the remediation applied on 2026-11-19 is recorded at the end.
Nothing was committed. A pre-existing untracked `.claude/` directory was left
alone.

## Scope and method

Nine independent static audits covered Broker authority; Host/public contract;
Harness orchestration; Harness persistence; providers/runtime model; WinUI GUI;
CLI/TUI + Client + SDK; tooling/CI/installer/docs; and the four commits made
during the audit. This reviewer re-verified every reported finding against the
source call path before recording it, and rejected one candidate finding after
an isolated probe (see "Refuted candidate" below). The review is subsystem
coverage, not a claim that every line was inspected.

Verification actually run in this checkout:

| Check | Result |
| --- | --- |
| `scripts/test_dotnet.ps1 -Configuration Debug` (12 projects) | **10,497 passed, 0 failed, 0 skipped** |
| `dotnet build gui-winui3.Harness -c Release` | 0 warnings, 0 errors |
| `dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64 -p:Platform=x64 -p:SelfContained=true` | 0 warnings, 0 errors |
| `.venv\Scripts\python -m pytest tests -q` | **1 failed**, 286 passed, 1 skipped |
| `.venv\Scripts\ruff check tests scripts tools` | **1 error** (B905) |
| `scripts/check_aspnetcore_route_matrix.py` | pass: 54 HTTP, 0 WebSocket, all mapped routes declared |
| `scripts/check_python_runtime_boundary.py` | pass: 0 interpreter files in product |
| SDK `npm run typecheck`, `npm test`, `npm run build` | pass: 67 tests, build artifacts produced |
| `dotnet list … package --vulnerable --include-transitive` (solution and GUI) | no known vulnerable packages |
| `scripts/benchmark_agent_loop.ps1` (synthetic) | pass: warm-step p95 18–33 ms, prefix reuse 1.0 |
| `git show --check HEAD` / last 20 commits | clean |
| `pip check` | no broken requirements |

Not run: installer compile/install/update smoke (`ISCC`, `installer_smoke.ps1`,
`installer_local_smoke.ps1`), native GUI acceptance, RPC/publish smoke, real
provider calls, browser/docs-site build. Native and real-provider acceptance
remain separate evidence.

## Findings

### P1 — provider context-overflow code is dropped, so overflow compaction never runs

[`ModelStepService.Translation.cs`](../../../backend-dotnet/RuAgent.Runtime.Model/ModelStepService.Translation.cs)
maps provider codes in one table (lines 17–38) and has no
`context_window_exceeded` case; unknown codes fall to
`generation_failed` with the provider's retryability. All three adapters emit
that code deliberately: `OpenAiCompatibleModelProvider.cs` lines 56–60,
`OpenAiResponsesModelProvider.cs` and `AnthropicMessagesModelProvider.cs` via
`ModelContextOverflowError.Code`. The in-process runner preserves the mapped
code (`InProcessModelStepRunner.cs` lines 167–180), so
[`AgentModelInvocation.cs`](../../../backend-dotnet/RuAgent.Harness.Core/AgentModelInvocation.cs)
lines 153–159 never sees `context_window_exceeded` and never throws
`ContextOverflowStepException`; consequently
[`AgentRunCoordinator.cs`](../../../backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs)
line 263 never calls `RecoverContextOverflowAsync`. The checkpoint path has
the same defect: `InProcessModelStepRunner.Checkpoint.cs` lines 54–57 compares
against the already-remapped code, so `checkpoint_context_overflow` is
unreachable.

Impact: a provider that serves less than the pinned/assumed window (the
documented case in [providers.md](../../architecture/providers.md) lines 155–162,
"the run compacts under the same pin") makes the run fail with the generic
`generation_failed` instead of compacting and continuing. The seam is untested:
`ProviderContextOverflowTests` covers adapters only, and Harness tests inject
`ContextOverflowStepException` directly. Confidence: confirmed by call path.
Fix: add a pass-through case (`context_window_exceeded`, retryable `false`) and
an end-to-end regression that feeds a provider 400 whose body matches the
overflow pattern and asserts compaction plus `[HARNESS RECOVERY]`.

### P1 — CI is red on `dev`: Python source-encoding guard

`.venv\Scripts\python -m pytest tests -q` fails
`tests/test_source_encoding.py::test_tracked_sources_have_no_double_encoded_utf8`
on two committed lines of
[`TgPanelsTests.cs`](../../../backend-dotnet/RuAgent.Cli.Tests/TgPanelsTests.cs):
lines 62 and 80 hold a double-encoded copy of the production footer
`↑↓ select · enter open · esc cancel` (UTF-8 bytes decoded as CP1252 and
re-saved) instead of that footer. The
strings are internally consistent, so the CLI test suite still passes; only the
encoding guard catches the corruption. The same `pytest` command is a step in
[ci.yml](../../../.github/workflows/ci.yml) line 33 and in
[release.yml](../../../.github/workflows/release.yml) line 56, so pushes, pull
requests and tag releases are red. Confidence: confirmed (commands above).
Fix: restore the intended glyphs in both fixture lines.

### P1 — CI is red on `dev`: ruff B905

`tools/coding-benchmark/tool_calls.py` line 121 uses
`zip(calls, outcomes if paired else [None] * len(calls))` without `strict=`;
the prescribed gate (`python -m ruff check tests scripts tools`, ci.yml line 36
and release.yml line 59) reports one B905 error with the pinned ruff 0.16.2.
Confidence: confirmed. Fix: `strict=True` (both sequences have equal length by
construction).

### P2 — two more provider codes are swallowed

The same mapping table has no case for `model_unavailable`
(`OpenAiCompatibleModelProvider.cs` line 589, 404 = unknown model or no serving
capacity) or `provider_payment_required` (line 595, HTTP 402). The client
already ships copy for both codes (`RuAgent.Client/AgentFailureCopy.cs` lines
62 and 201), but generation can never surface them; users see
`generation_failed` instead of "pick another model" or "add credit".
Confidence: confirmed. Fix: pass both through as non-retryable, and extend the
pinned mapping test (`ModelStepServiceTests.cs` lines 1086–1115) with these
three codes so the omission cannot recur.

### P2 — crash between `tool.proposed` and `tool.execution_requested` leaves a run that can only fail

The tool pipeline commits proposals while evaluating a batch and commits
`tool.execution_requested` only afterwards in `CompleteExecutionsAsync`
([`AgentToolExecutionPipeline.cs`](../../../backend-dotnet/RuAgent.Harness.Core/AgentToolExecutionPipeline.cs)
lines 102 and 224). On restart,
[`HarnessRecoveryScanner.cs`](../../../backend-dotnet/RuBackend/Harness/Reconciliation/HarnessRecoveryScanner.cs)
lines 217–227 deliberately returns `null` for "proposal exists, nothing was
requested" and leaves the run `Running`. The next drive then rebuilds the model
context from an assistant step whose calls have no results;
`InProcessModelStepRunner.cs` lines 595–598 throws
`model_history_invalid` (non-retryable) during preparation, so the run dies
with a code that describes neither the crash nor the abandoned calls, and the
proposed calls never receive a named receipt. The parked-approval case is
served by the retained-approval rehydrator; a hard kill in this window is not.
Confidence: code path confirmed; frequency depends on crash timing. Fix:
settle undispatched proposals with a failed `not_executed_after_unknown`-style
receipt (or fail the run with a dedicated `recovery_proposals_abandoned` code)
before re-driving; test by seeding `model.step_completed` + `tool.proposed`
with no `execution_requested`.

### P2 — workspace trust is committed before the run transition

`AgentWorkspaceTrustService.cs` line 165 applies the trust decision
(`WorkspaceTrustRequestStore.ApplyAsync`, a separate transaction that consumes
the trust request) and only then commits the run transition at lines
234/292. If that second commit conflicts or fails, the endpoint returns 409 but
the workspace is already trusted/denied and the request id is spent; a retry
hits `trust_stale` (lines 128–132) and the run stays `WaitingWorkspaceTrust`
until expiry/cancel. Impact: partial persistence and a stuck run on a rare
store conflict or concurrent cancel. Confidence: ordering confirmed;
reachability suspected. Fix: one transaction (or compensating retry) for
"trust decision + run transition"; test cancel-vs-trust races.

### P2 — `agent output --json` silently writes raw payload bytes

The command is documented with `[--json]`
([`Program.cs`](../../../backend-dotnet/RuAgent.Cli/Program.cs) line 209,
`RuAgent.Cli/README.md` line 207) and the parser passes the flag
(`AgentCliParser.Policy.cs` line 48), but
[`AgentCommands.Run.cs`](../../../backend-dotnet/RuAgent.Cli/AgentCommands.Run.cs)
`OutputAsync` (lines 153–166) ignores it and writes the fetched payload with
`WriteLineAsync`. In JSON mode stdout then contains a non-JSON line (breaking
the NDJSON purity contract the tests assert for `run --json`), and payload text
— including tool output that may contain ANSI/OSC sequences — is written
unmodified to a terminal. Confidence: confirmed code path. Fix: emit a versioned
JSON envelope in `--json` mode; in text mode sanitize when stdout is a terminal
and keep raw bytes only for redirection.

### P2 — reopened conversations lose their image attachments

The history DTO carries `image_attachment_count` per message
(`SessionDocument.cs`, selected in `SessionStore.HistoryPages.cs` line 174), but
the GUI never reads it: history replay builds rows with
`AttachedPromptDisplay.Apply`, which only restores text document chips
([`AttachedPromptDisplay.cs`](../../../gui-winui3/Services/AttachedPromptDisplay.cs)
lines 19–41) and has no image branch, while live rows restore images through
`ChatModels.cs` lines 449–506. There is no message→payload reference in the
history contract, so the authenticated-client claim in
[host.md](../../architecture/host.md) line 133 ("stored images remain available")
is true only for the live run. Impact: after switching sessions or restarting
the GUI, a user turn shows no image chip; the conversation reads as if no image
was sent. Confidence: high. Fix: surface at least the recorded count as a chip
and extend the history contract with an image reference if full restore is
wanted; test send-with-image → reopen → assert the chip.

### P2 — local installer builds reject their own upgrade (Windows PowerShell 5.1)

`package_release.ps1` line 57 writes `ru-install.json` with `ConvertTo-Json`;
Windows PowerShell 5.1 emits `"Product":  "desktop"` (two spaces), which is the
default shell for `.\scripts\*.ps1` in the docs. The installer's ownership check
([`Ru.iss`](../../../installer/Ru.iss) line 106) searches for the one-space form
`"Product": "desktop"`, so a locally packaged installer refuses to upgrade or
reinstall into its own folder ("This folder belongs to another Ru product").
`installer_local_smoke.ps1` treats the resulting non-zero exit as the expected
lock refusal, so its assertion passes for the wrong reason before the helper
update step fails. CI/release use `pwsh` (PowerShell 7) and are unaffected.
Confidence: confirmed for 5.1. Fix: write `-Compress` JSON or parse the marker
instead of matching a formatted substring.

### P2 — operational docs claim schema v34 while the catalog is v37

`HarnessSqliteSchema.Migrations.cs` lines 75–77 add 34→35, 35→36, 36→37 and
`HarnessSqliteSchema.CurrentVersion` therefore returns 37, confirmed by the
Host's own log string and by `harness.md` line 496 ("schema v37"). But
[architecture.md](../../architecture.md) line 58 says "ending in v34" and
`database-maintenance.md` lines 3/13 says "Schema v34 … migrates supported
v18–v33 data to v34". Operators following the maintenance/backup guidance get
the wrong target version and source range. Confidence: confirmed. Fix: update
the three pages (and the released-floor wording) and add a guard comparing the
documented version with `CurrentVersion`.

### P2 — `agent_payloads` has no non-partial `run_id` index

The released baseline creates only partial indexes
(`HarnessSqliteSchema.Baseline.cs` lines 46–52, both `WHERE context_scope IS NOT
NULL`), and no later migration adds one. Queries that filter by `run_id`
(`HarnessPayloadStore.cs` line 320, `HarnessSessionImageHistory.cs` lines
13–14 and 33–40) cannot use them, and SQLite does not index foreign keys, so
`agent_payloads.run_id … ON DELETE CASCADE` re-scans the table per deleted run.
Payloads are the largest rows (up to 4 MiB each). Impact: session history/image
loads and session deletion degrade quadratically with payload volume; not
measured in this review. Confidence: schema confirmed, cost estimated. Fix: add
`CREATE INDEX ix_agent_payloads_run_kind ON agent_payloads(run_id, kind)` in a
migration and pin `EXPLAIN QUERY PLAN` in a test.

### P3 — contract and documentation drift

- Route count: `public-route-contract.json` has 54 entries and
  `docs/aspnetcore-route-matrix.md` agrees, but
  [`backend-dotnet/README.md`](../../../backend-dotnet/README.md) line 6 still
  says 52, and [host.md](../../architecture/host.md) line 153 says "23 non-Agent
  plus 32 `/agent/v1/*`" while line 86 of the same page says 31.
- Tool count: `AgentToolDescriptorTable` has 29 rows and `coding_v3` advertises
  29, but `docs/operations/agent-tools.md` line 20 says 28,
  [broker.md](../../architecture/broker.md) lines 312/346 say 28 definitions, and
  `docs/adr/0038-tool-descriptor-table.md` lines 13/59 say 28 rows.
  `agent-tools.md` lines 124–126 already carries the correct 29/27.
- `host.md` line 126 advertises a 26 MiB request-body bound for
  `POST /agent/v1/runs`; the effective endpoint bound is
  `MaxJsonBodyBytesAgentRun = 24 MiB` (`AgentV1Endpoints.cs` line 18), so a
  25 MiB body passes the middleware and is then rejected by the endpoint.
- `docs/guide/install.md` and `distribution-and-updates.md` document the new
  install roots, but the currently published `0.1.3-preview.1` package still
  installs under `Programs\Ru\{Desktop,CLI}`; the docs site deploys on `dev`
  pushes before the next release.
- `docs/architecture/broker.md` lines 140–143 still list
  `terminal_timeout_requires_approval` as a canonical ask, but
  `TerminalLimits.StandardApprovalThresholdMs == HardMaxTimeoutMs`, and the
  evaluator denies above the hard maximum first, so that branch cannot fire at
  current constants (the constant's comment documents the knob deliberately).
- `docs/operations/database-maintenance.md` version claims above.

### P3 — Broker metadata and copy that cannot act

- `ToolPolicyEngine.Evaluate` computes `RequiresApproval` from manifest
  risk/flag, but `ToolExecutionService` only consumes `Allowed`; production
  authorization comes from `PolicySnapshotEvaluator`, and
  `ToolCatalog.RequiresApprovalFor` has no production caller. The descriptor
  columns are latent debt: a future row that sets `RequiresApproval=true` will
  not prompt.
- `PendingApprovalStore.BuildMatcherPreview` strips `*` and `?` from canonical
  path, executable, argv and cwd (lines 260–274). A preview of
  `run_process ["Remove-Item", "C:\data\*"]` renders `C:\data\`, not the
  command that will run. Nothing in the Host/client consumes the field today
  (Broker tests only), so this is latent, not a bypass.
- `docs/architecture/broker.md` lines 156–158 describe `list` as skipping
  hidden/binary entries with counts; `FileListAdapter.cs` lines 99–144 only
  skips protected/outside entries.
- MCP output redaction (`McpServerHost.cs` lines 314–322) replaces the literal
  secret only; a server that echoes the credential base64/JSON-escaped puts it
  into the persisted receipt and model context. Limited: the server already
  holds the credential; it is disclosure to history, not escalation.
- Plugin MCP consent shows the endpoint and environment variable *names* but
  not the Credential Manager entry the Broker will read and send
  (`PluginExecutableSurface.cs` lines 138–159; digest does include
  `bearer_credential`/`credential`). A user approving
  `"bearer_credential": "ru-anthropic-key"` sees only "connects to …".

### P3 — Run-event feed robustness

- The SSE reader accepts `Last-Event-ID: t<seq>` (transient) as "no durable
  cursor" and then replays durable events from sequence 0
  (`AgentV1Endpoints.cs` lines 218–259), and a cursor beyond
  `run.LastSequence` is accepted, leaving the stream silently waiting
  (`ReadEventsAsync(runId, lastSent)`). Ru's own clients always send their
  durable `after_sequence`, so impact is limited to generic SSE clients and
  malformed cursors; the docs promise `cursor_mismatch` rejection only for a
  mismatch.
- No keepalive comment is ever written: `HarnessSseWriter.WriteCommentAsync` has
  no caller. The CLI SSE client intentionally disables the HTTP timeout
  (`AgentSseClient.cs` line 30, `Program.cs` line 71) and `AgentRunFeed` has no
  idle deadline, so `ru agent run/attach/chat` can wait indefinitely on a
  half-open loopback connection or a stalled Host without producing a named
  failure. Confidence: code confirmed, occurrence unmeasured.
- `SafeEventRenderer` escapes `safe_summary` but interpolates `event_type`,
  `status`, `payload_ref` and `event_id` raw into stdout
  (`SafeEventRenderer.cs` lines 23/29/34). The Host issues these fields and the
  client does not re-validate them, so this is defense-in-depth rather than a
  current exploit.

### P3 — GUI lifecycle and retention

- `TurnProcessDisclosure` subscribes to the process-wide
  `TranscriptTypography.Instance.PropertyChanged` in its constructor and never
  unsubscribes (`TurnProcessDisclosure.xaml.cs` lines 59–70; `Unloaded` only
  stops animation). Every realized template instance stays reachable for the
  process lifetime; changing transcript typography then calls `UpdateHeader`
  on unloaded controls. `ActivityRowHeadline` shows the correct
  subscribe-on-Loaded/unsubscribe-on-Unloaded pattern.
- Window teardown runs from `Task.Run`, awaits a `DispatcherQueue` enqueue and
  only then disposes `_composition`, which owns the Host process and Broker Job
  (`MainWindow.xaml.cs` lines 739–777, 810–833). If the dispatcher stops
  between `TryEnqueue` returning true and the callback running, the task never
  completes and the Host is left to its idle shutdown. Confidence: suspected
  WinUI shutdown race; needs a native close test.

### P3 — Harness edge cases

- A manual `/compact` (or any commit) between generation admission and
  completion changes the run version; the coordinator discards the paid model
  step (`AgentRunCoordinator.cs` lines 316–322) without retracting streamed
  text, and the next drive bills another call. The step leaves
  `model.step_started` without a completion.
- Cancelling a batch mid-flight can lose receipts for calls that already ran:
  `ToolCallScheduler` only returns receipts on the normal path
  (`ToolCallScheduler.cs` lines 112–119), the pipeline aborts and rethrows
  (`AgentToolExecutionPipeline.cs` lines 109–113), and receipts are committed
  later by `CompleteExecutionsAsync`. Side effects have already happened and
  the model never sees a receipt for them.
- `HarnessRunState.Interrupted` is a terminal state with no production caller
  (`HarnessRunTransition.Interrupt` appears only in the state machine and
  tests); dead state or a missing abort path.

### P3 — Persistence lifecycle

- Deleting a session does not delete that session's
  `agent_session_preset_snapshots` (`HarnessSessionArtifactStore.cs` lines
  187–210 deletes runs, settings and work states only); snapshots expire after
  10 minutes and hold no conversation text, so this is a small retention
  inconsistency.
- `FileRecoveryStore` is fully implemented and tested but not composed into the
  Host (only its own file and tests reference it), while
  `docs/adr/0009-broker-execution-authority.md` and
  `docs/operations/agent-tools.md` describe it as the Host-persisted recovery
  store. The class also uses deferred transactions for read-then-write and
  records `content_length = 0`, so its size cap never applies — worth fixing or
  deleting when it is wired.
- `SqliteSubagentUserApplyStore.ListAsync` reads `LIMIT 25` and then throws
  `subagent_user_apply_invalid` when more than 24 rows exist (lines 71–82).
  Upgraded profiles retain historical rows, so a root with ≥25 could make every
  live/read/drive call for it fail. Suspected: needs a legacy-profile fixture.

### P3 — Tooling and reproducibility

- `scripts/check_python_runtime_boundary.py` filters `bin`/`obj`/`.git`/… by
  `path.parts` of the absolute checkout path (line 66). A checkout whose path
  contains e.g. `bin` produces an empty inventory and a green exit; the
  summary also has no "must not be empty" assertion.
- The repository has no `global.json`, `nuget.config` or lock files, and CI
  restores `dotnet-version: "10.0.x"` plus unpinned `pytest>=8` /
  `packaging>=24` / `httpx>=0.25`, while `docs/operations/verification.md`
  describes deterministic gates.
- `install_gui_shortcuts.ps1` lines 61–64 rewrites an existing Desktop shortcut
  named `Run MiniGPT Dev.lnk` without checking its target; pinned by
  `tests/test_gui_shortcuts.py`.
- Auto-update verifies a SHA-256 taken from the same GitHub release API and
  runs the unsigned installer; the docs disclose that integrity does not imply
  publisher identity, so this is an accepted preview limitation rather than a
  new defect — but it remains the main supply-chain risk if the repository or
  a maintainer token is compromised.
- `RuntimeDescriptorValidator` exists twice (Client and Hosting) and the client
  copy parses the start timestamp culture-sensitively while the Hosting copy
  pins invariant culture and has a regression test for non-Gregorian cultures.

### P3 — product-language consistency

Production UI copy is English, but a few user-visible strings remain
Vietnamese: `SessionUsagePanel.xaml` ("Đang đọc usage…"), the session-delete
fallback in `ApiService.Sessions.cs` line 218, several `BusinessEndpoints`
error messages, `MetadataPolicy.EmptyResponseText`, and the default session
title in `SessionStore`/the schema (`Cuộc trò chuyện mới`). This looks like
leftover copy from a Vietnamese-first build rather than intent.

### Audited the four new commits

`60dcd959`, `8649783c`, `d88dacea`, `d1a89ed2` (composer wrapping/growth,
modal scrim brush, provider-token context meters incl. `ContextBaselineMemory`
and checkpoint-usage filtering, installer folder rename). No P1/P2 defect was
found. Notes: the route/baseline arithmetic is safe (`EstimatedInputTokens` is
validated `> 0` before use, the ratio is bounded 0.5–2.0, the static 64-route
cache holds counts only); `RequestContextReading` keeps a stale `_rawTrigger`
when a later estimate omits one (only cosmetic before the first request); and
the change left two now-unused `AdvisorUsageSafeCode` constants in
`ChatSession.ContextUsage.cs` line 14 and `ChatUsageReader.cs` line 16.

## Refuted candidate

A persistence finding claimed that multi-statement commands executed through
`SqliteCommand.ExecuteNonQueryAsync` (artifact/subagent stores) silently swallow
errors from statements after the first. An isolated probe against the pinned
Microsoft.Data.Sqlite 10.0.11 (sync and async, including a `CREATE TEMP` batch)
threw the second statement's `SqliteException` both times, so the claim is not
reproduced on this version and is not recorded as a finding. The migration code's
reader-drain pattern remains correct for its own reasons.

## Verified-good areas

- One execution authority: the only authority-composing `IToolGateway` is
  `InProcessToolGateway` behind `InProcessToolGatewayFactory`; `ask_user` and
  `advisor` are Host-intercepted before the gateway; hook gateway is a
  decorator, not a second authority.
- Approval binding: single-use `ApprovalTokenService` tokens keyed by hashed
  token, bound to session/tool/version/argument hash/intent hash/snapshot hash;
  retained approvals re-resolve canonical path and argv before consuming a
  token (`decision_stale` on change).
- Broker security: terminal start is suspended→Job Object→resume; web SSRF
  filtering covers scheme/userinfo, private/mapped/NAT64/6to4 ranges, per-hop
  redirect and DNS-rebinding via connect callback; protected-path resolution
  handles alternate data streams, `\\?\`, trailing dot/space, reparse walks and
  hard links.
- Host authentication: bearer-only (query tokens rejected), SHA-256 +
  `FixedTimeEquals`, options bypass limited to OPTIONS-only endpoints, loopback
  bind validation, owner-only descriptor ACL, 413 JSON on oversized bodies.
- Harness: state-machine transitions and terminal semantics match callers;
  receipt-backed verification requires a declared check after the last source
  edit and honours `supersedes`; recovery notes are durable and never soften an
  approval/policy denial; checkpoint admission commits before dispatch.
- Persistence: ordered migration catalog with one atomic immediate transaction,
  version re-read under the write lock, downgrade rejection before mutation;
  AES-GCM with random nonce, tag and AAD binding run/payload/kind/audience, DPAPI
  key store that refuses to create a new key over encrypted rows; no SQL
  injection found (all values parameterized).
- CI/CD hygiene: all actions pinned by commit SHA, least-privilege tokens
  (only the publish job writes, and only a draft release), no
  `pull_request_target`, installer refuses to kill processes and preserves
  profile data on uninstall/upgrade, updates download from an exact
  origin/asset with size/redirect bounds.

## Suggested order of work

1. Fix the two red gates (P1) — the encoding fixture and `zip(strict=True)` —
   and add the provider-overflow mapping with an end-to-end regression.
2. Fix the two remaining provider-code mappings and the harness
   crash-window/history-invalid path; both are cheap and prevent misleading
   terminal codes.
3. Correct the operational docs (schema v37, route/tool/body counts) and add a
   guard that compares documented schema version and route total with the code.
4. Finish the CLI contract (`agent output --json`), GUI image chip on reopen,
   and the installer marker format; each needs one regression test.
5. Schedule measurement-driven follow-ups: payload-index migration, SSE
   keepalive/idle policy, GUI lifecycle leaks, and the local packaging path.

## Remediation applied (2026-11-19)

No commit was made. Of the suggested order below, item 1 (both red gates), the
provider-mapping half of item 2 and item 3 are applied; the harness crash-window
path and every suspected race or capacity item remain open.

Fixed and verified in this checkout:

- `TgPanelsTests.cs` lines 62/80 now carry the intended footer glyphs instead of
  the double-encoded copy; the Python source-encoding guard and the full Python
  suite pass.
- `tools/coding-benchmark/tool_calls.py` uses `zip(..., strict=True)`; the
  prescribed ruff gate passes.
- `ModelStepService.MapProviderException` passes `context_window_exceeded`,
  `model_unavailable` and `provider_payment_required` through unchanged, so the
  Harness overflow-compaction path and the two operator-facing failure codes are
  reachable again; the pinned mapping test now covers all three codes.
- Removed the two unused `AdvisorUsageSafeCode` constants from the CLI (the test
  fixture uses `SessionUsageReplayPolicy.AdvisorUsageSafeCode`), and recorded the
  provider fix in the CHANGELOG Unreleased section.
- Corrected verified counts and versions in current documentation: schema v37 and
  the v18-v36 source range in `architecture.md`, `harness.md` and
  `database-maintenance.md`; 54 routes (31 Agent + 23 non-Agent) in
  `backend-dotnet/README.md` and `host.md`; the 24 MiB effective encoded-body
  bound in `host.md`; 29 definitions / 27 Broker bindings in `agent-tools.md` and
  `broker.md`; a dated ADR 0038 catalog amendment for the `advisor` row.

Verification after the changes:

- `scripts/test_dotnet.ps1 -Configuration Debug`: 12 projects, **10,500 passed,
  0 failed, 0 skipped** (`Runtime.Model.Tests` grew from 148 to 151).
- `.venv\Scripts\python.exe -m pytest tests -q`: **287 passed, 1 skipped**.
- `.venv\Scripts\ruff.exe check tests scripts tools`: pass.
- Documentation guards and the route-matrix guard pass; `git diff --check` is
  clean.

Deliberately left for a scoped follow-up (each needs a decision or evidence that
this review did not establish):

- `agent_payloads(run_id)` index: adding a migration changes the schema version
  and needs its own migration, measurement and docs evidence.
- `agent output --json` wire shape and the GUI image chip on reopen: both need a
  contract decision (payload envelope; message-to-image reference) first.
- Installer marker formatting: the Windows PowerShell 5.1 mismatch is confirmed,
  but the PowerShell 7 marker written in CI was not reproduced here, and a
  half-fix could break release packaging.
- MCP consent credential names, the `list` hidden/binary docs claim and the
  dormant `terminal_timeout_requires_approval` copy: product decisions rather
  than confirmed defects.
- Every item marked suspected (trust ordering, batch cancellation receipts,
  `/compact` during generation, GUI teardown, SSE idle handling): needs
  fault-injection or native acceptance before behaviour changes.

## Limits

Static review plus the deterministic matrix above. Real-provider behaviour,
native WinUI acceptance, installer/ISCC execution and long-running capacity
were not exercised. Several P2/P3 items depend on crash/race timing and are
marked "suspected" accordingly. HEAD moved during the audit; a second local
session was also running tests early in the window, so any timing-sensitive
observation must be repeated on an idle machine.
