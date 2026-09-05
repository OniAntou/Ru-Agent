# Agent tools and truthful execution

This document is the operating contract for Ru's action execution kernel v1
plus the additive Phase-2 policy authority and selected Phase-3 headless Agent
v2 surfaces (2026-09-05).
It distinguishes a Broker binding that can execute from a capability the model
is actually allowed to select. Those states are not interchangeable.

This checkpoint includes declarative Skills, interactive Windows ConPTY
terminal sessions, session branching/fork/rewind, managed Git commit workflow,
and dynamic model context compaction. The MCP stdio bridge is tested
infrastructure but is not production-composed; connector libraries are not
advertised Agent tools.

> **Current checkpoint (Agent-only generation, 2026-09-05):**
> - Public Host (`RuBackend.exe --managed-runtime`) now owns Worker/Broker in
>   one Job Object; `RuApp` and `RuAgent.Cli` start-or-attach via
>   `RuAgent.Client`. Worker v1 is a device/runtime adapter only; Agent
>   generation runs on the v2 one-step protocol at `/worker/v2` and the
>   `/agent/v1/*` (19 HTTP + SSE `GET /agent/v1/runs/{runId}/events` with
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
>   `approve <run-id> <approval-id>`/`deny`, `run --workspace PATH "prompt"`,
>   with no `--scope`/`--preset` flags exposed; the server picks the right
>   one-time token from the pending binding — session-scoped directory grant
>   for `external_directory_required`, ephemeral terminal token for
>   `terminal_delete_requires_approval` or `terminal_unknown_executable`).
>   `--json` cannot confirm any approval.
>   Policy/approval/reconcile routes are active on the Agent v2 headless path.
>   `POST /agent/v1/runs/{runId}/reconcile` resolves ambiguous dispatch and
>   reconciliation outcomes. `--json` cannot create global rules or confirm
>   trust/approval.
>   Headless terminal requests and interactive Windows ConPTY sessions are available
>   under the policy/approval boundary; the managed Git commit workflow is fully shipped
>   with temporary index isolation and path attribution rollback.
>   Typed command dispatch is available through `POST /agent/v1/runs/{runId}/commands`;
>   `/compact`, `/help`, `/skills`, and `/skill <id> {json}` use the shared
>   Protocol parser and do not become model history. Declarative Skills are
>   session-granted and execute through the Broker's existing scope/approval
>   boundary.
>   Managed startup enables `web.search` and `web.fetch` as read-only
>   capabilities backed by the fixed DuckDuckGo HTML endpoint; results are
>   bounded, sanitized, and explicitly untrusted.
>   Workspace coordination is now opt-in per workspace through the authenticated
>   concurrency/activity/changes endpoints. Shared mode permits disjoint runs;
>   active Ru runs hold renewable workspace membership leases, and external file
>   changes are surfaced as `unknown_external` after an authoritative SHA-256
>   check and durably appended to the encrypted change journal. Terminal
>   `read_only` and `write_set` fields are additive protocol metadata; full
>   terminal write-set enforcement remains gated.
> - Public Contract is now **64 HTTP plus 1 WebSocket** (43 non-Agent + 20 `/agent/v1/*` plus 1 `POST /host/shutdown`;
>   `/voice/stream` is the only public WebSocket).
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

The immutable Protocol catalog contains 13 model-visible definitions: eleven
always-available core bindings (including offline `help.docs`) plus
runtime-enabled read-only `web.search`/`web.fetch`. Managed
startup uses the fixed DuckDuckGo endpoint
`https://html.duckduckgo.com/html/`. The `network_intent` argument on a
terminal request is descriptive input metadata, not a security boundary.

| Capability family | Model exposure | Broker behavior |
| --- | --- | --- |
| `files.list`, `files.glob`, `files.search`, `files.read` | always advertised | automatic inside the trusted or session-granted root |
| `files.write`, `files.edit` | always advertised | ordinary trusted-root changes are allowed; outside roots ask for `external_directory_required`; preimage mismatch returns `workspace_changed` |
| `files.delete`, `files.restore` | always advertised | delete remains recoverable and approval/policy controlled; restore is one-time |
| `terminal.run`, `terminal.shell` | always advertised | recognized safe commands are automatic; deletion and unknown executables require one-time approval |
| `web.search`, `web.fetch` | advertised when managed Web is enabled | bounded, sanitized, explicitly untrusted; search redirect targets are decoded and policy-validated |
| `help.docs` | always advertised | offline, workspace-independent, approval-free search over five embedded documents |
| Declarative Skills | only via explicit session grant and slash command | every step re-enters normal schema, scope, approval, timeout, and output checks |
| MCP and connector libraries | not advertised | experimental/unwired; require a separate production trust and credential design |

