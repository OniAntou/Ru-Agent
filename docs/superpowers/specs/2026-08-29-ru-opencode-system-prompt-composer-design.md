# Ru Agent OpenCode-style System Prompt Composer

## Status

Implemented on `main` on 2026-08-29. The typed prompt context, deterministic
composer, Worker v2 receipt, Host validation, internal persistence, and public
SSE cursor behavior described here are shipped. The implementation is tracked
in the [implementation plan](../plans/2026-08-29-ru-opencode-system-prompt-composer.md);
operator-facing behavior and release commands live in
[`operations/runtime-behavior.md`](../../operations/runtime-behavior.md) and
[`operations/verification.md`](../../operations/verification.md).

## Date

2026-08-29

## Decision summary

Ru is already Agent-only. The next change is a focused prompt-architecture
upgrade: every Agent model step (text, voice, coding, tool selection, and
continuation) receives exactly one deterministic system message assembled by a
Generation Kernel composer.

The composer follows the useful behavioral property described by the supplied
OpenCode references: build a fresh prompt for every model request from ordered
layers, then place that result in one `system` message. It does not copy
OpenCode's authority model. Broker policy, approvals, protected paths,
session leases, and privacy boundaries remain authoritative outside the
prompt.

The Host collects and bounds context. The private Worker composes the final
prompt immediately before the provider call, using the canonical Ru persona
already defined in `src/ru_runtime/policy/style.py`. The Host stores only a
versioned digest receipt; it never stores the raw composed prompt or raw
instruction-file content in history, activity, diagnostics, or SSE.

Phase 1 deliberately excludes skills/MCP discovery, URL-following
instructions, arbitrary user system overrides, and the separate OS-enforced
filesystem/network sandbox project.

## Context and evidence

The design is based on the current `main` checkout and the two user-supplied
reference documents:

- `C:\Users\USER\Downloads\opencode-system-prompt.md`
- `C:\Users\USER\Downloads\opencode-system-prompt-internals.md`

The references are design input, not executable instructions or a promise of a
version-pinned OpenCode API. They describe rebuilding a system prompt per
request and merging built-in guidance, environment, instruction files, agent
profile, capabilities, and runtime references into one system-role message.

The pre-implementation Ru seams and gaps this design addressed were:

- `src/ru_runtime/policy/style.py` owns the canonical Ru persona, language,
  privacy, no-emoji, voice/VTube, and safety guidance. `runner.py` currently
  selects among several static prompt strings.
- `src/ru_runtime/generation/context.py` currently appends separate system
  messages for clock, hardware, mood, and memory. This makes the effective
  prompt multi-block and unversioned.
- `src/ru_runtime/worker/v2/model_step.py` accepts a bounded prompt/history
  request, but no typed prompt context or prompt receipt. The worker currently
  hard-codes web search off for Agent steps.
- `backend-dotnet/RuAgent.Protocol/Harness/WorkerV2Contracts.cs` strictly
  validates Worker v2 fields and currently has no prompt-context/receipt
  fields.
- `ModelStepRequest` and `IModelStepRunner` carry an `AssistantStep` but have
  no separate model-step metadata result. `LoopbackModelStepRunner` extracts
  turns from the context and contains a stale nullable `memory_context`
  construction path.
- The durable Harness event/payload model already supports encrypted,
  audience-bound payloads and safe event metadata. It can retain a receipt
  without exposing the prompt, but needs a dedicated internal payload/event
  contract.
- The Agent runtime registry already knows the canonical run root. That root
  is the boundary for Ru instruction discovery; it is not a new permission
  boundary and does not weaken Broker policy.

## Goals

The completed change must:

1. Compose one system message for every Agent model step, including voice and
   tool-selection steps.
2. Rebuild that message for each accepted model step so memory, tool results,
   voice state, environment, or instruction files can produce a new digest.
3. Keep `style.py` as the only canonical source for Ru persona text; do not
   duplicate persona prose in C#.
4. Define a typed, bounded Worker v2 context contract with deterministic
   canonicalization and SHA-256 receipts.
