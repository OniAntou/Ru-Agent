# Ru documentation

Current operating documentation and accepted architecture decisions live here.
Isolated [historical verification evidence](history/verification/README.md)
records old snapshots without making them current operating instructions.
Completed implementation plans and design drafts belong in Git history.

Open plans may sit under `docs/plans/` with a `status: pending` or
`status: in-progress` marker. Generated plans and specs under
`docs/superpowers/` must be removed before verification/integration.
`test_completed_implementation_archives_are_not_kept_in_the_checkout` checks
these locations so obsolete drafts cannot accumulate in the checkout.

## Guides

Task-oriented pages for people using Ru; start here before the reference material below.

- [Install Ru](guide/install.md)
- [Connect a provider and run your first task](guide/first-task.md)
- [Permissions and approvals](guide/permissions.md)
- [Using the terminal UI](guide/terminal-ui.md)
- [Teach Ru your project](guide/teach-ru.md)
- [Subagents](guide/subagents.md)
- [Troubleshooting](guide/troubleshooting.md)

## Current system

- [Windows distributions and updates](operations/distribution-and-updates.md)
- [Architecture and ownership](architecture.md)
- Component implementation: [Host](architecture/host.md), [Harness](architecture/harness.md),
  [Broker](architecture/broker.md), [GUI](architecture/gui.md), [TUI](architecture/tui.md),
  [Providers](architecture/providers.md)
- [Domain language](../CONTEXT.md)
- [Context management and checkpoints](architecture/context-management.md)
- [Database history, backup and maintenance](operations/database-maintenance.md)
- [Current runtime behavior](operations/runtime-behavior.md)
- [Agent performance diagnostics and offline benchmark](operations/agent-performance.md)
- [The stdio RPC bridge](operations/rpc.md)
- [TypeScript SDK for the bridge](../sdk/ru-agent/README.md)
- [TUI known limitations and debugging](operations/tui-known-limitations.md)
- [Provider catalogs and model-specific reasoning](operations/provider-catalog.md)
- [Operations and verification](operations/verification.md)
- [Agent tools and truthful execution](operations/agent-tools.md)
- [Skill setup, invocation and continuity](operations/skills.md)
- [Coding-agent capability and acceptance map](operations/coding-agent-capabilities.md)
- [Custom Terminal/SWE/Everyday coding benchmark](../tools/coding-benchmark/README.md)
- [Logging and diagnostics](operations/logging.md)
- [Python dependency ownership](operations/python-dependency-ownership.md)
- [Python runtime boundary](operations/python-runtime-boundary.md)
- [Shared workspace coordination](operations/shared-workspace-coordination.md)
- [Driving Ru without a provider](operations/scripted-model.md)
- [ASP.NET Core public route contract](aspnetcore-route-matrix.md)
- [Canonical route data](../backend-dotnet/public-route-contract.json)
- [Contributing](../CONTRIBUTING.md)
- [Documentation site](../docs-site/README.md): how these pages are published
- [Agent guidance](../AGENTS.md)

## Decisions

- [Shared skill catalog and run-local workflow snapshots](adr/0032-skill-catalog-and-workflow-snapshots.md)

- [Retire the legacy tool surface](adr/0029-retire-legacy-tool-surface.md)
- [Bounded programmatic coding tools](adr/0028-bounded-programmatic-coding-tools.md)
- [Pinned coding tool profiles](adr/0027-pinned-coding-tool-profiles.md) (legacy compatibility superseded by 0029)

- [`read` returns workspace images to vision models](adr/0026-read-returns-workspace-images.md) (proposed)
- [MCP servers through the Broker behind one `mcp` tool](adr/0025-mcp-servers-through-the-broker.md)
- [Bounded session-run handoff across model changes](adr/0023-bounded-session-run-handoff.md)
- [Full access is literally full](adr/0021-full-access-is-literally-full.md)
- [Always allow parallel sessions in one workspace](adr/0020-always-parallel-workspace-sessions.md)
- [Product packages and explicit updates](adr/0019-product-packages-and-explicit-updates.md)
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
- [Standard workspace Python files](adr/0016-standard-workspace-python-files.md)
- [Workspace terminal policy](adr/0017-workspace-terminal-policy.md)
- [Approval patterns and durable folder grants](adr/0018-approval-patterns-and-durable-folder-grants.md)
- [Delegated subagent runs](adr/0022-delegated-subagent-runs.md)

- [Relational history and database maintenance](adr/0030-relational-history-and-database-maintenance.md)
- [Computer use through the Broker](adr/0031-computer-use-through-the-broker.md)
- [Provider-hosted web search](adr/0033-provider-hosted-web-search.md)
- [Named custom providers](adr/0034-named-custom-providers.md)

Git history is the recovery source for removed dated plans, specifications,
reviews and verification records.

Agent permissions use the canonical, session-scoped `plan` / `standard` /
`full_access` profiles defined by the Protocol catalog and described in
[tool policy](operations/agent-tools.md); [CONTEXT.md](../CONTEXT.md) defines the
domain terms. This index does not restate the matrix. Under
`full_access` everything else runs automatically, and structured protected
paths are allowed like any other target. Workspace trust is explicit once per
canonical identity: the
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
[ADR 0017](adr/0017-workspace-terminal-policy.md); the structured protections hold in `plan` and `standard`. Hard timeout,
output cap, cancellation, process-tree cleanup, credential storage and
diagnostic redaction remain enforced.

- [Subagents](operations/subagents.md): coding/review task browser, explicit model requests, main-agent controls and guarded integration.
