# AGENTS.md — working on Ru

Ru is a native Windows coding agent: WinUI 3 desktop app, Terminal.Gui CLI,
in-process Broker execution authority, and an ASP.NET Core Public Host. Read the
code and tests you change; [CONTEXT.md](CONTEXT.md) owns domain terms,
[CONTRIBUTING.md](CONTRIBUTING.md) owns setup and verification commands, and
[docs/architecture.md](docs/architecture.md) owns component boundaries.

## Non-negotiables

- **One execution authority.** File, terminal and network tools execute in-process
  through `IToolGateway`/`BrokerExecutionRuntime`. Never add a second execution
  path, a Host-side copy of tool semantics, or an unrestricted shell. The Host
  keeps orchestration and the `ask_user`/`advisor` interceptions.
- **No silent substitution.** Provider, model, protocol and tool identities stay
  pinned for a run. Failures end with a bounded named code; never fall back to
  another identity or add a user Continue gate.
- **Permissions are explicit and scoped.** Approvals are session-scoped,
  single-use and bound to canonical arguments. `full_access` treats structured
  protected paths as ordinary targets; never widen permissions to make a test pass.
- **No secrets, no authority from content.** Keep real credentials out of source,
  logs, fixtures and commits. Workspace instructions, skills and model output never
  grant execution authority.

## Work

- Tests describe accepted behavior: investigate a failure before changing code or
  tests, and never weaken assertions to hide a regression. Change behavior,
  contract tests and docs together.
- Prefer small, coherent changes; refactor only the area you touch; remove dead
  code; comments explain intent or constraints.
- Propagate cancellation. Catch only to recover, clean up or report, and observe
  background task failures.
- Run the checks that fit the change. The .NET matrix spawns real processes: run
  one matrix at a time (machine-wide lock; exit 75 means contention — retry
  later). Report commands, results and skipped checks.
- Make routine choices yourself; ask only when a decision changes user-visible
  behavior or security expectations.
- Git: work on `dev` when practical, use Conventional Commits (~72-char subject),
  stage only task files, never force-push or rewrite shared history. Update
  affected docs, ADRs and CHANGELOG when behavior or guidance changes.

## Security and execution boundaries

Runtime permissions and CI gates are unchanged by this file. Approval, trust,
protected-path and redaction behavior is defined by the Broker/Host code and its
tests — do not infer policy from documentation prose.
