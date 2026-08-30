# Declarative Agent Skills Implementation Plan

> **Status:** Future implementation plan; work has not started. The current
> runtime has no auto-loaded skill package roots; keep this proposal separate
> from the current capability contract until its gates are shipped.

> For agentic workers: REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Enable bounded declarative Skills through the production Broker session, registry, orchestrator, and typed SkillStart protocol without loading arbitrary code.

**Architecture:** Treat SkillRegistry as the single source of validated skill/version manifests. Extend PolicyGrant and BrokerSession with explicit skill IDs and skill.execute authorization, then run every CallTool through ToolExecutionService and the existing policy/approval pipeline. Package files remain data-only; deterministic predicates/transforms are code-owned allowlists.

**Tech Stack:** C#/.NET 10, xUnit, System.Text.Json.Nodes, existing RuAgent.Broker/Protocol WebSocket contracts.

## Global Constraints

- Only declarative skill.json plus bounded instructions.md are accepted; executable package content is rejected.
- No extension, plugin, MCP, subagent, or LSP integration is added.
- Agent and Full profiles include skill.execute; a skill cannot add scopes outside the session grant.
- Every underlying tool call uses existing schema, scope, approval, timeout, output-cap, and receipt checks.
- Unsupported declarative steps are rejected at registration and never silently skipped.
- Skill receipts/events contain bounded safe metadata, not raw secrets or unrestricted tool output.

---

### Task 1: Bind skills to policy grants and profiles

**Files:**
- Modify: backend-dotnet/RuAgent.Broker/Policy/CapabilityProfile.cs
- Modify: backend-dotnet/RuAgent.Broker/Policy/ToolScope.cs
- Modify: backend-dotnet/RuAgent.Broker/Policy/ToolPolicyEngine.cs
- Modify: backend-dotnet/RuAgent.Broker/Runtime/BrokerSession.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs

**Interfaces:**
- Consumes: current CapabilityProfile.Scopes(), BrokerSession.Open(sessionId, scopes, skillIds), and PolicyGrant.
- Produces: PolicyGrant with optional SkillIds, Agent/Full profiles containing skill.execute, and a policy decision code skill_not_granted.

- [ ] Step 1: Write failing grant/profile tests

Add tests asserting Agent and Full scope sets contain ToolScope.SkillExecute,
Chat does not, a PolicyGrant with no matching SkillIds denies a skill request with
skill_not_granted, and a grant with the skill ID plus skill.execute allows the
skill-level check while still requiring each tool scope.

- [ ] Step 2: Run the focused tests and verify the expected failure

Run:
    dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~PolicyTests|FullyQualifiedName~BrokerSessionTests"

Expected: compile or assertion failure because PolicyGrant has no skill binding
and profiles do not include skill.execute.

- [ ] Step 3: Implement explicit skill grant data

Add an optional IReadOnlySet<string> SkillIds property to PolicyGrant while
retaining the existing constructor call shape. Add skill.execute to Agent and
Full profile scopes. Add ToolPolicyEngine.EvaluateSkill(grant, skillId,
requiredScopes) that checks expiry, skill.execute, skill ID, and every declared
scope with stable codes.

- [ ] Step 4: Run policy/session tests and commit

Run the command from Step 2. Expected: all focused tests pass.

    git add backend-dotnet/RuAgent.Broker/Policy backend-dotnet/RuAgent.Broker/Runtime/BrokerSession.cs backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs
    git commit -m "feat: bind declarative skills to session grants"

---

### Task 2: Harden package loading and registry validation

**Files:**
- Modify: backend-dotnet/RuAgent.Broker/Skills/SkillPackageLoader.cs
- Modify: backend-dotnet/RuAgent.Broker/Skills/SkillRegistry.cs
- Modify: backend-dotnet/RuAgent.Broker/Skills/SkillManifest.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/ManifestTests.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/SkillPackageLoaderTests.cs

**Interfaces:**
- Consumes: SkillManifest, SkillPackageLoader.Load(string packageDirectory), and ToolRegistry.
- Produces: strict package validation, SkillRegistry.Resolve(id, version), ListGranted(grant), and bounded package metadata.

- [ ] Step 1: Write failing loader/registry tests

Create temporary packages for an unknown manifest field, a nested executable file,
an oversized instructions file, a duplicate skill version, a CallTool missing
from required_tools, and a missing skill.execute required scope. Assert each is
rejected with a stable code and the registry has no partial entry. Assert a valid
builtin manifest can be resolved by exact ID/version and filtered by a grant.

- [ ] Step 2: Run the focused tests and verify missing strict behavior

Run:
    dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~ManifestTests|FullyQualifiedName~SkillPackageLoaderTests"

Expected: failure for unknown-field acceptance, missing grant checks, or missing
SkillPackageLoaderTests type.

