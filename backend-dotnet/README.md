# RuBackend ASP.NET Core compatibility edge

RuBackend is the ASP.NET Core compatibility edge for the existing Python
runtime. It authenticates the desktop bearer token, forwards every non-
WebSocket HTTP method/path, and relays /chat and /voice/stream frames
without reimplementing model, TTS, the STT recognition provider, the VTS
hardware runtime, or screen logic. Memory, STT, and VTS remain Python-owned by
default; their optional ASP.NET ownership modes are documented below.

The implementation supports two explicit topologies:

~~~text
parallel:  client -> RuBackend:8010 -> FastAPI:8000
cutover:   WinUI -> RuBackend:8000 -> FastAPI worker:8001
~~~

The WinUI default is now the ASP.NET Core edge after the live process, latency,
shutdown, and confirmed OmniVoice-audio gates passed. Explicitly selecting
RU_BACKEND_HOST_MODE=fastapi rolls back to the prior owner. No public client
needs to know about worker port 8001.

## Parallel local run

Start FastAPI on 8000, then run the compatibility edge on 8010:

~~~powershell
$env:RU_DESKTOP_TOKEN = "local-test-secret"
$env:RuBackend__BindUrl = "http://127.0.0.1:8010"
$env:RuBackend__UpstreamUrl = "http://127.0.0.1:8000"
dotnet run --project backend-dotnet/RuBackend/RuBackend.csproj -- --urls http://127.0.0.1:8010
~~~

HTTP requests use Authorization: Bearer local-test-secret. WebSocket clients
retain the existing query-token contract:

~~~text
ws://127.0.0.1:8010/chat?token=local-test-secret
ws://127.0.0.1:8010/voice/stream?token=local-test-secret
~~~

## WinUI startup modes

build.bat publishes both RuBackend.exe and RuApp.exe. The default RuApp startup
uses the dual-child topology:

~~~powershell
& D:\LLM\build.bat
& D:\LLM\start_dev.bat
~~~

The process manager starts FastAPI on 127.0.0.1:8001, starts the published
edge on 127.0.0.1:8000, gives both children the same per-run token, assigns
both to one kill-on-close Job Object, and probes /settings through public
8000. Set RU_BACKEND_HOST_MODE=fastapi and restart RuApp to roll back to the
single FastAPI owner on 8000; there is no automatic same-port fallback.

The published edge is expected at:

~~~text
backend-dotnet\RuBackend\bin\Release\net10.0\win-x64\publish\RuBackend.exe
~~~

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| RU_DESKTOP_TOKEN | empty | Required token; empty fails closed. |
| RuBackend__BindUrl | http://127.0.0.1:8010 | ASP.NET Core bind URL. |
| RuBackend__UpstreamUrl | http://127.0.0.1:8000 | FastAPI worker base URL. |
| RuBackend__WorkerUrl | ws://127.0.0.1:5053/worker/v1 | Versioned private Python runtime worker URL. |
| RuBackend__MaxRequestBodyBytes | 8388608 | Maximum POST/PUT/PATCH body size. |
| RuBackend__Ownership__Chat | python | `/chat` owner: `python`, `shadow`, or `aspnet`. |
| RuBackend__Ownership__Memory | python | Memory route owner: `python`, `shadow`, or `aspnet`. |
| RuBackend__Ownership__Stt | python | `/voice/transcribe` owner: `python` or `aspnet`. |
| RuBackend__Ownership__Vts | python | All eleven `/vts/*` method/path owners: `python` or `aspnet`. |
| RU_BACKEND_HOST_MODE | aspnetcore | WinUI owner mode; fastapi is the explicit rollback. |
| RU_BACKEND_WORKER_PORT | 8001 | Private FastAPI port in ASP.NET Core mode. |
| RU_WORKER_TOKEN | empty | Separate private-worker handshake token; empty fails closed when runtime loading is requested. |
| RU_RUBACKEND_EXE | published path above | Optional explicit edge executable path. |

The complete route inventory is in docs/aspnetcore-route-matrix.md. With the
default `RuBackend__Ownership__Memory=python`, both memory operations remain
on FastAPI and the edge does not duplicate the store. `shadow` makes the ASP.NET
GET path read the same SQLite database read-only and compare bounded metadata
with the Python response; DELETE remains Python-owned. `aspnet` makes ASP.NET
the owner of GET/DELETE and maps the existing WinUI DTO/error contract. The
shadow repository skips legacy-import writes and releases its read connection
after each read so Python can still clear the database on Windows. Roll back
the memory seam with `RuBackend__Ownership__Memory=python`; this does not remove
FastAPI from the system.

Runtime-changing `POST /settings` requests use the private protocol v1
`runtime.load` operation. ASP.NET sends only backend, model, variant,
checkpoint, and reasoning fields. Hosted API keys remain in Windows Credential
Manager and are read by the Python worker locally; they are never copied into a
worker envelope. Until the process manager owns this private worker and the
restart/rollback gate passes, settings ownership remains `python` by default.

With `RuBackend__Ownership__Chat=aspnet`, ASP.NET owns public frame validation,
session/context ordering, memory retrieval and persistence, metadata filtering,
activity history, and event streaming. Model generation and private VTS calls
continue through the token-protected Python worker. `python` and `shadow` keep
the FastAPI relay public; shadow comparison is allowed only in the isolated
synthetic harness so a real prompt is never generated twice.

With `RuBackend__Ownership__Stt=aspnet`, ASP.NET validates the bounded STT JSON,
sample-rate, and base64 contract before dispatching recognition through
`IRuntimeWorkerClient`. With `RuBackend__Ownership__Vts=aspnet`, ASP.NET
explicitly owns the eleven documented `/vts/*` routes and relays them through
the same seam. The current `IRuntimeWorkerClient` implementation calls the
FastAPI worker, which remains the single owner of Google recognition and VTube
Studio connection/motion state. Restart with either ownership setting at
`python` to roll that route group back. These seams prepare a later loopback
worker migration; they do not remove Python or FastAPI yet.

## Verification

Run heavy checks sequentially from D:\LLM:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_voice_stream.py tests/test_vts_api.py -q
& D:\LLM\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~Memory
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_memory.py tests/test_memory_closed.py tests/test_memory_extraction.py tests/test_memory_learning.py tests/test_server_api.py -q
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .superpowers\sdd\2026-08-10-ru-aspnetcore-memory-ownership-migration\run-task7-memory-gate.ps1
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter "FullyQualifiedName~SttEndpointTests|FullyQualifiedName~VtsEndpointTests|FullyQualifiedName~FastApiRuntimeWorkerClientTests"
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_speech_runtime.py tests/test_vts_api.py tests/test_aspnetcore_route_matrix.py tests/test_aspnetcore_compatibility_tools.py -q
~~~

The isolated live probe and direct-versus-edge benchmark are described in
docs/superpowers/plans/2026-08-10-ru-aspnetcore-full-compatibility-and-cutover.md.
Their output must not contain bearer tokens, request bodies, chat text, audio,
or private VTS metadata. A passing unit suite alone does not authorize changing
the default host mode. Task 7 memory evidence is stored at
artifacts/aspnetcore/memory-ownership-gate.json and summarized in
docs/aspnetcore-cutover-validation.md. The STT/VTS isolated topology result is
stored at artifacts/aspnetcore/stt-vts-ownership-gate.json; it does not claim a
real microphone transcription or a live VTS action acceptance.
