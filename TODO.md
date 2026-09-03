# Ru Roadmap

Last reviewed: 2026-09-02.

## Completed hardening (2026-09-02)

- [x] Closed workspace watcher stale-dedup and bounded snapshot edge cases;
  each run now resets the workspace baseline, baseline updates reset observation
  state per workspace/path, and debounce callbacks re-read the latest baseline
  before emitting. Escaping/reparse watcher events are ignored safely instead
  of escaping from event callbacks; watcher buffer errors trigger a bounded
  baseline rescan.
- [x] Made activity-log append/clear failure semantics retry-safe and changed
  read, trim, and migration paths to bounded streaming processing; malformed
  migration rows no longer abort the whole migration.
- [x] Bounded activity-preview sanitization input and output lengths (including
  tiny caller limits) before privacy regex processing, while preserving the
  capped original-length field.
- [x] Added fail-closed size caps for legacy memory/history files and broker
  session descriptors, harness/runtime descriptors, and policy files, and made
  malformed activity-outbox rows independently skippable.
- [x] Added bounded SQLite BLOB reads for workspace roots, policy records, and
  model-context payloads; restore now computes current-file hashes by streaming
  instead of allocating the whole workspace file.
- [x] Added bounded reads for session/settings JSON and the VTube Studio token
  file so oversized local state cannot trigger unbounded parsing allocations.
- [x] Closed the metadata-check race for legacy memory/history, session, and
  settings readers, and capped the history migration manifest.
- [x] Bounded STT request-body buffering for both content-length and chunked
  uploads before forwarding audio to the runtime worker.
- [x] Applied the configured request-body cap inside runtime/VTS relays too,
  covering unexpected bodies on GET routes before worker dispatch.
- [x] Hardened policy and workspace-root key handling so missing/invalid key
  material returns a fail-closed store error instead of a null dereference.
- [x] Added bounded, structurally validated Windows Credential Manager blobs
  and rejected oversized secrets before native writes.
- [x] Made declarative skill package reads streaming and bounded after the
  initial file-size check, preventing a concurrent file growth from bypassing
  package limits.
- [x] Bounded GUI attachment and theme-preference reads; clipboard image
  preview/upload streams are independent, and oversized growing text files are
  rejected instead of silently queued with missing content.
- [x] Reclaimed one-shot workspace watcher debounce timers after callbacks so
  long-lived registrations do not retain every changed path.
- [x] Fixed `files.read` truncated-window arithmetic to avoid `Int32` overflow
  for offsets near the 2-GiB boundary.
- [x] Added reparse-point boundary checks around workspace-change restore
  hashing and replacement to fail closed on junction/symlink redirection.
- [x] Bounded durable run-Git attributed paths on write and rehydration,
  including count, path length, and serialized-column limits.
- [x] Disposed client/CLI JSON documents at their ownership boundary and
  removed a Windows diagnostic-test file-sharing race.

## Active Architecture: Cloud-First & External OpenAI-Compatible Providers

- [x] Switched runtime defaults to Cloud (OpenCode Zen / Kilo Gateway).
- [x] Kept OpenAI-compatible adapters for explicitly configured external services and Custom OpenAI endpoints.
- [x] Removed the in-process conversational model, GGUF/HF assets, training tooling, evaluator and V5 suite; preserved active OmniVoice and structured memory.
- [x] Synced settings, dynamic model listing (`GET /models`), and WinUI 3 SettingsView across the managed C# Worker, ASP.NET Core backend, and Desktop Client; no production Python Worker remains.

## Completed maintenance (2026-08-31)

- [x] Removed cloud generation tuning controls (`Temperature`, `Top-K`, and
  `Max New Tokens`) from the WinUI surface, persistence/API snapshots, and
  Worker v2 settings contract; legacy values are ignored and stripped.
- [x] Enabled the managed read-only `web.search` and `web.fetch` tools with the
  fixed DuckDuckGo endpoint and removed the obsolete online-search setting.
