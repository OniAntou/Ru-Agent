# ASP.NET Core compatibility route matrix

The canonical matrix is [backend-dotnet/compatibility-route-matrix.json](../backend-dotnet/compatibility-route-matrix.json). The checker reads the FastAPI router decorators and the `server.py` router prefixes without importing model or voice runtime code.

Current inventory: 38 HTTP method/path pairs and two WebSocket paths.

## Ownership policy

| Classification | Edge behavior |
| --- | --- |
| `forwarded` | Forward method, escaped path, query, body, authentication, and content type to the Python worker. |
| `unused-but-forwarded` | Preserve the Python endpoint even when the current WinUI build does not call it. |
| `python-parity` | A WinUI caller already expects the route; Python remains the default implementation and writer. |
| `feature-selected` | The edge selects Python forwarding, shadow-read, or direct ASP.NET handling from an ownership setting. |
| `websocket-relay` | Relay frames only; do not add a second chat, voice, or playback state machine. |

## Parallel and cutover topology

```text
parallel:  client -> RuBackend:8010 -> FastAPI:8000
cutover:   WinUI -> RuBackend:8000 -> FastAPI worker:8001
```

The public WinUI contract remains on port `8000` in both modes. Memory ownership is
selected independently with `RuBackend:Ownership:Memory`:

- `python` (default): both memory methods remain catch-all forwards to FastAPI.
- `shadow`: `GET /memory` returns the Python response, then reads ASP.NET memory
  and compares count, canonical ID/category/timestamp hashes, and ordering hash.
  `DELETE /memory` remains Python-owned so shadow mode never dual-writes.
- `aspnet`: `GET /memory` returns only the WinUI fields `id`, `text`, `category`,
  and `created_at`; `DELETE /memory` uses the ASP.NET memory service.

Shadow diagnostics contain only counts, hashes, status, byte counts, and exception
types. They never log memory text, evidence, credentials, or request bodies.

STT and VTS ownership are selected independently with
`RuBackend:Ownership:Stt` and `RuBackend:Ownership:Vts`:

- `python` (default): the compatibility catch-all forwards the public route to
  FastAPI exactly as before.
- `aspnet`: ASP.NET explicitly owns `/voice/transcribe` or the eleven `/vts/*`
  method/path pairs. STT performs bounded JSON, sample-rate, and base64
  validation before dispatch. VTS preserves the request/response bytes while
  the Python runtime remains the single owner of authentication, reconnect,
  catalog, action cooldown, motion, and overlay process state.

Both ASP.NET modes call `IRuntimeWorkerClient`; the current implementation is a
FastAPI adapter and can be replaced by the planned versioned loopback worker.
An ownership switch requires restart. No STT audio, transcript, VTS payload, or
private marker is written to diagnostics.

## Route groups

The JSON matrix is the source of truth for the complete list. The groups below make the ownership boundary easy to review:

- status/settings: `/status`, `/activity-log`, `/models`, `/history`, `/memory`, `/settings`
- sessions: `/sessions`, `/sessions/new`, `/sessions/{session_id}/activate`, `/sessions/{session_id}`
- observation: `/observation/*`
- voice HTTP: `/voice/status`, `/voice/latency`, `/voice/tts`, `/voice/warmup`, `/voice/release`, `/voice/transcribe`
- VTS HTTP: `/vts/*`
- WebSocket relay: `/chat`, `/voice/stream`

The JSON matrix marks `/memory`, `/voice/transcribe`, and every `/vts/*` route
as `feature-selected` while retaining FastAPI as the current compatibility
worker. These ownership fields are descriptive and do not change the default
Python owner.

Validate after route changes:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
```
