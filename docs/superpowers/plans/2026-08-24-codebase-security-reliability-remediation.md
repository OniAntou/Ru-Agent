# Ru Codebase Security and Reliability Remediation Implementation Plan

> **Status:** Historical implementation record. The remediation pass shipped on
> `main`; current ownership and verification boundaries are in
> `docs/architecture.md` and `docs/operations/verification.md`.

> **Approved for execution:** 2026-08-24. Work is performed directly on
> `main`, preserving unrelated user changes. The primary agent owns P0/P1;
> Luna agents may edit only the explicitly delegated P2 file sets.

**Goal:** Close every actionable P0-P2 finding from the 2026-08-24 codebase
review while preserving Ru's public contracts and avoiding live desktop,
audio, volume, microphone, and VTube Studio mutation.

**Architecture:** Keep the existing Public Host, Runtime Worker, Broker, and
WinUI boundaries. Tighten authorization at the Broker boundary, centralize
model-visible tool contracts in `RuAgent.Protocol`, bound file/process data at
the point of capture, and make CI exercise the same deterministic release
matrix documented for local development.

**Toolchain:** Python 3.13, pytest, Ruff 0.16.2, Mypy 2.3.0, .NET 10/xUnit,
WinUI 3, CMake/vcpkg, GitHub Actions.

## Global constraints

- Use focused RED-GREEN tests for every behavior change. Record the expected
  failing assertion before editing production code.
- Run heavyweight suites sequentially in `D:\LLM`; do not run Python, .NET,
  GUI, or native builds concurrently.
- Do not invoke live microphone, speaker, volume, desktop-control, OmniVoice,
  or VTube Studio acceptance paths.
- Do not persist raw prompts, replies, provider bodies, paths, command output,
  exception text, credentials, or private Agent/VTS metadata.
- Do not add permanent lint/type ignores, `continue-on-error`, or policy
  exceptions to make a gate green.
- Inspect `git diff` before each commit and leave unrelated user work intact.
- Reconcile current operational documentation after implementation; preserve
  historical checkpoint documents as historical records.

## Baseline evidence

- `python -m ruff check src tests scripts tools`: 348 findings, 266 safely
  auto-fixable at baseline.
- `python -m mypy src`: 90 findings across 17 files at baseline.
- The accepted design is
  `docs/superpowers/specs/2026-08-24-codebase-security-reliability-remediation-design.md`.

## Task 1: Terminal capability and approval policy

**Primary owner**

**Tests:**

- Modify `backend-dotnet/RuAgent.Broker.Tests/ToolBindingRegistryTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/ToolExecutionServiceTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs`.

**Implementation:**

- Modify `backend-dotnet/RuAgent.Broker/Policy/CapabilityProfile.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Policy/ToolPolicyEngine.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalManifest.cs`.

**RED:** Add assertions that Agent advertises no terminal binding and cannot
invoke one, while Full advertises both terminal tools but receives
`approval_required` for every `terminal.run` and `terminal.shell` request.
Assert that an adapter is not called before a valid bound approval is supplied.

Run:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --nologo --filter "FullyQualifiedName~ToolBindingRegistryTests|FullyQualifiedName~ToolCatalogTests|FullyQualifiedName~PolicyTests|FullyQualifiedName~ToolExecutionServiceTests|FullyQualifiedName~BrokerHostTests"
```

Expected RED: old Agent terminal assertions and Full no-approval behavior fail.

**GREEN:** Remove `terminal.execute` from Agent, keep it only in Full, mark both
terminal manifests approval-required, and delete the policy bypass coupled to
`terminal.network`. Preserve session/request/tool/argument-hash approval
binding and stale/replay denial.

Run the focused command again and require zero failures.

## Task 2: Canonical model-tool contracts

**Primary owner**

**Tests:**

- Add `backend-dotnet/RuAgent.Protocol.Tests/AgentModelToolContractCatalogTests.cs`
  if a Protocol test project is justified; otherwise keep cross-boundary tests
  in the two existing test projects below.
- Modify `backend-dotnet/RuAgent.Broker.Tests/ToolSchemaValidatorTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Conversation/AgentModeProfileTests.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs`.

**Implementation:**

- Modify `backend-dotnet/RuAgent.Protocol/AgentToolContracts.cs` or add one
  small catalog file beside it.
- Modify `backend-dotnet/RuAgent.Broker/Tools/ToolManifest.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalManifest.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Tools/ToolSchemaValidator.cs` only if
  the canonical schemas need a currently unsupported validation keyword.
- Modify `backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs`.

**RED:** Prove the Host-provided terminal example uses `executable`,
`arguments`, `working_directory`, and `network`; prove that exact example is
accepted by the Broker schema; prove missing/wrong primitive fields fail before
adapter execution. Prove catalog consumers receive independent JSON nodes so
one mutation cannot corrupt later requests.

Run:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --nologo --filter "FullyQualifiedName~ToolCatalogTests|FullyQualifiedName~ToolSchemaValidatorTests"
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~AgentModeProfileTests|FullyQualifiedName~ConversationCoordinatorTests"
```

