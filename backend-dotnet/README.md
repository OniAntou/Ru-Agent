# Ru ASP.NET Core backend

`RuBackend` is the only public backend for Ru. It binds to loopback port `8000`
when launched by RuApp and owns all 41 HTTP routes plus 2 WebSocket routes:
`/chat` and `/voice/stream`.

```text
RuApp / local client
        |
        | bearer token or authenticated WebSocket
        v
ASP.NET Core :8000
        |\
        | \-- private RuAgentBroker (leased loopback endpoint)
        |
        | protocol v1, separate per-run token
        v
Python runtime worker :8005
        |
        +-- hosted / external OpenAI-compatible provider adapters
        +-- OmniVoice and Google STT
        +-- VTube Studio and observation runtime
RuApp owns all three children (Public Host, Worker, and Broker) in one
kill-on-close Windows Job Object.
```

There is no public Python HTTP server, forwarding catch-all, WebSocket relay,
host-mode selector, or automatic fallback. Git commit `e023fd3` is the final
pre-decommission rollback checkpoint if repository-level recovery is required.

## Ownership

ASP.NET Core directly owns authentication, request-size boundaries, settings,
credentials, sessions, history, activity, structured memory, chat ordering,
metadata filtering, and every public endpoint contract. Runtime-dependent work
uses the authenticated `ws://127.0.0.1:8005/worker/v1` protocol:

- model generation and runtime transitions;
- TTS, streaming voice, STT, and GPU lifecycle calls;
- VTube Studio connection, motion, catalog, action, and overlay state;
- structured observation reactions and model/provider status.

The worker is private implementation code, not another public framework. WAV
payloads stay binary instead of being expanded into base64. Chat, TTS, history,
memory, activity, and logs keep private voice/VTS metadata out of visible and
persistent text.

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| `RU_DESKTOP_TOKEN` | required per run | Authenticates public REST/WebSocket clients. |
| `RU_WORKER_TOKEN` | required per run | Authenticates ASP.NET to the private worker. |
| `RU_BACKEND_WORKER_PORT` | `8005` | Private worker loopback port selected by RuApp. |
| `RuBackend__BindUrl` | `http://127.0.0.1:8010` standalone | Public ASP.NET bind URL; RuApp sets port `8000`. |
| `RuBackend__WorkerUrl` | `ws://127.0.0.1:8005/worker/v1` standalone | Private worker protocol URL; RuApp overrides it. |
| `RU_DATA_ROOT` | project/user data default | Isolates settings, sessions, activity, and memory for tests. |

RuApp generates distinct 256-bit desktop and worker secrets, starts the private
Worker and RuAgentBroker before `RuBackend.exe`, assigns all three children to
one kill-on-close Windows Job Object, probes public readiness, and terminates
the owned process tree on shutdown or failure.

## Build and verification

```powershell
dotnet restore backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet list backend-dotnet/RuBackend/RuBackend.csproj package --vulnerable --include-transitive
& D:\LLM\.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
& D:\LLM\build.bat
```

The canonical public surface is
[`public-route-contract.json`](public-route-contract.json). The Python checker
validates its schema/source ownership, and `PublicRouteContractTests` compares
it with ASP.NET's live `EndpointDataSource` so a missing route cannot be hidden
by a forwarder.

For an isolated live gate on temporary ports and data:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\aspnetcore_compatibility_harness.py `
  --worker-port 18030 --edge-port 18031 --require-voice-audio `
  --benchmark-samples 20
```

The harness records only statuses, timings, event types, counts, cleanup flags,
and privacy checks. It must not store tokens, prompts, replies, transcripts,
audio bytes, session IDs, or VTS metadata.

Removing the HTTP proxy eliminates its measured local hop, but it does not make
model inference, cloud providers, voice synthesis, STT, or VTube Studio itself
intrinsically faster. Measure those paths independently before making latency
claims.
