# Current runtime behavior

Status: operating contract for the current main branch, updated 2026-10-08.

This document keeps current runtime decisions separate from implementation
history. Where automated and live evidence differ, the narrower claim wins.

## Conversation and generation

The cloud-first desktop default is OpenAI. Hosted OpenAI and Anthropic
providers are the supported cloud adapters, alongside any `models.dev:<provider-id>`
generic provider; OpenAI-compatible custom
adapters remain available for explicitly configured external services. Ru no
longer ships or starts an in-process conversational model. Switching providers
must preserve bounded context, cancellation, streamed visible-text
normalization, and public error mapping by ASP.NET Core. Stale `llama.cpp` or
GGUF settings migrate to `models.dev:opencode` and retired `opencode`/`kilo`
choices migrate to their `models.dev:` equivalents (keys must be re-entered).
Cloud generation requests omit provider-specific `Temperature`, `Top-K`, and
`Max New Tokens` controls; legacy stored values are ignored and stripped rather
than forwarded to a provider.

Provider models and verified reasoning options come from the Host-owned
catalog, refreshed by the GUI while Chat is active. The Chat picker renders
all non-empty provider catalogs in one stable grouped list with global
provider/model search; one selection saves a strict two-field payload through
the existing atomic `/settings` transition. Failed catalog groups stay
isolated, stale same-identity data is labeled, and a selected model that is no
longer available is never replaced. Provider default omits reasoning
overrides; invalid explicit choices or unsupported model protocols stop new
model requests without an API/model/effort fallback. See
[provider catalogs](provider-catalog.md) for refresh and capability boundaries.

Ru mirrors Vietnamese, English, and mixed-language turns. In Vietnamese Ru
calls the user `anh` and itself `em`. Agent replies contain no Unicode emoji or ASCII
emoticons. Search commands, model controls, segment
identifiers, and transport timings are private metadata and must be removed
before display, history, logs, or reviewed examples.

Each Agent turn carries a bounded `correlation_id` and a durable run/session
identity. The composer keeps one active run; reconnecting to the Agent SSE
stream replays only durable events and never duplicates a committed history
entry. Stale or malformed events are ignored by the presenter, so a late reply
cannot update a newer bubble. A disconnect marks the active run as a bounded
retryable connection error. Run creation makes at most two attempts on the
same endpoint, each with a 15-second deadline, using the identical serialized
request and idempotency key. Cancellation before dispatch prevents creation;
after dispatch the bounded acknowledgement path still observes a late run so
the client can cancel it. Exhaustion reports `agent_create_outcome_unknown`.
WinUI retains the request identity, blocks overlapping creation and offers
Stop to replay that same request before cancelling the acknowledged run.
A failed Stop reconciliation retains the identity for another attempt.
An unusable success response, a server error, or an HTTP error after a lost
acknowledgement also retains that identity, while preserving its original
error code. A definitive first-attempt client error does not trigger this
unknown-outcome state.

Opening a session adopts its newest non-terminal run, so a run that outlived
the host is not invisible: WinUI re-arms Continue/Stop for it (`AdoptRunAsync`;
a running run is subscribed from its durable `LastSequence`, a parked one waits
for the typed Continue), the TUI adopts it when `chat --session` reopens the
session (parked → the `/continue` path, running or approval-waiting → an
explicit `/follow` action), and switching sessions detaches a parked run
instead of cancelling it. In-chat follow/continue reject foreign-session runs.
The Continue path is the same on both clients: a parked run
(`waiting_user`, `reconciliation_required`) resumes only when the client
continues or reconciles it.

### Token usage accounting

Opening Ru does not call a model, so startup alone has no provider token usage.
For an actual model step, adapters preserve provider-reported input, output,
total, cache-read, and cache-write counts through the typed model step
result. The Host persists those counts as a `ModelUsage` payload alongside a
final reply and inside an `AssistantStep` for tool turns. The payload is a
sidecar record for authenticated telemetry consumers; the current chat bubble
fetches it from the durable `model.usage_recorded` event and renders the
reported counts below the assistant text. A missing `usage`
object means the provider did not report counts; it is not converted to zero.
Numeric zero remains an exact provider value. `estimated_input_tokens` is kept
for backward compatibility and remains an estimate, not a billing record.

