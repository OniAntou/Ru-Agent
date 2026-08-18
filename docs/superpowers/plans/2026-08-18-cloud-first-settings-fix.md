# Cloud-First Settings Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Ollama / LM Studio / Custom OpenAI selectable and savable end-to-end (GUI → .NET → worker → Python), migrate retired GGUF settings to OpenCode Zen, and make `/status` report real load failures.

**Architecture:** Extend `SettingsSnapshot` with six explicit fields (per-backend model + URL), route `custom_api_key` through the Windows Credential Manager like OpenCode/Kilo, accept the three backends in the worker's `runtime_load` via a pure parameter-mapping helper, map `gguf`/`llama.cpp` to OpenCode Zen at every settings boundary, and track load errors on `ModelRunner` for honest status reporting.

**Tech Stack:** .NET 8+ (RuBackend, xUnit), C# WinUI3 (gui-winui3), Python 3 (ru_runtime), pytest, Windows PowerShell 5.1.

## Global Constraints

- Backend canonical names (verbatim): `"OpenCode Zen"`, `"Kilo Gateway"`, `"Ollama"`, `"LM Studio"`, `"Custom OpenAI"`; wire aliases `opencode` / `kilo` / `ollama` / `lmstudio` / `custom` / `gguf`.
- Credential accounts: `api_key` (OpenCode), `kilo_gateway_api_key`, `gemini_vision_api_key`, `custom_api_key` (new). Service name `"Ru"`.
- Settings file keys (verbatim): `ollama_model`, `lmstudio_model`, `custom_model`, `ollama_url`, `lmstudio_url`, `custom_url`, `custom_api_key`.
- Defaults (must match `_DEFAULT_SETTINGS` in `runtime_services.py`): `ollama_model=qwen2.5:latest`, `lmstudio_model=default`, `custom_model=default`, `ollama_url=http://localhost:11434/v1`, `lmstudio_url=http://localhost:1234/v1`, `custom_url=http://localhost:8000/v1`.
- Do NOT remove `llama_cpp.py`, the GGUF branch in `runner.load`, or `BACKEND_GGUF` — they stay dormant.
- Test commands (PowerShell):
  - Python: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/<file> -q`
  - .NET: `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo`
  - GUI: `dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo`
- All steps on Windows PowerShell 5.1; use `;` between commands, never `&&`.

---

### Task 1: SettingsSnapshot fields + SettingsStore persistence + GGUF read migration

**Files:**
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs`
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsStore.cs`
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsTransitionService.cs:205-234` (constructor call only)
- Modify: `backend-dotnet/RuBackend.Tests/Worker/RuntimeSettingsLoaderTests.cs:91-111` (constructor call only)
- Modify: `backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs:385-405` (constructor call only)
- Test: `backend-dotnet/RuBackend.Tests/Business/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: existing `SettingsSnapshot` record, `SettingsStore`, `DataPaths`.
- Produces: `SettingsSnapshot` with six new trailing fields in this exact order: `OllamaModel`, `LmStudioModel`, `CustomModel`, `OllamaUrl`, `LmStudioUrl`, `CustomUrl`. `SettingsStore.ReadAsync` returns `Backend == "OpenCode Zen"` for files whose `backend` is `gguf`/`llama.cpp` (with `OpenCodeModel` NOT inherited from legacy `model`), strips `custom_api_key` from `Raw` and from persisted output.

- [ ] **Step 1: Write the failing tests** — append to `SettingsStoreTests.cs`:

```csharp
[Fact]
public async Task Read_ollama_settings_populates_the_new_snapshot_fields()
{
    var (store, paths) = CreateStore();
    Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
    await File.WriteAllTextAsync(paths.SettingsFile, """
        {
          "backend": "ollama",
          "ollama_model": "qwen3:8b",
          "ollama_url": "http://127.0.0.1:11434/v1",
          "lmstudio_model": "local-model",
          "custom_url": "http://10.0.0.5:8000/v1"
        }
        """);

    var snapshot = await store.ReadAsync(CancellationToken.None);

    Assert.Equal("Ollama", snapshot.Backend);
    Assert.Equal("qwen3:8b", snapshot.OllamaModel);
    Assert.Equal("http://127.0.0.1:11434/v1", snapshot.OllamaUrl);
    Assert.Equal("local-model", snapshot.LmStudioModel);
    Assert.Equal("http://10.0.0.5:8000/v1", snapshot.CustomUrl);
}

[Fact]
public async Task Save_persists_the_new_backend_fields_and_strips_custom_api_key()
{
    var (store, paths) = CreateStore();
    Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
    await File.WriteAllTextAsync(paths.SettingsFile, """
        {
          "backend": "custom",
          "custom_api_key": "must-not-survive"
        }
        """);
    var current = await store.ReadAsync(CancellationToken.None);

    Assert.Null(current.Raw["custom_api_key"]);

    await store.SaveAsync(
        current with
        {
            Backend = "Custom OpenAI",
            CustomModel = "my-model",
            CustomUrl = "http://localhost:9000/v1",
            OllamaUrl = "http://localhost:11434/v1",
        },
        CancellationToken.None);

    var persisted = JsonNode.Parse(await File.ReadAllTextAsync(paths.SettingsFile))!.AsObject();
    Assert.Equal("Custom OpenAI", persisted["backend"]!.GetValue<string>());
    Assert.Equal("my-model", persisted["custom_model"]!.GetValue<string>());
    Assert.Equal("http://localhost:9000/v1", persisted["custom_url"]!.GetValue<string>());
    Assert.Null(persisted["custom_api_key"]);
}

