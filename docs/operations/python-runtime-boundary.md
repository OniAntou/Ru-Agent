# Python runtime boundary

Status: Phase 2 managed Worker composition is implemented opt-in; the Phase 1
startup topology and Phase 0 inventory remain the current default, 2026-08-30.

The deterministic inventory is generated with:

```powershell
D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_python_runtime_boundary.py `
  --root D:\LLM `
  --write-baseline D:\LLM\tests\fixtures\python-csharp-phase1-baseline.json
```

The scanner records relative paths, a bounded content hash, byte count,
classification, and whether an explicit managed-startup edge reaches the file.
It parses Python imports without importing or executing product modules. The
managed startup's `ru_runtime.worker.server` argument is recorded as a
subprocess edge, not as a Python import edge. Output is count-only on stdout;
the baseline fixture contains only deterministic bounded reference values.

Current boundary classes are:

- `production-to-port`: Agent generation, Worker v2, and deterministic
  generation parsers that are candidates for the managed runtime;
- `provider-only`: Python Worker v1/device adapters retained for compatibility
  while their typed C# boundaries are validated;
- `tooling`: scripts, tests, and build/verification helpers;
- `legacy`: retired corpus and other files outside managed startup reachability.

The managed default still starts a separate C# Agent Worker v2 and Python
Device Worker v1 (`--v1-only`) on distinct loopback endpoints, with separate
tokens and a single-use DPAPI-protected provider bootstrap. The C# Worker now
has an opt-in composition that serves both `/worker/v2` and `/worker/v1`: the
v1 graph owns typed runtime settings, voice/STT/VTS/observation adapters, one
shared lease coordinator, and OmniVoice process shutdown. The bootstrap is
read only for the v2 model provider and its environment path is cleared before
the v1 graph is composed; v1 never receives the provider credential. Startup
cutover to this composition is a later rollout gate, so Python remains the
default device compatibility path until Task 10 and the parity/manual gates
pass. The Python v2 generation implementation remains available only through
the explicit, trusted `PythonCompatibility` startup rollback; it is not a
public request or UI mode and there is no automatic fallback. This inventory is
classification evidence, not evidence that the OS-enforced
filesystem/network sandbox is complete.

## Managed speech adapter boundary (Phase 2, opt-in)

The managed runtime now owns the speech request boundary when a C# Worker v1
router is composed with `GoogleSpeechRecognizer`: it accepts only bounded
16 kHz mono PCM/WAV input, validates RIFF chunks before provider I/O, forces
`vi-VN`, applies the existing 15-second timeout, normalizes non-empty
transcripts, and maps provider failures to fixed codes without returning raw
audio or provider bodies.

The Google transport itself remains an injected, isolated provider seam. The
anonymous `speech_recognition` implementation converts captured PCM to FLAC
and relies on a package-managed request shape; it is not silently replaced by
an unverified C# HTTP approximation. The managed Worker composition binds this
boundary to a safe default that returns `speech_unavailable` until a concrete
transport is supplied. Until a stable transport is composed and the startup
cutover gates pass, the shipped default remains the Python Device Worker path
described above. This section records an opt-in adapter contract, not a claim
that managed STT is the default or that Python can be retired.

## Managed Gemini observation adapter boundary (Phase 2, opt-in)

The managed runtime also has an opt-in `IObservationProvider` implementation.
It owns bounded screenshot/prompt input, the Gemini `generateContent` request,
model fallback, JSON parsing, numeric clamping, and redaction of returned scene,
event, reaction, and visible-text fields. The API key is accepted only through
the provider configuration and sent only as the `x-goog-api-key` header. A
shared observation lease can fail closed with `generation_busy`; missing
credentials return a stable skipped result. Provider response bodies and raw
failure text never cross the Worker response boundary.

Screen capture, reaction scheduling, and the public observation lifecycle are
not silently moved by this adapter. The managed Worker composition binds the
provider with an empty-key default, so missing credentials produce a stable
skipped result and no network call; a trusted credential channel is still a
separate rollout concern. Until startup cutover is complete, the shipped
default remains the Python Device Worker observation path above. This section
records an opt-in C# provider contract, not live Gemini or Python-retirement
evidence.
