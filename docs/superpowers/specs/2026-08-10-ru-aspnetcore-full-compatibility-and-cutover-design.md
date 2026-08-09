# Ru ASP.NET Core Full Compatibility and Cutover Design

## Status

Proposed follow-up to the accepted parallel compatibility slice. The first
slice remains intact: `RuBackend` runs beside FastAPI on `8010`, while WinUI
still owns FastAPI on `8000`. This design covers the complete migration path;
implementation will proceed in separately verified slices.

## Goal

Move the public loopback backend ownership from FastAPI to ASP.NET Core without
changing the WinUI wire contract, while keeping Python as the worker runtime
for model generation, memory, TTS, STT, VTube Studio, and screen-reaction
logic.

The migration must prove compatibility and measure latency before changing
process ownership. It must also leave an explicit rollback mode instead of
silently starting two public backends on the same port.

## Current facts

- `backend-dotnet/RuBackend` currently owns authentication, generic HTTP
  forwarding, readiness forwarding, and frame-preserving `/chat` and
  `/voice/stream` relay.
- `gui-winui3/Services/BackendProcessManager.cs` currently starts
  `python -m mini_gpt.server` on `127.0.0.1:8000` and assigns that process to a
  Windows Job Object.
- `ApiService`, `ChatWebSocketClient`, and `VoiceStreamClient` already target
  port `8000`; they should not learn a new port during cutover.
- FastAPI exposes 38 HTTP route/method pairs plus the two WebSocket routes
  under the status, settings, sessions, observation, voice, and VTS routers.
- WinUI calls `GET /memory`; the pre-existing FastAPI drift is now repaired by a
  Python-owned read-only DTO route. The ASP.NET proxy still only forwards it and
  does not own the memory store.

## Target topology

### Parallel mode

```text
manual/test client -> RuBackend :8010 -> FastAPI :8000
                                             ├─ model runtime
                                             ├─ memory store
                                             ├─ OmniVoice
                                             ├─ STT
                                             └─ VTS/screen workers
```

### Cutover mode

```text
WinUI : public contract on 8000
        └─ RuBackend edge :8000
             └─ FastAPI worker :8001
                  ├─ model runtime
                  ├─ memory store
                  ├─ OmniVoice
                  ├─ STT
                  └─ VTS/screen workers
```

The edge and worker use the same per-run token. The worker remains loopback
only and is never exposed as the public WinUI endpoint. Both child processes
are assigned to the same Job Object, so shutdown and crash cleanup retain one
ownership model.

## Design decisions

### 1. Generic HTTP compatibility seam

ASP.NET Core will forward every non-WebSocket HTTP method and path through one
catch-all compatibility route. The route must preserve:

- HTTP method, escaped path, query string, and request body;
- `Authorization` and `Content-Type` request headers;
- upstream status code, JSON/body bytes, content type, and safe cache/location
  headers;
- cancellation and client disconnects;
- the existing `OPTIONS` 204 response, `401` JSON shape, `413` body limit, and
  deterministic `503` when the FastAPI worker is unavailable.

The generic seam now replaces the former explicit `/status` and `/settings`
maps; contract tests prove route precedence still keeps WebSocket requests on
the relay. The proxy will not reimplement Python business logic.

### 2. Contract inventory and the `/memory` drift

The route matrix records the FastAPI routers and WinUI callers. Every
method/path pair is classified as one of:

1. forwarded unchanged;
2. WebSocket-relayed unchanged;
3. intentionally unused by WinUI but still forwarded for compatibility; or
4. a pre-existing drift that must be resolved before cutover.

For `GET /memory`, preserve memory ownership in Python through the read-only
FastAPI route. It returns the WinUI DTO shape (`id`, `text`, `category`,
`created_at`) from active structured-memory records; `DELETE /memory` remains
the existing clear operation. This avoids duplicating the SQLite/vector store
in .NET and makes the existing Memory view functional.

### 3. WebSocket policy

