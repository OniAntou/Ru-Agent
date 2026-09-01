# Tool Truth and Selection Design

**Date:** 2026-09-02

**Status:** Approved for implementation planning

**Scope:** Establish one Protocol-owned definition catalog for Ru tools and make
the managed Agent's prompt-visible capabilities and Worker-selectable tools
derive from the same runtime-filtered projection.

## Problem

Ru currently has several conflicting descriptions of its tool surface:

- `AgentRuntimeCoordinatorFactory.RuntimeCapabilities` lists twelve managed
  Agent tools.
- `LoopbackModelStepRunner.RuntimeTools` independently lists the same intended
  twelve tools and contains local fallback schemas for `files.delete` and
  `files.restore`.
- `RuPromptContextProvider.CapabilityScopes` repeats IDs and scopes, but skips a
  capability when `AgentModelToolContractCatalog` has no matching contract.
- `AgentModelToolContractCatalog` defines fifteen tools, mixing model-visible,
  deterministic-device, and Broker-internal surfaces while omitting
  `files.delete` and `files.restore`.
- `ToolCatalog` and `HarnessToolGateway` each maintain additional manifest,
  allow, validation, and execution lists.

As a result, the Worker accepts twelve model selections while the composed
system prompt describes only ten when web tools are enabled. Tool schemas,
descriptions, scopes, and exposure classes can drift without a failing test.
Existing focused tests validate individual projections but do not prove that
the projections agree.

Tool selection is also under-specified for the model. The composed capability
block currently contains generic summaries such as `Host tool capability:
files.edit`, while provider adapters do not send `model_tools` as native
function definitions. A model is therefore expected to emit a JSON selection
without receiving the exact envelope, schema, or useful tool description in
its system prompt.

## Goals

- Create one immutable Tool Definition Module in `RuAgent.Protocol`.
- Give every current Ru tool an explicit identity, version, description,
  proposal schema, example, scope, and exposure flags.
- Derive the managed Agent prompt projection and Worker selection projection
  from the same runtime-filtered definitions.
- Make the text-selection envelope and argument schemas explicit to every model
  provider.
- Remove local tool-ID, scope, and schema fallbacks from the managed Agent path.
- Add parity gates that fail when any projection drifts.
- Preserve existing policy, approval, durable execution, and provider behavior
  except where the current prompt/selection mismatch is corrected.

## Non-goals

- No `help.docs` implementation in this slice. Its approved design follows
  after this foundation.
- No migration of managed Agent execution from `HarnessToolGateway` to
  RuAgentBroker.
- No provider-native function/tool calling.
- No connector exposure for calendar, tasks, or mail.
- No CRLF, parallel search, terminal stdin, recovery-limit, OS sandbox, or Git
  workflow behavior changes.
- No new public HTTP route, setting, permission preset, or GUI surface.
- No claim that existing allowlists or request metadata form an OS sandbox.

## Chosen architecture

### Deep Tool Definition Module

Add `AgentToolDefinitionCatalog` to `RuAgent.Protocol`. The Module presents one
small immutable Interface: enumerate definitions, find a definition by exact
ID/version, and select definitions by exposure flag. The implementation owns
the schema factories, examples, descriptions, scopes, and stable ordering.

Deleting this Module would force the same knowledge back into the prompt
provider, Worker settings builder, Protocol contract catalog, and tests. That
leverage and locality make the Module deep rather than a pass-through.

The existing `AgentModelToolContractCatalog` becomes a compatibility projection
over definitions instead of an independent registry. Existing callers of
`Get`, `Find`, `ToolIds`, and `WebToolIds` remain source-compatible during this
slice, but their values are derived from the new catalog.

### Definition contract

Each immutable `AgentToolDefinition` owns:

| Field | Meaning |
| --- | --- |
| `ToolId` | Stable dotted ID |
| `ToolVersion` | Exact semantic version; current value `1.0.0` |
| `Description` | Concise model-facing purpose and important use constraint |
| `Scope` | Canonical single scope required by the current managed Agent projection |
| `Exposure` | Flags controlling model, Broker-internal, and deterministic-host projection |
| `InputSchema` | Fresh strict proposal JSON schema with `additionalProperties: false` |
| `ExampleArguments` | Fresh valid example or null when an empty object is sufficient |

