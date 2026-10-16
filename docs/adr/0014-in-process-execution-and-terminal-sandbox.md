---
status: partially superseded
date: 2026-10-10
superseded_by: ADR-0015 for terminal sandbox decision only
amended_by: ADR-0021 full access is literally full
  (`0021-full-access-is-literally-full.md`, accepted 2026-10-17), for the
  "protected paths stay denied in every preset" guarantee only.
---

# Run tool execution in-process and sandbox `standard` terminal children

> Historical decision record. ADR-0015 retires the terminal OS sandbox portion.
> The in-process execution authority and other guarantees below remain active.
>
> Amended by [ADR 0021](0021-full-access-is-literally-full.md) on 2026-10-17:
> the "protected paths stay denied in every preset" guarantee below is
> historical — the structured protected-path denial belongs to `plan` and
> `standard`, and `full_access` evaluates catalog-protected paths like any
> other target. Canonical path containment and the in-process authority remain
> in force.

[ADR-0009](0009-broker-execution-authority.md) chose one tool execution
authority behind the `IToolGateway` seam and enforced it with a separate
`RuAgentBroker` child process reached over an authenticated loopback WebSocket
lease. [ADR-0012](0012-in-process-model-runner.md) had already merged model
generation into the Host for the same reason: the boundary's only remaining
benefit was fault isolation, while its cost was a second process, a private wire
protocol, a descriptor, packaging and lifecycle plumbing. Two coupled changes
were approved on 2026-10-09: run the execution authority in the Host process,
and confine `standard` terminal children in a Windows AppContainer.

## Decision

**One execution authority, now in-process.** `BrokerExecutionRuntime` owns the
per-host state (session, tool execution service, pending approvals, approval
tokens, run cancellation registry) and exposes typed methods; the single
`IToolGateway` implementation (`InProcessToolGateway`, beside the wire gateway
it replaced) is created behind `IToolGatewayFactory`. One runtime exists per
Host process (DI singleton, composed lazily), one gateway per run lease. The
authenticated WebSocket transport, the broker descriptor, the child-process
plumbing and `RuAgentBroker.exe` are deleted; `RuAgent.Broker` remains as a
class library. The Host keeps orchestration and the `ask_user` interception and
still contains no execution policy or side-effecting tool adapters. The shared
`ToolGatewayConformance` suite and the ported runtime tests are the behavioral
contract.

**`standard` terminal children run in a Windows AppContainer.** Terminal and
managed-git children spawn through the same `SuspendedProcess` injection point
with a `SECURITY_CAPABILITIES` attribute carrying a derived AppContainer SID.
The accepted design created one profile per workspace + Agent session identity
(`ru-sandbox-<hash>`), because persisted ACLs reference its SID. Current
implementation clarification (2026-10-10): the sandbox manager caches one live
profile per identity, but every cache miss appends a fresh random suffix
(`ru-sandbox-<hash>-<nonce>`). A recreated session never reuses the prior
profile name, so orphaned ACLs from a crash or restart remain attached to an old
SID instead of reviving for the new session. Session close and grant revocation
cancel and drain terminal pumps before releasing that session's ACLs and
profile. The workspace root and a session-scoped sandbox temp directory get
`Modify`; user-profile toolchain
directories get `ReadAndExecute` (Program Files and Windows need none — the
AppContainer keeps `ALL APPLICATION PACKAGES` read access). Network is off
inside the sandbox unless the call carries an authorized `network_intent`, in
which case the child gets the `internetClient` capability for that exact
command. Session directory grants become ACL grants on the granted canonical
directories. A model request that cannot run confined (`sandbox: "off"`) asks
`terminal_sandbox_escape_requires_approval`; the ask is one-time only, is never
remembered as a session or project rule, and the evaluator gates it before any
ordered allow rule, so it always reaches an explicit user decision. A sandbox
setup failure is a bounded fail-closed `sandbox_setup_failed` result — never a
silent unsandboxed run.

`full_access` stays the explicit unsandboxed danger mode, and `plan` stays
read-only with no terminal; both are unchanged.

## Guarantees kept

- Fail-closed policy: invalid or ambiguous authority asks or denies; no fallback
  executes a call that was not authorized.
- Exact-intent, single-use approvals bound to the canonical arguments, lease and
  policy snapshot; protected paths stay denied in every preset.
- One execution path: every model-visible tool call goes through the same
  `IToolGateway` implementation and the same policy, approval and receipt code.
- The conformance suite locks the gateway behavior; runtime-level tests carry the
  ported wire coverage.

## Compatibility

The Public Contract is unchanged: 42 HTTP routes, no WebSocket route. The
published folder ships three executables (`RuApp.exe`, `RuBackend.exe`,
`RuAgent.Cli.exe`). `full_access` terminal remains as the current Windows user
with network access; `plan` is unchanged. Structured file tools remain
in-process against authorized handles with canonical path containment and
protected-path denial, so the OS sandbox applies to spawned children only.

## Consequences

- [ADR-0001](0001-aspnet-core-public-host.md)'s "owns the private
  RuAgentBroker child and its lifecycle" clause is amended: the Host owns the
  in-process execution authority instead.
- [ADR-0009](0009-broker-execution-authority.md) is superseded in part: its
  process-isolation rationale is retired while its single-execution-authority
  invariant remains in force.
- The broker descriptor (`broker.json`) and `RU_AGENT_BROKER_DESCRIPTOR` are
  gone; the Host's own `runtime_descriptor.json` for clients is unchanged.

## Considered options

- **Keep the child process (rejected).** The private lease was the only
  transport left after ADR-0012 and its fault isolation was its only remaining
  benefit. Keeping it means keeping two lifecycles, a descriptor, packaging and
  a wire contract for one behavior that one code path already enforces; the
  crash story for the in-process runner (durable state + restart recovery)
  applies the same way here.
- **In-process without a sandbox (rejected).** Removing the process boundary
  while leaving `standard` terminal children unconfined would weaken the
  default preset relative to mainstream coding agents, which confine
  workspace-write execution. The sandbox is the replacement for the isolation
  the process boundary used to provide for terminal work.
- **LPAC (rejected by the Task 0 spike).** A less-privileged AppContainer
  cannot run the default tooling: PowerShell 5.1 fails with `Cannot open
  registry key SOFTWARE\Microsoft\PowerShell. Access is denied.` (exit
  `0xFFFB0000`). A regular AppContainer with zero capabilities by default ran
  PowerShell 5.1, `git` and `dotnet`, denied outside writes, gated network by
  capability and kept Job Object tree kill (`spike-results.md`).
- **OS sandbox for structured file tools (not applicable).** Those tools run
  in-process against opened, authorized handles; there is no child process to
  confine. Canonical/symlink/junction/hard-link containment and protected-path
  denial remain their boundary.

## Trade-offs accepted and residuals

- **Fault isolation is traded for one execution path.** An adapter crash now
  fails the Host process. Durable run state with the existing restart recovery
  is the story, and the gateway keeps its bounded error boundaries.
- **Crash-orphan profiles.** A hard process crash can leave an AppContainer
  profile and its ACL grants behind. Recreation uses a fresh profile name and
  SID, so those stale ACEs are not reactivated; orphan cleanup remains deferred.
- **Non-Windows `standard` is unsandboxed.** The sandbox is Windows-only; the
  product is Windows-only, so this is a documented boundary rather than a
  supported second platform.
- AppContainer children cannot use the `Start-Process` ShellExecute path
  (`[Diagnostics.Process]::Start` works); the native spawn path is unaffected.
