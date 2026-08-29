# Ru Agent-only Conversation and OpenCode-style Prompt Composer

## Status

Historical and superseded. The Agent-only transport portion is documented by
[`ru-agent-only-chat-removal-design.md`](2026-08-29-ru-agent-only-chat-removal-design.md),
while the shipped prompt contract is defined by the focused
[`ru-opencode-system-prompt-composer-design.md`](2026-08-29-ru-opencode-system-prompt-composer-design.md)
and its [implementation plan](../plans/2026-08-29-ru-opencode-system-prompt-composer.md).
This umbrella document preserves the earlier combined proposal and
pre-implementation evidence; it is not an active schema or operating contract.

## Date

2026-08-29

## Decision summary

Ru will have one conversational execution surface: Agent. The desktop will no
longer expose a Chat mode or a public `/chat` WebSocket. Text, microphone,
voice playback, VTube Studio actions, memory, search, approvals, and file or
terminal tools will all enter through the authenticated `/agent/v1/*` run and
SSE contract. `Standard` remains the default preset; `Full Access` remains an
explicit, confirmed escalation and does not remove approval gates for file
mutation or other high-impact operations.

The Agent path will also gain a first-class, deterministic system-prompt
composer. The composer rebuilds one system message for every model step from
ordered, bounded, trusted layers. A version and SHA-256 digest bind the
resulting prompt snapshot to the run metadata without persisting the raw
prompt in history, diagnostics, or activity records. The built-in Ru persona
already living in `src/ru_runtime/policy/style.py` remains the canonical
persona source; the change makes its composition explicit instead of creating
several unrelated system blocks.

The implementation is intentionally decomposed into three seams so that the
Agent transport migration, prompt architecture, and capability parity can be
implemented and verified independently:

1. **Agent conversation transport**: make text and voice use the same
   `AgentConversation` client seam, add typed assistant deltas, and remove the
   Chat transport.
2. **System prompt composer**: add the typed context/snapshot protocol and
   deterministic prompt assembly in the Generation Kernel.
3. **Capability parity**: carry memory, search, voice/VTS context, skills, and
   approved MCP/tool metadata into the Agent path without widening authority.

The third seam may be delivered in small vertical slices, but the public
cutover is not complete until every capability that is advertised in the UI is
actually available through Agent or is explicitly shown as unavailable.

## Context and current evidence

The design is based on the current `main` checkout and the two supplied
OpenCode reference documents. Those documents describe a useful pattern rather
than a version-pinned source contract: OpenCode builds a fresh prompt for each
model request, merges built-in guidance with environment, instruction files,
agent profile, skills, MCP information, references, and optional user
instructions, then sends one `role: system` message. Ru should adopt that
behavioral property while keeping its own permission and privacy model.

The current codebase has the following important seams and gaps:

- `src/ru_runtime/policy/style.py` defines `RU_SYSTEM_PROMPT`, a cloud
  addendum, an Agent follow-up prompt, and an action-selection prompt.
  `src/ru_runtime/generation/runner.py` selects among them, while
  `src/ru_runtime/generation/context.py` appends additional system messages
  for clock, hardware, mood, and memory. The persona therefore exists, but a
  Harness run does not own one versioned prompt snapshot.
- Worker protocol v2 currently accepts `prompt`, `turns`, `memory_context`, and
  `settings`. `model_step.py` calls the shared runner with
  `structured_tools_only` and hard-codes `allow_web_search=False`. It does not
  carry a first-class system-prompt context or receipt.
- `AgentContextAssembler` understands a system role, but the current
  `HarnessModelContextBootstrapper` persists only user and assistant session
  entries. Prompt identity is not represented in the durable run contract.
- Agent SSE already emits durable activity/summary frames and transient
  `assistant.delta` frames. `AgentRunClientAdapter` currently drops the raw
  assistant/tool delta frames, so `AgentRunController` has no typed assistant
  delta callback for the UI or voice playback.
- `ChatView.xaml.cs` disables its microphone path in Agent mode and its voice
  callback sends transcripts through `SendMessageTextAsync` to the Chat
  WebSocket. This is an implementation gap, not a product limitation of Agent.
