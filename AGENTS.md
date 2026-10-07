# AGENTS.md — guidance for AI coding agents working on Ru

This file instructs AI coding agents (and humans who prefer rules to vibes)
working inside this repository. It complements [CONTRIBUTING.md](CONTRIBUTING.md)
and does not repeat it.

## What this repository is

Ru is a native Windows coding agent: WinUI 3 desktop app (`gui-winui3`),
terminal TUI (`RuAgent.Cli`), ASP.NET Core Public Host (`RuBackend`), and
Broker. Pure .NET 10. Python under `tests/`, `scripts/`, `tools/`
is repository tooling only — never add Python, Node, or any interpreter to the
product.

## Read before editing

1. [CONTEXT.md](CONTEXT.md) — the domain language. Terms like *Public
   Contract*, *Execution Authority*, *Tool Manifest*, *Approval Token* have
   exact meanings; use them as written and update the file when you introduce
   or retire a concept.
2. [docs/architecture.md](docs/architecture.md) — the wiring and the
   invariants.
3. The tests are contracts, not suggestions. Many pin behavior deliberately
   (`tests/` guards documentation counts and route contracts;
   `gui-winui3.Tests` reads GUI sources and pins where decisions live). If a
   contract test fails, assume the code regressed — do not weaken the test to
   go green.

## Non-negotiable invariants

- **Fail closed.** Never widen permissions, presets, grants, or path rules to
  make a test pass.
- **One execution authority.** Model-visible tool calls execute only inside
  `RuAgentBroker` via the `IToolGateway` seam. Never add an in-process Host
  copy of tool semantics.
- **No fallbacks.** No provider, model, protocol, or tool *substitution*: a
  failure surfaces as a bounded, named code rather than being retried on a
  different backend. Bounded self-correction is not substitution: a recovery
  note is durable `[HARNESS RECOVERY]` feedback returned to the same model,
  provider, and tool, and exhaustion still ends at the original fail-closed
  code; an exhausted transient provider parks the run for an explicit Continue
  that retries the same provider instead of switching backends. One narrow
  exception is deliberate and documented — a single tool may race a fixed set
  of its own upstream endpoints (`web_search` over its configured engine plus
  three public HTML engines). That is one tool, one policy decision, one result
  contract: every request still passes `WebPolicy`, so it can never widen the
  network boundary or bypass a denial.
- **Loopback + bearer only.** The Public Contract is the 42 routes in
  `public-route-contract.json`. No new public transport, no WebSocket routes.
- **Permissions are session-scoped.** Approval tokens are single-use,
  argument-bound, and survive nothing. The harness self-protection paths
  (harness key/database files and runtime descriptors) deny at every preset;
  users can add more with `RU_PROTECTED_PATHS`.
- **Bounded everything.** New outputs, requests, and listings get a cap.

## Working rules

- **Minimal changes.** Edit existing files; do not create files without a
  clear need; do not rename or reformat code you are not changing.
- **Read the surrounding code first** and mimic its patterns, naming, and
  structure. Boolean names start with `is`/`has`/`should`; no magic numbers
  (use named constants); functions stay small (the repo target is well under
  50 lines); nesting stays shallow (early return over else-pyramids).
- **No comments by default.** Code explains itself; a comment explains *why*.
  No dead code, no commented-out code, no console/log noise in production
  paths.
- **Async discipline.** Every awaited path handles cancellation and
  exceptions; no unhandled rejections; no blocking `.Result`/`.Wait()` on
  threads that own a dispatcher.
- **Security.** No hardcoded secrets; validate inputs at boundaries; use
  parameterized queries; escape output.
- **Verify after every change.** Run the relevant matrix before claiming
  anything works:

```powershell
dotnet build backend-dotnet/RuBackend/RuBackend.csproj -c Debug     # fast compile check
.\scripts\test_dotnet.ps1 -Configuration Debug                      # full .NET matrix
.venv\Scripts\python.exe -m pytest tests -q                         # doc/route guards
```

  Run **one matrix at a time** — the suite takes a machine-wide lock and exits
  75 when another run holds it. Tests spawn real processes on real loopback
  ports; parallel runs produce false failures.

## Source-contract tests

`gui-winui3.Tests` contains tests that read the GUI sources (`GuiSource`)
and pin decisions to a class. Views are split into `partial` classes; the
helper `GuiSource.ViewText("ChatView")` concatenates every partial file, so
moving a member between partials keeps its contract as long as the class keeps
it. When renaming or re-splitting a view, update the helper, not the contract.

## Git and workflow

- Single long-lived branch: `dev` (the default). Feature branches: `feat/...`,
  `fix/...`, `refactor/...`.
- Conventional Commits with a scope (`fix(broker):`, `feat(gui):`, `docs:`);
  first line ≤ 72 characters; the body explains why. Breaking changes use `!`
  and a CHANGELOG entry.
- Commit only when asked or when your workflow requires it; never force-push;
  never commit secrets; stage only the files that belong to the change.
- Update `CHANGELOG.md` for user-visible changes, and keep docs in sync with
  code — outdated documentation is treated as a defect here.

## When uncertain

The domain language in CONTEXT.md decides naming and scope. If a requirement
is ambiguous or a change would touch policy/approval code, state the plan and
the invariants it touches before editing — then implement.