Agent generation runs inside the Public Host process through the typed
`ModelStepService` behind `IModelStepRunner`; the device v1
runtime/voice/STT/VTS/observation protocol was removed with the media surfaces,
and the managed C# worker process, its loopback port, and its wire protocol
were retired (ADR-0012). No model step crosses a process boundary, and no
per-step credential handoff is written to disk. The credential store keeps
its existing protected-at-rest ownership.

Managed startup starts no child process: tool execution runs in process
through `BrokerExecutionRuntime` behind `IToolGatewayFactory` (ADR-0014), and
the retired Broker child, its loopback lease and `RuAgentBroker.exe` are gone.
Each Agent
step resolves the run's cached in-memory provider snapshot from a secret-free
durable Run Provider Selection. Restart rebuilds the snapshot from that same
selection and credential account; legacy runs without it fail closed. The
bounded protocol
(`chat_completions`/`openai_responses`/`anthropic_messages`), verified native
reasoning options, and for Anthropic the bounded `max_tokens`; changes to
settings apply to the next run, not a continuation of the active run.
The step dispatches exactly the pinned protocol through one of three focused
adapters; unknown or mismatched protocols fail closed before HTTP. Managed startup is unconditional.

### Agent system-prompt composition

Every accepted Agent model step—typed text, coding, native tool selection, and
continuation—rebuilds a fresh prompt context before the model step. The
private Generation Kernel emits exactly one `system` message with composer
`2.0.0` from seven ordered blocks: neutral Ru Agent runtime contract, Agent
execution contract, environment, profile plus canonical effective policy,
root-selected instruction blocks, validated host capabilities, and bounded
runtime context. The model then receives ordered typed user/assistant/tool
messages; clock, mood, and references are not appended as separate system
messages.

Host capabilities are the runtime projection of thirteen Protocol-owned tool
definitions: `read`, `list`, `help`, `todo_write`, `grep`, `glob`, `write`,
`edit`, `bash`, `web_search`, `web_fetch`, `skill`, and Host-intercepted
`ask_user`. `ask_user` never dispatches to the Broker: it parks the run in
`WaitingUser` with a durable `input.requested` event until the client posts the
answer, which arrives as that call's durable tool result. On provider-native
paths, one generation may return optional narration and one or more ordered
calls. The Host persists the assistant batch and matching durable tool results;
it does not flatten native calls into prose. Compatibility text selection emits
exactly one JSON object. The model makes no success claim before durable
results. If a batch asks, no call executes; after approval the runtime
re-evaluates the retained durable proposals without a new model proposal.
Read-only calls may overlap up to eight at a time while mutation/terminal
barriers and receipt order remain intact. Oversized outputs are clamped while
untrusted references retain the bounded high-fidelity context. Unknown,
duplicate, malformed, or digest-mismatched contracts fail before provider
execution; wire and invariant violations remain terminal.

The Host reads the fixed `AGENTS.md`, `CONTEXT.md`, `.ru/AGENTS.md`, and
`.ru/CONTEXT.md` candidates below the selected run root, then discovers
direct-child skill directories from
`<workspace-root>/skills`, `<workspace-root>/.ru/skills`, and
`%LOCALAPPDATA%\Ru\skills`, in that order. A directory is a skill
only when it contains a case-insensitive `SKILL.md`. For example:

```markdown
---
name: pdf-analyzer
description: Analyze and summarize PDF files
allowed-tools: read, bash
---

# PDF Analyzer

Describe the review workflow here.
```

