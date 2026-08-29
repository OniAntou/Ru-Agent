# Python runtime boundary

Status: Phase 1 managed Agent cutover, with the Phase 0 inventory retained for
classification evidence, 2026-08-30.

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
- `provider-only`: Worker v1, voice, speech, VTube Studio, and observation
  adapters retained for the device boundary in later phases;
- `tooling`: scripts, tests, and build/verification helpers;
- `legacy`: retired corpus and other files outside managed startup reachability.

The managed default now starts a separate C# Agent Worker v2 and Python
Device Worker v1 (`--v1-only`) on distinct loopback endpoints, with separate
tokens and a single-use DPAPI-protected provider bootstrap. The Python v2
generation implementation remains available only through the explicit,
trusted `PythonCompatibility` startup rollback; it is not a public request or
UI mode and there is no automatic fallback. This inventory is classification
evidence, not evidence that the later voice/device migration or the
OS-enforced filesystem/network sandbox is complete.

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
an unverified C# HTTP approximation. Until a stable transport is composed and
the startup cutover gates pass, the shipped default remains the Python Device
Worker path described above. This section records an opt-in adapter contract,
not a claim that managed STT is the default or that Python can be retired.
