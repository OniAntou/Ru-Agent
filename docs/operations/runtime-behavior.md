# Current runtime behavior

Status: operating contract for the current main branch, 2026-09-07.

This document keeps current runtime decisions separate from implementation
history. Where automated and live evidence differ, the narrower claim wins.

## Conversation and generation

The cloud-first desktop default is OpenCode Zen. Hosted OpenCode Zen and Kilo
Gateway providers are the supported cloud adapters; OpenAI-compatible custom
adapters remain available for explicitly configured external services. Ru no
longer ships or starts an in-process conversational model. Switching providers
must preserve bounded context, cancellation, streamed visible-text
normalization, and public error mapping by ASP.NET Core. Stale `llama.cpp` or
GGUF settings migrate to the cloud default and cannot select a removed runtime.
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
retryable connection error; Ru does not automatically replay an ambiguous
request and risk duplicate history.

### Token usage accounting

Opening Ru does not call a model, so startup alone has no provider token usage.
For an actual model step, adapters preserve provider-reported input, output,
total, cache-read, and cache-write counts through the private Worker v2 done
envelope. The Host persists those counts as a `ModelUsage` payload alongside a
final reply and inside an `AssistantStep` for tool turns. The payload is a
sidecar record for authenticated telemetry consumers; the current chat bubble
fetches it from the durable `model.usage_recorded` event and renders the
reported counts below the assistant text. A missing `usage`
object means the provider did not report counts; it is not converted to zero.
Numeric zero remains an exact provider value. `estimated_input_tokens` is kept
for backward compatibility and remains an estimate, not a billing record.

The managed C# Worker is the Agent v2 generation boundary; the device v1
runtime/voice/STT/VTS/observation protocol was removed with the media surfaces.
Agent generation runs through the managed v2 step protocol on one private
loopback port and token. The v2 graph receives no provider credential; each
step receives a separate protected handoff.

Managed startup launches one `RuAgent.Runtime.Worker.exe` with
`ws://127.0.0.1:8005/worker/v2`, followed
by the private Broker. A neutral single-use DPAPI startup handoff composes the
C# v2 graph without reading or retaining a provider credential. Each Agent
step requires a separate protected handoff containing
the run-pinned provider selection, bounded protocol
(`chat_completions`/`openai_responses`/`anthropic_messages`), verified native
reasoning options, and for Anthropic the bounded `max_tokens`; changes to
settings apply to the next run, not a continuation of the active run.
Each handoff is deleted after consumption. The Worker dispatches exactly the
pinned protocol through one of three focused adapters; unknown or mismatched
protocols fail closed before HTTP. Managed startup is unconditional.

### Agent system-prompt composition

Every accepted Agent model step—typed text, coding, tool
selection, and continuation—rebuilds a fresh prompt context before the Worker
request. The private Generation Kernel emits exactly one `system` message from
these ordered layers: a neutral Ru Agent runtime contract, Agent execution contract,
environment, root-selected Ru instruction files, profile/preset, validated
host capabilities, and bounded runtime context. The model then receives the
ordered user/assistant/tool turns; clock, mood, and
references are not appended as separate system messages.

Host capabilities are the runtime projection of the 8 Protocol-owned tool
definitions: model tools (`read`, `grep`, `glob`, `write`, `edit`, `bash`,
`web_search`, `web_fetch`). Each capability includes its ID/version, description, canonical
scope, strict proposal schema, example, and SHA-256 digest. To select one, Ru
calls it through the provider's native function-calling interface when the
dialect supports it (`chat_completions`; the adapter sends `tools` derived from
the Host/Worker selection contract and maps `delta.tool_calls` back to Host tool
calls). Other dialects keep the text contract: exactly one JSON object of the
form `{"tool_id":"read","tool_version":"1.0.0","arguments":{"op":"read","path":"README.md"}}`.
Either way the model makes no success claim until the durable result arrives and
ordinary answers remain ordinary text. Tool exchanges stay in that dialogue:
each selection is followed by its bounded, explicitly untrusted tool output, and
the newest user-side block becomes the next prompt, so a resumed step never
re-runs work that already succeeded. Oversized outputs are clamped to the prompt budget
while the untrusted runtime references retain the full-fidelity result. The
Worker rejects unknown, duplicate, or digest-mismatched contracts before
provider execution. When the model's own reply cannot be assembled into a call
(incomplete arguments, more than one call, or an unadvertised tool) the step is
retried inside the bounded attempt budget and the next attempt carries a short
`[harness]` note naming the rejection, so the model repairs its own selection
instead of failing the run; wire and invariant violations remain terminal.