Schema and example access returns fresh JSON nodes so one caller cannot mutate
catalog state seen by another caller. Definitions sort by an explicit stable
ordinal, not dictionary iteration or alphabetical accident.

### Exposure flags

`AgentToolExposure` is a flags enum with these values:

- `ModelVisible`: may appear in the managed Agent prompt and Worker model-tool
  selection surface when its runtime gate is enabled.
- `BrokerInternal`: may be registered by the private Broker but is not thereby
  made model-visible.
- `DeterministicHost`: selected by a Host-owned deterministic intent path rather
  than ordinary model tool selection.

Exposure is not permission. Standard versus Full Access, approval decisions,
risk, protected paths, and runtime gates remain authoritative after a model
selects an exposed tool.

## Canonical inventory

The catalog contains exactly seventeen definitions at this checkpoint.

### Managed Agent model definitions

| Tool | Scope | Runtime gate |
| --- | --- | --- |
| `files.list` | `workspace.read` | always |
| `files.glob` | `workspace.read` | always |
| `files.search` | `workspace.read` | always |
| `files.read` | `workspace.read` | always |
| `files.write` | `workspace.write` | always |
| `files.edit` | `workspace.write` | always |
| `files.delete` | `workspace.write` | always |
| `files.restore` | `workspace.write` | always |
| `terminal.run` | `terminal.execute` | always |
| `terminal.shell` | `terminal.execute` | always |
| `web.search` | `network.search` | managed web search enabled |
| `web.fetch` | `network.fetch` | managed web fetch enabled |

All twelve use `ModelVisible`. Both Standard and Full Access receive the same
enabled model definitions, consistent with ADR-0007. The selected preset affects
policy decisions, not the advertised list.

### Non-model definitions

| Tool | Scope | Exposure |
| --- | --- | --- |
| `system.time` | `system.read` | `BrokerInternal` |
| `system.status` | `system.read` | `BrokerInternal` |
| `app.list` | `system.read` | `BrokerInternal` |
| `notes.create` | `workspace.write` | `BrokerInternal` |
| `system.volume` | `system.write` | `BrokerInternal | DeterministicHost` |

These definitions never enter the managed Agent prompt solely because a Broker
adapter exists. `system.volume` remains a deterministic device action and is
not converted into an ordinary model binding.

## Canonical scopes

Move the shared scope strings needed by definitions into a Protocol-owned
`AgentToolScopes` module:

- `workspace.read`
- `workspace.write`
- `terminal.execute`
- `network.search`
- `network.fetch`
- `system.read`
- `system.write`

Broker `ToolScope` constants remain as compatibility aliases or are validated
against these exact values. Broker-only scopes such as approval/session/skill
authority stay Broker-owned. This avoids making Protocol depend on Broker while
preventing spelling drift in the shared tool contract.

## Proposal schemas

The schema describes only arguments a model is allowed to propose. Internal
preparation state, canonical paths, preimage hashes, approval tokens, recovery
bytes, and execution IDs are not model inputs.

### File discovery and read

- `files.list`: optional `path`, optional `max_entries` from 1 through 512.
- `files.glob`: required `pattern`; optional `path`; optional `max_results` from
  1 through 64.
- `files.search`: required `query`; optional `path`, `file_glob`,
  `case_sensitive`, canonical `is_regex`, and `max_results` from 1 through 64.
  The legacy `regex` alias is not part of the proposal schema.
- `files.read`: required `path`; optional non-negative `offset`; optional
  `limit` from 1 through 256 KiB.

### File mutation and recovery

- `files.write`: required `path` and `content`; optional `overwrite`.
- `files.edit`: required `path` and non-empty `old_string`; optional
  `new_string`; optional `expected_occurrences` from 1 through 1024.
- `files.delete`: required `path` only.
- `files.restore`: required `recovery_id` only.

`expected_sha256` is intentionally absent from all model proposal schemas. The
execution kernel captures and binds the actual preimage during preparation. An
adapter may use a typed/internal hash after the proposal seam, but the model may
not provide or override it.

### Terminal and web

Existing bounded terminal schemas remain unchanged: `terminal.run` uses an
executable plus bounded argument vector; `terminal.shell` uses bounded script
text; both accept working directory, timeout, and network-intent metadata.