The existing relay remains the only WebSocket transport module. It will keep
the `/chat` event types and the `/voice/stream` control/binary ordering. No
second chat state machine, voice queue, audio playback state, or VTS metadata
path will be introduced in ASP.NET Core.

The live gate must prove:

- chat session/chunk/reset/done/error ordering;
- voice start/cancel/playback control forwarding;
- each audio control frame is followed by its matching binary frame;
- request IDs and sequence numbers survive the relay;
- cancellation and close propagate in both directions;
- confirmed OmniVoice audio is received by the real WinUI voice path.

### 4. Process-manager cutover

`BackendProcessManager` will gain an explicit host mode, defaulting to the
current FastAPI mode until cutover acceptance is complete. In ASP.NET Core mode
it will:

1. create one Job Object with kill-on-close;
2. start FastAPI on configurable worker port `8001` with `RU_DESKTOP_TOKEN`;
3. start the published `RuBackend` executable on public port `8000` with
   `RuBackend__UpstreamUrl=http://127.0.0.1:8001` and the same token;
4. assign both processes to the same Job Object;
5. wait for the edge `/settings` readiness response, which proves the worker
   is reachable through the public contract;
6. fail closed and terminate both children if either startup/ownership step
   fails.

The public `Port` remains `8000`; WinUI REST and WebSocket clients do not
change. The worker port is configuration, not a new public API. Development
uses a published `RuBackend.exe`; the manager must report a clear error when
the configured executable is missing rather than silently falling back to an
unowned `dotnet` process.

Rollback is selecting the existing FastAPI host mode and restarting the app;
there is no automatic fallback while another process might still own port
`8000`.

### 5. Live benchmark

The benchmark will compare direct FastAPI worker calls with the same calls
through the ASP.NET edge on the same machine and token. It will record warm
request count, p50, p95, failures, and proxy overhead for:

- read-only HTTP readiness/settings calls;
- WebSocket handshake and first control event;
- chat first chunk when a deterministic hosted/local test fixture is available;
- voice first confirmed audio frame when the real OmniVoice service is
  available.

The benchmark must not claim that ASP.NET Core is faster merely because Kestrel
is fast. The cutover gate is no material regression on the public contract;
model generation and TTS timings are reported separately because the Python
worker still dominates those paths.

## Testing and acceptance gates

1. .NET contract tests cover the generic HTTP route seam, method/path/query/body
   preservation, headers, auth, 413, 503, WebSocket relay, and frame order.
2. Python tests cover the new `GET /memory` response and retain all existing
   settings/session/voice/VTS/observation behavior.
3. A live compatibility harness starts a tokenized FastAPI worker and the
   ASP.NET edge on separate loopback ports, probes the route matrix, and
   exercises real chat and voice sockets without mutating the user's normal
   runtime data.
4. A process-manager harness verifies both children receive the Job Object,
   readiness waits for the worker, and disposal terminates both processes.
5. The latency benchmark records direct-versus-edge results and keeps its raw
   output out of chat/history/memory/logs containing secrets or user text.
6. The full sequential gates remain green:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_voice_stream.py tests/test_vts_api.py -q
```

7. Only after all gates pass may the default host mode change from FastAPI to
   ASP.NET Core. The existing README and parallel-run documentation must then
   be updated together with the cutover decision.

## Non-goals

- Rewriting Python model, memory, OmniVoice, STT, VTS, or screen-reaction code
  in C#.
- Changing WinUI REST/WebSocket payloads or introducing a second audio queue.
- Exposing the FastAPI worker beyond loopback.
- Removing historical design documents merely because they describe FastAPI.
- Claiming latency improvement without measured direct-versus-edge results.

## Rollback and observability

All startup failures, readiness failures, child exit codes, and Job Object
assignment failures must be logged without bearer tokens, chat text, audio
bytes, or private VTS metadata. A rollback must leave the current FastAPI mode
available and must not delete user data or persisted memory.
