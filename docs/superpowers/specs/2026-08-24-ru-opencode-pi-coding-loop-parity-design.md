# Ru OpenCode/Pi Coding-Loop Parity Design

**Status:** Approved design; not implemented
**Date:** 2026-08-24
**Scope:** Give Ru Agent an OpenCode/Pi-class coding loop through one headless
runtime shared by Ru Desktop and a thin CLI. This milestone covers workspace
files, terminal execution, tests/builds, local Git, granular permissions,
cancellation, reconnect, and a durable event trail.

## Outcome

Ru Agent becomes a real coding harness instead of a collection of model-visible
desktop tools. A single .NET orchestration core owns each Agent run, asks the
Python Worker for one model step at a time, and sends approved tool calls to the
private Agent Broker. Ru Desktop and a new CLI are clients of the same public
Harness API and therefore observe the same run, approval, output, cancellation,
and completion state.

The target for this milestone is coding-loop parity, not overall feature parity.
Session trees and compaction follow in milestone 2. Extensions, custom tools,
skills, sub-agents, MCP, LSP, and web tools follow in milestone 3. An
OS-enforced filesystem/network sandbox remains a separate future security
project.

This is a parity-first design. Agent commands run with the permissions of the
current Windows user, like the selected comparison baseline. Permission rules,
working-directory checks, command analyzers, and request flags are policy and
user-experience controls; they are not an operating-system sandbox.

## Goals

- Let Agent inspect, search, create, edit, patch, and delete workspace files.
- Let Agent run PowerShell commands, tests, linters, builds, and local Git.
- Give Standard mode useful autonomy through deterministic `allow`, `ask`, and
  `deny` rules rather than requiring approval for every tool call.
- Make Full Access a permission preset on the same Agent runtime, not a second
  implementation.
- Preserve unrelated dirty work and make concurrent file changes fail instead
  of being overwritten.
- Stream model and process activity to Desktop and CLI, support explicit
  cancellation, and allow clients to reconnect without losing durable state.
- Keep raw commands, diffs, tool output, and approval data out of visible chat,
  memory, TTS, VTS metadata, and diagnostic logs.
- Keep the domain core independent of ASP.NET Core, WinUI, PowerShell, and
  concrete Worker/Broker transports.
- Reach the outcome through vertical slices and remove the old Python-owned
  Agent loop after verified cutover.

## Non-goals

- Claiming filesystem or network isolation.
- Automatic push, pull-request creation, account access, or other external
  mutations.
- Interactive PTY sessions, background daemons, or password entry through a
  terminal tool.
- Cross-platform runtime support in this milestone; the supported execution
  host is Windows.
- Automatic resume of a command that was active during a crash.
- Session fork/tree navigation, context compaction, extensions, custom tools,
  skills, sub-agents, MCP, LSP, or web access.
- Replacing ordinary Chat history, structured memory, voice, or VTS ownership.
- Changing live microphone, speaker, volume, desktop, or VTube Studio state as
  part of deterministic acceptance.

## Approved product decisions

### Parity trust boundary

Ru adopts the local-user trust boundary for coding-loop parity. Standard rules
reduce accidental or unreviewed actions, and Full Access deliberately removes
most prompts, but both execute under the current Windows account. The UI and
documentation must state this directly.

### Mode model

The product exposes Chat and Agent capabilities. Agent is the coding harness.
Standard and Full Access are permission presets applied to Agent; they do not
select different loops, transports, or tool implementations.

Legacy inputs that name the `full` capability profile are translated during
migration to Agent plus the Full Access preset. The legacy wire value is
deprecated and removed only after Desktop, API, and stored-setting compatibility
tests pass.

### Shared headless runtime

Ru Desktop is the primary visual client, but it is not the runtime owner. A thin
CLI can start or attach to the same headless runtime when Desktop is closed. The
CLI never hosts a model loop, policy engine, or tool adapter.

### Default autonomy

