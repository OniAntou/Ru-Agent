# Provider Catalog Refresh Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for the integrated work; use superpowers:dispatching-parallel-agents only for the disjoint GUI and Worker transport slices described below. Steps use checkbox tracking.

**Goal:** Refresh real provider catalogs and model-specific reasoning options without fallback, lost selections or stale cross-provider results.

**Architecture:** A Host catalog/cache service owns discovery and safe capability parsing. The GUI consumes a typed additive `/models` response. Each Agent step receives a protected, validated provider snapshot so model/endpoint/key/reasoning correspond to the current selection rather than startup-only settings.

**Tech Stack:** .NET 10, ASP.NET Core, WinUI 3, xUnit, existing Python checks.

## Global constraints

- Work in the current D:\LLM checkout as previously authorized; preserve existing docs changes. No commit/push, app launch, device changes or live generation.
- No fallback provider, credential, protocol or reasoning choice. Heavy checks run sequentially; coordinate test ownership with Luna.
- Reuse 56 HTTP + 1 WebSocket routes. Cache five minutes; throttle manual refresh 30 seconds. Credentials remain Host-owned and protected in private per-step handoffs.
- UI updates do not save settings; unknown reasoning means Provider default only. Explicit invalid choices are visible and stop dispatch.

## Shared contract

`GET /models?backend=<alias>&refresh=true` returns:

```json
{"status":"ok","backend":"kilo","identity":"opaque","selected_model":"model-a","selected_effort":"default","models":["model-a"],"catalog":[{"id":"model-a","supported":true,"protocol":"chat_completions","reasoning":{"state":"levels","options":["low","high"]}}],"stale":false,"error_code":null,"last_success_utc":"2026-09-01T00:00:00Z"}
```

Catalog errors have `status:error`, a safe `error_code`, and possibly same-identity stale data. No credentials or private wire overlays are returned. A valid empty catalog is `status:ok`.

Reasoning saves use the existing settings endpoint, a partial update:

```json
{"reasoning_effort":"high","reasoning_model":"model-a","reasoning_identity":"opaque"}
```

Host validates the matching identity/model and stores the choice under a provider/endpoint/model key. `default` always means omission. GUI treats this as a separate explicit save, never writes it during a catalog refresh. Successful settings saves notify GUI catalog consumers to invalidate in-flight results.

### Task 1: Host catalog, cache and settings

Files: create `backend-dotnet/RuBackend/Providers/{ProviderSelection,ProviderCatalogModels,ProviderCatalogParser,ProviderCatalogService,ProviderReasoningPreferences}.cs`; modify `Runtime/StatusEndpoints.cs`, `Program.cs`, `Business/Settings/SettingsTransitionService.cs`, `Business/BusinessEndpoints.cs`; add `RuBackend.Tests/Providers/ProviderCatalogTests.cs`.

- [x] RED: exercise HTTP discovery with a fake handler, controlled clock and credentials. Assert real IDs, canonical account, no redirects/fallback, cache expiry, forced-refresh throttle, malformed vs empty, outage retention and identity changes.
- [x] Implement `ProviderCatalogService.GetAsync(SettingsSnapshot settings, bool refresh, CancellationToken ct)` returning a snapshot. Extract shared provider resolution; bound responses, timeout, item count, strings and retry delay.
- [x] RED/GREEN capability parser tests: Kilo `opencode.variants` with allowlisted reasoning overlays, unknown Zen bare IDs, malformed variants and advertised unsupported protocols. Do not infer effort levels from model names.
- [x] RED/GREEN partial reasoning-save validation and per-model persistence. Outdated identity and unsupported effort fail without mutating settings.

```csharp
var first = await service.GetAsync(settings, false, CancellationToken.None);
Assert.Equal(new[] { "model-a" }, first.Models);
clock.Advance(TimeSpan.FromMinutes(6));
handler.Json = "{\"data\":[{\"id\":\"model-b\"}]}";
var next = await service.GetAsync(settings, false, CancellationToken.None);
Assert.Equal(new[] { "model-b" }, next.Models);
```

Run focused tests with `dotnet test backend-dotnet/RuBackend.Tests -c Release --filter FullyQualifiedName~ProviderCatalog --no-restore`.

### Task 2: Protected per-step provider/reasoning transport (Luna slice)

