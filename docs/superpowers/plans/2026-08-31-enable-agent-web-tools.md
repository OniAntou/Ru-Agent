# Enable Agent Web Tools Implementation Plan

> **Status:** Historical implementation record. The obsolete online-search
> setting was retired and default managed `web.search`/`web.fetch` shipped on
> 2026-08-31; current behavior is maintained in the operations docs.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Remove the obsolete online-search setting and make the active managed Agent GUI path execute bounded DuckDuckGo-backed `web.search` and `web.fetch` calls.

**Architecture:** Keep the existing typed Worker v2 model-selection contract and Harness lifecycle. Add a focused `AgentWebToolExecutor` in the active Host path that reuses the Broker's already-tested `WebOptions`, `WebPolicy`, `WebSearchAdapter`, `WebFetchAdapter`, and `WebContentSanitizer`; the existing `HarnessToolGateway` remains the evaluate/prepare/execute authority. The host advertises web capabilities from this enabled executor, while settings no longer participate in web authorization.

**Tech Stack:** C#/.NET 10, ASP.NET Core, WinUI 3, xUnit, `System.Text.Json.Nodes`, `HttpClient`, existing `RuAgent.Protocol`, `RuAgent.Broker`, `RuAgent.Harness.Core`, and `RuBackend`.

## Global Constraints

- The default search source is `https://html.duckduckgo.com/html/`.
- Web tools are read-only external operations and do not grant workspace, terminal, connector, or application-launch permissions.
- Automatic redirects stay disabled; every followed hop is validated by `WebPolicy`.
- Private, loopback, link-local, multicast, reserved, and test-net targets are rejected.
- Web response bytes, UTF-8/content types, sanitized text, and citation counts remain bounded; fetched text retains the `[untrusted_web_reference]` marker.
- No credentials enter settings, prompts, capabilities, events, receipts, or model context.
- Legacy `allow_web_search` and `allow_online_search` inputs are ignored and stripped when settings are read or saved; they never control capability exposure.
- Preserve the unrelated dirty files `gui-winui3/Views/MemoryView.xaml`, the pre-existing portions of `gui-winui3/Views/SettingsView.xaml`, and `artifacts/`.
- Do not modify historical plans/design records; update only current documentation and documentation tests that describe the shipped behavior.
- Run heavyweight C# projects sequentially and use `.venv\Scripts\python.exe -m pytest` for Python checks.

---

## File map

### Settings retirement and protocol cleanup

- Modify `gui-winui3/Views/SettingsView.xaml` to remove only the online-search row.
- Modify `gui-winui3/Views/SettingsView.xaml.cs` to remove its toggle event, load, and save mapping.
- Modify `gui-winui3/Models/Models.cs` to remove `AppSettingsDto.AllowOnlineSearch`.
- Modify `backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs` to remove `AllowWebSearch`.
- Modify `backend-dotnet/RuBackend/Business/Settings/SettingsStore.cs` to strip legacy search fields without reading or persisting them.
- Modify `backend-dotnet/RuBackend/Business/Settings/SettingsTransitionService.cs` to stop mapping legacy search fields.
- Modify `backend-dotnet/RuBackend/Business/BusinessEndpoints.cs` to stop returning search-setting fields.
- Modify `backend-dotnet/RuAgent.Protocol/Harness/WorkerV2Contracts.cs` and `backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs` to remove the legacy Worker boolean.
- Update `backend-dotnet/RuBackend.Tests/Business/SettingsStoreTests.cs`, `BusinessEndpointTests.cs`, `Harness/RuPromptContextProviderTests.cs`, `Harness/AgentReplyPolicyTests.cs`, `Worker/WorkerV2ClientTests.cs`, and `Worker/WorkerV2ContractTests.cs` for the shorter settings snapshot and strict wire contract.

### Active web execution path

- Add a project reference from `backend-dotnet/RuBackend/RuBackend.csproj` to `backend-dotnet/RuAgent.Broker/RuAgent.Broker.csproj` so the active Host can reuse the existing Broker web implementation.
- Create `backend-dotnet/RuBackend/Harness/Broker/AgentWebToolExecutor.cs` as the focused Host adapter around the Broker web classes.
- Modify `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs` to recognize, authorize, prepare, execute, cancel, and receipt-wrap `web.search`/`web.fetch`.
- Modify `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs` to construct/pass the enabled web executor and advertise both web IDs.
- Modify `backend-dotnet/RuBackend/Harness/Prompting/RuPromptContextProvider.cs` to expose both web capabilities from runtime wiring, not settings.
- Modify `backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs` to include both web bindings in `model_tools`.
- Add `backend-dotnet/RuBackend.Tests/Harness/AgentWebToolExecutorTests.cs` and extend the prompt/gateway/Worker contract tests with web assertions.

