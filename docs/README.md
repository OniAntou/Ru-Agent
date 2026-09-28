# Ru documentation

This directory contains only current operating documentation and accepted
architecture decisions. Completed implementation plans and design drafts are
kept in Git history instead of the working tree.

A plan may sit in the working tree only while it is still open, and it must say
so with a `status: pending` marker. `test_completed_implementation_archives_are_
not_kept_in_the_checkout` enforces that for `docs/superpowers/plans/`,
`docs/superpowers/specs/` and `docs/plans/`, so an unlabelled draft cannot sit
in the checkout indefinitely.

## Current system

- [Architecture](architecture.md)
- [Domain language](../CONTEXT.md)
- [Current runtime behavior](operations/runtime-behavior.md)
- [TUI known limitations and debugging](operations/tui-known-limitations.md)
- [Provider catalogs and model-specific reasoning](operations/provider-catalog.md)
- [Operations and verification](operations/verification.md)
- [Agent tools and truthful execution](operations/agent-tools.md)
- [Logging and diagnostics](operations/logging.md)
- [Python dependency ownership](operations/python-dependency-ownership.md)
- [Python runtime boundary](operations/python-runtime-boundary.md)
- [Shared workspace coordination](operations/shared-workspace-coordination.md)
- [Driving Ru without a provider](operations/scripted-model.md)
- [ASP.NET Core public route contract](aspnetcore-route-matrix.md)
- [Dated verification records](verification/)
- [Canonical route data](../backend-dotnet/public-route-contract.json)
- [Contributing](../CONTRIBUTING.md)
- [Agent guidance](../AGENTS.md)

## Decisions

- [Architecture decision records](adr/)
- [ASP.NET Core Public Host](adr/0001-aspnet-core-public-host.md)
- [Typed Worker protocol v2](adr/0002-typed-worker-protocol-v2.md)
- [ADR 0003 reserved](adr/0003-reserved.md)
- [Canonical Ru data location](adr/0004-canonical-ru-data-location.md)
- [Local conversational-model retirement](adr/0005-retire-legacy-llama-line.md)
- [Structured logging and activity](adr/0006-structured-logging-and-activity-boundaries.md)
- [Capability profiles](adr/0007-mode-capability-profiles.md)
- [Shared workspace coordination](adr/0008-shared-workspace-coordination.md)
- [Broker execution authority](adr/0009-broker-execution-authority.md)
- [Product logic outside WinUI code-behind](adr/0010-product-logic-outside-winui-code-behind.md)
- [Model autonomy and workspace trust boundary](adr/0011-model-autonomy-and-workspace-trust-boundary.md)

Git history is the recovery source for removed dated plans and specifications.

## Open plans

- [TUI image attachments](plans/tui-image-attachments.md) — `status: pending`

Agent permissions use the canonical, session-scoped `plan` / `standard` /
`full_access` profiles from `RuAgent.Protocol`: `plan` allows workspace read and
Web while denying terminal and mutation; `standard` auto-allows bounded-local
in-workspace mutation and asks for external/escape intent; `full_access` allows
external structured files and arbitrary terminal while structured protected
paths remain denied. Workspace trust is explicit once per canonical identity:
the GUI confirms it and CLI `run`/`chat` use `--trust-workspace`. Full Access
requires the GUI gate or CLI `--confirm-full-access`.

The canonical profiles do not synthesize `web_approval_required`; that legacy
reason remains available to an explicit ask rule. Current approval reasons also
include `external_directory_required`, `terminal_delete_requires_approval`,
`terminal_external_mutation_requires_approval`,
`terminal_timeout_requires_approval`, and `terminal_unknown_executable`.
Folder grants remain session-scoped, while workspace trust follows the canonical
identity. Full-access terminal runs as the current Windows user, can access data
outside the workspace, and is not a network/path sandbox. Hard timeout, output
cap, cancellation, and process-tree cleanup remain enforced.
