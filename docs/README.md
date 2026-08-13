# Ru documentation

This index separates current system documentation from dated implementation
records. Current documents describe what operators and maintainers should rely
on now; historical plans explain how earlier changes were reached and are not
an operating contract.

## Current

- [Architecture](architecture.md)
- [Domain language](../CONTEXT.md)
- [Current runtime behavior](operations/runtime-behavior.md)
- [Operations and verification](operations/verification.md)
- [ASP.NET Core public route contract](aspnetcore-route-matrix.md)
- [FastAPI decommission validation](aspnetcore-cutover-validation.md)
- [Architecture-v2 cleanup preflight](validation/architecture-v2-cleanup-preflight.md)
- [Architecture decisions](adr/)
- [Canonical route data](../backend-dotnet/public-route-contract.json)

## Active architecture-v2 design

- [Umbrella design](superpowers/specs/2026-08-13-ru-architecture-v2-refactor-design.md)
- [Plan 1: architecture spine and cleanup preflight](superpowers/plans/2026-08-13-ru-architecture-spine-cleanup-preflight.md)
- [Plan 2: legacy removal and workspace hygiene](superpowers/plans/2026-08-13-ru-legacy-removal-workspace-hygiene.md)

## Historical implementation records

Superseded pre-Architecture-v2 plans and specs were removed after current
decisions were consolidated into the architecture, ADRs, runtime behavior, and
verification documents. Git history remains the recovery source for those
implementation records.
