# Python source boundary

Status: 2026-09-13 — the repository keeps no Python product source. The last
retained provider sources (`src/ru_runtime/voice` and `src/ru_runtime/speech`
plus the persistence/policy/system mirrors) were removed together with the
voice/STT, VTS, and screen-observation media surfaces: the OmniVoice server,
the Python benchmarks, the `[voice]` optional extra, and the `mypy src` gate no
longer exist. Python in this repository is tooling only — verification scripts,
maintenance helpers, and their tests.

The deterministic inventory is generated from `%RU_ROOT%` with:

```powershell
.venv\Scripts\python.exe scripts\check_python_runtime_boundary.py `
  --root .
```

The scanner records relative paths, a bounded content hash, byte count,
classification, and an `is_known_managed_startup` label for the two configured
C# entrypoint files. Schema 2 reports `known_managed_startup_count`, not a
reachability count. It does not analyze C# launch edges, Python imports, or
transitive dependencies. It does not import or execute product modules. The
scanner is inventory evidence, not permission to launch a retired module.
Output is count-only on stdout. Prompt parity is owned by the C# harness
contract tests; this scanner no longer imports a Python prompt implementation
or generates a cross-language baseline.

Current boundary classes are:

- `managed-startup`: the configured C# entrypoint files;
- `tooling`: scripts, tests, and build/verification helpers;
- `legacy`: other files under the scanned roots (`backend-dotnet/**`) that are
  neither entrypoints nor tooling.

The scan roots are `scripts`, `tools`, `tests`, and `backend-dotnet`.

The managed runtime is the C# Agent Worker: one process that serves
`/worker/v2` on one loopback port with a single-use DPAPI-protected provider
bootstrap. There is no device v1 graph, no OmniVoice process, no shared lease
coordinator, and no provider credential written to disk. Media adapters
(voice, STT, VTS, observation) and their Python counterparts are gone; this
inventory is classification evidence, not evidence that the separate
OS-enforced filesystem/network sandbox project is complete.
