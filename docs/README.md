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

## Implemented checkpoint (Phase 2 — policy authority and complete file tools, 2026-08-25)

- [Phase 1: Harness Foundation and Headless Read-Only Tracer](superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity-01-harness-foundation.md) — Public Host owns Worker/Broker in one Job Object, Worker v2 one-step `AssistantStep`, 54 HTTP + 2 WS (13 `/agent/v1/*` additive, `RuBackend:AgentHarness:Version=v1` default), encrypted SQLite harness store, SSE reconnect with durable `id:<sequence>`, and thin `RuAgent.Cli` tracer (safe NDJSON, no private payload auto-fetch).
- [Phase 2: Policy Authority and Complete File Tools](superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity-02-policy-file-tools.md) — Standard allows ordinary trusted-workspace reads/creates/edits and asks for delete/protected/outside/network/unknown; Full Access changes fallback but never bypasses trust; immutable encrypted-at-rest `PolicySnapshot`/`WorkspaceTrust` snapshots, `CanonicalPathResolver`/`ProtectedPathCatalog` `v1`, Broker v2 `PolicyContext`/`PolicySnapshotEvaluator`, durable approvals first-writer-wins `approval_stale` with exact scope preview and old-snapshot execution ordering, complete `files.*` (`list`/`glob`/`search`/`read` offset/limit plus hash-guarded `write`/`edit`/`apply_patch` and recoverable `delete`/`restore` one-time), CLI `policy list`/`import`/`remove` and `approve`/`deny` plus `run --preset full` interactive, terminal/Git still phase 3.

## Approved future work (Phases 3-4)

- [OpenCode/Pi coding-loop parity design](superpowers/specs/2026-08-24-ru-opencode-pi-coding-loop-parity-design.md)
- [OpenCode/Pi coding-loop parity implementation program](superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity.md)

## Historical implementation records

- [FastAPI decommission validation](aspnetcore-cutover-validation.md)
- [2026-08-24 codebase security and reliability remediation design](superpowers/specs/2026-08-24-codebase-security-reliability-remediation-design.md)
- [2026-08-24 codebase security and reliability remediation plan](superpowers/plans/2026-08-24-codebase-security-reliability-remediation.md)
- [Action execution kernel v1 design](superpowers/specs/2026-08-22-ru-action-execution-kernel-v1-design.md)
- [Action execution kernel v1 implementation plan](superpowers/plans/2026-08-22-ru-action-execution-kernel-v1.md)
- [Superseded volume vertical-slice design](superpowers/specs/2026-08-22-ru-volume-tool-vertical-slice-design.md)
- [Superseded volume vertical-slice plan](superpowers/plans/2026-08-22-ru-volume-tool-vertical-slice.md)
- [Agent tools and skills roadmap](superpowers/plans/2026-08-22-ru-agent-tools-skills.md)

Superseded local-model, training, and evaluation plans were removed after the
current decisions were consolidated into the architecture, ADRs, runtime
behavior, and verification documents. Git history remains the recovery source
for implementation records.