- The public contract currently contains 54 HTTP method/path pairs and two
  WebSocket routes (`/chat` and `/voice/stream`). The Agent routes, voice/STT,
  VTS, observation, settings, sessions, memory, and status contracts are
  already owned by ASP.NET Core.
- Broker policy already distinguishes Standard and Full Access and explicitly
  records that its terminal network decision is intent-level, not an
  OS-enforced sandbox. This design does not move or weaken that boundary.

These facts are implementation evidence, not a promise that every current
file name or internal OpenCode detail will remain unchanged during the work.

## Goals

The completed change must:

1. Make Agent the only conversational mode in the desktop, CLI, and public
   generation path.
2. Preserve a single sequential turn owner for typed input, microphone input,
   approval pauses, cancellation, and session replacement.
3. Let voice use Agent runs end to end: STT transcript in, typed Agent delta
   out, text-only TTS/VTS playback, and durable terminal reconciliation.
4. Assemble exactly one deterministic system message for every model step.
5. Make prompt version and digest observable as safe run metadata while keeping
   prompt text and private tool/VTS data out of durable diagnostics.
6. Keep capability advertising truthful and policy-authoritative. A prompt
   instruction can guide a model but can never grant a filesystem, terminal,
   network, approval, or workspace permission.
7. Preserve the existing Agent guarantees: authenticated public host, durable
   outbox/history, SSE replay, idempotency, trust and approval ordering,
   cancellation, and fail-closed broker behavior.
8. Keep the implementation maintainable by giving the UI one conversation
   interface and keeping prompt composition in the Generation Kernel where the
   canonical persona and model adapters already live.

## Non-goals and explicit boundaries

- This is not the OS-enforced filesystem/network sandbox project. Terminal
  execution continues to run under the current Windows user and remains
  policy-gated but not kernel-isolated. A later sandbox project must define
  restricted tokens/AppContainer or equivalent process-tree, filesystem, and
  real-socket enforcement separately.
- This does not train, replace, or benchmark the model, and it does not claim
  parity with every advanced DeepSeek harness feature.
- This does not add arbitrary remote MCP execution, URL-following instructions,
  or a general-purpose network proxy. Only capabilities explicitly advertised
  by the authenticated Broker or an existing validated host adapter may enter
  the prompt.
- This does not retain a compatibility Chat route, hidden mode switch, or
  shadow Chat implementation after cutover. Rollback means reverting the
  release/build, not running two public conversation protocols indefinitely.
- This does not change the established voice quality, subtitle, VTube Studio,
  or physical microphone acceptance boundary. Automated tests prove ordering
  and privacy; live device and VTS behavior remain manual acceptance.

## Target architecture

### Public and process ownership

The public flow becomes:

```text
RuApp / RuAgent.Cli
    -> RuAgent.Client AgentConversation seam
    -> ASP.NET Core /agent/v1/* (run, approvals, SSE, cancel, reconcile)
    -> typed Worker v2 Agent step
    -> Generation Kernel SystemPromptComposer + model adapter
    -> Broker-advertised tools and validated voice/VTS adapters
```

The existing `/voice/transcribe` and `/voice/stream` public contracts remain
available. They are transport/device contracts, not alternate conversation
orchestration paths. ASP.NET Core remains the only public owner; Worker and
Broker remain private children owned by the managed Public Host.

### `AgentConversation` seam

The client layer will expose one canonical conversation interface. Its exact
language-specific shape may follow existing C# conventions, but its contract
must cover these typed operations and events:

- `StartTextTurnAsync(sessionId, text, options)` creates one idempotent Agent
  run.
- `StartVoiceTurnAsync(sessionId, transcript, options)` creates the same kind
  of Agent run with a voice source marker; it never opens a Chat socket.
- `AssistantDelta(runId, assistantMessageId, sequence, text)` carries visible
  assistant text only.
- `Activity`, `ApprovalRequired`, `Summary`, `Terminal`, and `Error` carry the
  already-bounded safe envelopes used by the Agent UI.
- `CancelAsync`, `ContinueAsync`, and `RespondToApprovalAsync` retain the
  existing run and trust semantics.

