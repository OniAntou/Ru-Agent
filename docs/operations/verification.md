# Verification

Run heavyweight gates sequentially on Windows. Restore each .NET test project
before using `--no-restore`; concurrent Python, frontend, and .NET builds can
exhaust the page file on this workspace.

## Deterministic release matrix

```powershell
dotnet restore D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj --nologo
& D:\LLM\.venv\Scripts\python.exe -m ruff check D:\LLM\src D:\LLM\tests D:\LLM\scripts D:\LLM\tools
& D:\LLM\.venv\Scripts\python.exe -m mypy D:\LLM\src
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\measure_phase1_runtime.py --root D:\LLM
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output "$env:TEMP\ru-prompt-composer-harness.json"
& D:\LLM\.venv\Scripts\python.exe -m pip check
& D:\LLM\scripts\test_dotnet.ps1 -Configuration Release
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --no-restore --nologo
cmd /c D:\LLM\build.bat
```

CI and local verification share `scripts/test_dotnet.ps1`, which recursively
discovers `*Tests.csproj` under `backend-dotnet` and `gui-winui3.Tests`, excluding
generated `bin`, `obj`, and `out` paths. It runs them sequentially and stops on
missing roots, discovery errors, an unavailable SDK, or the first test failure.
It restores by default; use `-NoRestore` only after restoring the projects.
Protocol and all three Runtime test projects are included.

The test cleanup removed the unused Python VTS/overlay package and its tests,
including the test that could open a real VTube Studio connection. Managed C#
VTS tests and opt-in native acceptance remain separate. Build and launcher
unit tests replace only their external process boundary and never open RuApp.

### Multimodal image attachment evidence (2026-09-03)

- End-to-end multimodal image attachment pipeline implemented across WinUI 3, ASP.NET Core Public Host, Worker v2, and LLM providers (OpenAI Responses, Anthropic Messages, and OpenAI-Compatible).
- `HarnessPayloadKind.UserImage = 11` dual audience (`AuthenticatedClient | ModelContext`) persisted atomically in SQLite `agent_payloads` table.
- `POST /agent/v1/runs` body cap bumped to 1 MB for runs with `image_attachments`. Strict validation enforces max 4 images, max 4 MB decoded per image, max 20 MB total decoded, allowed MIME types (`image/png`, `image/jpeg`, `image/webp`), strict base64 encoding, and model vision capability. Non-vision models fail closed with `vision_unsupported`.
- Worker v2 wire protocol supports `"images"` in start payload; `LoopbackModelStepRunner.LoadRunImagesAsync` feeds decrypted payload data URLs into `ModelRequest.Images`.
- Client-side `ImageAttachmentResizer` downscales images exceeding 2048px on max dimension and compresses them to JPEG (0.85 quality) or PNG before sending. Chat bubbles render thumbnails up to 240px and provide click-to-zoom in a `ContentDialog`.
- All 13 test projects passed (2,438 tests, 0 failed, 0 skipped). Both Debug and Release builds succeeded with 0 warnings and 0 errors. See [2026-09-03-image-multimodal-verification.md](../verification/2026-09-03-image-multimodal-verification.md).

### Cross-system hardening evidence (2026-09-02)

- Workspace external-change detection resets per-workspace baselines between
  runs, resets per-path observation state when a new baseline is registered,
  skips reparse-point traversal, hashes through a bounded streaming pass, and
  keeps watcher debounce state isolated per registration. Debounce callbacks
  re-read the latest baseline before emitting; watcher callbacks also ignore
  paths escaping through reparse boundaries without leaking exceptions, and
  watcher buffer errors trigger a bounded baseline rescan. The stale-dedup,
  lifetime, and race regressions and eight monitor cases pass.
- Activity logs now mark event IDs only after a successful append, preserve
  duplicate state when clear fails, and read/trim/migrate JSONL through bounded
  streaming paths. Malformed migration rows are skipped independently. The
  ActivityLogStore and migration slices pass, including fault-injected retries.
- Activity preview sanitization now bounds the raw text entering regex/privacy
  processing and clamps caller-provided preview lengths (including tiny limits),
  preventing oversized malformed activity payloads from causing avoidable
  allocations.
- Legacy `memory_texts.json`, `history.json`, broker session descriptors, and
  harness/runtime descriptors are rejected before parsing when they exceed
  their bounded input caps; bounded streaming also closes the metadata-check
  race. Oversized fixtures pass without allocating the full payload.
- History migration manifests, session files, settings, and legacy memory
  imports use the same bounded streaming discipline; oversized or concurrently
  growing local state falls back to the existing safe-empty/failed-import path.
- SQLite workspace roots, policy documents, model-context payloads, and journal
  envelopes now validate BLOB lengths before allocation/decryption. Workspace
  restore hashes the current file in a streaming pass instead of loading it
  wholly into memory.
- Local session/settings JSON and the VTube Studio token file use bounded
  streaming reads before parsing; malformed or oversized local state falls back
  to the existing safe-empty behavior. Session messages are capped at 1 MiB
  and persisted titles are bounded to the UI's 50-character contract before
  they can be split or displayed; session writes preflight aggregate content
  size before serializing a document that cannot fit the 16 MiB file cap.
- The STT HTTP endpoint now bounds request-body buffering (including chunked
  requests) before forwarding audio to the worker; oversized requests are
  rejected without a worker call. The private speech runtime handler repeats
  the same 8 MiB cap before JSON parsing so direct worker invocation cannot
  bypass that boundary, and the Google recognizer rejects provider transcripts
  over 64 KiB before whitespace normalization/splitting.
- Runtime and VTS relays now apply the configured request-body cap themselves,
  including GET routes with unexpected bodies; oversized relay requests stop
  before the worker is dispatched. The direct VTS and observation-control
  handlers also cap their JSON bodies before parsing, so tests/compositions
  that invoke a handler without the router keep the same boundary.
