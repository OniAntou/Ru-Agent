# Agent tools and truthful execution

This document is the operating contract for Ru's action execution kernel v1
plus the additive Phase-2 policy authority and selected Phase-3 headless Agent
v2 surfaces (2026-08-29).
It distinguishes a Broker binding that can execute from a capability the model
is actually allowed to select. Those states are not interchangeable.

> **Current checkpoint (Phases 1–3 selected surfaces, 2026-08-29):**
> - Public Host (`RuBackend.exe --managed-runtime`) now owns Worker/Broker in
>   one Job Object; `RuApp` and `RuAgent.Cli` start-or-attach via
>   `RuAgent.Client`. Normal Chat remains on Worker v1; harness runs are
>   feature-flagged at `RuBackend:AgentHarness:Version` (default `v1`), with
>   additive Agent v2 at `/worker/v2` (one-step `AssistantStep`) and
>   `/agent/v1/*` (13 HTTP + SSE `GET /agent/v1/runs/{runId}/events` with
>   `id:<sequence>`/`Last-Event-ID`/`after_sequence` cursor, reconnect replay).
>   This checkpoint exposes Broker-backed model tools (`files.list`,
>   `files.glob`, `files.search`, `files.read` offset/limit, `files.write`,
>   `files.edit`, `files.delete`, one-time
>   `files.restore`) through a durable Core (state machines incl.
>   `tool.execution_requested`, SQLite WAL `agent_harness.db`, AES-GCM
>   payloads, idempotent conversation outbox, workspace trust,
>   session permissions with monotonic version, encrypted `PolicySnapshot`
>   snapshots, `FileRecoveryStore`/`RunChangeSetStore`, approval ordering
>   `consume → persist rule → execute under N → receipt → N+1` with
>   `reusable_rule_not_saved` still permitting once-only execution) and a thin
>   `RuAgent.Cli` tracer (safe envelope only, NDJSON `--json` `v:1`, no private
>   payload auto-fetch, `policy list`/`import --global FILE`/`remove`,
>   `approve --scope once|run|project`/`deny`, `run --preset full` interactive
>   with `X-Interactive-Confirmed: true`; `--json` cannot bypass).
>   Policy/approval/reconcile routes are active on the Agent v2 headless path.
>   `POST /agent/v1/runs/{runId}/reconcile` resolves ambiguous dispatch and
>   reconciliation outcomes. `full` preset requires
>   interactive confirmation and `X-Interactive-Confirmed: true`; `--json`
>   cannot create global rules or confirm trust/approval.
>   Headless terminal requests are available under the same policy/approval
>   boundary; the managed Git commit workflow (Phase 3 Task 6) is not shipped.
> - Public Contract is now **54 HTTP plus 2 WebSocket** (41 + 13 `/agent/v1/*`).
> - Worker v2, Harness Core/Persistence (`PolicySnapshot`/`PolicyMatcher`/
>   `SqlitePolicyRuleStore`/`PolicySnapshotLoader`/`WorkspaceTrust`,
>   `FileRecoveryStore`), `RuAgent.Hosting`, `RuAgent.Client`, `RuAgent.Broker`
>   (`CanonicalPathResolver`/`ProtectedPathCatalog` `v1`, `PolicyContext`/
>   `PolicySnapshotEvaluator`, `PendingApprovalStore`/`ApprovalTokenService`,
>   `FileList`/`Glob`/`Search`/`Read`/`Write`/`Edit`/`Delete`/`Restore`
>   adapters, `FileHashService`/`AtomicFileReplacement`/`FileMutationJournal`),
>   and `RuAgent.Cli` (`PolicyJsonCodec` versioned, exact scope preview, no
>   glob broadening) are covered in `docs/architecture.md`, `CONTEXT.md`,
>   `backend-dotnet/README.md`, and `backend-dotnet/RuAgent.Cli/README.md`.

## Current capability matrix

Model advertisement derives from the session capability profile: a binding is
visible to the model exactly when its manifest scopes fit the active profile's
scope set. Chat grants `{workspace.read}`; Agent adds `{workspace.write}` and
`system.write`. The headless Agent v2 path also advertises the
`terminal.execute`/`terminal.network` scopes; Full Access adds those scopes.
The `network` argument on a
terminal request is descriptive input metadata, not a security boundary.