5. Discover only Ru-owned instruction files at the selected run root, with
   root-only traversal and explicit size/encoding/link rules.
6. Keep all instructions and retrieved references as model guidance/data, never
   as a source of filesystem, terminal, network, approval, or preset authority.
7. Persist only safe receipt metadata (composer version, final digest, ordered
   block/source digests) and keep raw prompt/context private.
8. Preserve the existing Agent-only transport, Broker policy, approval,
   workspace trust, cancellation, SSE replay, idempotency, and privacy
   invariants.
9. Keep the implementation split into small provider, contract, composer, and
   persistence seams that can be tested independently.

## Non-goals and explicit boundaries

- This is not the OS-enforced filesystem/network sandbox. Terminal execution
  remains policy-gated under the current Windows user; a later project must
  provide a process-tree, filesystem, and real-socket boundary enforced by the
  OS.
- This does not reintroduce Chat, a second conversation transport, or a hidden
  compatibility mode. Agent-only remains the current public contract.
- This does not add skills or MCP discovery in phase 1. A later capability
  adapter may add registered metadata only after a separate security review.
- This does not fetch arbitrary URLs or read parent directories. Supplied
  OpenCode documents are reference material, not files Ru should load at
  runtime.
- This does not add an arbitrary user-configured system instruction in phase 1.
  A future extension must define its own validation, precedence, and audit
  contract.
- This does not alter Broker policy, permission presets, trust, approvals,
  external-directory grants, tool schemas, voice quality, subtitle ordering,
  VTube behavior, or live device ownership.
- This does not persist the composed prompt, instruction contents, memory
  payloads, search pages, tool arguments, VTS metadata, bearer values, or
  secrets.

## Ownership and data flow

```text
Agent run / canonical run root
        |
        v
ASP.NET Core Host
  - validates run + immutable policy snapshot
  - loads bounded Ru instruction files under the run root
  - collects environment, Broker capabilities, and runtime context
  - sends typed SystemPromptContext with agent.step.start
        |
        v
Worker v2 (private)
  - validates and canonicalizes context
  - SystemPromptComposer builds the final text from style.py blocks
  - generation adapter receives exactly one system message
  - returns assistant output + SystemPromptReceipt
        |
        v
Host
  - validates receipt against the request/contract
  - commits an InternalOnly receipt payload and model.prompt_composed event
  - retains only safe metadata; model output/history remain separate
```

The Host owns source selection and security-sensitive path resolution. The
Worker owns prompt text assembly because it owns `style.py` and the provider
adapter. Neither side may treat prompt text as an execution grant.

The context provider is invoked after the run state machine accepts a model
step and before the Worker request is sent. It is invoked again for every
later step; a reconnect does not recompose an already completed step.

## Typed context contract

`system_prompt_context` is a required field of `agent.step.start`. The
transport remains Worker v2; `schema_version` versions the prompt contract
independently so a future composer can evolve without pretending that an old
worker understands new semantics.

The phase-1 shape is:

```json
{
  "schema_version": 1,
  "agent_profile": "ru-agent",
  "preset": "standard",
  "environment": {
    "model": "provider/model-label",
    "platform": "windows",
    "locale": "vi-VN",
    "timezone": "Asia/Bangkok",
    "date": "2026-08-29",
    "workspace_label": "optional-safe-display-label"
  },
  "instruction_blocks": [
    {
      "source_id": "root:Ru.md",
      "display_name": "Ru.md",
      "content": "bounded instruction text",
      "content_sha256": "64-lowercase-hex"
    }
  ],
  "capabilities": [
    {
      "id": "files.read",
      "version": "1.0.0",
      "summary": "read a bounded file under Broker policy",
      "input_schema_digest": "64-lowercase-hex",
      "policy_summary": "workspace.read",
      "availability": "executable"
    }
  ],
  "runtime_context": {
    "memory": "bounded retrieved memory or null",
    "mood": "bounded mood guidance or null",
    "voice": "bounded voice pacing guidance or null",
    "vts": "bounded expression state or null",
    "hardware": "bounded hardware/time facts or null",
    "references": [
      {
        "kind": "search",
        "label": "untrusted reference",
        "content": "bounded reference data"
      }
    ]
  }
}
```