- Policy and workspace-root cryptographic paths now treat missing or malformed
  key material as explicit fail-closed errors and never dereference/clear a
  null key buffer.
- Windows Credential Manager reads now validate blob size, pointer, and UTF-16
  alignment before allocation; writes reject unreasonably large secrets before
  entering the native API.
- Declarative skill package loading now reads manifest/instructions through a
  bounded streaming path, so a file that grows after the initial metadata check
  cannot bypass the package size limits.
- GUI file attachments now re-check and stream image/text payloads through
  bounded readers; clipboard image preview and upload use independent streams,
  and a file that grows beyond the text cap is rejected instead of being added
  with empty content. Unsupported binary files are rejected instead of being
  queued with no transmission path. Because Agent v1 still ignores its legacy
  image field, image sends now fail explicitly in the GUI rather than silently
  disappearing; Agent v1 rejects the legacy field for external callers too, and
  the local UI-theme preference reader is capped as well.
- Watcher debounce timers remove themselves after firing, preventing a long-
  lived registration from retaining one timer per changed path indefinitely.
- `files.read` now performs offset/window arithmetic in `Int64`, avoiding a
  2-GiB boundary overflow that could incorrectly clear the `truncated` flag;
  it also rechecks the open handle's final length so concurrent appends are
  reported as truncated instead of being mistaken for a complete read.
- Workspace-change restore checks the target and every existing parent for
  reparse points both before hashing and immediately before replacement, so a
  junction/symlink cannot silently redirect the restore path outside the
  trusted workspace boundary.
- Durable run Git state now caps attributed path count, path length, and JSON
  column size on both write and rehydration; malformed persisted path sets fail
  closed instead of being deserialized without bounds.
- Agent payload reads now reject oversized client-visible payloads explicitly;
  they no longer silently truncate JSON or base64 content into a corrupt value.
- OpenAI-compatible streaming responses now use the bounded SSE reader, so a
  provider cannot force an unbounded `ReadLineAsync` allocation with one giant
  line; line, event, and total response caps are enforced before JSON parsing.
- OmniVoice process state now has a dedicated synchronization boundary around
  handle replacement/disposal; `IsRunning` cannot observe a disposed handle,
  and `StartAsync` rechecks disposal after acquiring its lifecycle gate.
- OmniVoice's private process-log drain now consumes bounded char chunks rather
  than allocating an entire stdout/stderr line before discarding it.
- Agent REST, session and SSE client adapters now share an 8 MiB streaming
  response cap before JSON/error parsing, including chunked responses, so
  malformed/hostile local HTTP peers cannot force an unbounded allocation.
- The Agent SSE client now reads lines from bounded byte chunks instead of
  `ReadLineAsync`, preserving strict UTF-8 and CR/LF handling while rejecting
  oversized lines before allocation; the first-line UTF-8 BOM behavior remains
  compatible with the previous `StreamReader` implementation.
- Session ID validation now enforces the documented ASCII
  `a-zA-Z0-9_-` alphabet instead of accepting arbitrary Unicode letters.
- Provider catalog refreshes now use a per-provider-scope single-flight gate;
  a slow or unavailable endpoint cannot hold the global cache lock and block
  unrelated provider scopes. Canceled refreshes restore the prior attempt
  timestamp before propagating cancellation, so the next request is not
  falsely throttled.
- Diagnostic and activity JSONL readers now consume fixed-size text chunks and
  discard overlong records before materializing strings, while continuing to
  later valid records in the same file.
- Gemini Vision now caps screenshot request bytes and response bodies before
  decoding/parsing, including chunked responses without a trustworthy length.
- Activity outbox ingestion also skips malformed scalar rows and continues with
  later events, instead of leaving a processing file stuck for every retry.
- CLI/client JSON response parsing disposes `JsonDocument` instances promptly;
  the full .NET matrix remains clean after the ownership changes.
- The diagnostic endpoint fixture writes to a prior-day file so its seed data
  cannot race the live writer's current-day append handle on Windows.
- Sequential `scripts/test_dotnet.ps1 -NoRestore`: `2,392 passed` across all
  13 test projects; Python suite: `413 passed`; route contract: `62 HTTP + 1
  WebSocket`; documentation/route checks: `8 passed`; `git diff --check` clean.

### Test-suite cleanup evidence (2026-08-31)

- Python: `402 passed` (audit baseline: `510`); unused VTS/overlay tests,
  synthetic file-tool simulations, and redundant guards were removed or
  consolidated, with new build/launcher/runner behavior checks added.
- .NET Release: `2,009 passed` across all 13 projects, including backend
  `633` and GUI `252`. The GUI reduction from `339` removes cosmetic snapshots,
  retaining three focused resource/accessibility contracts and behavior tests.
- Ruff, Mypy (`39` source files), `pip check`, and the
  `56 HTTP + 1 WebSocket` public route contract all passed.
- The first .NET run encountered a business-fixture JSONL cleanup lock. Only
  those business tests now inject a discarding diagnostic emitter; the `89`
  focused business/diagnostics tests and subsequent full matrix passed.
  The real writer's bounded shutdown wait still needs investigation, tracked
  in [TODO](../../TODO.md). This cleanup does not claim that lifecycle bug fixed.
- Cleanup verification did not publish a fresh GUI, automate the interactive
  GUI, perform real voice/VTS/cloud acceptance, or uninstall packages.
  Commit/push is a separately authorized Git handoff, not runtime acceptance.

### Codebase optimization evidence (2026-08-31)

