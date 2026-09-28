# Contributing to Ru Agent

Thank you for helping build Ru. This guide covers what you need, how the
repository is tested, and the rules that keep a security-sensitive local agent
trustworthy. For how the system works, read [README.md](README.md),
[CONTEXT.md](CONTEXT.md) (the domain language) and
[docs/architecture.md](docs/architecture.md).

Ru is a native Windows coding agent: a WinUI 3 desktop app, a terminal TUI, an
ASP.NET Core Public Host, a Runtime Worker, and a Broker — four processes, one
directory, nothing to install. The product is pure .NET 10. Python exists in
this repository for tooling and guards only, never as part of the product.

## Development environment

- **Windows 10 or 11, 64-bit**
- **.NET SDK 10.0 (x64)** — to build; published output is self-contained
- **Python 3.11–3.14** — repository tooling and documentation guards only
- **VS 2022 C++ Build Tools** — only if building native components from source

## Build and run

```powershell
.\build.bat          # Release, self-contained, flattened into one directory
.\start_dev.bat      # development launch
```

`build.bat` runs 13 stages and ends with a runnable folder:

```powershell
gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe
```

## Testing

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug   # 13 projects, ~4,675 tests
.venv\Scripts\python.exe -m pytest tests -q      # documentation and route guards
```

**Run one matrix at a time.** The .NET suite starts real processes and drives
real loopback HTTP hosts; a concurrent run (another matrix, a publish, a
running app) makes timing-sensitive tests fail for reasons unrelated to your
change. The script takes a machine-wide lock and refuses to start while it is
held (exit code 75). A failure is reported as a failure — there is no retry,
because a retry would hide a genuinely flaky test instead of getting it fixed.

The Python guards protect the documentation and contracts: README counts
(routes, tools, test projects), the live endpoint list versus
`public-route-contract.json` (42 routes), the tool manifest digests, and the
permission vocabulary. CI runs both matrices on `windows-latest`.

## How we work

- **Branches.** The repository has a single long-lived branch, `dev` (the
  default). Branch from it and name your branch `feat/<name>`, `fix/<name>`,
  `refactor/<name>`.
- **Commits.** [Conventional Commits](https://www.conventionalcommits.org/) with
  a detailed scope: `feat(broker):`, `fix(gui):`, `refactor(worker):`,
  `docs:`, `test(cli):`. First line ≤ 72 characters; use the body to explain
  *why*, not what the diff shows. Breaking changes are marked with `!`
  (`feat(providers)!: ...`) and documented in the CHANGELOG.
- **Never** force-push, rewrite shared history, or commit secrets. API keys
  belong in Windows Credential Manager through the app's Settings, never in
  files, logs, or tests.

## Pull requests

1. One concern per PR. Prefer small, focused changes over large rewrites.
2. Run the relevant test matrix **before** pushing and say what you ran.
3. Tests cover happy paths *and* error/edge cases; a bug fix comes with a
   regression test that fails on the old code.
4. Docs are part of the change. If you add, remove, or rename a route, a tool,
   a test project, or a user-visible behavior, the README, CONTEXT.md,
   `public-route-contract.json` and CHANGELOG must move with the code — the
   guards in `tests/` fail CI when they drift.
5. Update `CHANGELOG.md` (Keep a Changelog format) for every user-visible
   change.

## Architecture invariants (non-negotiable)

These rules are what make a local agent that executes terminal commands
trustworthy. A change that weakens one is rejected even if every test passes.

1. **Fail closed.** The policy evaluator asks or denies by default. Never
   widen a permission, preset, or grant to make a failing test pass — fix the
   test or the code that regressed.
2. **One execution authority.** Every model-visible tool call executes inside
   `RuAgentBroker`, reached through the `IToolGateway` seam. No in-process
   Host copy of tool semantics may exist.
3. **No fallbacks.** No provider, model, protocol, or tool fallback paths. A
   failure surfaces as a bounded, named error code.
4. **The protocol is versioned and digest-bound.** The 12 tool definitions
   live only in `RuAgent.Protocol`. A Broker manifest that disagrees with its
   Protocol digest refuses to start.
5. **Loopback and bearer only.** The Public Contract is the 42 loopback HTTP
   routes guarded by a bearer token. No new public transport; no WebSocket
   routes.
6. **Permissions are session-scoped.** Approval tokens are single-use and
   bound to the exact canonical arguments; no grant survives a restart.
   Protected paths deny at every preset.
7. **Skills are untrusted prompt guidance.** `allowed-tools` metadata never
   grants a tool, scope, path, network permission, or approval.
8. **Bounded everything.** Every tool output, request body, stream, and
   directory listing has a cap. A pathological input degrades into a bounded
   result, never a memory spike or an unbounded hang.

## Reporting security issues

Do not open a public issue for a vulnerability. This project's threat model is
a hostile prompt running with the user's own privileges: read
`ProtectedPathCatalog`, `WebPolicy`, `PolicySnapshotEvaluator` and the approval
tests before touching policy code, and report findings privately to the
maintainers so a fix can land before disclosure.

## Getting help

Start from [docs/README.md](docs/README.md) for the documentation index and
the decision register (`docs/adr/`). If the behavior you see contradicts the
documentation, that is a bug in one of them — file it either way.