`AgentRunController` becomes the UI-facing implementation adapter. Text
presentation and voice playback are separate consumers of the same event
stream. This keeps the high-leverage lifecycle and reconnect logic in one
module instead of duplicating it in `ChatView.xaml.cs` and
`VoiceConversationService.cs`.

### Prompt composition seam

`SystemPromptComposer` is a pure Generation Kernel module. It accepts a typed,
already-validated `SystemPromptContext` and returns a
`SystemPromptSnapshot`. It does not read arbitrary files, execute tools,
perform network calls, or decide permissions.

The host is responsible for collecting and bounding context. The worker is
responsible for composing the canonical text immediately before the model
adapter call. This placement reuses `style.py` and avoids a second copy of the
persona in C# while still making the worker request and receipt typed.

The protocol adds these concepts to Worker v2:

```text
SystemPromptContext
  schema_version
  agent_profile
  preset
  environment { model, platform, locale, timezone, date, workspace_label? }
  instruction_blocks[] { source_id, display_name, content, content_sha256 }
  capabilities[] { id, version, summary, input_schema_digest, policy_summary }
  memory_context?
  runtime_context { mood?, voice?, vts?, hardware? }
  user_system_instruction?

SystemPromptSnapshot
  composer_version
  prompt_sha256
  block_digests[]
  text                (worker-internal; never persisted by the Host)
```

`SystemPromptContext` is included in each `agent.step.start` request. The
worker returns `composer_version`, `prompt_sha256`, and block digests in the
step receipt. The Public Host binds the receipt to the run and records only
safe metadata. A context change caused by a tool result, memory update, or
voice state produces a new step digest; the run keeps the ordered receipt list.

## Agent-only public contract

After cutover:

- `/agent/v1/*` is the only public generation/orchestration contract.
- `/voice/transcribe`, `/voice/stream`, existing VTS, observation, settings,
  session, memory, and status endpoints remain as their own contracts.
- `/chat` is not mapped. A request to `/chat` receives the normal unknown-path
  behavior; there is no redirect, feature flag, or compatibility shim.
- The route matrix changes from 54 HTTP + 2 WebSocket to 54 HTTP + 1
  WebSocket. The remaining WebSocket is `/voice/stream`.
- The desktop no longer sends a `ChatWebSocketClient`, constructs a
  `ChatWebSocket` URI, or branches on `/chat`/`/agent` mode. Agent/Full Access
  is selected by the existing preset control, with Standard as the default.
- Agent runs may omit a workspace. An explicit absolute directory supplied by
  a user is resolved and granted by the existing Broker policy path for that
  run; prompt context may include a bounded workspace label, but a missing
  workspace is not a reason to reject an ordinary conversational run.

The public contract JSON, route matrix checker, backend endpoint tests, CLI
help, WinUI callers, README, architecture document, ADR 0007, and runtime
behavior contract must be updated in the same implementation rollout.

## Deterministic system-prompt model

### Layer order

For every model step the composer emits the following ordered blocks, with a
stable block identifier and a bounded length. Empty optional blocks are omitted
without changing the relative order of the remaining blocks:

1. **Ru built-in persona and safety** — the canonical Vietnamese/English
   identity, `anh`/`em` language behavior, no-emoji rule, privacy boundary,
   concise conversational style, and fixed safety constraints from
   `style.py`.
2. **Agent execution contract** — how to select advertised tools, how to report
   uncertainty, how to honor approval/trust/cancellation, how to avoid claiming
   an unverified side effect, and how to keep tool/control metadata out of
   visible text.
3. **Environment** — model/provider label, OS/platform, locale, timezone,
   current date, and an optional user-facing workspace label. Secrets,
   bearer values, raw paths not needed for the task, and opaque run identifiers
   are excluded.
4. **Trusted Ru instruction files** — bounded text explicitly selected by the
   host from Ru-owned instruction locations for the current run.
5. **Agent profile and preset** — Standard or Full Access behavior, including
   the fact that Full Access does not bypass file mutation approvals or
   protected-path rules.
