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
>   `/agent/v1/*` (20 HTTP + SSE `GET /agent/v1/runs/{runId}/events` with
>   `id:<sequence>`/`Last-Event-ID`/`after_sequence` cursor, reconnect replay).
>   This checkpoint exposes Broker-backed model tools (`read` with
>   `op=list|read|help` plus offset/limit, `glob`/`grep` for bounded discovery,
>   `write` with `op=write`,
>   `edit`, one-time `write` with `op=restore`) through a durable Core (state machines incl.
>   `tool.execution_requested`, SQLite WAL `agent_harness.db`, AES-GCM
>   payloads, idempotent conversation outbox, workspace trust,
>   session permissions with monotonic version, encrypted `PolicySnapshot`
>   snapshots, `FileRecoveryStore`/`RunChangeSetStore`, approval ordering
>   `consume one-time approval → execute under N → receipt → N+1` with no
>   reusable rule persisted; every approval is `allow_once`,
>   `allow_session_directory` (external folders only), or `deny`) and a thin
>   `RuAgent.Cli` tracer (safe envelope only, NDJSON `--json` `v:1`, no private
>   payload auto-fetch, `policy list`/`import --global FILE`/`remove`,
>   `approve <run-id> <approval-id>`/`deny`, `run --workspace PATH "prompt"`,
>   with no `--scope`/`--preset` flags exposed; the Host accepts exactly
>   `allow_once`, `allow_session_directory` (external folders only), and
>   `deny` — the server picks the right one-time token from the pending
>   binding — session-scoped directory grant
>   for `external_directory_required`, ephemeral token for
>   `terminal_delete_requires_approval`,
>   `terminal_external_mutation_requires_approval`,
>   `terminal_timeout_requires_approval`, `web_approval_required`, or
>   `terminal_unknown_executable`).
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
>   Managed startup enables standalone `web_search` and `web_fetch` as
>   approval-gated capabilities backed by Google HTML search with DuckDuckGo
>   HTML fallback; results are bounded, sanitized, and explicitly untrusted.
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
>   `FileList`/`Glob`/`Search`/`Read`/`Write`/`Edit`/`Delete`/`Restore` file
>   adapters behind the `read`/`write`/`edit` ops,
>   `FileHashService`/`AtomicFileReplacement`/`FileMutationJournal`),
>   and `RuAgent.Cli` (`PolicyJsonCodec` versioned, exact scope preview) are
>   covered in `docs/architecture.md`, `CONTEXT.md`,
>   `backend-dotnet/README.md`, and `backend-dotnet/RuAgent.Cli/README.md`.

## Current capability matrix

The immutable Protocol catalog contains 8 model-visible definitions (`read`,
`glob`, `grep`, `write`, `edit`, `bash`, `web_search`, `web_fetch`). `read`
carries an `op` discriminator (`list`, `read`, `help`); `glob` takes `pattern`
(required) plus optional `path` and `max_results` (1–64, default 64) and returns
stable sorted relative paths without content access; `grep` takes `query`
(1–512 chars, required) plus optional `path`, `file_glob`, `case_sensitive`
(default false), `is_regex` (default false) and `max_results` (1–64, default
64), and returns bounded match locations without raw file content; `write`
carries `op` (`write`, `delete`, `restore`); `bash` carries `mode`
(`run`, `shell`).
`web_search` takes `query` (1–512 chars) plus optional `max_results` (1–10,
default 10); `web_fetch` takes `url`. Both are standalone tools with their own
`network.search`/`network.fetch` scopes and always require one-time approval.
Google HTML (`https://www.google.com/search`) is the primary search engine
with DuckDuckGo HTML (`https://html.duckduckgo.com/html/`) as automatic
fallback; a consent/block page with no usable result anchors fails closed as
`web_search_parse_failed` on the last engine. Fetch keeps the pre-existing
bounded, sanitized, explicitly-untrusted behavior. The `network_intent`
argument on a bash request is descriptive input metadata, not a security
boundary.