### Current documentation

- Modify `README.md`, `docs/architecture.md`, `docs/operations/runtime-behavior.md`, `docs/operations/agent-tools.md`, and `tests/test_current_documentation.py` to describe the active default web tools and remove the stale “WebOptions disabled by default / explicit deployment required” claim.

---

### Task 1: Remove the obsolete search setting

**Files:**
- Modify: `gui-winui3/Views/SettingsView.xaml`
- Modify: `gui-winui3/Views/SettingsView.xaml.cs`
- Modify: `gui-winui3/Models/Models.cs`
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs`
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsStore.cs`
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsTransitionService.cs`
- Modify: `backend-dotnet/RuBackend/Business/BusinessEndpoints.cs`
- Test: `backend-dotnet/RuBackend.Tests/Business/SettingsStoreTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/AgentReplyPolicyTests.cs`

**Interfaces:**
- Consumes: existing settings persistence and `AppSettingsDto` API shape.
- Produces: a settings snapshot/API that has no active online-search field, while old JSON keys are removed from the canonical raw settings object.

- [x] **Step 1: Write failing settings-retirement tests**

Add assertions that reading a JSON file containing both legacy keys removes
`allow_web_search` and `allow_online_search` from `SettingsSnapshot.Raw`, that
saving a snapshot never writes either key, and that the business settings
response contains neither key. Keep the existing assertions for self-learning,
cloud-memory consent, and model credentials.

```csharp
var snapshot = await store.ReadAsync(CancellationToken.None);
Assert.False(snapshot.Raw.ContainsKey("allow_web_search"));
Assert.False(snapshot.Raw.ContainsKey("allow_online_search"));

await store.SaveAsync(snapshot, CancellationToken.None);
var persisted = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
Assert.False(persisted.ContainsKey("allow_web_search"));
Assert.False(persisted.ContainsKey("allow_online_search"));
```

- [x] **Step 2: Run the focused tests and verify the old contract fails**

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~BusinessEndpointTests|FullyQualifiedName~AgentReplyPolicyTests"
```

Expected: the new assertions fail or the build reports the old
`AllowWebSearch`/`AllowOnlineSearch` members still shape the contract.

- [x] **Step 3: Remove the field without changing neighboring settings**

Remove the property/constructor slot and all active reads/writes. In
`SettingsStore`, add the two legacy JSON names to the retired-field list so
they are removed on read and save. Do not reject old clients: the transition
service may accept the overall payload but must ignore both search keys. Remove
both fields from the business response. Remove the XAML row and only its
event/load/save references; leave the self-learning/cloud-memory rows and the
user's unrelated XAML redesign intact.

- [x] **Step 4: Run the focused tests and commit**

Run the command from Step 2. Expected: all focused settings tests pass.

```powershell
git add -- gui-winui3/Views/SettingsView.xaml gui-winui3/Views/SettingsView.xaml.cs gui-winui3/Models/Models.cs backend-dotnet/RuBackend/Business/Settings backend-dotnet/RuBackend/Business/BusinessEndpoints.cs backend-dotnet/RuBackend.Tests/Business backend-dotnet/RuBackend.Tests/Harness/AgentReplyPolicyTests.cs
git commit -m "refactor: remove online search setting"
```

---

### Task 2: Remove the Worker v2 legacy boolean

**Files:**
- Modify: `backend-dotnet/RuAgent.Protocol/Harness/WorkerV2Contracts.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs`
- Test: `backend-dotnet/RuBackend.Tests/Worker/WorkerV2ContractTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Worker/WorkerV2ClientTests.cs`

**Interfaces:**
- Consumes: `SystemPromptContext.Capabilities` and `model_tools`.
- Produces: Worker v2 start settings containing only `model_tools` among the
  host-owned selection settings; web availability is represented by tool
  bindings, not a boolean.

- [x] **Step 1: Write failing wire-contract tests**

Assert the loopback start payload contains `model_tools` entries for
`web.search` and `web.fetch` when the prompt context advertises them, and does
not contain `allow_web_search`. Assert `WorkerV2Contracts.ValidateEnvelope`
rejects an unknown legacy setting key.

