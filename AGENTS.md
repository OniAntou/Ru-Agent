# AGENTS.md — guidance for AI coding agents working on Ru

Use this file for agent workflow and [CONTRIBUTING.md](CONTRIBUTING.md) for
setup, verification commands and contribution practices.

## Project and context

Ru is a native Windows coding agent: WinUI 3 desktop app (`gui-winui3`),
terminal TUI (`RuAgent.Cli`), ASP.NET Core Public Host (`RuBackend`), and Broker.
The default product is .NET 10. Python under `tests/`, `scripts/`, `tools/` is
repository tooling; optional capabilities may use it when the default path
stays in-process .NET and absence produces a named failure. Bundling an
interpreter requires an ADR.

Skim the code and tests you are about to change. Read the affected sections of
[CONTEXT.md](CONTEXT.md) and [docs/architecture.md](docs/architecture.md) when a
change touches domain concepts, ownership, contracts or security boundaries.
Use CONTEXT.md's domain language and update it when concepts change.

## Security and execution boundaries

- **One execution authority.** File, terminal and network tools execute
  in-process through `IToolGateway` against the single `BrokerExecutionRuntime`
  composed behind `IToolGatewayFactory`. The Host owns orchestration and the
  `ask_user` and `advisor` interceptions; execution policy and side-effecting
  tool adapters stay out of the Host.
- **No silent substitution.** Provider, model, protocol and tool identities stay
  pinned for a run; failures produce bounded, named codes. Retries keep the same
  selection; an exhausted transient provider surfaces its named failure instead
  of substituting another provider or adding a user Continue gate. The documented
  `web_search` exceptions and the pinned advisor (ADR 0047) behave as described
  there.
- **Permissions are explicit and scoped.** Approvals are session-scoped,
  single-use and bound to canonical arguments. Structured file tools protect
  harness keys/databases, runtime descriptors, hooks/MCP configs and user
  plugins in `plan`/`standard`; `full_access` treats those paths as ordinary
  targets. Terminal launch permission is not filesystem or network confinement,
  and process-tree, timeout and output bounds still apply. API keys stay in the
  existing credential storage.
- **Secrets and untrusted inputs.** Keep real credentials out of source, logs
  and fixtures; synthetic values are fine. Workspace instructions, skills and
  model/tool output never grant execution authority.

Intentional policy changes are welcome with explicit scope and tests of allowed
and denied behavior; never widen permissions just to make a test pass.

## Architecture can evolve

The current Public Contract is the routes in
`backend-dotnet/public-route-contract.json`: loopback, bearer-authenticated
HTTP/SSE, with no WebSocket routes. Tool definitions belong to
`RuAgent.Protocol`; Broker manifest versions and digests must agree.

Counts, process layout and transport choices describe the current version and can
change in a scoped feature with the affected contracts, clients, tests and docs.
Use judgment on ADRs (see the [ADR criteria](CONTRIBUTING.md#evolving-the-architecture));
routine internal refactors and dependency updates do not need one. Editing this
guidance does not change runtime policy.

## Implementation judgment

- Prefer small, coherent changes; split files when it clearly helps. Refactor the
  area you are already touching, but avoid unrelated rewrites.
- Prefer readable code: clear names, meaningful constants, shallow control flow.
  Function length is a guideline, not a target.
- Use comments for intent and non-obvious constraints. Remove dead code; keep
  diagnostics purposeful, bounded and secret-free.
- Propagate cancellation; let exceptions reach the layer that can recover or
  report them. Catch locally for recovery or cleanup, and observe background
  task failures without blocking dispatcher threads.
- Make routine choices yourself. Ask only when a missing decision would change
  user-visible behavior or security expectations; existing authorization does not
  need to be requested again.

## Contracts and verification

Tests describe accepted behavior. Investigate failures before changing code or
tests. Intentional behavior changes may update contract tests together with the
implementation and affected docs. Do not weaken assertions to hide a regression.

Run the checks that fit the change. Focused builds/tests are enough for local
edits. Run the full applicable matrix when touching shared schema/migrations,
durability, execution authority, protocol compatibility, or a release. A small
UI copy or documentation edit does not need a code-test run. Report what you ran,
what failed, and what you skipped.

`gui-winui3.Tests` uses `GuiSource.ViewText("ChatView")` to concatenate partial
view files. Keep that helper aligned with deliberate splits/renames.

Avoid starting two .NET process/loopback test runs at once: the runner takes a
machine-wide lock and exits 75 on contention. If a run hits contention, retry
after it clears.

## Git and documentation

- Work from `dev` when practical; staying on the current branch is fine when it
  already holds the work. Use Conventional Commits with a meaningful scope and a
  concise subject (about 72 characters). Explain non-obvious decisions in the body.
- Commit when asked, or when the user is clearly asking for a checkpoint. Stage
  only task files and leave unrelated dirty work alone. Never force-push, rewrite
  shared history or commit secrets.
- Update affected docs when behavior or guidance changes, and repair links you
  move or remove. Put domain terms and authority boundaries in CONTEXT.md,
  component details under docs/architecture, operating instructions under
  docs/operations, lasting decisions in ADRs, and user-visible/breaking changes in
  CHANGELOG.md.