The loader applies final-path containment, strict UTF-8 decoding,
NFC/newline normalization, a 64 KiB per-file limit, and a 256 KiB combined
instruction limit. It re-reads those roots for every newly built prompt
context, so copying or changing `SKILL.md` is picked up on a new run or after
restart without a downloader or package manager. Ancillary `references/`,
`assets/`, and `scripts/` files are ignored by discovery and are not
automatically read or executed. Context values remain bounded (32 KiB per
reference and 512 KiB serialized context). Root-selected
instructions and skills are followed only as guidance; they cannot
grant tools, permissions, approvals, paths, network access, or preset changes.
The `allowed-tools` field is descriptive only; the optional `requires:` field is enforced on every body load — the model's `skill` tool and the Host's `/skill` command (only `python` is recognized: absent Python fails closed with `python_unavailable`). Runtime
references and tool/search results remain explicitly untrusted.

The `/skills` command lists workspace skill name + description and `/skill <name>`
loads one SKILL.md body as untrusted guidance; neither executes tools or grants
anything. The in-process execution authority remains the sole owner of path
canonicalization, capability policy, approvals, and tool execution.

The model step returns a typed `AssistantStep(text, tool_calls, finish_reason)`
together with a schema-validated, versioned prompt receipt. Core validates the
receipt against the exact context before committing model completion or
dispatching tools, then stores the receipt as an `InternalOnly` payload. Public
Agent SSE suppresses `model.prompt_composed` and its payload while advancing the
durable cursor. Invalid context or receipt metadata fails closed with
`prompt_contract_invalid`; there is no static-prompt fallback or
reduced-context retry. Managed runtime wiring enables the standalone
`web_search`/`web_fetch` tools by default; no settings field controls their
exposure.

Prompt composition is not a security boundary: execution-authority policy,
workspace trust, approvals, protected paths, external-directory grants, and
cancellation remain authoritative. Subagent orchestration, LSP,
extension/plugin/MCP discovery, arbitrary system overrides, Chat compatibility,
UI redesign, and OS filesystem/network confinement are outside this
implementation (ADR-0015).

When Ru needs product knowledge about its own features, tools, configuration,
providers, or operating boundaries, it selects the `help` tool (`help@1.0.0`). It
searches only five explicitly embedded Markdown resources and returns bounded,
stable document IDs and excerpts. It is offline, read-only, workspace-
independent, and approval-free. Runtime context and advertised terminal tools
remain the source for live machine, process, device, and current workspace
state; bundled help is untrusted reference data and cannot change instruction
precedence.

## WinUI responsive shell

The WinUI shell has three presentation tiers based on client width: Wide at
1280 px and above, Compact from 900 through 1279 px, and Narrow below 900 px.
Wide keeps the resizable 180–400 px sidebar; Compact keeps labels with tighter
spacing and 180–320 px bounds; Narrow uses a 72 px icon rail and stacks page
controls. These transitions are presentation-only and do not call the backend,
restart polling, or recreate list sources.

Automated tests cover policy boundaries, XAML state markers, vertical-only list
surfaces, and build correctness. Visual resizing at representative window sizes
remains a user-run acceptance check.

The current Agent composer uses the same tiers. The shell and conversation view use the
Editorial reading room presentation: the approved light mineral palette as the
baseline, a sidebar grouped by workspace (`WORKSPACES` with collapsible folder
groups, search, and relative timestamps), and a centered document-style reading column.
Log, Diagnostics, and Settings retain their existing layout
in this phase while inheriting the active shared Light or Dark token set.
The application keeps Light as its fallback and MainWindow can request the
active theme on the shell root so default controls follow the same contrast.

The appearance action in the sidebar footer switches Light and Dark
synchronously. The selected appearance is UI-only state stored at
`%LOCALAPPDATA%\Ru\ui-preferences.json`; it is not an ASP.NET setting or model
configuration. Switching remains presentation-only for application behavior:
it does not recreate message or session sources, call the backend during
layout, or alter event-stream ordering. Automated source/XAML contracts and the WinUI build cover
structural correctness. Visual resizing remains a
manual acceptance check.