- [x] Fixed session deletion handling: the GUI now requires the backend
  application envelope `status: "ok"` before refreshing the sidebar or creating
  a replacement session; added regression coverage for HTTP 200 error envelopes.
- [x] Refreshed current documentation, labeled completed/future historical
  records, and removed the superseded three-mode capability plan/spec.

## Completed test-suite maintenance (2026-08-31)

- [x] Removed self-contained file-tool simulations, no-op orchestration checks,
  duplicate retirement/build guards, exact documentation wording checks, and
  cosmetic XAML snapshots; retained actual managed behavior regressions.
- [x] Unified CI/local .NET testing through recursive project discovery with
  fail-closed discovery and execution. All 13 current test projects are covered,
  including Protocol and Runtime.Core/Adapters/Worker.
- [x] Retired unused Python VTS/overlay source together with nine test files
  (70 cases) and its obsolete dependencies. The default Python suite no longer
  contains the test that connected to VTube Studio.
- [x] Kept OmniVoice assets/provider, active memory/speech tooling, managed
  VTS/token storage, privacy, session deletion, and playback/subtitle tests.
- [x] Replaced misleading inventory reachability claims with bounded path
  classification; added behavioral build/launcher/CI runner checks and scoped
  XAML resource/accessibility integrity checks.

See [verification](docs/operations/verification.md) for measured results and
[the completed plan](docs/superpowers/plans/2026-08-31-test-suite-cleanup.md).

## Completed optimization (2026-08-31 codex/e6fa40c)

- [x] Fixed diagnostic writer lifecycle: `StructuredDiagnosticLogWriter` now owns `IDiagnosticLogSink`, `StopAsync(Drained/Cancelled/Incomplete)` and `Completion`, proving quiescence before disposing gates (covers `TODO` diagnostic shutdown).
- [x] Fixed native overlay Release checks: `assert` → `CHECK`, `test_check.h`, `--self-test-failure` with `WILL_FAIL`, `RU_BUILD_NATIVE_PROBES` opt-in, fused `PrepareRgbaForOverlay` (1 loop) for `SpoutOverlay`.
- [x] Optimized outbox: `HarnessOutboxStore.ReadNextWorkAsync` with `OutboxWork` Idle/Transition/Delivery, read-only idle path (no `BEGIN IMMEDIATE` when idle), prioritized re-read under transaction.
- [x] Optimized file search: single `ReadSearchText` stream per file (probe 8000 + bounded 1 MiB) for `FileSearchAdapter` and `HarnessToolGateway`, preserving binary/invalid_utf8/file_too_large semantics.
- [x] Added GUI single-flight `RefreshController<T>` and `AgentReplyAccumulator` coalesced render (ApiService CancellationToken, `DiagnosticsView`/`MainWindow`/`ChatView` wiring).
- [x] Removed unused `ddgs` from `runtime`; aligned voice Torch/torchaudio to `>=2.8,<2.9`, accepting setup's `2.8.0+cu128`. Tooling `pip check` is not fresh voice-install proof.

## Optimization handoff and acceptance follow-up (2026-08-31)

The handoff entry point is
[the optimization plan](docs/superpowers/plans/2026-08-31-codebase-optimization.md),
which links six scoped plans and their verification gates. Concurrent work in
`e6fa40c` already implements multiple slices, as recorded above; begin with the
plan's resume gate and verify gaps instead of duplicating those changes.

- [x] Add deterministic behavior coverage for logger/refresh lifetime, production reply scheduling, native status suppression, outbox and bounded file search.
- [ ] Collect the optimization plan's missing before/after work counters,
  allocation measurements and median/p95 benchmarks. Unit tests do not close
  performance acceptance; native/live acceptance remains separate.
- [x] Preserve the recorded Broker/Worker extraction rejection; only revisit
  with new evidence and approval, not as automatic unfinished work.
- [x] Resolve the remaining Torch declaration/setup compatibility discrepancy
  in `docs/operations/python-dependency-ownership.md` without upgrading the active environment.
