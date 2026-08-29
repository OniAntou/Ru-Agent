# Declarative Agent Skills

**Status:** Draft for implementation review
**Date:** 2026-08-29
**Scope:** Enable the existing declarative Skill protocol and registry. This
iteration intentionally excludes arbitrary extensions, plugins, and MCP.

## Outcome

Skills become a bounded, inspectable orchestration layer on top of the existing
Broker tool registry. A session can discover the skills it is granted, start a
skill through the existing typed Broker messages, observe bounded progress, and
receive a deterministic receipt. Every underlying tool call still passes
through the normal scope, approval, argument, and output-cap checks.

The implementation never loads or executes arbitrary package code. A skill is
data (`skill.json` plus bounded `instructions.md`) describing a workflow of
allowlisted steps. This keeps the capability model maintainable while leaving
extensions/plugins/MCP for a separate future design.

## Goals

- Wire `SkillManifest`, `SkillPackageLoader`, `SkillRegistry`, and
  `SkillOrchestrator` into the production Broker path.
- Validate skill IDs, SemVer, schemas, step limits, timeouts, trust class,
  required tools, and required scopes before registration.
- Make session grants explicitly bind skill IDs and their required scopes.
- Implement `SkillStart`, `SkillEvent`, and `SkillResult` with stable error
  codes and bounded receipts.
- Preserve per-tool approval and policy enforcement inside a skill.
- Make skill discovery show only skills visible under the current session
  grant/capability profile.
- Reject executable/package content and silently unsafe “skip this step”
  behavior.

## Non-goals

- No extension or plugin ABI, dynamic assembly loading, arbitrary Python/.NET
  execution, or MCP server/client.
- No subagent delegation and no LSP integration.
- No unrestricted file/network capability added by a skill. A skill can only
  request already-registered tools/scopes and the existing policy can deny it.
- No model-generated skill code or self-modifying manifest.

## Registry and package model

`SkillRegistry` is the single source of truth for `(skill ID, version)` and
rejects duplicate registrations. Built-ins and declarative package files use
the same validation path. Package roots are explicit configuration, not the
current working directory, and the loader applies bounded file counts and byte
limits before parsing.

The accepted trust classes remain `builtin` and `signed`. “Signed” means the
configured package verifier has established the package identity; it does not
grant additional scopes. All instructions are treated as data and are never
interpreted as policy or executable code.

The loader rejects executable extensions and unexpected files, invalid JSON,
unknown manifest fields that could hide behavior, oversized instructions,
malformed schemas, invalid SemVer, and steps outside the supported declarative
grammar. Registration is atomic: a failed package cannot partially add a skill.

## Declarative step contract

The first production grammar supports the existing `SkillStepKind` values:

- `CallTool`: invoke one registered tool with bounded JSON arguments;
- `ValidateResult`: apply a named, deterministic result predicate;
- `Branch`: choose between declared step indexes using a predicate result; and
- `NamedTransform`: apply a small allowlist of deterministic transforms.

The implementation must either execute every declared step kind or reject the
manifest at registration. It must never silently skip `ValidateResult`,
`Branch`, or `NamedTransform`. Named predicates/transforms are code-owned
allowlists with bounded input/output; their names are not arbitrary method
lookups. A step count, recursion/branch count, byte budget, and wall-clock
timeout are enforced independently of the tool's own limits.

`CallTool` resolves a manifest by exact tool ID/version, validates arguments
against that tool's schema, and passes the call through
`ToolExecutionService`. The skill cannot construct an unregistered executor
or change a tool's approval requirement.

## Session grant and policy

Opening a session with `skill_ids` resolves each ID/version through the
registry, checks the selected capability profile, and stores the granted skill
IDs in the session grant. Agent and Full profiles include the
`skill.execute` scope; Chat compatibility profiles do not. A missing skill,
scope, or profile permission fails closed with a stable
`skill_not_granted`/`scope_denied` code. The `skill.execute` scope is required
in addition to each manifest's declared scopes.

`SkillStart` requires an authenticated live session, a granted skill, an
input
object that passes the manifest input schema, and a valid request ID. If the
skill or any step requires approval, the normal approval binding remains tied
to the session, skill/tool ID and version, and canonical argument hash.
Approval is never inherited merely because the skill itself was approved.

## Broker integration

`ToolExecutionService` owns the production `SkillRegistry` and
`SkillOrchestrator` and exposes:

- `ListSkills(sessionId)` for granted, validated manifests only;
- `ExecuteSkillAsync(sessionId, skillId, version, input, cancellationToken)`;
- bounded progress/result receipts; and
- stable denial/failure codes without raw exception or secret text.

`AgentConnection` replaces the current `skills_disabled` placeholder:

1. `SkillStart` validates and starts the orchestrator;
2. each completed/denied step emits a `SkillEvent` with step index, tool ID,
   status, and bounded metadata;
3. completion emits one `SkillResult` with output-schema validation, counts,
   and a receipt ID; and
4. cancellation/timeout emits one terminal result and cannot continue the
   workflow.

The connection must not emit raw tool output to an untrusted client unless the
existing output policy allows it. Skill instructions and external content are
not system prompts and cannot override the Agent policy.

## Failure semantics

- Registry or manifest validation errors prevent registration.
- Missing grant, profile, scope, approval, or tool binding prevents execution
  before the adapter runs.
- A failed step terminates the run by default; a manifest may declare a
  bounded, deterministic branch only through the validated step grammar.
- Timeout/cancellation terminates the skill and releases any in-memory state.
- Duplicate start IDs are idempotent only when the canonical skill/version and
  input hash match; otherwise they fail with a conflict.
- Restart does not resume an in-flight skill. The existing receipt/event
  remains the audit record and the caller may explicitly start a new request.

## Tests

Focused tests cover:

1. valid built-in and signed declarative packages;
2. duplicate IDs, invalid SemVer/schema/step/timeout/trust, oversized files,
   unexpected/executable package content, and atomic registration;
3. execution of all four supported step kinds and rejection of unknown or
   silently skipped steps;
4. grant/profile/scope checks, per-tool approval, canonical argument binding,
   cancellation, timeout, output caps, and stable error codes;
5. `AgentConnection` SkillStart/event/result sequencing and duplicate request
   behavior; and
6. discovery filtering so ungranted skills never appear in capabilities.

Existing isolated `SkillOrchestratorTests`, `ManifestTests`, and
`ToolCatalogTests` are retained and extended with production wiring tests.
There are no tests for extensions/plugins/MCP because those capabilities are
out of scope.

## Documentation impact

Update current capability and operations documents to list Skills as
declarative, bounded Broker workflows and to state explicitly that
extensions/plugins/MCP are not implemented. Document package roots, trust
classes, grant/approval behavior, limits, and the absence of arbitrary code
execution. Remove stale statements that all skills are disabled, but retain
historical design records unchanged.
