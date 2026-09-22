# Model + Permission + Memory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Trien khai spec 2026-09-22: Models.dev catalog, ladder plan → standard → full_access(yolo), setting compaction.

**Architecture:** Spec chia 3 part doc lap: Model (catalog + settings), Permission (preset enum + evaluator), Memory (2 setting + policy). Moi task co test cycle rieng, commit rieng.

**Tech Stack:** .NET 10, C#, xUnit, WinUI3, CLI TUI.

## Global Constraints

- Windows 10/11 64-bit, .NET SDK 10.0.
- Fail-closed moi noi: offline giu cache cu, unknown model khong probe, approval het han fail.
- Khong them subagent, MCP, hooks, plugin trong dot nay.
- Conventional Commits voi scope chi tiet, chi commit/push len nhanh dev.

---

### Task 1: Memory — setting compaction threshold + target

**Files:**
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs:1-16`
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsStore.cs:84-126`
- Modify: `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs:52-59`
- Test: `backend-dotnet/RuBackend.Tests/Business/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `ContextBudgetPolicyOptions(NormalTriggerRatio, NormalTargetRatio)` da co san.
- Produces: `SettingsSnapshot.CompactionTriggerRatio`, `SettingsSnapshot.CompactionTargetRatio` (double, default 0.80/0.62). Ham doc `ContextBudgetPolicyOptions FromSettings(SettingsSnapshot)`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Settings_round_trips_compaction_ratios_with_defaults()
{
    var snapshot = new SettingsSnapshot(
        new JsonObject(), "OpenCode Zen", "model",
        "oc-model", "kilo-model", "default", false,
        "custom-model", "http://localhost:11434",
        "gpt-model", "claude-model",
        0.75, 0.55);
    Assert.Equal(0.75, snapshot.CompactionTriggerRatio);
    Assert.Equal(0.55, snapshot.CompactionTargetRatio);
}

[Fact]
public void Default_snapshot_uses_80_62_ratios()
{
    var snapshot = SettingsSnapshot.Default();
    Assert.Equal(0.80, snapshot.CompactionTriggerRatio);
    Assert.Equal(0.62, snapshot.CompactionTargetRatio);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~Settings_round_trips_compaction" -v q`
Expected: FAIL with "does not contain a definition for CompactionTriggerRatio"

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record SettingsSnapshot(
    JsonObject Raw,
    string Backend,
    string Model,
    string OpenCodeModel,
    string KiloModel,
    string ApiVariant,
    bool KiloReasoningEnabled,
    string CustomModel,
    string CustomUrl,
    string OpenAiModel,
    string AnthropicModel,
    double CompactionTriggerRatio = 0.80,
    double CompactionTargetRatio = 0.62);
```

Them validate trong `SettingsStore.Validate`: `0 < target < trigger < 1`, sai thi fail-closed ve default. `SaveAsync` persist 2 field. `AgentRunCoordinator` doc ratio tu settings de dung `ContextBudgetPolicyOptions(trigger, target)`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~Settings" -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs backend-dotnet/RuBackend/Business/Settings/SettingsStore.cs backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs backend-dotnet/RuBackend.Tests/Business/SettingsStoreTests.cs
git commit -m "feat(memory): setting compaction trigger va target ratio"
```

### Task 2: Permission — ladder plan → standard → full_access, bo trusted

**Files:**
- Modify: `backend-dotnet/RuAgent.Harness.Core/AgentPermissionPreset.cs:1-8`
- Modify: `backend-dotnet/RuAgent.Protocol/Harness/BrokerPolicyContracts.cs:11-13`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/PolicySnapshotEvaluator.cs:100-128`
- Modify: `backend-dotnet/RuBackend/Harness/AgentV1Service.Policies.cs:417-467`
- Test: `backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs`

**Interfaces:**
- Consumes: `AgentPermissionPreset` enum moi tu Task nay.
- Produces: preset wire `plan`, `standard`, `full_access`. Ham `IsYoloAllowableAsk` cho yolo.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Plan_preset_denies_all_mutations_without_auto()
{
    var snapshot = PolicyEvalSnapshots.SnapshotWithPreset("plan");
    var (lease, factory) = Bind(snapshot);
    var evaluator = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry());
    var verdict = evaluator.Evaluate(
        lease.Snapshot,
        FileCall(factory, "files.write", "src/a.txt"),
        PolicyRequestInvariants.Valid,
        Now);
    Assert.Equal("ask", verdict.Decision);
}

[Fact]
public void Yolo_preset_allows_terminal_without_asking()
{
    var snapshot = PolicyEvalSnapshots.SnapshotWithPreset("full_access");
    var (lease, factory) = Bind(snapshot);
    var evaluator = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry());
    var verdict = evaluator.Evaluate(
        lease.Snapshot,
        Terminal(factory, "git", "status"),
        PolicyRequestInvariants.Valid,
        Now);
    Assert.Equal("allow", verdict.Decision);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~Plan_preset" -v q`
Expected: FAIL with "SnapshotWithPreset not defined" hoac preset invalid

- [ ] **Step 3: Write minimal implementation**

```csharp
public enum AgentPermissionPreset
{
    Plan,
    Standard,
    FullAccess,
}
```