- [x] Reconcile current documentation and record final sequential gates;
  leave unmeasured/live acceptance explicitly open.

Keep live devices/cloud acceptance, destructive cleanup approval, and Git
commit/push separate. The completed records above/below are retained; these
follow-up boxes track acceptance reconciliation, not a reversal of that work.

## Pending product work

- [x] Implement a real end-to-end multimodal image attachment path (Agent
  request validation, run persistence, provider projection, and backend size
  negotiation): Harness payload kind `UserImage` with AuthenticatedClient | ModelContext;
  `ModelRequest` multimodal extension with `IReadOnlyList<ModelImage>`; backend validation
  (4 images, 4MB per image, 20MB total, strict Base64 data URL, vision model check) and
  persistence in SQLite `agent_payloads`; provider projection for OpenAI Responses (`input_image`),
  Anthropic Messages (`image`), and OpenAI-Compatible (`image_url`) with fail-closed non-vision
  rejection (`vision_unsupported`); Worker v2 integration feeding `UserImage` payloads into
  `ModelRequest`; WinUI 3 client image resizer (max 2048px / JPEG 0.85 quality) with 4-image
  guard and attachment preview/bubble zoom dialog. See
  [the design](docs/superpowers/specs/2026-09-03-image-multimodal-design.md),
  [the plan](docs/superpowers/plans/2026-09-03-image-multimodal.md), and
  [verification](docs/verification/2026-09-03-image-multimodal-verification.md).
- [x] Make oversized Agent payload responses fail explicitly instead of
  silently truncating client-visible JSON/base64 content.
- [x] Make `files.read` account for concurrent file growth and avoid reporting
  a complete snapshot from stale `FileInfo.Length` metadata.
- [x] Repeat the STT body cap inside the private speech runtime handler so a
  direct worker invocation cannot force an unbounded JSON parse.
- [x] Apply the same direct-invocation body boundary to VTube Studio and
  observation-control handlers before parsing their JSON payloads.
- [x] Bound Google speech transcripts before normalization so an oversized
  provider response cannot create an unbounded split/allocation.
- [x] Bound session message content and persisted titles before history
  projection/UI display; oversized legacy fields now fail closed or use the
  default title.
- [x] Preflight aggregate session content before JSON serialization so an
  oversized append cannot allocate a document that the 16 MiB file cap rejects.
- [x] Enforce the settings JSON file cap on writes as well as reads, including
  unknown future properties preserved through the settings round-trip.
- [x] Route OpenAI-compatible SSE responses through the bounded line/event
  reader so a single giant provider line cannot cause an unbounded allocation.
- [x] Serialize OmniVoice process-handle state with a dedicated lock so status
  checks cannot race disposal and late starts cannot resurrect a disposed host.
- [x] Drain OmniVoice child stdout/stderr in fixed-size chunks so one giant log
  line cannot create an unbounded throwaway allocation.
- [x] Bound all Agent REST client response bodies before JSON parsing, including
  chunked responses with no trustworthy Content-Length header.
- [x] Reuse the same bounded response reader for session creation and SSE error
  responses so all Agent client adapters enforce the same 8 MiB limit.
- [x] Replace Agent SSE's unbounded line reader with a bounded byte reader so
  one giant event-stream line cannot force a large allocation, while preserving
  BOM and CR/LF compatibility.
- [x] Enforce the SessionApiClient's documented ASCII session-ID alphabet;
  Unicode letters are rejected before any URL/lookup use.
- [x] Isolate provider catalog refresh single-flight gates per provider scope;
  global cache state is no longer held across HTTP, and canceled refreshes
  restore retry state before propagating cancellation. Different provider
  endpoints can refresh concurrently without cross-scope blocking.
- [x] Bound diagnostic and activity JSONL line reads so oversized legacy or
  hostile records are discarded without `ReadLineAsync` allocating the full
  line; valid records after an oversized line remain readable.
