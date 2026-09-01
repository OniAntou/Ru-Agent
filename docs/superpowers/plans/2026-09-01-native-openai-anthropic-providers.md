# Native OpenAI and Anthropic Providers Implementation Plan

**Status:** Implemented 2026-09-01. Deterministic gates after fail-closed review and managed-host startup remediation: `RuBackend.Tests` 683, `RuAgent.Runtime.Adapters.Tests` 169, `RuAgent.Runtime.Worker.Tests` 55, `gui-winui3.Tests` 330, Python 409, route 56+1, WinUI Release clean; local managed-host smoke returned authenticated HTTP 200. No live OpenAI/Anthropic key/catalog/generation/push was performed; those require separate authorization.

**Goal:** Add first-party OpenAI Responses and Anthropic Messages providers with isolated credentials, verified Agent-capable model catalogs, native reasoning controls, and no fallback.

**Architecture:** ASP.NET Core remains the settings, credential, catalog, and run-snapshot owner. A versioned capability registry intersects each authenticated live model catalog; the protected per-step bootstrap pins an explicit protocol and output cap. The managed Worker resolves that protocol to one of three focused adapters: existing OpenAI-compatible Chat Completions, native OpenAI Responses, or native Anthropic Messages.

**Tech Stack:** .NET 10, ASP.NET Core, WinUI 3, Windows Credential Manager, Windows DPAPI, `HttpClient`, `System.Text.Json`, xUnit, PowerShell.

## Global Constraints

- Work in `D:\LLM`. Start with `git status --short --branch`; preserve every unrelated dirty file and stage only files owned by the current task.
- The approved design is `docs/superpowers/specs/2026-09-01-native-openai-anthropic-providers-design.md` at commit `71f8228`.
- Do not add provider, credential, model, endpoint, protocol, or reasoning fallback. Do not infer protocol or capability from URLs or broad model-name prefixes.
- OpenAI uses fixed endpoints `https://api.openai.com/v1/models` and `https://api.openai.com/v1/responses` with Credential Manager account `openai_api_key`.
- Anthropic uses fixed endpoints `https://api.anthropic.com/v1/models` and `https://api.anthropic.com/v1/messages` with Credential Manager account `anthropic_api_key`.
- OpenAI requests set `store: false`. Anthropic `max_tokens` is `min(documented model maximum, 8192)` and is never exposed as a GUI setting.
- Provider default omits all reasoning fields. Unsupported, stale, or invalid explicit reasoning blocks dispatch.
- Do not enable provider-native tools, hosted search, files, MCP, function calling, OpenAI Chat Completions fallback, Anthropic compatibility mode, Bedrock, or Vertex AI.
- Credentials, prompts, responses, provider bodies, API headers, and account identity must not appear in public settings, catalog JSON, logs, diagnostics, tests, or evidence.
- All HTTP tests use controlled handlers and synthetic keys. Do not call a live provider or perform paid generation while implementing this plan.
- Run heavyweight .NET suites sequentially. Do not launch, publish, commit, push, or mutate live audio/device state unless the user separately requests it.
- Follow RED/GREEN for each task: add the focused test, run it and observe the expected failure, implement the minimum behavior, then rerun the focused project before committing that task.

## File Structure and Interfaces

New focused files:

- `backend-dotnet/RuBackend/Providers/ProviderCapabilityRegistry.cs`: exact first-party model/capability allowlist and source date.
- `backend-dotnet/RuBackend/Providers/ProviderCatalogRequestFactory.cs`: provider-specific authenticated model-list requests.
- `backend-dotnet/RuAgent.Runtime.Adapters/Models/BoundedSseReader.cs`: bounded strict-UTF-8 SSE framing shared by the two native adapters.
- `backend-dotnet/RuAgent.Runtime.Adapters/Models/OpenAiResponsesModelProvider.cs`: OpenAI request and stream semantics only.
- `backend-dotnet/RuAgent.Runtime.Adapters/Models/AnthropicMessagesModelProvider.cs`: Anthropic request and stream semantics only.
- `backend-dotnet/RuAgent.Runtime.Adapters/Models/IModelProviderResolver.cs`: protocol-to-adapter boundary.
- `backend-dotnet/RuAgent.Runtime.Adapters/Models/ModelProviderResolver.cs`: fail-closed exact protocol registry.
- `backend-dotnet/RuAgent.Runtime.Adapters/Bootstrap/ProviderProtocols.cs`: bounded protocol constants and validation.
- `backend-dotnet/RuAgent.Runtime.Adapters.Tests/OpenAiResponsesModelProviderTests.cs`: OpenAI wire tests.
- `backend-dotnet/RuAgent.Runtime.Adapters.Tests/AnthropicMessagesModelProviderTests.cs`: Anthropic wire tests.
- `backend-dotnet/RuAgent.Runtime.Adapters.Tests/BoundedSseReaderTests.cs`: framing, UTF-8, and size tests.

Shared exact contracts introduced by this plan:

```csharp
public static class ProviderProtocols
{
    public const string ChatCompletions = "chat_completions";
    public const string OpenAiResponses = "openai_responses";
    public const string AnthropicMessages = "anthropic_messages";
    public static string Require(string value);
}

public sealed record ProviderCapability(
    string Alias,
    string ModelId,
    string Protocol,
    IReadOnlyDictionary<string, JsonObject> ReasoningOptions,
    int? DocumentedMaxOutputTokens);

public sealed record ProviderSelection(
    string Backend,
    string Alias,
    string Protocol,
    Uri BaseUri,
    Uri ModelsUri,
    Uri CompletionUri,
    string Model,
    string? Account);

public interface IModelProviderResolver
{
    IModelProvider Resolve(string protocol);
}

public ProviderBootstrapData(
    string backend,
    string protocol,
    Uri endpoint,
    string model,
    string credential,
    JsonObject? requestOptions = null,
    int? maximumOutputTokens = null);

public ModelRequest(
    string model,
    string systemPrompt,
    string prompt,
    IReadOnlyList<ModelTurn> turns,
    JsonObject settings,
    Uri endpoint,
    string credential,
    int? maximumOutputTokens = null);
```

