# Python runtime boundary

Status: Phase 2 managed Worker startup is the current default, 2026-08-30.
Python runtime modules remain available only behind the explicit trusted
`PythonCompatibility` rollback while retirement evidence is collected.

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

The managed default starts one C# Worker process that serves `/worker/v2` and
`/worker/v1` on distinct loopback endpoints, with separate tokens and a
single-use DPAPI-protected provider bootstrap. Its v1 graph owns typed runtime
settings, voice/STT/VTS/observation adapters, one shared lease coordinator,
and OmniVoice process shutdown. The bootstrap is read only for the v2 model
provider and its environment path is cleared before the v1 graph is composed;
v1 never receives the provider credential. The Python Agent + Device topology
remains available only through the explicit, trusted `PythonCompatibility`
startup rollback; it is not a public request or UI mode and there is no
automatic fallback. This inventory is classification evidence, not evidence
that the OS-enforced
filesystem/network sandbox is complete.

## Managed speech adapter boundary (Phase 2)

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
transport is supplied. The legacy Python STT path remains reachable only in
`PythonCompatibility`; it is not the managed default and cannot be selected by
a public request.

## Managed Gemini observation adapter boundary (Phase 2)

The managed runtime also owns an `IObservationProvider` implementation.
It owns bounded screenshot/prompt input, the Gemini `generateContent` request,
model fallback, JSON parsing, numeric clamping, and redaction of returned scene,
event, reaction, and visible-text fields. The API key is accepted only through
the provider configuration and sent only as the `x-goog-api-key` header. A
shared observation lease can fail closed with `generation_busy`; missing
credentials return a stable skipped result. Provider response bodies and raw
failure text never cross the Worker response boundary.

Screen capture, reaction scheduling, and the public observation lifecycle are
not silently moved by this adapter. The managed Worker composition binds the
provider with an empty-key default and an explicit control handler, so missing
credentials produce a stable disabled/skipped result and no network call; the
managed default does not claim to capture a screen or emit a reaction. A
trusted credential channel and a managed capture/reaction loop are separate
rollout work. The legacy Python observation path remains reachable only in
`PythonCompatibility`. This section records the managed C# boundary, not live
Gemini or Python-retirement evidence.
