# Ru Agent Internal Tool Retirement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permanently remove `system.time`, `system.status`, `app.list`, `notes.create`, and `system.volume`, leaving one canonical 13-tool Ru Agent surface with no compatibility or fallback path.

**Architecture:** Collapse the Protocol catalog to the 13 surviving model tools, then make Broker manifests, executors, leases, policy scopes, skills, Harness probes, tests, and current documentation consume that single inventory. Delete the dedicated internal adapters and native-volume stack; replacement behavior is explicit use of existing terminal or file tools, never an automatic rewrite.

**Tech Stack:** C# 14, .NET 10, xUnit, ASP.NET Core, PowerShell, Python 3, pytest.

## Global Constraints

- Hard-delete `system.time`, `system.status`, `app.list`, `notes.create`, and `system.volume`.
- Do not add aliases, compatibility stubs, translation wrappers, replacement APIs, or fallback providers.
- Retired IDs fail through the existing `unknown_tool` or unavailable-capability path.
- Keep terminal analyzer, timeout, output, working-directory, folder-grant, and approval controls unchanged.
- Do not run a real volume-changing command or mutate the user's live audio devices.
- Preserve every unrelated dirty-worktree change; stage only files owned by the current task.
- Run heavyweight repository gates sequentially.

---

## Planned file structure

The surviving boundaries are:

- `RuAgent.Protocol/AgentToolDefinitionCatalog.cs`: the exact 13-tool canonical inventory and schemas.
- `RuAgent.Protocol/AgentModelToolContractCatalog.cs`: a direct contract projection of that inventory, with no exposure split.
- `RuAgent.Broker/Tools/ToolCatalog.cs`: manifests for the same inventory, with Web entries conditionally enabled.
- `RuAgent.Broker/Tools/ToolExecutionService.cs`: executors only for surviving tools.
- `RuAgent.Protocol/Harness/BrokerPolicyContracts.cs`: authoritative non-Web fallback inventory for private lease recovery.
- `RuAgent.Broker/Runtime/BrokerSession.cs`: tool-to-scope mapping for surviving IDs.
- `RuAgent.Broker/Policy/PolicyContextFactory.cs`: operation classification for surviving IDs.

Delete these production files because each exists only for a retired tool:

- `backend-dotnet/RuAgent.Broker/Tools/Desktop/SystemTools.cs`
- `backend-dotnet/RuAgent.Broker/Tools/Desktop/AppTools.cs`
- `backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs`
- `backend-dotnet/RuAgent.Broker/Tools/Desktop/WindowsCoreAudioVolumeController.cs`
- `backend-dotnet/RuAgent.Broker/Tools/Notes/NotesAdapter.cs`

Delete these dedicated acceptance/test files:

- `backend-dotnet/RuAgent.Broker.Tests/VolumeToolsTests.cs`
- `backend-dotnet/RuAgent.Broker.Tests/WindowsCoreAudioVolumeControllerTests.cs`
- `scripts/native_volume_acceptance.py`
- `tests/test_native_volume_acceptance.py`

Generic Broker, Harness, activity, diagnostic, and privacy tests remain and migrate to surviving tool fixtures.

---

### Task 1: Collapse Protocol to one 13-tool catalog

**Files:**

- Modify: `backend-dotnet/RuAgent.Protocol.Tests/AgentToolDefinitionCatalogTests.cs`
- Modify: `backend-dotnet/RuAgent.Protocol/AgentToolDefinitionCatalog.cs`
- Modify: `backend-dotnet/RuAgent.Protocol/AgentModelToolContractCatalog.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Prompting/AgentToolRuntimeProjection.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Worker/WorkerV2ClientTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Harness/RuPromptContextProviderTests.cs`

**Interfaces:**

- Produces: `AgentToolDefinitionCatalog.Get()` with exactly 13 entries and `Find(id, version)` returning `null` for every retired ID.
- Produces: `AgentModelToolContractCatalog.Get()` as a direct projection of all canonical definitions.
- Removes: `AgentToolExposure`, `AgentToolDefinition.Exposure`, and `AgentToolDefinitionCatalog.Select(...)` because no internal exposure tier remains.

- [ ] **Step 1: Write the failing Protocol inventory tests**

Replace the two expected arrays with one stable inventory and assert hard absence:

```csharp
private static readonly string[] ExpectedOrder =
[
    "files.list", "files.glob", "files.search", "files.read",
    "files.write", "files.edit", "files.delete", "files.restore",
    "terminal.run", "terminal.shell",
    "web.search", "web.fetch", "help.docs",
];

private static readonly string[] RetiredIds =
[
    "system.time", "system.status", "app.list", "notes.create", "system.volume",
];

[Fact]
public void Get_returns_exact_stable_inventory_and_retired_ids_are_absent()
{
    Assert.Equal(ExpectedOrder, AgentToolDefinitionCatalog.Get().Select(item => item.ToolId));
    Assert.Equal(13, AgentToolDefinitionCatalog.Get().Count);
    Assert.All(RetiredIds, id => Assert.Null(
        AgentToolDefinitionCatalog.Find(id, AgentToolDefinitionCatalog.CurrentVersion)));
}
```

Make `Model_contract_catalog_projects_the_entire_canonical_catalog` compare both catalogs directly and assert `Get(retiredId, "1.0.0")` throws `KeyNotFoundException`.

- [ ] **Step 2: Run the Protocol tests and confirm RED**

Run:

```powershell
dotnet test backend-dotnet\RuAgent.Protocol.Tests\RuAgent.Protocol.Tests.csproj --no-restore --filter FullyQualifiedName~AgentToolDefinitionCatalogTests
```

Expected: FAIL because the canonical catalog still returns 18 definitions and finds all five retired IDs.

- [ ] **Step 3: Remove the five definitions and the exposure split**

In `AgentToolDefinitionCatalog.cs`, remove the five entries, `EmptySchema`, `NotesCreateSchema`, and `VolumeSchema`. Remove exposure from `Entry`, `AgentToolDefinition`, validation, and construction so a canonical entry has this shape:

```csharp
private sealed record Entry(
    string Id,
    string Description,
    string Scope,
    Func<JsonObject> Schema,
    Func<JsonObject>? Example = null);
```

Delete `AgentToolExposure` entirely. Construct every remaining entry without an exposure argument.

In `AgentModelToolContractCatalog.cs`, replace the filtered list with:

```csharp
private static readonly IReadOnlyList<AgentToolDefinition> ModelDefinitions =
    AgentToolDefinitionCatalog.Get();

private static AgentToolDefinition? FindDefinition(string toolId, string toolVersion) =>
    AgentToolDefinitionCatalog.Find(toolId, toolVersion);
```

Replace exposure filtering in runtime projections and tests with direct use of `AgentToolDefinitionCatalog.Get()` or `Find(...)`. In `LoopbackModelStepRunner`, retain the unknown ID/version rejection but remove only the exposure predicate.

- [ ] **Step 4: Run affected Protocol and backend projection tests and confirm GREEN**

Run sequentially:

```powershell
dotnet test backend-dotnet\RuAgent.Protocol.Tests\RuAgent.Protocol.Tests.csproj --no-restore
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkerV2ClientTests|FullyQualifiedName~RuPromptContextProviderTests"
```

Expected: both commands PASS; the runtime prompt and Worker projection still expose the same 13 tools.

- [ ] **Step 5: Commit the Protocol collapse**

```powershell
git add -- backend-dotnet/RuAgent.Protocol/AgentToolDefinitionCatalog.cs backend-dotnet/RuAgent.Protocol/AgentModelToolContractCatalog.cs backend-dotnet/RuAgent.Protocol.Tests/AgentToolDefinitionCatalogTests.cs backend-dotnet/RuBackend/Harness/Prompting/AgentToolRuntimeProjection.cs backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs backend-dotnet/RuBackend.Tests/Worker/WorkerV2ClientTests.cs backend-dotnet/RuBackend.Tests/Harness/RuPromptContextProviderTests.cs
git commit -m "refactor(agent): collapse tool catalog to model surface"
```

---

### Task 2: Hard-delete Broker bindings, adapters, and obsolete scope

**Files:**

- Modify: `backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/ToolBindingRegistryTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/ToolAcceptanceMatrixTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/AdapterConformanceTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/PendingApprovalPreviewTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/PendingApprovalStoreTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/AgentEnvelopeTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Runtime/BrokerSession.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/PolicyContextFactory.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/CapabilityProfile.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/ToolScope.cs`
- Modify: `backend-dotnet/RuAgent.Protocol/AgentToolScopes.cs`
- Modify: `backend-dotnet/RuAgent.Protocol/Harness/BrokerPolicyContracts.cs`
- Delete: the five production adapter files and two C# volume-test files listed in Planned file structure.

**Interfaces:**

