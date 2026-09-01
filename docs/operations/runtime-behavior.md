# Current runtime behavior

Status: operating contract for the current main branch, 2026-09-01.

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
emoticons. Search commands, model controls, voice pace, VTS actions, segment
identifiers, and transport timings are private metadata and must be removed
before display, TTS, history, memory, logs, or reviewed examples.

Each Agent turn carries a bounded `correlation_id` and a durable run/session
identity. The composer keeps one active run; reconnecting to the Agent SSE
stream replays only durable events and never duplicates a committed history
entry. Stale or malformed events are ignored by the presenter, so a late reply
cannot update a newer bubble. A disconnect marks the active run as a bounded
retryable connection error; Ru does not automatically replay an ambiguous
request and risk duplicate history.

The managed C# Worker v1 protocol is the default runtime/device adapter
boundary for runtime load/request, voice/STT, VTS, observation, and health. It
rejects the retired conversation message types as `unknown_type`; Agent
generation uses the same process through the managed v2 step protocol. The two
private ports and tokens remain distinct even though the process is shared. The
v1 graph receives typed settings and adapters, never the provider credential.

Managed startup launches one `RuAgent.Runtime.Worker.exe` with
`ws://127.0.0.1:8005/worker/v2` and `ws://127.0.0.1:8006/worker/v1`, followed
by the private Broker. A neutral single-use DPAPI startup handoff composes the
C# v2 graph without reading or retaining a provider credential. Each Agent
step requires a separate protected handoff containing
the run-pinned provider selection, bounded protocol
(`chat_completions`/`openai_responses`/`anthropic_messages`), verified native
reasoning options, and for Anthropic the bounded `max_tokens`; changes to
settings apply to the next run, not a continuation of the active run.
Each handoff is deleted after consumption. The Worker dispatches exactly the
pinned protocol through one of three focused adapters; unknown or mismatched
protocols fail closed before HTTP. The retired `PythonCompatibility` selector
fails closed with `python_compatibility_retired`; `runtime-live` is a bounded
diagnostic without starting a child topology. There is no Python compatibility
startup path.

### Agent system-prompt composition

Every accepted Agent model step—typed text, microphone/voice, coding, tool
selection, and continuation—rebuilds a fresh prompt context before the Worker
request. The private Generation Kernel emits exactly one `system` message from
these ordered layers: Ru persona and safety, Agent execution contract,
environment, root-selected Ru instruction files, profile/preset, validated
host capabilities, and bounded runtime context. The model then receives the
ordered user/assistant/tool turns; clock, mood, memory, voice/VTS state, and
references are not appended as separate system messages.

The Host reads only `Ru.md`, `.ru/AGENTS.md`, and `.ru/CONTEXT.md` below the
selected run root. It applies final-path containment, strict UTF-8 decoding,
NFC/newline normalization, a 64 KiB per-file limit, and a 256 KiB combined
instruction limit. Context values remain bounded (100 KiB memory, 32 KiB per
reference, and 512 KiB serialized context). Instruction text, memory, and
tool/search results are model data marked untrusted; they cannot grant tools,
permissions, approvals, paths, network access, or preset changes.

The Worker returns only a versioned SHA-256 receipt. Core validates it against
the exact context before committing model completion or dispatching tools, then
stores the receipt as an `InternalOnly` payload. Public Agent SSE suppresses
`model.prompt_composed` and its payload while advancing the durable cursor.
Invalid context or receipt metadata fails closed with `prompt_contract_invalid`;
there is no static-prompt fallback or reduced-context retry. The managed Host's
enabled web executor advertises both `web.search` and `web.fetch`; no settings
field controls their exposure.

Prompt composition is not a security boundary: Broker policy, workspace trust,
approvals, protected paths, external-directory grants, and cancellation remain
authoritative. Declarative Skills are available only through explicit session
grants; subagent orchestration, LSP, extension/plugin/MCP discovery, arbitrary
system overrides, Chat compatibility, UI redesign, and the OS-enforced
filesystem/network sandbox are outside this implementation.

## Voice output

OmniVoice is the supported local TTS engine. Today the managed C# Worker owns
its lifecycle and reaches the isolated `omnivoice_env` service over loopback
HTTP. Architecture v2 later replaces that listener with a
bounded framed child-process protocol; the target is not current behavior yet.

Only the `original` voice style is accepted. Exactly 1.00x omits native speed
adjustment and remains the clone-quality reference. Other bounded speeds use
OmniVoice's native generation parameter; no local pitch shift, waveform DSP,
or time stretching is allowed. Non-1.00x output requires human listening before
any quality claim.