- **Phase 1 (L1 Diagnostics & N1 Native Checks)**:
  - `StructuredDiagnosticLogWriter` lifecycle: `IDiagnosticLogSink`, `StopAsync(Drained/Cancelled/Incomplete)` and `Completion` ensure quiescence before gate cleanup. Tested with `DiagnosticWriterShutdownTests` (7 cases covering normal, honor, ignore, flush, multi-stop, noop, failure bounds).
  - Native overlay Release tests: replaced assert with `CHECK` in `test_check.h`, registered `--self-test-failure` with `WILL_FAIL` in CMake, Spout probe made opt-in (`RU_BUILD_NATIVE_PROBES=OFF`). Pure tests pass (2/2) in Release.
- **Phase 2 (N2 Pixel Transform, P1 GUI Refresh, C1 Render Coalescing, O1 Outbox, F1 File Search)**:
  - Native fused pixel path: `PrepareRgbaForOverlay` fuses green matte, RGBA->BGRA, and premultiplication into a single loop.
  - GUI single-flight coordinator: `RefreshController<T>` prevents overlapping network fetches, cancels stale requests on deactivation, and drops old-generation results. 8 tests passing in `RefreshControllerTests`.
  - ApiService cancellation: added `CancellationToken` to `GetSessionsAsync`, `GetDiagnosticStatusAsync`, `GetDiagnosticLogsAsync`, tested in `ApiRefreshCancellationTests`.
  - ChatView render coalescing: `AgentReplyAccumulator` owns unicode deduplication and string accumulation, tested with 10k synthetic deltas in `AgentReplyAccumulatorTests` (5 tests passing) and scheduling in `AgentReplyRenderSchedulingTests`.
  - Outbox optimization: `HarnessOutboxStore.ReadNextWorkAsync` with `OutboxWork` union (Idle/Transition/Delivery) avoids `BEGIN IMMEDIATE` on empty queues and prioritizes transitions before deliveries. Tested in `HarnessOutboxTests` (79 tests passing).
  - Bounded file search: `ReadSearchText` probes binary prefix (8000 bytes) and reads bounded UTF-8 stream in a single open for `FileSearchAdapter` and `HarnessToolGateway`. Tested in `FileDiscoveryAdapterTests` and `HarnessFileSearchTests` (6 tests passing).
- **Phase 3 (B1/W1 Architecture Candidates)**:
  - Preserved rejection of Broker and Worker extractions due to distinct framing, concurrency, and security invariants; all Broker and Worker tests pass without degradation.
- **Phase 4 (D1 Dependencies & D2 Documentation)**:
  - Removed unused `ddgs` from `pyproject.toml`.
  - Aligned the `voice` extra with the evidence-backed isolated provider line (`torch>=2.8,<2.9`, `torchaudio>=2.8,<2.9`, `omnivoice>=0.2.1`, `flask>=3.1,<4`; accepts the installed `2.8.0+cu128` CUDA build including local tags) and recorded consumer inventory, version authority, and verification policy in `docs/operations/python-dependency-ownership.md`.
  - Setup script preflight verified via `setup_omnivoice_env.ps1 -WhatIf`; `.venv` `pip check` passes but is not fresh-install proof. Three metadata cases (`tests/test_python_metadata.py`) plus two documentation cases formed the five-case focused run; metadata checks do not import Torch.
- **Review remediation wave (six findings, 2026-08-31)**:
  - Logger/refresh lifetimes: `DiagnosticWriterShutdownTests` 10 cases + `DiagnosticWriterHostShutdownTests` 1 + `RefreshControllerTests` 15 all green; shutdown/disposal quiescence verified by coordinator race harness (7 cases) and independent review.
  - Sidebar health single-flight via the production `RefreshController` seam + `HealthStatusService`; production `AgentReplyRenderScheduler` used by ChatView and tested directly (`AgentReplyRenderSchedulingTests`).
  - Native unchanged-status suppression via `status_presentation.h` with failure-safe commit; pure tests pass and a forced-decision mutation fails at `overlay_options_tests.cpp:70`, proving the tests bind to production.
- **Historical aggregate gates, 2026-08-31 remediation checkpoint** (sanitized shell: no `ZCODE_DESKTOP_CONTEXT_PROMPT_ENABLED`, Windows-first `PATH`; see the SDD ledger note about host-env redaction and `timeout` resolution):
  - Python: `405 passed`; `ruff check .` clean; `mypy src` clean; `pip check` clean.
  - .NET Release across all 13 test projects: `2,080 passed, 0 failed` (Broker 488, RuBackend 650, Harness.Core 210, GUI 303, Cli 119, Adapters 84, Persistence 79, Worker 49, Client 42, Hosting 36, Protocol 11, Core 6, Connectors 3).
  - `build.bat` full release pipeline `13/13` including native overlay tests `2/2`.

These deterministic checks do not establish the optimization plan's missing
before/after work counters, allocation measurements or median/p95 benchmarks.
Performance acceptance remains open in TODO, independently of bug-fix tests.

### Provider/lifecycle review remediation (2026-09-01)

- Eight protected-bootstrap cases prove canonical credential isolation for
  OpenCode/Kilo/Custom and no cloud-key lookup for local providers. The missing
  OpenCode/Kilo cases failed before removal of both key fallbacks, then passed.
- The client process regression failed when Dispose killed its synthetic Host,
  then passed after client-owned job/kill behavior was removed. The helper is
  an isolated hidden test process, not RuBackend or any running user process.
- Startup cancellation checks before/after awaits suppress continuation even
  when a delayed operation ignores cancellation. Voice-status HTTP requests
  receive the startup token. No interactive WinUI acceptance was performed.
- Fault-injected policy/preset HTTP requests formerly exposed a synthetic
  secret/path; they now return safe bounded messages. Request cancellation is
  preserved. Focused backend checks passed 19 cases; startup/API checks 9.