- [ ] Step 3: Implement strict loader and registry APIs

Bound recursive file count and total bytes before reading. Reject any file not
named skill.json or instructions.md, executable extensions, unknown JSON fields,
and instructions over the existing cap. Require skill.execute in
RequiredScopes, validate every CallTool and supported step field, and expose
Resolve and ListGranted methods. Register builtins and signed packages through
the same atomic validation path.

- [ ] Step 4: Run broker manifest tests and commit

Run the command from Step 2. Expected: all loader/registry tests pass.

    git add backend-dotnet/RuAgent.Broker/Skills backend-dotnet/RuAgent.Broker.Tests/ManifestTests.cs backend-dotnet/RuAgent.Broker.Tests/SkillPackageLoaderTests.cs
    git commit -m "feat: enforce strict declarative skill packages"

---

### Task 3: Make every declarative step executable or reject it

**Files:**
- Modify: backend-dotnet/RuAgent.Broker/Skills/SkillOrchestrator.cs
- Modify: backend-dotnet/RuAgent.Broker/Skills/SkillRun.cs
- Create: backend-dotnet/RuAgent.Broker/Skills/SkillStepEvaluator.cs
- Create: backend-dotnet/RuAgent.Broker/Skills/SkillNamedTransformRegistry.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/SkillOrchestratorTests.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/SkillStepEvaluatorTests.cs

**Interfaces:**
- Consumes: SkillManifest.Steps, PolicyGrant, ToolRegistry, ToolPolicyEngine, and IToolInvoker.ExecuteAsync.
- Produces: bounded execution of CallTool, ValidateResult, Branch, and NamedTransform, with SkillRun state/output/error metadata.

- [ ] Step 1: Write failing step-behavior tests

Add one test per step kind. ValidateResult must fail a deliberately invalid
predicate; Branch must choose the declared true/false index without unbounded
loops; NamedTransform must execute an allowlisted identity/pick-field transform.
Add a test asserting a malformed branch/index or unknown transform is rejected
before any tool invocation. Add timeout/cancellation and max-step assertions.

- [ ] Step 2: Run the focused tests and verify skipped-step failure

Run:
    dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~SkillOrchestratorTests|FullyQualifiedName~SkillStepEvaluatorTests"

Expected: assertion failure because current orchestration silently continues past
non-CallTool steps and has no deterministic evaluator.

- [ ] Step 3: Implement bounded evaluators

Create code-owned registries for result predicates and transforms with explicit
names and bounded JsonNode input/output. Track the current step index, branch
visits, aggregate output bytes, and a linked timeout token. Return
SkillRunState.Failed with stable safe summaries on invalid predicates, branch
targets, transform names, tool failures, cancellation, or timeout. Enforce
MaxSteps independently from manifest length. Preserve the existing per-tool
approval decision and pass the grant's session/skill scopes to the invoker.

- [ ] Step 4: Run the focused tests and commit

Run the command from Step 2. Expected: all step/orchestrator tests pass.

    git add backend-dotnet/RuAgent.Broker/Skills backend-dotnet/RuAgent.Broker.Tests/SkillOrchestratorTests.cs backend-dotnet/RuAgent.Broker.Tests/SkillStepEvaluatorTests.cs
    git commit -m "feat: execute declarative skill steps safely"

---

### Task 4: Wire production SkillRegistry and SkillOrchestrator

**Files:**
- Modify: backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs
- Modify: backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs
- Modify: backend-dotnet/RuAgent.Broker/Configuration/BrokerOptions.cs
- Modify: backend-dotnet/RuAgent.Broker.Tests/ToolExecutionServiceTests.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/SkillProductionWiringTests.cs

**Interfaces:**
- Consumes: ToolCatalog.Registry, SkillRegistry, SkillOrchestrator, BrokerOptions skill roots, and BrokerSession.
- Produces: ToolExecutionService.ListSkills(string sessionId) returning IReadOnlyList<SkillManifest>, ToolExecutionService.ExecuteSkillAsync(string sessionId, string skillId, string version, JsonObject input, CancellationToken cancellationToken) returning Task<SkillRun>, and configured built-in/package registration.

- [ ] Step 1: Write failing production wiring tests

Construct a BrokerHost with one valid builtin manifest and assert ListSkills returns
it only after the session opens with its skill ID and skill.execute. Assert
ExecuteSkillAsync returns a real completed receipt for a read-only tool and no
longer returns skills_disabled. Assert a denied grant causes no invoker/adapter
call.

- [ ] Step 2: Run the focused tests and verify placeholder failure

Run:
    dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~ToolExecutionServiceTests|FullyQualifiedName~SkillProductionWiringTests"