The Host reads `Ru.md`, `.ru/AGENTS.md`, and `.ru/CONTEXT.md` below the
selected run root, then discovers direct-child Markdown skill directories from
`<workspace-root>/skills`, `<workspace-root>/.ru/skills`, and
`%LOCALAPPDATA%\Ru\skills`, in that order. A directory is a Markdown skill
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
instructions and Markdown skills are followed only as guidance; they cannot
grant tools, permissions, approvals, paths, network access, or preset changes.
The Markdown `allowed-tools` field is descriptive only. Runtime
references and tool/search results remain explicitly untrusted.

Markdown skills are prompt guidance, not executable workflows. The existing
declarative `SkillManifest` format (`skill.json` plus `instructions.md`) remains
the only path for `/skills` and `/skill` execution and still requires an
explicit session grant. `RuAgentBroker` remains the sole authority for path
canonicalization, capability policy, approvals, and tool execution.

The Worker returns only a versioned SHA-256 receipt. Core validates it against
the exact context before committing model completion or dispatching tools, then
stores the receipt as an `InternalOnly` payload. Public Agent SSE suppresses
`model.prompt_composed` and its payload while advancing the durable cursor.
Invalid context or receipt metadata fails closed with `prompt_contract_invalid`;
there is no static-prompt fallback or reduced-context retry. Managed runtime
wiring enables the standalone `web_search`/`web_fetch` tools by default;
no settings field controls their exposure.

Prompt composition is not a security boundary: Broker policy, workspace trust,
approvals, protected paths, external-directory grants, and cancellation remain
authoritative. Declarative Skills are available only through explicit session
grants; subagent orchestration, LSP, extension/plugin/MCP discovery, arbitrary
system overrides, Chat compatibility, UI redesign, and the OS-enforced
filesystem/network sandbox are outside this implementation.

When Ru needs product knowledge about its own features, tools, configuration,
providers, or operating boundaries, it selects `read` with `op=help` (`read@1.0.0`). The op
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
baseline, a flat sidebar, and a centered document-style reading column.
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
native TextBox caret/focus behavior and exposes no mode selector; there is no
preset picker, Chat mode switch, or workspace editor in the composer.
The UI uses the known project root as the initial Agent workspace and does not
expose a workspace editor, while the authenticated API still validates the
canonical path. All submissions use the authenticated `/agent/v1/*` API/SSE path.
Workspace trust, approval, Stop, Continue, and bounded
Agent status remain inline above the input. While a run is active, the transcript
may also show a separate transient activity feed above the assistant content,
driven by typed `agent.activity` SSE frames. It renders only safe state,
basename/line-range, symbol/count,
edit-count, duration, and safe-code fields; it is cleared on terminal,
cancellation, failure, disconnect, session replacement, or reset. Activity
frames do not enter durable history or audit. The projector covers nine kinds,
including `compaction` (bounded covered-entry count and elapsed time) and
`retry` (bounded read-only tool transport retry attempt), plus a generic
`status` fallback. Agent payloads and raw tool output
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
If an Agent API call reports `session_not_found` or `invalid_session_id`, the
desktop creates one replacement session and retries once only before a run is
created. A second recovery failure is shown as a bounded error and is never
looped or filled with an earlier response.

## Desktop runtime lifecycle and reconnection

The WinUI 3 desktop client connects to the shared local Public Host via loopback IPC.
`ManagedRuBackendSessionProvider` tracks the Host process lifecycle and coordinates runtime liveness:
- Liveness detection: `HealthStatusService` probes loopback status with a bounded 3-second timeout via `CheckHealthDetailedAsync`, distinguishing genuine Host offline state (`HttpRequestException` or connection refused) from worker initialization or readiness. Stale or disconnected sessions accurately mark `.NET Backend: Error`.
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

The Worker v2 settings advertise six bindings: `read` with
`op=list|glob|search|read|help`, `write` with
`op=write|delete|restore`, `edit`, `bash` with `mode=run|shell`, plus standalone
`web_search` (`query` 1–512 chars, optional `max_results` 1–10, default 10) and
`web_fetch` (`url`). Broker policy automatically allows file tools in the trusted root
or a folder granted to the same live session and applies analyzer decisions to
terminal requests. Protected paths and hard invariants remain gated. Terminal execution runs as the current Windows user and is intentionally
not an OS-enforced filesystem/network sandbox. File tools may
request an explicit absolute path outside the project; the user can grant that
canonical directory to all tools in the current live session. The grant is not
shared with a new or forked session and disappears on restart. Terminal
deletion, external mutation (network egress, package installs, push/fetch),
extended timeouts beyond the standard window, shell scripts, web calls when
approval-gated, and unknown executable launch require approval.
The model selection channel is active for every Agent run: one budget window
allows up to 128 model steps and 256 model-initiated tool calls through the
Agent Broker pipeline (`AgentBudgetPolicy.ModelStepLimit`/`ToolCallLimit`).
Exhausting a window moves the run to `WaitingUser`; only an explicit Continue
opens a fresh window. Selection
JSON is never streamed into
the visible transcript, history, mood state, or diagnostics; a malformed
selection ends the turn with fixed copy and one bounded diagnostic.
External-folder requests and terminal deletion or unknown-executable requests
use bounded approval cards. The Public Host opens
one profile-scoped Broker session per turn loop, and RuAgentBroker executes and
verifies adapters. Tool-class successes require an invoked adapter. Python does
not own desktop side effects.

