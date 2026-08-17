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
- [ASP.NET Core public route contract](aspnetcore-route-matrix.md)
- [Architecture decisions](adr/)
- [Canonical route data](../backend-dotnet/public-route-contract.json)
- [Plan 3 migration execution evidence](validation/plan3-task6-migration-evidence.md)

## Active architecture-v2 design

- [Umbrella design](superpowers/specs/2026-08-13-ru-architecture-v2-refactor-design.md)

Architecture-v2 plans 1-3 (architecture spine and cleanup preflight, legacy
removal and workspace hygiene, Python package and persistence migration) are
complete and integrated; their validation records are listed below.

## Historical implementation records

- [FastAPI decommission validation](aspnetcore-cutover-validation.md)
- [Architecture-v2 cleanup preflight](validation/architecture-v2-cleanup-preflight.md)
- [Architecture-v2 Plan 2 retirement validation](validation/architecture-v2-plan2-retirement.md)
- [Architecture-v2 Plan 3 package and persistence validation](validation/architecture-v2-plan3-package-persistence.md)

Superseded pre-Architecture-v2 plans and specs were removed after current
decisions were consolidated into the architecture, ADRs, runtime behavior, and
verification documents. Completed Architecture-v2 plan files were removed after
their evidence was consolidated into the validation records above. Git history
remains the recovery source for all implementation records.