Standard automatically permits ordinary workspace reads/writes and recognized
read, test, build, and lint operations. It asks before delete, Git mutation,
declared network use, access outside the workspace, protected files, or unknown
commands. Full Access permits every well-formed Harness tool call that the
Windows user can perform while retaining protocol, credential, timeout, output,
and cancellation invariants.

## Current architecture and gap

Ru already has useful foundations:

- versioned tool manifests and schemas;
- a private bearer-authenticated Broker;
- path guards, approval binding, one-time approval tokens, and receipt checks;
- process-tree cancellation support;
- a Python model/tool loop with a bounded call count;
- ASP.NET Core session/history ownership and typed Worker/Broker protocols;
- Desktop approval and Agent activity UI seams.

The current design is not yet a general coding harness. Policy is largely static
per manifest. Agent lacks terminal capability, while Full Access requires a new
approval for every terminal request. The Python Worker owns the multi-step tool
loop and waits for receipts, so session, permission, and activity state are
split across Worker, Public Host, Broker, and WinUI. Broker sessions and pending
approvals are memory-only. WinUI also owns the Worker/Broker/Public Host process
topology, preventing a true headless CLI.

## Target architecture

```text
Ru Desktop ----\
                +--> ASP.NET Harness API --> RuAgent.Harness.Core
Ru CLI --------/                              |--> Python Worker
                                               |--> Agent Broker
                                               `--> Harness Event Store
```

### `RuAgent.Harness.Core`

Create a .NET class library with no ASP.NET, WinUI, process-launch, shell, or
database-provider dependency. It owns:

- the Agent run and model-step state machines;
- tool-call scheduling and budgets;
- context assembly and normalized model results;
- the permission interaction workflow;
- cancellation propagation;
- durable-event intent through `IHarnessEventStore`;
- ports such as `IModelStepRunner`, `IToolGateway`, `IHarnessEventStore`,
  `ITimeProvider`, and `IIdGenerator`.

Core asks the Broker for the authoritative policy decision. It publishes an
approval event and waits when the Broker returns `ask`; it does not duplicate
Broker policy evaluation.

### Python Worker

The Worker becomes a model adapter for Agent v2. One request performs one model
step and returns a provider-neutral result:

```text
AssistantStep {
  text?
  tool_calls[]
  finish_reason
}
```

The Worker may normalize native provider tool calls or the existing structured
selection format into this result. It does not execute tools, wait for Broker
receipts, request user approval, persist Agent events, or own the outer Agent
loop. Ordinary Chat remains on its current path during migration.

### Agent Broker

The Broker remains the final execution authority. It owns:

- the canonical tool and binding registry;
- input schema validation;
- canonical path, executable, argument, and working-directory resolution;
- authoritative policy evaluation;
- approval token issuance and consumption;
- tool adapter invocation and process-tree ownership;
- bounded, typed receipts.

Desktop and CLI never connect to the Worker or Broker directly. The Broker does
not own conversation history, model context, UI state, or the public event log.

### ASP.NET runtime host

ASP.NET Core hosts Harness Core, the canonical Harness store, and the public
Harness API. It is the only writer to Harness persistence. It also becomes the
owner of the Python Worker and Broker child processes so that the runtime is not
coupled to WinUI.

The existing launch-plan, child-environment, Job Object, descriptor, cleanup,
and process-diagnostic logic moves out of the WinUI project into a shared
hosting layer used by the Public Host bootstrap. It must not be copied into the
CLI.

## Run and event model

### Identities

The model uses stable identifiers:

- `workspace_id`: identity for one trusted canonical workspace root;
- `session_id`: existing Ru conversation session;
- `run_id`: one user request from creation to a terminal run state;
- `model_step_id`: one Worker generation request;
- `tool_call_id`: one model-proposed tool call;
- `execution_id`: one dispatch attempt bound to an immutable canonical intent;
- `approval_id`: one pending approval;
- `event_id` and per-run `sequence`: durable ordering and reconnect cursors.

One session may have many historical runs but at most one active run. One
workspace holds at most one mutation-capable run lease. A second run receives
`workspace_busy` rather than competing for the same files.

### Run states

```text
created
  -> running
  -> waiting_approval
  -> waiting_user
  -> reconciliation_required
  -> completed | failed | cancelled | interrupted