- [x] Bound Gemini Vision image requests and response bodies before decoding or
  JSON parsing, including responses without a usable `Content-Length`.

- [x] Implement the grouped provider model picker per [the design](docs/superpowers/specs/2026-09-01-grouped-provider-model-picker-design.md) and [the plan](docs/superpowers/plans/2026-09-01-grouped-provider-model-picker.md): Chat shows all usable provider catalogs in stable groups with global provider/model search; one selection saves a strict two-field payload (`backend_choice` + `selected_model`) through the existing atomic `POST /settings` transaction (the Host expands `selected_model` onto the mapped provider model field); `GroupedProviderCatalogController` composes seven per-provider controllers with isolated failures, stale same-identity labeling, coalesced refreshes and late-result rejection; `ProviderModelPicker` renders grouped rows with deterministic automation names. The Host serves `GET /models` for every provider whose credential is configured, active or not, so the dropdown shows exactly the providers the user has keys for; unknown aliases fail closed as `provider_not_active` and no provider/model/key/protocol fallback exists. Deterministic backend/GUI gates (backend 706, GUI 354, Python 409) passed against the then-current 56+1 route contract; the WinUI Release build and a non-secret live-API spot check passed; live GUI acceptance remains separate.
- [x] Implement provider model-catalog auto-refresh (on demand plus five-minute active-Chat polling) and model-aware reasoning-effort selection per [the combined design](docs/superpowers/specs/2026-09-01-provider-catalog-refresh-design.md) and [the provider catalog plan](docs/superpowers/plans/2026-09-01-provider-catalog-refresh.md). Host owns discovery/cache (`ProviderCatalogService`, `ProviderCatalogParser`) with 5-min cache, 30-s throttle, single-flight, credential-revision identity, and bounded HTTP semantics; `GET /models` returns the typed additive snapshot (`catalog`, `stale`, `error_code`, `last_success_utc`) without credentials. GUI polls only while Chat is active and throttles manual refresh; Worker transport pins model/endpoint/key/reasoning per run through a protected DPAPI handoff. Deterministic gates and the visible Kilo catalog startup smoke passed; commit/publish, live generation and manual control acceptance remain separate; see [verification](docs/operations/verification.md).
- [x] Implement native OpenAI Responses and Anthropic Messages providers per [the native provider design](docs/superpowers/specs/2026-09-01-native-openai-anthropic-providers-design.md) and [plan](docs/superpowers/plans/2026-09-01-native-openai-anthropic-providers.md): fixed official endpoints (`https://api.openai.com/v1` / `https://api.anthropic.com/v1`), isolated Credential Manager accounts (`openai_api_key`/`anthropic_api_key`), source-dated `2026-09-01` capability registry, `ProviderCatalogRequestFactory` with `Authorization: Bearer` / `x-api-key`+`anthropic-version: 2023-06-01`, `store:false` and internal 8192 `max_tokens`, protected per-step `Protocol`+`MaximumOutputTokens` pinning, `BoundedSseReader` (raw-byte bounded, strict UTF-8, BOM-aware), `OpenAiResponsesModelProvider`/`AnthropicMessagesModelProvider` with strict protocol-ordered parsing and exact dispatch via `IModelProviderResolver`; malformed persisted providers remain unselected instead of falling back to OpenCode, unsupported native turn roles fail before HTTP, contradictory event/type pairs, malformed nested JSON containers, and duplicate OpenAI terminals fail closed; the internal `per-step` bootstrap starts without selecting a fallback provider; `ChatView` allows `openai_responses`/`anthropic_messages`; `.gitignore` `models/`→`/models/` so `RuAgent.Runtime.Adapters/Models` is tracked. Focused coverage is `BoundedSseReaderTests` 16, `OpenAiResponsesModelProviderTests` 24, `AnthropicMessagesModelProviderTests` 31 and `OfficialProviderTests` 5. Deterministic gates passed against the then-current 56+1 route contract (`RuBackend.Tests` 683, `gui-winui3.Tests` 330, `RuAgent.Runtime.Worker.Tests` 55, `RuAgent.Runtime.Adapters.Tests` 169, Python 409); local managed-host startup returned HTTP 200, and no live provider key/catalog/generation/push was performed.
- [x] Investigate diagnostic-writer shutdown under concurrent host teardown:
  `StructuredDiagnosticLogWriter.Dispose` waits via `StopAsync(2s)` and only disposes gates after `Completion` proves writer quiescence. Ten writer shutdown cases and one actual Host shutdown case cover the lifecycle. Business endpoint fixtures still use a discarding emitter for isolation.