The current Ru signature layer is also presentation-only: a quiet title-bar
wordmark, an editorial welcome eyebrow, message edge rules, and a GPU-accelerated
Composition visual field in the empty conversation state. The background features
gentle twinkling starlight points and periodic shooting star streaks rendered directly
via `Microsoft.UI.Composition` (`SpriteVisual`, `CompositionLinearGradientBrush` with
soft halo glow and core beam), runs only while `Messages.Count == 0`, and stops and
clears every visual element as soon as the first message exists. The composer keeps the
native TextBox caret/focus behavior and exposes one bounded preset selector
(`Plan` / `Standard` / `Full Access`); there is no Chat mode switch or workspace editor
in the composer.
The UI uses the known project root as the initial Agent workspace and does not
expose a workspace editor, while the authenticated API still validates the
canonical path. All submissions use the authenticated `/agent/v1/*` API/SSE
path. Workspace trust is explicit once per canonical identity: the GUI shows a
guarded confirmation, while CLI `run`/`chat` use `--trust-workspace`. A moved or
recreated identity requires trust again. Full Access requires the GUI gate or
CLI `--confirm-full-access`. Workspace trust, approval, Stop, Continue, and
bounded Agent status remain inline above the input. The terminal client keeps
the same parked-run path: the status row reads `▲ run paused · <code> · /continue`
and one notice carries `resume with /continue (or 'ru agent continue <run-id>')`,
cleared when the resumed run moves again. While a run is active, the transcript
may also show a separate transient activity feed above the assistant content,
driven by typed `agent.activity` SSE frames. It renders only safe state,
basename/line-range, symbol/count,
edit-count, duration, and safe-code fields; it is cleared on terminal,
cancellation, failure, disconnect, session replacement, or reset. Activity
frames do not enter durable history or audit. The projector covers nine kinds,
including `compaction` (bounded covered-entry count and elapsed time) and
`retry` (bounded tool transport retry attempt, any access kind, at most once
per call), plus a generic
`status` fallback (status rows can carry a bounded plain-text `detail`, for
example the inspection-gate pause with its Continue guidance). Agent payloads and raw tool output
never enter transport metadata or automation names.
Assistant text is delivered separately as a typed `assistant.delta` transient
frame. The client validates the outer kind, bounded UTF-8 text, model-step id,
optional execution id, and positive transient sequence before dispatching it;
unknown fields, malformed JSON, raw tool output, and replayed sequence keys are
dropped without changing the durable cursor.
Once the SSE feed emits `final_snapshot`, WinUI treats that terminal status as
authoritative if a follow-up `GET` races host shutdown or restart. A late
history/outbox failure after visible assistant deltas does not append a generic
failure sentence to the answer or replace it with a duplicate failure banner;
the durable run/event status remains available for diagnostics.
If the terminal snapshot races conversation-outbox delivery, a session-history
reload that still contains only the user turn preserves the already-rendered
assistant bubble. A later history refresh can reconcile it once the persisted
assistant entry is available.
Agent final replies are normalized through the metadata policy before the
authenticated payload and conversation outbox are committed; private
markers therefore never enter history or model context.
The safe assistant activity projection runs after that
commit, so a non-critical effect failure cannot roll back a completed Agent run.
Agent user/assistant activity records use the `agent` activity type and remain
preview-sanitized at the activity-store boundary.
When no session has been opened yet, the first Agent prompt creates a fresh
session before creating the run.
The desktop captures the submitted session ID after that creation, so an accepted
first turn clears its own composer draft. A refused submission retains the prompt
and attachments; its composer error remains visible across run-state refreshes,
even when no run exists yet.
Acceptance remains valid when the event stream finishes before the queued UI
transition runs. Session-switch teardown or a newer controller owner invalidates
that queued transition, so an older run cannot clear another session's draft.
If an Agent API call reports `session_not_found` or `invalid_session_id`, the
desktop creates one replacement session and retries once only before a run is
created. A second recovery failure is shown as a bounded error and is never
looped or filled with an earlier response.

## Desktop runtime lifecycle and reconnection

