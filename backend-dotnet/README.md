# RuBackend ASP.NET Core compatibility edge

RuBackend is the ASP.NET Core compatibility edge for the existing Python
runtime. It authenticates the desktop bearer token, forwards every non-
WebSocket HTTP method/path, and relays /chat and /voice/stream frames
without reimplementing model, memory, TTS, STT, VTS, or screen logic.

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
| RuBackend__MaxRequestBodyBytes | 8388608 | Maximum POST/PUT/PATCH body size. |
| RU_BACKEND_HOST_MODE | aspnetcore | WinUI owner mode; fastapi is the explicit rollback. |
| RU_BACKEND_WORKER_PORT | 8001 | Private FastAPI port in ASP.NET Core mode. |
| RU_RUBACKEND_EXE | published path above | Optional explicit edge executable path. |

The complete route inventory is in docs/aspnetcore-route-matrix.md. GET
/memory is implemented by Python and returns the existing WinUI DTO; .NET
does not duplicate the memory store.

## Verification

Run heavy checks sequentially from D:\LLM:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_voice_stream.py tests/test_vts_api.py -q
& D:\LLM\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
~~~

The isolated live probe and direct-versus-edge benchmark are described in
docs/superpowers/plans/2026-08-10-ru-aspnetcore-full-compatibility-and-cutover.md.
Their output must not contain bearer tokens, request bodies, chat text, audio,
or private VTS metadata. A passing unit suite alone does not authorize changing
the default host mode.
