# ASP.NET Core decommission validation

This record contains the final measured evidence for removing the public
FastAPI backend. It supersedes the earlier staged-cutover evidence and its
runtime ownership flags.

Note: the gate counts recorded below predate the architecture-v2 refactor;
later measured gates are recorded in
[docs/validation/](validation/). This record remains authoritative for the
FastAPI decommission itself.

## Final architecture and rollback

- ASP.NET Core is the only public HTTP and WebSocket host.
- The desktop launch plan starts one private Python runtime worker and then
  ASP.NET Core. The worker has no public API and is authenticated with a
  per-launch bearer token.
- Python remains only where the active model, STT, OmniVoice, VTS, or provider
  libraries require it. Session, memory, activity, settings, orchestration,
  public routing, authentication, and persistence ownership are in ASP.NET
  Core.
- There is no FastAPI host mode, forwarding fallback, upstream URL, or
  per-route ownership switch.
- The pre-deletion rollback point is Git commit `e023fd3`. Restoring FastAPI is
  a source rollback, not a runtime environment-variable change.

## Final static and build gates (2026-08-13)

| Gate | Result |
| --- | --- |
| Public route contract | 38 HTTP routes, 2 WebSocket routes |
| Python tests | 401 passed, 1 third-party deprecation warning |
| ASP.NET Core backend tests | 219 passed |
| WinUI tests | 94 passed |
| FastAPI decommission guard | Passed |
| Project `.venv` packages | `fastapi`, `uvicorn`, and `pydantic` not installed |
| Active-source FastAPI/import/forwarding scan | No matches |
| NuGet vulnerability audit | No vulnerable packages |
| Self-contained Release build | Passed |
| Published binaries | `RuBackend.exe` and `RuApp.exe` present |

The Python warning comes from `speech_recognition` importing the separately
packaged Python 3.13 `standard-aifc` compatibility module; it is unrelated to
the backend migration.

## Deterministic chat and state gate

The final ASP.NET Core host and an isolated deterministic private worker ran on
temporary ports and a temporary data root.

| Check | Result |
| --- | --- |
| Edge readiness | HTTP 200 |
| Chat event order | `session`, `reset`, `chunk`, `done` |
| Persisted messages | 1 user, 1 assistant |
| Activity events | 2 |
| Active memory records | 1 |
| Private metadata in events or persisted evidence | None |
| Cleanup | Both children terminated; both ports closed |

Count/type/privacy-only evidence is stored in the ignored
`artifacts/aspnetcore/final-deterministic-chat-gate.json` file. It contains no
prompt, reply, session identifier, bearer token, worker token, audio, or
private VTS metadata.

## Real runtime, audio, and latency gate

The final ASP.NET Core host and the real private runtime worker ran on
temporary ports. The gate did not use FastAPI.

| Check | Result |
| --- | --- |
| Edge readiness | HTTP 200 |
| Safe HTTP probes | 14 passed, 0 failed; 24 mutating routes skipped |
| Chat protocol rejection | Malformed frame produced ordered `error` |
| Voice protocol rejection | Invalid control produced ordered `failed` |
| OmniVoice transport | Non-empty binary audio frame received |
| Cleanup | Both children terminated; both ports closed; 0 listeners remained |

Latency used 20 local samples per path with zero failures:

| Path | p50 | p95 |
| --- | ---: | ---: |
| `GET /settings` | 1.944 ms | 2.147 ms |
| `/chat` WebSocket handshake | 1.522 ms | 2.064 ms |
| `/voice/stream` WebSocket handshake | 1.424 ms | 1.815 ms |

These numbers measure local ASP.NET Core routing and handshake latency, not
model inference, transcription, speech synthesis quality, or end-to-end reply
time. The previous extra public FastAPI forwarding hop no longer exists, but
provider/model/hardware work still crosses the authenticated private worker
boundary where Python libraries are required.

Numeric evidence is stored in the ignored
`artifacts/aspnetcore/final-runtime-gate.json` file. The artifact stores no
request body, chat text, token, audio bytes, or private VTS metadata.

After uninstalling the former `fastapi`, `uvicorn`, and `pydantic` packages
from the project virtual environment, the real runtime gate was repeated: edge
readiness was HTTP 200, all 14 safe probes passed, both WebSocket protocol
checks were ordered, non-empty OmniVoice audio was received, and both child
processes and ports were cleaned up. The count-only evidence is stored in
`artifacts/aspnetcore/post-uninstall-runtime-gate.json`.

## Remaining physical acceptance

- The gate proved that OmniVoice emitted a non-empty audio frame; it did not
  judge the sound through the user's speakers.
- Real microphone transcription was not recorded during this gate.
- A live VTube Studio action was not triggered or visually accepted. Safe VTS
  status/configuration and protocol behavior remain covered by automated
  tests.
- Local screen capture remains disabled by product design; the structured
  observation boundary remains available without restoring FastAPI.

These are hardware and user-perception checks, not blockers to the completed
FastAPI backend removal.
