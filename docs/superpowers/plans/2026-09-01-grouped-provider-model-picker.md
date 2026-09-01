# Grouped Provider Model Picker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show every usable conversation/Agent model in one Chat picker, grouped by provider, and persist a provider/model switch atomically without any fallback.

**Architecture:** Keep the Host-owned per-provider `GET /models` contract and compose one GUI controller from seven existing single-provider controllers. Build deterministic grouped/searchable display state outside the view, render it in a focused WinUI control, and add a strict two-field model-selection shape to the existing atomic `POST /settings` transition.

**Tech Stack:** .NET 10, ASP.NET Core, WinUI 3, C# records, xUnit, existing Python route/docs checks.

## Global Constraints

- Implement the approved design in `docs/superpowers/specs/2026-09-01-grouped-provider-model-picker-design.md`; do not broaden the Settings or Chat redesign.
- Work from the current `D:\LLM` checkout and preserve unrelated work. Stage only files owned by each task.
- If work is delegated, use Luna. Run heavyweight test suites sequentially because parallel full-suite execution can exhaust the Windows pagefile.
- Keep `GET /models` per provider. Do not add an aggregate endpoint or change the public route count (`56 HTTP + 1 WebSocket`).
- Never infer a provider from a model ID. Never retry another provider, key, model, protocol, or reasoning effort after a failure.
- Only Host-returned `Supported == true` models with an allowed conversation/Agent protocol are selectable.
- Keep the existing five-minute polling, 30-second forced-refresh throttle, identity-scoped stale cache, and bounded error behavior.
- Do not launch the GUI, call live providers, use real credentials, or commit/push unless the user separately authorizes those actions during execution.
- Keep UI-specific construction out of `ChatView.xaml.cs`; the file is already large. Pure projection logic must remain independently testable.

## Shared Data Contract

The Chat picker saves a provider/model pair through the existing `POST /settings` route:

```json
{
  "backend_choice": "kilo",
  "selected_model": "kilo-auto/frontier"
}
```

This specialized shape contains exactly two properties. The Host normalizes the backend alias, maps `selected_model` to that backend's persisted model field, and sends the resulting candidate through one existing `SettingsTransitionService` transaction. Invalid aliases, blank models, extra properties, and mixed generic/provider-specific model fields fail closed as `Invalid settings payload.` No alternate provider request is made.

The GUI owns presentation-only definitions in this stable order:

```csharp
new("OpenCode Zen", "opencode", "OpenCode", 0),
new("Kilo Gateway", "kilo", "Kilo", 1),
new("OpenAI", "openai", "OpenAI", 2),
new("Anthropic", "anthropic", "Anthropic", 3),
new("Ollama", "ollama", "Ollama", 4),
new("LM Studio", "lmstudio", "LM Studio", 5),
new("Custom OpenAI", "custom", "Custom", 6),
```

These definitions must not contain endpoints, credentials, protocol guesses, capability overrides, or fallback rules.

---

## Task 1: Add the Strict Atomic Provider/Model Save Shape

**Files:**

- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsTransitionService.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs`
- Modify: `gui-winui3/Models/ProviderCatalogModels.cs`
- Modify: `gui-winui3/Services/ApiService.cs`
- Modify: `gui-winui3.Tests/ProviderCatalogTests.cs`
- Modify: `gui-winui3.Tests/OfficialProviderTests.cs`

- [ ] **Step 1: Write backend RED tests for atomic selection.**

Add endpoint tests that post `{backend_choice:"kilo", selected_model:"kilo-model"}` and assert one runtime candidate and one persisted snapshot contain `Backend == "Kilo Gateway"` and `KiloModel == "kilo-model"`, while the prior OpenCode model remains unchanged. Add table-driven coverage for every alias: `opencode`, `kilo`, `openai`, `anthropic`, `ollama`, `lmstudio`, and `custom`.

```csharp
using var response = await client.PostAsJsonAsync("/settings", new
{
    backend_choice = "kilo",
    selected_model = "kilo-model",
});