| Capability | Chat | Agent | Full Access |
| --- | --- | --- | --- |
| `files.list@1.0.0`, `files.glob@1.0.0`, `files.search@1.0.0`, `files.read@1.0.0` (`workspace.read`) | automatic | automatic | automatic |
| `files.write@1.0.0`, `files.edit@1.0.0` (`workspace.write`) | — | Standard `allow` for ordinary trusted-workspace create/edit; an absolute path outside the workspace asks with `external_directory_required` and may be granted for one call, the current run, or the project; `workspace_changed` on preimage mismatch, `invalid_utf8`/`content_too_large` before dispatch | Full Access `allow` fallback (never bypasses trust); protected paths and destructive/network/unknown actions still follow their own approval gates |
| `files.delete@1.0.0` (`workspace.write`, recoverable) | — | `ask` → approval card + preview, including after an external-directory grant; `recovery_too_large` beyond 1 MiB remains intact, opaque `recovery_id` | `ask` → same; Full Access never bypasses `recovery_too_large` or workspace trust |
| `files.restore@1.0.0` (`workspace.write`, one-time) | — | advertised only while active run owns valid `recovery_id`; success consumes once (`recovery_consumed` on replay, `recovery_target_exists` if recreated) | same |
| `notes.create@1.0.0` (`workspace.write`) | — | approval card + one-time token | approval card + one-time token |
| `terminal.run@1.0.0`, `terminal.shell@1.0.0` (`terminal.execute`) | — | headless Agent v2: explicit approval for every request; current Windows user; arbitrary host code/sockets | explicit approval required for every request (same boundary) |
| `system.volume@1.0.0` (`system.write`) | — | yes, verified readback required | yes, verified readback required |
| `system.time@1.0.0`, `system.status@1.0.0`, `app.list@1.0.0` (`system.read`) | Broker-internal | Broker-internal | Broker-internal |
| Web search/fetch, connectors, `app.open` | absent | absent | absent |
| Skills | disabled | disabled | disabled |

Standard allows ordinary trusted-workspace reads/creates/edits. An explicit
absolute path outside that root is not silently rejected: the first request
asks for `external_directory_required`. The user can approve only that intent,
the canonical external directory for the current run, or a project-scoped
host-generated descendant rule (filesystem roots are never persisted as a
directory grant); relative traversal still cannot escape the trusted root.
Delete, protected paths, declared network, and unknown operations
keep their own approval gates even after a directory grant;
Full Access changes fallback but never bypasses workspace trust or protocol
invariants and remains a current-Windows-user boundary. Durable approvals
(`AllowOnce`/`AllowRun`/`AllowProject`/`Deny`) use first-writer-wins
`approval_stale`, exact scope preview (the model cannot supply a broadening
glob), 30-min expiry,
60-s execution token, and old-snapshot execution ordering: consume one-time
approval under snapshot N → persist reusable rule (or `reusable_rule_not_saved`)
→ execute already-authorized call under N → receipt → N+1 invalidates other
pending N decisions. `network_intent` is descriptive audit/approval metadata,
not network isolation. On the headless Agent v2 path, `terminal.run` and
`terminal.shell` are available and execute as the current Windows user: they can
run arbitrary host code and open sockets; Full Access advertises the terminal
bindings but both `terminal.run` and `terminal.shell` require a fresh explicit
approval for every request. Filesystem mutations remain trust- and
approval-gated: a project/run external-directory grant covers only the
canonical directory's read/create/edit operations; delete, protected paths,
network, and unknown operations keep their own approval gates. `native_pending`
means the volume binding has a native Windows adapter and deterministic
contract/adapter evidence, but real endpoint and GUI acceptance are still
pending until the user explicitly runs them.

`files.apply_patch` is intentionally absent from the runtime capability catalog
and is not model-usable at this checkpoint. Any internal adapter or test fixture
for patch application does not create a public model capability.

## Model selection channel

The model invokes its advertised bindings through a structured selection
loop, activated in all modes: Chat for its read-only set, Agent for its
workspace/system set, and Full Access for the terminal plus the rest of the
matrix above. Each turn allows at most 10
sequential tool selections; exhausting that budget forces one final wrap-up
answer without tools. Approvals still gate mutating tools — a card with a
bounded preview and an explicit user decision precede every side effect, in
every profile. The `hint` strings inside the advertisement are static usage
shapes: prompt material for the model, never executable schemas.

## One execution path

An explicit, unambiguous volume command uses the deterministic volume parser
before conversational generation. That parser creates the typed
`system.volume@1.0.0` request directly and does not invoke model selection.
Normal chat and Agent mode share the same execution ownership chain:

```text
WinUI authenticated chat
        -> ASP.NET Core Public Host and session grant
        -> Runtime Worker deterministic volume parser
        -> private RuAgentBroker policy and canonical binding
        -> bounded adapter plus readback verification
        -> typed receipt back to Worker
        -> visible reply
```

The Public Host keeps one active Broker lease. It reuses a valid matching
session, and closes the previous lease before opening a different chat session.
Close or open failure stops before adapter execution; the Broker continues to
reject concurrent owners with `session_conflict`.

Ordinary turns in both normal chat and Agent mode continue through normal
streamed generation. Ambiguous volume wording produces deterministic
clarification instead of an inferred action; unrelated conversation is never
forced through the action parser.

The Public Host derives `model_tools` from the active capability profile,
requests one session grant covering that profile's scope set, and publishes
the actual resulting grant as `agent.session`. WinUI renders that received
grant; it does not infer workspace, terminal, Web, connector, or skill access.

The Worker emits at most one tool request at a time and at most 10 sequential
tool requests in one conversational turn. It receives only the bounded enabled
capabilities and emits an exact ID/version plus typed arguments. No prose is
execution. A visible success reply is legal only after a matching outer
`succeeded` receipt; native volume additionally requires
`verification_state: verified` and readback from the same endpoint. Timeout,
cancellation, policy denial, malformed context, adapter failure, and failed
readback all terminate that action with a deterministic failure receipt.

A non-success outcome, including a failure or clarification, is also kept as
one bounded in-memory value. It is isolated by session, consumed once by the
next turn, and removed on session replacement, disconnect, or deletion. It is
never written to chat history, memory extraction, reviewed examples, TTS, or
VTS metadata. This lets Ru answer an immediate “why?” from the verified reason
without asking the model to reconstruct or invent the previous action state.

Legacy `[VOLUME: ...]`, `[SEARCH: ...]`, and app-open markers are stripped
from visible output and never translated into desktop side effects; structured
selection is the only action channel. Marker prose cannot launch an app,
mutate Core Audio, or become a success claim.

## Volume and adapter boundaries

The bounded volume operations are `get`, `set` from 0 through 100,
`increase`/`decrease` from 1 through 50, `mute`, and `unmute`. The Broker owns
default-render-endpoint acquisition and uses one disposable endpoint per
operation. A mutation succeeds only when readback matches on the same fixed
endpoint role and ephemeral endpoint hash.

For deterministic Vietnamese commands, `tăng âm lượng lên N` is an absolute
`set` target, while `tăng âm lượng thêm N` is a relative `increase`. Topic-free
shorthand is not promoted into an action from conversation history.

File, notes, and terminal bindings are advertised by capability profile as the
matrix above shows. On the headless Agent v2 path (Phase 3), `terminal.run`/
`terminal.shell` is available under explicit approval; it executes as the
current Windows user and can run arbitrary host code or open sockets. The
managed Git commit workflow (Phase 3 Task 6) is not shipped; Git commands are
ordinary terminal requests rather than a separate managed capability. Full
Access is intentionally not an
OS-enforced filesystem/network sandbox; its `network` field and
`network_intent` are descriptive input validation and audit/approval metadata,
not an operating-system sandbox or network-isolation boundary. A zero exit is
`succeeded`; non-zero exit and start failure are `failed`; timeout is
`timed_out`. `files.write`/`files.edit` are `allow` for
ordinary trusted-workspace create/edit in Standard and otherwise `ask`;
`files.delete`/`files.restore` and protected/outside/network/unknown are always
`ask` (even in Full Access trust still required); `notes.create` and both
terminal bindings retain explicit approval where advertised. Every mutation
requires `expected_sha256` (`missing` or 64-hex lowercase) and fails with
`workspace_changed` on mismatch without writing; `files.delete` refuses
`recovery_too_large` beyond
1 MiB.

Skills are disabled at session-open and execution boundaries. No current
skill package, Web connector, calendar/task/mail connector, workspace search,
or application launcher is advertised to the model.

## Agent activity shown in WinUI

Coding Agent is selected explicitly from the compact mode pill inside the Chat
composer; that dropdown contains only Chat and Coding Agent. Selecting Agent
reveals one compact preset selector with `Standard` and `Full Access`; the UI
uses the known project root and does not expose a workspace editor. The
sidebar keeps only primary conversation surfaces; Log,
Diagnostics, Policy, and Audit remain reachable from the `Công cụ` flyout.
Ordinary Chat prompts continue through the Chat WebSocket, while Agent prompts
are created through the authenticated `/agent/v1/*` API and observed over SSE.
The inline surface never displays raw commands, arguments, stdout, or private
payload content.

