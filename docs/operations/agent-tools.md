# Agent tools and truthful execution

This document is the operating contract for Ru's action execution kernel v1
plus the additive Phase-2 policy authority and selected Phase-3 headless Agent
v2 surfaces, updated 2026-09-24.
It distinguishes a Broker binding that can execute from a capability the model
is actually allowed to select. Those states are not interchangeable.

This checkpoint includes declarative Skills, bounded headless terminal execution
(`bash` with `mode=run` or `mode=shell`), session branching/fork/rewind, managed
Git commit workflow, and dynamic model context compaction. The MCP stdio bridge
is tested infrastructure but is not production-composed; connector libraries are
not advertised Agent tools.

> **Checkpoint (Agent-only generation, 2026-09-05).** Recorded at that date; the counts below are that snapshot's and the section that follows this one carries the current contract.
> - Public Host (`RuBackend.exe --managed-runtime`) now owns Worker/Broker in
>   one Job Object; `RuApp` and `RuAgent.Cli` start-or-attach via
>   `RuAgent.Client`. There is no device/runtime v1 boundary; Agent
>   generation runs on the v2 one-step protocol at `/worker/v2` and the
>   `/agent/v1/*` (21 HTTP + SSE `GET /agent/v1/runs/{runId}/events` with
>   `id:<sequence>`/`Last-Event-ID`/`after_sequence` cursor, reconnect replay).
>   This checkpoint exposes Broker-backed model tools (`read` for one file with
>   offset/limit, `list` for one directory, `help` for the bundled docs,
>   `glob`/`grep` for bounded discovery,
>   `write` with `op=write`,
>   `edit`, one-time `write` with `op=restore`) through a durable Core (state machines incl.
>   `tool.execution_requested`, SQLite WAL `agent_harness.db`, AES-GCM
>   payloads, idempotent conversation outbox, workspace trust,
>   session permissions with monotonic version, encrypted `PolicySnapshot`
>   snapshots, `FileRecoveryStore`/`RunChangeSetStore`, approval ordering
>   `consume one-time approval → execute under N → receipt → N+1` with a
>   reusable project rule persisted only for the explicit `allow_always`
>   decision; every other approval is `allow_once`,
>   `allow_session_directory` (external folders only), or `deny`) and a thin
>   `RuAgent.Cli` tracer (safe envelope only, NDJSON `--json` `v:1`, no private
>   payload auto-fetch, `policy list`/`import --global FILE`/`remove`,
>   `approve <run-id> <approval-id> [--always]`/`deny`, `run --workspace PATH "prompt"`,
>   with no `--scope`/`--preset` flags exposed; the Host accepts
>   `allow_once`, `allow_session_directory` (external folders only),
>   `allow_always`, and `deny` — the server picks the right one-time token from the pending
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
>   Headless terminal requests (`bash` with `mode=run` or `mode=shell`) are available
>   under the policy/approval boundary; the managed Git commit workflow is fully shipped
>   with temporary index isolation and path attribution rollback.
>   Typed command dispatch is available through `POST /agent/v1/runs/{runId}/commands`;
>   `/compact`, `/help`, `/skills`, and `/skill <name>` use the shared
>   Protocol parser and do not become model history. `/skills` lists workspace
>   skill name + description; `/skill <name>` loads one SKILL.md body as
>   untrusted prompt guidance. The `skill` tool lets the model load a body
>   on demand; neither path grants tools, scopes, paths, or approvals.
>   Managed startup enables standalone `web_search` and `web_fetch` as
>   approval-gated capabilities backed by the configured search engine
>   (Google HTML by default) with fixed free fallbacks (DuckDuckGo, Bing,
>   Mojeek); results are bounded, sanitized, and explicitly
>   untrusted.
>   Workspace coordination is now opt-in per workspace through the authenticated
>   concurrency/activity/changes endpoints. Shared mode permits disjoint runs;
>   active Ru runs hold renewable workspace membership leases, and external file
>   changes are surfaced as `unknown_external` after an authoritative SHA-256
>   check and durably appended to the encrypted change journal. Terminal
>   `read_only` and `write_set` fields are additive protocol metadata; full
>   terminal write-set enforcement remains gated.
> - Public Contract is now **42 HTTP with no WebSocket route** (21 non-Agent + 21 `/agent/v1/*`).
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

The immutable Protocol catalog contains 12 model-visible definitions (`read`,
`list`, `help`, `todo_write`, `grep`, `glob`, `write`, `edit`, `bash`,
`web_search`, `web_fetch`, `skill`). Each tool has one purpose: `read` takes `path` with
optional `offset`/`limit` and reads one file; `list` takes `path` with optional
`max_entries` (1–512); `help` takes `query` (1–256) with optional `max_results`
(1–5) and searches the five bundled offline documents; `todo_write` replaces the
session task list (at most 64 rows of `content` plus a `status` of `pending`,
`in_progress` or `completed`) and touches neither disk nor network.
`glob` takes `pattern`
(required) plus optional `path` and `max_results` (1–64, default 64) and returns
stable sorted relative paths without content access; `grep` takes `query`
(1–512 chars, required) plus optional `path`, `file_glob`, `case_sensitive`
(default false), `is_regex` (default false) and `max_results` (1–64, default
64), and returns bounded match locations without raw file content; `write`
carries `op` (`write`, `delete`, `restore`); `bash` carries `mode`
(`run`, `shell`).
`web_search` takes `query` (1–512 chars) plus optional `max_results` (1–10,
default 10); `web_fetch` takes `url`; `skill` takes `name` (1–96) and loads one
workspace SKILL.md body as untrusted prompt guidance. Web tools are standalone with their own
`network.search`/`network.fetch` scopes and are automatic in the canonical
`plan`, `standard`, and `full_access` profiles. `web_approval_required` remains
available only when an explicit ordered rule asks; profile fallback does not
synthesize it.
Google HTML (`https://www.google.com/search`) is the only search engine.
There is no second engine to fall back to: a failure or an unreadable layout
fails closed as `web_search_parse_failed`, because quietly re-querying another
service would send the query somewhere the user never configured and outside
the approval the call was granted under. Fetch keeps the pre-existing
bounded, sanitized, explicitly-untrusted behavior. The `network_intent`
argument on a bash request is descriptive input metadata, not a security
boundary.

| Capability family | Model exposure | Broker behavior |
| --- | --- | --- |
| `glob`, `grep`, `list` | always advertised | `plan`: trusted-workspace read auto, external deny; `standard`: trusted/session-granted read auto, external ask; `full_access`: external structured read auto, structured protected path deny |
| `read` | always advertised | same canonical read matrix; `read` carries no `op` discriminator |
| `write` with `op=write`, `edit` | always advertised | `plan`: mutation deny; `standard`: trusted-workspace mutation auto, external structured ask; `full_access`: external structured mutation auto, structured protected path deny; preimage mismatch returns `workspace_changed` |
| `write` with `op=delete`, `op=restore` | always advertised | same canonical mutation matrix; delete remains recoverable and restore one-time |
| `bash` with `mode=run`, `mode=shell` | always advertised | `plan`: deny; `standard`: classifier-bounded local auto, external/escape/risky ask; `full_access`: arbitrary current-Windows-user terminal auto, not a path/network sandbox |
| `web_search`, `web_fetch` | advertised when managed Web is enabled | automatic in canonical `plan`/`standard`/`full_access`, bounded, sanitized, explicitly untrusted; an explicit ask rule may still return `web_approval_required`; exactly one search engine answers and its failure is reported rather than retried elsewhere, while redirect targets are revalidated |
| `help` | always advertised | offline, workspace-independent, approval-free search over five embedded documents; never reads workspace files |
| `skill` (workspace SKILL.md body) | always advertised (`system.read`) | loads one name + description-listed body as untrusted prompt guidance; grants nothing |
| MCP and connector libraries | not advertised | experimental/unwired; require a separate production trust and credential design |

### Session folder grants and bash approval

Canonical `plan` denies terminal and mutation. Canonical `standard` allows only
classifier-bounded local terminal intent inside the trusted workspace; deletion,
unknown executables, arbitrary shell, external cwd/argv, and other risky intent
ask with the existing reason codes. Repository cleanliness does not change the
decision. Canonical `full_access` delegates arbitrary terminal execution as the
current Windows user. It can read or change data outside the workspace and open
sockets; it is not a network/path sandbox. Hard timeout, output cap,
cancellation, redaction, and process-tree cleanup still apply.

An explicit absolute structured-file path outside the trusted root asks for
`external_directory_required` under `standard`. Approval records a
session-scoped `CanonicalDirectoryIdentity` for structured descendants; it is
not shared with another/forked session, lost on restart, and never authorizes
terminal intent. Full Access instead selects an ephemeral nearest external
ancestor for that structured call and does not persist a folder grant.
Structured protected paths remain denied in every preset, and canonical,
symlink, junction, and hard-link checks stay active. An ambiguous mutation
pauses for inspection until explicit Continue.

`files.apply_patch` is intentionally absent from the runtime capability catalog
and is not model-usable at this checkpoint. Any internal adapter or test fixture
for patch application does not create a public model capability.

## Model-first selection and native batches

The Host advertises the same canonical tool definitions to the prompt and Worker.
On a provider-native path, one model generation may emit optional narration and
one or more ordered tool calls. On the compatibility text-selection path, the
model emits exactly one JSON object with `tool_id`, `tool_version`, and
`arguments`, without selection prose. The text path also accepts the existing
strict wrapper forms; malformed markup remains ordinary text and never executes.

The runtime evaluates a complete batch before executing any call. If any
non-denied call asks, prepared authorization is discarded and the whole retained
batch pauses for one explicit decision. Resolution re-evaluates the exact durable
proposals under the current lease; the model is not asked to reissue an approved
call. A denial may return a repair hint, while an exact repeated call is stopped
by `repeated_tool_call` before duplicate dispatch. An empty final step fails as
`empty_model_reply`.

The shared budget remains 256 model-initiated tool calls per window; there is no
new low per-step cap. Read-only calls may execute concurrently in chunks of at
most eight while receipts retain model order. Mutations and terminal calls are
sequential and act as barriers; an unknown mutation outcome stops later work
until reconciliation. A model cannot claim success before durable receipts.

Use the `help` tool when the user asks how Ru's own features, architecture,
configuration, tools, providers, or operating boundaries work. It returns
bounded excerpts from the five current bundled documents (`ru-overview`,
`architecture`, `runtime`, `agent-tools`, and `provider-catalog`). Use runtime
context or an advertised terminal tool for live machine, process, device, and
workspace state. There is no second docs alias or fallback tool; the only
model-visible bundled-help contract is `help` on tool
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
Web bindings through the Broker gateway. Workspace `SKILL.md` roots are
prompt guidance and grant nothing by themselves. WinUI renders a received grant;
it does not infer workspace, terminal, Web, connector, or skill access.

The Worker may return one or more native calls in a step and preserves their
provider IDs and order. The Host sends only bounded enabled capabilities, exact
tool versions, and typed arguments; narration is display text, never execution.
The scheduler allows at most eight overlapping read-only executions, keeps
mutation/terminal calls sequential, and appends receipts in model order. A
visible success reply is legal only after matching durable success receipts.
Timeout, cancellation, policy denial, malformed context, or adapter failure
terminates or reconciles the action with a deterministic receipt.

If the model produces ordinary assistant prose immediately following a committed
tool batch that contains any failed or unverified receipt, the coordinator suppresses
the prose from the conversation outbox, retains the assistant step internally for audit
as an `InternalOnly` payload, and fails the run with the receipt error code (falling
back to `tool_failed`). An unverified success claim is never displayed to the user as a
completed answer. A subsequent different tool call remains eligible for normal execution
to allow recovery. A first `policy_denied` is the exception: the run stays alive so
the model can read the failed receipt plus repair hint and adjust; only a repeated
identical deny terminates the run.

A non-success outcome, including a failure or clarification, is also kept as
one bounded in-memory value. It is isolated by session, consumed once by the
next Agent turn, and removed on session replacement, disconnect, or deletion.
It is never written to conversation history, reviewed
examples, or public payload metadata. This lets Ru answer an immediate “why?” from
the verified reason without asking the model to reconstruct or invent the
previous action state.

Legacy action-marker prose is stripped from visible output and never translated
into desktop side effects; structured selection is the only action channel.

## File and terminal boundaries

File and terminal bindings use the canonical matrix above. `plan` terminal
authority is denied. `standard` confines terminal execution to classifier-bounded
local intent and asks for external/escape/risky intent. `full_access` may run
arbitrary `bash` as the current Windows user against any path and may open
sockets; this is terminal delegation, not a network/path sandbox. Repository
cleanliness is irrelevant. Full Access still has hard timeout, output cap,
cancellation, redaction, and process-tree cleanup. Structured protected paths
remain denied, but they do not re-impose terminal confinement.

The managed Git commit workflow retains temporary-index isolation, path rollback
attribution, and refusal when the real index already contains staged user
changes. A zero exit is `succeeded`; non-zero exit and start failure are
`failed`; timeout is `timed_out`. Standard structured-file tools use the trusted
root or a session-scoped canonical folder grant; Full Access uses an ephemeral
per-call external root. Every structured mutation binds a Broker-captured
preimage and fails with `workspace_changed` on mismatch without writing.
`write` with `op=delete` keeps its bounded recovery cap and opaque recovery ID.
Bash children receive redirected stdin that is closed immediately; TTY/editor
requests remain rejected by canonical command policy.

### Skill guidance (`SKILL.md`)

Workspace `SKILL.md` guidance follows the Claude/OpenCode/Pi shape: one
directory per skill, `SKILL.md` with `name` + `description` frontmatter and a
markdown workflow body. The prompt lists name + description only (with the
body digest pinning the pointed-at version). The model loads the full body
on demand with the `skill` tool (`name`), then acts with its normal tools
under Broker policy. The user-facing `/skills` lists name + description;
`/skill <name>` loads one body. Neither executes tools by itself.

Skill bodies are untrusted guidance. `allowed-tools` is descriptive metadata
only: it never grants a tool, scope, path, network permission, approval, or
Broker capability.

`Ru.md`, `.ru/AGENTS.md`, and `.ru/CONTEXT.md` are user-workspace files below
the selected run root, not repo templates. Minimal format:

```markdown
# Ru workspace instructions

- Follow repo conventions.
- Keep changes minimal and verified.
```

Workspace skill roots holding that same `SKILL.md` shape. Copy a directory
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

Skill guidance is rendered as untrusted `ExternalSkill` prompt content. It
uses `SKILL.md` only and never executes tools by itself.
`RuAgentBroker` remains the only authority
for canonical paths, capabilities, scopes, approvals, and execution.

## Typed command channel

`POST /agent/v1/runs/{runId}/commands` accepts only the bounded commands
`/compact normal|aggressive`, `/help`, `/skills`, and `/skill <name>`.
The shared `AgentActionRouter` rejects unknown or malformed slash commands with
stable `command_unknown`/`command_invalid` codes, without creating a prompt,
history entry, or outbox message. `/compact` reuses the durable compaction
contract; `/help` is static; `/skills` and `/skill` are session-scoped. The CLI
adapter uses the same parser and renders only bounded result metadata.

## Web tool gates

For the managed Agent path, Web tools use the configured Google HTML search
engine, `https://www.google.com/search`, with fixed free fallbacks
(DuckDuckGo HTML, Bing, Mojeek) tried in order when the primary has no
usable results. Every fallback request still passes redirect and
private-host validation. Canonical
`plan`, `standard`, and
`full_access` all allow Web; an explicit ordered ask rule may still return the
legacy `web_approval_required` reason. Redirects are validated hop-by-hop and
private/reserved targets are rejected. Responses retain byte, UTF-8,
content-type, sanitizer, and citation caps, and all returned content stays
explicitly untrusted. These are bounded Web-tool controls, not the future
OS-enforced filesystem/network sandbox.

## Agent activity shown in WinUI

Agent is the only supported generation path. The desktop composer uses the
known project root and exposes one bounded preset selector (`Plan` /
`Standard` / `Full Access`) plus the model picker; there is no Chat mode switch
and no workspace editor. The `Full Access` choice needs one explicit
confirmation per session, is stored server-side with a monotonic version, and
flows into runs;
a version conflict surfaces as `preset_mismatch` / `preset_version_conflict`.
The sidebar
keeps only primary conversation surfaces; Log, Diagnostics, Policy, and
Audit remain reachable from the `Công cụ` flyout. Agent prompts are
created through the authenticated `/agent/v1/*` API and observed over
SSE. The inline surface never displays raw commands, arguments, stdout,
or private payload content.

## Agent permission UX

Every Agent run is session-scoped. The chat composer offers canonical `Plan`,
`Standard` (default), and opt-in `Full Access`; the Broker remains the only
authority. Plan allows workspace read/Web and denies terminal/mutation.
Standard auto-allows bounded-local in-workspace mutation and asks for
external/escape/risky intent. Full Access auto-allows external structured files
and arbitrary terminal while structured protected paths remain denied.

Workspace trust is not a preset. It is explicit once per canonical identity:
the GUI requires a guarded Approve action, while CLI `run`/`chat` require
`--trust-workspace`. A moved or recreated identity asks again. Full Access
requires its GUI confirmation or CLI `--confirm-full-access`. Host and UI only
display the server-selected pending approval kind with one bounded message per
reason code:

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
- `terminal_timeout_requires_approval` — compatibility reason retained for an
  explicit ask rule. Canonical profile fallback no longer creates a 900-second
  approval gate; a timeout above the protocol hard maximum is denied in every
  preset.
- `web_approval_required` — compatibility reason emitted only when an explicit
  ordered rule asks for Web. Canonical `plan`, `standard`, and `full_access`
  otherwise allow Web automatically; the returned authorization is
  single-use and exact-intent bound.

Standard auto-allows only classifier-bounded local terminal intent; arbitrary
shell, deletion, unknown executables, external cwd/argv, and other risky forms
ask even if the repository is clean. Full Access removes those profile asks and
delegates terminal execution to the current Windows user, but it is not a
network/path sandbox. The CLI exposes the same model through `agent approve` /
`agent deny`; `agent run` and `agent chat` accept `--preset
plan|standard|full_access`, `--trust-workspace`, and
`--confirm-full-access`. `--json` is accepted only with explicit trust and
Full Access confirmation.

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
reports a bounded tool transport retry attempt (any access kind, at most once
per call). Status rows can carry a bounded plain-text `detail` (for example the
inspection-gate pause with its Continue guidance). Completed tool kinds
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
live status row and `compaction`, `retry` and non-`frames_dropped` `status` kinds
as transient status lines; tool kinds are settled by the durable tool events,
which remain authoritative when transient frames are dropped. Because the
durable `safe_summary` for a tool event is a bounded safe code rather than a
name, the TUI keeps the activity captured for each tool and prints one row per
tool when the durable completion arrives (`verb`, basename, line range and
`duration_ms` when the activity was delivered); a dropped frame degrades to a
code-only row instead of duplicating the event. Completed tool kinds
additionally expose their bounded `duration_ms` on the desktop item.

Before an Agent final reply is persisted, the reply policy strips private
markers. The resulting
visible text is the only assistant payload sent to the authenticated client,
model context, and conversation outbox. Assistant
activity is a post-commit effect; failures are bounded observations and do not
turn a committed run into a failed run. User activity is projected from its
outbox intent with the same preview-sanitized `agent` shape.

The feed is shown only after the first valid activity frame and is cleared on
terminal completion, cancellation, failure, disconnect, session replacement,
or reset. The inline surface still owns bounded status, workspace trust,
approval, Stop, and Continue controls. Malformed, unknown, stale, or other
content-bearing transient frames are ignored. Raw commands, arguments, stdout,
or private payload content never reach the feed, durable audit, conversation
history, or public payload metadata.
The desktop entry point also creates a real 32-character session automatically
for the first Agent prompt when the empty conversation surface has no active
session; users do not need to send a preliminary message.

Agent requests use an opaque `correlation_id`, durable run identity, and SSE
cursor. The UI presents one active run, ignores stale or uncorrelated frames,
and reconnects by replaying durable events without duplicating history. A
disconnect marks an ambiguous request as a bounded retryable error; it never
silently replays a request that might already have committed. An approved run
rehydrates and re-evaluates its durable retained batch without a new model
proposal or duplicate proposal event; the model is never asked to reissue an
approved call. An ambiguous mutation outcome pauses with a waiting status
detail, and explicit `Continue` clears the inspection gate with a
`gate_cleared_by_user` audit event in the same commit.

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
`HostApprovalLedger`). A first deny keeps the run alive with a repair hint so
the model can adjust; only a repeated identical deny terminates the current
run with `approval_denied`,
so the model cannot immediately request the same permission again. Changed
fields and expired or moved-workspace replays return `approval_stale`. The
Broker reply carries the exact matcher preview plus one `authorization_token`
(60s); a reusable project rule is produced only for the explicit
`allow_always` decision. Ordering is
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
