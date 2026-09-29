# ASP.NET Core public route contract

The canonical public surface is
[`backend-dotnet/public-route-contract.json`](../backend-dotnet/public-route-contract.json):
42 HTTP method/path pairs and no WebSocket route. ASP.NET Core is the only
public owner, and every entry is `aspnet-direct`. The 21 Agent v1 routes under
`/agent/v1/*` and the `POST /host/shutdown` route are active alongside the
settings, session fork/rewind, activity, status, and model routes. They cover
run creation, policy/approval/trust, cancellation, typed command dispatch, SSE
replay, and reconciliation; this is not a read-only tracer surface.

Each entry contains:

- `method`, `path`, and `transport` — the stable public wire identity;
- `source` — the ASP.NET source file that maps the endpoint;
- `classification` — `aspnet-direct`;
- `winui_callers` — known desktop call sites, which may be empty for reserved
  lifecycle routes.

`aspnet-direct` means ASP.NET performs the business operation itself, including
`GET /status` and the `GET /models` provider catalog read from the host settings
store. There is no worker-backed or Python-backed public route left; the media
surfaces (voice, STT, VTS, observation) were removed in the 2026-09-13 passes.

There are no feature-selected owners, shadow reads, public forwarding routes,
or alternate host mode. Unknown public paths are never sent to Python.

## Reserved routes

16 entries intentionally have empty `winui_callers`: the two session
interchange routes (`GET /sessions/{sessionId}/export` and `POST /sessions/import`),
which the CLI and session flows drive, plus 14 additive `/agent/v1/*` routes —
the 11 run routes (creation, run read, event stream, payload read, approvals,
trust, cancel, continue, compact, commands, and reconcile) and the three policy
routes (`GET`/`POST /agent/v1/policies` and `DELETE /agent/v1/policies/{ruleId}`)
— which are driven through the Agent harness flow rather than direct WinUI
callers. The remaining `/agent/v1/*` routes (run activities, session run listing,
and the five workspace routes) do have direct `ApiService` callers.

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
