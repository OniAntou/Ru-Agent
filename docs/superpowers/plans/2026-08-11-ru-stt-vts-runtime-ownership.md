# Ru STT and VTS Runtime Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the public `/voice/transcribe` and `/vts/*` routes behind explicit ASP.NET Core ownership and a replaceable runtime-worker interface while preserving the current WinUI contract and Python hardware/provider implementation.

**Architecture:** ASP.NET validates the deterministic STT request boundary and explicitly maps the STT/VTS routes. An `IRuntimeWorkerClient` hides the current FastAPI transport so a later versioned loopback worker can replace it without changing public endpoints. Python remains the recognition and VTube Studio runtime in this slice; FastAPI is not removed and `python` remains the default rollback owner.

**Tech Stack:** .NET 10 minimal APIs, xUnit, existing `IUpstreamHttpTransport`, Python/FastAPI compatibility worker, PowerShell verification on Windows.

## Execution Status

- Runtime worker plus STT seam: complete in commit `3acf451`.
- VTS route ownership seam: complete in commit `1dc4ae8`.
- Final evidence: focused .NET 25 passed, full .NET 95 passed, selected Python
  compatibility 159 passed with two dependency warnings, route matrix 38 HTTP
  plus 2 WebSocket, and isolated topology 14 safe probes with zero failures.
- Defaults remain `python`; real microphone and user-observed VTS action
  acceptance are intentionally still pending.

## Global Constraints

- Execute inline on `main`; do not use sub-agents.
- Preserve all unrelated dirty files and stage only paths listed in this plan.
- Default STT/VTS ownership remains `python`; ownership changes take effect only after restart.
- Never log bearer tokens, base64 audio, transcripts, VTS payloads, private VTS markers, or response bodies.
- ASP.NET request-size middleware remains the outer 8 MiB guard.
- STT accepts `audio_b64` up to 7,500,000 characters and `sample_rate` from 8,000 through 192,000, defaulting to 16,000.
- VTS hardware state, authentication, reconnect, catalog, action cooldown, motion, and overlay process lifecycle remain single-owned by the Python worker in this slice.
- Run heavy Python and .NET suites sequentially.

---

### Task 1: Add the runtime-worker seam

**Files:**
- Create: `backend-dotnet/RuBackend/Runtime/IRuntimeWorkerClient.cs`
- Create: `backend-dotnet/RuBackend/Runtime/FastApiRuntimeWorkerClient.cs`
- Create: `backend-dotnet/RuBackend.Tests/Fakes/StubRuntimeWorkerClient.cs`
- Create: `backend-dotnet/RuBackend.Tests/Runtime/FastApiRuntimeWorkerClientTests.cs`
- Modify: `backend-dotnet/RuBackend/Program.cs`
- Modify: `backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs`

**Interfaces:**
- Consumes: `IUpstreamHttpTransport.SendAsync(UpstreamRequest, CancellationToken)` and `BackendOptions.DesktopToken`.
- Produces: `IRuntimeWorkerClient.SendAsync(RuntimeWorkerRequest, CancellationToken)` returning `UpstreamResponse`.

- [x] **Step 1: Write the failing adapter tests**

```csharp
[Fact]
public async Task FastApi_adapter_adds_worker_auth_and_preserves_body()
{
    var transport = new StubUpstreamHttpTransport(new UpstreamResponse(200, "application/json", "{}"u8.ToArray()));
    var client = CreateClient(transport, "worker-secret");

    await client.SendAsync(
        new RuntimeWorkerRequest(HttpMethod.Post, "/voice/transcribe", "application/json", "{\"audio_b64\":\"AA==\"}"u8.ToArray()),
        CancellationToken.None);

    Assert.Equal("Bearer worker-secret", transport.LastRequest!.Headers["Authorization"]);
    Assert.Equal("application/json", transport.LastRequest.Headers["Content-Type"]);
    Assert.Equal("/voice/transcribe", transport.LastRequest.PathAndQuery);
}
```

- [x] **Step 2: Verify RED**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~FastApiRuntimeWorkerClientTests`

Expected: compilation fails because `IRuntimeWorkerClient`, `RuntimeWorkerRequest`, and `FastApiRuntimeWorkerClient` do not exist.

- [x] **Step 3: Implement the minimal seam**

```csharp
public sealed record RuntimeWorkerRequest(
    HttpMethod Method,
    string PathAndQuery,
    string? ContentType,
    byte[] Body);