`BrokerPolicyContracts`: them `PlanPresetWireValue = "plan"`, xoa `TrustedPresetWireValue`, `CanonicalizePreset` nhan plan/standard/full_access. `PolicySnapshotEvaluator.Evaluate`: plan thi moi mutation ask (khong auto); full_access thi allow het tru deny cung (protected/invariant); standard giu logic cu + web khong can approval + trusted-lift gop vao. `AgentV1Service.Policies`: parse plan/standard/full_access, xoa trusted, full_access yeu cau interactive.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~Policy" -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Harness.Core/AgentPermissionPreset.cs backend-dotnet/RuAgent.Protocol/Harness/BrokerPolicyContracts.cs backend-dotnet/RuAgent.Broker/Policy/PolicySnapshotEvaluator.cs backend-dotnet/RuBackend/Harness/AgentV1Service.Policies.cs backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs
git commit -m "feat(permission): ladder plan standard full-access bo trusted"
```

### Task 3: Permission — web khong can approval o standard, GUI/CLI theo ladder moi

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Policy/PolicySnapshotEvaluator.cs:191-194`
- Modify: `backend-dotnet/RuAgent.Cli/AgentCliParser.cs:438-456`
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.cs:111-118`
- Modify: `gui-winui3/Views/ChatView.xaml.cs:3024`
- Test: `backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs`

**Interfaces:**
- Consumes: preset wire moi tu Task 2.
- Produces: web auto o standard/yolo, CLI/GUI picker 3 mode moi.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Standard_preset_allows_web_without_approval()
{
    var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWithPreset("standard"));
    var verdict = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry()).Evaluate(
        lease.Snapshot,
        factory.Create("web_search", "1.0.0", new JsonObject { ["query"] = "ru" }),
        PolicyRequestInvariants.Valid,
        Now);
    Assert.Equal("allow", verdict.Decision);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~Standard_preset_allows_web" -v q`
Expected: FAIL with "Expected allow, actual ask"

- [ ] **Step 3: Write minimal implementation**

Trong `EvaluateInner`: neu preset la standard hoac full_access thi web_search/web_fetch tra allow thay vi ask. CLI parser nhan plan/standard/full_access, xoa trusted, bo downgrade full_access→standard. GUI picker doi Standard/Trusted thanh Plan/Standard/Full Access.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~Policy" -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Broker/Policy/PolicySnapshotEvaluator.cs backend-dotnet/RuAgent.Cli/AgentCliParser.cs backend-dotnet/RuAgent.Cli/AgentCommands.cs gui-winui3/Views/ChatView.xaml.cs backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs
git commit -m "feat(permission): web auto o standard, GUI CLI theo ladder moi"
```

### Task 4: Model — Models.dev catalog + generic provider_id

**Files:**
- Modify: `backend-dotnet/RuBackend/Providers/ProviderCatalogService.cs:1-80`
- Modify: `backend-dotnet/RuBackend/Providers/ProviderSelection.cs:47-64`
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs`
- Test: `backend-dotnet/RuBackend.Tests/Providers/ProviderCatalogTests.cs`

**Interfaces:**
- Consumes: `SettingsSnapshot` co them `ProviderId` tu Task 1 pattern.
- Produces: `ProviderCatalogService.GetModelsDevAsync()` tra `IReadOnlyList<ModelsDevProvider>`. `ProviderSelection.From` nhan provider_id generic.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task ModelsDev_catalog_lists_generic_provider()
{
    var service = new ProviderCatalogService(new HttpClient(new FakeModelsDevHandler()), new FakeCredentials(), TimeProvider.System);
    var providers = await service.GetModelsDevAsync(CancellationToken.None);
    Assert.Contains(providers, p => p.Id == "openrouter");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~ModelsDev_catalog" -v q`
Expected: FAIL with "GetModelsDevAsync not defined"

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record ModelsDevProvider(string Id, string Name, string BaseUrl);

public async Task<IReadOnlyList<ModelsDevProvider>> GetModelsDevAsync(CancellationToken ct)
{
    using var response = await _http.GetAsync("https://models.dev/api.json", ct).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    var result = new List<ModelsDevProvider>();
    foreach (var property in doc.RootElement.EnumerateObject())
    {
        if (property.Value.TryGetProperty("api", out var api)
            && api.GetString() is string url
            && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            result.Add(new ModelsDevProvider(property.Name, property.Name, uri.GetLeftPart(UriPartial.Authority)));
        }
    }
    return result;
}
```

Cache local TTL 24h trong `%LocalAppData%/Ru/models-dev.json`, offline doc cache cu, khong co cache thi tra rong fail-closed. `ProviderSelection.Describe` them nhanh generic: backend bat dau bang `models.dev:` thi dung BaseUri tu catalog + protocol chat_completions. Giu 5 backend cu nhu alias.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~Provider" -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuBackend/Providers/ProviderCatalogService.cs backend-dotnet/RuBackend/Providers/ProviderSelection.cs backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs backend-dotnet/RuBackend.Tests/Providers/ProviderCatalogTests.cs
git commit -m "feat(model): Models.dev catalog va generic provider"
```

### Task 5: Docs — dong bo permission + model + memory

**Files:**
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `docs/architecture.md`
- Modify: `docs/operations/provider-catalog.md`

**Interfaces:**
- Consumes: 3 task tren da xong.
- Produces: docs mo ta dung ladder moi, catalog moi, setting moi.

- [ ] **Step 1: Write the failing test**

Run: `python tests/test_current_documentation.py -v`
Expected: FAIL neu docs con chu trusted hoac thieu full_access

- [ ] **Step 2: Run test to verify it fails**

Run: `Select-String -Path docs -Pattern "trusted" -Recurse`
Expected: con match o agent-tools/runtime-behavior/architecture

- [ ] **Step 3: Write minimal implementation**

Sua docs: ladder plan → standard → full_access, xoa trusted; web auto o standard; Models.dev catalog 75+ provider; 2 setting compaction moi voi default.

- [ ] **Step 4: Run test to verify it passes**

Run: `python tests/test_current_documentation.py -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add docs/operations/agent-tools.md docs/operations/runtime-behavior.md docs/architecture.md docs/operations/provider-catalog.md
git commit -m "docs: ladder permission moi, Models.dev, compaction settings"
```