- The initial repair did not publish/launch the GUI, call live providers, mutate
  credentials, install packages or claim performance/live acceptance.
- Final sequential gates on this working tree: Python `405 passed`; .NET
  Release `2,100 passed` across 13 projects (Broker 488, CLI 119, Client 45,
  Connectors 3, Harness.Core 210, Persistence 79, Hosting 36, Protocol 11,
  Runtime.Adapters 84, Runtime.Core 6, Worker 49, RuBackend 661, GUI 309).
  `ruff check src tests scripts tools`, `mypy src` (39 files), `pip check`,
  `62 HTTP + 1 WebSocket` route validation and `git diff --check` passed.
  Actual WinUI Release/x64 compile passed with zero warnings/errors using
  `dotnet build gui-winui3/RuApp.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore`.
  That compile did not update the shortcut's published GUI artifact; see the
  separately authorized release follow-up below.

### Provider catalog refresh evidence (2026-09-01)

Design and plan are in
[the provider catalog design](../superpowers/specs/2026-09-01-provider-catalog-refresh-design.md)
and [the implementation plan](../superpowers/plans/2026-09-01-provider-catalog-refresh.md).
Host discovery (`ProviderCatalogService` with bounded 15 s timeout, 4 MiB cap, 5000-item cap),
capability parsing (`ProviderCatalogParser` for Kilo `opencode.variants` allowlist vs bare-ID unknown),
per-model reasoning persistence (`ProviderReasoningPreferences`), typed additive `GET /models` snapshot,
GUI polling/throttle/single-flight (`ProviderCatalogController`) and protected per-run Worker handoff
(`AgentProviderSnapshotFactory` + `ProviderRequestOptionsValidator`) are covered by controlled fake-handler/clock/credential
tests only — no live provider credentials or generation and no published GUI launch.

Focused and sequential gates on this uncommitted working tree after native OpenAI/Anthropic provider implementation (commit/publish remain separately authorized):

- Backend `RuBackend.Tests` `683 passed` (including provider catalog/cache/parser, OpenAI/Anthropic capability registry `2026-09-01`, catalog request factory with `Authorization: Bearer` / `x-api-key`+`anthropic-version: 2023-06-01`, `store:false` / `max_tokens` 8192, endpoint, malformed preference, neutral startup and run-pinned snapshot cases, plus fail-closed persisted-provider/API alias handling without OpenCode fallback).
- GUI `gui-winui3.Tests` `330 passed` (including provider-catalog cases covering contract, no fabricated defaults, reasoning options, stale/empty vs error, single-flight, throttle, polling, disposal, partial saves, and `OfficialProviderTests` for OpenAI/Anthropic selector rows, DTO wire names, and model-save routing).
- Worker `RuAgent.Runtime.Worker.Tests` `55 passed` (including Worker v2 reasoning-option stripping, trusted-root bootstrap requirement, protocol dispatch via `IModelProviderResolver`, cancellation cleanup, and internal `per-step` bootstrap startup without provider fallback).
- Adapters `RuAgent.Runtime.Adapters.Tests` `169 passed` (including protected-bootstrap lifecycle with protocol/output-cap, `BoundedSseReaderTests` 16, `OpenAiResponsesModelProviderTests` 24, `AnthropicMessagesModelProviderTests` 31). Regression coverage proves raw-byte line rejection before whole-stream consumption, CR-only framing, strict native roles before HTTP, duplicate OpenAI terminal rejection, matching SSE event/type pairs, known-event/ping mismatch rejection, and safe malformed scalar/container mapping.
- Full `scripts/test_dotnet.ps1` Release `2,234 passed` across all 13 projects (Broker 488, CLI 119, Client 45, Connectors 3, Harness.Core 210, Persistence 79, Hosting 36, Protocol 11, Adapters 169, Core 6, Worker 55, RuBackend 683, GUI 330). A local managed-host smoke test also kept Backend, Worker, and Broker alive and returned authenticated HTTP 200 from `/settings`.
- Python `409 passed` (`ruff check src tests scripts tools` clean, `62 HTTP + 1 WebSocket` route contract clean, `git diff --check` clean, `test_current_documentation.py` 2/2, doc links valid).
- `dotnet build gui-winui3/RuApp.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore` clean. `.gitignore` fixed: `models/` → `/models/` so `RuAgent.Runtime.Adapters/Models` (BoundedSseReader, OpenAiResponses, AnthropicMessages, resolvers) is no longer ignored. Anthropic: matching `event: ping` frames are ignored, mismatches involving a known event fail closed, and unknown future event pairs remain forward-compatible.

Boundaries: `mypy src` passed for 39 source files; native/microphone/speaker/VTube Studio/observation live acceptance remain pending.
Provider catalog coverage proves bounded HTTP behavior, capability-registry intersection, native reasoning serialization and protocol dispatch, not liveness of OpenAI/Anthropic/Custom/Ollama/LM Studio endpoints or billing.
No live OpenAI/Anthropic key, catalog refresh, generation, publish, or push was performed here; those are separate user-authorized acceptance steps.

Visible GUI startup/catalog smoke on 2026-09-01 launched the current Release/x64
`RuApp.exe` without automating controls. RuApp, Public Host, Worker and Broker
all remained responsive; authenticated `/status` and `/settings` returned 200.
The selected Kilo catalog refresh returned `status: ok`, one model and one
matching metadata entry, `stale: false`, with no error. The window remains open
for user inspection. No prompt, generation, paid request, voice/device action,
or reasoning-setting mutation was performed, so those live paths remain pending.

### Grouped provider model picker evidence (2026-09-01)

Design and plan are in
[the grouped picker design](../superpowers/specs/2026-09-01-grouped-provider-model-picker-design.md)
and [the implementation plan](../superpowers/plans/2026-09-01-grouped-provider-model-picker.md).