Assert.Equal(HttpStatusCode.OK, response.StatusCode);
Assert.Equal("success", (await response.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.GetValue<string>());
Assert.Single(runtime.Candidates);
Assert.Equal("Kilo Gateway", runtime.Candidates[0].Backend);
Assert.Equal("kilo-model", runtime.Candidates[0].KiloModel);
```

- [ ] **Step 2: Write backend rejection tests.**

Cover unknown backend, blank/whitespace model, model longer than 256 characters, missing either required property, extra properties, and payloads that combine `selected_model` with `kilo_model`, `model`, credentials, URLs, or reasoning fields. Assert `status:error`, no runtime load, and byte-for-byte unchanged settings.

- [ ] **Step 3: Run the backend RED tests.**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --filter "FullyQualifiedName~BusinessEndpointTests" --no-restore
```

Expected: the new atomic-selection tests fail because `selected_model` is not recognized or validated.

- [ ] **Step 4: Implement one strict expansion at the transition boundary.**

At the start of `SettingsTransitionService.ApplyAsync`, before `BuildCandidate`, recognize `selected_model`. Require exactly `backend_choice` and `selected_model`, normalize the alias through the existing backend normalization, and replace the specialized payload with the corresponding canonical pair:

```csharp
private static JsonObject ExpandModelSelection(JsonObject payload)
{
    if (!payload.ContainsKey("selected_model")) return payload;
    if (payload.Count != 2 || !payload.ContainsKey("backend_choice"))
        throw new SettingsPayloadException();

    string backend = NormalizeBackend(
        OptionalString(payload, "backend_choice", 64)
        ?? throw new SettingsPayloadException());
    string model = OptionalString(payload, "selected_model", 256)?.Trim()
        ?? throw new SettingsPayloadException();
    if (model.Length == 0) throw new SettingsPayloadException();

    var expanded = new JsonObject { ["backend_choice"] = backend };
    expanded[ModelFieldForBackend(backend)] = model;
    return expanded;
}
```

Centralize the canonical backend-to-model-field map in one private method. Feed the expanded payload to `BuildCandidate`, `BuildCredentialUpdates`, and `RequiresRuntimeLoad`, so the existing runtime-load/save/rollback transaction remains the only write path. Do not add a second save or a new endpoint.

- [ ] **Step 5: Add the GUI DTO and API method with RED serialization tests.**

Replace Chat's model-only DTO boundary with:

```csharp
public sealed class ProviderModelSelectionUpdateDto
{
    [JsonPropertyName("backend_choice")]
    public string BackendChoice { get; init; } = string.Empty;

    [JsonPropertyName("selected_model")]
    public string SelectedModel { get; init; } = string.Empty;
}
```

Add `ApiService.SaveProviderModelSelectionAsync(string backend, string model)`. It posts this DTO once to `/settings`, raises `SettingsSaved` only on a success envelope, and returns `false` on bounded API/JSON failures. Remove `SaveModelSettingsAsync` and `ModelSettingsUpdateDto` after confirming there are no remaining production callers.

Update `ProviderCatalogTests` and `OfficialProviderTests` to assert the exact two-property body for Kilo, OpenAI, and Anthropic and that no provider-specific model field is serialized by the GUI.

- [ ] **Step 6: Run focused GREEN tests.**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --filter "FullyQualifiedName~BusinessEndpointTests" --no-restore
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --filter "FullyQualifiedName~ProviderCatalogTests|FullyQualifiedName~OfficialProviderTests" --no-restore
rg -n "SaveModelSettingsAsync|ModelSettingsUpdateDto" gui-winui3 gui-winui3.Tests
```

Expected: tests pass and the final search returns no matches.

- [ ] **Step 7: Commit the atomic boundary.**

```powershell
git add backend-dotnet/RuBackend/Business/Settings/SettingsTransitionService.cs backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs gui-winui3/Models/ProviderCatalogModels.cs gui-winui3/Services/ApiService.cs gui-winui3.Tests/ProviderCatalogTests.cs gui-winui3.Tests/OfficialProviderTests.cs
git commit -m "feat: save provider model selection atomically"
```

---

## Task 2: Build Pure Provider Group and Picker Projection Models

**Files:**

- Create: `gui-winui3/Models/ProviderGroupDefinitions.cs`
- Create: `gui-winui3/Models/ProviderModelPickerModels.cs`
- Create: `gui-winui3.Tests/ProviderModelPickerProjectionTests.cs`

- [ ] **Step 1: Write projection RED tests.**

Cover all approved pure behavior:

- stable order independent of active provider or arrival order;
- empty, unconfigured, failed-without-data, and unsupported-only groups hidden;
- same-identity stale groups with usable models retained and marked `Dữ liệu cũ`;
- provider-name search retains all models in that group;
- model-ID search filters rows and hides empty result groups;
- search is case-insensitive and does not mutate source state;
- the active provider/model row is selected;
- a selected model missing from a successful non-empty catalog is inserted once, disabled, and annotated `Không còn khả dụng`;
- a selected model is not fabricated when its provider has no successful non-empty catalog;
- unsupported protocols and `Supported == false` rows never become selectable;
- all hidden groups produce `Không có model khả dụng`.

```csharp
var projection = ProviderModelPickerProjection.Create(
    groups: [kilo, openCode, emptyOpenAi],
    selectedBackend: "kilo",
    selectedModel: "kilo-auto/frontier",
    searchText: "OpenCode");

Assert.Collection(projection.Groups,
    group =>
    {
        Assert.Equal("OpenCode", group.Provider.DisplayName);
        Assert.Equal(openCode.Models.Count, group.Rows.Count);
    });
```

- [ ] **Step 2: Run the projection RED tests.**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --filter "FullyQualifiedName~ProviderModelPickerProjectionTests" --no-restore
```

Expected: compilation fails because the projection types do not exist.

- [ ] **Step 3: Implement immutable definitions and display state.**

Use small records such as:

```csharp
public sealed record ProviderGroupDefinition(
    string Backend,
    string Alias,
    string DisplayName,
    int Order);

public sealed record ProviderModelPickerRow(
    string Backend,
    string ProviderName,
    string ModelId,
    string ContextLabel,
    bool IsSelected,
    bool IsEnabled,
    bool IsStale,
    string Annotation);

public sealed record ProviderModelPickerGroup(
    ProviderGroupDefinition Provider,
    IReadOnlyList<ProviderModelPickerRow> Rows,
    bool IsStale);
```

Expose `ProviderGroupDefinitions.All` as an immutable ordered list and `ByAlias`/`ByBackend` lookup helpers. Keep definitions presentation-only.

`ProviderModelPickerProjection.Create(...)` accepts raw per-provider `ProviderCatalogState` values plus the explicit active alias/model. It must use catalog descriptors as the capability authority; do not infer support from model names. Reuse the existing allowed protocol check by moving it from `ChatView` into a focused shared helper if necessary.

- [ ] **Step 4: Run projection GREEN tests.**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --filter "FullyQualifiedName~ProviderModelPickerProjectionTests" --no-restore
```

Expected: all projection tests pass.

- [ ] **Step 5: Commit the pure model layer.**

```powershell
git add gui-winui3/Models/ProviderGroupDefinitions.cs gui-winui3/Models/ProviderModelPickerModels.cs gui-winui3.Tests/ProviderModelPickerProjectionTests.cs
git commit -m "feat: model grouped provider picker state"
```

---

## Task 3: Compose All Provider Catalogs Without Coupling Failures

**Files:**

- Create: `gui-winui3/Services/GroupedProviderCatalogController.cs`
- Create: `gui-winui3.Tests/GroupedProviderCatalogControllerTests.cs`
- Modify only if a narrow test seam is required: `gui-winui3/Services/ProviderCatalogController.cs`

- [ ] **Step 1: Write grouped-controller RED tests with controlled services and clock.**

Assert that the controller:

- creates one child catalog controller per `ProviderGroupDefinitions.All` entry and fixes each child's alias once;
- starts all provider refreshes before awaiting completion;
- publishes each successful provider independently rather than waiting for the slowest provider;
- retains same-identity stale child data while hiding failed children without data through the projection;
- coalesces overlapping grouped force refreshes;
- preserves selected alias/model and search text across refreshes;
- rejects late state from a previous grouped generation after invalidation/disposal;
- polls all due children only while active;
- cancels and disposes every child without post-dispose publication.

Use `TaskCompletionSource<ProviderCatalogSnapshot?>` per alias so completion order is deterministic.

- [ ] **Step 2: Run grouped-controller RED tests.**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --filter "FullyQualifiedName~GroupedProviderCatalogControllerTests" --no-restore
```

Expected: compilation fails because the grouped controller does not exist.

- [ ] **Step 3: Implement the controller by composition.**

Do not duplicate HTTP/cache/throttle logic. Construct seven existing `ProviderCatalogController` children, call `SetBackend(definition.Alias)` once for each, subscribe to `StateChanged`, and aggregate immutable snapshots under one lock.

Required surface:

```csharp
public sealed class GroupedProviderCatalogController : IAsyncDisposable
{
    public GroupedProviderCatalogState State { get; }
    public event EventHandler<GroupedProviderCatalogState>? StateChanged;
    public void SetSelection(string backend, string model);
    public void SetSearchText(string searchText);
    public void SetPollingActive(bool active);
    public Task<bool> RefreshAsync(bool force = false);
    public Task<bool> RefreshIfDueAsync();
    public void Invalidate();
    public ValueTask DisposeAsync();
}
```

`RefreshAsync` starts every child call before `Task.WhenAll`. Child `StateChanged` updates only that provider and publishes immediately. The grouped task result is true when at least one current child applied data. One child failure must not throw away another child result. Use a grouped generation for invalidate/dispose and child reference checks for event safety.

Do not copy the active model from each child catalog into global selection; the active alias/model comes only from `/settings` and `SetSelection`.

- [ ] **Step 4: Run grouped-controller GREEN and existing controller regressions.**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --filter "FullyQualifiedName~GroupedProviderCatalogControllerTests|FullyQualifiedName~ProviderCatalogTests" --no-restore
```

Expected: all grouped and existing single-provider lifecycle tests pass.

- [ ] **Step 5: Commit catalog composition.**

```powershell
git add gui-winui3/Services/GroupedProviderCatalogController.cs gui-winui3/Services/ProviderCatalogController.cs gui-winui3.Tests/GroupedProviderCatalogControllerTests.cs
git commit -m "feat: coordinate grouped provider catalogs"
```

If `ProviderCatalogController.cs` was not changed, omit it from `git add`.

---

## Task 4: Extract and Render the Grouped WinUI Picker

**Files:**

- Create: `gui-winui3/Views/ProviderModelPicker.xaml`
- Create: `gui-winui3/Views/ProviderModelPicker.xaml.cs`
- Modify: `gui-winui3/Views/ChatView.xaml`
- Modify: `gui-winui3/Views/ChatView.xaml.cs`
- Modify: `gui-winui3.Tests/ProviderModelPickerProjectionTests.cs`

- [ ] **Step 1: Extend projection tests for UI text and accessibility state.**

For selected, unavailable, and stale rows, assert deterministic automation text in the form `Provider, model, selected/unavailable/stale`. Assert trigger display `Provider · model`, full tooltip/automation text, and `Model...` only when no pair is selected.

- [ ] **Step 2: Create a focused picker `UserControl`.**

Move the search box and scrollable list from `ChatView.xaml` into `ProviderModelPicker.xaml`. Keep the existing width and vertical `MaxHeight`; explicitly disable horizontal scrolling. Expose:

```csharp
public void Apply(ProviderModelPickerProjection projection);
public void FocusSearch();
public string SearchText { get; }
public event EventHandler<string>? SearchTextChanged;
public event EventHandler<ProviderModelSelectionEventArgs>? ModelSelected;
```

Render each group as a non-interactive heading row with display name, visible count, and optional `Dữ liệu cũ`. Render model rows as keyboard-focusable buttons using the existing Ru resources, context badge, selected treatment, and check icon. Set `AutomationProperties.Name`, `ToolTipService.ToolTip`, and `IsEnabled` from the projection. Do not assign provider colors or logos.

- [ ] **Step 3: Replace flat-list construction in ChatView.**

In `ChatView.xaml`, replace `ModelSearchBox` and `ModelListContainer` with the focused control inside the existing flyout. In `ChatView.xaml.cs`, remove `_loadedModels`, `FilterAndRenderModels`, `TryGetUnavailableReason`, and dynamic button construction.

ChatView should now:

- apply grouped-controller state through `ProviderModelPickerProjection.Create`;
- focus search and force-refresh all groups on flyout open;
- forward search changes to the grouped controller without network I/O;
- close the flyout and call one selection method on a row event;
- keep the previous selection until save success;
- on success, call `SetSelection`, update `Provider · model`, refresh context metadata, and synchronize model-aware reasoning state;
- on failure, keep the prior pair and show the existing five-second bounded error.

Replace the current pre-save backend-equality check. Cross-provider selection is now intentional; the atomic Host request is the authority.

- [ ] **Step 4: Handle selected-model disappearance exactly.**

The projection, not ChatView, inserts a disabled `Không còn khả dụng` row only when the selected provider has a successful non-empty catalog that no longer contains the selected model. Never auto-select another row. A stale same-identity row remains enabled only when the retained Host descriptor is still supported.

- [ ] **Step 5: Compile and run focused tests.**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --filter "FullyQualifiedName~ProviderModelPickerProjectionTests" --no-restore
dotnet build gui-winui3/RuApp.csproj -c Release -p:Platform=x64 --no-restore
```

Expected: projection tests pass and WinUI XAML/C# compile cleanly.

- [ ] **Step 6: Commit the focused control.**

```powershell
git add gui-winui3/Views/ProviderModelPicker.xaml gui-winui3/Views/ProviderModelPicker.xaml.cs gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/ProviderModelPickerProjectionTests.cs
git commit -m "feat: render provider-grouped model picker"
```

---

## Task 5: Integrate Lifecycle, Polling, Settings Invalidation, and Reasoning

**Files:**

- Modify: `gui-winui3/MainWindow.xaml.cs`
- Modify: `gui-winui3/Views/ChatView.xaml.cs`
- Modify: `gui-winui3/Views/SettingsView.xaml.cs`
- Modify: `gui-winui3.Tests/ProviderCatalogTests.cs`
- Modify: `gui-winui3.Tests/OfficialProviderTests.cs`

- [ ] **Step 1: Write lifecycle RED tests at available non-UI seams.**

Add tests showing that a successful atomic selection raises one `SettingsSaved` event, updates grouped selection only after success, and leaves the previous pair on failure. Add a controller test proving `Invalidate` cancels old children and a subsequent refresh cannot be overwritten by their late results.

- [ ] **Step 2: Give Chat and Settings separate controller responsibilities.**

In `MainWindow`, retain the existing single `ProviderCatalogController` for Settings' active provider/reasoning surface. Add one `GroupedProviderCatalogController` for Chat and pass both required dependencies explicitly to `ChatView.Initialize`. The five-minute dispatcher timer invokes both `RefreshIfDueAsync` calls only while Chat is active.

On successful settings save:

- invalidate the Settings controller;
- invalidate grouped provider identities;
- reload the active provider/model pair from `/settings` before refreshing;
- do not issue any fallback catalog or settings request.

When leaving Chat, disable grouped polling and stop the timer. When returning, enable it, reload selection, and refresh due groups. Dispose the grouped controller alongside the existing controller during window shutdown. ChatView must unsubscribe but must not dispose a MainWindow-owned controller.

- [ ] **Step 3: Synchronize reasoning after successful selection.**

After the atomic save succeeds, point the existing Settings catalog controller at the newly selected alias and refresh it so reasoning options are derived from that provider/model catalog. Keep `Provider default` if catalog metadata is unknown/stale. Do not save or invent a reasoning effort during model selection.

- [ ] **Step 4: Run focused GUI tests and Release build.**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --filter "FullyQualifiedName~ProviderCatalogTests|FullyQualifiedName~GroupedProviderCatalogControllerTests|FullyQualifiedName~ProviderModelPickerProjectionTests|FullyQualifiedName~OfficialProviderTests" --no-restore
dotnet build gui-winui3/RuApp.csproj -c Release -p:Platform=x64 --no-restore
```

Expected: focused tests and Release build pass with no XAML warnings promoted to errors.

- [ ] **Step 5: Commit lifecycle integration.**

```powershell
git add gui-winui3/MainWindow.xaml.cs gui-winui3/Views/ChatView.xaml.cs gui-winui3/Views/SettingsView.xaml.cs gui-winui3.Tests/ProviderCatalogTests.cs gui-winui3.Tests/OfficialProviderTests.cs
git commit -m "feat: integrate grouped provider picker lifecycle"
```

---

## Task 6: Update Current Documentation and Remove Superseded Claims

**Files:**

- Modify: `TODO.md`
- Modify: `docs/architecture.md`
- Modify: `docs/operations/provider-catalog.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `docs/operations/verification.md`
- Review for accuracy: `CONTEXT.md`
- Review for outdated content: `docs/README.md`, `docs/adr/*.md`, `docs/superpowers/plans/*.md`, `docs/superpowers/specs/*.md`

- [ ] **Step 1: Update active product and architecture documentation.**

Record that Chat displays all non-empty provider catalogs in stable groups, search spans provider names/model IDs, and one selection changes provider/model atomically. State explicitly that failed groups are isolated, stale same-identity data is labeled, unavailable selected models are not replaced, and no provider/model/key/protocol fallback exists.

Add a completed TODO item linking the approved spec and this plan only after implementation and deterministic gates pass. Do not rewrite historical plan/spec evidence as if it were current runtime documentation.

- [ ] **Step 2: Audit outdated documents before deletion.**

Search for claims that Chat lists only the active provider, model save updates only a provider-specific field, or the GUI fabricates fallback models:

```powershell
rg -n -i "active provider|current provider|SaveModelSettingsAsync|ModelSettingsUpdateDto|fallback model|flat model|model dropdown" README.md TODO.md CONTEXT.md docs gui-winui3
```

Delete a document only if all current guidance it contains has been superseded and inbound references can be updated in the same task. Do not delete approved historical design specs/plans merely because implementation is complete.

- [ ] **Step 3: Run docs and route checks.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py tests/test_aspnetcore_route_matrix.py -q
& D:\LLM\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
```

Expected: docs tests pass and route contract remains `56 HTTP + 1 WebSocket`.

- [ ] **Step 4: Commit documentation.**

```powershell
git add TODO.md docs/architecture.md docs/operations/provider-catalog.md docs/operations/runtime-behavior.md docs/operations/verification.md CONTEXT.md docs/README.md docs/adr docs/superpowers
git diff --cached --name-status
git commit -m "docs: document grouped provider model selection"
```

Before committing, unstage any reviewed-but-unchanged or unrelated documentation. If no obsolete document satisfies the deletion rule, record that the audit found none and do not delete files for appearance's sake.

---

## Task 7: Sequential Verification and Manual Acceptance Boundary

**Files:**

- Modify only if results materially differ: `docs/operations/verification.md`
- Modify only if implementation status changes: `TODO.md`

- [ ] **Step 1: Run focused backend and GUI suites.**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --no-restore
```

- [ ] **Step 2: Run repository contract checks sequentially.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
git diff --check
```

- [ ] **Step 3: Build the production GUI artifact.**

Use the repository's current documented Release build/publish command. At minimum:

```powershell
dotnet build gui-winui3/RuApp.csproj -c Release -p:Platform=x64 --no-restore
```

Do not launch it unless the user explicitly authorizes GUI control in that execution session.

- [ ] **Step 4: Inspect the final diff and fallback absence.**

```powershell
git status --short --branch
git diff --stat origin/main...HEAD
rg -n -i "fallback|try.*provider|default.*opencode" gui-winui3 backend-dotnet/RuBackend
```

Classify every search hit. Existing comments/tests that assert absence are acceptable; runtime logic that retries or silently defaults the selection is not.

- [ ] **Step 5: Perform manual GUI acceptance only with user authorization.**

Using controlled/non-secret provider catalogs, verify:

1. OpenCode, Kilo, OpenAI, Anthropic, Ollama, LM Studio, and Custom appear in stable order only when they have usable rows.
2. Provider-name and model-ID searches behave as specified.
3. Keyboard focus starts in search; arrow/Tab navigation, Enter, Escape, tooltip, automation names, and check state work.
4. Long `Provider · model` text truncates visually but retains its full accessible name.
5. Dark/light themes and bounded vertical scrolling render correctly without horizontal scrolling.
6. A failed provider does not hide successful groups; stale and unavailable labels are correct.
7. Cross-provider selection updates the next turn's provider/model and a simulated save failure preserves the previous pair.

Do not claim live provider generation, credentials, billing, or in-flight turn behavior from deterministic tests alone.

- [ ] **Step 6: Record exact evidence and commit only evidence changes.**

Update verification counts/commands/results without copying secrets or provider response bodies. If docs changed:

```powershell
git add TODO.md docs/operations/verification.md
git commit -m "docs: record grouped picker verification"
```

- [ ] **Step 7: Stop before push.**

Report commits, tests, build artifact, manual acceptance status, and remaining live limitations. Push only after explicit user authorization.

## Completion Criteria

- Every usable provider is visible in one grouped dropdown; empty groups are fully absent.
- Ordering, global search, selected/unavailable/stale states, and empty state match the approved design.
- Provider/model selection is one strict Host transaction and failures preserve the prior pair.
- The grouped controller reuses existing per-provider cache/refresh logic, isolates failures, and rejects late results.
- Chat's grouped controller and Settings' active-provider controller have explicit, disposable lifecycles.
- Only Host-approved conversation/Agent models are selectable and no fallback exists.
- Focused tests, full backend/GUI suites, Python checks, route matrix, docs checks, and WinUI Release build pass sequentially.
- Current docs and TODO are updated; obsolete-document deletion is evidence-based, not automatic.
