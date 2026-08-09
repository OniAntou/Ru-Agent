# Ru ASP.NET Core Backend Migration Design

**Date:** 2026-08-10  
**Status:** Proposed for review  
**Scope:** First migration slice only

## Goal

Move the public Ru backend interface from FastAPI to an ASP.NET Core backend while preserving the existing WinUI 3 interface and keeping Python for CUDA-heavy model, TTS, STT, and VTube Studio implementations during the first migration stages.

The first implementation slice creates an ASP.NET Core backend shell and compatibility contract tests. It runs beside the existing FastAPI backend on port `8010`; FastAPI remains the unchanged upstream on port `8000` until the new backend covers and passes the required interface.

## Current context

The current Python entrypoint is `src/mini_gpt/server.py`. It builds the FastAPI application, installs authentication and request-size middleware, mounts the status, observation, settings, sessions, voice, VTS, and chat routers, and starts Uvicorn on loopback.

The WinUI host currently:

- creates one per-run bearer secret;
- starts `python -m mini_gpt.server`;
- passes the secret through `RU_DESKTOP_TOKEN`;
- waits for `GET /settings` to succeed;
- owns the child process through a Windows Job Object;
- uses `ApiService` for REST and `ChatWebSocketClient`/`VoiceStreamClient` for WebSocket traffic.

The existing interface is therefore a real seam. The first migration must preserve it rather than redesign it.

## Selected architecture

```text
WinUI 3
   │ existing REST + WebSocket interface
   ▼
ASP.NET Core RuBackend :8010
   │ compatibility adapter during migration
   ▼
FastAPI Python backend :8000
   ├── llama-server.exe
   ├── OmniVoice Python process :5052
   ├── Python STT runtime
   └── Python VTS and memory implementation
```

After the compatibility slice is accepted, ASP.NET Core will absorb the stateless modules first, then chat and voice orchestration. Python will remain behind explicit worker interfaces until a native adapter demonstrates feature parity.

### Backend modules

1. **Loopback host module** — binds the configured loopback port, exposes readiness, and owns graceful shutdown.
2. **Authentication module** — validates the same bearer secret for HTTP and the same `token` query value for WebSocket connections; missing or invalid credentials fail closed.
3. **REST compatibility module** — preserves method, path, status code, JSON field names, request-size rejection, and upstream error translation.
4. **Chat stream module** — preserves the `/chat` WebSocket message ordering and event types: `session`, `chunk`, `reset`, `done`, and `error`.
5. **Voice stream module** — preserves `/voice/stream` control frames, request IDs, sequence numbers, cancellation, playback acknowledgement, and the rule that an audio control frame is followed by its binary audio frame.
6. **Worker adapter module** — communicates with FastAPI or later Python workers over local HTTP/WebSocket; it does not embed CPython in the ASP.NET Core process.

The public interface stays small and deep: WinUI learns no new protocol while the implementation moves behind the seam.

## First implementation slice

### In scope

- Create a `backend-dotnet/RuBackend` ASP.NET Core project targeting `net10.0`.
- Create a `backend-dotnet/RuBackend.Tests` test project.
- Add authenticated readiness/status behavior.
- Add a configurable upstream adapter for compatibility forwarding.
- Add contract tests for HTTP auth, readiness, status propagation, and representative JSON forwarding.
- Add contract fixtures for the existing chat and voice WebSocket event envelopes without changing the Python server.
- Keep the new backend on `127.0.0.1:8010` while the existing FastAPI backend remains on `127.0.0.1:8000`.
- Add a short migration note to the design/plan documentation only; do not change WinUI startup or the production port yet.

### Out of scope

- Rewriting `ModelRunner`, PyTorch/Transformers, OmniVoice, STT, PyVTS, or the Python memory worker.
- Changing the WinUI REST or WebSocket clients.
- Moving the public backend to port `8000`.
- Migrating persisted settings, sessions, history, or memory data.
- Deleting historical design documents or changing unrelated dirty files.
- Adding a reverse-proxy package solely to hide an undefined contract; forwarding behavior must be tested at the interface.