Sequential deterministic gates on this working tree:

- Backend `RuBackend.Tests` Release `706 passed`, including the strict
  atomic-selection expansion table (seven aliases), fail-closed rejection of
  unknown aliases, blank/oversized models, extra properties, mixed
  provider-specific model fields, and byte-for-byte unchanged settings after
  every rejection with no runtime load, plus per-provider catalog endpoint
  coverage: a configured but inactive provider serves its own catalog, a
  provider without a credential fails as `catalog_unauthorized`, and an
  unknown alias fails as `provider_not_active`.
- A follow-up endpoint change (`GET /models` for any configured provider,
  active or not) re-ran the full backend suite at `706 passed` and was
  verified against the locally running managed Host with non-secret state
  only: configured providers returned `status: ok` with usable catalogs
  (32 and 363 usable rows) and unconfigured providers failed closed with
  `catalog_unauthorized` or `catalog_unavailable`.
- GUI `gui-winui3.Tests` Release `354 passed`, including grouped projection
  ordering/visibility/stale/unavailable/search cases, grouped controller
  composition (independent per-provider publishing, coalesced refreshes,
  invalidation with late-result rejection, due-only polling, disposal without
  post-dispose publication), exact two-field body serialization for
  Kilo/OpenAI/Anthropic, and `SettingsSaved` raised only on a success envelope.
- Python `409 passed`; `62 HTTP + 1 WebSocket` route contract unchanged;
  `git diff --check` clean; the documentation/route-matrix pairing passed `4/4`.
- `dotnet build gui-winui3/RuApp.csproj -c Release -p:Platform=x64 --no-restore`
  passed with zero warnings and zero errors.
- Fallback audit: every `fallback`/provider-retry hit in `gui-winui3` and
  `backend-dotnet/RuBackend` is unrelated to model selection (playback-device,
  subtitle-geometry, mutex, port-config, encoding, and lease-registry naming).
  The grouped picker contains no provider/model/key/protocol fallback.

Boundaries: no GUI launch, no live provider catalog/generation, no credential
or settings mutation, no publish, and no push. Interactive grouped-picker
acceptance (visible order, searches, keyboard navigation, themes, cross-provider
save with a simulated failure) remains a user-run manual gate.

### Authorized release follow-up (2026-09-01)

- The user requested commit/push and a refreshed GUI publish. Fresh sequential
  gates passed: Python `409` (including four new build-script regressions),
  .NET `2,100` across all 13 projects, and native CTest `2/2`.
- Artifact inspection caught an existing build-script defect: XAML copy used
  `obj/Release` while x64 publish emitted `obj/x64/Release`, and copy failures
  were ignored. Four behavioral regressions failed before the fix, then passed;
  all 12 build/launcher script cases passed. Clean and copy now use x64, and
  missing/copy-failed XAML fails the release before the native build.
- The corrected `build.bat` completed `13/13`, publishing GUI, CLI, managed
  Worker/Core, Broker and backend. The GUI is at the `start_dev.bat` Release
  path shown below. Its self-contained runtime config, PRI and all 11 loose
  compiled XAML files were verified; GUI/Client/CLI/Worker/Broker/backend DLLs
  and PRI matched their build outputs by SHA-256.
- Ruff, mypy (39 files), dependency and `62 HTTP + 1 WebSocket` route checks
  passed. No GUI was launched, no user Ru process was stopped, and no live
  provider/audio/VTS acceptance was claimed. The old rebuildable GUI publish
  output was replaced; user settings, credentials and session data were untouched.

The route checker must remain at 62 HTTP routes and 1 WebSocket
route (`/voice/stream`; 41 legacy plus 20 additive `/agent/v1/*` plus
`POST /host/shutdown`). The
deterministic Harness is contract evidence. A successful build proves only compilation,
publish output, and the incremental native overlay build/test invocation; it
does not prove that a GUI, microphone, speaker, VTube Studio model, or Windows
audio endpoint worked.

The managed runtime topology is deterministic: one C# Worker serves the Agent
loopback endpoint (`/worker/v2`) and the separate device endpoint
(`/worker/v1`). Worker tokens are distinct; the startup handoff is
credential-free and provider credentials are carried only by per-step
single-use DPAPI handoffs, never by the device graph,
legacy Python configuration, descriptors, logs, or public responses. The
production Python generation/worker modules and compatibility launch branch
are retired; the test-only fixture under `scripts/harness` is not a production
fallback. The retired `runtime-live` command is a bounded diagnostic and does
not start a child topology.

The Phase 1 measurement command times deterministic prompt/tool operations in
small batches to amortize sub-millisecond scheduler noise, then measures their
allocation peak in a separate `tracemalloc` pass. It compares p50, p95, and
peak bytes with the checked-in baseline using the ten-percent gate plus a
bounded 0.05 ms timing-jitter floor; it never loads a model or contacts a
device.

The Worker v2 model inventory is deterministic and should stay aligned with
`AgentRuntimeCoordinatorFactory.RuntimeCapabilities`: `files.list`,
`files.read`, `files.glob`, `files.search`, `files.write`, `files.edit`,
`files.delete`, `files.restore`, `terminal.run`, and `terminal.shell`. Standard
and Full Access receive the same advertised list; policy fallback behavior is
verified separately. `system.volume` is a direct deterministic action and is
not a model-selection binding. `notes.create`, read-only `system.*`, and
`app.list` remain Broker-internal. `files.apply_patch` remains unadvertised.
Typed commands (`/compact`, `/help`, `/skills`, `/skill`) use the shared
Protocol parser and are covered by CLI/Host tests. Web search/fetch are
enabled read-only managed capabilities backed by the fixed DuckDuckGo HTML
endpoint; each call remains bounded, redirect-safe, policy-checked, and marked
untrusted. Declarative Skills are explicit session grants with per-step policy
checks. Connectors and `app.open` remain unadvertised.