Existing web schemas remain unchanged: one bounded query for `web.search` and
one bounded absolute HTTP(S) URL for `web.fetch`.

## Runtime projection

Add a focused runtime projection in the Host that receives only current runtime
availability, initially the two web enabled flags. It selects model-visible
definitions while preserving catalog order.

The projection yields:

- ten core definitions when web tools are disabled;
- twelve definitions when both managed web tools are enabled;
- a matching subset if search and fetch gates ever differ.

`AgentRuntimeCoordinatorFactory.RuntimeCapabilities` no longer stores a
handwritten ID list. If a compatibility property is retained for tests or
diagnostics, it returns IDs from the runtime projection.

`RuPromptContextProvider` no longer owns `CapabilityScopes`. For each projected
definition it uses the definition's description, scope, schema digest, schema,
and example to build the typed prompt capability.

`LoopbackModelStepRunner` no longer owns `RuntimeTools` or `CreateToolSchema`.
It builds Worker `model_tools` from the exact capabilities already present in
the `SystemPromptContext`, resolving each ID/version through the catalog. A
missing, duplicate, or digest-mismatched definition fails closed with
`tool_contract_mismatch`; no empty or local fallback schema is emitted.

## Prompt selection contract

Extend `PromptCapability` and the private Worker v2 prompt codec with bounded,
canonical model-facing contract data:

- `description`;
- `input_schema_json`;
- optional `example_arguments_json`.

The Host canonicalizes these JSON strings and binds their SHA-256 digest. Worker
validates strict JSON, object schema shape, size, and digest before composing the
system prompt. The total existing prompt cap remains authoritative.

`SystemPromptComposer` renders each enabled capability with its ID/version,
description, policy scope, exact input schema, and optional example. It also
adds one provider-independent selection instruction:

```json
{
  "tool_id": "files.read",
  "tool_version": "1.0.0",
  "arguments": {
    "path": "README.md"
  }
}
```

When choosing a tool, the model must emit exactly one JSON object in that shape
and no user-facing success claim. The next step receives the durable tool result
and may then answer the user. Ordinary conversational answers must not be
wrapped in the tool envelope.

Tool results remain untrusted reference data. Adding exact schemas does not
raise instruction precedence or expose private execution state.

## Broker and adapter validation

`ToolCatalog` continues to own Tool Manifests: adapter identity, risk,
verification, approval requirement, timeout, output bound, workspace
requirement, and network allowance remain execution-policy concerns.

Each Broker manifest must resolve to an `AgentToolDefinition` with an identical
ID/version and proposal schema digest. A manifest may expose only the definition
flags it actually supports. Existing Broker profile filtering remains in place.

This slice does not force RuAgentBroker to implement every managed Agent tool.
The known Host/Broker execution split is recorded and addressed by the later
Broker authority slice. The new validation makes the split explicit instead of
allowing same-named contracts to drift silently.

`HarnessToolGateway` retains execution during this slice. Its allow and
validation branches must consume projected definitions or validate themselves
against them; they may not introduce an argument accepted by the gateway but
forbidden by the model proposal schema. Internal prepared state stays outside
the proposal JSON.

## Data flow

1. Host startup determines runtime availability for web search and fetch.
2. The runtime projection selects enabled `ModelVisible` definitions.
3. `RuPromptContextProvider` creates typed prompt capabilities from those
   definitions.
4. `SystemPromptComposer` renders the same capability contracts the Worker will
   accept.
5. `LoopbackModelStepRunner` resolves those capabilities back through the
   catalog and serializes Worker `model_tools`.
6. The provider receives the composed text contract. It returns either ordinary
   text or one exact JSON selection envelope.
7. Worker validates the selected ID/version against its projected list; Core and
   the gateway apply existing scheduling, policy, preparation, execution, and
   durable receipt behavior.

At no point does a second hard-coded tool list or fallback schema participate.

## Error behavior

| Condition | Result |
| --- | --- |
| Duplicate definition ID/version | Catalog construction fails with `duplicate_tool_definition` |
| Invalid ID, version, scope, description, schema, or example | Catalog construction fails with `invalid_tool_definition` |
| Runtime projection contains unknown or non-model definition | Startup/composition fails with `tool_contract_mismatch` |
| Prompt capability schema digest differs from catalog | Worker step fails non-retryably with `tool_contract_mismatch` |
| Model selects disabled/unknown tool or wrong version | Existing non-retryable `capability_unavailable` |
| Model arguments violate the proposal schema | Failed tool selection/execution with `invalid_arguments`; adapter is not invoked |
| Ordinary model text is not a tool envelope | Treated as ordinary assistant output |