The effective Agent policy allows ordinary trusted-workspace
reads/creates/edits. An explicit
absolute path outside that root is not silently rejected: the first request
asks for `external_directory_required`. The user can approve only that intent,
the canonical external directory for the current run, or a project-scoped
host-generated descendant rule (filesystem roots are never persisted as a
directory grant); relative traversal still cannot escape the trusted root.
Delete, protected paths, declared network, and unknown operations keep their
own approval gates even after a directory grant. Hard invariants, protected
paths, and extended timeout thresholds still require the corresponding
approval or denial. Durable approvals
(`AllowOnce`/`AllowRun`/`AllowProject`/`Deny`) use first-writer-wins
`approval_stale`, exact scope preview (the model cannot supply a broadening
glob), 30-min expiry,
60-s execution token, and old-snapshot execution ordering: consume one-time
approval under snapshot N → persist reusable rule (or `reusable_rule_not_saved`)
→ execute already-authorized call under N → receipt → N+1 invalidates other
pending N decisions. `network_intent` is descriptive audit/approval metadata,
not network isolation. On the headless Agent v2 path, `terminal.run` and
`terminal.shell` execute as the current Windows user and can run arbitrary host
code or open sockets. Direct deletion, unknown executables, extended timeouts,
network intent, and other analyzer-risk cases remain approval- or policy-gated.
Filesystem mutations remain trust- and policy-gated: a
project/run external-directory grant covers only the canonical directory's
read/create/edit operations; delete, protected paths, network, and unknown
operations keep their own gates. Time, machine status, app discovery, and
optional volume operations use `terminal.run` or `terminal.shell`; note files
use `files.write`.

`files.apply_patch` is intentionally absent from the runtime capability catalog
and is not model-usable at this checkpoint. Any internal adapter or test fixture
for patch application does not create a public model capability.

## Model selection channel

The model invokes its advertised bindings through the exact selection contract
rendered by System Prompt Composer. Each capability includes ID/version,
description, scope, strict proposal schema, example, and digest; selection is
one JSON object with `tool_id`, `tool_version`, and `arguments`, with no prose
success claim. Both presets receive the same runtime-enabled definitions. Each
step allows at most 10 sequential tool selections; exhausting that budget
forces one final wrap-up answer without tools. Approvals still gate mutating
tools — a card with a bounded preview and an explicit user decision precede
every side effect, in every profile.

Use `help.docs` when the user asks how Ru's own features, architecture,
configuration, tools, providers, or operating boundaries work. It returns
bounded excerpts from the five current bundled documents (`ru-overview`,
`architecture`, `runtime`, `agent-tools`, and `provider-catalog`). Use runtime
context or other system tools for live machine, process, device, and workspace
state. `app.docs` is a product-language alias only; the model-visible contract
contains exactly `help.docs@1.0.0`.

## One execution path

Agent text and voice turns share the same execution ownership chain:

```text
WinUI authenticated Agent composer
        -> ASP.NET Core Public Host and session grant
        -> managed C# Worker model and tool selection
        -> private RuAgentBroker policy and canonical binding
        -> bounded adapter plus readback verification
        -> typed receipt back to Worker
        -> visible reply
```

The Public Host keeps one active Broker lease. It reuses a valid matching
session, and closes the previous lease before opening a different Agent
session. Close or open failure stops before adapter execution; the Broker
continues to reject concurrent owners with `session_conflict`.

The desktop session switch also waits for the previous Harness run's
cancellation commit before creating a run for the replacement session. A
workspace-trust run that was abandoned past its ten-minute trust window is
cancelled during startup recovery, so an interrupted prompt cannot keep the
workspace's active-run lock indefinitely.

