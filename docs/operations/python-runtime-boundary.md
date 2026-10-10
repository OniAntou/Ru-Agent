# Python source boundary

Status: 2026-09-13 — the repository keeps no Python product source. The last
retained provider sources (`src/ru_runtime/voice` and `src/ru_runtime/speech`
plus the persistence/policy/system mirrors) were removed together with the
voice/STT, VTS, and screen-observation media surfaces: the OmniVoice server,
the Python benchmarks, the `[voice]` optional extra, and the `mypy src` gate no
longer exist. Python in this repository is tooling — verification scripts,
maintenance helpers, and their tests. Amendment (2026-10-08): Python may also
back optional product capabilities (skills declaring `requires: python`); the
default path stays in-process .NET and a missing interpreter fails the
capability closed with a named code. Bundling an interpreter requires an ADR.

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

- `managed-startup`: the two configured C# entrypoint files,
  `backend-dotnet/RuBackend/Program.cs` and
  `backend-dotnet/RuBackend/Hosting/InProcessExecutionAuthority.cs`;
- `tooling`: every file under `scripts/`, `tools/`, and `tests/`;
- `legacy`: every other scanned file — the rest of `backend-dotnet/**` plus
  `gui-winui3/**`, `gui-winui3.Tests/**`, and `gui-winui3.Harness/**` — that is
  neither an entrypoint nor tooling.

The scan roots are `scripts`, `tools`, `tests`, `backend-dotnet`, `gui-winui3`,
`gui-winui3.Tests`, and `gui-winui3.Harness`.

The managed model runtime is the Host's in-process typed model step
(`ModelStepService` behind `IModelStepRunner`): there is no worker process,
loopback port, wire protocol, or provider bootstrap file. Tool execution is
in process as well (`BrokerExecutionRuntime` behind `IToolGatewayFactory`,
ADR-0014), so the Host starts no managed child at all: the Broker child, its
loopback lease and `RuAgentBroker.exe` are gone. There is no device v1 graph,
no OmniVoice process, no shared lease coordinator, and no provider credential
written to disk. Media adapters
(voice, STT, VTS, observation) and their Python counterparts are gone; this
inventory is classification evidence, not evidence of OS-level confinement.
Terminal children in `standard` and `full_access` run as the current Windows
user without OS filesystem or network confinement. Structured file tools retain
their own path protections.
