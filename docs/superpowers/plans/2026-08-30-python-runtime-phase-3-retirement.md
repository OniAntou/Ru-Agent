# Python runtime retirement — Phase 3

**Date:** 2026-08-30
**Status:** Completed 2026-08-30; managed production startup is C#-only,
legacy Python startup is retired, and native hardware acceptance remains
separate. GUI publish and launcher hardening completed 2026-08-31.
Cloud generation tuning controls and backend fields were removed 2026-08-31.

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

## GUI publish and launcher follow-up (2026-08-31)

At this follow-up's 2026-08-31 checkpoint, the desktop shortcut targeted
`start_dev.bat`, but the
published GUI artifact had a framework-dependent runtimeconfig and failed before
creating a window on a host where the x64 runtime was not resolved. The release
build now explicitly selects `Platform=x64`, `RuntimeIdentifier=win-x64`, and
`SelfContained=true`, removes the stale GUI publish directory, and fails unless
`RuApp.runtimeconfig.json` contains `includedFrameworks`. `start_dev.bat` passes
the repository workspace as the GUI working directory and uses
`scripts/launch_gui.ps1` to keep immediate startup failures visible.
The probe treats a clean exit as successful as well, which preserves normal
single-instance handoff when the GUI is already open.

Superseded on 2026-09-04: normal desktop shortcuts now target the published
`RuApp.exe` directly to avoid a console flash. `start_dev.bat` remains available
for manual startup diagnostics.

Verification on 2026-08-31 passed: the Python suite completed 506/506, the
13-project C# Release matrix completed 2,086/2,086, `build.bat` reached
`[13/13] Build complete`, and the published runtimeconfig contained
`includedFrameworks` with the native self-contained host files present. A
direct startup-probe run returned exit code 0 with `RuApp.exe` still alive;
the missing-executable probe returned exit code 1 as expected.

## Cloud generation tuning retirement (2026-08-31)

The cloud-first product no longer exposes `Temperature`, `Top-K`, or `Max New
Tokens`. The controls and DTO properties were removed from the WinUI settings
surface; the C# settings snapshot, persistence/API response, and Worker v2
settings contract no longer carry those fields. Legacy values are ignored and
stripped when settings are read or saved. The managed Agent request now omits
all three provider parameters, allowing each cloud provider/model to apply its
own defaults.

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
