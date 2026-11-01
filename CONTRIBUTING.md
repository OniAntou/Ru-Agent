# Contributing to Ru Agent

This guide covers setup, verification and contribution practices. Agent
workflow lives in [AGENTS.md](AGENTS.md); domain language and current wiring live
in [CONTEXT.md](CONTEXT.md) and [docs/architecture.md](docs/architecture.md).

Start a [bug report](https://github.com/OniAntou/Ru-Agent/issues/new?template=bug_report.yml)
with a reproduction, or a [feature request](https://github.com/OniAntou/Ru-Agent/issues/new?template=feature_request.yml)
with the workflow you want to improve. General questions belong in
[Discussions](https://github.com/OniAntou/Ru-Agent/discussions); security reports
follow [SECURITY.md](SECURITY.md). For the repository wordmark, see
[docs/assets/README.md](docs/assets/README.md).

Ru is a native Windows coding agent with a WinUI 3 app, terminal TUI, and
ASP.NET Core Public Host that runs the Broker execution authority in process.
The default product is .NET 10 and its
published runtime is self-contained. Python may back optional skills/document
capabilities when the default path remains in-process .NET; a missing
interpreter produces a named failure. Bundling one requires an ADR.

## Development environment

- Windows 10 or 11, 64-bit
- .NET SDK 10.0 (x64)
- Python 3.11–3.13 for repository tooling/guards; optional Python-based
  capabilities document their own requirements
- VS 2022 C++ Build Tools when building native components from source

## Build and run

```powershell
.\build.bat          # Release, self-contained, flattened into one directory
.\start_dev.bat      # development launch
```

The first stage restores the GUI's self-contained win-x64 runtime packs before
cleaning; even `dotnet clean` needs those assets on a fresh checkout.

`build.bat` currently runs 11 stages and publishes the desktop executable to:

```powershell
gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe
```

## Workspace maintenance

Historical publish copies and installer fixtures under `artifacts/` can retain
several copies of the self-contained runtime. Remove obsolete binary payloads
only after checking the exact paths and confirming no running process uses
them. Preserve acceptance reports, logs and checksums as verification evidence.
Keep the current release, `artifacts/tools` and the publish directories used by
`bin/ru.cmd` and the development launch scripts.

Ignored files are not necessarily disposable: retain local scripts, Python
environments, models, credentials and profile databases. Do not use a blanket
`git clean -fdx` for routine workspace cleanup.

## Testing

Terminal integration tests require `rg` (ripgrep) on PATH in addition to Windows
PowerShell and Git. Windows CI extracts the official ripgrep 14.1.1 archive,
verifies its pinned SHA-256, and adds it to the runner's PATH.
Concurrency fixtures use independent workers for editor/cancellation probes so
thread-pool load cannot suppress the action being tested. Real PowerShell startup
budgets are separate from cancellation latency and production tool limits.
Broker tests spawning terminal process trees share a nonparallel xUnit collection;
other collections retain parallel execution. This prevents unrelated test load
from interfering with process startup, pipe drains and cancellation probes.
ANSI snapshot fixtures wait for view coordinates to match the driver's resized
buffer before asserting cells; a fixed timer alone does not guarantee layout.
Terminal.Gui disposal and diagnostic-writer fixtures enter and leave a running
frame before disposal, matching native startup/shutdown and avoiding the Windows
ANSI native-input startup race from disposing immediately after Init.
ConPTY fixtures retain their process-exit assertions and allow up to five seconds
for Windows to release sharing locks on their own temporary directory during cleanup.

Choose verification by impact. During iteration, run checks that exercise the
changed behavior; expand for failures or cross-component dependencies. A small
edit does not require every matrix on each iteration.

| Change | Local verification |
| --- | --- |
| Documentation only | Relevant link, count and documentation guards |
| Scoped .NET implementation, including local query/filter changes | Affected project build and test project(s), including regression/error cases |
| Python tooling or SDK | Affected tests and language-specific checks |
| Shared schema migration, transaction/recovery semantics, execution authority/approval behavior or protocol compatibility | Full .NET matrix and affected tooling/contract guards |
| Release/distribution | Full applicable matrices, `build.bat` and acceptance for changed packaged behavior |

Judge the affected behavior, not the folder name or number of changed files.
A persistence query or internal refactor can use focused checks when it preserves
schema, atomicity, recovery, authority and contracts. Expand to the full matrix
when shared behavior can affect consumers across components. Run the final
applicable matrix before integration, not after every intermediate edit.

Useful focused commands:

```powershell
dotnet build backend-dotnet/RuBackend/RuBackend.csproj -c Debug
.\scripts\test_dotnet.ps1 -Configuration Debug -ProjectFilter "Broker"
.\scripts\test_terminal_input.ps1  # Windows ConPTY: six Unicode/paste/navigation cases
.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q
```

Choose the build target and `-ProjectFilter` for the changed module. Full matrices:

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug   # currently 13 projects
.venv\Scripts\python.exe -m pytest tests -q
```

CI also runs the credential-free Host continuation and request-prefix gate:
`scripts/benchmark_agent_loop.ps1`. It shares the test-matrix mutex, uses fresh
sequential 16/32/60-step samples, and retains per-sample evidence. See the
[workload, comparison mode and latency budget](tools/agent-performance/README.md).

**Run one process/loopback matrix at a time.** The .NET suite launches real
processes and HTTP hosts; concurrent matrices, publishing or a running app can
cause environmental failures. Keep the machine-wide lock; the runner exits 75
on contention. Do not bypass it.

The runner does not automatically retry failed tests. Preserve the first
failure, investigate it, then rerun after a fix or justified environment
correction. Report both outcomes and suspected flakiness; a later green run does
not erase the original failure.

Python guards check README counts, manifest digests, permission vocabulary and
the endpoint list against `public-route-contract.json`. CI keeps
both full matrices on `windows-latest`; focused local checks do not remove CI
gates. Report commands, outcomes and material gaps. Deterministic tests do not
establish real-provider quality or native UI acceptance.

## Changes and contracts

NuGet versions belong in root `Directory.Packages.props`; product and test
projects omit local `PackageReference` versions. Benchmark candidate repositories
under `tools/coding-benchmark` opt out of this product dependency policy.
GUI C# builds use the same warnings-as-errors baseline as product services.

Keep changes focused on one outcome. Create/split files for clear ownership;
refactor the affected area when needed. Prefer cohesive functions and readable
names; line counts and naming prefixes are guidance. Comments explain intent
or constraints. Handle async errors at the responsible boundary and propagate
cancellation.

Tests capture accepted behavior, which can evolve. Investigate failures before
choosing whether code or expectations need correction. Intentional behavior
changes update implementation, contract and tests together, with the reason
visible in review. Refactors preserve behavior and permission/error coverage.
Prefer behavioral tests; retain source-contract checks for explicit ownership
and update their helpers/assumptions with deliberate structural changes.

Reproducible behavioral bugs need a focused regression test that fails on the
old implementation. Simple documentation, presentation or low-impact edits can
use existing checks; avoid tests that merely restate the implementation.

## Security and execution boundaries

[AGENTS.md](AGENTS.md#security-and-execution-boundaries) owns the execution,
permission and secret-handling rules for contributors and agents. Consult
[the tool policy](docs/operations/agent-tools.md) for runtime behavior;
keep these guides aligned rather than copying the policy into each one.

Provider keys use Windows Credential Manager through Settings. Tests may use
synthetic credentials, placeholders and mocked credential stores; do not put
real credentials or tokens in fixtures or read the user's credentials for tests.

## Evolving the architecture

The current Public Contract is the loopback HTTP routes authenticated by
bearer token, using HTTP/SSE and no WebSocket routes. The tool definitions
live in `RuAgent.Protocol`; a mismatched Broker manifest digest refuses to
start. Counts and process/transport layout describe this version, not a limit
on future features.

Routes, tools and components may evolve in a scoped change. Update affected
schemas/versions/digests, clients, tests and docs together. Write
[an ADR](docs/adr/) when changing a lasting architectural decision, such as:

- Authentication, execution authority or permission lifetime.
- Public/private transport or process and data ownership.
- Canonical persistence technology or a foundational runtime dependency,
  including a bundled interpreter.

Explain guarantees, compatibility and the alternatives considered. Routine
package version updates, local schema/index refinements and internal refactors
that preserve these decisions do not need a new ADR; describe ordinary tradeoffs
in the commit or PR. Preserve authentication and execution-authority enforcement.
Editing guidance does not itself change runtime behavior.

## Git, review and documentation

- The long-lived branch is `dev`. Use a focused feature/fix/refactor branch when
  appropriate (`feat/<name>`, `fix/<name>`, `refactor/<name>`).
- Use [Conventional Commits](https://www.conventionalcommits.org/) with a
  meaningful scope and concise subject (target 72 characters). Explain
  non-obvious motivation or tradeoffs in the body. Mark breaking changes with
  `!` and describe them in CHANGELOG.
- Commit when requested or already authorized; stage only task files. Preserve
  unrelated dirty work. Never force-push, rewrite shared history or commit
  secrets.
- Before integration, record appropriate verification and resolve applicable
  CI failures. Describe the problem, resulting behavior and evidence in the PR.
- Review docs after each task and update affected claims: routes affect
  `public-route-contract.json`; domain changes affect CONTEXT.md; wiring affects
  architecture/ADRs; setup affects this guide. Update README counts when they
  change and CHANGELOG for user-visible behavior. Correct/remove stale guidance
  and repair incoming links when removing it. Routine edits need only the
  documents they actually affect.

## Reporting issues and getting help

Report vulnerabilities privately to maintainers so fixes can land before public
disclosure. Review relevant policy/path/network code and approval tests for
execution-authority changes. Start general questions from
[docs/README.md](docs/README.md) and the [decision register](docs/adr/).
When code and docs disagree, identify which is outdated and correct it with the
affected change.

Release packaging and update verification are documented in
[distribution and updates](docs/operations/distribution-and-updates.md).
