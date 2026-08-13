# Ru documentation

This index separates current system documentation from dated implementation
records. Current documents describe what operators and maintainers should rely
on now; historical plans explain how earlier changes were reached and are not
an operating contract.

## Current

- [Architecture](architecture.md)
- [Domain language](../CONTEXT.md)
- [Operations and verification](operations/verification.md)
- [ASP.NET Core public route contract](aspnetcore-route-matrix.md)
- [FastAPI decommission validation](aspnetcore-cutover-validation.md)
- [Architecture decisions](adr/)
- [Canonical route data](../backend-dotnet/public-route-contract.json)

## Active architecture-v2 design

- [Umbrella design](superpowers/specs/2026-08-13-ru-architecture-v2-refactor-design.md)
- [Plan 1: architecture spine and cleanup preflight](superpowers/plans/2026-08-13-ru-architecture-spine-cleanup-preflight.md)

## Historical implementation records

Documents under `docs/superpowers/` that are not listed above are dated design
or implementation records. They are non-authoritative until the architecture-v2
cleanup extracts any still-current decision and removes superseded records.