Contract rules:

- `schema_version` is a positive integer and only supported versions are
  accepted. `agent_profile` and `preset` are allowlisted identifiers. The
  preset is a description of the already-bound policy snapshot, not a request
  to change it.
- Environment fields are bounded display/context values. No bearer token,
  opaque run identifier, raw secret, or unnecessary full filesystem path may
  enter this object. A workspace label is descriptive only; Broker receives
  the actual canonical path through its existing private lease.
- Instruction blocks are ordered by a stable source order. `source_id` and
  `display_name` are safe bounded identifiers; `content` is transient model
  guidance; `content_sha256` must match strict UTF-8 normalized content.
- Capabilities are a snapshot of what the authenticated Broker actually
  advertises for this run. The model sees summaries, schema digests, and
  policy classes, not hidden implementation details or credentials.
- Runtime values are individually bounded and explicitly labeled. Memory,
  voice/VTS state, hardware facts, and search references are data. Embedded
  instructions inside those values must not override the Agent execution
  contract.
- Phase 1 has no `user_system_instruction` field. Unknown fields are rejected
  instead of silently accepted for future use.

The aggregate request remains below the existing Worker frame cap. In
addition, phase-1 prompt context limits are:

- at most 16 instruction blocks and 16 capabilities;
- at most 64 KiB per instruction file and 256 KiB combined instruction text;
- at most 100 KiB retrieved memory, 32 KiB per history response, and the
  existing 16,000-character current prompt cap;
- bounded environment/runtime strings and references whose aggregate size is
  covered by the 5 MiB Worker frame cap and a dedicated 512 KiB context cap;
- all byte caps are measured after strict UTF-8 encoding, not by an unsafe
  implicit platform encoding.

The Host validates these bounds before sending. The Worker validates them
again at the trust boundary. A validation failure is a bounded protocol error,
not a best-effort truncation that could change instruction meaning.

## Instruction discovery and trust

The Host loader accepts only these paths below the canonical run root:

1. `Ru.md` at the root itself;
2. `.ru/AGENTS.md` directly below that root;
3. `.ru/CONTEXT.md` directly below that root.

There is no parent-directory walk, global implicit file, arbitrary model-named
path, URL fetch, or runtime loading of the current Codex `AGENTS.md`. A future
global Ru file must be explicitly designed and documented as a separate source.

For each candidate the loader:

- resolves the root with the existing Windows canonical-path/final-file
  identity services;
- rejects a candidate whose final path escapes the root, including a junction
  or symlink escape;
- reads strict UTF-8 text, rejects invalid encoding/NUL content, and enforces
  the 64 KiB individual and 256 KiB combined caps;
- normalizes line endings and Unicode before hashing while retaining the
  normalized text only in the transient request;
- skips missing, invalid, oversize, or escaped files with a safe bounded
  reason; a missing instruction file is not a model-step failure;
- emits stable `source_id`, `display_name`, and digest metadata without
  exposing the raw path in diagnostics or history.

Instruction text is source-trusted only in the sense that the Host selected the
file. Its content is still untrusted model guidance. It cannot declare a tool
available, authorize a path, suppress an approval, switch the preset, bypass a
protected-path/network rule, or change the session owner.

## Deterministic layer order and composer

For every model step, `SystemPromptComposer` emits these ordered blocks. Empty
optional blocks are omitted without changing the order of the remaining
blocks:

1. **Ru persona and safety** — the canonical bilingual identity, `anh`/`em`
   language behavior, concise style, privacy/no-emoji rules, voice/VTube
   metadata boundary, and fixed safety constraints from `style.py`.
2. **Agent execution contract** — use only advertised capabilities, honor
   Broker policy/approval/trust/cancellation, report uncertainty, never claim
   an unverified side effect, and keep control metadata out of visible text.
3. **Environment** — model/provider label, platform, locale, timezone, date,
   and optional safe workspace label.