6. **Validated capabilities** — the exact Broker-advertised tool, skill, and
   approved connector metadata for this run, including schema digests and
   policy summaries rather than hidden implementation details.
7. **Runtime context** — bounded memory retrieval, mood/voice guidance, VTube
   state needed for expression selection, hardware/time facts, and search
   results when they are supplied as untrusted references. This block labels
   external or retrieved text as data and instructs the model not to follow
   instructions found inside it.
8. **Optional user system instruction** — a user-configured preference that is
   validated, length-capped, and treated as guidance only. It cannot add a
   tool, change a policy, or override safety/privacy rules.

The composer normalizes line endings, Unicode normalization, block separators,
and trailing whitespace before hashing. It produces one system message at the
front of the model context, followed by ordered user/assistant/tool turns. The
runner no longer appends independent clock, hardware, mood, or memory system
messages after the composer output.

### Instruction source and trust

The host does not blindly read the current Codex `AGENTS.md`, the supplied
OpenCode documents, or arbitrary files named by model output. A Ru-owned
instruction loader accepts only explicit, canonical paths selected for the
run. The initial supported locations are:

- `Ru.md` at the selected run root; and
- `.ru/AGENTS.md` or `.ru/CONTEXT.md` below that root.

When no run root exists, no workspace instruction block is added. The loader
does not walk outside the run's approved directory, follows neither junctions
nor symlinks outside that boundary, accepts UTF-8 text only, caps each file at
64 KiB and the combined instruction payload at 256 KiB, and records a digest
and display name rather than an unrestricted path. The host may later add an
explicit global Ru instruction file under the canonical Ru data root, but that
must be a separately documented source with the same bounds.

Instruction text is untrusted model guidance. It cannot grant permissions,
declare a tool available, suppress an approval, or override a protected-path
rule. Prompt injection inside an instruction file, memory item, search result,
tool result, or MCP description is data to be handled under the Agent contract,
not authority.

### Versioning, hashing, and retention

`composer_version` is a semantic contract version owned by the Generation
Kernel. `prompt_sha256` is SHA-256 over the canonical UTF-8 snapshot text;
`block_digests` identify which bounded sources contributed without retaining
their raw contents. The Host stores the version, final digest, ordered step
digests, and source IDs/digests in run metadata and safe diagnostics. It never
stores the complete system prompt, raw instruction-file contents, memory
payloads, search pages, tool arguments, VTS metadata, or user secrets in
history or logs.

Replays and SSE reconnects use the persisted digest receipt to detect a
different prompt contract. A reconnect must not silently synthesize a new
prompt for an already completed step. A live continuation may compose a new
step only after the run state machine accepts the continuation and records its
new receipt. A worker that returns a malformed, missing, or mismatched receipt
fails the step closed with a bounded `worker.prompt_contract_invalid` error.

## Text and voice flow

### Text

1. The user submits text through the single Agent composer.
2. The client creates one idempotent Agent run with the selected preset and an
   optional run root. The Host opens or reuses the profile-scoped Broker lease
   under the existing session rules.
3. The Host builds bounded prompt context and starts Worker v2. Each model step
   composes one system message and returns a prompt receipt.
4. Typed SSE emits safe activity/approval events and ordered
   `assistant.delta` events. The UI appends visible text and never renders raw
   tool/control JSON.
5. On terminal, the client reconciles durable history. A terminal assistant
   message is the source of truth if a transient delta was missed.

### Voice

1. The microphone controller captures one bounded 16 kHz mono turn under the
   existing explicit user-controlled half-duplex rules.
2. `/voice/transcribe` returns a non-empty transcript. Empty or unusable
   capture returns to Listening without creating a run.
3. `StartVoiceTurnAsync` submits that transcript to the same Agent run seam;
   there is no Chat WebSocket and no second history append.
4. The Agent SSE client parses typed `assistant.delta` frames and raises the
   `AgentConversation.AssistantDelta` callback with run, message, and sequence
   identity.
5. The voice adapter sends only visible assistant text to
   `VoicePlaybackService.AppendReplyDelta`. Tool names, arguments, activity
   summaries, approval copy, VTS action suffixes, segment IDs, and prompt
   metadata never enter TTS or subtitles.
