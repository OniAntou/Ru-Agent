# ASP.NET Core cutover validation

This record contains measured evidence for the ASP.NET Core edge migration.
It is intentionally separate from the design/spec documents so a unit-test
pass cannot be mistaken for a live WinUI cutover.

## Current decision

- Default WinUI host mode: FastApi.
- Opt-in mode: RU_BACKEND_HOST_MODE=aspnetcore.
- Public contract: 127.0.0.1:8000.
- Private worker in opt-in mode: 127.0.0.1:8001.
- Default change: pending live process, benchmark, shutdown, and confirmed
  OmniVoice-audio evidence.

## Completed static/unit evidence

| Gate | Result |
| --- | --- |
| ASP.NET route checker | 38 HTTP routes, 2 WebSocket routes |
| ASP.NET backend tests | 15 passed |
| GUI tests | 99 passed |
| Selected Python API tests | 152 passed, 2 dependency deprecation warnings |
| Compatibility tool tests | 4 passed |
| Backend launch/process-manager tests | 12 passed |

## Pending live evidence

- Isolated FastAPI worker plus ASP.NET edge readiness through public /settings.
- Safe route probe results and WebSocket control/error ordering.
- Direct FastAPI versus edge p50/p95/failure counts and measured overhead.
- Both cutover children assigned to one Job Object and terminated on WinUI close.
- Real WinUI voice playback with an audio control frame immediately followed
  by its non-empty binary frame.

Run the live checks only with temporary ports/data and keep their numeric JSON
artifacts under the ignored artifacts/aspnetcore/ directory. Never record
tokens, request bodies, chat text, audio bytes, or private VTS metadata here.

Rollback is clearing RU_BACKEND_HOST_MODE and restarting the app; it does not
delete persisted memory or user data.