`ProviderBootstrapData` gains `string Protocol` and `int? MaximumOutputTokens`.
`ModelRequest` gains `int? MaximumOutputTokens`; reasoning remains in the
validated `Settings` object. No public API returns protected wire overlays or
the output cap.

---

### Task 1: Persist first-party provider settings and isolate credentials

**Files:**
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs`
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsStore.cs`
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsTransitionService.cs`
- Modify: `backend-dotnet/RuBackend/Business/BusinessEndpoints.cs`
- Modify: `backend-dotnet/RuBackend/Business/Settings/LoopbackRuntimeSettingsLoader.cs`
- Test: `backend-dotnet/RuBackend.Tests/Runtime/ProviderCredentialIsolationTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Runtime/StatusEndpointsTests.cs`

**Interfaces:**
- Consumes: existing `ISettingsStore`, `ICredentialStore`, and settings endpoint.
- Produces: `SettingsSnapshot.OpenAiModel`, `SettingsSnapshot.AnthropicModel`; backend names `OpenAI` and `Anthropic`; public fields `has_openai_api_key`, `has_anthropic_api_key`; accepted secret inputs `openai_api_key`, `anthropic_api_key`.

- [ ] **Step 1: Add failing credential-isolation and public-redaction tests**

Add cases proving each backend reads/writes only its canonical account and that
public settings expose presence flags but empty secret strings:

```csharp
[Theory]
[InlineData("OpenAI", "openai_api_key")]
[InlineData("Anthropic", "anthropic_api_key")]
public async Task Official_provider_writes_only_its_canonical_account(
    string backend, string account)
{
    var payload = new JsonObject
    {
        ["backend_choice"] = backend.ToLowerInvariant(),
        [account] = "synthetic-secret",
    };
    Assert.True((await Service().ApplyAsync(payload, default)).Success);
    Assert.Equal([account], _credentials.SetAccounts);
}

