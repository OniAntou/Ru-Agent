# Agent tools and truthful execution

This document is the operating contract for Ru's action execution kernel v1
plus the additive Phase-2 policy authority and selected Phase-3 headless Agent
v2 surfaces (2026-08-31).
It distinguishes a Broker binding that can execute from a capability the model
is actually allowed to select. Those states are not interchangeable.

This checkpoint includes declarative Skills only. Subagent orchestration, LSP,
extension/plugin discovery, and MCP servers are intentionally outside the
runtime surface.

> **Current checkpoint (Agent-only generation, 2026-08-29):**
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
>   `approve --scope once|run|project`/`deny`, `run --preset full` interactive
>   with `X-Interactive-Confirmed: true`; `--json` cannot bypass).
>   Policy/approval/reconcile routes are active on the Agent v2 headless path.
>   `POST /agent/v1/runs/{runId}/reconcile` resolves ambiguous dispatch and
>   reconciliation outcomes. `full` preset requires
>   interactive confirmation and `X-Interactive-Confirmed: true`; `--json`
>   cannot create global rules or confirm trust/approval.
>   Headless terminal requests are available under the same policy/approval
>   boundary; the managed Git commit workflow (Phase 3 Task 6) is not shipped.
>   Typed command dispatch is available through `POST /agent/v1/runs/{runId}/commands`;
>   `/compact`, `/help`, `/skills`, and `/skill <id> {json}` use the shared
>   Protocol parser and do not become model history. Declarative Skills are
>   session-granted and execute through the Broker's existing scope/approval
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
> - Public Contract is now **62 HTTP plus 1 WebSocket** (41 + 20 `/agent/v1/*` plus 1 `POST /host/shutdown`;
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

The Worker v2 settings derive from the immutable Protocol tool-definition
catalog. The model therefore receives the same eleven core bindings (including
offline `help.docs`) plus each
runtime-enabled read-only `web.search`/`web.fetch` binding in Standard and Full
Access; the preset changes Broker policy decisions and fallbacks, not the
advertised list. Managed startup constructs enabled WebOptions with the fixed
DuckDuckGo endpoint `https://html.duckduckgo.com/html/`. The `network_intent` argument on a
terminal request is descriptive input metadata, not a security boundary.

| Worker v2 model capability | Agent (Standard) | Agent (Full Access) |
| --- | --- | --- |
| `files.list@1.0.0`, `files.glob@1.0.0`, `files.search@1.0.0`, `files.read@1.0.0` (`workspace.read`) | automatic | automatic |
| `files.write@1.0.0`, `files.edit@1.0.0` (`workspace.write`) | `allow` for ordinary trusted-workspace create/edit; outside paths ask with `external_directory_required`; `workspace_changed` on preimage mismatch | `allow` fallback (never bypasses trust or invariants) |
| `files.delete@1.0.0` (`workspace.write`, recoverable) | `ask` → approval card + preview; bounded recovery cap; opaque `recovery_id` | ordinary targets use the Full fallback; protected/root targets still ask or deny; recovery remains bounded |
| `files.restore@1.0.0` (`workspace.write`, one-time) | advertised; requires a live recovery record; replay returns `recovery_consumed` | advertised; same one-time recovery constraint |
| `terminal.run@1.0.0`, `terminal.shell@1.0.0` (`terminal.execute`) | advertised; analyzer/rule/approval policy decides each request | advertised; well-formed calls normally use the Full fallback; extended timeouts still ask |
| `system.volume@1.0.0` (`system.write`) | not a model-selection binding; deterministic parser with verified readback | same deterministic direct-action path |
| `notes.create@1.0.0`, `system.time@1.0.0`, `system.status@1.0.0`, `app.list@1.0.0` | Broker-internal; not advertised | Broker-internal; not advertised |
| `web.search@1.0.0` (`network.search`) | advertised read-only; fixed DuckDuckGo endpoint; at most ten bounded citations | same |
| `web.fetch@1.0.0` (`network.fetch`) | advertised read-only; bounded sanitized text | same |
| `help.docs@1.0.0` (`system.read`) | advertised read-only; searches five bundled Markdown sources offline; no workspace, network, or approval | same |
| Connectors, `app.open` | Broker-internal; not advertised | Broker-internal; not advertised |
| Declarative Skills (`skill.execute`) | only explicitly granted manifests; each step rechecks tool scope/approval | same explicit grant and checks |

Standard allows ordinary trusted-workspace reads/creates/edits. An explicit
absolute path outside that root is not silently rejected: the first request
asks for `external_directory_required`. The user can approve only that intent,
the canonical external directory for the current run, or a project-scoped
host-generated descendant rule (filesystem roots are never persisted as a
directory grant); relative traversal still cannot escape the trusted root.
Delete, protected paths, declared network, and unknown operations
keep their own approval gates even after a directory grant;
Full Access changes fallback but never bypasses workspace trust or protocol
invariants and remains a current-Windows-user boundary. Full Access can allow
well-formed terminal and ordinary file calls through its fallback; hard
invariants, protected paths, and extended timeout thresholds still require the
corresponding approval or denial. Durable approvals
(`AllowOnce`/`AllowRun`/`AllowProject`/`Deny`) use first-writer-wins
`approval_stale`, exact scope preview (the model cannot supply a broadening
glob), 30-min expiry,
60-s execution token, and old-snapshot execution ordering: consume one-time
approval under snapshot N → persist reusable rule (or `reusable_rule_not_saved`)
→ execute already-authorized call under N → receipt → N+1 invalidates other
pending N decisions. `network_intent` is descriptive audit/approval metadata,
not network isolation. On the headless Agent v2 path, `terminal.run` and
`terminal.shell` execute as the current Windows user and can run arbitrary host
code or open sockets. Standard usually asks for shell, mutations, unknown
commands, network intent, or other analyzer-risk cases; Full Access normally
allows a well-formed request but still asks for an extended timeout and still
fails hard invariants. Filesystem mutations remain trust- and policy-gated: a
project/run external-directory grant covers only the canonical directory's
read/create/edit operations; delete, protected paths, network, and unknown
operations keep their own gates. The deterministic volume parser is the only
current path that creates a `system.volume` request; it reads back the same
endpoint before reporting success.

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

An explicit, unambiguous volume command uses the deterministic volume parser
before conversational generation. That parser creates the typed
`system.volume@1.0.0` request directly and does not invoke model selection.
Agent text and voice turns share the same execution ownership chain:

```text
WinUI authenticated Agent composer
        -> ASP.NET Core Public Host and session grant
        -> managed C# Worker deterministic volume parser
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

The Public Host derives `model_tools` from the active capability profile,
when a Broker session has been explicitly opened, and publishes the actual
resulting grant as `agent.session`. Managed startup enables the Host web
executor and the default headless model loop advertises both read-only web
bindings through its in-process core gateway. Skill package roots and
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
matrix above shows. On the headless Agent v2 path (Phase 3), `terminal.run`/
`terminal.shell` execute as the current Windows user and can run arbitrary host
code or open sockets; Standard applies analyzer/rule/approval decisions while
Full Access uses its broader fallback for well-formed calls. The managed Git
commit workflow (Phase 3 Task 6) is not shipped; Git commands are ordinary
terminal requests rather than a separate managed capability. Full Access is
intentionally not an OS-enforced filesystem/network sandbox; its `network`
field and `network_intent` are descriptive input validation and audit/approval
metadata, not an operating-system sandbox or network-isolation boundary. A
zero exit is `succeeded`; non-zero exit and start failure are `failed`; timeout
is `timed_out`. `files.write`/`files.edit` are `allow` for ordinary
trusted-workspace create/edit in Standard and otherwise `ask`; Full Access
uses its fallback for ordinary calls while protected paths and invariants stay
gated. Every mutation binds the preimage hash captured by Host preparation and
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
known project root and exposes one static Agent indicator plus one compact
preset selector with `Standard` and `Full Access`; it does not expose a Chat
mode switch or workspace editor. The sidebar keeps only primary conversation
surfaces; Log, Diagnostics, Policy, and Audit remain reachable from the
`Công cụ` flyout. Agent prompts are created through the authenticated
`/agent/v1/*` API and observed over SSE. The inline surface never displays raw
commands, arguments, stdout, or private payload content.

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