- [x] Remove all credential alias/cross-provider fallback from generation;
  startup reads no provider key, while each run reads only the selected
  provider's canonical account without migrating keys.
- [x] Detach GUI/CLI clients without killing the shared Public Host; cancel
  startup before teardown so late responses cannot create controllers/timers.
- [x] Keep policy/preset failures bounded and privacy-safe; propagate request
  cancellation rather than converting it into an internal-error response.
- [x] Align Release clean/XAML-copy paths with the x64 GUI publish and stop
  the build on missing/copy-failed compiled XAML; cover the release-script
  regression with controlled artifacts.
- [ ] Collect user-listened hosted audio evidence and numeric live-cloud p50/p95
  before calling the first-audio rollout accepted. Keep the provider/model A/B
  comparison pending until that evidence exists; do not change providers or
  spend credits as part of this task.
- [ ] Run a manual voice (mic → STT, TTS playback) and VTS smoke test with
  VTube Studio open.
- [ ] Keep screen observation on the hosted Gemini boundary. Do not add a
  local vision model until a new product decision explicitly reopens that scope.
- [ ] Keep the structured `/observation/*` cloud-reaction boundary available so
  a future model can be connected without changing the reaction coordinator.
- [x] Establish one Protocol-owned catalog for Ru's 18 tool definitions and
  derive the runtime prompt/Worker projection from it. The exact selection
  envelope, strict schemas, examples, scopes, and schema digests now prevent
  model-contract drift; Broker manifest validation remains in place while the
  later Broker-authority migration stays open.
- [x] Implement bundled internal documentation & docs lookup tool (`help.docs`; `app.docs` is not a second model tool) in `RuAgentBroker` and System Prompt Composer. The offline, read-only search is backed by five explicitly embedded current documents and works from any workspace. See `docs/superpowers/specs/2026-09-02-bundled-help-docs-design.md` and `docs/superpowers/plans/2026-09-02-bundled-help-docs.md`.
- [x] Harden Agent Broker file and terminal tools against edge-case failures: `files.edit` now preserves CRLF while accepting LF markers; `files.search` uses clamped concurrent reads, cancellation, and ordinal result ordering; terminal child stdin is redirected and closed immediately; delete/restore recovery caps are configurable but bounded by the 4 MiB hard maximum. See `docs/superpowers/specs/2026-09-02-agent-tool-hardening-design.md` and `docs/superpowers/plans/2026-09-02-agent-tool-hardening.md`.

## Deferred capabilities

- [ ] Design a Windows OS-enforced filesystem/network sandbox for Agent
  execution as a separate architecture project. It must restrict the complete
  child process tree, deny filesystem access outside explicitly granted roots,
  deny network at the OS boundary unless granted, and include adversarial
  junction/symlink and real-socket acceptance. Until then, do not describe
  executable allowlists, working directories, or request metadata as sandboxing.
- [ ] Keep microphone STT on the current Google boundary; any offline STT
  provider requires a separate product decision and resource review.
- [ ] Design WASAPI system-audio understanding only after the hosted observation
  path has clear quality, latency, resource, and process-lifecycle evidence.
- [ ] Implement Discord Bot feature: Bring AI into Voice Channels. (STT:
  CTranslate2 Whisper. TTS: OmniVoice.)