```csharp
var settings = CaptureStartSettings(contextWithWebCapabilities);
Assert.DoesNotContain("allow_web_search", settings.Select(pair => pair.Key));
Assert.Contains(settings["model_tools"]!.AsArray(), node => node!["tool_id"]!.GetValue<string>() == "web.search");
Assert.Contains(settings["model_tools"]!.AsArray(), node => node!["tool_id"]!.GetValue<string>() == "web.fetch");
```

- [x] **Step 2: Run the focused Worker tests and verify failure**

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~WorkerV2ContractTests|FullyQualifiedName~WorkerV2ClientTests"
```

Expected: the new assertions fail because the old boolean is still emitted or
the current runtime tool list does not contain web bindings.

- [x] **Step 3: Remove the boolean from the protocol allow-list and payload**

Change `SettingFields` to contain `model_tools` only, delete its boolean
validation, and make `LoopbackModelStepRunner.BuildSettings` serialize the
runtime tool list without `allow_web_search`. Keep schema/version validation
for every model tool unchanged.

- [x] **Step 4: Run the focused Worker tests and commit**

Run the command from Step 2. Expected: all focused Worker tests pass.

```powershell
git add -- backend-dotnet/RuAgent.Protocol/Harness/WorkerV2Contracts.cs backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs backend-dotnet/RuBackend.Tests/Worker/WorkerV2ContractTests.cs backend-dotnet/RuBackend.Tests/Worker/WorkerV2ClientTests.cs
git commit -m "refactor: remove legacy web search wire flag"
```

---

### Task 3: Add the focused Host web executor

**Files:**
- Modify: `backend-dotnet/RuBackend/RuBackend.csproj`
- Create: `backend-dotnet/RuBackend/Harness/Broker/AgentWebToolExecutor.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/AgentWebToolExecutorTests.cs`

**Interfaces:**
- Consumes: `RuAgent.Broker.Configuration.WebOptions`,
  `WebSearchAdapter`, `WebFetchAdapter`, `WebPolicy`, and
  `WebContentSanitizer`.
- Produces: `AgentWebToolExecutor(WebOptions options, HttpClient httpClient)`
  and `Task<JsonObject> ExecuteAsync(string toolId, JsonObject arguments,
  CancellationToken cancellationToken)`.

- [x] **Step 1: Write failing executor tests**

Use a fake `HttpMessageHandler` and an enabled `WebOptions` endpoint pointed at
a public-looking test host. Assert search returns no more than ten citations,
fetch includes `untrusted = true` and the untrusted marker, scripts are absent,
and invalid tool IDs/arguments are rejected before the handler is called.

```csharp
var output = await executor.ExecuteAsync(
    "web.search",
    new JsonObject { ["query"] = "ru" },
    CancellationToken.None);

Assert.True(output["citations"]!.AsArray().Count <= 10);
Assert.True(output["untrusted"]!.GetValue<bool>());
Assert.Equal(1, handler.RequestCount);
```

- [x] **Step 2: Run the new executor test and verify the missing type**

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo --filter FullyQualifiedName~AgentWebToolExecutorTests
```

Expected: compile failure because the Host executor and project reference do
not exist.

- [x] **Step 3: Implement the adapter with one default runtime configuration**

Add the project reference and implement a small dispatcher that owns one
`WebOptions` instance and the two existing adapters. The production factory
will pass:

```csharp
new WebOptions(
    enabled: true,
    searchEndpoint: new Uri("https://html.duckduckgo.com/html/"),
    requireApproval: false)
```

`ExecuteAsync` accepts only `web.search` and `web.fetch`, clones bounded JSON
arguments, delegates to the existing adapters, and returns the same output
shape as the Broker service: `{untrusted:true, citations:[...]}` for search
and `{untrusted:true, source:{...}, content, content_type}` for fetch. It does
not serialize exceptions or request headers.

- [x] **Step 4: Run the executor tests and commit**

Run the command from Step 2. Expected: all executor tests pass, including
private/redirect/size/sanitizer cases delegated to the existing adapter tests.

```powershell
git add -- backend-dotnet/RuBackend/RuBackend.csproj backend-dotnet/RuBackend/Harness/Broker/AgentWebToolExecutor.cs backend-dotnet/RuBackend.Tests/Harness/AgentWebToolExecutorTests.cs
git commit -m "feat: add managed agent web executor"
```

---

### Task 4: Wire web tools into the active Harness gateway

