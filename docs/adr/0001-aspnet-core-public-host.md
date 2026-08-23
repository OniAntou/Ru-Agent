---
status: accepted
date: 2026-08-13
---

# ASP.NET Core is Ru's only public host

Ru keeps the authenticated public contract in ASP.NET Core: 41 HTTP routes and
2 WebSocket routes. Python runtime processes remain private implementation
adapters and may not expose a public HTTP fallback, because dual public
ownership previously added latency, route drift, and ambiguous lifecycle
ownership.

The ASP.NET Core process is the only public host. RuApp owns its lifecycle and
starts the private Runtime Worker and RuAgentBroker alongside it; the Broker
is the execution boundary for local action adapters, not a second public
host. Unknown public paths fail in ASP.NET Core rather than being forwarded to
Python.
