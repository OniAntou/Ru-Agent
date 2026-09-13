# Ru documentation

This directory contains only current operating documentation and accepted
architecture decisions. Completed implementation plans and design drafts are
kept in Git history instead of the working tree. Plans with unchecked steps
under `docs/superpowers/plans/` and `docs/superpowers/specs/` are active work,
not archives — see the verification note below before deleting them.

> **Active plans (2026-09-10, unchecked steps remain):**
> `plans/2026-09-10-tui-sticky-footer.md`,
> `plans/2026-09-10-god-class-slice2.md`,
> `specs/2026-09-10-tui-sticky-footer-design.md`.
> The `test_completed_implementation_archives_are_not_kept_in_the_checkout`
> guard fails while they exist; that failure is intentional until the plans
> land, and only completed plans are removed afterwards.

## Current system

- [Architecture](architecture.md)
- [Domain language](../CONTEXT.md)
- [Current runtime behavior](operations/runtime-behavior.md)
- [Provider catalogs and model-specific reasoning](operations/provider-catalog.md)
- [Operations and verification](operations/verification.md)
- [Agent tools and truthful execution](operations/agent-tools.md)
- [Logging and diagnostics](operations/logging.md)
- [Python dependency ownership](operations/python-dependency-ownership.md)
- [Python runtime boundary](operations/python-runtime-boundary.md)
- [Shared workspace coordination](operations/shared-workspace-coordination.md)
- [ASP.NET Core public route contract](aspnetcore-route-matrix.md)
- [ASP.NET Core cutover validation (historical)](aspnetcore-cutover-validation.md)
- [Dated verification records](verification/)
- [Canonical route data](../backend-dotnet/public-route-contract.json)

## Decisions

- [Architecture decision records](adr/)
- [ASP.NET Core Public Host](adr/0001-aspnet-core-public-host.md)
- [Typed Worker protocol v2](adr/0002-typed-worker-protocol-v2.md)
- [Canonical Ru data location](adr/0004-canonical-ru-data-location.md)
- [Local conversational-model retirement](adr/0005-retire-legacy-llama-line.md)
- [Structured logging and activity](adr/0006-structured-logging-and-activity-boundaries.md)
- [Capability profiles](adr/0007-mode-capability-profiles.md)
- [Shared workspace coordination](adr/0008-shared-workspace-coordination.md)
- [Broker execution authority](adr/0009-broker-execution-authority.md)

Git history is the recovery source for removed dated plans and specifications.

Agent permissions are session-scoped. The current approval reasons are
`external_directory_required`, `terminal_delete_requires_approval`,
`terminal_external_mutation_requires_approval`,
`terminal_timeout_requires_approval`, `web_approval_required`, and
`terminal_unknown_executable`; no folder grant survives a restarted or new
session.
