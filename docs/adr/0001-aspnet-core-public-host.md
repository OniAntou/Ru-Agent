---
status: accepted
date: 2026-08-13
---

# ASP.NET Core is Ru's only public host

Ru keeps the authenticated public contract in ASP.NET Core. The original
decision covered 41 HTTP routes and 2 WebSocket routes; the current contract is
42 HTTP routes, including 21 `/agent/v1/*` routes, with no WebSocket route (the
media surfaces were removed on 2026-09-13). Python product processes no longer
exist; any future private
adapter
may not expose a public HTTP fallback, because dual public ownership
previously added latency, route drift, and ambiguous lifecycle ownership.

The ASP.NET Core process is the only public host. The managed Public Host owns
the private RuAgentBroker child and its lifecycle;
RuApp starts or attaches to that host through its validated descriptor rather
than starting the child directly. The Broker is the execution boundary
for local action adapters, not a second public host. Unknown public paths fail
in ASP.NET Core rather than being forwarded to Python.

Clarification (2026-09-01): disposing a GUI/CLI starter releases its local
process handle only. It does not terminate the shared Public Host or place
that Host in a client-owned kill-on-close job. Graceful Host shutdown and the
Broker job remain owned by the Host.

Amendment (2026-09-30): the Host no longer owns a Runtime Worker child. Model
generation runs in the Host process behind `IModelStepRunner` (see
[ADR 0012](0012-in-process-model-runner.md)), and the Host owns only the Broker
child.

Amendment (2026-09-30): the contract is 41 HTTP routes after the session-fork
removal.

Amendment (2026-10-07): the contract is 42 HTTP routes after the input-resolve
route was added.