- Consumes: the 13-entry Protocol catalog from Task 1.
- Produces: a Broker registry containing 11 non-Web bindings by default and 13 when both managed Web tools are enabled.
- Removes: `system.write` from canonical and Broker scope sets because no surviving tool uses it.
- Produces: retired calls rejected before adapter dispatch.

- [ ] **Step 1: Change Broker tests to demand the reduced inventory**

Update catalog, binding, lease, and Host expected IDs to:

```csharp
private static readonly string[] ExpectedCoreIds =
[
    "files.list", "files.glob", "files.search", "files.read",
    "files.write", "files.edit", "files.delete", "files.restore",
    "terminal.run", "terminal.shell", "help.docs",
];
```

Add an explicit rejection theory:

```csharp
[Theory]
[InlineData("system.time")]
[InlineData("system.status")]
[InlineData("app.list")]
[InlineData("notes.create")]
[InlineData("system.volume")]
public void Retired_tool_is_not_bound(string toolId)
{
    var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
    Assert.Throws<KeyNotFoundException>(() =>
        catalog.Registry.Get(toolId, AgentToolDefinitionCatalog.CurrentVersion));
}
```

Remove volume-only and notes-only cases from `AdapterConformanceTests`, `PendingApprovalPreviewTests`, and `PendingApprovalStoreTests`; keep those generic tests using `files.write` or `terminal.shell` fixtures.

- [ ] **Step 2: Run focused Broker tests and confirm RED**

Run:

```powershell
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~ToolCatalogTests|FullyQualifiedName~ToolBindingRegistryTests|FullyQualifiedName~ToolAcceptanceMatrixTests|FullyQualifiedName~BrokerSessionTests|FullyQualifiedName~BrokerHostTests"
```

Expected: FAIL because the Broker still binds and leases the five retired IDs.

- [ ] **Step 3: Remove Broker manifests and executors**

In `ToolCatalog.Manifests`, delete the five retired yields. Remove the `nativePending` branch and register every surviving binding as executable and model-enabled:

```csharp
Registry.Register(new ToolBinding(
    manifest,
    executors[manifest.Id],
    ModelEnabled: true,
    Availability: "executable",
    VerificationRequired: false));
```

In `ToolExecutionService`, remove the retired adapter fields, namespaces, constructor parameter, object construction, executor dictionary entries, `ExecuteVolumeAsync`, `ExecuteNotesCreate`, `ResolveNotes`, note members in `RootToolSet`, and retired `SafeAction` cases. The executor dictionary must contain only the 11 core entries plus conditionally constructed Web entries.

In `BrokerHost`, remove `IVolumeController` constructor/start overloads and call:

```csharp
_tools = new ToolExecutionService(
    session,
    ResolveWorkspaceRoot(),
    options?.BuiltinSkills,
    options?.SkillPackageRoots,
    options?.Web);
```

Adjust `ToolExecutionService`'s constructor order to:

```csharp
public ToolExecutionService(
    BrokerSession session,
    string workspaceRoot,
    IEnumerable<SkillManifest>? builtinSkills = null,
    IEnumerable<string>? skillPackageRoots = null,
    WebOptions? webOptions = null,
    HttpClient? webHttpClient = null)
```

- [ ] **Step 4: Delete dedicated adapters and native-volume tests**

Delete exactly:

```text
backend-dotnet/RuAgent.Broker/Tools/Desktop/SystemTools.cs
backend-dotnet/RuAgent.Broker/Tools/Desktop/AppTools.cs
backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs
backend-dotnet/RuAgent.Broker/Tools/Desktop/WindowsCoreAudioVolumeController.cs
backend-dotnet/RuAgent.Broker/Tools/Notes/NotesAdapter.cs
backend-dotnet/RuAgent.Broker.Tests/VolumeToolsTests.cs
backend-dotnet/RuAgent.Broker.Tests/WindowsCoreAudioVolumeControllerTests.cs
```

Remove volume fake classes and injected-controller setup from `BrokerHostTests`. Do not delete generic Host coverage around capability parsing, session ownership, or unknown-tool failure.

- [ ] **Step 5: Remove obsolete policy and lease mappings**

Change `BrokerPolicyContracts.AuthoritativeFallbackCapabilities()` to return only the 11 core IDs. In `BrokerSession.WorkspaceScopesFor`, keep only these branches:

```csharp
"help.docs" => [ToolScope.SystemRead],
"files.list" or "files.glob" or "files.search" or "files.read"
    => [ToolScope.WorkspaceRead],
"files.write" or "files.edit" or "files.delete" or "files.restore"
    => [ToolScope.WorkspaceWrite],
"terminal.run" or "terminal.shell" => [ToolScope.TerminalExecute],
"web.search" => [ToolScope.WebSearch],
"web.fetch" => [ToolScope.WebFetch],
```

Remove `SystemWrite`/`system.write` from `AgentToolScopes`, `ToolScope.Known`, and Agent/Full `CapabilityProfile` sets. In `PolicyContextFactory`, keep `help.docs`, `web.search`, and `web.fetch` as target-free reads and remove the other retired cases.

- [ ] **Step 6: Run the complete Protocol and Broker projects and confirm GREEN**

Run sequentially:

```powershell
dotnet test backend-dotnet\RuAgent.Protocol.Tests\RuAgent.Protocol.Tests.csproj --no-restore
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --no-restore
```

Expected: both projects PASS; no test constructs a native volume controller or requests a retired binding.

- [ ] **Step 7: Commit the Broker hard deletion**

Review `git diff --name-status` against the exact Task 2 manifest, then stage only those paths and commit:

```powershell
git commit -m "refactor(agent): remove redundant internal tools"
```

---

### Task 3: Migrate skills, Harness probes, and generic contract fixtures

**Files:**

- Modify: `backend-dotnet/RuAgent.Broker.Tests/SkillProductionWiringTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/SkillPackageLoaderTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/SkillConnectionTests.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core.Tests/Conformance/ToolGatewayConformance.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Harness/BrokerToolGatewayFactoryTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Agent/AgentBrokerClientTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Agent/AgentStoreTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Diagnostics/DiagnosticEventTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Diagnostics/DiagnosticEmitterTests.cs`
- Modify: `scripts/harness/session.py`
- Modify: `tests/test_agent_harness.py`
- Modify: `tests/test_harness_session.py`
- Modify: `tests/test_diagnostic_schema.py`
- Modify: `tests/test_privacy_logging.py`
- Delete: `scripts/native_volume_acceptance.py`
- Delete: `tests/test_native_volume_acceptance.py`

**Interfaces:**

- Consumes: `files.list` for read-only declarative skill fixtures.
- Consumes: `files.write` for approval, external-directory, receipt, activity, and diagnostic fixtures.
- Produces: Harness approval proof that writes only inside its fresh temporary workspace.
- Removes: all active acceptance code that assumes native volume control exists.

- [ ] **Step 1: Convert declarative skill fixtures to `files.list`**

Use this manifest shape in production-wiring and connection tests:

```csharp
var skill = new SkillManifest(
    "workspace.list.skill", "1.0.0",
    new JsonObject { ["type"] = "object" },
    new JsonObject { ["type"] = "object" },
    new HashSet<string>(["files.list"]),
    new HashSet<string>([ToolScope.WorkspaceRead, ToolScope.SkillExecute]),
    [new SkillStep(
        SkillStepKind.CallTool,
        "list",
        "files.list",
        new JsonObject { ["path"] = ".", ["max_entries"] = 8 })],
    false, 4, 30_000, "builtin", "Data only.");
```

Change package-loader JSON to the same required tool, scope, step name, and bounded arguments. The skill behavior under test remains registration, grant enforcement, idempotency, and declarative loading—not clock access.

- [ ] **Step 2: Convert approval and external-directory fixtures to `files.write`**

In `scripts/harness/session.py`, call:

```python
summary = await probe_approval_lifecycle(
    base_url=self.broker_base_url,
    token=self._broker_token,
    tool_id="files.write",
    tool_version="1.0.0",
    arguments={
        "path": "harness/approval.txt",
        "content": "bounded deterministic fixture",
        "overwrite": False,
    },
)
```

In `BrokerToolGatewayFactoryTests`, replace the `notes.create` proposal/resume with `files.write` arguments containing an absolute temporary `notePath`, `content`, and `overwrite: false`. Preserve assertions that the first evaluation asks, directory approval is session-scoped, the resumed evaluation allows, execution succeeds, and the file contains the exact content.

- [ ] **Step 3: Convert Broker-client and generic event fixtures**

Use one executable model capability in `AgentBrokerClientTests`:

```csharp
private static JsonArray DefaultTools() =>
[
    new JsonObject
    {
        ["tool_id"] = "files.write",
        ["tool_version"] = "1.0.0",
        ["scopes"] = new JsonArray("workspace.write"),
        ["risk"] = "reversible",
        ["availability"] = "executable",
        ["model_enabled"] = true,
    },
];
```