The WinUI 3 desktop client connects to the shared local Public Host via loopback IPC.
`ManagedRuBackendSessionProvider` tracks the Host process lifecycle and coordinates runtime liveness:
- Liveness detection: `HealthStatusService` probes loopback status with a bounded 3-second timeout via `CheckHealthDetailedAsync`, distinguishing genuine Host offline state (`HttpRequestException` or connection refused) from backend startup or readiness. Stale or disconnected sessions accurately mark `.NET Backend: Error`.
- Exit detection: `ManagedRuBackendSessionProvider` hooks the Host process `Exited` event, clearing the active session and firing `BackendExited`.
- Backoff & auto-reconnect: When the backend stops or becomes unreachable, high-frequency polling timers (catalog, sessions) are stopped to prevent UI thread socket storms. A gentle reconnect timer probes for backend recovery every 3 seconds, re-attaching or restarting the backend and reinitializing active views and controllers without requiring a GUI restart.
- Single-instance activation: Multiple instances detect existing instances via process enumeration and native WinUI class queries, bringing the existing window to the foreground cleanly.
- Shutdown: Window close coordinates timer cancellation and Host shutdown without orphan processes.

## Session and history lifecycle

ASP.NET Core is authoritative for persisted conversation sessions, history, and
the Harness records owned by a session. `DELETE /sessions/{sessionId}` keeps a
stable HTTP 200 response envelope: `status: "ok"` is returned only after the
deletion coordinator removes the session and its owned Harness artifacts;
missing or failed deletes return `status: "error"` with bounded copy.

Clients must inspect the application status instead of treating HTTP success as
proof of deletion. WinUI asks for confirmation, refreshes the sidebar only
after `status: "ok"`, and shows a bounded error without silently replacing the
conversation when deletion fails. If the last session was deleted
successfully, the UI creates one fresh session.

## Action execution kernel v1

The typed model-step settings advertise thirteen tools, one purpose each:
`read`, `list`,
`grep`, `glob`, `help`, `todo_write`, `skill`, `write` with
`op=write|delete|restore`, `edit`, `bash` with `mode=run|shell`, plus standalone
`web_search`, `web_fetch`, and Host-intercepted `ask_user`. The canonical matrix is: `plan` allows workspace
read and Web but denies terminal and mutation outright (read-only, no
per-change asks) and asks for external structured access; `standard`
auto-allows bounded-local in-workspace mutation and asks for
external/escape/risky intent; `full_access` is the skip-all mode — external
structured files and arbitrary terminal auto-allow while structured protected
paths (harness key/database files and runtime descriptors) and hard-invalid
requests remain denied.
Repository cleanliness never changes the decision.

Standard structured-file access outside the trusted root may create a
session-scoped canonical folder grant; it is not shared with a new
session, lost on restart, and never authorizes terminal. Full Access uses an
ephemeral structured root per external call. Terminal children in `standard`
and `full_access` run as the current Windows user without OS filesystem or
network confinement, including outside the workspace (ADR-0015). Standard
keeps its existing classifier and
approval rules; Full Access keeps its existing arbitrary-terminal policy. The
`bash` schema has no sandbox selector and rejects obsolete sandbox arguments.
Hard timeout, output cap, cancellation, redaction, and process-tree cleanup
remain. Structured file tools retain their independent path protections. API
keys remain in credential storage and diagnostics redact secrets.
Web is automatic in all canonical presets; `web_approval_required` remains only
as an explicit-rule compatibility reason.
The model selection channel is active for every Agent run: one budget window
allows up to 128 model steps and 256 model-initiated tool calls through the
Broker pipeline. Native batches may contain multiple calls; the scheduler caps
read-only concurrency at eight and keeps mutations/terminal sequential. Receipts
retain model order. Exhausting a window moves the run to `WaitingUser`; only an
explicit Continue opens a fresh window, and the TUI offers the same path: the
parked run shows a `▲ run paused` row plus `resume with /continue (or 'ru
agent continue <run-id>')`, and `/continue [run-id]` (or a bare `Continue`
while a parked run is known) POSTs the resume and follows the run from its
durable cursor. Native calls, text-selected JSON,
arguments, and outputs never enter the visible transcript as execution markup,
mood state, or public diagnostics.