4. **Ru instruction files** — the ordered, bounded blocks selected by the Host;
   each block is labeled as guidance, not authority.
5. **Agent profile and preset** — the already-bound Standard or Full Access
   behavior. Full Access never bypasses protected/destructive approvals or
   Broker trust.
6. **Validated capabilities** — the exact current Broker-advertised tools and
   their versions, schema digests, availability, and policy summaries.
7. **Runtime context** — bounded memory, mood, voice/VTS, hardware/time, and
   untrusted retrieved/search references with explicit data labels.

The composer is a pure Generation Kernel function. It does not read files,
call a provider, execute a tool, perform network I/O, select permissions, or
write persistence. It normalizes line endings, Unicode normalization form,
block separators, and trailing whitespace before computing digests. Its output
is a `SystemPromptSnapshot` containing:

```text
composer_version   semantic contract version, phase 1 starts at 1.0.0
prompt_sha256      SHA-256 of canonical final UTF-8 prompt text
block_digests      ordered stable block/source identifiers and SHA-256 values
text               worker-internal canonical prompt text
```

The `text` field never crosses into durable Host metadata. The model adapter
receives one message `("system", snapshot.text)`, followed by ordered
user/assistant/tool messages. `build_conversation_messages` must not append
independent system messages for clock, hardware, mood, or memory after the
composer output.

The existing canonical persona constants may be refactored into named blocks,
but their meaning and privacy guarantees must remain covered by current style
tests. No C# persona copy is allowed.

## Worker and Host contract changes

### C# Core/Host

- Add immutable bounded `SystemPromptContext` and `SystemPromptReceipt`
  records in the protocol/Core seam, with canonical validation shared by
  request/response tests.
- Add a per-coordinator `IRuPromptContextProvider` bound to the run root and
  policy snapshot. `AgentRunCoordinator` calls it for each accepted step after
  assembling model context.
- Extend `ModelStepRequest` with the typed context. Change
  `IModelStepRunner` to return `ModelStepResult` (`AssistantStep` plus
  `SystemPromptReceipt`) so prompt metadata does not contaminate assistant
  output or future model turns.
- Update `LoopbackModelStepRunner` to serialize the typed context, remove the
  stale nullable-memory construction/comments, and extract only user/assistant
  turns into `turns`; system/runtime context goes through the typed prompt
  context.
- Add an internal-only `PromptReceipt` payload policy and a
  `model.prompt_composed` event carrying that payload. The event is committed
  with the corresponding model completion (or tool-step completion) so replay
  can identify the prompt contract without exposing text. The existing
  assistant text/assistant-step payloads remain unchanged.
- Update event mappers, SQLite payload/event validation, safe diagnostics, and
  recovery/replay readers. Receipts are bounded and keyed by run/model-step;
  raw prompt/context content is never copied into a public envelope.

### Worker v2/Python

- Add strict `system_prompt_context` validation to `protocol.py` and
  `model_step.py`; reject unknown fields, unsupported schema versions,
  mismatched content digests, caps, and invalid capability identifiers.
- Add `src/ru_runtime/generation/prompt_context.py` for typed parsing,
  normalization, and digest helpers, and
  `src/ru_runtime/generation/system_prompt_composer.py` for the pure ordered
  composer.
- Update `style.py` only to expose canonical persona/execution blocks without
  duplicating text. Keep screen-reaction prompt behavior separate unless a
  later design explicitly brings it into Agent step composition.
- Pass the typed context through `ModelRunner.generate_reply` and
  `execute_reply`; make search availability capability-derived rather than
  silently true/false by an unrelated legacy default. Search results remain
  untrusted references and cannot grant network execution.
- Return a required `prompt_receipt` in `agent.step.done` with
  `composer_version`, `prompt_sha256`, and ordered `block_digests`. The worker
  must not return the snapshot text.

### Wire versioning and failure

Worker transport `ProtocolVersion` remains `2`; the new prompt context has its
own schema version. `agent.step.start` requires the context and
`agent.step.done` requires the receipt once this design is shipped. An old or
partial worker therefore fails closed instead of receiving a less-safe legacy
prompt.