The Host serializes one active Broker grant: a matching valid session is reused;
a replacement Agent session closes the old grant before open. Failure is
fail-closed and never reaches the native adapter.

Non-success tool outcomes are represented as bounded Agent activity and durable
run state. Raw tool receipts never cross into the visible transcript, history,
reviewed examples, or public payload metadata.

The six Worker v2 bindings above are the model surface, with standalone
`web_search`/`web_fetch` tools managed by startup. Managed
startup constructs an enabled WebOptions instance with Google HTML primary
`https://www.google.com/search` and automatic DuckDuckGo HTML fallback
`https://html.duckduckgo.com/html/`; their bounded responses carry an
explicit `untrusted` marker and never become instructions. `read` with
`op=search` searches the workspace, while `web_search` searches
the public web. `read` with `op=help`
searches only bundled Ru documentation. Unbound connectors and `app.open`
remain outside the model-visible catalog.
Declarative Skills are added only from validated manifests explicitly included
in the session grant; each Skill step re-enters normal tool scope, schema,
approval, timeout, and output limits. Executable Skill package roots are not
auto-loaded; the separate Markdown skill roots are prompt guidance only.
The managed Git commit workflow is shipped inside terminal execution, using a
temporary index and refusing dirty user index state. Other Git commands remain
ordinary terminal requests governed by Broker policy. The
GUI renders the actual `agent.session` grant received from the Host. Managed
startup enables the bounded WebOptions instance with Google primary and DuckDuckGo
fallback, so standalone `web_search` and `web_fetch` are advertised as
read-only managed capabilities without a settings flag or separate deployment
switch; Broker
policy remains authoritative for each call. The tested MCP bridge and the
mail/calendar/task connector libraries are not production-composed or
advertised as Agent tools.

### Typed commands

The authenticated `POST /agent/v1/runs/{runId}/commands` route handles
`/compact normal|aggressive`, `/help`, `/skills`, and
`/skill <id> <json-object>`. `AgentActionRouter` is shared by Public Host and
CLI, bounds input, rejects unknown or malformed slash commands with stable
codes, and prevents command text from entering prompts, history, or the
conversation outbox. `/compact` uses the durable context-compaction contract;
the other commands return bounded static or session-scoped data.

### Web access

The managed Agent constructs an enabled WebOptions object with Google HTML
primary `https://www.google.com/search` and DuckDuckGo HTML automatic fallback
`https://html.duckduckgo.com/html/`. It
maps standalone `web_search` to `network.search` and standalone
`web_fetch` to `network.fetch`;
the normal Harness policy remains authoritative for each read-only call.
Redirects are disabled or validated at every hop, private/reserved targets are
rejected, and response bytes, content types, UTF-8, sanitizer output, and
citation count are capped. Web payloads are data only and cannot grant tools,
permissions, paths, approvals, or preset changes; embedded URL credentials
are rejected as well. Fetched text is wrapped with
`[untrusted_web_reference]`. These gates are not an OS-enforced network
sandbox.

Declarative Skills remain the only executable workflow extension surface in
this checkpoint; Markdown skills are untrusted prompt guidance. Subagents,
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
and reviewed-example policy. Business records live in the canonical
`%LOCALAPPDATA%\Ru` location (see
`docs/adr/0004-canonical-ru-data-location.md`). Windows Credential
Manager remains the secret store.

Logs, telemetry, manifests, and automated evidence may contain fixed event
types, counts, durations, hashes, and statuses only. They must not contain
credentials, bearer values, request bodies, user/session identifiers,
transcripts, or private marker metadata.

Technical diagnostics are structured JSONL under `RU_LOG_ROOT` (defaulting to
`%LOCALAPPDATA%\Ru\logs`) with bounded ASP.NET, managed-worker, test-fixture,
and WinUI writers. The HTTP boundary records fixed route categories and durations; the
diagnostics API rejects invalid/oversized queries and caps exports at 2 MB.
Activity Timeline writes continue through the private worker outbox and
ASP.NET ingestion path, with read-time re-sanitization for older records.

## Acceptance boundary

Automated tests can establish routing, protocol ordering, bounds, cancellation,
privacy filtering, persistence semantics, and process
lifecycle. They cannot establish the effect of an arbitrary terminal command
on a live external application or device, provider quality, or
natural bilingual dialogue. Those
remain explicit user-run checks and must be reported as pending until performed.