Expected: failure because ToolExecutionService.ListSkills is empty and
ExecuteSkillAsync always returns skills_disabled.

- [ ] Step 3: Wire the service with explicit configuration

Construct one ToolRegistry-backed SkillRegistry after ToolCatalog creation,
register built-ins and configured declarative package roots, and create
SkillOrchestrator with the service as IToolInvoker. Build PolicyGrant from the
BrokerSession's scopes, descriptor expiry, and SkillIds. Validate input/output
schemas and return a bounded ToolResult receipt. Keep default package roots
empty so no unexpected filesystem is scanned.

- [ ] Step 4: Run broker production tests and commit

Run the command from Step 2. Expected: all wiring tests pass.

    git add backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs backend-dotnet/RuAgent.Broker/Configuration/BrokerOptions.cs backend-dotnet/RuAgent.Broker.Tests
    git commit -m "feat: wire declarative skills into broker execution"

---

### Task 5: Enable SkillStart/session discovery over WebSocket

**Files:**
- Modify: backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs
- Modify: backend-dotnet/RuAgent.Protocol/AgentEnvelope.cs
- Modify: backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/AgentEnvelopeTests.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/SkillConnectionTests.cs

**Interfaces:**
- Consumes: ToolExecutionService skill list/execute APIs and existing SkillStart, SkillEvent, SkillResult message types.
- Produces: session_open skill validation, SkillStart execution, bounded SkillEvent progress, and one terminal SkillResult. Inject skill delegates with signatures Func<string, IReadOnlyList<SkillManifest>> listSkills and Func<string, string, string, JsonObject, CancellationToken, Task<SkillRun>> executeSkill.

- [ ] Step 1: Write failing connection tests

Send a session_open payload with one granted skill and assert session_opened
returns the validated skill ID. Send skill_start with skill_id, version, and an
input object and assert the response sequence has step events followed by one
skill_result. Assert missing/unganted skills return skill_not_granted,
malformed input returns schema_invalid, duplicate request IDs with different
inputs return request_conflict, and raw tool output is absent from events.

- [ ] Step 2: Run connection tests and verify skills_disabled failure

Run:
    dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~BrokerHostTests|FullyQualifiedName~AgentEnvelopeTests|FullyQualifiedName~SkillConnectionTests"

Expected: current connection returns skills_disabled or rejects non-empty skill_ids.

- [ ] Step 3: Implement protocol handling

Inject skill list/execute delegates into AgentConnection. In OpenSession, resolve
and validate requested skill IDs before calling BrokerSession.Open. In SkillStart,
validate ownership, request ID, skill ID/version, and bounded JSON input; execute
the skill; emit bounded SkillEvent records and one SkillResult with receipt ID,
state, step count, and safe summary. Store request hashes for idempotent duplicate
handling and clear them on session close.

- [ ] Step 4: Run connection tests and commit

Run the command from Step 2. Expected: all WebSocket skill tests pass.

    git add backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs backend-dotnet/RuAgent.Protocol/AgentEnvelope.cs backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs backend-dotnet/RuAgent.Broker.Tests
    git commit -m "feat: enable SkillStart broker protocol"

---

### Task 6: Reconcile docs and run the skills verification slice

**Files:**
- Modify: docs/operations/agent-tools.md
- Modify: docs/operations/runtime-behavior.md
- Modify: docs/architecture.md
- Modify: README.md
- Modify: backend-dotnet/README.md
- Modify: tests/test_current_documentation.py
- Test: backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs

**Interfaces:**
- Consumes: registered Skills, session grants, broker protocol receipts, and strict package limits.
- Produces: current documentation that no longer claims all skills are disabled and explicitly says extensions/plugins/MCP are absent.

- [ ] Step 1: Write failing documentation assertions

Require current docs to mention declarative SkillStart, skill.execute/session
grant, package executable rejection, bounded steps, and the explicit absence of
extensions/plugins/MCP/subagents/LSP. Require no current document to advertise
arbitrary skill code execution.

- [ ] Step 2: Run documentation assertions and verify stale wording

Run:
    .venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q

Expected: failure on the existing skills-disabled wording or missing limits.

- [ ] Step 3: Update current docs only

Describe the actual production SkillRegistry/Orchestrator path, package roots,
trust classes, approval propagation, and stable SkillEvent/SkillResult metadata.
Leave historical specs unchanged and do not stage unrelated dirty documents.

- [ ] Step 4: Run the skills test slice sequentially

Run:
    dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj
    .venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q

Expected: both commands exit 0.

- [ ] Step 5: Commit the skills docs

    git add docs/operations/agent-tools.md docs/operations/runtime-behavior.md docs/architecture.md README.md backend-dotnet/README.md tests/test_current_documentation.py
    git commit -m "docs: document declarative agent skills"
