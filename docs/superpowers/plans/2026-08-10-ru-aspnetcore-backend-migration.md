# Ru ASP.NET Core Backend Migration — First Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` for inline execution, or `superpowers:subagent-driven-development` if the user chooses subagent-driven execution. Each step is tracked with checkbox syntax.

**Goal:** Build a testable ASP.NET Core `RuBackend` compatibility module that runs on `127.0.0.1:8010`, preserves the current authenticated HTTP/WebSocket interface, and forwards to the unchanged FastAPI backend on `127.0.0.1:8000`.

**Architecture:** ASP.NET Core owns loopback hosting, bearer-token authentication, readiness, HTTP forwarding, and WebSocket frame relaying. Python remains the upstream implementation for model, memory, TTS, STT, and VTS. WinUI startup and port `8000` are unchanged in this slice.

**Tech Stack:** .NET SDK 10.0.302, ASP.NET Core `net10.0`, xUnit 2.9.2, Microsoft.NET.Test.Sdk 17.12.0, `Microsoft.AspNetCore.Mvc.Testing` 10.0.0, `Microsoft.AspNetCore.TestHost` 10.0.0.

---

## File map

### New production module

- Create: `backend-dotnet/RuBackend/RuBackend.csproj` — ASP.NET Core executable targeting `net10.0`.
- Create: `backend-dotnet/RuBackend/Program.cs` — host bootstrap and endpoint registration.
- Create: `backend-dotnet/RuBackend/Configuration/BackendOptions.cs` — bind URL, upstream URL, and per-run token settings.
- Create: `backend-dotnet/RuBackend/Security/DesktopTokenMiddleware.cs` — fail-closed HTTP authentication.
- Create: `backend-dotnet/RuBackend/Security/RequestSizeMiddleware.cs` — preserves the current 8 MiB request limit.
- Create: `backend-dotnet/RuBackend/Compatibility/UpstreamRequest.cs` — transport-neutral upstream request record.
- Create: `backend-dotnet/RuBackend/Compatibility/UpstreamResponse.cs` — transport-neutral upstream response record.
- Create: `backend-dotnet/RuBackend/Compatibility/IUpstreamHttpTransport.cs` — HTTP transport interface at the Python seam.
- Create: `backend-dotnet/RuBackend/Compatibility/HttpUpstreamTransport.cs` — production `HttpClient` adapter.
- Create: `backend-dotnet/RuBackend/Compatibility/HttpForwarder.cs` — request/response forwarding policy.
- Create: `backend-dotnet/RuBackend/Compatibility/IUpstreamWebSocketConnector.cs` — WebSocket connector interface.
- Create: `backend-dotnet/RuBackend/Compatibility/ClientWebSocketConnector.cs` — production upstream WebSocket adapter.
- Create: `backend-dotnet/RuBackend/Compatibility/WebSocketRelay.cs` — cancellation-safe, frame-preserving relay.
- Create: `backend-dotnet/RuBackend/Contracts/ChatFrame.cs` — chat event envelope used by contract tests.
- Create: `backend-dotnet/RuBackend/Contracts/VoiceFrame.cs` — voice control envelope used by contract tests.

### New test module

- Create: `backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj` — `net10.0` xUnit test project.
- Create: `backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs` — in-process host with deterministic token/upstream adapters.
- Create: `backend-dotnet/RuBackend.Tests/Fakes/StubUpstreamHttpTransport.cs` — real interface fake, not a production shortcut.
- Create: `backend-dotnet/RuBackend.Tests/Fakes/StubUpstreamWebSocketConnector.cs` — connected test sockets for relay tests.
- Create: `backend-dotnet/RuBackend.Tests/Fakes/WebSocketRelayFixture.cs` — two connected test sockets and deterministic client/upstream helpers.
- Create: `backend-dotnet/RuBackend.Tests/AuthenticationTests.cs` — HTTP and WebSocket authentication behavior.
- Create: `backend-dotnet/RuBackend.Tests/RequestBoundaryTests.cs` — `OPTIONS` and oversized-body behavior.
- Create: `backend-dotnet/RuBackend.Tests/HttpForwardingTests.cs` — status, JSON, headers, and upstream failure behavior.
- Create: `backend-dotnet/RuBackend.Tests/WebSocketRelayTests.cs` — chat ordering and voice JSON/binary framing.
- Create: `backend-dotnet/RuBackend.Tests/ContractFrameTests.cs` — event envelope serialization and field names.