| Capability family | Model exposure | Broker behavior |
| --- | --- | --- |
| `glob`, `grep` | always advertised | automatic inside the trusted or session-granted root |
| `read` with `op=list`, `op=read` | always advertised | automatic inside the trusted or session-granted root |
| `write` with `op=write`, `edit` | always advertised | ordinary trusted-root changes are allowed; outside roots ask for `external_directory_required`; preimage mismatch returns `workspace_changed` |
| `write` with `op=delete`, `op=restore` | always advertised | automatic in a trusted or session-granted root; delete is recoverable and restore is one-time |
| `bash` with `mode=run`, `mode=shell` | always advertised | recognized safe commands are automatic; deletion and unknown executables require one-time approval |
| `web_search`, `web_fetch` | advertised when managed Web is enabled | approval-gated (`web_approval_required`), bounded, sanitized, explicitly untrusted; empty results fall back Google → DuckDuckGo, redirect targets are decoded and policy-validated |
| `read` with `op=help` | always advertised | offline, workspace-independent, approval-free search over five embedded documents |
| Declarative Skills | only via explicit session grant and slash command | every step re-enters normal schema, scope, approval, timeout, and output checks |
| MCP and connector libraries | not advertised | experimental/unwired; require a separate production trust and credential design |

### Session folder grants and bash approval

Ordinary tools run automatically in the trusted root. An explicit absolute
path outside that root asks once for `external_directory_required`; approval
adds the canonical folder to the current live session and all tools may use it
subject to schemas, protected paths, and hard invariants. The folder grant is
not shared with another or forked session and is lost when the app restarts.
Continuing the same live session reuses it. Relative traversal cannot escape a
trusted root. When the user supplies an absolute path inside a granted folder,
the model must preserve that exact path instead of rewriting it relative to the
trusted workspace. When executing `bash` with `mode=run` or `mode=shell` with a
working directory inside a session-granted directory, the Broker selects an
effective root toolset (`RootToolSet`) rooted at that granted directory.
Confinement remains enabled against that root toolset so child commands cannot
escape the granted root, path formatting uses the selected root, and
external-root terminal adapters share the Broker's approval-token authority for
unknown or destructive commands. The implementation never disables confinement
globally.

Only `bash` commands with `mode=run` that perform destructive deletion, external mutation
(network egress, package installs, push/fetch/pull), run longer than the
standard timeout window, or launch an unknown executable ask for a one-time
terminal approval; every `bash` script with `mode=shell` asks. Recognized read-only,
test,
build, lint, and local Git run commands are not dangerous solely by command name
(see `TerminalAutoPolicy`, consumed by the evaluator fallback and enforced by
the adapter execution leg).
Terminal calls
still run as the current Windows user and may execute host code or open sockets;
`network_intent` is audit metadata, not network isolation. Current time,
machine/process status, application discovery, and optional volume commands use
an advertised terminal tool. Note-file creation uses `write` with `op=write`.

`files.apply_patch` is intentionally absent from the runtime capability catalog
and is not model-usable at this checkpoint. Any internal adapter or test fixture
for patch application does not create a public model capability.

## Model selection channel

The model invokes its advertised bindings through the exact selection contract
rendered by System Prompt Composer. Each capability includes ID/version,
description, scope, strict proposal schema, example, and digest; selection is
one JSON object with `tool_id`, `tool_version`, and `arguments`, with no prose
success claim. The Worker also accepts that exact object inside one
attribute-free `<tool_call>...</tool_call>` wrapper or an attribute-free leading
`<tool_call>` followed by a complete JSON object through the end of the response
for compatibility with providers that emit textual tool markup; malformed, nested,
prefixed, or suffixed wrappers remain prose and never execute. There is a single
runtime policy profile; the preserved `standard`/`full_access` wire values map to
the same enabled definitions. Each
step allows at most 10 sequential tool selections; exhausting that budget
forces one final wrap-up answer without tools. Approvals still gate mutating
tools — a card with a bounded preview and an explicit user decision precede
every side effect, in every profile.