The transcript activity feed is connection-local and transient. Runtime maps the
Core projection to an `agent.activity` SSE frame whose inner JSON contains only
the typed fields `activity_id`, `kind`, `state`, optional safe `file_name`
(basename/line range only), `line_start`/`line_end`, `symbol`, `count`, `added`, `removed`,
`duration_ms`, and bounded `safe_code`. It does not advance the durable event
cursor, append to the Harness event store, or enter session history.

Each activity item exposes one of four visual states: a spinner while started,
a check when completed, an error mark when failed, and an ellipsis while waiting
for approval or continuation. Thinking items may show only their elapsed
duration after completion. File edits and analysis show a basename and optional
line range; search and exploration show bounded symbols or counts. The temporary
user prompt and activity items use separate collections, so durable assistant
history remains unchanged when the feed is cleared.

The feed is shown only after the first valid activity frame and is cleared on
terminal completion, cancellation, failure, disconnect, session replacement,
or reset. The inline surface still owns bounded status, workspace trust,
approval, Stop, and Continue controls. Malformed, unknown, stale, or other
content-bearing transient frames are ignored. Raw commands, arguments, stdout,
or private payload content never reach the feed, durable audit, Chat history,
TTS, VTS metadata, or memory.
The desktop entry point also creates a real 32-character session automatically
for the first Agent prompt when the empty Chat surface has no active session;
users do not need to send a preliminary Chat message.

Chat and Agent have separate transport contracts. Chat requests use an opaque
`correlation_id` plus a GUI-only `turn_id`; the GUI presents one active Chat
turn in FIFO order and ignores stale or uncorrelated frames. The bounded
connection queue accepts eight deferred requests and reports
`chat_queue_full` on the correlation that cannot be retained. A backend
session gate serializes history writes, and a disconnect invalidates pending
turns without automatic replay. This protects ordered history when a socket
dies after the server may already have appended a request.

The short-intent resolver may answer only an exact, high-confidence greeting
or acknowledgement locally. Corrections, filesystem/path, search, network,
current-fact, and uncertain text continue through the full model context; the
resolver does not select Agent capabilities. A stale Agent session is recovered
by creating a replacement session and retrying at most once before a run exists.

## Approval, privacy, and diagnostics

External or destructive Broker requests use a short-lived approval bound to
session, request, tool version, canonical path/executable/argv/cwd/operation/
network, snapshot version, and intent hash with 30-min expiry. Consumption is
first-writer-wins `approval_stale` (including `PendingApprovalStore` and Host
`HostApprovalLedger`); graceful `approval_rejected` on Deny; `approval_stale`
on any changed field or expired/moved-workspace replay. The Broker returns the
normalized candidate matcher for `AllowRun`/`AllowProject`; the UI/CLI shows
that exact scope before confirmation. The model cannot broaden a rule with a
glob; for an external-directory approval the Host itself generates a bounded
descendant matcher only after the user chooses run/project scope. Ordering is
`consume one-time approval under snapshot N → persist
reusable rule (or `reusable_rule_not_saved` still permits once-only execution)
→ execute already-authorized call under N → persist final receipt → build/bind
N+1 → invalidate other pending N decisions`; Broker restart before dispatch
invalidates ephemeral decision and causes safe re-evaluation, never adapter
invocation. File tools follow the same ordering with `tool.execution_requested`
persisted before dispatch and `FileRecoveryStore`/`RunChangeSetStore` for
recoverable deletes. The deterministic Broker approval probe uses
`notes.create` (and file delete/restore) only inside a fresh temporary
workspace, approves once, expects the replay to be denied as `approval_stale`,
closes the private session, and removes the workspace. Production WinUI
approval-card behavior remains covered independently by its UI contract tests.

Public Agent events and diagnostics may contain correlation ID, tool
ID/version, terminal status, stable error code, duration, verification state,
the fixed endpoint role, and an ephemeral endpoint hash. They must not contain
arguments, output values, prompts, replies, paths, scripts, stdout/stderr,
credentials, device names, or native exception text.

Pre-dispatch intent diagnostics use only `tool.intent.resolved`,
`tool.intent.clarification`, or `tool.intent.rejected` plus bounded status and
reason metadata. They never record prompt text, parsed arguments, requested or
observed levels, output, device identity, or exception details.

See [Verification](verification.md) for the separate contract, adapter, and
explicit opt-in native evidence classes. Deterministic evidence never proves
that the user's Windows volume or audible output changed.
