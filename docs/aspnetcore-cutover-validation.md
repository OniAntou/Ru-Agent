# ASP.NET Core cutover validation

This record contains measured evidence for the ASP.NET Core edge migration.
It is intentionally separate from the design/spec documents so a unit-test
pass cannot be mistaken for a live WinUI cutover.

## Current decision

- Default WinUI host mode: AspNetCore.
- Rollback mode: RU_BACKEND_HOST_MODE=fastapi.
- Public contract: 127.0.0.1:8000.
- Private worker in default mode: 127.0.0.1:8001.
- Default change: activated after the live process, benchmark, shutdown, and
  confirmed OmniVoice-audio gates below passed.

## Completed static/unit evidence

| Gate | Result |
| --- | --- |
| ASP.NET route checker | 38 HTTP routes, 2 WebSocket routes |
| ASP.NET backend tests | 15 passed |
| GUI tests | 99 passed |
| Selected Python API tests | 152 passed, 2 dependency deprecation warnings |
| Compatibility tool tests | 4 passed |
| Backend launch/process-manager tests | 12 passed |

## Live evidence

- Compatibility harness: edge readiness 200; 14 safe HTTP probes passed, 24
  mutating routes skipped without changing state, and 0 probe failures.
- WebSockets: chat malformed-frame response was error; voice invalid-control
  response was failed; both connections and ordering checks passed.
- OmniVoice: voice_audio_confirmed=true; an audio control frame was followed
  by a non-empty binary frame.
- Benchmark: 20 samples, 0 failures. Direct FastAPI /settings p50/p95 was
  1.462/1.697 ms; edge was 1.898/2.409 ms; measured edge overhead was
  +0.436/+0.712 ms. WebSocket handshake p95 was direct 2.212/1.782 ms
  (/chat /voice/stream) and edge 3.126/2.335 ms.
- WinUI smoke: published RuApp.exe observed listeners on both 8000 and 8001;
  after app shutdown, remaining listeners were 0.

Run future live checks only with temporary ports/data and keep their numeric
JSON artifacts under the ignored artifacts/aspnetcore/ directory. Never record
tokens, request bodies, chat text, audio bytes, or private VTS metadata here.

Rollback is setting RU_BACKEND_HOST_MODE=fastapi and restarting the app; it does
not delete persisted memory or user data.