The phase-1 Worker v2 start payload no longer has a separate top-level
`memory_context` field. Memory and other dynamic values travel only inside
`system_prompt_context.runtime_context`; accepting both paths would permit two
different prompt inputs and make the digest ambiguous. The existing
`allow_web_search` setting is derived by the Host from the authenticated
capability manifest and is accepted only when `web.search` is advertised; it
does not grant a terminal or network capability.

The Host rejects a receipt when any of the following holds:

- required fields are missing, malformed, out of bounds, or contain unknown
  fields;
- the composer version is unsupported or does not match the expected phase;
- the final digest is not lowercase SHA-256 or the ordered block/source digest
  set does not match the request and canonical composer contract;
- a receipt appears for the wrong run/model step or arrives after cancellation.

The stable public failure category is `worker.prompt_contract_invalid`.
There is no fallback to the former static prompt and no retry with a reduced
context. Cancellation remains idempotent; a late response is discarded by the
existing run/model-step identity checks.

### Receipt binding

The Worker returns a receipt in this shape and never returns the composed text:

```json
{
  "composer_version": "1.0.0",
  "prompt_sha256": "64-lowercase-hex",
  "block_digests": [
    {"block_id": "ru.persona", "sha256": "64-lowercase-hex"},
    {"block_id": "instruction.root:Ru.md", "sha256": "64-lowercase-hex"}
  ]
}
```

The Host validates the receipt shape, version, digest format, stable block
order, and every instruction source digest against the exact context sent for
that `run_id`/`model_step_id`. It does not need to reproduce provider output,
but it must reject unexpected block IDs, duplicate IDs, missing required
blocks, or a receipt associated with another step. Receipt validation happens
before the completion/tool transition is committed.

## Persistence, replay, and privacy

After a successful worker step, the Host creates one bounded JSON receipt
payload with `HarnessPayloadAudience.InternalOnly` and a new
`HarnessPayloadKind.PromptReceipt`. A `model.prompt_composed` event references
that payload and the same `model_step_id`. The receipt contains only:

- `composer_version`;
- final `prompt_sha256`;
- ordered block IDs/digests and selected instruction source IDs/digests.

It contains no prompt text, instruction text, memory, search page, tool
arguments, VTS state, raw path, provider body, token, or secret. The
`model.prompt_composed` event is durable for internal replay but is suppressed
from the public SSE event-type allowlist; the SSE cursor still advances over a
suppressed event so reconnects cannot loop on it. CLI/WinUI rendering exposes
no receipt payload in phase 1.

The receipt event is committed transactionally with the model completion so a
replay can associate it with a model step. Reconnects replay the durable event
cursor and never invoke the composer again for a completed step. A continuation
after a valid state transition creates a new model step and therefore a new
context and receipt.

If an internal receipt payload is missing or corrupt during replay, the run is
parked/marked with the existing safe recovery mechanism; the system must not
invent a digest or silently treat the step as having used the old prompt.

## Security and authority invariants

- Prompt composition is not a permission check. Broker policy remains the only
  authority for files, terminal, network intent, approvals, protected paths,
  workspace trust, and external-directory grants.
- The `preset` in context is copied from the immutable run policy snapshot.
  Prompt text cannot mutate it, and Full Access does not remove approval gates.
- Instruction files, memory, search results, tool results, voice/VTS state,
  and capability descriptions are data. Injection text inside any of them is
  handled under the Agent execution contract.
- The Host performs root-only file selection and final-path checks. The Worker
  never follows an instruction-provided path or URL while composing.
- Raw prompt/context content remains transient in the worker/provider call and
  is excluded from activity, diagnostics, SSE, history, and safe exception
  messages.
- Re-reading instructions on every model step is deterministic for that step;
  it does not allow a running prompt to change the already-bound Broker lease.
- The future OS sandbox remains a separate project and must not be simulated
  with request flags, prompt text, or an allowlist in this change.

## Test and verification strategy

### Python Generation Kernel