A dead-end model step does not end the run by default. Ordinary prose after a
failed or unverified tool receipt is suppressed and answered with a durable
`[HARNESS RECOVERY]` note (up to 4 notes); empty final text gets up to 2 notes,
and a runner-rejected malformed, unavailable or invalid selection gets up to 4.
Incomplete `Length` finishes get up to 2 truncation notes. These kinds share a
cumulative cap of 12 notes per run. System recovery notes remain visible through
compaction. Feedback keeps the same model/provider; spent bounds fail with the
original code. Exact repeated calls and short failed cycles receive at most two
`[REPETITION GUARD]` notes, then park for Continue. The suppressed assistant step
is audit-only; its calls never enter native history without matching receipts.
Trailing advice does not hide the earlier receipt from repetition detection or
allow final prose to claim success after failed verification.
An exhausted provider-transient failure
(`provider_rate_limited`, `provider_timeout`, `provider_server_error`,
`provider_transport_error`) is not failed either: it parks the run in
`waiting_user` with a `ContinueRequested` that preserves the provider's code,
and an explicit Continue retries the same provider in a fresh budget window.
A provider stream that goes silent is the case behind `provider_timeout`: the
SSE reader resets a 120-second idle watchdog on every received chunk and
cancels a stream that sends nothing, so a provider that accepts the connection
and stalls becomes the same retryable timeout instead of holding the step until
the user cancels. The watchdog applies to all three protocols; the HTTP request
timeout itself stays infinite because a whole-request bound would cut a healthy
long generation.
Approval-decision deny and reconciliation decisions stay outside this loop: a
user deny keeps its fail-closed rule, the approval rules above are unchanged,
and an ambiguous mutation still pauses until explicit reconciliation. An allow
decision is remembered for the session: the exact approved intent is
materialized as a run-scope allow rule into the session's later policy
snapshots, so an identical call — including later in the same run — evaluates
as allowed without another prompt. The memory is in-process, expires and is
capped per session, and dies with the session or a host restart; protected-path
denials and Broker evaluation still decide every call. A
repeated `policy_denied` tool receipt followed by prose is not an approval
decision: it may still take the bounded `unverified_after_tool_failure` notes,
with the denial code as the terminal code.

External-folder requests and terminal deletion or unknown-executable requests
use bounded approval cards. The Public Host opens
one profile-scoped execution session per turn loop, and the in-process
authority executes and verifies adapters. Tool-class successes require an
invoked adapter. Python does not own desktop side effects.

The Host serializes one active execution grant: a matching valid session is
reused; a replacement Agent session closes the old grant before open. Failure
is fail-closed and never reaches the native adapter.

Non-success tool outcomes are represented as bounded Agent activity and durable
run state. Raw tool receipts never cross into the visible transcript, history,
reviewed examples, or public payload metadata.

The thirteen tools above are the model surface. `read`, `list`, `grep`,
`glob`, and `help` are separate definitions; `write`, `edit`, and `bash` expose
their own schemas, alongside `todo_write`, `skill`, `web_search`,
`web_fetch`, and the Host-intercepted `ask_user`. Managed startup constructs an enabled WebOptions instance with
Google HTML as the primary search engine `https://www.google.com/search`, raced
against the fixed free fallbacks (DuckDuckGo HTML, Bing, Mojeek); the bounded
responses carry an explicit `untrusted` marker and
never become instructions. `grep`
searches the workspace, `web_search` searches the public web, and `help`
searches only bundled Ru documentation. Unbound connectors and `app.open`
remain outside the model-visible catalog.
The managed Git commit workflow is shipped inside terminal execution, using a
temporary index and refusing dirty user index state. Other Git commands remain
ordinary terminal requests governed by Broker policy. The
GUI renders the actual `agent.session` grant received from the Host. Managed
startup enables the bounded WebOptions instance with Google HTML as its primary
search engine, so standalone `web_search` and `web_fetch` are advertised as
read-only managed capabilities without a settings flag or separate deployment
switch; Broker
policy remains authoritative for each call. The tested MCP bridge and the
mail/calendar/task connector libraries are not production-composed or
advertised as Agent tools.

