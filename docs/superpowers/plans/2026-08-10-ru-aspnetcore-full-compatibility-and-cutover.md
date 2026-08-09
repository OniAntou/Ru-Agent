# Ru ASP.NET Core Full Compatibility and Cutover — Implementation Plan

> REQUIRED SUB-SKILL: Use superpowers:executing-plans for inline execution on the approved main branch. Use superpowers:verification-before-completion before reporting any gate as passed.

**Goal:** Move the public loopback backend contract from FastAPI to an ASP.NET Core edge without changing WinUI REST/WebSocket URLs, while keeping Python responsible for model generation, memory, OmniVoice, STT, VTS, and screen-reaction behavior.

**Architecture:** FastAPI remains the Python worker. In parallel mode, RuBackend listens on 8010 and forwards to FastAPI on 8000. In cutover mode, RuBackend owns public port 8000 and forwards to a loopback-only FastAPI worker on 8001. The WinUI process manager owns both children in one Windows Job Object and waits for readiness through the public edge.

**Tech Stack:** ASP.NET Core net10.0, the existing .NET SDK, xUnit, Microsoft.AspNetCore.Mvc.Testing, Python pytest, httpx, websockets, PowerShell process orchestration, and the existing WinUI 3 net10.0-windows10.0.26100.0 application.

**Global Constraints:**

- Work directly on D:\LLM and the user-approved main branch; preserve unrelated dirty files and stage only migration paths.
- Keep the public WinUI port at 8000. Use 8010 for the parallel ASP.NET host and 8001 for the private FastAPI worker during cutover.
- Use one per-run RU_DESKTOP_TOKEN for edge and worker. Never log bearer tokens, request bodies, chat text, audio bytes, or private VTS metadata.
- Do not rewrite Python model, memory, OmniVoice, STT, VTS, screen-awareness, or WebSocket business logic in C#.
- Keep WebSocket paths as relay paths. The edge must not parse, reorder, buffer, or duplicate chat/voice state.
- Fail closed when the worker, published edge executable, Job Object, or readiness probe fails. Do not automatically fall back to another host on the same port.
- Keep the default WinUI host mode as FastAPI until the live edge, process ownership, benchmark, and real OmniVoice-audio gates all pass. The ASP.NET mode must be explicit and testable before activation.
- Run heavy backend, GUI, and Python suites sequentially to avoid known Windows GPU/pagefile pressure.
- After each implementation slice, inspect README.md, backend-dotnet/README.md, relevant docs/, and migration documents; update current claims and leave historical design records intact.

## File map

### Route inventory and Python compatibility

- Create backend-dotnet/compatibility-route-matrix.json — canonical list of the 38 HTTP method/path pairs and two WebSocket paths after adding GET /memory.
- Create docs/aspnetcore-route-matrix.md — human-readable ownership/classification view generated from the JSON matrix.
- Create scripts/check_aspnetcore_route_matrix.py — AST-based check against FastAPI router decorators and server.py prefixes.
- Create tests/test_aspnetcore_route_matrix.py — deterministic route-inventory tests.
- Modify src/mini_gpt/routers/status.py — add the missing read-only GET /memory contract.
- Modify src/mini_gpt/_server_shared.py, src/mini_gpt/backend_core.py, and src/mini_gpt/activity_log.py — honor an optional RU_DATA_ROOT for isolated live probes while preserving current defaults.
- Modify tests/test_server_api.py — test memory DTO shape, filtering, and isolated storage.

### ASP.NET Core compatibility edge