Use `files.write`, capability class `workspace_mutation`, and a harmless fixture path/content for exact binding, missing-version, missing-scope, and malformed-receipt tests. Generic diagnostics, privacy, and activity tests should also replace the literal `system.volume` with `files.write` while retaining their original redaction, serialization, status-order, and dedup assertions.

Replace generic grant fixtures using the retired `system.write` scope in
`AgentEnvelopeTests` and `AgentStoreTests` with `workspace.write`; preserve the
same round-trip, ordering, and persistence assertions.

- [ ] **Step 4: Delete native-volume acceptance and remove source-contract assertions**

Delete `scripts/native_volume_acceptance.py` and `tests/test_native_volume_acceptance.py`. In `tests/test_agent_harness.py`, remove assertions that `VolumeTools.cs`, `notes.create`, or a volume manifest exists; replace the inventory assertion with checks for the exact surviving core IDs and absence of the five retired IDs.

- [ ] **Step 5: Run focused managed and Python tests**

Run sequentially:

```powershell
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~SkillProductionWiringTests|FullyQualifiedName~SkillPackageLoaderTests|FullyQualifiedName~SkillConnectionTests"
dotnet test backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj --no-restore --filter FullyQualifiedName~ToolGatewayConformance
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --filter "FullyQualifiedName~BrokerToolGatewayFactoryTests|FullyQualifiedName~AgentBrokerClientTests|FullyQualifiedName~AgentStoreTests|FullyQualifiedName~DiagnosticEventTests|FullyQualifiedName~DiagnosticEmitterTests"
.venv\Scripts\python.exe -m pytest tests\test_agent_harness.py tests\test_harness_session.py tests\test_diagnostic_schema.py tests\test_privacy_logging.py -q
```

Expected: every command PASS; the approval probe remains deterministic and no native audio endpoint opens.

- [ ] **Step 6: Commit fixture migration and acceptance retirement**

Stage only Task 3 paths, confirm both deleted files appear as `D`, and commit:

```powershell
git commit -m "test(agent): retire internal tool fixtures"
```

---

### Task 4: Reconcile current documentation and retire active volume instructions

**Files:**

- Modify: `README.md`
- Modify: `CONTEXT.md`
- Modify: `TODO.md`
- Modify: `docs/architecture.md`
- Modify: `docs/adr/0007-mode-capability-profiles.md`
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `docs/operations/verification.md`
- Modify: `backend-dotnet/README.md`
- Modify: `backend-dotnet/RuAgent.Cli/README.md`
- Modify: `docs/superpowers/specs/2026-09-05-ru-agent-tool-hardening-design.md`
- Modify: `docs/superpowers/plans/2026-09-05-ru-agent-tool-hardening.md`

**Interfaces:**

- Produces: one current-state description of exactly 13 tools.
- Produces: replacement guidance—terminal for machine/app/time/volume operations and `files.write` for notes.
- Preserves: historical implementation detail, explicitly labeled as superseded rather than rewritten.

- [ ] **Step 1: Update current capability documentation**

Change the current matrix to state:

```markdown
The canonical Protocol and model catalogs contain the same 13 tools: eight
`files.*` tools, two terminal tools, two managed Web tools, and `help.docs`.
There are no Broker-internal or deterministic-host tool definitions.
```

Remove the deterministic-volume execution diagram and native acceptance commands. State that time, host status, application discovery, and optional volume commands go through normal terminal analysis/policy; notes use `files.write`. Make clear that Ru does not automatically translate retired IDs to terminal commands.

- [ ] **Step 2: Update roadmap and supersession markers**

Set `TODO.md`'s review date to `2026-09-05` and add a completed maintenance item recording the 13-tool collapse and removal of native-volume acceptance. Correct the older completed item that says the Protocol catalog contains 18 definitions.

Remove the retired deterministic-volume claim from superseded ADR 0007 and
add this notice below the title of the same-day tool-hardening spec and plan:

```markdown
> Superseded on 2026-09-05 by the hard-retirement design and plan for the five
> former internal/deterministic tools. The verification record below remains
> historical evidence for its checkpoint.
```

Do not rewrite older dated plans/specs/ADRs as current implementation. Delete a document only if this audit finds it is linked as current instructions, contains no unique historical decision, and is wholly replaced by the retirement spec.

- [ ] **Step 3: Run documentation/source truth checks**

