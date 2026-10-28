# ASP.NET Core public route contract

The canonical public surface is
[`backend-dotnet/public-route-contract.json`](../backend-dotnet/public-route-contract.json):
52 HTTP method/path pairs and no WebSocket route. ASP.NET Core is the only
public owner, and every entry is `aspnet-direct`. The 28 Agent v1 routes under
`/agent/v1/*` (including the run-independent skills catalog/preferences)
and the `POST /host/shutdown` route are active alongside the
settings, session rewind, activity, status, and model routes. They cover
run creation, policy/approval/input/trust, cancellation, typed command dispatch, SSE
replay, and reconciliation; this is not a read-only tracer surface.

`POST /settings/provider/key` validates a candidate Models.dev credential before
committing it, preserving an existing key on rejection or unavailable validation.
It shares the normal loopback/bearer boundary and settings transaction gate. GUI
does not fall back to `POST /settings` when an older Host lacks this route.

Session history adds `/sessions/page` for paged summaries,
`/sessions/{sessionId}/messages` for bounded retained-message pages and
`/sessions/search` for literal full-text hits. These share bearer/loopback checks
and do not expose database maintenance over HTTP.

Each entry contains:

- `method`, `path`, and `transport` — the stable public wire identity;
- `source` — the ASP.NET source file that maps the endpoint;
- `classification` — `aspnet-direct`;
- `winui_callers` — known desktop call sites, which may be empty for routes
  without a WinUI caller.

`aspnet-direct` means ASP.NET performs the business operation itself, including
`GET /status` and the `GET /models` provider catalog read from the host settings
store. There is no worker-backed or Python-backed public route left; the media
surfaces (voice, STT, VTS, observation) were removed in the 2026-09-13 passes.

There are no feature-selected owners, shadow reads, public forwarding routes,
or alternate host mode. Unknown public paths are never sent to Python.

## Reserved routes

23 entries intentionally have empty `winui_callers`: the message search route
`GET /sessions/search` driven by the CLI; the two session
interchange routes (`GET /sessions/{sessionId}/export` and `POST /sessions/import`),
which the CLI and session flows drive; 15 additive `/agent/v1/*` routes —
the 12 run routes (creation, run read, event stream, payload read, approvals,
inputs, trust, cancel, continue, compact, commands, and reconcile) and the three policy
routes (`GET`/`POST /agent/v1/policies` and `DELETE /agent/v1/policies/{ruleId}`)
— which are driven through the Agent harness flow rather than direct WinUI
callers; and the five workspace coordination routes, which external clients
drive over HTTP. The remaining `/agent/v1/*` routes (run activities, session
run listing, and the session preset read) do have direct `ApiService` callers.
The two subagent routes have `AgentRunClientAdapter` callers through the typed
Client library and drive the chat's read-only Subagents panel.
The three skill catalog/preferences/package routes have `CustomizeView` callers
through `SkillsApiClient`, before a run exists. Package installation is an
explicit user action, not an advertised model tool.

Empty `winui_callers` is therefore expected for these reserved routes and is not a dead-route signal.

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