Files: `RuAgent.Runtime.Adapters/Bootstrap/ProtectedProviderBootstrap.cs`, new bounded reasoning-option validator, `RuAgent.Runtime.Worker/{WorkerV2Options,WorkerV2StepService,ManagedWorkerComposition,Program}.cs`, `RuAgent.Protocol/Harness/WorkerV2Contracts.cs`, `RuBackend/Harness/Worker/LoopbackModelStepRunner.cs`, relevant transport tests. No Host catalog/settings/Program changes in this slice.

- [x] RED: a captured request uses the per-step protected model/endpoint/key and safe reasoning overlay instead of startup defaults; invalid/missing/outside-root handoff never generates.
- [x] Add optional validated `RequestOptions` to `ProviderBootstrapData`, serialized inside DPAPI; old startup data remains readable.
- [x] Expose `LoopbackModelStepRunner.CreateProviderBootstrapAsync` as an optional `Func<ModelStepRequest,CancellationToken,Task<string>>` init property. The request supplies RunId for run-pinned selection. Create the handoff before dispatch, attach top-level `provider_bootstrap_path`, and clean only the owned handoff on success/error/cancellation. No path/key in public errors.
- [x] Bind a trusted bootstrap root from Worker startup, consume the per-step file with existing guarded ReadAndDelete and merge only protected reasoning options into ModelRequest. Strip untrusted reasoning fields from step settings. A production trusted root requires the per-step handoff, not silent startup fallback.
- [x] GREEN: protect default omission, malformed options, cancellation cleanup and isolation. Host supplies the factory during integration.

```csharp
Assert.Equal("model-b", captured.Model);
Assert.Equal("high", captured.Settings["reasoning"]?["effort"]?.GetValue<string>());
Assert.False(File.Exists(handoffPath));
```

### Task 3: GUI refresh and reasoning selector (Luna slice)

Files: new GUI catalog DTO/service/controller, `Services/ApiService.cs`, `Views/ChatView.xaml.cs` (prefer partial catalog file), `Views/SettingsView.xaml[.cs]` (prefer partial reasoning file), `MainWindow.xaml.cs`, focused `gui-winui3.Tests` files. No backend edits.

- [x] RED/GREEN controlled fetch/clock tests for active five-minute scheduling, 30-second manual throttle, identity invalidation, single flight, cancellation/disposal, stale vs empty and selection/search preservation.
- [x] Consume the shared JSON contract, never reinterpret API error as an empty success. Remove fabricated model fallback lists.
- [x] Invalidate after successful settings/model/key changes; activate polling only while Chat is active; refresh on flyout open. Use a focused helper instead of expanding MainWindow/ChatView networking logic.
- [x] Replace fixed OpenCode options and Kilo toggle with the shared model-aware selector in the existing settings row. Keep invalid saved effort visible, make explicit partial saves only, and do not overwrite the model selected in Chat using stale SettingsView copies.
- [x] GREEN focused GUI tests then hand off for sequential real WinUI compilation.

```csharp
await controller.RefreshAsync(force: true);
Assert.Equal("model-a", state.SelectedModel);
// Complete an older delayed response after an identity invalidation.
Assert.DoesNotContain("old-provider-model", state.Models);
```

### Task 4: Integration, evidence and docs

- [x] Supply the Host per-step factory: read settings once, resolve matching catalog, validate explicit effort, write one protected snapshot using canonical credential, and reject unavailable/unsupported selections without network generation.
- [x] Review both Luna diffs for contract alignment and privacy; run focused Host + Worker + GUI integration tests sequentially.
- [x] Run `.venv/Scripts/python.exe -m pytest -q`, `scripts/test_dotnet.ps1 -Configuration Release -NoRestore`, actual WinUI Release/x64 build, route checker, Ruff and diff checks.
- [x] Update architecture, verification, spec status and TODO with measured results and unknown/provider protocol limitations. No claiming live GUI/provider acceptance from mocks. Commit/push only when requested.

## Progress / evidence

- Implementation complete in the working tree; commit/push/publish remain separate.
- Public unauthenticated catalog inspection: Kilo returns `opencode.variants` with per-model reasoning overlays; Zen `/models` currently returns bare IDs only. No user keys or model generation used.
- Sequential gates: Python 409; .NET Release 2,155 across 13 projects; GUI Release/x64 build clean; Ruff, Mypy, route matrix and diff checks clean. No live provider generation or interactive GUI acceptance.
