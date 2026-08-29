# Python runtime boundary

Status: Phase 0 baseline for the Python-to-C# Agent migration, 2026-08-29.

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

The current default checkout still starts the Python Worker multiplexer. Its
v1 runtime/device handlers and v2 Agent reference implementation are both kept
for compatibility while Phase 1 is implemented. The Phase 1 target is a
separate C# Agent Worker v2 plus a Python v1-only device Worker, with an
explicit trusted Python-compatibility rollback. This inventory is a baseline,
not evidence that the migration or the later OS-enforced filesystem/network
sandbox is complete.
