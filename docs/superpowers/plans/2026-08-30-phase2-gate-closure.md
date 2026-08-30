# Phase 2 gate closure plan

**Date:** 2026-08-30  
**Status:** In progress

## Objective

Close the remaining Phase 2 implementation and verification gaps without
retiring Python prematurely. Phase 3 may start only after the managed runtime
has deterministic coverage for every supported private route and the native
device gates are either passed with bounded evidence or explicitly blocked by
an external dependency.

## Work items

- [ ] Make the OmniVoice boundary readiness-aware. A reachable provider whose
  model is not loaded must be warmed up or return a bounded unavailable error;
  it must never be treated as healthy solely because TCP port 5052 is open.
- [ ] Add managed-runtime integration coverage for the default C# Worker
  topology, while keeping the Python compatibility Harness as a separate
  rollback fixture.
- [ ] Re-audit managed speech, observation, and VTube Studio composition for
  explicit provider configuration, stable errors, and no accidental Python
  imports. Do not add an unverified replacement for the anonymous Google STT
  request contract.
- [ ] Run the route matrix, focused C# tests, Python reference tests, static
  checks, build, and deterministic Harness sequentially. Record only bounded
  status/codes in the current verification documents.
- [ ] Re-run the native acceptance probes when the user-owned microphone,
  speaker, VTube Studio, and optional Gemini credentials are available. Keep
  those results separate from deterministic evidence.

## Exit criteria

1. No managed code reports a loopback provider as healthy without a bounded
   readiness check.
2. Default Dotnet startup has an automated authenticated smoke path covering
   both Worker endpoints and Broker ownership.
3. All deterministic gates pass with `56 HTTP + 1 WebSocket` preserved.
4. Phase 2 documentation states the exact remaining native gates; Python is
   retained until a separate Phase 3 import/startup retirement plan is
   approved and verified.