Voice streaming uses ordered WAV chunks, cancellation, playback
acknowledgements, and a complete-WAV fallback only when streaming fails before
the first chunk. The desktop starts `MediaPlayer.Play()` before notifying the
capacity-one subtitle event pump. Subtitle rendering is fail-open: a blocked,
failed, stale, or disabled subtitle path may drop text but cannot delay audio,
clear a newer cue, or fail playback.

Generation and OmniVoice do not own the GPU simultaneously. A voice failure is
reported as unavailable and never silently changes the saved engine/profile.

## Speech input and VTube Studio

Microphone capture is bounded 16 kHz mono PCM WAV. In WinUI the primary
microphone button starts an explicit, user-controlled half-duplex conversation:
the local AudioGraph/VAD capture waits for one turn, normalizes variable
AudioGraph quantum sizes into the detector frame size, sends only a non-empty
transcript through the existing `/voice/transcribe` Google `vi-VN` path, submits
it to the Agent HTTP/SSE path, and waits for the existing reply-completion
boundary before opening the microphone again. The button, typed Send, session
switch, and view shutdown cancel the loop; cancellation is idempotent and a
late STT result cannot create a prompt. The microphone remains closed while Ru
speaks, so speaker audio is not fed back into STT.

Raw PCM frames and transcripts are held only in memory for the active turn.
Audio leaves the machine for the explicitly requested STT operation and is not
persisted by Ru. A no-speech turn returns to Listening without sending an empty
prompt. A capture whose input stays near full scale with no level dips for the
first ~500 ms ends as an unusable-input error with a visible microphone message
instead of looping sixty seconds of noise through STT; a broken or corrupted
microphone stream therefore stops the loop instead of silently retrying. The
controller and VAD are covered by deterministic tests; physical
microphone accuracy, speaker output, and live VTube Studio behavior remain
manual acceptance gates.

VTube Studio hotkeys and expressions are resolved from the connected model's
current catalog. The runtime revalidates catalog codes before dispatch and
removes private action suffixes before TTS, history, memory, and approved
examples. The managed Worker also exposes runtime-scoped GUI state/config/connect
surface, persists its authentication token at canonical `pyvts_token.txt`,
and honors `RU_DATA_ROOT` for isolated runs; enabling the optional overlay is
fail-closed, while overlay process
ownership and semantic facial/body actions remain explicitly
unavailable until their typed adapters are added. `VoiceVolume` is the lip-sync
input. `LLM_MouthOpen` must not be mapped or recreated. Live model connection,
action, lip-sync, and subtitle appearance remain user-run acceptance checks.

## Observation and desktop overlay

Screen observation is opt-in. The managed Worker owns the bounded observation
analysis adapter, but its default v1 control handler is fail-closed: status is
`disabled`, start/react report a stable unavailable result, and no screen frame,
credential, or reaction is fabricated. A managed Windows capture/reaction loop
with a trusted Gemini credential channel is still pending; the legacy Python
observer is not reachable from managed startup.
When that loop exists, WinUI will poll the bounded status contract and pass each
event through `VoicePlaybackService`, so MediaPlayer playback, subtitles, VTS
speaking state, and acknowledgement share the normal voice path. Missing
credentials, stale events, or a busy Agent run must report a skip/error and must
not pretend that capture, reaction, or audio playback occurred.

`RuDesktopOverlay` is an optional separate Spout2 receiver. It does not own
RuApp, VTube Studio, the model, VTS actions, or voice. Building the receiver may
use a local vcpkg cache; the cache is build infrastructure, not application
data or a distributable artifact.

## WinUI responsive shell

The WinUI shell has three presentation tiers based on client width: Wide at
1280 px and above, Compact from 900 through 1279 px, and Narrow below 900 px.
Wide keeps the resizable 180–400 px sidebar; Compact keeps labels with tighter
spacing and 180–320 px bounds; Narrow uses a 72 px icon rail and stacks page
controls. These transitions are presentation-only and do not call the backend,
restart polling, recreate list sources, or alter audio/VTS behavior.

Automated tests cover policy boundaries, XAML state markers, vertical-only list
surfaces, and build correctness. Visual resizing at representative window sizes
remains a user-run acceptance check.

The current Agent composer uses the same tiers. The shell and conversation view use the
Editorial reading room presentation: the approved light mineral palette as the
baseline, a flat sidebar, and a centered document-style reading column.
Voice/VTS, Log, Diagnostics, Settings, and Memory retain their existing layout
in this phase while inheriting the active shared Light or Dark token set.
The application keeps Light as its fallback and MainWindow can request the
active theme on the shell root so default controls follow the same contrast.

