# Ru documentation

This index separates current system documentation from dated implementation
records. Current documents describe what operators and maintainers should rely
on now; historical records explain how earlier changes were reached and are not
an operating contract.

## Current

- [Architecture](architecture.md)
- [Domain language](../CONTEXT.md)
- [Current runtime behavior](operations/runtime-behavior.md)
- [Operations and verification](operations/verification.md)
- [Agent tools and truthful execution](operations/agent-tools.md)
- [ASP.NET Core public route contract](aspnetcore-route-matrix.md)
- [Architecture decisions](adr/)
- [Canonical route data](../backend-dotnet/public-route-contract.json)
- [Local conversational-model retirement](adr/0005-retire-legacy-llama-line.md)

## Implemented checkpoints (Phases 1–3 selected surfaces, 2026-08-31)

- [Phase 1: Harness Foundation and Headless Read-Only Tracer](superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity-01-harness-foundation.md) — Public Host owns Worker/Broker in one Job Object, Worker v2 one-step `AssistantStep`, 56 HTTP + 1 WebSocket (`/voice/stream`; 15 `/agent/v1/*` additive), encrypted SQLite harness store, SSE reconnect with durable `id:<sequence>`, and thin `RuAgent.Cli` tracer. Text and microphone turns now use the same authenticated Agent API/SSE path.
- [Phase 2: Policy Authority and File Tools](superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity-02-policy-file-tools.md) — Standard and Full Access receive the same Worker v2 file/terminal advertisement; Standard applies ordinary-workspace and analyzer/rule decisions while Full Access broadens fallbacks without bypassing trust or invariants. Immutable encrypted-at-rest policy/workspace snapshots, exact-scope approvals, external-directory grants, hash-guarded mutations, recoverable delete/restore, and CLI policy controls are active. `files.apply_patch` is not advertised or model-usable.
- [Phase 3: Terminal and reconciliation](superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity-03-terminal-git.md) — `terminal.run`/`terminal.shell`, bounded output, process-tree cancellation, and reconciliation are active on the headless Agent v2 path. The managed Git commit workflow (Task 6) is not shipped; Git commands remain ordinary terminal requests.
- Desktop entry point — WinUI exposes one static Agent indicator and a compact `Standard`/`Full Access` selector in the composer. There is no Chat generation mode or workspace editor. The transient activity feed is safe-summary-only and separate from durable history.
- Agent-only transport retirement — [design](superpowers/specs/2026-08-29-ru-agent-only-chat-removal-design.md) and [implementation plan](superpowers/plans/2026-08-29-ru-agent-only-chat-removal.md) are implemented through deterministic release gates: `/voice/stream` is the only public WebSocket, Worker v1 is runtime/device-only, and every text/voice turn uses Agent API/SSE.
- OpenCode-style prompt composition — the [focused contract](superpowers/specs/2026-08-29-ru-opencode-system-prompt-composer-design.md) and [implementation plan](superpowers/plans/2026-08-29-ru-opencode-system-prompt-composer.md) are shipped. Each Agent step receives one freshly composed system message, root-bound instruction context, and a digest-only internal receipt.
- Managed runtime Phase 2 — one published C# Worker now owns both private Worker v2 (Agent) and Worker v1 (runtime/device) ports with separate tokens and a single-use DPAPI provider bootstrap. Runtime settings, voice orchestration, STT/VTS/observation boundaries, leases, and OmniVoice process lifetime are managed in C#. The deterministic Harness uses a test-only fixture, while managed STT/Gemini credentials remain opt-in.
- Python runtime Phase 3 retirement — the production Python Agent/Worker modules, compatibility launch branch, and legacy live launch path are retired. Managed C# startup is the only supported production topology; `runtime-live` is a bounded diagnostic and native device acceptance remains user-owned.
- Phase 2 deterministic evidence — the shared `python-csharp-phase2-device-baseline.json` fixture and Python/C# parity tests cover v1 sequence/type contracts, settings, voice ordering, stable errors, STT normalization, VTS code sets, observation bounds, cancellation, and privacy redaction. Release build publication also requires `RuAgent.Runtime.Core.dll` beside the managed Worker executable.
- Typed Agent commands and bounded web tools — `/compact`, `/help`, `/skills`,
  and `/skill` share one Protocol parser and Public Host route. Managed startup
  enables read-only `web.search` and `web.fetch` through the fixed
  `https://html.duckduckgo.com/html/` endpoint; results are bounded, sanitized,
  and marked untrusted, with fetched text wrapped in
  `[untrusted_web_reference]`.

## Completed implementation plans

- [Python-to-C# Runtime Phase 2 implementation plan](superpowers/plans/2026-08-30-python-to-csharp-runtime-phase-2.md) — managed Worker v1/device ownership, parity/privacy gates, and release/manual acceptance evidence.
- [Python runtime Phase 3 retirement plan](superpowers/plans/2026-08-30-python-runtime-phase-3-retirement.md) — production Python Agent/Worker retirement and the bounded native-acceptance boundary.

## Approved future work

- [Python-to-C# runtime migration design](superpowers/specs/2026-08-29-python-to-csharp-runtime-migration-design.md) — the staged replacement record; Phase 1 through the managed Phase 3 production cleanup are shipped while native acceptance remains a separate gate.
- [OpenCode/Pi coding-loop parity design](superpowers/specs/2026-08-24-ru-opencode-pi-coding-loop-parity-design.md) and [implementation program](superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity.md) — remaining managed Git/real-model acceptance and later session/extension milestones are tracked there.
- OS-enforced filesystem/network sandbox — a separate security project. Policy rules, path checks, and `network_intent` are not a sandbox and must not be relabeled as one.

## Historical implementation records

- [FastAPI decommission validation](aspnetcore-cutover-validation.md)
- [2026-08-24 codebase security and reliability remediation](superpowers/specs/2026-08-24-codebase-security-reliability-remediation-design.md)
- [Action execution kernel v1](superpowers/specs/2026-08-22-ru-action-execution-kernel-v1-design.md) and its [implementation plan](superpowers/plans/2026-08-22-ru-action-execution-kernel-v1.md)
- [Agent tools and skills roadmap](superpowers/plans/2026-08-22-ru-agent-tools-skills.md)

The remaining dated UI, voice, logging, policy, and harness plans/specs remain
in the tree only as implementation history. They are not operating contracts;
the current documents above are authoritative. Each retained record is
bannered as historical or future work so an old checklist is not mistaken for
an active task. Superseded records, when present in a working tree, follow the
same historical-only rule. Git history remains the recovery source for those
records.
