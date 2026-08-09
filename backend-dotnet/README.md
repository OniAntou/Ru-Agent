# RuBackend ASP.NET Core parallel slice

`RuBackend` is the first compatibility slice for moving the loopback host from
FastAPI to ASP.NET Core. It currently listens on `127.0.0.1:8010`, validates the
same desktop bearer token, and forwards the REST/WebSocket contracts to the
existing FastAPI process on `127.0.0.1:8000`.

This is a parallel migration slice. It does not replace FastAPI, does not move
the production backend to port `8000`, and does not change WinUI startup. The
existing `start_dev.bat` and `gui-winui3/Services/BackendProcessManager.cs`
still start and own FastAPI on port `8000`.

## Local run

Start the existing FastAPI backend on port `8000`, then run the ASP.NET Core
compatibility host from the repository root:

```powershell
$env:RU_DESKTOP_TOKEN = "local-test-secret"
$env:RuBackend__UpstreamUrl = "http://127.0.0.1:8000"
dotnet run --project backend-dotnet/RuBackend/RuBackend.csproj -- --urls http://127.0.0.1:8010
```

Requests to the ASP.NET Core host need:

```text
Authorization: Bearer local-test-secret
```

WebSocket clients use the existing query contract, for example:

```text
ws://127.0.0.1:8010/chat?token=local-test-secret
ws://127.0.0.1:8010/voice/stream?token=local-test-secret
```

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| `RU_DESKTOP_TOKEN` | empty | Required desktop token; an empty value fails closed. |
| `RuBackend__BindUrl` | `http://127.0.0.1:8010` | ASP.NET Core bind URL. |
| `RuBackend__UpstreamUrl` | `http://127.0.0.1:8000` | FastAPI upstream base URL. |
| `RuBackend__MaxRequestBodyBytes` | `8388608` | Maximum POST/PUT/PATCH body size. |

The `/status` and `/settings` HTTP endpoints are forwarded so a stopped
upstream is reported as unavailable. `/chat` and `/voice/stream` relay text and
binary WebSocket frames without parsing or reordering them. Model execution,
memory, TTS, STT, VTS, and audio playback remain in Python for this slice.

## Verification

Run the checks sequentially from `D:\LLM`:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_voice_stream.py tests/test_vts_api.py -q
```

These checks validate compatibility; they do not authorize the port `8000`
cutover. A later cutover must verify WinUI process ownership, readiness,
cancellation, shutdown, and live OmniVoice playback separately.