An exact consecutive repeat of the same tool ID, version, and canonical
arguments terminates the run with `repeated_tool_call` before a second Broker
dispatch. This applies to read-only calls as well as mutations and terminal
commands, preventing model loops from duplicating side effects.

Use `read` with `op=help` when the user asks how Ru's own features, architecture,
configuration, tools, providers, or operating boundaries work. It returns
bounded excerpts from the five current bundled documents (`ru-overview`,
`architecture`, `runtime`, `agent-tools`, and `provider-catalog`). Use runtime
context or an advertised terminal tool for live machine, process, device, and
workspace state. There is no second docs alias or fallback tool; the only
model-visible bundled-help contract is `read` with `op=help` on tool
`1.0.0`.

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
workspace's active-run lock indefinitely. The same rule covers approvals: a run
still parked in `WaitingApproval` past the 30-minute approval lifetime fails as
`approval_expired` (startup recovery and the running host's drive loop), because
no client can resolve an older approval — the endpoint refuses it as
`approval_stale`. The gated tool call is pre-dispatch, so failing that run cannot
roll back a side effect. A `409 workspace_busy` (or the session-scoped variant)
names the run that owns the resource in `blocking_run_id`/`blocking_run_status`,
so a client can point the operator at the exact run to cancel
(`ru agent cancel <run-id>`) or attach to (`ru agent attach <run-id>`).

While a run is actively driven, a dedicated heartbeat renews its workspace
membership independently of model and tool step duration. A failed renewal
cancels the active step and fails closed with `workspace_membership_expired`;
slow provider responses alone do not let a healthy membership expire.

Agent turns continue through normal streamed generation. Unrelated
conversation is never forced through a tool-selection path.

The Public Host derives `model_tools` from canonical model exposure and runtime
availability, while the Broker session owns executable scopes and publishes the actual
resulting grant as `agent.session`. Managed startup enables the Host web
configuration and the default headless model loop advertises both read-only
Web bindings through the Broker gateway. Executable Skill package roots and
`/skills`/`/skill` require explicit session grants; Markdown skill roots are
prompt guidance and are not executable grants. WinUI renders a received grant;
it does not infer workspace, terminal, Web, connector, or skill access.

The Worker emits at most one tool request at a time and at most 10 sequential
tool requests in one conversational turn. It receives only the bounded enabled
capabilities and emits an exact ID/version plus typed arguments. No prose is
execution. A visible success reply is legal only after a matching outer
`succeeded` receipt with `verification_state: verified`. Timeout, cancellation,
policy denial, malformed context, or adapter failure terminates that action
with a deterministic failure receipt.

If the model produces ordinary assistant prose immediately following a committed
tool batch that contains any failed or unverified receipt, the coordinator suppresses
the prose from the conversation outbox, retains the assistant step internally for audit
as an `InternalOnly` payload, and fails the run with the receipt error code (falling
back to `tool_failed`). An unverified success claim is never displayed to the user as a
completed answer. A subsequent different tool call remains eligible for normal execution
to allow recovery.

A non-success outcome, including a failure or clarification, is also kept as
one bounded in-memory value. It is isolated by session, consumed once by the
next Agent turn, and removed on session replacement, disconnect, or deletion.
It is never written to conversation history, memory extraction, reviewed
examples, TTS, or VTS metadata. This lets Ru answer an immediate “why?” from
the verified reason without asking the model to reconstruct or invent the
previous action state.

Legacy action-marker prose is stripped from visible output and never translated
into desktop side effects; structured selection is the only action channel.

## File and terminal boundaries

File and terminal bindings are advertised by the Worker v2 settings as the
matrix above shows. On the headless Agent v2 path, `bash` with `mode=run` and
`mode=shell` execute as the current Windows user and can run arbitrary host
code or open sockets; analyzer/rule/approval decisions remain authoritative.
The managed Git commit workflow is shipped inside terminal execution with
temporary-index isolation, path attribution rollback, and refusal when the
real index already contains staged user changes. The Agent permission model is
not an OS-enforced filesystem/network sandbox; its `network`
field and `network_intent` are descriptive input validation and audit/approval
metadata, not an operating-system sandbox or network-isolation boundary. A
zero exit is `succeeded`; non-zero exit and start failure are `failed`; timeout
is `timed_out`. File tools run automatically in the trusted root or a folder
granted to the same live session; protected paths and hard invariants stay
gated. Every mutation binds the preimage hash captured by Broker preparation and
fails with `workspace_changed` on mismatch without writing; the hash is not a
model argument. `write` with `op=delete` keeps its bounded, host-configurable recovery cap
(default and hard maximum 4 MiB) and returns an opaque `recovery_id`.
`edit` accepts LF-authored markers in CRLF files while preserving the
file's newline style. `read` with `op=search` uses bounded concurrent reads, propagates
cancellation, and sorts results by ordinal path/line before applying its result
cap. Bash children receive redirected stdin that is closed immediately;
TTY/editor-style requests remain rejected by canonical command policy.

### Markdown skill guidance

`Ru.md`, `.ru/AGENTS.md`, and `.ru/CONTEXT.md` are user-workspace files below
the selected run root, not repo templates. Minimal format:

```markdown
# Ru workspace instructions

- Follow repo conventions.
- Keep changes minimal and verified.
```

Markdown skills are a separate prompt-guidance format. Copy a directory
containing a case-insensitive `SKILL.md` into one of the supported roots:
`<workspace-root>/skills`, `<workspace-root>/.ru/skills`, or
`%LOCALAPPDATA%\Ru\skills`. A minimal package can look like this:

```markdown
---
name: pdf-analyzer
description: Analyze and summarize PDF files
allowed-tools: read, bash
---

# PDF Analyzer

Describe the review workflow here.
```

The Host discovers direct child directories in that precedence order and
re-reads them for each newly built prompt context, including a new Agent run
or after restart. `allowed-tools` is descriptive metadata only: it does not
advertise or grant tools, scopes, paths, network access, or approvals. Only
`SKILL.md` is read by discovery. Ancillary `references/`, `assets/`, and
`scripts/` files are ignored and never automatically read or executed; any
later access still has to use an advertised tool under Broker policy.

Markdown guidance is rendered as untrusted `ExternalSkill` prompt content. It
does not become a `SkillManifest` executable workflow, does not use
`skill.json`/`instructions.md`, and does not appear in the executable
`/skills`/`/skill` session grant. `RuAgentBroker` remains the only authority
for canonical paths, capabilities, scopes, approvals, and execution.

Declarative Skills are available at session-open only when their validated
`SkillManifest` IDs are included in the session grant. `/skills` lists only
those grants, and `/skill <id> {json}` resolves the highest granted version
before the Broker rechecks required scopes, tool bindings, schema, step,
output, and timeout limits. A Skill never bypasses policy or approval, and raw
step output is not promoted to visible chat. Step `name` values are local
aliases for readability only; execution always dispatches on the adjacent
`tool_id`, which must be an advertised binding. A Skill that requires a
non-advertised binding (for example `calendar.*`, which remains
experimental/unwired per the capability table) fails closed at validation and
never executes. `SkillEvent` and `SkillResult`
failures keep the same bounded identity/state/error shape as successes,
including when the session is missing, so clients can fail closed without
parsing raw exceptions. Connectors, workspace search, and application
launchers remain unadvertised.

## Typed command channel

`POST /agent/v1/runs/{runId}/commands` accepts only the bounded commands
`/compact normal|aggressive`, `/help`, `/skills`, and `/skill <id> <json-object>`.
The shared `AgentActionRouter` rejects unknown or malformed slash commands with
stable `command_unknown`/`command_invalid` codes, without creating a prompt,
history entry, or outbox message. `/compact` reuses the durable compaction
contract; `/help` is static; `/skills` and `/skill` are session-scoped. The CLI
adapter uses the same parser and renders only bounded result metadata.

## Web tool gates

For the managed Agent path, web tools are enabled with Google HTML search
`https://www.google.com/search` as primary and DuckDuckGo HTML
`https://html.duckduckgo.com/html/` as automatic fallback, and always
require approval (`RU_AGENT_BROKER_WEB_REQUIRE_APPROVAL=true`). `web_search`
is advertised with `network.search`; `web_fetch` is advertised with
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
known project root and exposes no mode selector; there is no
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
- `terminal_external_mutation_requires_approval` — Lệnh terminal này gây
  mutation ngoài workspace (push/fetch mạng, cài package, restore).
  The approval returns an ephemeral one-time terminal token, never a
  reusable rule.
- `terminal_timeout_requires_approval` — Lệnh terminal này chạy lâu hơn
  cửa sổ chuẩn (900s). The approval returns an ephemeral one-time terminal
  token, never a reusable rule.
- `web_approval_required` — Ru cần phê duyệt trước khi gọi web bên ngoài.
  The approval returns an ephemeral one-time token, never a reusable rule.
  Web tools always ask; explicit rules can still deny them first, and managed
  startup keeps approval-gating on.

Recognized local Git mutations, read-only/test/build/lint run commands are
auto-allowed (shell scripts always ask); network-affecting external mutations, direct terminal
deletion, extended timeouts, and unrecognized executables require a single-use approval
even inside the project root or an approved folder. The CLI exposes the same model with
`agent approve <run-id> <approval-id>` and `agent deny`; there is no
`--scope` flag and no `--preset` flag in the normal flow.

The transcript activity feed is connection-local and transient. Runtime maps the
Core projection to an `agent.activity` SSE frame whose inner JSON contains only
the typed fields `activity_id`, `kind`, `state`, optional safe `file_name`
(basename/line range only), `line_start`/`line_end`, `symbol`, `count`, `added`, `removed`,
`duration_ms`, and bounded `safe_code`. It does not advance the durable event
cursor, append to the Harness event store, or enter session history.

The feed projects nine kinds: `thinking`, `explore`, `search`, `analyze`, `edit`,
`terminal`, `compaction`, `retry`, and a generic `status` fallback. `compaction`
reports the bounded number of covered context entries and the elapsed time of a
context compaction (the summary text never crosses the sink boundary); `retry`
reports a bounded read-only tool transport retry attempt. Completed tool kinds
carry a bounded `duration_ms` measured from the scheduled execution.

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

The desktop surface renders every kind. The TUI renders `thinking` through the
inline spinner and `compaction`, `retry` and non-`frames_dropped` `status` kinds
as transient status lines; tool kinds stay owned by the durable tool events,
which remain authoritative when transient frames are dropped. Completed tool
kinds additionally expose their bounded `duration_ms` on the desktop item.

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

External-folder and dangerous-terminal requests use a short-lived approval bound to
session, request, tool version, canonical path/executable/argv/cwd/operation/
network, snapshot version, and intent hash with 30-min expiry. Consumption is
first-writer-wins `approval_stale` (including `PendingApprovalStore` and Host
`HostApprovalLedger`). Deny terminates the current run with `approval_denied`,
so the model cannot immediately request the same permission again. Changed
fields and expired or moved-workspace replays return `approval_stale`. The
Broker reply carries the exact matcher preview plus one `authorization_token`
(60s); no reusable rule is ever produced. Ordering is
`consume one-time approval under snapshot N
→ execute already-authorized call under N → persist final receipt → build/bind
N+1 → invalidate other pending N decisions`; Broker restart before dispatch
invalidates ephemeral decision and causes safe re-evaluation, never adapter
invocation. File tools follow the same ordering with `tool.execution_requested`
persisted before dispatch and `FileRecoveryStore`/`RunChangeSetStore` for
recoverable deletes. The deterministic Broker approval probe uses
`write` with `op=write` (and file delete/restore ops) only inside a fresh temporary
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
explicit opt-in native evidence classes. Deterministic evidence does not prove
that an arbitrary terminal command changed a live external application or
device.