- Modify backend-dotnet/RuBackend/Program.cs — replace the narrow HTTP route list with a generic non-WebSocket catch-all while preserving explicit WebSocket and OPTIONS precedence.
- Modify backend-dotnet/RuBackend/Compatibility/HttpForwarder.cs — preserve escaped path, query, method, body, request headers, response body/status/content type, safe response headers, cancellation, and the existing 503 body.
- Modify backend-dotnet/RuBackend/Compatibility/HttpUpstreamTransport.cs only where required by the expanded request contract.
- Modify backend-dotnet/RuBackend.Tests/Fakes/StubUpstreamHttpTransport.cs — capture requests and support deterministic response/error factories.
- Modify backend-dotnet/RuBackend.Tests/HttpForwardingTests.cs — cover arbitrary method/path/query/body/header forwarding and catch-all precedence.
- Modify backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs — keep all tests on deterministic fakes and never require a live port.

### Live harness and benchmark

- Create scripts/aspnetcore_compatibility_harness.py — start isolated FastAPI and RuBackend processes, probe safe route entries, exercise chat/voice sockets, and terminate children in finally.
- Create scripts/benchmark_aspnetcore_compat.py — compare direct-worker and edge timings without persisting response content.
- Create tests/test_aspnetcore_compatibility_tools.py — test percentile calculations, redaction, route probe classification, and failure accounting without starting processes.
- Modify pyproject.toml only if the existing server extra does not expose the already-used websockets client needed by the harness.
- Add artifacts/aspnetcore/ to .gitignore if the benchmark output directory is not already ignored.

### WinUI process ownership and publishing

- Create gui-winui3/Services/BackendHostMode.cs — explicit FastApi and AspNetCore modes with strict parsing.
- Create gui-winui3/Services/BackendLaunchPlan.cs — pure, testable worker/edge command and environment construction.
- Create gui-winui3/Services/BackendProcessRuntime.cs — injectable process launcher, Job Object wrapper, readiness probe, and child-handle abstractions.
- Modify gui-winui3/Services/BackendProcessManager.cs — own one or two children through the shared Job Object, expose Port=8000, support explicit mode, and clean up fail-closed.
- Modify gui-winui3/MainWindow.xaml.cs — display the selected backend mode and last startup error without changing API client ports.
- Create gui-winui3.Tests/BackendLaunchPlanTests.cs — test exact command lines, ports, environment variables, and mode parsing.
- Create gui-winui3.Tests/BackendProcessManagerTests.cs — use fake launcher/job/readiness seams to verify assignment, readiness, failure cleanup, and disposal.
- Modify build.bat — publish the self-contained RuBackend.exe before publishing RuApp.exe.

### Documentation and acceptance record

- Modify backend-dotnet/README.md — document generic compatibility, parallel mode, opt-in cutover mode, private worker port, publish path, and rollback.
- Modify root README.md only where current startup/build claims need to describe the new explicit host mode.
- Create docs/aspnetcore-cutover-validation.md — record actual commands, measured benchmark output, process/WS gates, voice-audio evidence, and whether the default mode was changed.
- Do not delete historical FastAPI migration/design documents; mark them as historical or parallel only when their current status is otherwise ambiguous.

## Task 1: Build and verify the route matrix first

**Files:** backend-dotnet/compatibility-route-matrix.json, docs/aspnetcore-route-matrix.md, scripts/check_aspnetcore_route_matrix.py, tests/test_aspnetcore_route_matrix.py.

- [ ] Step 1: Write the failing route-inventory test.

Add a test that imports the checker and asserts that the matrix contains exactly these HTTP routes, with the /observation router prefix applied:

~~~text
GET /status
GET /activity-log
DELETE /activity-log
GET /models
GET /history
GET /memory
DELETE /memory
GET /settings
POST /settings
GET /sessions
POST /sessions/new
POST /sessions/{session_id}/activate
DELETE /sessions/{session_id}
POST /observation/preflight
GET /observation/vision-config
POST /observation/react
POST /observation/cancel
POST /observation/ack
POST /observation/start
POST /observation/stop
GET /observation/status
GET /voice/status
POST /voice/latency
POST /voice/tts
POST /voice/warmup
POST /voice/release
POST /voice/transcribe
GET /vts/mouse-look
POST /vts/mouse-look
GET /vts/overlay
POST /vts/overlay
GET /vts/status
POST /vts/connect
GET /vts/config
POST /vts/config
POST /vts/activity
POST /vts/action
POST /vts/speaking
~~~

