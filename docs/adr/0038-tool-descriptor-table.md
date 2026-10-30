---
status: accepted
date: 2026-10-30
---

# Tool descriptor table

## Status

Accepted.

## Context

A model-visible tool used to be described in several hand-maintained places that had to
agree: the Protocol definition catalog, the Broker manifest list and its binding list,
the trusted adapter allowlist, the per-tool session scopes that `BrokerSession` mapped
from tool names, and the access class. The Broker gateway and the hook gateway each
computed their own access class. Several of these mappings had a default arm, so a
missing or mistyped entry fell through silently. The hook gateway classified unknown
tools and several mutating tools as `ReadOnly`, while the Broker classified the same tools
differently.

## Decision

1. `RuAgent.Protocol/Tools/AgentToolDescriptorTable.cs` is the one explicit registry of
   model-visible tools, with one row per tool in model order (29 rows). Each row carries
   its ordinal, version, description, primary and extra scopes, risk, approval
   requirement, adapter, workspace requirement, access class, output bound, input schema
   builder and example. Every value a security decision depends on is a literal on its row.
2. `AgentToolDefinitionCatalog` builds its definitions from the table. It rejects a row
   whose ordinal, version or identity is wrong, a duplicate, a non-strict input schema and an
   example that does not validate against its schema.
3. The Broker derives manifests from the rows that it binds (28 rows; the Host-intercepted
   `ask_user` has none). `ToolCatalog` requires executors and manifests to match in both
   directions. `ToolDescriptorMapping` maps risk and adapter values through explicit arms
   that throw on any unmapped value. The trusted adapter allowlist in `ToolRegistry` is
   derived from the `AgentToolAdapter` enum with `HostIntercepted` excluded; the set is the same
   as before (`builtin`, `desktop`, `terminal`, `web`, `connector`, `language_server`).
4. `BrokerSession` adds to a session grant exactly the scopes of the advertised tool's row.
   An unknown or Host-intercepted id adds nothing.
5. `ToolAccessClassification` derives a call's access class (`ReadOnly`, `Mutation` or
   `Terminal`) from the row. The Broker gateway and the hook gateway both call it. A tool
   without a row is `Terminal`. The only argument-dependent refinement is the computer tool's
   observation actions, which are `ReadOnly` (ADR 0031).

Intentional behavior changes:

- Hook-gateway access classes now match the Broker's. `run_code` changes from `Mutation` to
  `Terminal`. `todo_write`, `agent_spawn`, `agent_send`, `agent_cancel` and `agent_apply` change
  from `ReadOnly` to `Mutation`. `mcp`, `code_intelligence`, `ask_user`, `terminal_stop`,
  non-observation `computer` actions and unknown tools change from `ReadOnly` to `Terminal`.
  The class appears only on a hook-denied decision. Broker access classes are unchanged for
  all 29 rows.
- Per-tool session scopes now equal each tool's declared scopes. `read`, `read_file` and
  `run_code` no longer add `workspace.read`. `list` adds `system.read`. `help`, `todo_write`,
  `skill`, `agent_spawn`, `agent_send`, `agent_wait` and `agent_cancel` move from
  `workspace.read` to `system.read`. `agent_apply` adds `workspace.write`. `code_intelligence`
  adds `terminal.execute` instead of the default `workspace.read`. `ask_user` and unknown ids
  add nothing. Over the full advertised catalog the union of scopes is unchanged; a profile
  that advertises a subset can have a different union.

Adding a tool now requires:

- a descriptor row with its schema builder;
- an executor binding (validated in both directions by `ToolCatalog`);
- an explicit mapping arm for any new risk or adapter member;
- deliberate membership where the tool should be advertised: a coding profile in
  `AgentToolProfiles`, a subagent role in `SubagentRoleTools`, or a program in
  `ProgramToolContract`.

## Consequences

Positive:

- Each tool is described in one place. A missing binding, schema or mapping fails at catalog
  construction rather than reaching the model or defaulting silently.
- Hook and Broker denials use the same access class for the same call.

Negative:

- Hook-denied decisions report a different access class for the tools listed above.
- Grants for `read`, `read_file` and `run_code` are narrower, and `code_intelligence` now
  grants `terminal.execute` (the scope its manifest requires). Any saved behavior that relied on
  the previous grants changes.

## Deferred

- Done 2026-10-30: policy-family and alias consolidation, including durable rule matching for
  old operation tags. `PolicyFamily` on the descriptor row is the single source for
  `CodingToolArguments.AdapterToolId`, and legacy rule names live in `AgentToolPolicyAliases`;
  `PolicyFamilyGoldenTests` locks the mapping.
- Done 2026-10-30: host approval-binding derivation moved into the Broker. `AgentApprovalsService`
  no longer parses tool arguments or derives matchers; the Broker returns the remember plan with the
  approval grant ([Remembered Approval](../architecture/broker.md#remembered-approval)).
- A persisted contract lock. The golden locks are source-level tests.
- Manifest `RequiresApproval` for `web_search` and `web_fetch` is not applied by the v2
  policy path. `Standard_v2_web_tools_skip_approval_although_their_manifest_requires_it`
  pins the current behavior.
- Presentation labels for tools.

## Verification

Coverage is in `RuAgent.Protocol.Tests/AgentToolGoldenLockTests.cs` (schema digests),
`RuAgent.Broker.Tests/ToolManifestGoldenLockTests.cs` (manifest security columns),
`RuAgent.Broker.Tests/ToolDescriptorMappingTests.cs` (unmapped values) and
`RuBackend.Tests/Harness/ToolAccessClassificationTests.cs` (both gateways). The implementation
record reports that the golden locks keep schema digests and manifest security columns equal to
the previous code, apart from the scope and access changes listed above.