### Session deletion regression evidence (2026-08-31)

The GUI client regression test uses an isolated loopback HTTP listener that
returns HTTP 200 with `status: "error"`. `ApiService.DeleteSessionAsync` must
reject that response, and the WinUI delete handler must not refresh or create a
replacement session after application-level failure:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --filter "FullyQualifiedName~SessionDeletionApiTests"
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --filter "FullyQualifiedName~Session"
```

The pre-cleanup verification passed the focused regression (`1/1`), the then
full GUI suite (`339/339`), and the backend session slice (`39/39`). These
behavior tests remain; the cleanup only removed cosmetic GUI checks. No
production session or user data is used by the regression listener.

The desktop launcher and release build share one GUI artifact, and the release
build publishes the managed Worker before the backend:
`gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe`.
`backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Worker.exe`.
`backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Core.dll`.
`build.bat` fails if any of these managed artifacts is absent and verifies that
the GUI runtimeconfig contains `includedFrameworks`, proving the publish is
self-contained. `start_dev.bat` refuses to run when the Worker Release artifact
is missing instead of silently selecting a stale Debug executable; it passes the
workspace directory to a short startup probe so an immediate `RuApp.exe`
failure remains visible. A clean exit code from a second launch is accepted when
the existing single-instance GUI remains alive. Any Windows shortcut should
target `start_dev.bat` so it uses the same release-path and startup checks.

Phase 2 parity is deterministic and status-only. The shared fixture
`tests/fixtures/python-csharp-phase2-device-baseline.json` is exercised by
`RuAgent.Runtime.Worker.Tests/Phase2ManagedDeviceParityTests.cs`; these tests
cover v1 types/sequences, settings shape, voice control ordering, stable
errors, STT normalization, VTS code sets, observation bounds, cancellation,
and forbidden-value redaction without starting a provider or device.

Ruff, Mypy, and `pip check` are required dependency/tooling gates; do not hide
findings with permanent ignores or `continue-on-error`. Live microphone,
speaker, volume, and VTube Studio acceptance remain explicit user-run checks.

### Phase 2 entry evidence before Python retirement (2026-08-30)

This is the deterministic evidence retained from the checkout before the Phase 3
retirement changes:

| Gate | Result |
| --- | --- |
| Python reference/tooling suite | `501 passed` |
| C# Release matrix | `2,085 passed` across 13 test assemblies (14 restored projects including the Harness build) |
| Route contract | `56 HTTP + 1 WebSocket` |
| Runtime-boundary scan | `865` files; schema 1 reported `reachable_count=2` (a startup-path label, not graph analysis) |
| Backend deterministic Harness | `contract` / `passed`; owned cleanup green |
| Agent GUI Harness | 4/4 supported Agent scenarios passed |
| Release build | `build.bat` `13/13`; GUI and managed Worker published |

Managed startup now rejects an explicit legacy `PythonCompatibility` setting
before acquiring a mutex, writing a descriptor, or starting child processes;
the stable code is `python_compatibility_retired`.

The logging benchmark remains an optional latency probe. Its percentile tail
is scheduler-sensitive on this Windows host; a noisy run is recorded as an
environmental measurement failure and does not change the deterministic
runtime gate. The per-writer enqueue tests remain the blocking logging guard.

### Phase 2 manual acceptance record (2026-08-30)

The deterministic release gates are automated and do not stand in for native
device behavior. The following checks are intentionally pending a user-owned
Windows run. Automated verification may launch isolated RuApp, Worker, and
Broker processes, but it does not touch the physical microphone, speaker,
volume endpoint, VTube Studio, or provider process:

| Check | Status | Recorded evidence |
| --- | --- | --- |
| Microphone capture and no-speech handling | pending user-run | not collected |
| OmniVoice stream, cancellation, playback, and subtitle order | pending user-run | not collected |
| VTube Studio catalog, action, and reconnect | pending user-run | not collected |
| Opt-in Gemini observation | pending user-run | not collected |

The authorized 2026-08-30 run passed all four deterministic Agent GUI
scenarios and the backend contract Harness. The retired `runtime-live` command
returns `startup.readiness=blocked_by_startup` with reason
`python_compatibility_retired` and points to `managed_runtime_native`; it does
not start a child topology. No native microphone, audio, VTube Studio, or
Gemini quality claim is made here.

When these checks are run, record only pass/fail, duration, and bounded reason
codes. Never copy prompts, transcripts, audio, credentials, paths, provider
responses, or device values into this document or its artifacts.

### Historical Phase 3 retirement evidence (2026-08-30)

The retirement changes were verified sequentially before the test-suite cleanup:

| Gate | Result |
| --- | --- |
| Python suite | `504 passed` |
| Ruff | passed |
| Mypy | passed (`50` source files) |
| Dependency check | `pip check` passed |
| Python compile check | `compileall` passed |
| C# Release matrix | `2,086 passed` across 13 test assemblies |
| Route contract | `56 HTTP + 1 WebSocket` |
| Runtime-boundary scan | `864` files; schema 1 reported `reachable_count=2` (a startup-path label, not graph analysis) |
| Backend deterministic Harness | `48/56` HTTP passed, `8` external skipped, `1/1` WebSocket passed; cleanup green |
| Agent GUI Harness | 4/4 supported Agent scenarios passed |
| Managed startup smoke | `/status` `200`, wrong bearer `401`; Worker and Broker ports listened and shut down cleanly |
| Retired selector smoke | exit `5`; `python_compatibility_retired`; no child topology |
| Release build | `build.bat` `13/13`; GUI, managed Worker/Core, Broker, and backend published |
| `git diff --check` | passed |

The `runtime-live` command is intentionally retained only as a bounded
retirement diagnostic; it returns `startup.readiness=blocked_by_startup` with
`python_compatibility_retired` and does not launch any child. The GUI voice
runner and native microphone, speaker, OmniVoice, VTube Studio, and Gemini
checks remain separate acceptance boundaries; the Agent GUI scenarios above
exercise the deterministic Agent path only.

## Prompt composer contract checks

Run these focused checks when changing prompt context, composition, Worker v2,
or receipt persistence. Run them sequentially. They cover the canonical seven
layers, strict context/receipt validation, root-only instruction loading and
final-path escape handling, the C# wire codec, internal persistence, and public
SSE cursor suppression.

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_python_runtime_boundary.py
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AgentPromptReceipt|FullyQualifiedName~SystemPromptContract"
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RuInstructionLoader|FullyQualifiedName~RuPromptContextProvider|FullyQualifiedName~WorkerV2Prompt|FullyQualifiedName~WorkerV2Contract|FullyQualifiedName~WorkerV2Client|FullyQualifiedName~AgentSseReconnect"
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~HarnessPayloadStore|FullyQualifiedName~HarnessProjectionTransaction"
```