### Typed commands

The authenticated `POST /agent/v1/runs/{runId}/commands` route handles
`/compact normal|aggressive`, `/help`, `/skills`, and
`/skill <name>`. `AgentActionRouter` is shared by Public Host and
CLI, bounds input, rejects unknown or malformed slash commands with stable
codes, and prevents command text from entering prompts, history, or the
conversation outbox. `/compact` uses the durable context-compaction contract;
the other commands return bounded static or session-scoped data.
Auto-compaction is tuned by two settings (`compaction_trigger_ratio`
default `0.80`, `compaction_target_ratio` default `0.62`); values must
satisfy `0 < target < trigger < 1` and anything else fails closed to the
defaults.

### Web access

The managed Agent constructs an enabled WebOptions object with Google HTML as
the primary search engine `https://www.google.com/search`, raced against fixed
free fallbacks (DuckDuckGo HTML, Bing, Mojeek); the first engine with usable
results answers and the rest are cancelled, and a total failure surfaces the
earliest engine's bounded error instead of an empty result. It
maps standalone `web_search` to `network.search` and standalone
`web_fetch` to `network.fetch`;
the canonical Broker policy remains authoritative for each read-only call:
`plan`, `standard`, and `full_access` all allow Web. An explicit ordered ask
rule may still return `web_approval_required`.
Redirects are disabled or validated at every hop, private/reserved targets are
rejected, and response bytes, content types, UTF-8, sanitizer output, and
citation count are capped. Web payloads are data only and cannot grant tools,
permissions, paths, approvals, or preset changes; embedded URL credentials
are rejected as well. Fetched text is wrapped with
`[untrusted_web_reference]`. Web policy applies to each request separately
from terminal execution; neither path uses OS network confinement.

Workspace `SKILL.md` bodies are untrusted prompt guidance. Subagents,
LSP, extensions, plugins, and MCP are not loaded or discoverable.

Selecting a preset while idle does not add a composer status row, so the empty
conversation welcome content keeps its position. The Agent inline status appears only
after a request or another activity that needs user interaction; a typed
request starts with `Đang chuẩn bị thao tác…`. Subsequent status and event
summaries are bounded safe copy, while verified completion and failure remain
fixed or safe-reason states. Malformed or stale-session activity is ignored.
Disconnect and session replacement clear the visible state
and pending approval.

The populated conversation transcript keeps its reading column centered and capped at
820 px, while the scroll host spans the full conversation viewport so its native
vertical scrollbar sits at the far-right edge. Horizontal scrolling remains
disabled and does not alter message
width or composer alignment.

## Persistence and privacy

ASP.NET Core owns settings, credentials, sessions (including message history), activity,
and reviewed-example policy. Business records share `agent_harness.db` under the canonical
`%LOCALAPPDATA%\Ru` location (see
`docs/adr/0004-canonical-ru-data-location.md`). Windows Credential
Manager remains the secret store.

Logs, telemetry, manifests, and automated evidence may contain fixed event
types, counts, durations, hashes, and statuses only. They must not contain
credentials, bearer values, request bodies, user/session identifiers,
transcripts, or private marker metadata.

Technical diagnostics are structured JSONL under `RU_LOG_ROOT` (defaulting to
`%LOCALAPPDATA%\Ru\logs`) with bounded ASP.NET, test-fixture, and WinUI
writers. The HTTP boundary records fixed route categories and durations; the
diagnostics API rejects invalid/oversized queries and caps exports at 2 MB.
Activity Timeline records are sanitized before SQLite insertion and on read.
Agent projections still follow the durable conversation outbox. The retired
file activity-outbox poller is replaced by one-time legacy import (ADR-0013).

## Acceptance boundary

Automated tests can establish routing, protocol ordering, bounds, cancellation,
privacy filtering, persistence semantics, and process
lifecycle. They cannot establish the effect of an arbitrary terminal command
on a live external application or device, provider quality, or
natural bilingual dialogue. Those
remain explicit user-run checks and must be reported as pending until performed.
