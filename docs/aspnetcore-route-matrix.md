# ASP.NET Core public route contract

The canonical public surface is
[`backend-dotnet/public-route-contract.json`](../backend-dotnet/public-route-contract.json):
41 HTTP method/path pairs and two WebSocket routes. ASP.NET Core is the only
public owner.

Each entry contains:

- `method`, `path`, and `transport` — the stable public wire identity;
- `source` — the ASP.NET source file that maps the endpoint;
- `classification` — `aspnet-direct` or `runtime-worker`;
- `winui_callers` — known desktop call sites, which may be empty for reserved
  lifecycle routes.

`aspnet-direct` means ASP.NET performs the business operation itself. The
current direct groups are settings, credentials, sessions, history, activity,
memory, and chat orchestration. `runtime-worker` means ASP.NET still owns the
public request/response or WebSocket contract but delegates model/device work
over authenticated private protocol v1. This covers status/models, voice, STT,
VTS, and observation.

There are no feature-selected owners, shadow reads, public forwarding routes,
or alternate host mode. Unknown public paths are never sent to Python.

## Reserved routes

Two entries intentionally have empty `winui_callers`:

- `GET /history` — legacy `IHistoryStore.ReadLegacyAsync` retained for migration verification only; WinUI uses `GET /sessions` and `POST /sessions/{id}/activate` (`ApiService.GetSessionsAsync`/`GetSessionMessagesAsync`).
- `POST /observation/preflight` — internal harness/validation preflight for observation; WinUI polls via `GET /observation/status` and `POST /observation/start|stop|react|ack` (`ApiService.GetObservationStatusAsync` etc.).

Empty `winui_callers` is therefore expected for these reserved lifecycle routes and is not a dead-route signal.

## Verification

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore `
  --filter FullyQualifiedName~PublicRouteContractTests
```

The checker validates count, uniqueness, schema, final classification, source
existence, and route literals. The .NET test starts the real application and
compares the contract against ASP.NET's `EndpointDataSource`; WebSocket routes
are represented by their HTTP upgrade endpoints during this comparison.

Any public route change must update its endpoint tests, this JSON contract, and
the WinUI caller list in the same change.