The deterministic Harness artifact is status-only contract evidence. It and all public
events must exclude prompt text, receipts, raw instruction contents, tool
arguments, filesystem paths, and provider secrets. `model.prompt_composed` is a
durable internal event only; it must remain absent from the public event
allowlist and from CLI/WinUI rendering. Deterministic checks do not replace
live microphone, speaker, VTube Studio, or real Windows endpoint acceptance.

The backend-deterministic Harness is a separate synthetic fixture topology. It
starts the dependency-free test-only `harness.deterministic_worker`, which
serves the v1/v2 contract probes without importing production
`ru_runtime.generation` or `ru_runtime.worker` modules. This keeps contract
probes isolated from the published managed Worker; normal desktop startup is
managed C# only.

This fixture evidence does not prove raw Worker v1 route parity in the
default managed mode. The managed composition now binds every allowlisted
private route to an explicit C# handler. Status/models expose only the active
typed settings (provider credentials remain host-owned), runtime-scoped VTS
state/config and canonical token persistence, and
catalog/action/connect use the managed adapter, voice/STT use typed providers,
and observation control is explicitly disabled until its managed capture and
reaction loop exists. Overlay enable is fail-closed while its process owner is
missing. The generic private-route fallback is no longer reachable
from the managed composition. Public provider-backed model catalogs are now
Host-owned (see the 2026-09-01 catalog evidence); the private Worker model
status is not their discovery source. VTS overlay process/semantic actions
and observation capture/reaction still require a
dedicated managed-runtime integration gate before claiming full production
parity.

On 2026-08-30, a bounded smoke of the freshly published managed runtime used
isolated temporary roots and loopback ports: the authenticated Public Host
`/settings` and Broker `/health` checks returned `200`, while the Agent
`/worker/v2` and Device `/worker/v1` listeners returned the expected `400` for
non-WebSocket probes. The owned process tree was stopped and the probe roots
were removed. This confirms managed startup/listener wiring only; it does not
replace native microphone, OmniVoice, VTube Studio, or Gemini acceptance.

## Test ownership and documentation integrity

Python tests cover the retained OmniVoice provider, reference fixtures and
maintenance/harness tooling. Managed C# suites cover the Public Host, Broker,
Worker, settings migration, session deletion, privacy and cancellation.
Do not keep a copied implementation in a test as evidence about production.

Documentation checks validate local links and ADR metadata, not exact wording,
ADR counts, colors or historical deletion lists. GUI structural checks are
not visual acceptance; keep real responsive-policy and interaction tests.

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_current_documentation.py
```

## evidence_class: contract

Contract evidence proves protocol shape, lifecycle ordering, authorization,
privacy, and cleanup without performing a valid native mutation.

```powershell
$artifact = Join-Path $env:TEMP 'ru-action-kernel-contract.json'
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output $artifact
$report = Get-Content -Raw -LiteralPath $artifact | ConvertFrom-Json
if ($report.evidence_class -ne 'contract') { throw 'wrong evidence class' }
if ($report.summary.status -ne 'passed') { throw 'contract Harness failed' }
```

The Harness owns temporary Worker, Broker, Public Host, credentials, loopback
ports, data root, and workspace. Its synthetic Worker exercises a read-only
`files.read` Agent step against the temporary fixture. The canonical binding
must emit one bounded tool activity before the Agent completion and must not
open a native device endpoint. The artifact contains statuses and safe reason
codes only; raw tool arguments, receipts, prompts, and replies remain absent.

A separate authenticated private-Broker probe opens only `workspace.write`,
requests `notes.create` in the temporary workspace, approves once, expects the
replay to be denied as `approval_stale`, closes the session, and removes the
fixture. The schema-v2 artifact contains bounded statuses and cleanup
booleans, not request arguments, note content, workspace paths, tokens, or
native output values.

For a cold start, the Harness keeps all three port leases while it builds the
Broker and Public Host projects sequentially, then launches each with
`--no-build`. This avoids concurrent writes to their shared
`RuAgent.Protocol` intermediate output. If a child still exits before
readiness, `startup.readiness` reports `child_unexpected_exit` plus only the
child role, exit code, and lifecycle classification; a live child that never
becomes ready reports `readiness_timeout`.

## evidence_class: adapter

Adapter evidence proves result semantics with fake endpoints, temporary files,
and bounded child processes. It does not open the real Windows audio endpoint.

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_artifacts.py tests/test_harness_session.py tests/test_agent_harness.py tests/test_native_volume_acceptance.py
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AdapterConformanceTests|FullyQualifiedName~VolumeToolsTests|FullyQualifiedName~TerminalAdapterTests|FullyQualifiedName~ProcessTreeRunnerTests"
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentApprovalTests|FullyQualifiedName~AgentApprovalUiContractTests"
```

