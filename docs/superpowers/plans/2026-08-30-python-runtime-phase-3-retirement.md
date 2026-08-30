# Python runtime retirement — Phase 3

**Date:** 2026-08-30
**Status:** Completed 2026-08-30; managed production startup is C#-only,
legacy Python startup is retired, and native hardware acceptance remains
separate.

## Objective

Retire the remaining production Python Agent/Worker topology and make the
managed C# Worker the only supported runtime startup. Keep the deterministic
Harness fixture and the isolated OmniVoice provider seam where they are still
test or provider boundaries. This phase is a runtime cleanup and cutover; it
does not implement the separate future OS-enforced filesystem/network sandbox.

## Entry evidence from Phase 2

The Phase 2 deterministic gate is complete for the current working tree:

- Python reference/tooling suite: `501 passed`.
- C# Release matrix: `2,085 passed` across the 14 restored test projects.
- Route contract: `56 HTTP + 1 WebSocket` (`/voice/stream`).
- Backend deterministic Harness: `contract` / `passed`, with owned-process and
  cleanup checks green.
- Agent GUI Harness: all four supported Agent scenarios passed.
- Release build: `build.bat` completed all `13/13` steps and published the GUI
  and managed Worker artifacts.
- Runtime-boundary scan: `865` files classified, `2` managed-startup reachable;
  the deterministic worker is test-only and does not import retired production
  generation/worker modules.

Native microphone, speaker/OmniVoice playback, VTube Studio, and opt-in Gemini
acceptance remain user-owned gates and are not prerequisites for this cleanup.

## Implementation slices

1. **Remove the compatibility selector from production startup — complete.**
   The `RuntimeMigrationMode.PythonCompatibility` branch, legacy launch
   arguments, compatibility child environment, and compatibility-only overloads
   were removed. Selecting a retired value fails closed with the stable
   `python_compatibility_retired` error before mutex, descriptor, or child
   startup. Host, Harness, and contract tests no longer advertise the selector.
2. **Retire dead Python production modules — complete.** The remaining
   `src/ru_runtime/generation` and `src/ru_runtime/worker/v1`/`v2` production
   modules and Python Worker multiplexer/fallbacks were removed. The
   `scripts/harness` fixture and generation stub remain explicitly test-only;
   OmniVoice remains an isolated provider boundary.
3. **Close the live Harness gap — complete by retirement.** `runtime-live` is
   retained only as a bounded diagnostic until a managed-native live path exists.
   It never launches `ru_runtime.worker.v1.server` or any partial topology and
   returns `python_compatibility_retired` with the next gate
   `managed_runtime_native`.
4. **Reconcile contracts and documentation — complete.** Launch-plan tests,
   boundary scans, packaging checks, READMEs, verification records, and current
   runbooks now describe managed C# ownership. The `56 HTTP + 1 WebSocket`
   public contract and private voice/VTS/provider-data boundary are preserved.
5. **Re-run the gates sequentially — complete.** Import/compile, Python,
   C# Release, route, backend Harness, Agent GUI Harness, managed startup smoke,
   release build, and `git diff --check` were run sequentially. Native
   user-owned acceptance remains separate.

## Verification record (2026-08-30)

| Gate | Result |
| --- | --- |
| Python suite | `504 passed`; Ruff, Mypy (`50` files), `pip check`, and compileall passed |
| C# Release matrix | `2,086 passed` across 13 test assemblies |
| Route contract | `56 HTTP + 1 WebSocket` |
| Boundary scan | `864` files; `2` managed-startup reachable; no production-to-port reachable |
| Backend Harness | `48/56` HTTP passed, `8` external skipped, `1/1` WebSocket passed; cleanup green |
| Agent GUI Harness | 4/4 supported Agent scenarios passed |
| Managed startup | `/status` `200`, wrong bearer `401`; managed Worker and Broker owned both private ports |
| Retired selector | exit `5`, stable `python_compatibility_retired`, no child topology |
| Release build | `build.bat` `13/13`; managed Worker/Core, Broker, backend, and GUI published |
| Workspace diff | `git diff --check` passed |

The voice-focused GUI runner was not counted as a production success gate: its
first synthetic turn reached capture/transcription but timed out before the
reply lifecycle. This remains separate from the native microphone, speaker,
OmniVoice, VTube Studio, and Gemini acceptance table below.

## Exit criteria

- [x] No production code, launch plan, package manifest, or current documentation
  references a deleted Python generation/worker module as a supported startup.
- [x] A clean checkout can import and build without the retired Python modules;
  test-only fixtures remain isolated under `scripts/harness`.
- [x] Managed C# Worker startup owns both private ports, with authenticated Agent
  and device probes plus Broker lifecycle/cleanup evidence.
- [x] The public route matrix remains `56 HTTP + 1 WebSocket`, and deterministic
  Agent/voice/privacy checks remain green.
- [x] The native hardware table is still reported separately; no deterministic or
  build result is presented as audible, microphone, VTube Studio, or Gemini
  quality acceptance.
