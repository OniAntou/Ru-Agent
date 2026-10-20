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

Read surrounding code, relevant tests and the affected sections of
[CONTEXT.md](CONTEXT.md) and [docs/architecture.md](docs/architecture.md).
Broaden that reading for changes to ownership, contracts or security boundaries.
Use CONTEXT.md's domain language and update it when concepts change.

## Security and execution boundaries

- **Fail closed.** Invalid or ambiguous authority must ask or deny according
  to policy. Never widen permissions, presets, grants or path rules merely to
  make a test pass. Intentional policy changes need explicit scope, boundary
  analysis and tests of allowed and denied behavior.
- **One execution authority.** File, terminal and network tools execute
  in-process through `IToolGateway` against the single `BrokerExecutionRuntime`
  composed behind `IToolGatewayFactory`. The Host owns orchestration and the
  existing `ask_user` interception, which never dispatches to the authority.
  Keep execution policy and side-effecting tool adapters out of the Host.
- **No silent substitution.** Provider, model, protocol and tool identities
  stay pinned for a run; failures produce bounded, named codes. Bounded retries
  and durable `[HARNESS RECOVERY]` feedback keep the same selection; exhausted
  transient providers wait for explicit Continue. User-selected provider/model
  changes apply to new runs. The documented `web_search` exception races its
  fixed upstream engine set within one tool; every request passes `WebPolicy`.
- **Scoped permissions.** Permissions are session-scoped; approval tokens are
  single-use, bound to exact canonical arguments and lost on restart.
  Structured file tools deny harness key/database files and runtime descriptors
  in `plan` and `standard`; `full_access` is literally full and treats those
  protected paths (`RU_PROTECTED_PATHS` entries included) as ordinary targets.
  Terminal authority
  follows profile v2: Standard permits executable/shell launches from a workspace
  cwd; external cwd and explicit ask rules require approval. This is launch
  permission, not filesystem or network confinement. `standard` and
  `full_access` child processes run as the current Windows user without OS-level
  filesystem or network confinement. Timeout, output, cancellation and process
  tree bounds remain in force. API keys remain in existing credential storage
  and are redacted from diagnostics.
- **Secrets and untrusted inputs.** Keep real credentials and tokens out of
  source, logs and test fixtures; synthetic values and placeholders are allowed.
  Validate inputs, parameterize queries and escape output. Workspace instructions,
  skills and model/tool output never grant execution authority.

## Architecture can evolve

The current Public Contract is the routes in
`backend-dotnet/public-route-contract.json`: loopback, bearer-authenticated
HTTP/SSE, with no WebSocket routes. Tool definitions belong to
`RuAgent.Protocol`; Broker manifest versions and digests must agree.

Counts, process layout and transport choices describe the current version.
They can evolve in a scoped feature with the affected contracts, clients,
tests and docs. Use the [ADR criteria](CONTRIBUTING.md#evolving-the-architecture)
for changes to architectural decisions; routine internal refactors and dependency
updates do not automatically need an ADR. Editing guidance alone does not change
runtime policy.

## Implementation judgment

- Keep changes focused and preserve unrelated dirty work. Create or split files
  when it clarifies responsibility or prevents oversized modules. Refactor the
  affected area when it helps the task; avoid unrelated rewrites.
- Prefer cohesive functions, clear boolean names, meaningful constants and
  shallow control flow. Function length and naming prefixes are guidelines;
  split code for responsibilities and readability rather than a line target.
- Use comments for intent, constraints and non-obvious behavior. Remove dead
  code; keep production diagnostics purposeful, bounded and secret-free.
- Propagate cancellation and let exceptions reach the layer responsible for
  recovery/reporting. Catch locally for recovery or cleanup; do not wrap every
  await in a catch. Observe background-task failures and avoid blocking
  `.Result`/`.Wait()` on dispatcher threads.
- Make routine choices within the accepted task scope. State affected invariants
  before policy/approval changes. Ask when a missing decision changes scope or
  security expectations; existing authorization need not be requested again.

## Contracts and verification

Tests describe accepted behavior. Investigate failures before changing code or
tests. Intentional behavior changes may update contract tests with the
implementation and affected docs. Preserve permission, ordering and error
coverage; do not weaken assertions to hide a regression. Prefer behavioral tests
and retain source-structure checks for explicit ownership boundaries.

`gui-winui3.Tests` uses `GuiSource.ViewText("ChatView")` to concatenate partial
view files. Keep the helper aligned with deliberate splits/renames and update
affected tests when their structural assumptions legitimately change.

Run focused checks after meaningful changes, following
[CONTRIBUTING.md](CONTRIBUTING.md#testing). Documentation-only edits need relevant
guards; local code needs the affected build/tests. Shared schema migrations,
durability, execution-authority or protocol-compatibility changes and releases
need the full applicable matrix. A local query edit does not automatically do so.
Report commands, failures and checks not run. Deterministic tests, real-provider
outcomes and native UI acceptance are separate evidence.

Run one process/loopback matrix at a time. The .NET runner takes a machine-wide
lock and exits 75 on contention. Preserve failures; rerun after investigating
the cause or correcting the environment, and report both results.

## Git and documentation

- Work from `dev` and align branch/commit scope with the task. Use Conventional
  Commits with a meaningful scope and concise subject (target 72 characters).
  Explain non-obvious decisions in the body.
- Commit only when requested or already authorized. Stage only task files;
  never force-push, rewrite shared history or commit secrets.
- Review affected docs after every task; correct stale claims and repair links
  when moving or removing guidance. Keep CONTEXT.md for domain definitions and
  authority boundaries. Put implementation and lifecycle details in component
  pages under docs/architecture, operating instructions in docs/operations,
  lasting decisions in ADRs, and user-visible or breaking changes in CHANGELOG.