### Documentation

- Create: `backend-dotnet/README.md` — local run command, configuration names, and explicit statement that this slice does not replace FastAPI or WinUI startup.
- Existing spec: `docs/superpowers/specs/2026-08-10-ru-aspnetcore-backend-migration-design.md` — do not rewrite; the plan implements its first slice.

### Protected files

Do not modify in this slice:

- `src/mini_gpt/server.py`
- `src/mini_gpt/routers/*`
- `src/mini_gpt/omnivoice_tts_server.py`
- `gui-winui3/Services/BackendProcessManager.cs`
- `gui-winui3/Services/ApiService.cs`
- `gui-winui3/Services/ChatWebSocketClient.cs`
- `gui-winui3/Services/VoiceStreamClient.cs`

---

## Task 1: Scaffold the two .NET projects

**Files:** create the project files listed above; no runtime behavior beyond the empty host bootstrap.

- [ ] **Step 1: Create the executable project file.**

Create `backend-dotnet/RuBackend/RuBackend.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create the test project file.**

Create `backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" Version="10.0.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\RuBackend\RuBackend.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add the empty host bootstrap.**

Create `backend-dotnet/RuBackend/Program.cs` with no endpoints yet:

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.Run();

public partial class Program { }
```

- [ ] **Step 4: Restore and build the scaffold.**

Run:

```powershell
dotnet restore backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj
dotnet build backend-dotnet/RuBackend/RuBackend.csproj --no-restore
```

Expected: both commands succeed; no FastAPI, WinUI, or Python files are touched.

- [ ] **Step 5: Commit the scaffold.**

```powershell
git add -- backend-dotnet
git commit -m "feat: scaffold ASP.NET Core backend"
```

## Task 2: Add options, HTTP authentication, and status contract

**Files:** `Configuration/BackendOptions.cs`, `Security/DesktopTokenMiddleware.cs`, `Security/RequestSizeMiddleware.cs`, `Program.cs`, `AuthenticationTests.cs`, `RequestBoundaryTests.cs`, `BackendWebApplicationFactory.cs`.

- [ ] **Step 1: Add the deterministic test factory and write the failing authentication tests.**

Create `BackendWebApplicationFactory` before the tests so the test failure is an HTTP behavior failure rather than a missing test harness:

```csharp
public sealed class BackendWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("RuBackend:DesktopToken", "test-secret");
        builder.UseSetting("RuBackend:UpstreamUrl", "http://127.0.0.1:65531");
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-secret");
        return client;
    }
}
```

Create tests that build the in-process host with `RU_DESKTOP_TOKEN=test-secret` and assert the currently missing behavior:

```csharp
[Fact]
public async Task MissingBearerTokenReturnsUnauthorized()
{
    using var factory = new BackendWebApplicationFactory();
    using var client = factory.CreateClient();

    using var response = await client.GetAsync("/status");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}

