# Python runtime boundary

Status: Phase 3 retirement complete for production startup. Dotnet is the only
supported production startup. The production Python generation and Worker
v1/v2 modules are retired. The 2026-08-31 test cleanup also removed the unused
Python VTS/overlay package and its nine test files; managed VTS, its canonical
token file, and native overlay assets are unchanged.
The deterministic Harness uses a test-only fixture under
`scripts/harness`; the retired `PythonCompatibility` selector fails closed with
`python_compatibility_retired` and is not a supported configuration.

The deterministic inventory is generated with:

```powershell
D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_python_runtime_boundary.py `
  --root D:\LLM
```

The scanner records relative paths, a bounded content hash, byte count,
classification, and an `is_known_managed_startup` label for the two configured
C# entrypoint files. Schema 2 reports `known_managed_startup_count`, not a
reachability count. It does not analyze C# launch edges, Python imports, or
transitive dependencies; the earlier schema 1 labels were not reachability
proof either. It does not import or execute product modules. The scanner is
inventory evidence, not permission to launch a retired module. The
Dotnet default launches only `dotnet-agent-worker`; test-only fixture imports
are not production reachability.
Output is count-only on stdout; the baseline fixture contains only deterministic
bounded reference values. Regenerate that fixture only intentionally with
`--write-baseline tests/fixtures/python-csharp-phase1-baseline.json`, then
review the changes and run the C# golden-parity consumers.

Current boundary classes are:

- `managed-startup`: the configured C# entrypoint files;
- `production-to-port`: historical generation/Worker candidates, when scanning
  older trees;
- `provider-only`: retained Python voice, speech and observation sources; this
  directory classification does not establish production use;
- `tooling`: scripts, tests, and build/verification helpers;
- `legacy`: other files outside the classified source groups.

The active Python provider is `voice/omnivoice_server.py` plus its chunks,
pacing, style, tuning and Vietnamese-normalization helpers. Their 48 cases
remain. Memory and speech Python code also retain tooling consumers; Python
tests for those modules are not evidence for managed C# runtime behavior.

The managed default starts one C# Worker process that serves `/worker/v2` and
`/worker/v1` on distinct loopback endpoints, with separate tokens and a
single-use DPAPI-protected provider bootstrap. Its v1 graph owns typed runtime
settings, voice/STT/VTS/observation adapters, one shared lease coordinator,
and OmniVoice process shutdown. The bootstrap is read only for the v2 model
provider and its environment path is cleared before the v1 graph is composed;
v1 never receives the provider credential. The old Python Agent + Device
topology is retired; `runtime-live` reports `python_compatibility_retired`
without launching a child topology. This inventory is classification evidence,
not evidence that the separate
OS-enforced filesystem/network sandbox project is complete.

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
transport is supplied. The legacy Python STT path is not reachable from
managed startup and is not a supported compatibility mode while its
production worker is retired. Native STT acceptance remains a separate
user-owned gate.

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
rollout work. The legacy Python observation path is not reachable from managed
startup. This section records the managed C# boundary, not live Gemini or
Python-retirement evidence.
