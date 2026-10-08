# LeaseRegistry

This small library coordinates expiring resource leases. `LeaseRegistry` accepts a .NET `TimeProvider` for deterministic callers and exposes acquire, renew, release, and read operations. `LeaseChangeResult` includes a status and the current lease when one exists.

Build the library and the visible smoke project with `dotnet build smoke/Smoke.csproj`. The smoke project documents the basic API contract. Keep public type and method signatures stable while fixing the reported ownership and expiry behavior.