[Fact]
public async Task ValidBearerTokenReachesStatusEndpoint()
{
    using var factory = new BackendWebApplicationFactory();
    using var client = factory.CreateAuthenticatedClient();

    using var response = await client.GetAsync("/status");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

- [ ] **Step 2: Run the focused tests and verify RED.**

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~AuthenticationTests
```

Expected: `MissingBearerTokenReturnsUnauthorized` fails because the empty host returns `404`, proving the test is exercising missing backend behavior.

- [ ] **Step 3: Add the options record.**

Create `BackendOptions` with these exact defaults:

```csharp
public sealed class BackendOptions
{
    public const string SectionName = "RuBackend";
    public string BindUrl { get; init; } = "http://127.0.0.1:8010";
    public string UpstreamUrl { get; init; } = "http://127.0.0.1:8000";
    public string DesktopToken { get; init; } = "";
    public long MaxRequestBodyBytes { get; init; } = 8 * 1024 * 1024;
}
```

Bind configuration from the `RuBackend` section, then override `DesktopToken` with `RU_DESKTOP_TOKEN` when that environment variable is present. Do not generate a fallback token inside the backend.

- [ ] **Step 4: Write and run the request-boundary tests.**

Create `RequestBoundaryTests.cs` with these assertions:

```csharp
[Fact]
public async Task OptionsRequestDoesNotRequireBearerToken()
{
    using var factory = new BackendWebApplicationFactory();
    using var client = factory.CreateClient();

    using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/status"));

    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
}

[Fact]
public async Task DeclaredOversizedRequestReturnsPayloadTooLarge()
{
    using var factory = new BackendWebApplicationFactory();
    using var client = factory.CreateAuthenticatedClient();
    using var request = new HttpRequestMessage(HttpMethod.Post, "/settings");
    request.Content = new ByteArrayContent(new byte[1]);
    request.Content.Headers.ContentLength = 8 * 1024 * 1024 + 1;

    using var response = await client.SendAsync(request);

    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
}
```

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~RequestBoundaryTests
```

Expected: the tests fail before `OPTIONS` and request-size handling are implemented.

- [ ] **Step 5: Add fail-closed middleware, request limits, and status endpoint.**

Implement the following rules in `DesktopTokenMiddleware`:

```csharp
if (context.Request.Method == "OPTIONS")
{
    await _next(context);
    return;
}

var expected = options.DesktopToken;
var supplied = context.Request.Headers.Authorization.ToString();
var valid = !string.IsNullOrWhiteSpace(expected)
    && supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
    && CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(expected),
        Encoding.UTF8.GetBytes(supplied[7..].Trim()));

if (!valid)
{
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new { detail = "Unauthorized" });
    return;
}

await _next(context);
```

Register the middleware before endpoints and map `/status` to the exact current response shape:

```csharp
app.MapGet("/status", () => Results.Ok(new
{
    status = "ok",
    message = "Backend is running and ready for the WinUI3 UI."
}));
```

Add the request boundary rules before endpoint execution:

- `OPTIONS` requests bypass bearer authentication and return `204 No Content` through `app.MapMethods("/{**path}", new[] { "OPTIONS" }, () => Results.NoContent())`.
- `Content-Length` above `8 * 1024 * 1024` returns `413` with `{"detail":"Request body too large"}`.
- `POST`, `PUT`, and `PATCH` bodies are counted while read and return the same `413` shape when the limit is exceeded.

`RequestSizeMiddleware` must reject a declared oversized body before reading it and must not log the body.

- [ ] **Step 6: Run the focused tests and verify GREEN.**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter "FullyQualifiedName~AuthenticationTests|FullyQualifiedName~RequestBoundaryTests"
```

Expected: all authentication/status tests pass.

- [ ] **Step 7: Commit the authentication and request-boundary slice.**

```powershell
git add -- backend-dotnet/RuBackend backend-dotnet/RuBackend.Tests
git commit -m "feat: add ASP.NET Core desktop authentication"
```

## Task 3: Add the HTTP upstream adapter and readiness forwarding

**Files:** `Compatibility/UpstreamRequest.cs`, `UpstreamResponse.cs`, `IUpstreamHttpTransport.cs`, `HttpUpstreamTransport.cs`, `HttpForwarder.cs`, `Program.cs`, `HttpForwardingTests.cs`, `BackendWebApplicationFactory.cs`.

- [ ] **Step 1: Write failing forwarding tests.**

The test fake must implement the transport interface that does not exist yet. The tests must cover:

```csharp
[Fact]
public async Task SettingsPreservesUpstreamStatusAndJson()
{
    using var factory = new BackendWebApplicationFactory(
        new StubUpstreamHttpTransport(
            new UpstreamResponse(
                StatusCodes.Status200OK,
                "application/json",
                "{\"backend\":\"llama.cpp\"}")));
    using var client = factory.CreateAuthenticatedClient();

    using var response = await client.GetAsync("/settings");
    var json = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("{\"backend\":\"llama.cpp\"}", json);
}

[Fact]
public async Task UpstreamFailureMakesReadinessUnavailable()
{
    using var factory = new BackendWebApplicationFactory(
        new StubUpstreamHttpTransport(new HttpRequestException("upstream down")));
    using var client = factory.CreateAuthenticatedClient();

    using var response = await client.GetAsync("/settings");

    Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
}
```

Give `StubUpstreamHttpTransport` exactly two constructors so the tests describe both outcomes without network access:

```csharp
public StubUpstreamHttpTransport(UpstreamResponse response)
{
    _response = response;
}

public StubUpstreamHttpTransport(Exception error)
{
    _error = error;
}
```

Replace the factory's existing `ConfigureWebHost` implementation and add an optional transport so the registered production transport is replaced only when the test supplies one:

```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;

private readonly IUpstreamHttpTransport? _httpTransport;

public BackendWebApplicationFactory(IUpstreamHttpTransport? httpTransport = null)
{
    _httpTransport = httpTransport;
}

protected override void ConfigureWebHost(IWebHostBuilder builder)
{
    builder.UseSetting("RuBackend:DesktopToken", "test-secret");
    builder.UseSetting("RuBackend:UpstreamUrl", "http://127.0.0.1:65531");
    builder.ConfigureTestServices(services =>
    {
        if (_httpTransport is not null)
        {
            services.RemoveAll<IUpstreamHttpTransport>();
            services.AddSingleton(_httpTransport);
        }
    });
}
```

- [ ] **Step 2: Run the forwarding tests and verify RED.**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~HttpForwardingTests
```

Expected: compile/test failure because the transport and forwarding module do not exist.

- [ ] **Step 3: Add transport records and interface.**

Use these types:

```csharp
public sealed record UpstreamRequest(
    HttpMethod Method,
    string PathAndQuery,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);

public sealed record UpstreamResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string>? Headers = null);

public interface IUpstreamHttpTransport
{
    Task<UpstreamResponse> SendAsync(UpstreamRequest request, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement the production HTTP adapter.**

`HttpUpstreamTransport` must:

1. construct a `Uri` from `BackendOptions.UpstreamUrl` and `PathAndQuery`;
2. copy the incoming method, bearer header, content type, and body;
3. use `HttpClient.SendAsync` with the caller cancellation token;
4. return status, content type, body, and safe response headers;
5. let `HttpRequestException` reach `HttpForwarder`, which maps it to `503`.

Do not log authorization headers, request bodies, chat text, or audio bytes.

- [ ] **Step 5: Implement forwarding for `/status` and `/settings`.**

`HttpForwarder` should accept the current `HttpContext`, build `UpstreamRequest`, call `IUpstreamHttpTransport`, and copy status/content type/body to the response. Map only transport failures to:

```json
{"detail":"Backend upstream unavailable"}
```

with status `503`. Preserve every upstream status code and JSON body when transport succeeds.

Map `/status` and `/settings` through this forwarder. Keep authentication middleware before both routes.

- [ ] **Step 6: Run focused tests and verify GREEN.**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~HttpForwardingTests
```

Expected: forwarding, JSON preservation, and `503` tests pass.

- [ ] **Step 7: Commit the HTTP slice.**

```powershell
git add -- backend-dotnet/RuBackend backend-dotnet/RuBackend.Tests
git commit -m "feat: forward ASP.NET Core HTTP contracts"
```

## Task 4: Add frame-preserving WebSocket relay

**Files:** `Compatibility/IUpstreamWebSocketConnector.cs`, `ClientWebSocketConnector.cs`, `WebSocketRelay.cs`, `Contracts/ChatFrame.cs`, `Contracts/VoiceFrame.cs`, `Program.cs`, `WebSocketRelayTests.cs`, `ContractFrameTests.cs`, and the test fakes.

- [ ] **Step 1: Write failing frame and relay tests.**

The contract tests must assert exact JSON fields, while relay tests must send a text frame followed by a binary frame and verify the client receives the same message types, payloads, and order:

```csharp
[Fact]
public void VoiceAudioEnvelopeUsesStableFieldNames()
{
    var json = JsonSerializer.Serialize(new VoiceFrame(
        "audio", "r1", 0, 24_000, "wav"));

    Assert.Equal(
        "{\"type\":\"audio\",\"request_id\":\"r1\",\"sequence\":0,\"sample_rate\":24000,\"format\":\"wav\"}",
        json);
}

[Fact]
public async Task RelayPreservesTextThenBinaryVoiceFrames()
{
    using var fixture = await WebSocketRelayFixture.StartAsync();
    using var client = await fixture.ConnectClientAsync("/voice/stream?token=test-secret");

    await fixture.Upstream.SendTextAsync("{\"type\":\"audio\"}");
    await fixture.Upstream.SendBinaryAsync(new byte[] { 1, 2, 3 });

    var text = await client.ReceiveTextAsync();
    var binary = await client.ReceiveBinaryAsync();

    Assert.Equal("{\"type\":\"audio\"}", text);
    Assert.Equal(new byte[] { 1, 2, 3 }, binary);
}
```

- [ ] **Step 2: Run WebSocket tests and verify RED.**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~WebSocketRelayTests
```

Expected: compile/test failure because the connector, relay, and frame records do not exist.

- [ ] **Step 3: Add contract records with explicit JSON names.**

Use `JsonPropertyName` attributes so the wire contract does not depend on serializer naming options:

```csharp
public sealed record ChatFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null);