public interface IRuntimeWorkerClient
{
    Task<UpstreamResponse> SendAsync(RuntimeWorkerRequest request, CancellationToken cancellationToken);
}
```

`FastApiRuntimeWorkerClient` converts the request into `UpstreamRequest`, adds `Authorization: Bearer <DesktopToken>`, forwards through `IUpstreamHttpTransport`, and never logs request or response content. Register it as a singleton-compatible typed service in `Program.cs`. Extend `BackendWebApplicationFactory` so tests can replace it with `StubRuntimeWorkerClient`.

- [x] **Step 4: Verify GREEN**

Run the focused command from Step 2. Expected: all `FastApiRuntimeWorkerClientTests` pass.

- [x] **Step 5: Commit**

```powershell
git add -- backend-dotnet/RuBackend/Runtime backend-dotnet/RuBackend.Tests/Runtime backend-dotnet/RuBackend.Tests/Fakes/StubRuntimeWorkerClient.cs backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs
git commit -m "feat: add ASP.NET runtime worker seam"
```

### Task 2: Move the STT public contract into ASP.NET

**Files:**
- Create: `backend-dotnet/RuBackend/Speech/SttEndpoints.cs`
- Create: `backend-dotnet/RuBackend.Tests/Speech/SttEndpointTests.cs`
- Modify: `backend-dotnet/RuBackend/Configuration/BackendOptions.cs`
- Modify: `backend-dotnet/RuBackend/Program.cs`
- Modify: `backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs`

**Interfaces:**
- Consumes: `IRuntimeWorkerClient.SendAsync`.
- Produces: feature-selected `POST /voice/transcribe` controlled by `RuBackend:Ownership:Stt` with modes `python` and `aspnet`.

- [x] **Step 1: Write failing endpoint tests**

The test class must prove:

```csharp
[Fact] public async Task Python_mode_keeps_the_catch_all_owner();
[Fact] public async Task AspNet_mode_rejects_empty_audio_without_worker_call();
[Fact] public async Task AspNet_mode_rejects_invalid_base64_without_worker_call();
[Theory] [InlineData(7999)] [InlineData(192001)]
public async Task AspNet_mode_rejects_out_of_range_sample_rate(int sampleRate);
[Fact] public async Task AspNet_mode_sends_valid_audio_to_the_runtime_worker();
[Fact] public async Task AspNet_mode_returns_503_when_the_runtime_worker_is_unavailable();
[Fact] public async Task Authentication_runs_before_STT_validation();
```

Valid requests must reach the worker with the same path and JSON body. Empty or invalid base64 returns HTTP 200 with the existing `{ "status":"error", "message":... }` shape. Missing/invalid fields and out-of-range rates return 422 without audio content in the response or logs.

- [x] **Step 2: Verify RED**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~SttEndpointTests`

Expected: tests fail because `SttEndpoints` and `BackendOwnershipOptions.Stt` do not exist.

- [x] **Step 3: Implement the minimal STT endpoint**

`SttEndpoints.Map(WebApplication)` maps the route only when ownership normalizes to `aspnet`. The handler reads bounded JSON, validates the deterministic fields, then sends the original body through `IRuntimeWorkerClient`. It writes worker status/content type/body unchanged. `HttpRequestException` becomes:

```json
{"status":"error","message":"Speech recognition is temporarily unavailable."}
```

with HTTP 503. Cancellation is rethrown when `RequestAborted` is cancelled.

- [x] **Step 4: Verify GREEN and regression**

Run the focused command from Step 2, then:

`dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~Speech`

Expected: all focused tests pass.

- [x] **Step 5: Commit**

```powershell
git add -- backend-dotnet/RuBackend/Speech backend-dotnet/RuBackend.Tests/Speech backend-dotnet/RuBackend/Configuration/BackendOptions.cs backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs
git commit -m "feat: add ASP.NET STT ownership seam"
```

### Task 3: Move VTS public routing behind the worker seam

**Files:**
- Create: `backend-dotnet/RuBackend/Vts/VtsEndpoints.cs`
- Create: `backend-dotnet/RuBackend.Tests/Vts/VtsEndpointTests.cs`
- Modify: `backend-dotnet/RuBackend/Configuration/BackendOptions.cs`
- Modify: `backend-dotnet/RuBackend/Program.cs`
- Modify: `backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs`

**Interfaces:**
- Consumes: `IRuntimeWorkerClient.SendAsync`.
- Produces: feature-selected ownership for all eleven current VTS HTTP contracts through `RuBackend:Ownership:Vts`.