[Fact]
public async Task Read_retired_gguf_backend_migrates_to_opencode_without_legacy_model()
{
    var (store, paths) = CreateStore();
    Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
    await File.WriteAllTextAsync(paths.SettingsFile, """
        {
          "backend": "llama.cpp",
          "model": "LFM2.5-2.6B-Q4_K_M.gguf"
        }
        """);

    var snapshot = await store.ReadAsync(CancellationToken.None);

    Assert.Equal("OpenCode Zen", snapshot.Backend);
    Assert.Equal(string.Empty, snapshot.OpenCodeModel);
    Assert.Equal("LFM2.5-2.6B-Q4_K_M.gguf", snapshot.Model);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: compile error — `SettingsSnapshot` has no members `OllamaModel`, `OllamaUrl`, etc.

- [ ] **Step 3: Add the six fields to the record** — `SettingsSnapshot.cs`, after `ShowSubtitles`:

```csharp
public sealed record SettingsSnapshot(
    JsonObject Raw,
    string Backend,
    string Model,
    string OpenCodeModel,
    string KiloModel,
    string ApiVariant,
    int MaxTokens,
    double Temperature,
    int TopK,
    bool AllowWebSearch,
    bool EnableSelfLearning,
    bool ShareMemoryWithCloud,
    bool KiloReasoningEnabled,
    string CheckpointPath,
    string VoiceLabel,
    string VoiceStyleId,
    double VoiceSpeed,
    bool AutoPaceEnabled,
    bool AutoReadReplies,
    bool ShowSubtitles,
    string OllamaModel,
    string LmStudioModel,
    string CustomModel,
    string OllamaUrl,
    string LmStudioUrl,
    string CustomUrl);
```

- [ ] **Step 4: Fix the three other construction sites** (compile the test project to find them all; there are exactly three):

`RuntimeSettingsLoaderTests.cs` `Snapshot(...)` — append before the closing paren:
```csharp
        "qwen2.5:latest",
        "default",
        "default",
        "http://localhost:11434/v1",
        "http://localhost:1234/v1",
        "http://localhost:8000/v1");
```

`ConversationCoordinatorTests.cs` `FakeSettingsStore.ReadAsync` — append the same six defaults before the closing paren.

`SettingsTransitionService.cs` `BuildCandidate` — append before the closing paren:
```csharp
            current.OllamaModel,
            current.LmStudioModel,
            current.CustomModel,
            current.OllamaUrl,
            current.LmStudioUrl,
            current.CustomUrl);
```
(Task 2 replaces the `current.*` values with payload parsing.)

- [ ] **Step 5: Implement store read/write/validate + migration** — `SettingsStore.cs`:

In `CredentialFields`, add `"custom_api_key"`:
```csharp
    private static readonly string[] CredentialFields =
    [
        "api_key",
        "opencode_api_key",
        "kilo_api_key",
        "gemini_vision_api_key",
        "custom_api_key",
    ];
```

In `SaveAsync`, after `persisted["show_subtitles"] = ...`:
```csharp
            persisted["ollama_model"] = candidate.OllamaModel;
            persisted["lmstudio_model"] = candidate.LmStudioModel;
            persisted["custom_model"] = candidate.CustomModel;
            persisted["ollama_url"] = candidate.OllamaUrl;
            persisted["lmstudio_url"] = candidate.LmStudioUrl;
            persisted["custom_url"] = candidate.CustomUrl;
```

In `ReadUnlockedAsync`, replace the backend/model block:
```csharp
        var rawBackend = ReadString(raw, "backend", string.Empty);
        var migratedFromGguf = rawBackend is "gguf" or "llama.cpp";
        var backend = NormalizeBackend(rawBackend);
        var model = ReadString(raw, "model", string.Empty);
        var openCodeModel = ReadString(raw, "opencode_model", string.Empty);
        var kiloModel = ReadString(raw, "kilo_model", string.Empty);
        if (backend == BackendOpenCode && string.IsNullOrWhiteSpace(openCodeModel) && !migratedFromGguf)
        {
            openCodeModel = model;
        }
        else if (backend == BackendKilo && string.IsNullOrWhiteSpace(kiloModel))
        {
            kiloModel = model;
        }
```

Add to the `new SettingsSnapshot(...)` call, after `ReadBoolean(raw, "show_subtitles", false)`:
```csharp
            ReadString(raw, "ollama_model", "qwen2.5:latest"),
            ReadString(raw, "lmstudio_model", "default"),
            ReadString(raw, "custom_model", "default"),
            ReadString(raw, "ollama_url", "http://localhost:11434/v1"),
            ReadString(raw, "lmstudio_url", "http://localhost:1234/v1"),
            ReadString(raw, "custom_url", "http://localhost:8000/v1"));
```

In `NormalizeBackend`, change the GGUF line:
```csharp
            "gguf" or "llama.cpp" => BackendOpenCode,
```

In `Validate`, after the `KiloModel` line:
```csharp
        ValidateLength(candidate.OllamaModel, 256, nameof(candidate.OllamaModel));
        ValidateLength(candidate.LmStudioModel, 256, nameof(candidate.LmStudioModel));
        ValidateLength(candidate.CustomModel, 256, nameof(candidate.CustomModel));
        ValidateLength(candidate.OllamaUrl, 1024, nameof(candidate.OllamaUrl));
        ValidateLength(candidate.LmStudioUrl, 1024, nameof(candidate.LmStudioUrl));
        ValidateLength(candidate.CustomUrl, 1024, nameof(candidate.CustomUrl));
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: all pass, including the three new tests.

- [ ] **Step 7: Commit**

```powershell
git add backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs backend-dotnet/RuBackend/Business/Settings/SettingsStore.cs backend-dotnet/RuBackend/Business/Settings/SettingsTransitionService.cs backend-dotnet/RuBackend.Tests
git commit -m "feat(backend): add ollama/lmstudio/custom settings fields and migrate gguf on read"
```

---

### Task 2: SettingsTransitionService accepts the new backends + custom key credential

**Files:**
- Modify: `backend-dotnet/RuBackend/Business/Settings/SettingsTransitionService.cs`
- Test: `backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs`

**Interfaces:**
- Consumes: `SettingsSnapshot` from Task 1; existing `ISettingsStore`, `ICredentialStore`, `IRuntimeSettingsLoader`; test doubles `FakeCredentialStore`, `FakeRuntimeSettingsLoader`, `TemporaryBusinessData`, `CreateAspNetFactory` (all already in `BusinessEndpointTests.cs`).
- Produces: `ApplyAsync` accepts payloads whose `backend_choice` is `ollama`/`lmstudio`/`custom` (no `SettingsPayloadException`); stores `custom_api_key` under account `"custom_api_key"`; `RequiresRuntimeLoad` true for any of the new keys; `gguf`/`llama.cpp` payloads migrate to `OpenCode Zen`.

- [ ] **Step 1: Write the failing endpoint tests** — append to `BusinessEndpointTests.cs`:

```csharp
[Fact]
public async Task PostSettings_saves_ollama_backend_with_model_and_url()
{
    using var data = TemporaryBusinessData.Create();
    var credentials = new FakeCredentialStore();
    var runtime = new FakeRuntimeSettingsLoader();
    using var factory = CreateAspNetFactory(
        data.Paths,
        credentials: credentials,
        runtimeLoader: runtime);
    using var client = factory.CreateAuthenticatedClient();

    using var response = await client.PostAsJsonAsync(
        "/settings",
        new
        {
            backend_choice = "ollama",
            ollama_model = "qwen3:8b",
            ollama_url = "http://127.0.0.1:11434/v1",
            max_new_tokens = 192,
        });
    var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("success", payload.GetProperty("status").GetString());
    var loaded = Assert.Single(runtime.Candidates);
    Assert.Equal("Ollama", loaded.Backend);
    Assert.Equal("qwen3:8b", loaded.OllamaModel);
    Assert.Equal("http://127.0.0.1:11434/v1", loaded.OllamaUrl);
    var persisted = JsonNode.Parse(await File.ReadAllTextAsync(data.Paths.SettingsFile))!.AsObject();
    Assert.Equal("Ollama", persisted["backend"]!.GetValue<string>());
    Assert.Equal("qwen3:8b", persisted["ollama_model"]!.GetValue<string>());
}

[Fact]
public async Task PostSettings_custom_backend_stores_key_as_credential_and_never_in_file()
{
    using var data = TemporaryBusinessData.Create();
    var credentials = new FakeCredentialStore();
    var runtime = new FakeRuntimeSettingsLoader();
    using var factory = CreateAspNetFactory(
        data.Paths,
        credentials: credentials,
        runtimeLoader: runtime);
    using var client = factory.CreateAuthenticatedClient();

    using var response = await client.PostAsJsonAsync(
        "/settings",
        new
        {
            backend_choice = "custom",
            custom_model = "my-model",
            custom_url = "http://localhost:9000/v1",
            custom_api_key = "private-custom-key",
        });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(
        "private-custom-key",
        await credentials.GetAsync("Ru", "custom_api_key", CancellationToken.None));
    var persisted = await File.ReadAllTextAsync(data.Paths.SettingsFile);
    Assert.DoesNotContain("private-custom-key", persisted, StringComparison.Ordinal);
    Assert.DoesNotContain("custom_api_key", persisted, StringComparison.OrdinalIgnoreCase);
    Assert.Equal("Custom OpenAI", Assert.Single(runtime.Candidates).Backend);
}

[Fact]
public async Task PostSettings_with_gguf_payload_migrates_to_opencode()
{
    using var data = TemporaryBusinessData.Create();
    var runtime = new FakeRuntimeSettingsLoader();
    using var factory = CreateAspNetFactory(
        data.Paths,
        runtimeLoader: runtime);
    using var client = factory.CreateAuthenticatedClient();

    using var response = await client.PostAsJsonAsync(
        "/settings",
        new
        {
            backend_choice = "gguf",
            model = "LFM2.5-2.6B-Q4_K_M.gguf",
        });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("OpenCode Zen", Assert.Single(runtime.Candidates).Backend);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~BusinessEndpointTests"`
Expected: first test fails with `"status":"error"` / "Invalid settings payload."; the others fail on missing candidate/credential.

- [ ] **Step 3: Implement** — `SettingsTransitionService.cs`:

Add the constant near the other accounts:
```csharp
    private const string CustomAccount = "custom_api_key";
```

`NormalizeBackend` — full replacement:
```csharp
    private static string NormalizeBackend(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "gguf" or "llama.cpp" => BackendOpenCode,
            "opencode" or "opencode zen" => BackendOpenCode,
            "kilo" or "kilo gateway" => BackendKilo,
            "ollama" => "Ollama",
            "lmstudio" or "lm studio" => "LM Studio",
            "custom" or "custom openai" => "Custom OpenAI",
            _ => throw new SettingsPayloadException(),
        };
```

In `BuildCandidate`, after the `kiloModel` line:
```csharp
        var ollamaModel = OptionalString(payload, "ollama_model", 256)
            ?? current.OllamaModel;
        var lmStudioModel = OptionalString(payload, "lmstudio_model", 256)
            ?? current.LmStudioModel;
        var customModel = OptionalString(payload, "custom_model", 256)
            ?? current.CustomModel;
        var ollamaUrl = OptionalString(payload, "ollama_url", 1024)
            ?? current.OllamaUrl;
        var lmStudioUrl = OptionalString(payload, "lmstudio_url", 1024)
            ?? current.LmStudioUrl;
        var customUrl = OptionalString(payload, "custom_url", 1024)
            ?? current.CustomUrl;
```

In the `new SettingsSnapshot(...)` call, replace the six `current.*` placeholders from Task 1 with these variables (in order `ollamaModel`, `lmStudioModel`, `customModel`, `ollamaUrl`, `lmStudioUrl`, `customUrl`).

In `BuildCredentialUpdates`, after the `gemini` block:
```csharp
        var custom = OptionalSecret(payload, "custom_api_key");
```
and after the `if (gemini is not null)` block:
```csharp
        if (custom is not null)
        {
            updates[CustomAccount] = custom;
        }
```

In `RequiresRuntimeLoad`, extend `runtimeFields`:
```csharp
        var runtimeFields = new[]
        {
            "backend",
            "backend_choice",
            "model",
            "opencode_model",
            "kilo_model",
            "ollama_model",
            "lmstudio_model",
            "custom_model",
            "ollama_url",
            "lmstudio_url",
            "custom_url",
            "custom_api_key",
            "api_variant",
            "kilo_reasoning_enabled",
            "checkpoint_path",
        };
        return runtimeFields.Any(payload.ContainsKey)
            || updates.Any(update =>
                update.Account is OpenCodeAccount or KiloAccount or CustomAccount);
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~BusinessEndpointTests"`
Expected: all pass, including the three new tests.

- [ ] **Step 5: Commit**

```powershell
git add backend-dotnet/RuBackend/Business/Settings/SettingsTransitionService.cs backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs
git commit -m "feat(backend): accept ollama/lmstudio/custom settings saves with custom key credential"
```

---

### Task 3: Forward the new fields to the worker

**Files:**
- Modify: `backend-dotnet/RuBackend/Business/Settings/LoopbackRuntimeSettingsLoader.cs`
- Test: `backend-dotnet/RuBackend.Tests/Worker/RuntimeSettingsLoaderTests.cs`

**Interfaces:**
- Consumes: `SettingsSnapshot` (Task 1), `WorkerConnection`, `WorkerEnvelope`, `WorkerMessageTypes`.
- Produces: `runtime_load` payload containing `ollama_model`, `lmstudio_model`, `custom_model`, `ollama_url`, `lmstudio_url`, `custom_url`; never contains credential values.

- [ ] **Step 1: Write the failing test** — append to `RuntimeSettingsLoaderTests.cs`:

```csharp
    [Fact]
    public async Task Load_forwards_the_new_backend_fields()
    {
        WorkerEnvelope? observed = null;
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            observed = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, new WorkerEnvelope(
                1,
                observed.Id,
                WorkerMessageTypes.RuntimeState,
                0,
                new JsonObject
                {
                    ["status"] = "success",
                    ["message"] = "Runtime loaded.",
                    ["backend"] = "Ollama",
                }));
        });
        var loader = new LoopbackRuntimeSettingsLoader(new WorkerClientOptions
        {
            Uri = server.Uri,
            Token = "worker-secret",
        });
        var candidate = Snapshot(new JsonObject()) with
        {
            Backend = "Ollama",
            OllamaModel = "qwen3:8b",
            OllamaUrl = "http://127.0.0.1:11434/v1",
            LmStudioModel = "local-model",
            CustomModel = "my-model",
            CustomUrl = "http://localhost:9000/v1",
        };

        var message = await loader.LoadAsync(candidate, CancellationToken.None);

        Assert.Equal("Runtime loaded.", message);
        Assert.Equal("Ollama", observed!.Payload["backend"]!.GetValue<string>());
        Assert.Equal("qwen3:8b", observed.Payload["ollama_model"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:11434/v1", observed.Payload["ollama_url"]!.GetValue<string>());
        Assert.Equal("local-model", observed.Payload["lmstudio_model"]!.GetValue<string>());
        Assert.Equal("my-model", observed.Payload["custom_model"]!.GetValue<string>());
        Assert.Equal("http://localhost:9000/v1", observed.Payload["custom_url"]!.GetValue<string>());
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~RuntimeSettingsLoaderTests"`
Expected: `Assert.Null(...)` / `GetValue` throws — keys absent from payload.

- [ ] **Step 3: Implement** — `LoopbackRuntimeSettingsLoader.cs`, in the `new JsonObject { ... }`, after `["kilo_model"] = ...`:

```csharp
                ["ollama_model"] = candidate.OllamaModel,
                ["lmstudio_model"] = candidate.LmStudioModel,
                ["custom_model"] = candidate.CustomModel,
                ["ollama_url"] = candidate.OllamaUrl,
                ["lmstudio_url"] = candidate.LmStudioUrl,
                ["custom_url"] = candidate.CustomUrl,
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~RuntimeSettingsLoaderTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add backend-dotnet/RuBackend/Business/Settings/LoopbackRuntimeSettingsLoader.cs backend-dotnet/RuBackend.Tests/Worker/RuntimeSettingsLoaderTests.cs
git commit -m "feat(backend): forward ollama/lmstudio/custom model and url fields to the worker"
```

---

### Task 4: Settings response + GUI DTO

**Files:**
- Modify: `backend-dotnet/RuBackend/Business/BusinessEndpoints.cs`
- Modify: `gui-winui3/Models/Models.cs`
- Test: `backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs`
- Test: `gui-winui3.Tests/SettingsDtoTests.cs`

**Interfaces:**
- Consumes: `SettingsSnapshot` (Task 1), `ISettingsStore`, `ICredentialStore` (`HasCredentialAsync`), `AppSettingsDto`.
- Produces: `GET /settings` response containing `ollama_model`, `lmstudio_model`, `custom_model`, `ollama_url`, `lmstudio_url`, `custom_url`, `has_custom_api_key` (and empty `custom_api_key`); `AppSettingsDto.CustomApiKey` (`custom_api_key`) and `AppSettingsDto.HasCustomKey` (`has_custom_api_key`).

- [ ] **Step 1: Write the failing tests**

Append to `BusinessEndpointTests.cs`:
```csharp
    [Fact]
    public async Task GetSettings_returns_the_new_backend_fields_and_custom_key_presence()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"ollama\",\"ollama_model\":\"qwen3:8b\",\"ollama_url\":\"http://127.0.0.1:11434/v1\",\"custom_url\":\"http://localhost:9000/v1\"}");
        var credentials = new FakeCredentialStore();
        credentials.Seed("Ru", "custom_api_key", "private-custom-key");
        using var factory = CreateAspNetFactory(data.Paths, credentials: credentials);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/settings");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ollama", payload.GetProperty("backend_choice").GetString());
        Assert.Equal("qwen3:8b", payload.GetProperty("ollama_model").GetString());
        Assert.Equal("http://127.0.0.1:11434/v1", payload.GetProperty("ollama_url").GetString());
        Assert.Equal("http://localhost:9000/v1", payload.GetProperty("custom_url").GetString());
        Assert.True(payload.GetProperty("has_custom_api_key").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("custom_api_key").GetString());
    }
```

Append to `SettingsDtoTests.cs`:
```csharp
    [Fact]
    public void CustomApiKeyUsesTheBackendWireName()
    {
        string payload = JsonSerializer.Serialize(new AppSettingsDto
        {
            CustomApiKey = "sk-custom",
        });

        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.Equal("sk-custom", document.RootElement.GetProperty("custom_api_key").GetString());
        Assert.False(document.RootElement.GetProperty("has_custom_api_key").GetBoolean());
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~BusinessEndpointTests"` and `dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter "FullyQualifiedName~SettingsDtoTests"`
Expected: property missing errors (`KeyNotFoundException` for `ollama_model`; compile error for `CustomApiKey`).

- [ ] **Step 3: Implement .NET response** — `BusinessEndpoints.cs`:

`ToSettingsResponse` signature gains a parameter:
```csharp
    private static JsonObject ToSettingsResponse(
        SettingsSnapshot value,
        bool hasOpenCode,
        bool hasKilo,
        bool hasGemini,
        bool hasCustom)
```

In the credential-removal array add `"custom_api_key"`.

After `result["kilo_model"] = value.KiloModel;`:
```csharp
        result["ollama_model"] = value.OllamaModel;
        result["lmstudio_model"] = value.LmStudioModel;
        result["custom_model"] = value.CustomModel;
        result["ollama_url"] = value.OllamaUrl;
        result["lmstudio_url"] = value.LmStudioUrl;
        result["custom_url"] = value.CustomUrl;
```

After the `gemini_vision_api_key` block:
```csharp
        result["custom_api_key"] = string.Empty;
        result["has_custom_api_key"] = hasCustom;
```

Update `GetSettingsAsync` (lines 33-50) — after the `hasGemini` line add:
```csharp
            var hasCustom = await HasCredentialAsync(
                credentials,
                "custom_api_key",
                cancellationToken).ConfigureAwait(false);
```
and change the `ToSettingsResponse(...)` call to pass `hasCustom` as the fifth argument (after `hasGemini`).

- [ ] **Step 4: Implement GUI DTO** — `gui-winui3/Models/Models.cs`, inside `AppSettingsDto`, after `CustomUrl`:

```csharp
        [JsonPropertyName("custom_api_key")]
        public string CustomApiKey { get; set; } = string.Empty;

        [JsonPropertyName("has_custom_api_key")]
        public bool HasCustomKey { get; set; } = false;
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~BusinessEndpointTests"`; `dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter "FullyQualifiedName~SettingsDtoTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```powershell
git add backend-dotnet/RuBackend/Business/BusinessEndpoints.cs backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs gui-winui3/Models/Models.cs gui-winui3.Tests/SettingsDtoTests.cs
git commit -m "feat(backend,gui): expose ollama/lmstudio/custom settings and custom key presence in DTOs"
```

---

### Task 5: GUI — Custom API Key row

**Files:**
- Modify: `gui-winui3/Views/SettingsView.xaml`
- Modify: `gui-winui3/Views/SettingsView.xaml.cs`

**Interfaces:**
- Consumes: `AppSettingsDto.CustomApiKey` / `HasCustomKey` (Task 4).
- Produces: `CustomApiKeyBox` password row (visible only for backend `custom`), placeholder "Đã lưu (để trống để giữ nguyên)" when `HasCustomKey`, auto-save on change, `CustomApiKey` included in the save DTO.

- [ ] **Step 1: Add the XAML row** — `SettingsView.xaml`, right after the `CustomEndpointRow` StackPanel (line ~121), still inside the model-settings StackPanel:

```xml
                            <StackPanel x:Name="CustomApiKeyRow" Spacing="16" Visibility="Collapsed">
                                <Border Height="1" Background="{StaticResource AppCardBorderSubtleBrush}"/>
                                <Grid ColumnDefinitions="*,Auto">
                                    <StackPanel Grid.Column="0" VerticalAlignment="Center" Spacing="2">
                                        <TextBlock Text="Custom API Key" FontSize="14" FontWeight="Medium" Foreground="{StaticResource AppTextPrimaryBrush}"/>
                                        <TextBlock Text="Khóa API lưu an toàn trong Windows Credential Manager (tùy chọn)" FontSize="12.5" Foreground="{StaticResource AppTextSecondaryBrush}"/>
                                    </StackPanel>
                                    <PasswordBox x:Name="CustomApiKeyBox" Grid.Column="1" PlaceholderText="sk-..." Width="300"
                                                 CornerRadius="{StaticResource AppControlCornerRadius}"/>
                                </Grid>
                            </StackPanel>
```

- [ ] **Step 2: Wire the code-behind** — `SettingsView.xaml.cs`:

In `SetupAutoSave`, after the `CustomEndpointUrlBox.TextChanged` line:
```csharp
            CustomApiKeyBox.PasswordChanged += (_, __) => ScheduleAutoSave();
```

In `UpdateVisibilityForBackend`, after the `CustomEndpointRow.Visibility` line:
```csharp
            CustomApiKeyRow.Visibility = (choice == "custom") ? Visibility.Visible : Visibility.Collapsed;
```

In `LoadSettingsAsync`, after the `CustomEndpointUrlBox.Text = settings.CustomUrl;` line:
```csharp
                CustomApiKeyBox.Password = string.Empty;
                CustomApiKeyBox.PlaceholderText = settings.HasCustomKey
                    ? "Đã lưu (để trống để giữ nguyên)"
                    : "sk-...";
```

In `SaveSettingsAsync`, in the `dto` initializer after `CustomUrl = ...`:
```csharp
                    CustomApiKey = CustomApiKeyBox.Password,
```

- [ ] **Step 3: Build the GUI**

Run: `dotnet build D:\LLM\gui-winui3\RuApp.csproj --nologo`
Expected: build succeeds (XAML compiles, `CustomApiKeyBox` resolves).

- [ ] **Step 4: Commit**

```powershell
git add gui-winui3/Views/SettingsView.xaml gui-winui3/Views/SettingsView.xaml.cs
git commit -m "feat(gui): add custom API key row with credential keep-unchanged pattern"
```

---

### Task 6: Python — custom credential + worker runtime_load for the new backends

**Files:**
- Modify: `src/ru_runtime/worker/v1/core.py`
- Modify: `src/ru_runtime/worker/v1/handlers.py`
- Test: `tests/test_worker_runtime_handlers.py`

**Interfaces:**
- Consumes: `CredentialStore` (existing), `ModelRunner` (existing `select_backend`/`load` with `base_url` param).
- Produces: `ru_runtime.worker.v1.core.RuRuntimeCore.custom_cred` (`CredentialStore("Ru", "custom_api_key")`); module-level pure function `handlers._runtime_load_params(payload: dict[str, Any]) -> dict[str, Any]` returning keys `backend`, `model_name`, `base_url` (`str | None`), `checkpoint_path`, `api_variant`, `kilo_reasoning_enabled`; raises `ValueError("unsupported runtime backend")` for anything outside `("OpenCode Zen", "Kilo Gateway", "Ollama", "LM Studio", "Custom OpenAI")`. `create_runtime_handlers()` closure `runtime_load` uses it, passes `base_url` to `runner.load`, reads `custom_api_key` from `core.custom_cred`.

- [ ] **Step 1: Write the failing tests** — append to `tests/test_worker_runtime_handlers.py`:

```python
def test_runtime_load_params_maps_ollama_lmstudio_and_custom() -> None:
    from ru_runtime.worker.v1.handlers import _runtime_load_params

    ollama = _runtime_load_params(
        {
            "backend": "Ollama",
            "ollama_model": "qwen3:8b",
            "ollama_url": "http://127.0.0.1:11434/v1",
        }
    )
    assert ollama["backend"] == "Ollama"
    assert ollama["model_name"] == "qwen3:8b"
    assert ollama["base_url"] == "http://127.0.0.1:11434/v1"

    lmstudio = _runtime_load_params(
        {
            "backend": "LM Studio",
            "lmstudio_model": "local-model",
            "lmstudio_url": "http://127.0.0.1:1234/v1",
        }
    )
    assert lmstudio["model_name"] == "local-model"
    assert lmstudio["base_url"] == "http://127.0.0.1:1234/v1"

    custom = _runtime_load_params(
        {
            "backend": "Custom OpenAI",
            "custom_model": "my-model",
            "custom_url": "http://localhost:9000/v1",
        }
    )
    assert custom["model_name"] == "my-model"
    assert custom["base_url"] == "http://localhost:9000/v1"

    kilo = _runtime_load_params(
        {"backend": "Kilo Gateway", "kilo_model": "kilo-auto/free"}
    )
    assert kilo["model_name"] == "kilo-auto/free"
    assert kilo["base_url"] is None


def test_runtime_load_params_rejects_retired_and_unknown_backends() -> None:
    from ru_runtime.worker.v1.handlers import _runtime_load_params

    for backend in ("llama.cpp", "gguf", "unknown"):
        with pytest.raises(ValueError, match="unsupported runtime backend"):
            _runtime_load_params({"backend": backend})


def test_runtime_load_params_rejects_non_boolean_reasoning() -> None:
    from ru_runtime.worker.v1.handlers import _runtime_load_params

    with pytest.raises(ValueError, match="invalid reasoning setting"):
        _runtime_load_params(
            {"backend": "Ollama", "kilo_reasoning_enabled": "yes"}
        )
```

Add `import pytest` at the top of the file if not already present (it is not — check the imports at lines 1-28; add `import pytest` after `import json`).

- [ ] **Step 2: Run to verify they fail**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_worker_runtime_handlers.py -q`
Expected: `ImportError: cannot import name '_runtime_load_params'`.

- [ ] **Step 3: Implement the helper + custom credential**

`core.py`, in `RuRuntimeCore.__init__`, after the `gemini_vision_cred` line:
```python
        self.custom_cred = CredentialStore("Ru", "custom_api_key")
```

`handlers.py` — add at module level (after `_RUNTIME_TRANSITION_LOCK`, line ~47):
```python
_RUNTIME_BACKENDS = (
    "OpenCode Zen",
    "Kilo Gateway",
    "Ollama",
    "LM Studio",
    "Custom OpenAI",
)


def _runtime_load_params(payload: dict[str, Any]) -> dict[str, Any]:
    backend = str(payload.get("backend") or "")
    if backend not in _RUNTIME_BACKENDS:
        raise ValueError("unsupported runtime backend")
    model_name = str(payload.get("model") or "")
    base_url: str | None = None
    if backend == "OpenCode Zen":
        model_name = str(payload.get("opencode_model") or model_name)
    elif backend == "Kilo Gateway":
        model_name = str(payload.get("kilo_model") or model_name)
    elif backend == "Ollama":
        model_name = str(payload.get("ollama_model") or model_name)
        base_url = str(payload.get("ollama_url") or "") or None
    elif backend == "LM Studio":
        model_name = str(payload.get("lmstudio_model") or model_name)
        base_url = str(payload.get("lmstudio_url") or "") or None
    else:
        model_name = str(payload.get("custom_model") or model_name)
        base_url = str(payload.get("custom_url") or "") or None
    checkpoint_path = str(payload.get("checkpoint_path") or "")
    api_variant = str(payload.get("api_variant") or "")
    reasoning = payload.get("kilo_reasoning_enabled", False)
    if not isinstance(reasoning, bool):
        raise ValueError("invalid reasoning setting")
    return {
        "backend": backend,
        "model_name": model_name,
        "base_url": base_url,
        "checkpoint_path": checkpoint_path,
        "api_variant": api_variant,
        "kilo_reasoning_enabled": reasoning,
    }
```

- [ ] **Step 4: Refactor the `runtime_load` closure** — in `create_runtime_handlers()` (lines 102-149), replace the body with:

```python
    def runtime_load(payload: dict[str, Any]) -> Mapping[str, Any]:
        runner = shared.core.runner
        params = _runtime_load_params(payload)
        backend = params["backend"]

        with _RUNTIME_TRANSITION_LOCK:
            previous = {
                "backend": runner.backend,
                "checkpoint_path": runner.checkpoint_path,
                "openai_runtime": runner.openai_runtime,
                "kilo_reasoning_enabled": runner.kilo_reasoning_enabled,
            }
            try:
                api_key = ""
                if backend == "OpenCode Zen":
                    api_key = shared.core.opencode_cred.get_key() or ""
                elif backend == "Kilo Gateway":
                    api_key = shared.core.kilo_cred.get_key() or ""
                elif backend == "Custom OpenAI":
                    api_key = shared.core.custom_cred.get_key() or ""
                runner.select_backend(backend)
                message = runner.load(
                    checkpoint_path=params["checkpoint_path"],
                    api_key=api_key,
                    model_name=params["model_name"],
                    api_variant=params["api_variant"],
                    kilo_reasoning_enabled=params["kilo_reasoning_enabled"],
                    base_url=params["base_url"],
                )
                return {
                    "status": "success",
                    "message": str(message),
                    "backend": backend,
                }
            except Exception:
                runner.select_backend(previous["backend"])
                runner.checkpoint_path = previous["checkpoint_path"]
                runner.openai_runtime = previous["openai_runtime"]
                runner.kilo_reasoning_enabled = previous["kilo_reasoning_enabled"]
                raise RuntimeError("runtime_load_failed") from None
```

(Remove the now-unused `from ru_runtime.generation.runner import BACKEND_GGUF` inside `create_runtime_handlers` only if nothing else uses it — `generation_scope` still references `BACKEND_GGUF` at line 98, so keep that import.)

- [ ] **Step 5: Run the tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_worker_runtime_handlers.py -q`
Expected: all pass (new + existing).

- [ ] **Step 6: Commit**

```powershell
git add src/ru_runtime/worker/v1/core.py src/ru_runtime/worker/v1/handlers.py tests/test_worker_runtime_handlers.py
git commit -m "feat(runtime): accept ollama/lmstudio/custom backends in worker runtime load with custom key"
```

---

### Task 7: Python — retired GGUF never loads at worker startup

**Files:**
- Modify: `src/ru_runtime/worker/v1/runtime_services.py`
- Test: `tests/test_runtime_services.py`

**Interfaces:**
- Consumes: `load_settings()`, `_resolve_backend()`, `core.runner`, `_DEFAULT_SETTINGS` (existing).
- Produces: `apply_saved_settings()` treats raw `backend` values `gguf`/`llama.cpp` as `OpenCode Zen` with the default opencode model; logs a migration notice.

- [ ] **Step 1: Write the failing test** — append to `tests/test_runtime_services.py`:

```python
def test_apply_saved_settings_migrates_retired_gguf_to_opencode(monkeypatch, tmp_path) -> None:
    from ru_runtime.worker.v1 import runtime_services as shared

    settings_file = tmp_path / "settings.json"
    settings_file.write_text(
        '{"backend": "llama.cpp", "model": "LFM2.5-2.6B-Q4_K_M.gguf"}',
        encoding="utf-8",
    )
    monkeypatch.setattr(shared, "SETTINGS_FILE", settings_file)
    loaded: dict = {}

    def fake_load(**kwargs) -> str:
        loaded.update(kwargs)
        return "Ready"

    monkeypatch.setattr(shared.core.runner, "load", fake_load)
    selected: list[str] = []

    def fake_select(backend: str) -> None:
        selected.append(backend)

    monkeypatch.setattr(shared.core.runner, "select_backend", fake_select)

    result = shared.apply_saved_settings()

    assert selected == ["OpenCode Zen"]
    assert loaded["model_name"] == "deepseek-v4-flash-free"
    assert result == "Ready"
```

- [ ] **Step 2: Run to verify it fails**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_runtime_services.py -q`
Expected: `AssertionError: selected == ['llama.cpp']` (it resolves `llama.cpp` and attempts a GGUF load).

- [ ] **Step 3: Implement** — `runtime_services.py`, in `apply_saved_settings()`, right after the `backend = _resolve_backend(...)` line:

```python
    if backend in (BACKEND_GGUF, "gguf", "llama.cpp"):
        logger.info(
            "Retired llama.cpp backend in saved settings; migrating to %s",
            BACKEND_OPENCODE,
        )
        settings = {
            **settings,
            "backend": BACKEND_OPENCODE,
            "opencode_model": settings.get("opencode_model")
            or _DEFAULT_SETTINGS["opencode_model"],
        }
        backend = BACKEND_OPENCODE
```

(`_resolve_backend` returns `"llama.cpp"` verbatim for a `llama.cpp` raw value since it is not in `_BACKEND_ALIASES` — the `in (BACKEND_GGUF, "gguf", "llama.cpp")` guard catches both raw spellings.)

- [ ] **Step 4: Run the tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_runtime_services.py -q`
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add src/ru_runtime/worker/v1/runtime_services.py tests/test_runtime_services.py
git commit -m "fix(runtime): never attempt a GGUF load from retired llama.cpp settings"
```

---

### Task 8: Python — honest /status via runner load_error

**Files:**
- Modify: `src/ru_runtime/generation/runner.py`
- Modify: `src/ru_runtime/worker/v1/status.py`
- Test: `tests/test_runner_status.py` (new)
- Test: `tests/test_status.py` (new)

**Interfaces:**
- Consumes: `ModelRunner.load()` (existing signature), `core.runner` from `runtime_services`.
- Produces: `ModelRunner.load_error: str | None` — set to the exception message when `load()` raises, cleared to `None` at the start of every `load()` call; `status.get_status()` returns `{"status": "error", "message": <load_error>}` when set, else `{"status": "ok", "message": "Backend is running and ready for the WinUI3 UI.", "backend": <runner.backend>}`; `status.get_models()` reads the custom key from `core.custom_cred`.

- [ ] **Step 1: Write the failing tests**

Create `tests/test_runner_status.py`:
```python
from __future__ import annotations

import pytest

from ru_runtime.generation import runner as runner_module
from ru_runtime.generation.runner import ModelRunner


def test_load_records_load_error_and_re_raises(monkeypatch) -> None:
    def fail_openai(*_args, **_kwargs) -> None:
        raise RuntimeError("cannot reach server")

    monkeypatch.setattr(runner_module, "OpenAIRuntime", fail_openai)
    model = ModelRunner()
    model.select_backend("Ollama")

    with pytest.raises(RuntimeError, match="cannot reach server"):
        model.load(model_name="qwen2.5:latest")

    assert model.load_error == "cannot reach server"


def test_load_clears_load_error_on_success() -> None:
    model = ModelRunner()
    model.select_backend("Ollama")

    model.load(model_name="qwen2.5:latest")

    assert model.load_error is None
    assert model.openai_runtime is not None
    assert model.openai_runtime.model_name == "qwen2.5:latest"
```

Create `tests/test_status.py`:
```python
from __future__ import annotations

from ru_runtime.worker.v1 import status


def test_get_status_reports_load_error(monkeypatch) -> None:
    class FakeRunner:
        backend = "OpenCode Zen"
        load_error = "model failed to load"

    monkeypatch.setattr(
        "ru_runtime.worker.v1.runtime_services.core.runner",
        FakeRunner(),
    )

    result = status.get_status()

    assert result["status"] == "error"
    assert result["message"] == "model failed to load"


def test_get_status_ok_when_loaded(monkeypatch) -> None:
    class FakeRunner:
        backend = "OpenCode Zen"
        load_error = None

    monkeypatch.setattr(
        "ru_runtime.worker.v1.runtime_services.core.runner",
        FakeRunner(),
    )

    result = status.get_status()

    assert result["status"] == "ok"
    assert result["backend"] == "OpenCode Zen"
```

- [ ] **Step 2: Run to verify they fail**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_runner_status.py tests/test_status.py -q`
Expected: `AttributeError: 'ModelRunner' object has no attribute 'load_error'`; `get_status` returns `ok` without error.

- [ ] **Step 3: Implement runner tracking** — `runner.py`:

In `ModelRunner.__init__`, after `self.kilo_reasoning_enabled = False`:
```python
        self.load_error: str | None = None
```

In `load()`, after `self.kilo_reasoning_enabled = bool(kilo_reasoning_enabled)`:
```python
        self.load_error = None
```

Wrap the body of `load()` (from `logger.info("Loading backend=...")` through the final `raise ValueError(...)`) in:
```python
        try:
            <existing body>
        except Exception as error:
            self.load_error = str(error)
            raise
```
Do not change the body itself. If your editor makes this awkward, extract the existing body into the try block verbatim.

- [ ] **Step 4: Implement status** — `status.py`:

Replace `get_status()`:
```python
def get_status() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    runner = shared.core.runner
    if runner.load_error:
        return {"status": "error", "message": runner.load_error}
    return {
        "status": "ok",
        "message": "Backend is running and ready for the WinUI3 UI.",
        "backend": runner.backend,
    }
```

In `get_models()`, replace the `else` branch:
```python
    else:
        base_url = settings.get("custom_url", "http://localhost:8000/v1")
        try:
            api_key = shared.core.custom_cred.get_key() or ""
        except Exception as error:
            shared.logger.warning("Could not read Custom key: %s", error)
            api_key = ""
```

- [ ] **Step 5: Run the tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_runner_status.py tests/test_status.py tests/test_worker_runtime_handlers.py tests/test_headless_runner.py -q`
Expected: all pass.

- [ ] **Step 6: Commit**

```powershell
git add src/ru_runtime/generation/runner.py src/ru_runtime/worker/v1/status.py tests/test_runner_status.py tests/test_status.py
git commit -m "feat(runtime): report real model load state in worker status"
```

---

### Task 9: Delete retired GGUF build/eval scripts and their configs

**Files:**
- Delete: `scripts/build_ru_v1_1.py`, `scripts/build_ru_v1_2.py`, `scripts/evaluate_ru_lfm2_5_v1.py`, `configs/ru_lfm2_5_v1_1.json`, `configs/ru_lfm2_5_v1_2.json`

**Interfaces:**
- Consumes: nothing (self-contained deletion).
- Produces: repo free of GGUF-only build/eval artifacts. `scripts/evaluate_ru_model.py` + `configs/ru_judge_eval.json` stay (live eval path).

- [ ] **Step 1: Verify no live references remain**

Run: `rg -rn "build_ru_v1_1|build_ru_v1_2|evaluate_ru_lfm2_5" --glob "!docs/validation/*" .`
Expected: no matches (docs/validation/* are historical records; leave them). Also check `TODO.md`: `rg -n "build_ru_v1|evaluate_ru_lfm2_5" TODO.md` — if it lists them, remove those lines.

- [ ] **Step 2: Delete the files**

```powershell
git rm scripts/build_ru_v1_1.py scripts/build_ru_v1_2.py scripts/evaluate_ru_lfm2_5_v1.py configs/ru_lfm2_5_v1_1.json configs/ru_lfm2_5_v1_2.json
```

- [ ] **Step 3: Confirm the eval entrypoint still works**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_ru_judge_evaluation.py tests/test_ru_evaluation_cases.py -q`
Expected: pass (judge/cases module untouched).

- [ ] **Step 4: Commit**

```powershell
git add -u
git commit -m "chore: delete retired GGUF build and eval scripts"
```

---

### Task 10: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Run the Python suite**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest -q`
Expected: all pass (baseline ~472 tests + new ones). Fix any breakage caused by Tasks 6-8 before proceeding.

- [ ] **Step 2: Run the .NET suites**

Run: `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo` then `dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo`
Expected: all pass (~220 backend + ~94 GUI + new tests).

- [ ] **Step 3: Run the route contract check**

Run: `& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py`
Expected: 38 HTTP routes and 2 WebSocket routes.

- [ ] **Step 4: Smoke the settings round-trip manually (optional, requires a running worker)**

Write `settings.json` with `{"backend":"ollama","ollama_model":"qwen3:8b"}` in a scratch `RU_DATA_ROOT`, call `apply_saved_settings()`, and confirm the log shows the Ollama backend selected with the right model/URL. Then repeat with `"backend":"llama.cpp"` and confirm the migration log line appears and no GGUF asset is touched.

- [ ] **Step 5: Commit any docs/TODO leftovers**

```powershell
git add -u
git commit -m "docs: update TODO after cloud-first settings fix"  # only if TODO.md changed
```