These tests establish file write/readback and cleanup, notes approval,
terminal zero/non-zero/timeout/start-failure outer statuses, volume argument
validation before endpoint acquisition, verified fake-endpoint success, and
readback-mismatch failure. They also establish restoration logic and artifact
privacy for the opt-in native runner through injected process, HTTP,
WebSocket, and artifact-writer seams.

Host tests cover cross-session rotation with a stateful single-owner fake and
prove close-before-open, same-session reuse, and no dispatch after close failure.

External-directory tests prove canonical descendant grants for read/list and
create/edit, sibling isolation, absolute-path output, project-scope reuse across
runs, and delete/root-directory approval gates.

## evidence_class: native

Native evidence is an explicit user-owned smoke against a freshly published
`RuAgent.Broker.exe`. It is not part of CI, the default build, or the
deterministic Harness. Do not run it without the confirmation flag:

```powershell
$broker = 'D:\LLM\backend-dotnet\RuAgent.Broker\bin\Release\net10.0\win-x64\publish\RuAgent.Broker.exe'
$artifact = Join-Path $env:TEMP 'ru-native-volume.json'
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\native_volume_acceptance.py --broker-exe $broker --output $artifact --confirm-native-volume-change
```

The runner proves the correct bearer and a fresh wrong bearer before opening
the private WebSocket, captures the original state, changes volume by one
point, verifies the same endpoint role/hash, and restores level and mute state
in `finally`. Restoration failure forces exit 1 and a failed native artifact.
Both CLI paths must be absolute, the output must be a `.json` file distinct
from the published Broker, and the child Broker receives only the required
descriptor/workspace variables plus a small Windows process environment
allowlist; unrelated Ru and provider credentials are not inherited.
The artifact excludes levels, mute values, credentials, paths, arguments,
outputs, device names, and native exception text.

The authorized native volume acceptance on 2026-08-30 passed both the one-point
change/readback and the restoration check, with all owned Broker artifacts and
processes cleaned up. The runner opens the Broker with the `agent` capability
profile so `system.volume` is visible after the Agent-only cutover; it does not
record the endpoint level, mute state, device identity, or request values.

Even a passing native artifact does not prove audible quality or that the user
observed the GUI behave correctly. The user must separately inspect the
Windows playback level and confirm the restored state.

## Deterministic GUI Harness

The GUI Harness is manual and interactive because it launches WinUI. It uses
synthetic microphone/speaker boundaries and refuses to start while another
RuApp instance exists. The supported Agent scenarios are exactly:

- `agent-volume-contract`
- `agent-cancel`
- `agent-stale-result`
- `agent-private-metadata`

The cancel and stale-result scenarios replace the Agent session while its
synthetic tool request is still in flight, wait beyond the delayed result, and
fail if a user/assistant message, private marker, or enabled Agent mode leaks
back into the replacement session.

Each Agent GUI run receives a fresh temporary workspace through the Harness
descriptor, creates the bounded `harness-agent.txt` fixture, and approves the
workspace-trust card through UI Automation before the model step starts. The
`agent-volume-contract` identifier is retained for catalog compatibility; its
Agent-only fixture now exercises the read-only `files.read` loop and checks the
bounded terminal reply. It never opens or changes the Windows volume endpoint.

HTTP route probes use schema-valid VTS fixtures: the complete `MotionConfig`
shape for `/vts/config`, a `typing` boolean for `/vts/activity`, and a bounded
codes string for `/vts/action`. This keeps compatibility probes aligned with
the typed route contract; live VTube Studio reconnect/action behavior remains
a separate user-run acceptance gate.

The UI driver verifies the static Agent indicator without requiring an idle status row;
`AgentStatusText` is expected only after an Agent request or another activity
that needs user interaction. Typed requested, granted, verified, and failed
copy is covered by WinUI contract tests. Agent artifacts and event-sink records
contain only bounded states, counts, tool IDs, durations, and safe reason
codes—not arguments, output, levels, amounts, mute state, prompts, replies,
device identity, endpoint hashes, or exception text. The WinUI activity feed
additionally accepts only typed transient `agent.activity` fields:
basename/line range, symbol, counts, edit counts, duration, and `safe_code`;
malformed or content-bearing transient frames are ignored and never become
durable history.

Broker approval dispatch/replay is covered by the private deterministic probe;
the visible approval card remains covered by `AgentApprovalTests` and
`AgentApprovalUiContractTests`. GUI Harness scenarios do not claim that
terminal, files, Web, connectors, or skills are model-usable.

Agent-only visual acceptance covers the GUI cleanup in Task 7 of the approved
plan. The composer leaves one static Agent indicator and one compact preset
selector (`Standard`/`Full Access`), keeps the welcome layout fixed, and uses
the Agent HTTP/SSE path for every prompt. Grow a transcript until scrolling is
needed and verify messages stay centered while the native vertical scrollbar
sits at the far-right edge; horizontal scrolling must remain disabled. These
remain manual release checks; deterministic tests cover the source and layout
contract but do not replace visual inspection.

A passing deterministic GUI result does not claim an audible Windows volume
change and does not accept live microphone capture, audible OmniVoice,
subtitles, or VTube Studio. The legacy runtime-live compatibility probe is
blocked until a managed-native live path is available; physical microphone
accuracy, speaker quality, natural bilingual conversation, and VTube Studio
behavior remain explicit user-run checks. Real volume mutation remains the
opt-in `native_volume_acceptance.py` path with
`--confirm-native-volume-change` and separate user confirmation.

See [Agent tools and truthful execution](agent-tools.md) for the current
capability matrix and ownership boundary.