Agent turns continue through normal streamed generation. Ambiguous volume
wording produces deterministic clarification instead of an inferred action;
unrelated conversation is never forced through the action parser.

The Public Host derives `model_tools` from canonical model exposure and runtime
availability, while the Broker session owns executable scopes and publishes the actual
resulting grant as `agent.session`. Managed startup enables the Host web
configuration and the default headless model loop advertises both read-only
Web bindings through the Broker gateway. Skill package roots and
`/skills`/`/skill` still require explicit session grants. WinUI renders a
received grant; it does not infer workspace, terminal, Web, connector, or
skill access.

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
next Agent turn, and removed on session replacement, disconnect, or deletion.
It is never written to conversation history, memory extraction, reviewed
examples, TTS, or VTS metadata. This lets Ru answer an immediate “why?” from
the verified reason without asking the model to reconstruct or invent the
previous action state.

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

File and terminal bindings are advertised by the Worker v2 settings as the
matrix above shows. On the headless Agent v2 path, `terminal.run` and
`terminal.shell` execute as the current Windows user and can run arbitrary host
code or open sockets; analyzer/rule/approval decisions remain authoritative.
The managed Git commit workflow is shipped inside terminal execution with
temporary-index isolation, path attribution rollback, and refusal when the
real index already contains staged user changes. The Agent permission model is
not an OS-enforced filesystem/network sandbox; its `network`
field and `network_intent` are descriptive input validation and audit/approval
metadata, not an operating-system sandbox or network-isolation boundary. A
zero exit is `succeeded`; non-zero exit and start failure are `failed`; timeout
is `timed_out`. `files.write`/`files.edit` are allowed for ordinary
trusted-workspace create/edit and otherwise ask; protected paths and invariants
stay gated. Every mutation binds the preimage hash captured by Broker preparation and
fails with `workspace_changed` on mismatch without writing; the hash is not a
model argument. `files.delete` keeps its bounded, host-configurable recovery cap
(default and hard maximum 4 MiB) and returns an opaque `recovery_id`.
`files.edit` accepts LF-authored markers in CRLF files while preserving the
file's newline style. `files.search` uses bounded concurrent reads, propagates
cancellation, and sorts results by ordinal path/line before applying its result
cap. Terminal children receive redirected stdin that is closed immediately;
TTY/editor-style requests remain rejected by canonical command policy.

Declarative Skills are available at session-open only when their validated
manifest IDs are included in the session grant. `/skills` lists only those
grants, and `/skill <id> {json}` resolves the highest granted version before
the Broker rechecks required scopes, tool bindings, schema, step, output, and
timeout limits. A Skill never bypasses policy or approval, and raw step output
is not promoted to visible chat. `SkillEvent` and `SkillResult` failures keep
the same bounded identity/state/error shape as successes, including when the
session is missing, so clients can fail closed without parsing raw exceptions.
Connectors, workspace search, and application launchers remain unadvertised.

## Typed command channel

`POST /agent/v1/runs/{runId}/commands` accepts only the bounded commands
`/compact normal|aggressive`, `/help`, `/skills`, and `/skill <id> <json-object>`.
The shared `AgentActionRouter` rejects unknown or malformed slash commands with
stable `command_unknown`/`command_invalid` codes, without creating a prompt,
history entry, or outbox message. `/compact` reuses the durable compaction
contract; `/help` is static; `/skills` and `/skill` are session-scoped. The CLI
adapter uses the same parser and renders only bounded result metadata.

## Web tool gates

For the managed Agent path, `WebOptions` is enabled with the fixed public
DuckDuckGo HTML endpoint `https://html.duckduckgo.com/html/`. `web.search` is
advertised with `network.search`; `web.fetch` is advertised with
`network.fetch`; the normal Harness policy remains authoritative. Automatic
redirects are disabled or validated hop-by-hop; private, loopback, link-local,
multicast, reserved, and test-net targets are rejected. Responses enforce a
configured byte cap, strict UTF-8/content-type checks, sanitizer limits, and
at most ten validated search citations. Tool results contain only bounded
source/content fields plus an `untrusted` marker; fetched text retains the
`[untrusted_web_reference]` marker. This is a web-tool policy boundary, not
the future OS-enforced filesystem/network sandbox.