**Files:**
- Modify: `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/AgentWebToolExecutorTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/HarnessReadOnlyTracerTests.cs`

**Interfaces:**
- Consumes: `AgentWebToolExecutor.ExecuteAsync`, existing
  `ToolProposal`/`ApprovedToolExecution`/`PreparedToolExecution` lifecycle.
- Produces: normal read-only receipts for `web.search` and `web.fetch` after
  evaluate → prepare → execute, with no workspace mutation side effect.

- [x] **Step 1: Write failing gateway lifecycle tests**

Create a public `ToolProposal` for each web tool, evaluate it with both
Standard and Full presets, prepare the allowed execution, and execute it with a
fake HTTP client. Assert `Decision == Allow`, `AccessKind == ReadOnly`, the
receipt is `Succeeded`, `SideEffectsPossible == false`, and the fake handler
was called. Assert a web tool is denied as `phase_not_available` when it is not
in the gateway's advertised capability set.

```csharp
var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);
Assert.Equal(PolicyDecision.Allow, decision.Decision);
Assert.Equal(ToolAccessKind.ReadOnly, decision.AccessKind);
var prepared = await gateway.PrepareAsync(
    new ApprovedToolExecution(proposal, executionId, decision.CanonicalIntentToken!),
    CancellationToken.None);
var receipt = await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, CancellationToken.None);
Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
Assert.False(receipt.SideEffectsPossible);
```

- [x] **Step 2: Run the gateway test and verify it fails**

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentWebToolExecutorTests|FullyQualifiedName~HarnessReadOnlyTracerTests"
```

Expected: web evaluation returns `phase_not_available` or execution falls
through to the file-only branch.

- [x] **Step 3: Add web IDs, validation, and receipt dispatch**

In `HarnessToolGateway`:

1. accept `web.search`/`web.fetch` in the known-ID set;
2. map both to `ToolAccessKind.ReadOnly` and a non-filesystem operation class;
3. allow them in `EvaluateNonTerminal` only when the executor is present;
4. validate exactly the bounded `query` (max 512 chars) or absolute HTTP(S)
   `url` (max 2048 chars), rejecting extra properties before network access;
5. let generic preparation create a read-only prepared execution;
6. dispatch web IDs before terminal/file branches;
7. publish only the bounded JSON output in 16 KiB chunks; and
8. map cancellation, `UnauthorizedAccessException`, `InvalidDataException`,
   `ArgumentException`, `IOException`, and unknown adapter failures to the
   existing bounded receipt codes without raw exception text.

The canonical token remains the existing tool/version/argument hash, so web
calls retain the same stale/forged-preparation protection as other tools.

- [x] **Step 4: Run gateway tests and commit**

Run the command from Step 2. Expected: web lifecycle tests pass and existing
filesystem/terminal read-only tests remain green.

```powershell
git add -- backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs backend-dotnet/RuBackend.Tests/Harness/AgentWebToolExecutorTests.cs backend-dotnet/RuBackend.Tests/Harness/HarnessReadOnlyTracerTests.cs
git commit -m "feat: execute web tools through agent gateway"
```

---

### Task 5: Advertise web capabilities end-to-end

**Files:**
- Modify: `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Prompting/RuPromptContextProvider.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs`
- Modify: `backend-dotnet/RuBackend/Program.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/RuPromptContextProviderTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Worker/WorkerV2ContractTests.cs`

**Interfaces:**
- Consumes: enabled `AgentWebToolExecutor` and the existing static core
  capability list.
- Produces: prompt capabilities and Worker `model_tools` containing
  `web.search@1.0.0` (`network.search`) and `web.fetch@1.0.0`
  (`network.fetch`) on the active managed Agent path.

- [x] **Step 1: Write failing capability tests**

Extend the prompt provider test to build with `webEnabled: true` and assert
both web IDs are present; build with `webEnabled: false` and assert neither is
present. Assert the capability list no longer depends on a settings field.
Extend the runtime capability test to require both IDs.

```csharp
Assert.Contains(context.Capabilities, item => item.Id == "web.search" && item.PolicySummary == "network.search");
Assert.Contains(context.Capabilities, item => item.Id == "web.fetch" && item.PolicySummary == "network.fetch");
```

- [x] **Step 2: Run the focused capability tests and verify failure**

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~RuPromptContextProviderTests|FullyQualifiedName~WorkerV2ContractTests"
```

Expected: the web capabilities are absent or still depend on the deleted
settings field.