- Unit tests for typed context bounds, required/unknown fields, strict UTF-8,
  Unicode and line-ending normalization, canonical JSON, and SHA-256.
- Golden composer fixtures for every layer combination, empty optional blocks,
  stable separators, exact order, and one final system string.
- Tests prove clock/hardware/mood/memory/voice/VTS/search no longer become
  separate system messages.
- Tests prove untrusted-reference labels and prompt-injection text cannot
  alter capability selection or permission semantics.
- Worker v2 step tests require a receipt, reject mismatches, preserve
  cancellation, and retain bounded public error mapping.

### C# Core/Host/Persistence

- Provider tests for root-only `Ru.md`/`.ru/*` selection, missing/invalid/
  oversize files, UTF-8, combined caps, final-path escape, symlink/junction
  escape, stable source order, and source digests.
- Protocol tests for start context/done receipt validation, unknown fields,
  frame/context caps, digest formats, and old-worker fail-closed behavior.
- `ModelStepResult`/coordinator tests prove each step asks for fresh context,
  preserves pure `AssistantStep`, binds receipt to run/model-step, and commits
  the internal payload/event transactionally.
- Persistence tests prove the payload audience is `InternalOnly`, event
  mapping/replay is deterministic, public projections contain no raw prompt,
  and corrupt receipts park safely.
- Existing Agent policy, approval first-writer-wins, path containment,
  session-rotation, SSE cursor, tool, voice, and cancellation suites remain
  green.

### Full gates and manual boundary

- Run Python and .NET suites sequentially with the repository's configured
  `.venv\Scripts\python.exe` and `dotnet` commands. Re-run the route/contract
  checker and release build gates.
- Manual Agent text/voice/tool smoke remains user-owned acceptance. It checks
  that the app starts, Agent runs continue, approvals pause safely, and visible
  text/TTS/subtitles never receive prompt metadata.
- Live microphone, speaker, VTube Studio, and any real network behavior are
  not replaced by deterministic prompt tests and must not be auto-controlled.

## Rollout order

1. Add RED contract tests and typed C# protocol records for context/receipt.
2. Add Host instruction/context provider and root-only file tests; keep the
   existing model path unchanged until the request contract is ready.
3. Add Python parser, canonicalization, pure composer, and golden fixtures.
4. Wire Worker v2 request/response and `ModelStepResult`; prove one system
   message and fail-closed receipt validation in both language stacks.
5. Wire coordinator receipt persistence/event mapping and remove the stale
   loopback memory path; run integration tests.
6. Update current architecture/runtime/verification/README/ADR references and
   mark any earlier umbrella prompt spec as superseded by this focused design.
7. Run sequential deterministic gates, then leave manual device/VTS smoke for
   explicit user-owned acceptance.

Each implementation slice should be small enough to review independently. No
skills/MCP, UI redesign, Chat compatibility, or OS sandbox work may be
smuggled into a composer slice.

## Documentation impact

After implementation, update at minimum:

- `docs/architecture.md` ownership and Worker v2 prompt-contract sections;
- `docs/operations/runtime-behavior.md` for one-system-message and receipt
  privacy rules;
- `docs/operations/verification.md` for composer/protocol/persistence gates;
- `docs/README.md` to index this focused design and distinguish it from the
  superseded umbrella prompt document;
- `docs/adr/0007-mode-capability-profiles.md` only where Agent-only/preset
  wording needs the prompt-authority clarification;
- Worker v2 protocol contracts, public safe diagnostics, and release/build
  verification notes.

Before implementation, this design is self-reviewed for missing decisions and
contradictions. The implementation plan must point to focused tests and a
release checklist. A failed manual device check blocks a claim of voice
acceptance but never justifies weakening the deterministic prompt contract.

## Maintenance boundary

This document is the current prompt-composer contract for the shipped Agent
path. Changes to the schema, layer order, instruction roots, receipt fields, or
privacy boundary require an updated implementation plan, focused regression
tests, and the sequential release gates. The future OS-enforced
filesystem/network sandbox remains a separate project and must not be implied
by this prompt contract.