The appearance action in the sidebar footer switches Light and Dark
synchronously. The selected appearance is UI-only state stored at
`%LOCALAPPDATA%\Ru\ui-preferences.json`; it is not an ASP.NET setting or model
configuration. Switching remains presentation-only for application behavior:
it does not recreate message or session sources, call the backend during
layout, alter WebSocket ordering, change STT, voice playback, subtitles, or
VTube Studio control. Automated source/XAML contracts and the WinUI build cover
structural correctness. Visual resizing and live audio/VTS behavior remain
manual acceptance checks.

The current Ru signature layer is also presentation-only: a quiet title-bar
wordmark, an editorial welcome eyebrow, message edge rules, and a GPU-accelerated
Composition visual field in the empty conversation state. The background features
gentle twinkling starlight points and periodic shooting star streaks rendered directly
via `Microsoft.UI.Composition` (`SpriteVisual`, `CompositionLinearGradientBrush` with
soft halo glow and core beam), runs only while `Messages.Count == 0`, and stops and
clears every visual element as soon as the first message exists. The composer keeps the
native TextBox caret/focus behavior and contains one static Agent indicator plus
a compact Agent preset selector with `Standard` and `Full Access`; there is no
Chat mode switch or workspace editor in the composer.
The UI uses the known project root as the initial Agent workspace and does not
expose a workspace editor, while the authenticated API still validates the
canonical path. All submissions use the authenticated `/agent/v1/*` API/SSE path.
Workspace trust, approval, Stop, Continue, and bounded
Agent status remain inline above the input. While a run is active, the transcript
may also show a separate transient activity feed driven by typed `agent.activity`
SSE frames. It renders only safe state, basename/line-range, symbol/count,
edit-count, duration, and safe-code fields; it is cleared on terminal,
cancellation, failure, disconnect, session replacement, or reset. Activity
frames do not enter durable history or audit. Agent payloads and raw tool output
never enter TTS, VTS metadata, memory, or automation names.
Assistant text is delivered separately as a typed `assistant.delta` transient
frame. The client validates the outer kind, bounded UTF-8 text, model-step id,
optional execution id, and positive transient sequence before dispatching it;
unknown fields, malformed JSON, raw tool output, and replayed sequence keys are
dropped without changing the durable cursor.
Agent final replies are normalized through the metadata policy before the
authenticated payload and conversation outbox are committed; private VTS,
voice, and action markers therefore never enter history or model context.
Validated VTS effects and the safe assistant activity projection run after that
commit, so a non-critical effect failure cannot roll back a completed Agent run.
Agent user/assistant activity records use the `agent` activity type and remain
preview-sanitized at the activity-store boundary.
When no session has been opened yet, the first Agent prompt creates a fresh
session before the preset call.
If an Agent API call reports `session_not_found` or `invalid_session_id`, the
desktop creates one replacement session and retries once only before a run is
created. A second recovery failure is shown as a bounded error and is never
looped or filled with an earlier response.

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

The Worker v2 settings currently advertise the same ten core bindings plus the
managed read-only web bindings in both presets: `files.list`, `files.read`,
`files.glob`, `files.search`, `files.write`, `files.edit`, `files.delete`,
`files.restore`, `terminal.run`, `terminal.shell`, `web.search`, and
`web.fetch`. Standard and Full Access differ in Broker policy, not in the
model-visible list. Standard automatically allows ordinary trusted-workspace
file reads/creates/edits and applies analyzer/rule/approval decisions to
terminal requests; Full Access uses a broader fallback for well-formed calls,
while protected paths, hard invariants, and extended timeout thresholds remain
gated. Terminal execution runs as the current Windows user and is intentionally
not an OS-enforced filesystem/network sandbox. File tools may
request an explicit absolute path outside the project; the user can grant that
canonical directory for the current run or project. That grant covers the
directory's read/create/edit operations only; protected and destructive
operations keep their own approval gates. `files.apply_patch`
is not advertised or model-usable in the current runtime.
Explicit, unambiguous volume commands use a deterministic parser before
generation in Agent Standard and Full Access. The parser creates one typed
`system.volume@1.0.0` request and does not call the configured model for action
selection; volume is not in the Worker v2 model-tool list. Ambiguous volume
wording returns a bounded clarification.

The model selection channel is active for every Agent run: each step allows up
to ten model-initiated tool calls through the Agent Broker pipeline. Selection
JSON is never streamed into
the visible transcript, history, mood state, or diagnostics; a malformed
selection ends the turn with fixed copy and one bounded diagnostic.
Mutating tools (`files.write`, `files.edit`, `files.delete`, `files.restore`,
and terminal requests that the active policy marks `ask`) use bounded approval
cards; reads and verified volume operations do not. `notes.create` is Broker-
internal and is not model-selectable. The Public Host opens
one profile-scoped Broker session per turn loop, and RuAgentBroker executes and
verifies adapters. The Worker may render native-volume success only from a
matching `succeeded` receipt whose verification state is `verified` and whose
readback matches;
tool-class successes require an invoked adapter without readback. Python does
not own desktop side effects.