**GREEN:** Introduce one protocol-level catalog for tool ID, version, schema,
and model hint. Make Broker manifests and Host worker settings consume it.
Return fresh `JsonObject`/`JsonNode` instances from factories. Do not retain a
second hand-written Host hint table.

Run both focused commands again and require zero failures.

## Task 3: Bounded file reads and edits

**Primary owner**

**Tests:**

- Add `backend-dotnet/RuAgent.Broker.Tests/FileReadAdapterTests.cs` if no
  existing focused read fixture is suitable.
- Modify `backend-dotnet/RuAgent.Broker.Tests/FileEditAdapterTests.cs`.

**Implementation:**

- Modify `backend-dotnet/RuAgent.Broker/Tools/Desktop/FileTools.cs`.

**RED:** Use a large/sparse temporary file. Prove `files.read` returns at most
`maxBytes`, reports truncation, and does not request an allocation equal to the
complete file. Prove `files.edit` rejects metadata above 4 MiB before reading;
retain a bounded second guard for growth between stat and read.

Run:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --nologo --filter "FullyQualifiedName~FileReadAdapterTests|FullyQualifiedName~FileEditAdapterTests"
```

**GREEN:** Replace `File.ReadAllBytes` on untrusted sizes with a helper that
streams a fixed `limit + 1` window. Preserve UTF-8 handling, response fields,
workspace containment, junction/symlink checks, and write approvals.

Run the focused command again and require zero failures.

## Task 4: Child-process environment and bootstrap diagnostics

**Primary owner**

**Tests:**

- Add `gui-winui3.Tests/BackendChildProcessPolicyTests.cs`.
- Modify `gui-winui3.Tests/BackendLaunchPlanTests.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Diagnostics/DiagnosticRetentionTests.cs`.

**Implementation:**

- Add `gui-winui3/Services/BackendChildProcessPolicy.cs`.
- Modify `gui-winui3/Services/BackendProcessRuntime.cs`.
- Modify `gui-winui3/Services/BackendLaunchPlan.cs`.
- Add `gui-winui3/Properties/AssemblyInfo.cs` only if a narrow
  `InternalsVisibleTo` seam is required.
- Modify `backend-dotnet/RuBackend/Diagnostics/DiagnosticRetentionService.cs`.

**RED:** Start an inert child through the policy seam with a sentinel provider
credential and unrelated `RU_*` variable in the parent. Prove they are absent
while required Windows runtime variables and role variables remain. Feed raw
secret/control-marker stdout and stderr, prove both streams are fully drained,
and prove no raw line appears in the JSONL bootstrap summary. Prove retention
recursively includes bootstrap JSONL and legacy bootstrap `.log` files without
making legacy raw logs readable through the diagnostics API.

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter "FullyQualifiedName~BackendChildProcessPolicyTests|FullyQualifiedName~BackendLaunchPlanTests"
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~DiagnosticRetentionTests"
```

**GREEN:** Clear inherited child environment, rebuild the documented small
Windows allowlist, then apply role variables. Drain streams into bounded
structured counters only: timestamp, fixed role/stream, saturated line/byte
counts, and truncation. Store summaries as JSONL under `logs/bootstrap` and
extend recursive retention for JSONL plus legacy `.log` cleanup.

Run both focused commands again and require zero failures.

## Task 5: Production logging-provider ownership

**Primary owner**

**Tests:**

- Modify `tests/test_diagnostic_log.py` and/or `tests/test_privacy_logging.py`.
- Modify
  `backend-dotnet/RuBackend.Tests/Diagnostics/StructuredDiagnosticLoggerProviderTests.cs`
  or add a focused service-registration assertion through the existing test
  application factory.

**Implementation:**

- Modify `src/ru_runtime/__init__.py`.
- Modify `src/ru_runtime/persistence/diagnostic_log.py` if a replace-existing
  registration seam is needed.
- Modify `src/ru_runtime/worker/v1/server.py`.
- Modify `backend-dotnet/RuBackend/Program.cs`.

