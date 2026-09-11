# ASP.NET Core public route contract

The canonical public surface is
[`backend-dotnet/public-route-contract.json`](../backend-dotnet/public-route-contract.json):
62 HTTP method/path pairs and one WebSocket route (`/voice/stream`). ASP.NET Core
is the only public owner. The 20 Agent v1 routes under `/agent/v1/*` and the
`POST /host/shutdown` route are active
alongside the settings, session fork/rewind, activity, runtime, and voice
routes. They cover run creation, policy/approval/trust, cancellation, typed
command dispatch, SSE replay, and reconciliation; this is not a read-only
tracer surface.

Each entry contains:

- `method`, `path`, and `transport` — the stable public wire identity;
- `source` — the ASP.NET source file that maps the endpoint;
- `classification` — `aspnet-direct` or `runtime-worker`;
- `winui_callers` — known desktop call sites, which may be empty for reserved
  lifecycle routes.

`aspnet-direct` means ASP.NET performs the business operation itself. The
current direct groups are settings, credentials, sessions, activity,
and Agent orchestration. `runtime-worker` means ASP.NET still owns the
public request/response or WebSocket contract but delegates model/device work
over authenticated private protocol v1. This covers status/models, voice, STT,
VTS, and observation.

There are no feature-selected owners, shadow reads, public forwarding routes,
or alternate host mode. Unknown public paths are never sent to Python.

## Reserved routes

21 entries intentionally have empty `winui_callers`:

- `POST /observation/preflight` — internal harness/validation preflight for observation; WinUI polls via `GET /observation/status` and `POST /observation/start|stop|react|ack` (`ApiService.GetObservationStatusAsync` etc.).
- 20 additive `POST|GET|PUT|DELETE /agent/v1/*` routes — Agent v2 run/policy/workspace surface driven through the Agent harness flow rather than direct WinUI callers.

Empty `winui_callers` is therefore expected for these reserved lifecycle routes and is not a dead-route signal.

## Verification

```powershell
& (Join-Path $env:RU_ROOT ".venv\Scripts\python.exe") scripts\check_aspnetcore_route_matrix.py
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore `
  --filter FullyQualifiedName~PublicRouteContractTests
```

The checker validates count, uniqueness, schema, final classification, source
existence, and route literals. The .NET test starts the real application and
compares the contract against ASP.NET's `EndpointDataSource`; WebSocket routes
are represented by their HTTP upgrade endpoints during this comparison.

Any public route change must update its endpoint tests, this JSON contract, and
the WinUI caller list in the same change.