- [x] **Step 1: Write failing route-coverage tests**

Use a theory over these exact method/path pairs:

```text
GET  /vts/mouse-look
POST /vts/mouse-look
GET  /vts/overlay
POST /vts/overlay
GET  /vts/status
POST /vts/connect
GET  /vts/config
POST /vts/config
POST /vts/activity
POST /vts/action
POST /vts/speaking
```

Tests must prove `python` mode uses catch-all forwarding, `aspnet` mode calls `IRuntimeWorkerClient` exactly once with the original body/content type, worker responses are byte-preserved, unauthenticated requests do not call the worker, and worker unavailability returns a bounded HTTP 503 JSON error without exception details.

- [x] **Step 2: Verify RED**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~VtsEndpointTests`

Expected: tests fail because `VtsEndpoints` and `BackendOwnershipOptions.Vts` do not exist.

- [x] **Step 3: Implement explicit VTS route mapping**

`VtsEndpoints.Map(WebApplication)` maps only the eleven listed routes in `aspnet` mode. Each route copies the bounded request body into `RuntimeWorkerRequest`, relays the worker response, propagates cancellation, and catches only worker transport failure. Hardware state and VTS semantic validation remain in the single Python worker owner.

- [x] **Step 4: Verify GREEN and regression**

Run the focused command from Step 2. Then run:

`dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~Vts`

Expected: all focused tests pass.

- [x] **Step 5: Commit**

```powershell
git add -- backend-dotnet/RuBackend/Vts backend-dotnet/RuBackend.Tests/Vts backend-dotnet/RuBackend/Configuration/BackendOptions.cs backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs
git commit -m "feat: add ASP.NET VTS ownership seam"
```

### Task 4: Freeze route ownership evidence and documentation

**Files:**
- Modify: `backend-dotnet/compatibility-route-matrix.json`
- Modify: `tests/test_aspnetcore_route_matrix.py`
- Modify: `docs/aspnetcore-route-matrix.md`
- Modify: `docs/aspnetcore-cutover-validation.md`
- Modify: `backend-dotnet/README.md`
- Modify: `README.md`
- Modify: `docs/superpowers/plans/2026-08-11-ru-stt-vts-runtime-ownership.md`

**Interfaces:**
- Consumes: the implemented `Stt` and `Vts` ownership settings.
- Produces: accurate route inventory, rollback commands, and verification evidence.

- [x] **Step 1: Write the failing matrix assertions**

Add assertions that `/voice/transcribe` and every `/vts/*` entry are `feature-selected`, use `RuBackend:Ownership:Stt` or `RuBackend:Ownership:Vts`, and document Python as the default owner.

- [x] **Step 2: Verify RED**

Run:

`& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_aspnetcore_route_matrix.py -q`

Expected: failure because the JSON matrix still marks the routes `forwarded`.

- [x] **Step 3: Update matrix and docs**

Document:

- `python` default and restart-boundary rollback;
- `aspnet` public ownership with Python runtime-worker execution;
- this slice does not remove the Python worker or FastAPI compatibility mode;
- STT audio and VTS payloads are never written to diagnostics;
- exact focused/full commands and pass counts.

- [x] **Step 4: Run sequential verification**

```powershell
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_server_api.py tests\test_speech_runtime.py tests\test_vts_api.py tests\test_aspnetcore_route_matrix.py tests\test_aspnetcore_compatibility_tools.py -q
& D:\LLM\.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
git diff --check
```

Expected: all tests pass; the route checker reports 38 HTTP routes and 2 WebSocket routes. The known pinned `SQLitePCLRaw.lib.e_sqlite3` NU1903 warning is reported separately and is not hidden.

- [x] **Step 5: Commit documentation and evidence**

```powershell
git add -- backend-dotnet/compatibility-route-matrix.json tests/test_aspnetcore_route_matrix.py docs/aspnetcore-route-matrix.md docs/aspnetcore-cutover-validation.md backend-dotnet/README.md README.md docs/superpowers/plans/2026-08-11-ru-stt-vts-runtime-ownership.md
git commit -m "docs: record STT and VTS runtime ownership gates"
```

## Completion Boundary

This plan is complete when ASP.NET can explicitly own `/voice/transcribe` and all `/vts/*` routes while invoking the current Python runtime through `IRuntimeWorkerClient`, all ownership modes are restart-boundary and reversible, and the listed gates pass. It does not claim direct C# speech recognition, a C# VTube Studio motion engine, the final versioned WebSocket worker, or FastAPI removal.