[Fact]
public async Task Public_settings_redact_official_provider_keys()
{
    await _credentials.SetAsync("Ru", "openai_api_key", "synthetic-openai", default);
    await _credentials.SetAsync("Ru", "anthropic_api_key", "synthetic-anthropic", default);
    var json = await GetSettingsJsonAsync();
    Assert.True(json["has_openai_api_key"]!.GetValue<bool>());
    Assert.True(json["has_anthropic_api_key"]!.GetValue<bool>());
    Assert.Equal("", json["openai_api_key"]!.GetValue<string>());
    Assert.Equal("", json["anthropic_api_key"]!.GetValue<string>());
    Assert.DoesNotContain("synthetic-", json.ToJsonString());
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~ProviderCredentialIsolationTests|FullyQualifiedName~StatusEndpointsTests"
```

Expected: FAIL because the new backend aliases, fields, and canonical accounts
do not exist.

- [ ] **Step 3: Implement settings fields and canonical account updates**

Extend `SettingsSnapshot` with `OpenAiModel` and `AnthropicModel`. Persist only
`openai_model` and `anthropic_model`; keep both secret names in
`SettingsStore.SecretFields` so they are never written to settings JSON.

In `SettingsTransitionService`, add:

```csharp
private const string OpenAiAccount = "openai_api_key";
private const string AnthropicAccount = "anthropic_api_key";
private const string BackendOpenAi = "OpenAI";
private const string BackendAnthropic = "Anthropic";
```

Normalize only `openai`/`openai api` to `OpenAI` and
`anthropic`/`anthropic claude`/`claude` to `Anthropic`. Add each secret directly
to its own account regardless of the selected backend, matching existing
explicit named-field behavior. Do not map legacy `api_key` to either new
provider. Empty strings continue to mean retain the existing secret.

Add `openai_model`, `anthropic_model`, `openai_api_key`, and
`anthropic_api_key` to runtime-load detection. Return only redacted key fields
and presence booleans from `BusinessEndpoints`.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the command from Step 2. Expected: PASS with no secret values in output.

- [ ] **Step 5: Commit only Task 1 files**

```powershell
git add -- backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs backend-dotnet/RuBackend/Business/Settings/SettingsStore.cs backend-dotnet/RuBackend/Business/Settings/SettingsTransitionService.cs backend-dotnet/RuBackend/Business/BusinessEndpoints.cs backend-dotnet/RuBackend/Business/Settings/LoopbackRuntimeSettingsLoader.cs backend-dotnet/RuBackend.Tests/Runtime/ProviderCredentialIsolationTests.cs backend-dotnet/RuBackend.Tests/Runtime/StatusEndpointsTests.cs
git commit -m "feat(providers): persist official provider settings"
```

### Task 2: Add exact capability registry and authenticated catalogs

**Files:**
- Create: `backend-dotnet/RuBackend/Providers/ProviderCapabilityRegistry.cs`
- Create: `backend-dotnet/RuBackend/Providers/ProviderCatalogRequestFactory.cs`
- Modify: `backend-dotnet/RuBackend/Providers/ProviderSelection.cs`
- Modify: `backend-dotnet/RuBackend/Providers/ProviderCatalogModels.cs`
- Modify: `backend-dotnet/RuBackend/Providers/ProviderCatalogParser.cs`
- Modify: `backend-dotnet/RuBackend/Providers/ProviderCatalogService.cs`
- Test: `backend-dotnet/RuBackend.Tests/Providers/ProviderCatalogTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Providers/ProviderCatalogEndpointTests.cs`

**Interfaces:**
- Consumes: Task 1 settings fields and canonical accounts.
- Produces: `ProviderCapabilityRegistry.TryGet(alias, modelId, out capability)`, `ProviderCatalogRequestFactory.Create(selection, credential)`, and `ProviderSelection.Protocol`/exact endpoint URIs.

- [ ] **Step 1: Add failing selection, registry, filtering, and header tests**

Use synthetic catalog bodies containing one registered model and one unknown
model. Assert the unknown ID is not selectable and no generation endpoint is
called:

```csharp
[Theory]
[InlineData("OpenAI", "openai", "openai_responses", "openai_api_key",
    "https://api.openai.com/v1/models", "https://api.openai.com/v1/responses")]
[InlineData("Anthropic", "anthropic", "anthropic_messages", "anthropic_api_key",
    "https://api.anthropic.com/v1/models", "https://api.anthropic.com/v1/messages")]
public async Task Official_selection_is_fixed_and_canonical(
    string backend, string alias, string protocol, string account,
    string models, string completion)
{
    var settings = (await SettingsAsync()) with { Backend = backend };
    var selected = ProviderSelection.From(settings);
    Assert.Equal(alias, selected.Alias);
    Assert.Equal(protocol, selected.Protocol);
    Assert.Equal(account, selected.Account);
    Assert.Equal(models, selected.ModelsUri.AbsoluteUri);
    Assert.Equal(completion, selected.CompletionUri.AbsoluteUri);
}

[Fact]
public async Task OpenAi_catalog_uses_bearer_and_hides_unknown_models()
{
    _handler.Json = """{"data":[{"id":"gpt-5.6-sol"},{"id":"future-image-model"}]}""";
    var snapshot = await Service().GetAsync(OpenAiSettings(), true, default);
    Assert.Equal(["gpt-5.6-sol"], snapshot.Models);
    Assert.Equal("Bearer synthetic-key-1", _handler.Authorization);
    Assert.Null(_handler.AnthropicVersion);
}

[Fact]
public async Task Anthropic_catalog_uses_native_headers_and_hides_unknown_models()
{
    _handler.Json = """{"data":[{"id":"claude-sonnet-5"},{"id":"unknown-model"}]}""";
    var snapshot = await Service().GetAsync(AnthropicSettings(), true, default);
    Assert.Equal(["claude-sonnet-5"], snapshot.Models);
    Assert.Equal("synthetic-key-1", _handler.ApiKey);
    Assert.Equal(ProviderCatalogRequestFactory.AnthropicVersion, _handler.AnthropicVersion);
    Assert.Null(_handler.Authorization);
}
```

- [ ] **Step 2: Run catalog tests and verify RED**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~ProviderCatalog"
```

Expected: FAIL because official selections, headers, and capability intersection
are absent.

- [ ] **Step 3: Implement exact provider selections and request factory**

`ProviderSelection.From` must return fixed official URIs and the protocol
constants from the File Structure section. `ProviderCatalogRequestFactory`
creates exactly one GET request:

```csharp
public static HttpRequestMessage Create(ProviderSelection selection, string credential)
{
    var request = new HttpRequestMessage(HttpMethod.Get, selection.ModelsUri);
    if (selection.Alias == "openai")
        request.Headers.Authorization = new("Bearer", RequireCredential(credential));
    else if (selection.Alias == "anthropic")
    {
        request.Headers.TryAddWithoutValidation("x-api-key", RequireCredential(credential));
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
    }
    else if (selection.Alias == "custom" && credential.Length != 0)
        request.Headers.Authorization = new("Bearer", credential);
    return request;
}
```

Set `AnthropicVersion` to the stable official API version `2023-06-01` and pin
the literal in tests. Do not read it from an environment variable or settings.

- [ ] **Step 4: Implement the source-dated exact capability registry**

Set `ProviderCapabilityRegistry.SourceDate` to `2026-09-01`. Include this exact
initial selectable baseline only when returned by the live account catalog:

```text
OpenAI / openai_responses:
  gpt-5.6-sol, gpt-5.6-terra, gpt-5.6-luna
  efforts: none, low, medium, high, xhigh, max

Anthropic / anthropic_messages:
  claude-fable-5, claude-opus-5, claude-opus-4-8, claude-sonnet-5
  efforts: low, medium, high, xhigh, max; adaptive thinking; max output 128000
  claude-sonnet-4-6
  efforts: low, medium, high, max; adaptive thinking; max output 64000
  claude-haiku-4-5-20251001
  efforts: none exposed (Provider default only); no adaptive override; max output 64000
```

Registry reasoning overlays are native and protected:

```csharp
// OpenAI explicit effort
new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = effort } };

// Anthropic adaptive model explicit effort
new JsonObject
{
    ["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "omitted" },
    ["output_config"] = new JsonObject { ["effort"] = effort },
};
```

Keep Provider default outside the registry overlay map so it always resolves to
an empty options object. Exact model IDs only; do not add aliases unless the
official model page identifies that exact alias and a test pins it.

- [ ] **Step 5: Intersect parsed models with capabilities**

Keep current Kilo/Zen/Custom parsing behavior. For `openai` and `anthropic`,
parse the bounded `data[].id`, then project registered entries as supported with
their exact protocol/reasoning. Keep unknown entries only in internal parse
results as `Supported=false`; `ProviderCatalogSnapshot.Models` must contain only
supported IDs, and GUI-visible catalog data may include unsupported entries but
must never select them.

Move provider-specific header construction out of `ProviderCatalogService` into
the request factory. Missing official credentials map to
`catalog_unauthorized` without sending HTTP.

- [ ] **Step 6: Run focused catalog tests and verify GREEN**

Run the command from Step 2. Expected: PASS, including cache/throttle/single-flight
tests already present.

- [ ] **Step 7: Commit only Task 2 files**

```powershell
git add -- backend-dotnet/RuBackend/Providers/ProviderCapabilityRegistry.cs backend-dotnet/RuBackend/Providers/ProviderCatalogRequestFactory.cs backend-dotnet/RuBackend/Providers/ProviderSelection.cs backend-dotnet/RuBackend/Providers/ProviderCatalogModels.cs backend-dotnet/RuBackend/Providers/ProviderCatalogParser.cs backend-dotnet/RuBackend/Providers/ProviderCatalogService.cs backend-dotnet/RuBackend.Tests/Providers/ProviderCatalogTests.cs backend-dotnet/RuBackend.Tests/Providers/ProviderCatalogEndpointTests.cs
git commit -m "feat(providers): discover official model catalogs"
```

### Task 3: Pin protocol and output cap in the protected provider snapshot

**Files:**
- Create: `backend-dotnet/RuAgent.Runtime.Adapters/Bootstrap/ProviderProtocols.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Adapters/Bootstrap/ProtectedProviderBootstrap.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Adapters/Bootstrap/ProviderRequestOptionsValidator.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Adapters/Models/ModelRequest.cs`
- Modify: `backend-dotnet/RuBackend/Providers/AgentProviderSnapshotFactory.cs`
- Modify: `backend-dotnet/RuBackend/Runtime/ManagedProviderBootstrapFactory.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Worker.Tests/ManagedWorkerCompositionTests.cs`
- Test: `backend-dotnet/RuAgent.Runtime.Adapters.Tests/ProtectedProviderBootstrapTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Providers/AgentProviderSnapshotTests.cs`
- Test: `backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV2StepServiceTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Worker/WorkerV2ClientTests.cs`

**Interfaces:**
- Consumes: Task 2 `ProviderSelection.Protocol` and capability max output.
- Produces: protected `Protocol`, `MaximumOutputTokens`, and native reasoning options; `ModelRequest.MaximumOutputTokens`.

- [ ] **Step 1: Add failing bootstrap round-trip and run-pinning tests**

```csharp
[Fact]
public void Protected_bootstrap_round_trips_protocol_and_output_cap()
{
    var data = new ProviderBootstrapData(
        "Anthropic", "anthropic_messages", new Uri("https://api.anthropic.com/v1/messages"),
        "claude-sonnet-5", "synthetic", new JsonObject
        {
            ["output_config"] = new JsonObject { ["effort"] = "high" },
            ["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "omitted" },
        }, 8192);
    var restored = ProtectedProviderBootstrap.ReadAndDelete(_root, ProtectedProviderBootstrap.Write(_root, data));
    Assert.Equal("anthropic_messages", restored.Protocol);
    Assert.Equal(8192, restored.MaximumOutputTokens);
}
```

Add snapshot tests proving OpenAI has no output cap, Anthropic gets 8192, and a
missing/unsupported model or missing official key throws before writing a
handoff.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Runtime.Adapters.Tests/RuAgent.Runtime.Adapters.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~ProtectedProviderBootstrapTests"
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentProviderSnapshotTests"
```

Expected: FAIL because the protected payload lacks protocol/output cap and the
current request-options validator rejects Anthropic native overlays.

- [ ] **Step 3: Extend and validate the protected contract**

Add `ProviderProtocols.Require` in its focused file and accept only the three
constants. Make protocol a required `ProviderBootstrapData` constructor
argument; update every constructor call in production and tests so no implicit
Chat Completions default exists. Add `MaximumOutputTokens` validation: null or integer
`1..128000`; Chat Completions and OpenAI Responses require null; Anthropic
Messages requires a non-null value no greater than 8192.

Change the validator entry point to
`ValidateAndClone(string protocol, JsonObject? requestOptions, string parameterName)`
and accept exactly one native shape by protocol:

- Chat Completions: existing `reasoning_effort` or `reasoning` allowlist.
- OpenAI Responses: only `reasoning.effort` with an allowed effort.
- Anthropic Messages: exactly `output_config.effort`, optionally accompanied by
  `thinking: {type:"adaptive", display:"omitted"}`; no other fields.
- Empty options normalize to null. Unknown fields or mixed protocol shapes throw.

Serialize `protocol` and `maximum_output_tokens` inside DPAPI. Increase no byte
caps unless a focused test demonstrates the existing 16 KiB cap is insufficient.
The neutral startup bootstrap uses `chat_completions` and no output cap; it must
remain credential-free and unusable for a real per-step generation.

- [ ] **Step 4: Pin registry capability in `AgentProviderSnapshotFactory`**

After catalog identity/model/reasoning validation, require the exact capability
for the selected alias/model. Build:

```csharp
var maximumOutput = selected.Protocol == ProviderProtocols.AnthropicMessages
    ? Math.Min(capability.DocumentedMaxOutputTokens!.Value, 8192)
    : (int?)null;
snapshot = new ProviderBootstrapData(
    selected.Backend, selected.Protocol, selected.CompletionUri,
    selected.Model, key, options, maximumOutput);
```

Reject an empty official credential before writing. Do not change local/custom
credential rules.

- [ ] **Step 5: Pass the output cap into `ModelRequest` and verify GREEN**

Add the constructor/property to `ModelRequest`. `WorkerV2StepService` copies the
cap from the consumed per-step bootstrap. Update constructor calls in
`ManagedWorkerCompositionTests`, `WorkerV2StepServiceTests`,
`WorkerV2ClientTests`, and `ProtectedProviderBootstrapTests` with the intended
exact protocol. Then verify no old call shape remains:

```powershell
rg -n "new ProviderBootstrapData\(" backend-dotnet
```

Review every match; each call must contain an explicit protocol argument. Run
both commands from Step 2 plus:

```powershell
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~WorkerV2StepServiceTests"
```

Expected: PASS.

- [ ] **Step 6: Commit only Task 3 files**

```powershell
git add -- backend-dotnet/RuAgent.Runtime.Adapters/Bootstrap/ProviderProtocols.cs backend-dotnet/RuAgent.Runtime.Adapters/Bootstrap/ProtectedProviderBootstrap.cs backend-dotnet/RuAgent.Runtime.Adapters/Bootstrap/ProviderRequestOptionsValidator.cs backend-dotnet/RuAgent.Runtime.Adapters/Models/ModelRequest.cs backend-dotnet/RuBackend/Providers/AgentProviderSnapshotFactory.cs backend-dotnet/RuBackend/Runtime/ManagedProviderBootstrapFactory.cs backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs backend-dotnet/RuAgent.Runtime.Adapters.Tests/ProtectedProviderBootstrapTests.cs backend-dotnet/RuBackend.Tests/Providers/AgentProviderSnapshotTests.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/ManagedWorkerCompositionTests.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV2StepServiceTests.cs backend-dotnet/RuBackend.Tests/Worker/WorkerV2ClientTests.cs
git commit -m "feat(providers): pin native protocol per run"
```

### Task 4: Add bounded SSE framing and native OpenAI Responses adapter

**Files:**
- Create: `backend-dotnet/RuAgent.Runtime.Adapters/Models/BoundedSseReader.cs`
- Create: `backend-dotnet/RuAgent.Runtime.Adapters/Models/OpenAiResponsesModelProvider.cs`
- Create: `backend-dotnet/RuAgent.Runtime.Adapters.Tests/BoundedSseReaderTests.cs`
- Create: `backend-dotnet/RuAgent.Runtime.Adapters.Tests/OpenAiResponsesModelProviderTests.cs`

**Interfaces:**
- Consumes: existing `IModelProvider`, `ModelRequest`, `ModelDelta`, and Task 3 native OpenAI reasoning shape.
- Produces: `BoundedSseReader.ReadAsync(Stream, CancellationToken)` and `OpenAiResponsesModelProvider.GenerateAsync`.

- [ ] **Step 1: Add failing bounded SSE tests**

Cover CRLF/LF event framing, `event:` plus multi-line `data:`, comments, strict
UTF-8, a 256 KiB line/event cap, 5 MiB total cap, cancellation, and EOF with an
unfinished event. The reader returns:

```csharp
public sealed record ServerSentEvent(string? Event, string Data);
```

Malformed UTF-8 and cap violations throw `ModelProviderException` with
`malformed_payload` and `response_too_large` respectively.

- [ ] **Step 2: Run SSE tests and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Runtime.Adapters.Tests/RuAgent.Runtime.Adapters.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~BoundedSseReaderTests"
```

Expected: FAIL because the reader does not exist.

- [ ] **Step 3: Implement the minimal bounded SSE reader**

Use `StreamReader` with `new UTF8Encoding(false, true)`. Count UTF-8 bytes before
concatenating data lines. Ignore comment lines beginning with `:`. Reject an
event with no data only when the provider parser requires data; the framing
reader itself may return an empty data string.

- [ ] **Step 4: Add failing OpenAI request/stream tests**

Capture the outbound JSON and assert:

```json
{
  "model":"gpt-5.6-sol",
  "instructions":"system text",
  "input":[
    {"role":"user","content":"old question"},
    {"role":"assistant","content":"old answer"},
    {"role":"user","content":"current question"}
  ],
  "stream":true,
  "store":false,
  "reasoning":{"effort":"high"}
}
```

Assert Provider default omits `reasoning`; `model_tools`, Chat Completions
`messages`, `temperature`, `top_k`, and output-token settings are absent. Assert
only `Authorization: Bearer synthetic-openai` is added.

Stream fixtures must cover `response.output_text.delta`, documented progress
events, `response.completed`, provider `error`, `response.failed`, incomplete
EOF, malformed JSON, duplicate terminal events, 401/403/429/5xx, cancellation,
and response caps. Assert the completed response model exactly matches the
requested registry ID; a changed model is `malformed_payload`, not accepted as a
fallback. Only output-text deltas become `ModelDelta.Text`.

- [ ] **Step 5: Run OpenAI tests and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Runtime.Adapters.Tests/RuAgent.Runtime.Adapters.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~OpenAiResponsesModelProviderTests"
```

Expected: FAIL because the adapter does not exist.

- [ ] **Step 6: Implement `OpenAiResponsesModelProvider` and verify GREEN**

Build JSON with `System.Text.Json.Nodes`; deep-clone only the validated
`reasoning` object. Use `HttpCompletionOption.ResponseHeadersRead`. Reuse the
current stable status mapping (`auth_failed`, `forbidden`, `rate_limited`,
`server_error`, `http_error`, `timeout`, `transport_error`) without exposing
provider bodies. Map a successful completed stream to finish reason `stop`; map
documented output-limit completion to `length`; reject all contradictory
terminal state.

Run both Task 4 test commands. Expected: PASS.

- [ ] **Step 7: Commit only Task 4 files**

```powershell
git add -- backend-dotnet/RuAgent.Runtime.Adapters/Models/BoundedSseReader.cs backend-dotnet/RuAgent.Runtime.Adapters/Models/OpenAiResponsesModelProvider.cs backend-dotnet/RuAgent.Runtime.Adapters.Tests/BoundedSseReaderTests.cs backend-dotnet/RuAgent.Runtime.Adapters.Tests/OpenAiResponsesModelProviderTests.cs
git commit -m "feat(providers): add OpenAI Responses adapter"
```

### Task 5: Add native Anthropic Messages adapter

**Files:**
- Create: `backend-dotnet/RuAgent.Runtime.Adapters/Models/AnthropicMessagesModelProvider.cs`
- Create: `backend-dotnet/RuAgent.Runtime.Adapters.Tests/AnthropicMessagesModelProviderTests.cs`

**Interfaces:**
- Consumes: Task 3 `MaximumOutputTokens`, validated Anthropic options, and Task 4 `BoundedSseReader`.
- Produces: `AnthropicMessagesModelProvider.GenerateAsync`.

- [ ] **Step 1: Add failing Anthropic request tests**

Capture and assert the native body:

```json
{
  "model":"claude-sonnet-5",
  "system":"system text",
  "messages":[
    {"role":"user","content":"old question"},
    {"role":"assistant","content":"old answer"},
    {"role":"user","content":"current question"}
  ],
  "stream":true,
  "max_tokens":8192,
  "thinking":{"type":"adaptive","display":"omitted"},
  "output_config":{"effort":"high"}
}
```

Provider default omits `thinking` and `output_config` but keeps `max_tokens`.
Null, zero, negative, or greater-than-8192 caps fail before HTTP. Assert only
`x-api-key: synthetic-anthropic`, pinned `anthropic-version`, and JSON content
headers are sent; no Bearer header.

- [ ] **Step 2: Add failing Anthropic stream/error tests**

Fixtures cover the exact lifecycle:

```text
message_start -> content_block_start(text) -> content_block_delta(text_delta)
-> content_block_stop -> message_delta(stop_reason=end_turn) -> message_stop
```

Also cover ping, thinking/signature deltas, unknown future event types,
`max_tokens` stop reason, `error` events including `overloaded_error`, HTTP 401,
403, 429, 529/5xx, malformed order/index/type/JSON, incomplete EOF,
cancellation, and byte caps. Assert thinking text never appears in emitted
`ModelDelta` values or exception messages. Assert `message_start.message.model`
exactly matches the selected model. Reject an Anthropic `fallback` content block
or changed model with a safe `malformed_payload` result; never continue rendering
a provider-selected replacement model.

- [ ] **Step 3: Run Anthropic tests and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Runtime.Adapters.Tests/RuAgent.Runtime.Adapters.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AnthropicMessagesModelProviderTests"
```

Expected: FAIL because the adapter does not exist.

- [ ] **Step 4: Implement native Messages mapping and parser**

Use the top-level system field and user/assistant messages only. Emit text from
`content_block_delta.delta.type == "text_delta"`. Ignore `ping`, unknown future
event names, thinking/signature blocks, and non-visible content blocks only when
their outer event structure is valid and the block is not `fallback`. A
stream-level Anthropic error maps to a
stable safe code; never include `error.message`.

Map `end_turn` and `stop_sequence` to `stop`, `max_tokens` to `length`, and reject
`tool_use` because provider-native tools are not enabled. Complete only after a
valid `message_stop` following one valid terminal `message_delta`.

- [ ] **Step 5: Run Anthropic tests and verify GREEN**

Run the command from Step 3. Expected: PASS.

- [ ] **Step 6: Commit only Task 5 files**

```powershell
git add -- backend-dotnet/RuAgent.Runtime.Adapters/Models/AnthropicMessagesModelProvider.cs backend-dotnet/RuAgent.Runtime.Adapters.Tests/AnthropicMessagesModelProviderTests.cs
git commit -m "feat(providers): add Anthropic Messages adapter"
```

### Task 6: Dispatch the exact pinned protocol in the managed Worker

**Files:**
- Create: `backend-dotnet/RuAgent.Runtime.Adapters/Models/IModelProviderResolver.cs`
- Create: `backend-dotnet/RuAgent.Runtime.Adapters/Models/ModelProviderResolver.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Worker/ManagedWorkerComposition.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Worker/Program.cs`
- Test: `backend-dotnet/RuAgent.Runtime.Worker.Tests/ManagedWorkerCompositionTests.cs`
- Test: `backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV2StepServiceTests.cs`

**Interfaces:**
- Consumes: Task 3 protocol bootstrap and Tasks 4-5 providers.
- Produces: exact `IModelProviderResolver.Resolve(protocol)` dispatch.

- [ ] **Step 1: Add failing resolver and Worker dispatch tests**

```csharp
[Theory]
[InlineData("chat_completions", typeof(OpenAiCompatibleModelProvider))]
[InlineData("openai_responses", typeof(OpenAiResponsesModelProvider))]
[InlineData("anthropic_messages", typeof(AnthropicMessagesModelProvider))]
public void Resolver_returns_only_exact_protocol(string protocol, Type expected) =>
    Assert.IsType(expected, Resolver().Resolve(protocol));

[Fact]
public void Resolver_rejects_unknown_protocol_without_fallback() =>
    Assert.Throws<ArgumentException>(() => Resolver().Resolve("responses-ish"));
```

In `WorkerV2StepServiceTests`, supply three recording providers and prove one
per-step bootstrap invokes exactly one matching provider. Make the selected
provider throw and assert the other two remain at zero calls.

- [ ] **Step 2: Run focused Worker tests and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~ManagedWorkerCompositionTests|FullyQualifiedName~WorkerV2StepServiceTests"
```

Expected: FAIL because composition still creates one OpenAI-compatible provider.

- [ ] **Step 3: Implement exact resolver and preserve test seams**

`ModelProviderResolver` accepts a fixed dictionary and validates all three keys
once. `Resolve` uses an ordinal exact lookup and throws for unknown values.

Add `IModelProviderResolver? ModelProviderResolver` to
`ManagedWorkerCompositionDependencies`. Preserve the existing
`IModelProvider? ModelProvider` test seam by wrapping it in a fixed resolver for
all three known protocols only in tests/dependency injection; production must
construct one instance of each real adapter and register exact protocols.

Share one owned `HttpClient` if lifecycle and handler behavior remain identical;
do not let adapters mutate `DefaultRequestHeaders`. Each adapter adds headers to
its own request.

- [ ] **Step 4: Resolve after consuming the per-step bootstrap**

Change `WorkerV2StepService` to call:

```csharp
var provider = _providers.Resolve(providerBootstrap.Protocol);
await foreach (var delta in provider.GenerateAsync(request, cancellationToken))
{
    // existing bounded accumulation and completion handling
}
```

The startup bootstrap never supplies a usable generation fallback. A missing,
invalid, or unknown per-step protocol maps to the existing bounded worker error
before provider HTTP.

- [ ] **Step 5: Run Worker and adapter project tests sequentially**

```powershell
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj -c Release --no-restore --nologo
dotnet test backend-dotnet/RuAgent.Runtime.Adapters.Tests/RuAgent.Runtime.Adapters.Tests.csproj -c Release --no-restore --nologo
```

Expected: both projects PASS.

- [ ] **Step 6: Commit only Task 6 files**

```powershell
git add -- backend-dotnet/RuAgent.Runtime.Adapters/Models/IModelProviderResolver.cs backend-dotnet/RuAgent.Runtime.Adapters/Models/ModelProviderResolver.cs backend-dotnet/RuAgent.Runtime.Worker/ManagedWorkerComposition.cs backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs backend-dotnet/RuAgent.Runtime.Worker/Program.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/ManagedWorkerCompositionTests.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV2StepServiceTests.cs
git commit -m "feat(providers): dispatch pinned native protocols"
```

### Task 7: Add OpenAI and Claude controls to WinUI

**Files:**
- Modify: `gui-winui3/Models/Models.cs`
- Modify: `gui-winui3/Models/ProviderCatalogModels.cs`
- Modify: `gui-winui3/Services/ApiService.cs`
- Modify: `gui-winui3/Views/SettingsView.xaml`
- Modify: `gui-winui3/Views/SettingsView.xaml.cs`
- Modify: `gui-winui3/Views/SettingsView.Reasoning.cs`
- Test: `gui-winui3.Tests/SettingsDtoTests.cs`
- Test: `gui-winui3.Tests/ProviderCatalogTests.cs`
- Test: `gui-winui3.Tests/ResponsiveLayoutContractTests.cs`

**Interfaces:**
- Consumes: Task 1 public settings and Task 2 catalog contract.
- Produces: selector tags `openai`, `anthropic`; key fields and model-save fields for both providers.

- [ ] **Step 1: Add failing DTO and source-contract tests**

Assert JSON names and redacted placeholders:

```csharp
[Fact]
public void Official_provider_update_serializes_separate_keys()
{
    var json = JsonSerializer.Serialize(new ProviderSettingsUpdateDto
    {
        BackendChoice = "anthropic",
        OpenAiApiKey = "openai-only",
        AnthropicApiKey = "anthropic-only",
    });
    Assert.Contains("\"openai_api_key\":\"openai-only\"", json);
    Assert.Contains("\"anthropic_api_key\":\"anthropic-only\"", json);
}
```

Add XAML/source assertions for exactly two new ComboBox items and separate rows
named `OpenAiApiRow`/`AnthropicApiRow`. Assert retired Temperature, Top-K, and
Max New Tokens controls remain absent.

- [ ] **Step 2: Run GUI tests and verify RED**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~SettingsDtoTests|FullyQualifiedName~ProviderCatalogTests|FullyQualifiedName~ResponsiveLayoutContractTests"
```

Expected: FAIL because the DTO fields and controls do not exist.

- [ ] **Step 3: Extend DTOs and model-save routing**

Add `OpenAiApiKey`, `AnthropicApiKey`, `HasOpenAiKey`, `HasAnthropicKey`,
`OpenAiModel`, and `AnthropicModel` with the exact snake-case JSON names. Extend
`ModelSettingsUpdateDto` and the existing provider-to-model save switch; never
send another provider's model field during a model save.

- [ ] **Step 4: Add the two provider/key rows and visibility behavior**

Add selector labels exactly:

```xml
<ComboBoxItem Content="OpenAI (Official API)" Tag="openai"/>
<ComboBoxItem Content="Anthropic Claude (Official API)" Tag="anthropic"/>
```

Each `PasswordBox` starts empty and uses `Đã lưu (để trống để giữ nguyên)` when
the matching `has_*` flag is true. OpenAI placeholder is `sk-...`; Anthropic is
`sk-ant-...`. `UpdateVisibilityForBackend` shows only the matching key row;
cloud-memory consent is visible for `opencode`, `kilo`, `openai`, and
`anthropic`. Add both password-change handlers to the existing debounce.

`SaveSettingsAsync` sends each password box independently. Refresh the shared
catalog after a successful provider/key save through the existing controller;
do not create another polling timer or fallback model list.

- [ ] **Step 5: Verify model/reasoning UI uses supported catalog entries only**

Keep `ProviderCatalogController` as the state owner. Filter selectable models by
`Supported`; stale/error state offers no explicit reasoning choice. Ensure a
delayed prior-provider response cannot replace state after switching between
OpenAI and Anthropic. Provider default remains the first reasoning option.

- [ ] **Step 6: Run focused tests and WinUI Release build**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~SettingsDtoTests|FullyQualifiedName~ProviderCatalogTests|FullyQualifiedName~ResponsiveLayoutContractTests"
dotnet build gui-winui3/gui-winui3.csproj -c Release -p:Platform=x64 --no-restore --nologo
```

Expected: tests PASS and build exits 0 without XAML/compiler errors.

- [ ] **Step 7: Commit only Task 7 files**

```powershell
git add -- gui-winui3/Models/Models.cs gui-winui3/Models/ProviderCatalogModels.cs gui-winui3/Services/ApiService.cs gui-winui3/Views/SettingsView.xaml gui-winui3/Views/SettingsView.xaml.cs gui-winui3/Views/SettingsView.Reasoning.cs gui-winui3.Tests/SettingsDtoTests.cs gui-winui3.Tests/ProviderCatalogTests.cs gui-winui3.Tests/ResponsiveLayoutContractTests.cs
git commit -m "feat(gui): configure OpenAI and Claude providers"
```

### Task 8: Integrate, document, and run deterministic gates

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `docs/operations/provider-catalog.md`
- Modify: `docs/operations/verification.md`
- Modify: `docs/README.md` only if navigation needs a new link
- Modify: `TODO.md`
- Modify: `docs/superpowers/specs/2026-09-01-native-openai-anthropic-providers-design.md`
- Modify: `docs/superpowers/plans/2026-09-01-native-openai-anthropic-providers.md`
- Test: all affected test projects and repository documentation checks

**Interfaces:**
- Consumes: Tasks 1-7 complete working tree.
- Produces: evidence-backed repository state ready for separate user-authorized live catalog/generation acceptance.

- [ ] **Step 1: Run focused provider projects sequentially**

```powershell
dotnet test backend-dotnet/RuAgent.Runtime.Adapters.Tests/RuAgent.Runtime.Adapters.Tests.csproj -c Release --no-restore --nologo
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj -c Release --no-restore --nologo
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~Provider|FullyQualifiedName~StatusEndpoints|FullyQualifiedName~WorkerV2"
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --no-restore --nologo
```

Expected: every command exits 0. Stop and repair the first failure before
starting the next heavyweight suite.

- [ ] **Step 2: Run full managed and Python gates sequentially**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test_dotnet.ps1 -Configuration Release -NoRestore
.\.venv\Scripts\python.exe -m pytest -q
.\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
dotnet build gui-winui3/gui-winui3.csproj -c Release -p:Platform=x64 --no-restore --nologo
```

Expected: all suites/builds exit 0. Record exact passed/skipped counts rather
than copying historical numbers.

- [ ] **Step 3: Run privacy, documentation, and diff checks**

```powershell
.\.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q
rg -n "openai_api_key|anthropic_api_key|sk-ant-|synthetic-openai|synthetic-anthropic" logs docs/operations/verification.md -g '*.json' -g '*.jsonl' -g '*.md'
git diff --check
git status --short --branch
```

Expected: documentation tests pass; the credential scan finds only intentional
field names and explicitly synthetic test examples, never real values; diff
check exits 0. Review the final status and ensure no unrelated dirty file is
staged.

- [ ] **Step 4: Update current documents and remove only superseded claims**

Document:

- exact official endpoints, canonical accounts, and protocol dispatch;
- capability source date and exact initial model registry;
- five-minute cache, thirty-second throttle, credential-revision invalidation;
- OpenAI `store:false`, Anthropic internal 8192 cap, and native reasoning
  omission/serialization;
- deterministic test/build counts actually measured;
- no live provider, billing, GUI, voice, device, or quality claim.

Mark the design status as implemented only after all deterministic gates pass.
Update this plan's checkboxes/evidence and `TODO.md`. Delete a document only if
its entire content is superseded; otherwise edit it in place and preserve useful
history.

- [ ] **Step 5: Commit integration/docs only after reviewing staged scope**

```powershell
git add -- docs/architecture.md docs/operations/runtime-behavior.md docs/operations/provider-catalog.md docs/operations/verification.md docs/README.md TODO.md docs/superpowers/specs/2026-09-01-native-openai-anthropic-providers-design.md docs/superpowers/plans/2026-09-01-native-openai-anthropic-providers.md
git diff --cached --check
git diff --cached --name-only
git commit -m "docs: record native provider verification"
```

If `docs/README.md` did not change, omit it from `git add`. Do not stage by
directory or use `git add -A`.

## Separate acceptance requiring user authorization

These actions are deliberately not part of implementation:

1. Save a real OpenAI or Anthropic key in the GUI.
2. Refresh a live authenticated catalog and verify only registered Agent-capable
   models appear.
3. Send a live prompt, which may spend provider credits.
4. Publish or launch the GUI for interactive inspection.
5. Commit remaining unrelated work or push any commit.

Report implementation as deterministically verified until the user explicitly
authorizes each relevant live or publishing action.