## Interface contract

### HTTP

- Bind loopback only by default.
- HTTP requests require `Authorization: Bearer <per-run-secret>`.
- `OPTIONS` remains available for the existing desktop CORS behavior.
- The readiness probe remains `GET /settings` during the transition so the existing host semantics can be compared.
- A missing or invalid token returns `401` with the existing JSON shape.
- Oversized request bodies preserve the current `413` behavior.
- Upstream unavailability is reported as a deterministic `503` without exposing internal paths or secrets.

### Chat WebSocket

The endpoint remains `ws://127.0.0.1:<port>/chat?token=<secret>`.

The client sends JSON containing `prompt` and `session_id`. The backend preserves the existing event envelopes and ordering:

```json
{"type":"session","session_id":"..."}
{"type":"chunk","content":"..."}
{"type":"reset"}
{"type":"done","content":"...","session_id":"..."}
{"type":"error","content":"..."}
```

The adapter must forward cancellation and connection closure instead of buffering a complete reply before emitting chunks.

### Voice WebSocket

The endpoint remains `ws://127.0.0.1:<port>/voice/stream?token=<secret>`.

The compatibility test surface must cover `start`, `cancel`, and `playback` requests; `started`, `audio`, `ended`, `cancelled`, and `failed` events; request ID and sequence preservation; and JSON-control-then-binary-audio framing.

The adapter must allow only the current voice stream policy already enforced by the Python backend. It must not introduce a second queue or a second ownership model.

## Process ownership

The first slice does not change process ownership. `RuApp` continues to own the existing FastAPI child. The new `RuBackend` runs manually or from a test harness on port `8010`.

When cutover begins, ownership will change in one explicit step:

```text
RuApp.exe
   └── RuBackend.exe
       ├── llama-server.exe
       ├── OmniVoice Python process
       └── Python STT/VTS workers
```

Only one module may claim ownership of each child process. The cutover plan must verify Job Object assignment, readiness, log capture, cancellation, and shutdown before changing the WinUI command line.

## Error and lifecycle rules

- Authentication fails closed.
- A stopped upstream cannot leave the ASP.NET Core host reporting ready.
- Client cancellation propagates to the upstream connection and releases the request slot.
- WebSocket disconnects release stream state and do not consume a later request's `request_id`.
- Shutdown stops accepting new work, cancels active forwarding, closes WebSockets, then disposes the upstream client.
- Logs contain timing, route, event type, and error code, but not chat text, audio payloads, tokens, or private VTS metadata.

## Testing and acceptance

The implementation follows red-green-refactor for each contract slice:

1. Add one failing test for the desired interface behavior.
2. Run it and verify the failure is caused by missing ASP.NET Core behavior.
3. Add the smallest implementation.
4. Run the focused test and then the complete new backend test project.
5. Run the existing Python and WinUI tests without modifying their expected contract.

The first slice is accepted only when:

- `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj` passes;
- the host rejects missing and invalid credentials;
- readiness reflects the configured upstream state;
- representative REST JSON is forwarded without field-name changes;
- chat event ordering is preserved in a real local WebSocket test;
- voice JSON/binary framing is preserved in a real local WebSocket test;
- the existing Python suite and WinUI tests remain green;
- `git diff --check` passes;
- no existing FastAPI, WinUI, model, voice, or data files are removed.

Live OmniVoice playback is not part of this first slice. It becomes an acceptance gate only when the new backend owns the voice path.

## Rollback

Rollback is deleting or disabling the new `backend-dotnet` project and discarding the parallel test harness. Because FastAPI remains on port `8000` and WinUI is unchanged, the running application has no dependency on the new implementation during this slice.

## Documentation decision

The existing README and current design/plan documents correctly describe the current FastAPI runtime. They should not be rewritten to claim an ASP.NET Core cutover until the new backend passes the contract and the WinUI process manager has actually switched ports. This spec is the migration decision record for the first slice; outdated historical plans should be reviewed separately rather than deleted as part of this change.
