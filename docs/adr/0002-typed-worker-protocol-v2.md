---
status: accepted
date: 2026-08-13
---

# Use typed worker protocol v2

The private worker seam uses versioned typed domain commands over authenticated
loopback WebSocket. It does not carry HTTP methods, paths, content types, or
status codes; this keeps public response mapping in ASP.NET Core and removes
the migration-era pseudo-HTTP interface.