Also assert the WebSocket entries are exactly WS /chat and WS /voice/stream, and assert each matrix entry has source, classification, and winui_callers fields. Classification values are forwarded, websocket-relay, python-parity, or unused-but-forwarded.

- [ ] Step 2: Run the focused test and verify RED.

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_aspnetcore_route_matrix.py -q
~~~

Expected: collection fails because the checker and matrix do not exist yet.

- [ ] Step 3: Add the canonical JSON matrix.

Use one object per route with this exact shape:

~~~json
{
  "method": "GET",
  "path": "/memory",
  "transport": "http",
  "source": "src/mini_gpt/routers/status.py",
  "classification": "python-parity",
  "winui_callers": ["ApiService.GetMemoryAsync"]
}
~~~

Mark GET /memory as python-parity because the WinUI caller already exists but FastAPI does not yet expose it. Mark both WebSocket routes as websocket-relay. Include unused Python routes as unused-but-forwarded rather than dropping them from the public compatibility surface.

- [ ] Step 4: Implement the AST-based checker.

discover_fastapi_routes(repo_root) must:

1. parse src/mini_gpt/server.py for app.include_router(...) and collect router prefixes;
2. parse every src/mini_gpt/routers/*.py decorator named get, post, put, patch, delete, or websocket;
3. combine literal decorator paths with the router prefix;
4. compare (method, path, transport) sets with the JSON matrix; and
5. print Route matrix OK: 38 HTTP routes, 2 WebSocket routes on success or a sorted missing/extra diff on failure.

The checker must not import the FastAPI application or initialize model/voice code.

- [ ] Step 5: Generate the readable route document and run GREEN.

Generate docs/aspnetcore-route-matrix.md from the JSON matrix with grouped tables for status/settings, sessions, observation, voice, VTS, and WebSocket routes. Include the parallel topology and the note that GET /memory is a pre-existing WinUI/FastAPI drift fixed in the next task.

Run:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_aspnetcore_route_matrix.py -q
~~~

Expected: the checker reports 38 HTTP routes, 2 WebSocket routes; focused tests pass.

- [ ] Step 6: Commit only the inventory slice.

~~~powershell
git add -- backend-dotnet/compatibility-route-matrix.json docs/aspnetcore-route-matrix.md scripts/check_aspnetcore_route_matrix.py tests/test_aspnetcore_route_matrix.py
git commit -m "docs: add ASP.NET Core compatibility route matrix"
~~~

## Task 2: Replace narrow HTTP mappings with the generic compatibility seam

**Files:** backend-dotnet/RuBackend/Program.cs, backend-dotnet/RuBackend/Compatibility/HttpForwarder.cs, backend-dotnet/RuBackend/Compatibility/HttpUpstreamTransport.cs, backend-dotnet/RuBackend.Tests/Fakes/StubUpstreamHttpTransport.cs, backend-dotnet/RuBackend.Tests/HttpForwardingTests.cs, backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs.

- [ ] Step 1: Add failing tests for method/path/body/header preservation.

Extend the stub with LastRequest and a response factory. Add GenericRoutePreservesMethodEscapedPathQueryBodyAndHeaders that sends:

~~~text
PUT /sessions/room%2Fone/activate?first=1&first=2
Authorization: Bearer test-secret
Content-Type: application/json; charset=utf-8
Body: {"active":true}
~~~

Assert the captured request has HttpMethod.Put, the escaped path and exact query string, both required headers, and byte-identical UTF-8 body. Return 201 with application/json, ETag, and a JSON body; assert the client receives all of them unchanged.

Add UnknownPathIsStillForwarded for GET /future-contract?revision=7, proving that adding a Python route does not require another C# map.

Add SpecificWebSocketRouteWinsOverHttpCatchAll using the existing connector fake; assert the WebSocket connector is called and the HTTP transport is not called for /chat.

- [ ] Step 2: Run the focused tests and verify RED.

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~HttpForwardingTests
~~~

Expected: new tests fail because the current program only maps /status and /settings, and the stub does not yet capture requests.

- [ ] Step 3: Register endpoint precedence explicitly.

Keep the existing middleware order:

~~~csharp
app.UseWebSockets();
app.UseMiddleware<DesktopTokenMiddleware>();
app.UseMiddleware<RequestSizeMiddleware>();
~~~

Then register:

~~~csharp
app.MapMethods("/{**path}", new[] { HttpMethods.Options }, () => Results.NoContent());
app.MapGet("/chat", HandleWebSocketAsync);
app.MapGet("/voice/stream", HandleWebSocketAsync);
app.Map("/{**path}", (HttpContext context, HttpForwarder forwarder, CancellationToken cancellationToken) =>
    forwarder.ForwardAsync(context, cancellationToken));
~~~

The generic route must not add a second WebSocket implementation. Endpoint specificity must select the two explicit WebSocket routes before the catch-all; the test in Step 1 is the regression gate.

- [ ] Step 4: Preserve the raw HTTP contract in HttpForwarder.

Build PathAndQuery with URI components rather than a decoded path:

~~~csharp
var pathAndQuery = context.Request.PathBase.ToUriComponent()
    + context.Request.Path.ToUriComponent()
    + context.Request.QueryString.Value;
~~~

Copy HttpMethod, Authorization, Content-Type, and the complete request body. Keep request-size enforcement in RequestSizeMiddleware; do not add a second body limit or log body bytes.

On a successful transport response, preserve status, body bytes, content type, and only the existing safe response headers (Cache-Control, ETag, Last-Modified, Location, Retry-After). Preserve an empty body for 204. On HttpRequestException, return exactly {"detail":"Backend upstream unavailable"} with status 503 and application/json. Pass RequestAborted to all body writes and upstream calls.

- [ ] Step 5: Run all .NET backend tests and verify GREEN.

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
~~~

Expected: all existing and new tests pass, including authentication, OPTIONS, 413, 503, HTTP forwarding, and WebSocket relay tests.

- [ ] Step 6: Commit the complete HTTP seam.

~~~powershell
git add -- backend-dotnet/RuBackend backend-dotnet/RuBackend.Tests
git commit -m "feat: forward the complete ASP.NET Core HTTP contract"
~~~

## Task 3: Repair GET /memory and isolate live test data

**Files:** src/mini_gpt/routers/status.py, src/mini_gpt/_server_shared.py, src/mini_gpt/backend_core.py, src/mini_gpt/activity_log.py, tests/test_server_api.py.

- [ ] Step 1: Add failing Python tests for the existing WinUI DTO.

Add tests that point server.core.memory_db_path, memory_index_path, and memory_texts_path at tmp_path, reset server.core.memory_store, insert two active records with StructuredMemoryStore.upsert, and call authenticated GET /memory.

Assert the response is a JSON list of objects with exactly the WinUI-compatible fields id, text, category, and created_at; assert text equals record.content, category equals record.kind.value, and created_at is the stored ISO timestamp. Add a second record with an expired expires_at and assert list_active() filtering excludes it. Keep the existing DELETE /memory behavior covered.

- [ ] Step 2: Run the focused Python tests and verify RED.

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py -q -k memory
~~~

Expected: the new read test returns 404 because FastAPI currently exposes only DELETE /memory.

- [ ] Step 3: Add the read-only route without moving memory ownership.

Add this behavior to src/mini_gpt/routers/status.py:

~~~python
@router.get("/memory")
def get_memory():
    records = core.get_memory_store().list_active()
    return [
        {
            "id": record.id,
            "text": record.content,
            "category": record.kind.value,
            "created_at": record.created_at.isoformat(),
        }
        for record in records
    ]
~~~

Do not expose evidence, confidence, scope, VTS metadata, or internal SQLite fields through this WinUI DTO. Do not add a .NET memory store.

- [ ] Step 4: Add an opt-in isolated data root.

Preserve existing production defaults exactly, but when RU_DATA_ROOT is non-empty:

- _server_shared.py derives settings, history, and session paths below that directory;
- backend_core.py derives memory database/index/text paths below that directory; and
- activity_log.py derives its JSONL path below that directory.

Keep existing test monkeypatches valid by retaining module-level path constants. Add a test that imports the modules in a subprocess with RU_DATA_ROOT and confirms no file is created below normal user/project data paths.

- [ ] Step 5: Run focused and existing Python API tests.

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py -q -k memory
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_voice_stream.py tests/test_vts_api.py -q
~~~

Expected: memory-focused tests and the full selected Python API suite pass. The second command runs only after the first completes.

- [ ] Step 6: Commit the Python compatibility slice.

~~~powershell
git add -- src/mini_gpt/routers/status.py src/mini_gpt/_server_shared.py src/mini_gpt/backend_core.py src/mini_gpt/activity_log.py tests/test_server_api.py
git commit -m "fix: restore the WinUI memory read contract"
~~~

## Task 4: Add isolated live compatibility and latency tools

**Files:** scripts/aspnetcore_compatibility_harness.py, scripts/benchmark_aspnetcore_compat.py, tests/test_aspnetcore_compatibility_tools.py, pyproject.toml if required, .gitignore if required.

- [ ] Step 1: Write pure failing tests for metrics and redaction.

Test functions that calculate p50/p95 from known samples, count failed probes, classify mutating routes as skipped in safe mode, and redact a token from diagnostic text. Assert benchmark records contain only route names, status codes, counts, and numeric timings—not response bodies, request text, tokens, or audio bytes.

- [ ] Step 2: Run the focused tool tests and verify RED.

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_aspnetcore_compatibility_tools.py -q
~~~

Expected: collection fails because the tool modules do not exist.

- [ ] Step 3: Implement the isolated compatibility harness.

The harness accepts --project-root, --worker-port (default 8001), --edge-port (default 8010), --samples (default 20), and --require-voice-audio. It must:

1. create a temporary directory and set RU_DATA_ROOT to it for both children;
2. create an in-memory token and set it only in child environments and probe URLs;
3. start FastAPI with python -m mini_gpt.server --host 127.0.0.1 --port 8001;
4. start RuBackend with dotnet run --project backend-dotnet/RuBackend/RuBackend.csproj --no-launch-profile -- --urls http://127.0.0.1:8010, RuBackend__UpstreamUrl=http://127.0.0.1:8001, and the same token;
5. wait for GET /settings through the edge, never declaring readiness from the worker alone;
6. probe every safe GET entry in the route matrix and use OPTIONS for non-mutating contract checks; skip state-changing POST/DELETE routes with an explicit recorded reason in safe mode;
7. connect to /chat and /voice/stream through the edge, send a malformed chat frame and an invalid voice control frame, and record returned error/control event types and order without sending user text;
8. when --require-voice-audio is supplied, send a fixed synthetic voice request and require an audio control frame followed immediately by a non-empty binary frame before recording voice_audio_confirmed=true; and
9. terminate edge and worker in finally, wait for exit, kill only those child PIDs if needed, and report non-zero if any child remains alive.

The default safe run may report voice_audio_confirmed=false when OmniVoice is unavailable; that is a pending acceptance gate, not a successful migration claim.

- [ ] Step 4: Implement the direct-versus-edge benchmark.

benchmark_aspnetcore_compat.py accepts --direct-url, --edge-url, --token, --samples, and --output. For each target, measure only authenticated GET /settings and WebSocket handshake/close for /chat and /voice/stream. Record warm-up count, sample count, p50, p95, failures, and edge_minus_direct_ms; never store response bodies. Use a deterministic local request and the same token for both targets. Exit non-zero on failed samples or when the caller asks for a maximum relative p95 overhead and the edge exceeds it.

- [ ] Step 5: Run the pure tool tests and static checks.

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_aspnetcore_compatibility_tools.py tests/test_aspnetcore_route_matrix.py -q
& D:\LLM\.venv\Scripts\python.exe -m py_compile scripts/aspnetcore_compatibility_harness.py scripts/benchmark_aspnetcore_compat.py
~~~

Expected: focused tests pass and both scripts compile. Do not run the live harness until the process and edge slices below are available.

- [ ] Step 6: Commit the live-tool slice.

~~~powershell
git add -- scripts/aspnetcore_compatibility_harness.py scripts/benchmark_aspnetcore_compat.py tests/test_aspnetcore_compatibility_tools.py pyproject.toml .gitignore
git commit -m "test: add ASP.NET Core compatibility harness"
~~~

## Task 5: Make WinUI own FastAPI or ASP.NET Core explicitly

**Files:** gui-winui3/Services/BackendHostMode.cs, gui-winui3/Services/BackendLaunchPlan.cs, gui-winui3/Services/BackendProcessRuntime.cs, gui-winui3/Services/BackendProcessManager.cs, gui-winui3/MainWindow.xaml.cs, gui-winui3.Tests/BackendLaunchPlanTests.cs, gui-winui3.Tests/BackendProcessManagerTests.cs.

- [ ] Step 1: Add failing launch-plan tests.

Test BackendLaunchPlan.Create for these exact results.

FastAPI mode:

~~~text
children: 1
role: fastapi-worker
executable: D:\LLM\.venv\Scripts\python.exe
arguments: -m mini_gpt.server --host 127.0.0.1 --port 8000
RU_DESKTOP_TOKEN: generated per-run token shared by both children
public port: 8000
worker port: null
~~~

ASP.NET Core mode:

~~~text
children: 2, in worker-then-edge order
worker arguments: -m mini_gpt.server --host 127.0.0.1 --port 8001
edge executable: D:\LLM\backend-dotnet\RuBackend\bin\Release\net10.0\win-x64\publish\RuBackend.exe
edge environment: RuBackend__BindUrl=http://127.0.0.1:8000
edge environment: RuBackend__UpstreamUrl=http://127.0.0.1:8001
public port: 8000
worker port: 8001
~~~

Assert both children receive exactly the same token, the worker binds loopback, invalid mode strings fail with a clear error, and a missing published executable is rejected before any child starts.

- [ ] Step 2: Run GUI-focused tests and verify RED.

~~~powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~Backend
~~~

Expected: compile/test failure because the explicit mode and launch-plan types do not exist.

- [ ] Step 3: Implement mode parsing and pure launch planning.

BackendHostMode.Parse accepts only empty/fastapi and aspnetcore; empty defaults to FastApi until the final acceptance gate. Read RU_BACKEND_HOST_MODE, RU_BACKEND_WORKER_PORT, and optional RU_RUBACKEND_EXE once per manager construction. Reject worker/public port collisions and out-of-range ports.

BackendLaunchPlan must be independent of WinUI/XAML and contain role, executable, arguments, working directory, environment, public port, and worker port. It must not start processes or read user data.

- [ ] Step 4: Add injectable process, Job Object, and readiness seams.

Move the existing Win32 Job Object code behind IBackendJobFactory/IBackendJob, and move process creation behind IBackendProcessFactory/IBackendProcessHandle. Add an IBackendReadinessProbe whose production implementation sends authenticated GET /settings to 127.0.0.1:8000 with bounded retries and cancellation.

The production process factory redirects each role to separate lifecycle log files (logs/backend_fastapi_out.log, logs/backend_fastapi_err.log, logs/backend_aspnetcore_out.log, logs/backend_aspnetcore_err.log) without writing request content. The production Job Object sets JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE before the first child starts.

- [ ] Step 5: Refactor BackendProcessManager around the plan.

Keep AuthSecret, Port=8000, IsRunning, and StartBackendAsync compatible with the existing MainWindow call. Start in plan order, assign every child to the same Job Object immediately, then wait for public /settings. In ASP.NET Core mode, readiness through public 8000 proves both edge and worker connectivity.

On missing executable, process start failure, Job Object assignment failure, cancellation, child exit, or readiness timeout: kill/dispose every started child, close the Job Object, set a redacted LastError, return false, and never start a second mode automatically. Dispose must terminate all owned children and close the Job Object exactly once.

- [ ] Step 6: Add fake process-manager tests and run GREEN.

Fake launcher/job/readiness implementations record child roles, assigned handles, kill/dispose calls, readiness port, and token-free errors. Add tests for:

- two children assigned to one Job Object in ASP.NET Core mode;
- readiness called against public 8000, not worker 8001;
- readiness failure cleaning up both children;
- missing edge executable causing no child start;
- Dispose killing all children; and
- FastAPI mode retaining one child on public 8000.

Run:

~~~powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~Backend
~~~

Expected: all launch-plan and process-manager tests pass.

- [ ] Step 7: Update the WinUI status text without changing clients.

MainWindow.xaml.cs uses the process manager display name and LastError in startup status messages. ApiService, ChatWebSocketClient, and VoiceStreamClient continue to receive Port=8000; no client learns about 8001.

- [ ] Step 8: Commit the process ownership slice.

~~~powershell
git add -- gui-winui3/Services/BackendHostMode.cs gui-winui3/Services/BackendLaunchPlan.cs gui-winui3/Services/BackendProcessRuntime.cs gui-winui3/Services/BackendProcessManager.cs gui-winui3/MainWindow.xaml.cs gui-winui3.Tests/BackendLaunchPlanTests.cs gui-winui3.Tests/BackendProcessManagerTests.cs
git commit -m "feat: support explicit ASP.NET Core backend ownership"
~~~

## Task 6: Publish the edge and document the two startup modes

**Files:** build.bat, backend-dotnet/README.md, README.md, docs/aspnetcore-cutover-validation.md.

- [ ] Step 1: Add the backend publish step to build.bat.

Publish before the GUI with:

~~~bat
dotnet publish "%~dp0backend-dotnet\RuBackend\RuBackend.csproj" -c Release -r win-x64 --self-contained true
~~~

Keep deterministic output at backend-dotnet\RuBackend\bin\Release\net10.0\win-x64\publish\RuBackend.exe. Abort before publishing the GUI if backend publish fails. Preserve existing PRI/XAML steps and output checks.

- [ ] Step 2: Run the publish command and verify the executable.

~~~powershell
dotnet publish backend-dotnet/RuBackend/RuBackend.csproj -c Release -r win-x64 --self-contained true
Test-Path 'backend-dotnet/RuBackend/bin/Release/net10.0/win-x64/publish/RuBackend.exe'
~~~

Expected: publish exit code 0 and the executable exists. This is sequential; do not run it in parallel with GPU/Python tests.

- [ ] Step 3: Update current documentation.

Document these commands in backend-dotnet/README.md.

Parallel compatibility mode:

~~~powershell
$env:RU_DESKTOP_TOKEN = "local-test-secret"
$env:RuBackend__UpstreamUrl = "http://127.0.0.1:8000"
dotnet run --project backend-dotnet/RuBackend/RuBackend.csproj -- --urls http://127.0.0.1:8010
~~~

WinUI opt-in cutover mode before default activation:

~~~powershell
$env:RU_BACKEND_HOST_MODE = "aspnetcore"
& D:\LLM\build.bat
& D:\LLM\start_dev.bat
~~~

Explain that FastAPI remains the worker on 8001, the public contract remains 8000, and clearing RU_BACKEND_HOST_MODE rolls back to the existing FastAPI owner. State that a passing unit suite alone does not activate the default.

- [ ] Step 4: Create the validation record with measured facts only.

Add sections for route matrix, .NET tests, GUI tests, Python tests, live process ownership, WebSocket order, benchmark values, OmniVoice confirmed-audio evidence, default-mode decision, and rollback command. Start with Pending/Not run rather than inventing values; update only after the corresponding command produces evidence.

- [ ] Step 5: Commit build and documentation changes.

~~~powershell
git add -- build.bat backend-dotnet/README.md README.md docs/aspnetcore-cutover-validation.md
git commit -m "docs: document ASP.NET Core backend cutover modes"
~~~

## Task 7: Run live gates, benchmark, and decide default ownership

**Files:** docs/aspnetcore-cutover-validation.md, plus only files exposed by actual failures.

- [ ] Step 1: Run the full sequential static/unit gates.

Run each command only after the previous one exits:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_voice_stream.py tests/test_vts_api.py -q
& D:\LLM\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
git diff --check
~~~

Record exact pass counts and dependency/import warnings separately from code failures.

- [ ] Step 2: Run the isolated compatibility harness.

~~~powershell
& D:\LLM\.venv\Scripts\python.exe scripts/aspnetcore_compatibility_harness.py --project-root D:\LLM --worker-port 8001 --edge-port 8010 --output artifacts/aspnetcore/live-compatibility.json
~~~

Require edge readiness through /settings, all safe route probes pass or have a recorded expected upstream status, chat malformed-frame ordering is preserved, voice invalid-control ordering is preserved, both children terminate, and no normal user/project data path changes.

- [ ] Step 3: Run the direct-versus-edge benchmark.

Start direct FastAPI and parallel RuBackend on separate loopback ports as required by the harness, then run:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe scripts/benchmark_aspnetcore_compat.py --direct-url http://127.0.0.1:8001 --edge-url http://127.0.0.1:8010 --token "$env:RU_DESKTOP_TOKEN" --samples 20 --output artifacts/aspnetcore/benchmark.json
~~~

Record p50, p95, failure count, and proxy overhead. Do not state that ASP.NET Core is faster unless measured data supports it; treat Python worker/model/TTS time as a separate component.

- [ ] Step 4: Run the real WinUI cutover smoke gate.

With RU_BACKEND_HOST_MODE=aspnetcore, run the published WinUI app and verify:

1. BackendProcessManager starts FastAPI on 8001 and the published edge on 8000;
2. both processes are assigned to the same Job Object;
3. /settings, sessions, memory read/delete, chat WebSocket, and voice WebSocket work through public 8000;
4. a real voice request produces an audio control frame immediately followed by its non-empty binary frame and the WinUI player confirms playback; and
5. closing WinUI terminates both children and leaves no process listening on 8000 or 8001.

Real audio and shutdown checks cannot be replaced by unit tests. If OmniVoice audio is unavailable, leave the default host mode as FastAPI and record the exact missing evidence.

- [ ] Step 5: Change the default only after every acceptance gate passes.

If and only if Steps 1–4 all pass, change the manager's empty/default mode from FastApi to AspNetCore, update README/validation status, and rerun the full sequential gates. Otherwise keep the safe FastAPI default; the explicit ASP.NET Core mode and rollback command remain available.

- [ ] Step 6: Final document and worktree review.

Inspect all migration README/spec/plan/validation documents for stale claims, remove no historical document without explicit approval, verify no secrets or benchmark response bodies entered tracked files, confirm unrelated dirty files remain untouched, and run:

~~~powershell
git status --short --branch
git diff --check
git log -8 --oneline
~~~

Expected: only scoped migration commits and pre-existing user changes remain; no whitespace errors; validation document agrees with actual command output.