```

`waiting_approval` does not consume model-step or tool-call budget.
`waiting_user` is used when the run reaches its budget and can continue only
after an explicit Continue request. `reconciliation_required` means a side
effect may have completed without a durable receipt; the runtime must inspect
state before any continuation.

### Tool-call states

```text
proposed
  -> allowed | awaiting_approval | denied
  -> executing
  -> succeeded | failed | timed_out | cancelled | outcome_unknown
```

Every transition is emitted as a typed durable event. Live assistant and
terminal deltas are transient stream frames; a normalized final model message
and final tool result are durable.

### Idempotency

`POST /agent/v1/runs` requires an idempotency key. Repeating the same key returns
the existing run. An `execution_id` is immutable and bound to the canonical
tool intent. Broker deduplication prevents a duplicated transport request from
running twice during the Broker lifetime.

The design does not claim atomic exactly-once behavior across SQLite and an
arbitrary external process. Crash ambiguity is handled explicitly by
`reconciliation_required`.

## Persistence and private payloads

### Store

Use a separate SQLite database named `agent_harness.db` under the canonical Ru
business-data root. Enable WAL. Do not reuse the structured-memory database or
replace the existing Chat session store.

At minimum the database contains:

- `agent_runs`: query projection for current and terminal run state;
- `agent_events`: append-only safe event envelopes with a unique
  `(run_id, sequence)` constraint;
- `agent_payloads`: encrypted bounded payloads addressed by opaque IDs;
- `agent_project_rules`: user-approved rules keyed by `workspace_id`;
- `agent_workspaces`: canonical root identity and trust state.

The event append and run-projection update occur in one transaction. Deleting a
Ru session deletes its Agent runs, events, rules scoped only to that session,
and encrypted payloads. Project rules remain until removed from Agent settings.

### Sensitive content

Safe event envelopes may contain IDs, event type, status, duration, byte counts,
truncation state, stable error code, and bounded safe summary. They do not
contain commands, arguments, code, diffs, stdout, stderr, environment values,
provider bodies, credentials, or raw model-control metadata.

Payloads needed by the model or authenticated UI are encrypted with AES-GCM.
The local AES key is randomly generated and protected with Windows DPAPI
CurrentUser scope; key and database files receive current-user ACLs. Payload
size is bounded by the corresponding versioned tool manifest. Secret-bearing
environment variables, bearer tokens, and credential values are never persisted
even in encrypted payloads.

Ordinary Chat history receives only the user message and the final visible
assistant answer. Agent events and payloads never enter memory extraction, TTS,
VTS metadata, reviewed examples, or diagnostic logs.

## Permission model

### Canonical policy context

Before evaluation, Broker creates a `PolicyContext` from trusted resolution:

```text
tool identity
canonical workspace and final target paths
resolved executable and argv
canonical cwd
operation class
declared or recognized network intent
```

The model cannot label its own call as safe. File and working-directory paths
are re-resolved at execution time so a changed junction or symlink does not
reuse an earlier decision silently.

### Rule evaluation

Non-overridable protocol invariants run first: schema validity, request/session
binding, one-time token consumption, argument-intent hash, credential handling,
timeout, output bounds, and cancellation support.

The effective ordered rule list is then composed from the selected preset,
persistent project rules, run-scoped rules, and the current one-call decision.
The last matching rule wins. A rule can match tool ID, canonical relative or
absolute path pattern, resolved executable, argv pattern, cwd pattern, operation
class, and network intent. Unknown or invalid patterns fail closed.

Project rules live in `agent_harness.db`, not in the repository, so Agent file
tools cannot grant themselves more authority. Global policy changes are exposed
only through dedicated settings/CLI management, not through the ordinary
approval card.

### Standard preset

Standard uses these fallback classes:

- `allow`: ordinary reads, search, create, and edit inside the trusted
  workspace;
- `allow`: recognized `rg`, Git read-only, test, build, and lint forms;
- `ask`: delete, Git mutation and commit, declared/recognized network intent,
  paths outside the workspace, protected files, and unknown executable forms;
- `ask`: every `terminal.shell` call without an exact reusable rule;
- `deny`: malformed calls and operations blocked by protocol invariants.

Command analyzers are small versioned implementations per executable family,
including Git, .NET, Python/pytest, and ripgrep. There is no general regex that
purports to understand arbitrary shell scripts. Test and build commands are
allowed under the selected local-user trust model even though project code may
perform arbitrary host or network operations.

Protected paths include Agent policy data, Ru credential data, `.git/**`, and
credential-like workspace files such as `.env*`. The exact protected-path
catalog is versioned and covered by contract tests.

### Full Access preset

Full Access changes the policy fallback to `allow` for all well-formed Harness
tools, including terminal, network-declared, destructive, and out-of-workspace
operations. It remains subject to Windows account permissions and the
non-overridable protocol invariants. Selecting it requires an explicit warning
and applies to the Agent session rather than installing a separate runtime.

### Approval choices

The approval surface offers:

- Allow once;
- Allow for this run;
- Always for this project;
- Deny.

An approval binds `run_id`, `tool_id`, tool version, canonical intent hash, and
expiry. It is consumed once. Any change to path, command, argument, cwd, or
operation produces a new hash and a new decision. Approvals wait for at most 30
minutes and do not consume Agent budgets; after expiry the call must be
re-evaluated and presented again.

### Truthful network limitation

Without an OS network boundary, an allowed compiler, test, Python process, or
arbitrary executable can open sockets regardless of a request's declared
network field. `network_intent` improves prompting and audit only. No UI,
documentation, test, or error copy may describe it as network isolation.

## Coding tool contract

Retain versioned `files.*` and `terminal.*` namespaces and add only the missing
coding operations:

- `files.list`
- `files.glob`
- `files.search`
- `files.read` with offset and limit
- `files.write`
- `files.edit`
- `files.apply_patch`
- `files.delete`
- `terminal.run`
- `terminal.shell`

Local Git uses `terminal.run` and the Git command analyzer. Separate Git tools
are not added in milestone 1 because they would duplicate terminal capability
and policy behavior.

### File semantics

- A run captures initial Git status and hashes of files it observes or changes.
- Every mutation uses an expected preimage hash. A mismatch returns
  `workspace_changed` without writing.
- Multi-file patch validates every target before the first replacement and uses
  a bounded recovery journal for replacements already staged.
- Writes use same-volume temporary files and atomic replacement where Windows
  permits it.
- Delete places a recoverable encrypted copy in the run payload store before
  removing the workspace file.
- Every successful mutation returns before/after hashes and a bounded diff
  summary.
- A final run change set contains only paths attributed to that run.

The Agent never performs an implicit reset, clean, force checkout, or whole-tree
stage. A commit stages only the run-attributed path set, shows the exact staged
diff summary, and requires approval. Unrelated dirty files are neither reset nor
included. Push and PR operations are outside this milestone.

### Terminal semantics

`terminal.run` accepts a resolved executable, an argv list, a working directory,
and bounded timeout. `terminal.shell` accepts an explicit PowerShell script and
is the escape hatch for commands that cannot be represented structurally. A
command starts with a clean, least-privilege inherited environment assembled by
the existing child-environment policy; Agent does not receive Ru provider or
service secrets.

Each call owns an independent process tree. There is no hidden persistent shell
state. Stdout and stderr stream separately with monotonic frame order. The final
receipt contains exit code, duration, canonical cwd, truncation metadata, and a
bounded head/tail result. The runner drains output after the retention cap so a
noisy child cannot deadlock or allocate unbounded memory.

The default timeout is 120 seconds. Standard may request up to 15 minutes.
Longer execution, up to a hard 30-minute maximum, requires an exact approval.
Cancellation and timeout close the Windows Job Object and therefore terminate
child and grandchild processes.

Background daemons, interactive PTYs, and terminal password entry are rejected
in milestone 1.

### Tool scheduling and budgets

Core accepts multiple tool calls in an `AssistantStep`. Independent read-only
calls may run in parallel. File mutations, Git mutations, and terminal calls are
serialized in model-declared order.

A run defaults to 32 model steps, 64 tool calls, and the approved per-command
timeouts. Reaching a step or call budget changes the run to `waiting_user` and
publishes Continue; it never fabricates completion. Continue starts a new
budget window in the same run and is itself durable.

Typed tool failures are returned to the model so it can repair a command or
patch. Invalid schemas, mismatched identities, stale receipts, and protocol
violations fail closed.

## Runtime ownership and public API

### Start or attach

The Public Host is the durable process owner. It obtains a named single-instance
mutex, writes an ACL-restricted runtime descriptor under LocalAppData, creates
private Worker/Broker credentials, and starts Worker and Broker in one Windows
Job Object. Desktop and CLI validate the descriptor and authenticated health
before attaching. A stale descriptor is never trusted solely because its file
exists.

When no active run, approval, or client lease remains, the host shuts down after
a default ten-minute idle period. Explicit stop and crash cleanup terminate the
owned process tree and delete ephemeral descriptors/tokens. Durable Harness
state remains in the business-data database.

### API v1

The authenticated loopback Public Host exposes:

- `POST /agent/v1/runs`
- `GET /agent/v1/runs/{runId}`
- `GET /agent/v1/runs/{runId}/events`
- `GET /agent/v1/runs/{runId}/payloads/{payloadId}`
- `POST /agent/v1/runs/{runId}/approvals/{approvalId}`
- `POST /agent/v1/runs/{runId}/cancel`
- `POST /agent/v1/runs/{runId}/continue`
- `GET /agent/v1/sessions/{sessionId}/runs`

Run creation includes `session_id`, canonical workspace request, prompt, Agent
permission preset, and idempotency key. An untrusted workspace creates a
waiting trust decision rather than executing a model or tool.

The events endpoint uses Server-Sent Events. Durable events carry SSE IDs equal
to the per-run sequence, and reconnect accepts `Last-Event-ID` or
`after_sequence`. Transient assistant/process deltas are explicitly marked and
do not advance the durable cursor. Approval, cancel, Continue, and payload reads
use ordinary authenticated HTTP.

The safe event envelope includes protocol version, event ID, run/session IDs,
sequence, event type, occurrence time, status, safe summary, and optional opaque
payload reference. Only the payload endpoint decrypts private content for an
authenticated client bound to the same run.

The canonical public route matrix must include every new endpoint and preserve
the existing Chat, voice, VTS, settings, and session routes.

## Desktop behavior

- The mode surface exposes Chat and Agent.
- Agent requires a workspace and shows Standard or Full Access explicitly.
- First use of a workspace explains that tests and code run with current Windows
  privileges and asks for workspace trust.
- Model activity, file changes, terminal output, errors, and approvals render as
  separate Agent cards, not assistant chat bubbles.
- Approval previews retrieve private content only on demand.
- Stop cancels the Worker request and active Broker process tree.
- Reopening or switching back to a session attaches after the last durable
  sequence instead of creating a duplicate run.
- Only the final visible assistant answer enters Chat history and optional TTS.
- Coding-run creation and approval are typed/UI actions in milestone 1;
  hands-free voice cannot approve terminal or Git mutations.

## CLI behavior

Create `RuAgent.Cli` as a thin .NET client with these milestone commands:

```text
ru agent run --workspace D:\LLM "fix the bug and run tests"
ru agent attach <run-id>
ru agent approve <run-id> <approval-id> --scope once|run|project
ru agent deny <run-id> <approval-id>
ru agent cancel <run-id>
ru agent status <run-id>
```

Interactive mode renders safe activity, fetches selected payload previews, and
may prompt for approval. `--json` emits a versioned NDJSON event stream and
never prompts or implicitly approves, making it suitable for automation. The
CLI starts or attaches to the Public Host but never launches a separate Worker
or Broker topology of its own.

Desktop and CLI may attach concurrently. The first valid approval response is
accepted; later responses receive `approval_stale` and cannot trigger a second
execution.

## Failure, retry, and recovery

### Dispatch protocol

Before an external side effect, Core persists `tool.execution_requested` with
the immutable `execution_id`. Broker then executes and returns a receipt. Core
persists the receipt before asking the model for another step.

Read-only transport failures may retry at most twice with the same execution
identity. A mutation is never retried automatically after dispatch.

If the event store or Broker is unavailable before dispatch, execution fails
closed. If a crash happens after dispatch but before receipt persistence:

- deterministic file operations compare before/after hashes;
- an arbitrary terminal or Git outcome becomes `reconciliation_required`;
- neither startup nor Continue repeats the command automatically.

### Cancellation and ambiguous effects

Cancel and timeout propagate to Worker and Broker. Broker terminates the full
process tree. A race in which the side effect may already have occurred records
`cancelled_side_effects_possible`; the next step must inspect workspace state.

Worker failure before dispatch may restart the model step. Broker failure during
a mutation makes the run require reconciliation. Client disconnect leaves the
headless run active. A file changed outside Agent returns `workspace_changed`.

An Agent database failure disables Agent endpoints and surfaces a stable health
error but does not make ordinary Chat, history, memory, voice, or VTS ownership
depend on the failing Agent store.

### Diagnostics

Technical diagnostics contain stable event/error codes, IDs, duration, status,
byte counts, and truncation flags only. Commands, arguments, paths not already
approved for safe display, code, diffs, stdout/stderr, provider responses, and
credentials remain in the private payload boundary and never enter diagnostic
JSONL.

## Migration strategy

Implementation proceeds as vertical slices:

1. Add the Core state machine and SQLite store using fake Worker/Broker ports.
2. Add the one-step Worker v2 contract and a read-only end-to-end tracer run.
3. Move runtime ownership below WinUI and add CLI run/attach/cancel.
4. Add rule evaluation, workspace trust, and file edit/patch/delete.
5. Add terminal streaming, test/build analysis, and process-tree cancellation.
6. Add local Git mutation/commit approval and dirty-worktree protection.
7. Move Desktop Agent to the same API and event stream.
8. Translate legacy Full inputs, make Agent v2 the default, then remove the old
   Python-owned Agent loop and obsolete profile branches.

A temporary feature flag routes an entire Agent run to v1 or v2. Two writable
loops never process the same run. Chat remains on the current path until a
separate approved migration changes it. Existing deterministic volume
selection, verification, and private outcome contracts remain regression gates
while Agent moves to v2.

No old in-memory Agent run is migrated. Existing visible Chat sessions and
messages remain intact. New durable Agent runs attach to the existing
`session_id` without changing the Chat session storage format.

## Verification strategy

### Unit and property tests

- state transitions, terminal states, Continue budgets, and invalid transitions;
- idempotency keys, event sequence uniqueness, and projection transactions;
- ordered permission-rule precedence and invalid-pattern fail-closed behavior;
- Git/.NET/Python/ripgrep command analyzers;
- approval intent binding, expiry, replay rejection, and argument changes;
- encrypted payload round trips and absence of secrets from safe envelopes;
- output head/tail truncation and bounded draining;
- workspace path, junction/symlink, and protected-path handling;
- file preimage hashes, atomic replacement, patch preflight, and recovery copy.

### Contract and integration tests

- provider-neutral Worker `AssistantStep` compatibility;
- Public Host to Broker policy/approval/receipt contracts;
- SSE reconnect from every durable sequence;
- duplicate run creation and duplicate approval responses;
- backend crash boundaries before dispatch, after dispatch, and before receipt;
- temp Git repositories with unrelated staged and unstaged changes;
- branch and commit approval staging only run-attributed paths;
- real Windows cancellation that proves child and grandchild termination;
- Desktop and CLI attaching to the same run;
- runtime single-instance, stale descriptor, bearer rejection, and owned-child
  cleanup.

### Full deterministic matrix

- Python 3.11 and 3.13: Ruff, Mypy, and pytest;
- Broker, Connector, Backend, and WinUI .NET suites;
- canonical ASP.NET route-matrix verification;
- Release publish/build plus native overlay tests;
- deterministic backend and GUI Harness runs.

Heavyweight `D:\LLM` suites run sequentially. Deterministic Harness proof is
kept separate from live audio, desktop, microphone, speaker, volume, and VTube
Studio acceptance.

### Real-model acceptance

Before claiming coding-loop parity, run Ru against a disposable Git repository
containing a seeded bug and unrelated dirty work. From both CLI and Desktop,
observe Ru inspect the repository, edit only intended files, run the relevant
test/build command, respond correctly to Standard approvals, preserve unrelated
changes, and produce a correct final answer. A separate approved run verifies
local commit staging. Automated tests alone do not satisfy this gate.

## Coding-loop parity gate

Milestone 1 may be called **OpenCode/Pi coding-loop parity** only when all of the
following are shipped and accepted:

- one headless runtime shared by Desktop and CLI;
- file list/glob/search/read/write/edit/patch/delete;
- terminal, tests/builds/linters, and local Git;
- multi-step and multi-tool orchestration;
- Standard `allow/ask/deny`, reusable project rules, and Full Access;
- live output, cancellation, durable events, attach, and reconnect;
- dirty-worktree preservation and mutation reconciliation;
- truthful local-user trust and non-sandbox documentation;
- real-model acceptance in a disposable repository.

The project must not claim overall OpenCode/Pi harness parity at this gate.
Overall parity additionally requires the approved future session and extension
milestones.

## Documentation changes required with implementation

- Add a new ADR that supersedes ADR 0007 and separates Agent capability from
  permission presets.
- Update `CONTEXT.md` domain terms for Harness Core, Agent Run, Model Step,
  Policy Context, permission preset, and workspace trust.
- Update `docs/architecture.md`, `docs/operations/agent-tools.md`, the ASP.NET
  route matrix, root/backend/CLI READMEs, and canonical route data.
- Mark or remove any current operational document that still says Agent lacks
  terminal access or Full Access is a separate capability implementation.
- Keep dated historical specs/plans only as decision history; they must not be
  linked as current operating behavior.
- Retain the future OS-enforced filesystem/network sandbox as a separate TODO;
  do not relabel policy controls as delivery of that project.

## Future milestones

### Milestone 2: session parity

- resume completed/interrupted context where safe;
- fork and session-tree navigation;
- context compaction with durable provenance;
- named/custom agent configurations built on the same Core state machine.

### Milestone 3: extensibility parity

- stable custom-tool/extension API;
- enabled declarative skills;
- sub-agent orchestration and bounded delegation;
- MCP, LSP, web, and provider extension seams;
- extension permission and trust contracts.

### Separate security project

Evaluate a Windows OS-enforced process-tree boundary with explicit filesystem
roots, denied-by-default network, inherited child restrictions, and adversarial
junction/symlink plus real-socket acceptance. This project is intentionally not
hidden inside the coding-loop parity milestone.
