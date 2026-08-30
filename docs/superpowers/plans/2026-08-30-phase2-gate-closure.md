# Phase 2 gate closure plan

**Date:** 2026-08-30  
**Status:** Historical checkpoint. The Phase 2 gate closed on 2026-08-30 and
Phase 3 production retirement completed on 2026-08-30; native hardware remains
user-owned.

## Objective

Close the remaining Phase 2 implementation and verification gaps before the
Python runtime retirement. The managed runtime now has deterministic coverage
for every supported private route; Phase 3 may start with the retirement plan
in [`2026-08-30-python-runtime-phase-3-retirement.md`](2026-08-30-python-runtime-phase-3-retirement.md).
Native device checks remain separate user-owned evidence and do not block that
code cleanup.

## Work items

- [x] Make the OmniVoice boundary readiness-aware. A reachable provider whose
  model is not loaded must be warmed up or return a bounded unavailable error;
  it must never be treated as healthy solely because TCP port 5052 is open. (`OmniVoiceHttpProvider.IsReady` + `EnsureProviderReadyAsync` `backend-dotnet/RuAgent.Runtime.Adapters/Audio/OmniVoiceHttpProvider.cs:33`)
- [x] Add managed-runtime integration coverage for the default C# Worker
  topology. The deterministic Harness now uses a test-only fixture worker;
  the retired Python production worker is not a supported rollback path.
  (`WorkerBrokerLaunchPlanTests.Dotnet_mode_owns_both_worker_ports` +
  `ManagedWorkerCompositionTests` + `scan_runtime_boundary` reachable 2)
- [x] Re-audit managed speech, observation, and VTube Studio composition for
  explicit provider configuration, stable errors, and no accidental Python
  imports. Do not add an unverified replacement for the anonymous Google STT
  request contract. (`ManagedRuntimeStatusRequestHandler`, `VtubeStudioRuntimeRequestHandler`, `ObservationControlRequestHandler`, `UnavailableVoiceRuntimeRequestHandler` + scanner `provider-only 0 reachable`)
- [x] Run the route matrix, focused C# tests, Python reference tests, static
  checks, build, and deterministic Harness sequentially. Record only bounded
  status/codes in the current verification documents. (`501` Python tests,
  `2,085` C# Release tests, `56 HTTP + 1 WS`, build `13/13`, Harness passed)
- [ ] Re-run the native acceptance probes when the user-owned microphone,
  speaker, VTube Studio, and optional Gemini credentials are available. Keep
  those results separate from deterministic evidence. (pending user-run `docs/operations/verification.md:105`)

## Exit criteria

1. No managed code reports a loopback provider as healthy without a bounded
   readiness check.
2. Default Dotnet startup has an automated authenticated smoke path covering
   both Worker endpoints and Broker ownership.
3. All deterministic gates pass with `56 HTTP + 1 WebSocket` preserved.
4. Phase 2 documentation states the exact remaining native gates and links the
   approved Phase 3 import/startup retirement plan. The current compatibility
   selector is legacy-only; runtime-live reports a bounded retirement result
   instead of launching a missing Python worker.