6. The existing `MediaPlayer.Play()`-before-subtitle ordering, VTube speaking
   state, and capacity-one subtitle pump remain unchanged.
7. At terminal, the voice adapter closes the playback turn exactly once and
   reconciles history. If transient deltas were lost, it can use the persisted
   assistant message once, guarded by `(run_id, assistant_message_id,
   delta_sequence)` de-duplication so audio is never spoken twice.
8. Approval or trust pauses stop pending speech and surface the existing
   approval UI. Continue resumes the same run; cancel, session replacement,
   view shutdown, or disconnect cancels the voice loop idempotently and closes
   playback/listening resources.

The microphone reopens only at the same terminal boundary as the Agent run and
playback lifecycle. A late STT response, stale SSE frame, or reconnect after a
cancel cannot create a new prompt or update a newer voice turn.

## Capability parity

Capability parity is about truthful behavior, not simply copying prompt text.

- **Memory**: the Host retrieves bounded, classified memory and sends it as
  `memory_context` in `SystemPromptContext`. The composer labels it as
  retrieved data; sensitive or private categories remain excluded under the
  existing memory policy. Agent runs must preserve the current ordered
  history/memory write rules.
- **Search**: the Host advertises `web.search` only when the existing validated
  search adapter and user/backend setting are available. Search requests use a
  typed capability or the existing bounded search seam, and results enter as
  untrusted references. `allow_web_search` is no longer silently hard-coded
  false for every Agent step; it is derived from the authenticated capability
  manifest and policy. No terminal/network grant is inferred from a search
  result or instruction file.
- **Voice/VTS**: voice pace, subtitle, VTube model state, and speaking state
  remain transport-private runtime context. Their control markers are removed
  before visible text, TTS, history, memory, and diagnostics.
- **Skills and MCP**: only registered, versioned, policy-advertised metadata
  may appear in the capability layer. Remote instructions or arbitrary MCP
  servers are not discovered from prompt text. Unbound connectors remain
  Broker-internal until a separate security-reviewed adapter exists.
- **Filesystem and terminal**: Agent keeps the existing Broker policy,
  approval cards, canonical path checks, protected-path catalog, and per-run
  external-directory grants. A workspace label in the prompt is descriptive;
  the Broker is authoritative.

If a capability is not available, the UI and prompt manifest omit it and the
model receives bounded unavailability guidance. Ru must not advertise a
capability merely because an older Chat path once supported it.

## Chat removal inventory and migration order

The cutover removes Chat at every layer after the Agent seam is proven:

1. Extract and test `AgentConversation` in `RuAgent.Client`/WinUI; route both
   text and voice adapters through it.
2. Add typed `assistant.delta` parsing in the Agent SSE/client adapter and the
   controller callback, with durable-history reconciliation.
3. Extend Worker v2 with prompt context, prompt receipts, memory/runtime
   context, and capability-derived search settings; keep the existing v2
   schema validation and bounded errors.
4. Implement and test `SystemPromptComposer`; update `runner.py` and
   `context.py` so Agent sends exactly one system message.
5. Migrate the Agent UI to the single mode/preset state, keeping Standard as
   the default and Full Access confirmation inline.
6. Remove the public `ChatWebSocketEndpoint`, Chat request/turn/coordinator/
   presenter/frame DTOs and the WinUI `ChatWebSocketClient` call path.
7. Remove only the Worker v1 Chat generation branch and Chat-specific protocol
   messages. Retain v1 transport pieces still required by non-chat voice, STT,
   VTS, observation, or status endpoints until their own accepted replacement
   exists.
8. Remove Chat-only tests and fixtures; add Agent-only contract and voice
   integration coverage.
9. Update the public route JSON/matrix, architecture and runtime behavior
   docs, ADR 0007, CLI/README/help, and generated/build artifacts. The docs
   update is part of the same rollout, not a later cleanup task.

The migration is ordered so that the UI never loses a working conversation
seam while transport removal is in progress. No compatibility branch is kept
after the route contract is switched.

## Failure, cancellation, and reconnect rules

