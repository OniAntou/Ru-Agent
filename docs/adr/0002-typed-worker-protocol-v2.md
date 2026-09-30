---
status: superseded
date: 2026-08-13
superseded_by: ADR-0012 in-process model runner
  (`0012-in-process-model-runner.md`, accepted 2026-09-30).
---

# Use typed worker protocol v2

> Superseded by [ADR 0012](0012-in-process-model-runner.md) on 2026-09-30: the
> typed worker WebSocket protocol and the worker executable were retired when
> model generation moved into the Host process. This file remains as migration
> history.

The private worker seam uses versioned typed domain commands over authenticated
loopback WebSocket. It does not carry HTTP methods, paths, content types, or
status codes; this keeps public response mapping in ASP.NET Core and removes
the migration-era pseudo-HTTP interface.