The Host serializes one active Broker grant: a matching valid session is reused;
a replacement Agent session closes the old grant before open. Failure is
fail-closed and never reaches the native adapter.

Non-success tool outcomes are represented as bounded Agent activity and durable
run state. Raw tool receipts never cross into the visible transcript, history,
memory, reviewed examples, TTS, or VTS metadata.

The ten Worker v2 bindings above are the core model surface, and the managed
Host also advertises read-only `web.search`/`web.fetch` bindings. Managed
startup constructs an enabled WebOptions instance with the fixed
`https://html.duckduckgo.com/html/` endpoint; their bounded responses carry an
explicit `untrusted` marker and never become instructions. `files.search`
searches the workspace, while `web.search` searches the public web. `notes.create`,
read-only system bindings, unbound connectors, and `app.open` remain
Broker-internal.
Declarative Skills are added only from validated manifests explicitly included
in the session grant; each Skill step re-enters normal tool scope, schema,
approval, timeout, and output limits. Skill package roots are not auto-loaded.
The managed Git commit workflow (Phase 3 Task 6) is not shipped; Git commands
are ordinary terminal requests governed by the active preset and policy. The
GUI renders the actual `agent.session` grant received from the Host. Managed
startup enables the bounded WebOptions instance with the fixed DuckDuckGo
endpoint, so `web.search` and `web.fetch` are advertised as read-only managed
capabilities without a settings flag or separate deployment switch; normal
Harness policy remains authoritative for each call.

### Typed commands

The authenticated `POST /agent/v1/runs/{runId}/commands` route handles
`/compact normal|aggressive`, `/help`, `/skills`, and
`/skill <id> <json-object>`. `AgentActionRouter` is shared by Public Host and
CLI, bounds input, rejects unknown or malformed slash commands with stable
codes, and prevents command text from entering prompts, history, or the
conversation outbox. `/compact` uses the durable context-compaction contract;
the other commands return bounded static or session-scoped data.

### Web access

The managed Agent constructs an enabled WebOptions object with the public
DuckDuckGo HTML search endpoint `https://html.duckduckgo.com/html/`. It
advertises `web.search` as `network.search` and `web.fetch` as `network.fetch`;
the normal Harness policy remains authoritative for each read-only call.
Redirects are disabled or validated at every hop, private/reserved targets are
rejected, and response bytes, content types, UTF-8, sanitizer output, and
citation count are capped. Web payloads are data only and cannot grant tools,
permissions, paths, approvals, or preset changes; embedded URL credentials
are rejected as well. Fetched text is wrapped with
`[untrusted_web_reference]`. These gates are not an OS-enforced network
sandbox.

Declarative Skills are the only workflow extension surface in this checkpoint;
subagents, LSP, extensions, plugins, and MCP are not loaded or discoverable.

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

ASP.NET Core owns settings, credentials, sessions/history, activity, structured
memory, and reviewed-example policy. Business records live in the canonical
`%LOCALAPPDATA%\Ru` location; the transactional migration from the legacy
location completed on 2026-08-17 (see
`docs/adr/0004-canonical-ru-data-location.md`). Windows Credential
Manager remains the secret store.

Logs, telemetry, manifests, and automated evidence may contain fixed event
types, counts, durations, hashes, and statuses only. They must not contain
credentials, bearer values, request bodies, user/session identifiers,
transcripts, raw audio, assistant responses, or private VTS/voice metadata.

Technical diagnostics are structured JSONL under `RU_LOG_ROOT` (defaulting to
`%LOCALAPPDATA%\Ru\logs`) with bounded ASP.NET, managed-worker, test-fixture,
and WinUI writers. The HTTP boundary records fixed route categories and durations; the
diagnostics API rejects invalid/oversized queries and caps exports at 2 MB.
Activity Timeline writes continue through the private worker outbox and
ASP.NET ingestion path, with read-time re-sanitization for legacy records.

## Acceptance boundary

Automated tests can establish routing, protocol ordering, bounds, cancellation,
privacy filtering, persistence semantics, non-empty WAV structure, and process
lifecycle. They cannot establish an audible Windows volume change, speaker
quality, physical microphone accuracy, natural bilingual dialogue, or live
VTube Studio behavior. Those remain explicit user-run checks and must be
reported as pending until performed.