**RED:** Prove importing `ru_runtime` does not install a free-form console
handler. Prove Worker bootstrap replaces non-structured root handlers, and
ASP.NET dependency injection owns only the structured diagnostic provider.

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_diagnostic_log.py D:\LLM\tests\test_privacy_logging.py
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~StructuredDiagnosticLoggerProviderTests"
```

**GREEN:** Remove package-import `basicConfig`, give library logging a
`NullHandler`, make Worker production registration explicitly own its safe
handler, and clear ASP.NET default providers before adding the structured
provider. Keep diagnostic events bounded and allowlisted.

Run both focused commands again and require zero failures.

## Task 6: Activity outbox and `observation_reaction`

**Luna owner; disjoint P2 file set; no commit**

**Tests:**

- Modify `tests/test_activity_outbox.py`.
- Modify `backend-dotnet/RuBackend.Tests/Business/ActivityPreviewTests.cs`.
- Add or modify one focused WinUI activity-model test under
  `gui-winui3.Tests` if the presentation branch is not otherwise exercised.

**Implementation:**

- Modify `src/ru_runtime/persistence/activity_outbox.py`.
- Modify `backend-dotnet/RuBackend/Business/Activity/ActivityPreview.cs`.
- Modify `gui-winui3/Views/LogView.xaml.cs`.

**RED:** Deterministically enqueue between the two flush drain opportunities
and prove the second batch is currently lost. Prove `observation_reaction`
preview sanitization/persistence and WinUI label/presentation are missing.

**GREEN:** Preserve or immediately append the second drained batch, add the
canonical type end-to-end, and bound/filter preview, scene, event, and
observation ID. Run only focused Python, Backend, and WinUI tests; return the
RED and GREEN commands/output plus `git diff --check` to the primary agent.

## Task 7: Native build behavior and current documentation

**Luna owner; disjoint P2 file set; no commit**

**Tests:**

- Add `tests/test_build_script.py`.

**Implementation:**

- Modify `build.bat`.
- Modify `docs/adr/0001-aspnet-core-public-host.md`.
- Modify `docs/architecture.md`.
- Modify `backend-dotnet/README.md`.
- Modify `docs/operations/logging.md`.
- Modify `docs/operations/agent-tools.md`.
- Modify `docs/operations/verification.md`.

**RED:** Execute a copied build script in a temporary directory with stubbed
`dotnet.cmd` and `powershell.cmd`, a pre-existing overlay executable, and no
live build. Prove the old existence shortcut skips PowerShell/CMake. Do not use
a source-text-only assertion.

**GREEN:** Always invoke the incremental native build/test script, normalize
step counts, and keep failures fail-fast. Reconcile current docs to 41 HTTP + 2
WebSocket routes, Worker port 8005, three owned children including Broker,
structured bootstrap/retention, Agent no-terminal, Full per-request terminal
approval, and the final CI gates. Do not rewrite historical checkpoint docs.
Return RED/GREEN evidence and `git diff --check` to the primary agent.

## Task 8: Ruff, Mypy, dependency metadata, and CI

**Primary owner**

**Tests/gates:**

- Modify `pyproject.toml`.
- Modify `.github/workflows/ci.yml`.
- Modify Python source/test/script files reported by Ruff or Mypy, keeping
  changes mechanical or type-safety-preserving unless a focused regression
  test demonstrates a behavior fix.

**RED:** Preserve baseline output in the task record. Add pinned Ruff/Mypy to
the `dev` extra and add explicit CI commands:

```powershell
python -m ruff check src tests scripts tools
python -m mypy src
python -m pip check
```

Expand .NET CI to Broker, Connectors, Public Host, WinUI tests, GUI Harness
build, and release GUI build. CI must not use `continue-on-error`.

**GREEN sequence:**

1. Run `python -m ruff check src tests scripts tools --fix` for safe mechanical
   fixes only.
2. Repair remaining Ruff findings manually; do not add broad suppressions.
3. Repair all 90 baseline Mypy findings with real narrowing, typed helpers,
   platform guards, and optional-state assertions; do not replace useful types
   with `Any` merely to satisfy the checker.
4. Run focused tests for any non-mechanical behavior affected.
5. Run the three commands above and require zero findings/errors.
6. Validate workflow YAML by inspection and execute every new local equivalent
   sequentially in Task 9.

## Task 9: Integrated review and deterministic release matrix

**Primary owner**

1. Inspect every delegated diff. Reject scope leakage, source-grep pseudo-tests,
   broad ignores, privacy regressions, and unrelated edits.
2. Re-run each focused test group after integration.
3. Run `git diff --check` and inspect `git diff --stat` plus every security- or
   privacy-sensitive hunk.
4. Review `README.md`, `TODO.md`, `backend-dotnet/README.md`, `docs/README.md`,
   `docs/architecture.md`, `docs/adr`, and `docs/operations` for current-state
   drift. Remove only genuinely obsolete current docs; do not delete historical
   implementation records.
5. Run the following heavyweight gates sequentially:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m ruff check D:\LLM\src D:\LLM\tests D:\LLM\scripts D:\LLM\tools
& D:\LLM\.venv\Scripts\python.exe -m mypy D:\LLM\src
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output "$env:TEMP\ru-remediation-harness.json"
& D:\LLM\.venv\Scripts\python.exe -m pip check
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --nologo
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --nologo
cmd /c D:\LLM\build.bat
```

6. Do not run native volume acceptance or interactive GUI Harness. Report those
   as explicit user-run acceptance, separate from deterministic completion.
7. Commit cohesive reviewed changes directly to `main`. The final report must
   distinguish automated proof from unrun live acceptance and mention the
   deferred OS-enforced sandbox project.