All errors remain privacy-safe and exclude raw prompt text, arguments, paths,
credentials, schema bodies, and exception details from public diagnostics.

## Test strategy

Implementation follows RED/GREEN TDD in focused slices.

### Catalog tests

- Prove exactly seventeen definitions and stable ordering.
- Prove twelve `ModelVisible` definitions and the exact five non-model
  definitions.
- Prove every ID/version/scope/exposure/description is valid and unique.
- Prove schema/example nodes are fresh and examples validate.
- Prove `files.delete`, `files.restore`, and `files.search.is_regex` contracts.
- Prove mutation proposal schemas reject `expected_sha256`.

### Projection parity tests

- With web disabled, prompt IDs and Worker model-tool IDs are the same ten.
- With both web tools enabled, both sets are the same twelve.
- Mixed web availability produces the same exact subset in both projections.
- Prompt scope, schema digest, schema JSON, example, and Worker schema agree for
  every projected definition.
- Internal and deterministic-only tools never enter either model projection.
- Source-contract checks prove `RuntimeTools`, handwritten
  `RuntimeCapabilities`, `CapabilityScopes`, and fallback schema branches are
  absent.

### Prompt/Worker tests

- Prove the system prompt contains exact selection-envelope guidance.
- Prove every capability renders a non-generic description and valid schema.
- Prove ordinary text remains ordinary output.
- Prove exact tool JSON is parsed and unknown/disabled IDs fail closed.
- Prove digest mismatch, duplicate definitions, invalid examples, and oversized
  capability data fail before provider execution.

### Gateway/Broker tests

- Prove model arguments are validated before adapter invocation.
- Prove prepared preimage state is Host-owned and cannot be supplied through the
  model proposal schema.
- Prove every Broker manifest resolves to a compatible definition.
- Preserve existing policy, approval, recovery, cancellation, and privacy tests.

Run affected .NET projects sequentially. Run current-documentation tests,
route-matrix validation, and relevant Release builds after focused tests. These
gates do not prove live voice, VTube Studio, microphone, cloud-provider, or GUI
acceptance.

## Documentation lifecycle

After implementation and verification:

- Update `CONTEXT.md` so `Tool Manifest` and `Capability Profile` describe the
  canonical definition projection without changing their accepted meaning.
- Update `docs/operations/agent-tools.md` with exact counts: ten core managed
  model tools plus two runtime-gated web tools, and five non-model definitions.
- Update `docs/architecture.md` with Tool Definition ownership and the still-open
  Host/Broker execution split.
- Update `docs/operations/runtime-behavior.md` with the explicit JSON selection
  contract.
- Update `TODO.md` to mark Tool Truth and Selection complete, retain the
  approved `help.docs` follow-up, and classify hardening items by actual
  production-path status.

Audit the current documentation spine for stale tool-count, schema, deterministic
volume, regex, CRLF, recovery-limit, and Broker-authority claims. Delete a
document only if it is demonstrably superseded and fully replaced. Dated plans
and specs remain historical evidence rather than current behavior.

## Acceptance criteria

- One Protocol-owned catalog contains exactly seventeen current definitions.
- Prompt-visible and Worker-selectable tool IDs are identical for every runtime
  availability combination.
- Standard and Full Access expose the same enabled definitions; policy behavior
  remains different where documented.
- No managed Agent advertisement/selection ID list, scope map, or schema
  fallback is hard-coded outside the canonical definition/projection modules;
  execution dispatch may still name the tool it implements until the later
  Broker authority slice.
- The model receives the exact selection envelope, descriptions, proposal
  schemas, and examples needed to select tools without guessing.
- `files.delete`, `files.restore`, and regex search are represented by canonical
  proposal schemas.
- Model proposals cannot provide internal preimage hashes or other prepared
  execution state.
- Broker manifests validate against canonical definitions without moving the
  execution authority in this slice.
- All affected deterministic tests and builds pass sequentially before
  completion is claimed.
- Unrelated dirty work remains unstaged and unchanged.
