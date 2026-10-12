# Ru documentation

This directory contains only current operating documentation and accepted
architecture decisions. Completed implementation plans and design drafts are
kept in Git history instead of the working tree.

A plan may sit in the working tree only while it is still open, and it must say
so with a `status: pending` or `status: in-progress` marker. `test_completed_implementation_archives_are_
not_kept_in_the_checkout` enforces that for `docs/superpowers/plans/`,
`docs/superpowers/specs/` and `docs/plans/`, so an unlabelled draft cannot sit
in the checkout indefinitely.

## Current system

- [Architecture](architecture.md)
- [Domain language](../CONTEXT.md)
- [Current runtime behavior](operations/runtime-behavior.md)
- [The stdio RPC bridge](operations/rpc.md)
- [TypeScript SDK for the bridge](../sdk/ru-agent/README.md)
- [TUI known limitations and debugging](operations/tui-known-limitations.md)
- [Provider catalogs and model-specific reasoning](operations/provider-catalog.md)
- [Operations and verification](operations/verification.md)
- [Agent tools and truthful execution](operations/agent-tools.md)
- [Coding-agent capability and acceptance map](operations/coding-agent-capabilities.md)
- [Custom Terminal/SWE coding benchmark](../tools/coding-benchmark/README.md)
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
- [Typed Worker protocol v2 (superseded by 0012)](adr/0002-typed-worker-protocol-v2.md)
- [ADR 0003 reserved](adr/0003-reserved.md)
- [Canonical Ru data location](adr/0004-canonical-ru-data-location.md)
- [Local conversational-model retirement](adr/0005-retire-legacy-llama-line.md)
- [Structured logging and activity](adr/0006-structured-logging-and-activity-boundaries.md)
- [Capability profiles](adr/0007-mode-capability-profiles.md)
- [Shared workspace coordination](adr/0008-shared-workspace-coordination.md)
- [Broker execution authority (superseded in part by 0014)](adr/0009-broker-execution-authority.md)
- [Product logic outside WinUI code-behind](adr/0010-product-logic-outside-winui-code-behind.md)
- [Model autonomy and workspace trust boundary](adr/0011-model-autonomy-and-workspace-trust-boundary.md)
- [In-process model runner](adr/0012-in-process-model-runner.md)
- [SQLite business persistence](adr/0013-consolidate-business-persistence-in-sqlite.md)
- [In-process execution (terminal sandbox portion superseded)](adr/0014-in-process-execution-and-terminal-sandbox.md)
- [Retire terminal OS sandbox](adr/0015-retire-terminal-os-sandbox.md)
- [Workspace terminal policy](adr/0017-workspace-terminal-policy.md)
- [Approval patterns and durable folder grants](adr/0018-approval-patterns-and-durable-folder-grants.md)

Git history is the recovery source for removed dated plans and specifications.

Agent permissions use the canonical, session-scoped `plan` / `standard` /
`full_access` profiles defined in [`../CONTEXT.md`](../CONTEXT.md) — that file
is the single source for the matrix, and this page does not restate it. Under
`full_access` everything else runs automatically while the structured protected
paths still deny. Workspace trust is explicit once per canonical identity: the
GUI confirms it and CLI `run`/`chat` use `--trust-workspace`. Full Access
requires the GUI gate or CLI `--confirm-full-access`.

The canonical profiles do not synthesize `web_approval_required`; that legacy
reason remains available to an explicit ask rule. Current approval reasons also
include `external_directory_required`, `terminal_external_cwd_requires_approval`,
`terminal_timeout_requires_approval`, `rule_asks`, and
`terminal_protected_path_requires_approval` (never reusable as `allow_always`).
Legacy command-classification reasons are retained for recorded activity.
`allow_always` remembers a bounded pattern as a durable project rule: a
terminal command prefix (resolved executable plus at most two leading non-flag
argv tokens, with the exact launch directory), or an approved external folder
as a path pattern covering its subtree ([ADR 0018](adr/0018-approval-patterns-and-durable-folder-grants.md)).
Flag-first invocations and shell-mode scripts stay exact, and `rule_asks` /
`terminal_protected_path_requires_approval` never become a reusable rule.
A folder grant can stay session-scoped (the Broker grant) or be remembered for
the project; workspace trust follows the canonical identity. Terminal children in both `standard` and `full_access` run as the
current Windows user without OS filesystem or network confinement. Standard authorizes workspace launch directories under
[ADR 0017](adr/0017-workspace-terminal-policy.md); structured protections remain. Hard timeout,
output cap, cancellation, process-tree cleanup, credential storage and
diagnostic redaction remain enforced.