public sealed record VoiceFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("sequence")] int? Sequence = null,
    [property: JsonPropertyName("sample_rate")] int? SampleRate = null,
    [property: JsonPropertyName("format")] string? Format = null);
```

- [ ] **Step 4: Implement the upstream connector.**

Define the connector seam as:

```csharp
public interface IUpstreamWebSocketConnector
{
    Task<WebSocket> ConnectAsync(Uri upstreamUri, CancellationToken cancellationToken);
}
```

`ClientWebSocketConnector` must:

1. derive `ws`/`wss` from `BackendOptions.UpstreamUrl`;
2. preserve the original path and query, including the token query value;
3. connect with a cancellation token;
4. dispose the socket when connect or relay fails;
5. expose a single `ConnectAsync(Uri, CancellationToken)` method through `IUpstreamWebSocketConnector`.

- [ ] **Step 5: Implement the relay.**

`WebSocketRelay` must run exactly two loops:

- client → upstream: copy text/binary frames until close/cancellation;
- upstream → client: copy text/binary frames until close/cancellation.

Use one 64 KiB bounded buffer per direction, preserve `EndOfMessage`, and cancel the opposite loop when either socket closes. Never parse, reorder, or buffer a complete chat reply. Never treat a binary audio frame as text.

- [ ] **Step 6: Add authenticated `/chat` and `/voice/stream` endpoints.**

Before accepting a WebSocket, validate `context.Request.Query["token"]` with the same fixed-time comparison used by HTTP. Reject invalid credentials with HTTP `401`; valid requests connect to the configured upstream and enter `WebSocketRelay`.

The endpoint must not create a second voice queue or own an audio playback state machine. It only preserves the current Python protocol.

- [ ] **Step 7: Run WebSocket and contract tests and verify GREEN.**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter "FullyQualifiedName~WebSocketRelayTests|FullyQualifiedName~ContractFrameTests"
```

