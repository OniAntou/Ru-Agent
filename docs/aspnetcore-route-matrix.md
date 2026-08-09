# ASP.NET Core compatibility route matrix

The canonical matrix is [backend-dotnet/compatibility-route-matrix.json](../backend-dotnet/compatibility-route-matrix.json). The checker reads the FastAPI router decorators and the `server.py` router prefixes without importing model or voice runtime code.

Current inventory: 38 HTTP method/path pairs and two WebSocket paths.

## Ownership policy

| Classification | Edge behavior |
| --- | --- |
| `forwarded` | Forward method, escaped path, query, body, authentication, and content type to the Python worker. |
| `unused-but-forwarded` | Preserve the Python endpoint even when the current WinUI build does not call it. |
| `python-parity` | A WinUI caller already expects the route; Python owns the implementation and data. |
| `websocket-relay` | Relay frames only; do not add a second chat, voice, or playback state machine. |

## Parallel and cutover topology

```text
parallel:  client -> RuBackend:8010 -> FastAPI:8000
cutover:   WinUI -> RuBackend:8000 -> FastAPI worker:8001
```

The public WinUI contract remains on port `8000` in both modes. `GET /memory` was a pre-existing WinUI/FastAPI drift; FastAPI now provides the read-only DTO route, while memory storage remains Python-owned.

## Route groups

The JSON matrix is the source of truth for the complete list. The groups below make the ownership boundary easy to review:

- status/settings: `/status`, `/activity-log`, `/models`, `/history`, `/memory`, `/settings`
- sessions: `/sessions`, `/sessions/new`, `/sessions/{session_id}/activate`, `/sessions/{session_id}`
- observation: `/observation/*`
- voice HTTP: `/voice/status`, `/voice/latency`, `/voice/tts`, `/voice/warmup`, `/voice/release`, `/voice/transcribe`
- VTS HTTP: `/vts/*`
- WebSocket relay: `/chat`, `/voice/stream`

Validate after route changes:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
```