Run:

```powershell
rg -n "system\.time|system\.status|system\.volume|app\.list|notes\.create|18 tool definitions|18 definitions" README.md CONTEXT.md TODO.md docs/README.md docs/architecture.md docs/operations docs/adr/0007-mode-capability-profiles.md backend-dotnet/README.md backend-dotnet/RuAgent.Cli/README.md
rg -n --glob "*.cs" --glob "*.py" --glob "!docs/**" --glob "!**/bin/**" --glob "!**/obj/**" "system\.time|system\.status|system\.volume|app\.list|notes\.create|VolumeTools|WindowsCoreAudioVolumeController|NotesAdapter|SystemTools|AppTools" backend-dotnet scripts tests
```

Expected: both commands return no matches. Dated plans, specs, and ADRs are intentionally outside these current-state checks.

- [ ] **Step 4: Run documentation tests and commit**

Run:

```powershell
.venv\Scripts\python.exe -m pytest tests\test_current_documentation.py tests\test_agent_harness.py -q
```

Expected: both documentation test files PASS. Stage only Task 4 documents/tests and commit:

```powershell
git commit -m "docs: document Ru Agent tool retirement"
```

---

### Task 5: Full verification and closure

**Files:**

- Modify: `docs/superpowers/plans/2026-09-05-ru-agent-internal-tool-retirement.md` (checkboxes and measured results only)

**Interfaces:**

- Consumes: all implementation tasks.
- Produces: reproducible completion evidence without modifying live devices or unrelated working files.

- [ ] **Step 1: Verify no retired production surface remains**

Run the two zero-match searches from Task 4 again, then verify deleted paths:

```powershell
git ls-files --error-unmatch backend-dotnet/RuAgent.Broker/Tools/Desktop/SystemTools.cs backend-dotnet/RuAgent.Broker/Tools/Desktop/AppTools.cs backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs backend-dotnet/RuAgent.Broker/Tools/Desktop/WindowsCoreAudioVolumeController.cs backend-dotnet/RuAgent.Broker/Tools/Notes/NotesAdapter.cs scripts/native_volume_acceptance.py tests/test_native_volume_acceptance.py
```

Expected: the search commands have no output; `git ls-files --error-unmatch` exits nonzero because every listed path is gone from the index.

- [ ] **Step 2: Run all .NET test projects sequentially**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test_dotnet.ps1 -NoRestore
```

Expected: all recursively discovered .NET test projects PASS with zero failures.

- [ ] **Step 3: Run the complete Python suite**

Run:

```powershell
.venv\Scripts\python.exe -m pytest -q
```

Expected: PASS with zero failures and no collection reference to `test_native_volume_acceptance.py`.

- [ ] **Step 4: Verify routes and build distributable GUI/backend artifacts**

Run sequentially:

```powershell
.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
cmd /c build.bat
```

Expected: route matrix PASS with the repository's current measured HTTP/WebSocket counts; `build.bat` completes every managed and native gate and produces the GUI package without opening it.

- [ ] **Step 5: Check owned diff hygiene and unrelated-change preservation**

Run:

```powershell
git diff --check -- backend-dotnet/RuAgent.Protocol backend-dotnet/RuAgent.Protocol.Tests backend-dotnet/RuAgent.Broker backend-dotnet/RuAgent.Broker.Tests backend-dotnet/RuAgent.Harness.Core.Tests backend-dotnet/RuBackend backend-dotnet/RuBackend.Tests scripts/harness tests README.md CONTEXT.md TODO.md docs/README.md docs/architecture.md docs/operations docs/adr/0007-mode-capability-profiles.md backend-dotnet/README.md backend-dotnet/RuAgent.Cli/README.md docs/superpowers/specs/2026-09-05-ru-agent-tool-hardening-design.md docs/superpowers/plans/2026-09-05-ru-agent-tool-hardening.md
git status --short --branch
```

Expected: no whitespace errors in owned changes. Pre-existing unrelated dirty paths remain unstaged and unmodified by this task.

- [ ] **Step 6: Record measured evidence and commit plan closure**

Mark completed checkboxes and append exact test totals, route counts, build totals, and any explicitly unrun live acceptance to this plan. Then commit only the plan update:

```powershell
git add -- docs/superpowers/plans/2026-09-05-ru-agent-internal-tool-retirement.md
git commit -m "docs: close Ru Agent internal tool retirement"
```

Do not push unless the user asks for a push after reviewing the completed local commits.
