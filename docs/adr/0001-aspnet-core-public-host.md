---
status: accepted
date: 2026-08-13
---

# ASP.NET Core is Ru's only public host

Ru keeps the authenticated public contract in ASP.NET Core. The original
decision covered 41 HTTP routes and 2 WebSocket routes; the current contract
adds the 15 `/agent/v1/*` routes for a total of 56 HTTP routes and one public
WebSocket route (`/voice/stream`). Python runtime processes remain private
implementation adapters and
may not expose a public HTTP fallback, because dual public ownership
previously added latency, route drift, and ambiguous lifecycle ownership.

The ASP.NET Core process is the only public host. The managed Public Host owns
the private Runtime Worker and RuAgentBroker children and their lifecycle;
RuApp starts or attaches to that host through its validated descriptor rather
than starting those children directly. The Broker is the execution boundary
for local action adapters, not a second public host. Unknown public paths fail
in ASP.NET Core rather than being forwarded to Python.
