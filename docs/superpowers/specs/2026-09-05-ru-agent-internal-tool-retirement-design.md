# Ru Agent Internal Tool Retirement Design

**Date:** 2026-09-05

## Goal

Reduce Ru Agent to one canonical set of 13 model-usable tools by permanently
removing the five redundant internal or deterministic tools:
`system.time`, `system.status`, `app.list`, `notes.create`, and
`system.volume`.

The retirement is a hard deletion. Ru will not preserve compatibility aliases,
translation wrappers, no-op bindings, or fallback APIs for the removed tool
IDs.

## Resulting tool surface

`AgentToolDefinitionCatalog` will contain exactly these 13 definitions:

- `files.list`, `files.glob`, `files.search`, `files.read`, `files.write`,
  `files.edit`, `files.delete`, and `files.restore`;
- `terminal.run` and `terminal.shell`;
- `web.search` and `web.fetch`;
- `help.docs`.

All 13 definitions are model-visible. The distinction between a larger Broker
catalog and a smaller model catalog disappears. Scope filtering and provider
tool support can still reduce the tools advertised for an individual run.

## Replacement behavior

- Time and host-status requests use bounded terminal commands such as
  PowerShell `Get-Date`, `Get-ComputerInfo`, or `Get-CimInstance`.
- Local application discovery uses bounded terminal commands over the Start
  Menu, registry, or `Get-Command`.
- Notes are ordinary workspace files created through `files.write`.
- Volume changes, when requested, use an explicit terminal command or script.
  Ru no longer promises a dedicated Core Audio implementation, typed volume
  intent, or same-endpoint readback.

These are model choices using existing tools, not automatic rewrites in the
Host or Broker. Calling a retired tool ID fails through the existing
`unknown_tool` path.

## Code retirement

The implementation will remove the five definitions and their now-unused input
schemas, manifests, executor registrations, dependency-injection wiring,
policy/scope mappings, preview copy, and production adapters. Dedicated files
for system inspection, application listing, note creation, and native volume
control will be deleted when no remaining code references them.

The deterministic volume path is retired end to end. This includes the native
Core Audio controller, volume adapter, dedicated acceptance runner, and any
Host or Worker code that constructs `system.volume` requests. Generic activity
and diagnostic infrastructure remains tool-ID-agnostic; tests of that generic
behavior will use a surviving tool instead of retaining volume-only fixtures.

Skill examples and Harness approval probes will migrate to surviving tools.
Approval tests must use temporary files and existing recovery behavior so they
cannot damage the working checkout.

## Compatibility and failure handling

There is no migration period. Stored or in-flight requests naming a removed
tool are unsupported after restart and fail closed as unknown tools. Session
leases advertise only tools present in the reduced catalog, preventing new
runs from producing retired calls.

Terminal execution continues to use the existing command analyzer, timeout,
output bounds, working-directory enforcement, session-scoped folder grants,
and approval policy. This retirement does not weaken terminal safety and does
not introduce a shell-based fallback that bypasses those controls.

## Documentation retirement

Current architecture, operations, verification, backend, CLI, context, and todo
documents will describe only the 13-tool surface and the replacement behavior
above. Dedicated native-volume acceptance instructions and other current-state
claims for the five removed tools will be deleted.

Completed historical plans and design specs remain historical evidence unless
they are explicitly presented as current operating instructions. A historical
document that has become actively misleading and has no unique decision value
may be deleted; otherwise it will receive a concise superseded marker rather
than being rewritten as if the old implementation never existed.

## Verification

Implementation follows RED/GREEN retirement tests:

1. Change Protocol and Broker expectations to exactly the surviving 13 tools,
   and prove all five retired IDs are absent and rejected.
2. Migrate skill, session, approval, activity, and diagnostic fixtures to
   surviving tools while preserving the behavior each test is intended to
   validate.
3. Prove no production source, current-state documentation, or active script
   references any retired tool ID or dedicated volume implementation.
4. Run focused Protocol, Broker, Harness, and Python documentation tests.
5. Run the repository's sequential full .NET and Python test gates, route
   matrix, GUI/backend build, and `git diff --check` before completion.

Automated verification will not execute a real volume-changing command or
otherwise mutate the user's live audio devices.

## Non-goals

- No replacement volume API, PowerShell module, bundled executable, or hidden
  fallback provider.
- No changes to public HTTP or WebSocket routes.
- No redesign of folder grants, terminal approval rules, or provider tool
  negotiation.
- No unrelated cleanup of the existing dirty worktree.