Expected: all frame serialization, authentication, text ordering, binary ordering, cancellation, and close tests pass.

- [ ] **Step 8: Commit the WebSocket slice.**

```powershell
git add -- backend-dotnet/RuBackend backend-dotnet/RuBackend.Tests
git commit -m "feat: relay chat and voice websocket contracts"
```

## Task 5: Add local run documentation and perform the migration gate

**Files:** `backend-dotnet/README.md`; no existing runtime file changes.

- [ ] **Step 1: Document the parallel run command.**

Document these exact commands and environment values:

```powershell
$env:RU_DESKTOP_TOKEN = "local-test-secret"
$env:RuBackend__UpstreamUrl = "http://127.0.0.1:8000"
dotnet run --project backend-dotnet/RuBackend/RuBackend.csproj -- --urls http://127.0.0.1:8010
```

State explicitly that `start_dev.bat` and `BackendProcessManager.cs` still start FastAPI on `8000`; this backend is parallel-only until the cutover plan is approved.

- [ ] **Step 2: Run the complete new backend test project.**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
```

Expected: all new tests pass with zero failures.

- [ ] **Step 3: Run the existing WinUI tests separately.**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore
```

Expected: existing test count remains green; report environment/build failures separately from backend code failures.

- [ ] **Step 4: Run the existing Python contract suite separately.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_voice_stream.py tests/test_vts_api.py -q
```

Expected: the three named protocol suites run from the repository root. Do not run heavyweight Python, .NET, and Cargo checks in parallel.

- [ ] **Step 5: Run static checks and inspect docs.**

```powershell
git diff --check
git status --short --branch
rg -n -i "FastAPI|ASP.NET Core|RuBackend|8010" README.md docs backend-dotnet
```

Confirm that no current README claim was changed prematurely, no historical document was deleted, and the new `backend-dotnet/README.md` clearly labels the parallel state.

- [ ] **Step 6: Commit documentation and final first-slice state.**

```powershell
git add -- backend-dotnet/README.md
git commit -m "docs: explain parallel ASP.NET Core backend"
```

## Final handoff

Report:

- exact new project paths;
- each focused and full test command with result;
- whether the real local WebSocket relay test passed;
- whether FastAPI and WinUI remained untouched;
- any package restore or environment failure separately from code failures;
- the explicit next gate: switch WinUI/backend ownership only after a separate cutover plan.

Do not claim the ASP.NET Core migration is complete after this plan. This plan completes only the parallel compatibility slice; live OmniVoice playback and the `8000` cutover remain later acceptance gates.
