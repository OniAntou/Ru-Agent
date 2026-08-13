---
status: accepted
date: 2026-08-13
---

# ASP.NET Core is Ru's only public host

Ru keeps the existing authenticated 38-HTTP/2-WebSocket contract in ASP.NET
Core. Python runtime processes remain private implementation adapters and may
not expose a public HTTP fallback, because dual public ownership previously
added latency, route drift, and ambiguous lifecycle ownership.