- A duplicate create request with the same idempotency key returns the existing
  run result; it cannot create a second history entry or second voice stream.
- A stale assistant delta is ignored when its run, assistant message, or
  sequence is not active. Sequence gaps are recorded as a bounded reconnect
  condition, not silently filled with guessed text.
- If SSE disconnects before terminal, the client reconnects with the durable
  cursor. Transient deltas may be absent; the client waits for a terminal
  summary or reconciles persisted history before closing the visible message.
  Voice playback never replays already acknowledged sequences.
- If the worker rejects prompt schema, context bounds, or prompt receipt, the
  Host records a fixed `worker.prompt_contract_invalid` category, closes the
  step, and does not retry with a less-safe prompt.
- If a Broker lease close fails during session/preset replacement, no new lease
  or model step starts. The existing fail-closed Agent session rule remains in
  force.
- Cancel is idempotent across UI, SSE, voice playback, and microphone capture.
  A late worker or STT response is discarded by run/turn identity.
- A failed TTS/VTS path reports voice-unavailable while preserving the durable
  Agent text result. It never changes the saved voice engine/profile silently.

## Verification and acceptance matrix

### Contract and protocol

- Route checker and ASP.NET endpoint test prove 54 HTTP + 1 WebSocket, with no
  `/chat` endpoint or caller.
- Worker v2 schema tests prove `SystemPromptContext` bounds, canonicalization,
  required receipt fields, stable error mapping, and cancellation.
- Prompt composer tests prove exact layer order, empty-block behavior, line/
  Unicode normalization, per-file and total instruction limits, capability
  allowlisting, untrusted-reference labels, and deterministic SHA-256.
- Run metadata tests prove prompt version/digest binding without raw prompt,
  transcript, tool argument, VTS metadata, or secret leakage.

### Agent and voice integration

- Text input creates one Agent run, receives typed assistant deltas, renders
  visible text, and reconciles durable history once.
- Voice input proves STT -> Agent run -> SSE delta -> text-only TTS/VTS -> one
  terminal close, including no-speech, approval, trust, cancel, reconnect,
  stale-delta, duplicate-delta, and missed-transient fallback cases.
- Memory, search, capability, and preset tests prove advertised behavior is
  actually reachable and denied behavior remains denied.
- Existing Agent policy, approval first-writer-wins, path containment, and
  session-rotation tests remain green.

### Build and manual acceptance

- Run the repository's sequential Python, .NET, WinUI, route, docs, and
  Release-build gates using the configured `.venv\Scripts\python.exe` and
  `dotnet` commands; do not run heavyweight suites in parallel.
- Manually verify the published WinUI app starts on a clean process, Agent is
  the only mode, typed and microphone turns work, approval UI pauses safely,
  playback/subtitles do not duplicate, and the scrollbar/UI shell remain
  stable.
- Manually verify real microphone, speaker, and VTube Studio behavior. These
  checks are not replaced by deterministic Harness fixtures.

## Documentation and rollout

The implementation rollout must update, at minimum:

- `docs/architecture.md`, `docs/operations/runtime-behavior.md`, and
  `docs/aspnetcore-route-matrix.md`;
- `docs/adr/0007-mode-capability-profiles.md` to record Agent-only mode and
  preserve the Standard/Full Access policy distinction;
- `backend-dotnet/public-route-contract.json`, backend README/help, and the
  CLI/WinUI caller documentation;
- prompt/protocol contracts and verification notes for the new composer
  version and receipt fields.

Before implementation starts, this spec is self-reviewed for missing
decisions and contradictions. After implementation, the plan must point to
focused tests and a release checklist. A failed manual device check blocks a
claim of voice acceptance but does not justify weakening deterministic
contracts.

Rollback is a versioned build/release rollback to the last known-good Agent
release. Reintroducing `/chat` would be a new, separately reviewed migration,
not an emergency compatibility shortcut.

## Approval boundary

Approval of this document authorizes writing the detailed implementation plan
and the focused tests. It does not authorize deleting the current Chat code,
changing the public route contract, or staging unrelated dirty files until the
implementation plan is reviewed and execution is explicitly selected.