## Agent activity shown in WinUI

Agent is the only supported generation path. The desktop composer uses the
known project root and exposes one static Agent indicator; there is no
preset selector or Chat mode switch and no workspace editor. The sidebar
keeps only primary conversation surfaces; Log, Diagnostics, Policy, and
Audit remain reachable from the `Công cụ` flyout. Agent prompts are
created through the authenticated `/agent/v1/*` API and observed over
SSE. The inline surface never displays raw commands, arguments, stdout,
or private payload content.

## Agent permission UX

Every Agent run is session-scoped. The chat composer starts a run with
the broker's effective profile and never asks the user to pick a
permission scope. The Broker is the only authority; Host and the UI only
display the server-selected pending approval kind with one bounded
message per reason code:

- `external_directory_required` — Ru cần dùng thư mục ngoài project
  trong đoạn chat này, cho đến khi ứng dụng đóng. The approval opens a
  session-scoped directory grant in the Broker; the same session
  re-uses the grant on subsequent calls, and new sessions ask again.
- `terminal_delete_requires_approval` — Lệnh terminal này sẽ xóa file.
  The approval returns an ephemeral one-time terminal token, never a
  reusable rule.
- `terminal_unknown_executable` — Ru chưa nhận diện được executable của
  lệnh terminal này. The approval returns an ephemeral one-time
  terminal token.

Recognized Git commands and recognized package install/restore
commands are auto-allowed; direct terminal deletion and unrecognized
executables require a single-use approval even inside the project
root or an approved folder. The CLI exposes the same model with
`agent approve <run-id> <approval-id>` and `agent deny`; there is no
`--scope` flag and no `--preset` flag in the normal flow.

The transcript activity feed is connection-local and transient. Runtime maps the
Core projection to an `agent.activity` SSE frame whose inner JSON contains only
the typed fields `activity_id`, `kind`, `state`, optional safe `file_name`
(basename/line range only), `line_start`/`line_end`, `symbol`, `count`, `added`, `removed`,
`duration_ms`, and bounded `safe_code`. It does not advance the durable event
cursor, append to the Harness event store, or enter session history.

Assistant text uses a separate typed `assistant.delta` transient frame. The
client validates its bounded UTF-8 text and step/execution identities, then
delivers it on the UI dispatcher with `(run, model_step, transient_sequence)`
deduplication. Raw transient JSON and tool-output deltas are never exposed to
the UI or durable audit.

Each activity item exposes one of four visual states: a spinner while started,
a check when completed, an error mark when failed, and an ellipsis while waiting
for approval or continuation. Thinking items may show only their elapsed
duration after completion. File edits and analysis show a basename and optional
line range; search and exploration show bounded symbols or counts. The temporary
user prompt and activity items use separate collections, so durable assistant
history remains unchanged when the feed is cleared.

Before an Agent final reply is persisted, the reply policy strips private
metadata and accepts VTS codes only from the current catalog. The resulting
visible text is the only assistant payload sent to the authenticated client,
model context, and conversation outbox. VTS actions and `agent` assistant
activity are post-commit effects; failures are bounded observations and do not
turn a committed run into a failed run. User activity is projected from its
outbox intent with the same preview-sanitized `agent` shape.

The feed is shown only after the first valid activity frame and is cleared on
terminal completion, cancellation, failure, disconnect, session replacement,
or reset. The inline surface still owns bounded status, workspace trust,
approval, Stop, and Continue controls. Malformed, unknown, stale, or other
content-bearing transient frames are ignored. Raw commands, arguments, stdout,
or private payload content never reach the feed, durable audit, conversation
history, TTS, VTS metadata, or memory.
The desktop entry point also creates a real 32-character session automatically
for the first Agent prompt when the empty conversation surface has no active
session; users do not need to send a preliminary message.

Agent requests use an opaque `correlation_id`, durable run identity, and SSE
cursor. The UI presents one active run, ignores stale or uncorrelated frames,
and reconnects by replaying durable events without duplicating history. A
disconnect marks an ambiguous request as a bounded retryable error; it never
silently replays a request that might already have committed.

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
`files.write` (and file delete/restore) only inside a fresh temporary
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
