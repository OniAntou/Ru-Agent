# Ru Roadmap

Last reviewed: 2026-08-31.

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

## Pending product work

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
- [ ] Implement bundled internal documentation & docs lookup tool (`help.docs` / `app.docs`) in `RuAgentBroker` and System Prompt Composer so Agent can automatically inspect Ru's own features/documentation and guide the user across any workspace.
- [ ] Harden Agent Broker file and terminal tools against edge-case failures:
  - Fix `files.edit` line-ending normalization (`\r\n` vs `\n`) to prevent `edit_no_match` mismatches on CRLF files.
  - Add regex search support and multithreaded directory traversal to `files.search`.
  - Add interactive CLI detection / early abort for commands waiting on stdin in `terminal.run`/`terminal.shell`.
  - Increase and parameterize the 1 MiB recovery cap in `files.delete`/`FileRecoveryStore`.

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