- [x] **Step 3: Wire the enabled executor and both capability IDs**

Register one `AgentWebToolExecutor` in `Program.cs` with the DuckDuckGo
`WebOptions`, inject it into `AgentRuntimeCoordinatorFactory`, and pass it to
each newly-created `HarnessToolGateway`. Add `web.search` and `web.fetch` to
`RuntimeCapabilities`. Replace the prompt provider's old
`settings.AllowWebSearch && webSearchAuthenticated` branch with a single
`webEnabled` runtime check and add the two scope mappings. Add both bindings to
the loopback Worker tool list. Do not add a settings/API switch.

- [x] **Step 4: Run focused capability tests and commit**

Run the command from Step 2. Expected: prompt, runtime, and Worker contract
tests pass.

```powershell
git add -- backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs backend-dotnet/RuBackend/Harness/Prompting/RuPromptContextProvider.cs backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs backend-dotnet/RuBackend.Tests/Harness/RuPromptContextProviderTests.cs backend-dotnet/RuBackend.Tests/Worker/WorkerV2ContractTests.cs
git commit -m "feat: advertise web tools to managed agent"
```

---

### Task 6: Update current documentation and documentation tests

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `docs/operations/agent-tools.md`
- Modify: `tests/test_current_documentation.py`

**Interfaces:**
- Consumes: the verified default runtime/capability behavior from Tasks 3–5.
- Produces: current docs that accurately say DuckDuckGo-backed `web.search`
  and `web.fetch` are active in the managed Agent path.

- [x] **Step 1: Write failing documentation assertions**

Require current docs to mention the DuckDuckGo endpoint, both tool IDs, the
untrusted marker, and the absence of `allow_online_search` as a product
setting. Require them not to claim that managed startup leaves WebOptions
disabled by default or that an explicit caller configuration is required for
the standard GUI path.

- [x] **Step 2: Run the documentation test and verify the stale claim**

Run:

```powershell
.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q
```

Expected: the new assertions fail against the current “optional/disabled by
default” wording.

- [x] **Step 3: Update only current operational docs**

Document the fixed default endpoint, read-only policy, bounded/sanitized
output, and the distinction between `files.search` and `web.search`. Remove
the stale disabled-by-default claims. Keep historical plans and draft design
records unchanged.

- [x] **Step 4: Run the documentation test and commit**

Run the command from Step 2. Expected: all current documentation assertions
pass.

```powershell
git add -- README.md docs/architecture.md docs/operations/runtime-behavior.md docs/operations/agent-tools.md tests/test_current_documentation.py
git commit -m "docs: describe enabled agent web tools"
```

---

### Task 7: Full verification and release handoff

**Files:**
- Verify all changed files from Tasks 1–6.
- Review current documents for any remaining stale search-setting/default claims.

**Interfaces:**
- Consumes: all committed implementation slices and current dirty-worktree protections.
- Produces: verified GUI/backend release artifacts and a status report that separates deterministic tests from live network behavior.

- [x] **Step 1: Run focused regression slices sequentially**

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentWebToolExecutorTests|FullyQualifiedName~RuPromptContextProviderTests|FullyQualifiedName~WorkerV2ContractTests|FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~BusinessEndpointTests"
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~WebAdapterTests|FullyQualifiedName~WebExecutorTests|FullyQualifiedName~WebCapabilityTests|FullyQualifiedName~ToolCatalogTests"
```

Expected: all new wiring and existing Broker web safety tests pass.

- [x] **Step 2: Run the full relevant C# projects sequentially**

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj -c Release --no-restore --nologo
```

Expected: all tests pass without parallel process pressure.

- [x] **Step 3: Run docs, diff, and build checks**

Run:

```powershell
.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q
git diff --check
.\build.bat
```

Expected: documentation tests pass, no whitespace errors are reported, and
the release build reaches its normal completion marker.

- [x] **Step 4: Review docs and dirty state before handoff**

Run:

```powershell
rg -n --glob '!**/bin/**' --glob '!**/obj/**' "AllowOnlineSearch|AllowWebSearch|allow_online_search|allow_web_search|WebOptions disabled by default|WebOptions.*disabled" gui-winui3 backend-dotnet README.md docs tests
git status --short
```

Expected: no active UI/backend/runtime references remain; any historical
document matches are identified as historical or updated; unrelated dirty
files remain preserved. Report that deterministic fake-handler tests prove
the execution path, while real DuckDuckGo availability remains an external
network/service condition.
