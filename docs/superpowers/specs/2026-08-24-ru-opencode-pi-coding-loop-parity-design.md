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
- First-class push, pull-request, account, connector, or other external-mutation
  workflows. Full Access shell can still invoke installed network clients with
  the Windows user's authority; preventing that requires the separate OS
  sandbox and is not claimed here.
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

The selected preset belongs to the Agent session and is snapshotted immutably
when a run starts. Changing the session preset affects the next run, never a
tool call already proposed or executing. Full Access is not sticky across a new
session and is never made the global default implicitly.

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

The Public Host loads the selected preset and encrypted project/run rules from
Harness persistence, builds an immutable versioned `PolicySnapshot`, and sends
that snapshot when opening the Broker run lease. Broker validates and binds the
snapshot hash to the lease before advertising capabilities. A newly approved
run/project rule produces a new snapshot version; replacing the bound snapshot
invalidates pending decisions created under the old version.

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
- typed output-delta streaming correlated to an execution ID;
- bounded, typed receipts.

The canonical workspace root becomes part of the versioned run lease. Broker
constructs its path guard from that lease and does not infer the production
workspace once at process startup from `RU_WORKSPACE_ROOT` or the current
directory. A test-only explicit root override may remain for isolated Broker
tests.

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

Client-side discovery and starting of the Public Host live in a second small
shared client library used by Desktop and CLI. The Public Host hosting layer
owns only Worker/Broker children; it does not attempt to launch another Public
Host instance.

## Run and event model

### Identities

The model uses stable identifiers:

- `workspace_id`: host-generated identity for one trusted canonical workspace
  root, bound to its normalized path and Windows volume/root file identity;
- `session_id`: existing Ru conversation session;
- `run_id`: one user request from creation to a terminal run state;
- `model_step_id`: one Worker generation request;
- `tool_call_id`: one model-proposed tool call;
- `execution_id`: one dispatch attempt bound to an immutable canonical intent;
- `approval_id`: one pending approval;
- `event_id` and per-run `sequence`: durable ordering and reconnect cursors.

One session may have many historical runs but at most one active run. One
workspace holds at most one active Agent run because a read-only run may propose
a mutation later. A second run receives `workspace_busy` rather than competing
for the same files. Moving a workspace or changing its bound volume/root
identity requires trust again; no marker controlled by the repository defines
workspace trust.

### Run states

```text
created
  -> waiting_workspace_trust
  -> waiting_history
  -> running
  -> waiting_approval
  -> waiting_user
  -> reconciliation_required
  -> completed | failed | cancelled | interrupted
```

The legal non-terminal transitions are explicit:

- `created -> waiting_workspace_trust -> waiting_history -> running` after
  trust and visible-user-message delivery;
- `created -> waiting_history -> running` for an already trusted workspace;
- `running -> waiting_history -> completed` when the final visible answer is
  delivered idempotently to the session store;
- `running -> waiting_approval -> running` after approve, deny, or typed expiry
  delivery to the model;
- `running -> waiting_user -> running` only after an explicit Continue;
- `running -> reconciliation_required -> running` only after an explicit
  reconcile response records `applied`, `not_applied`, or
  `unknown_continue`; the original execution is never dispatched again;
- any non-terminal state may become `cancelled`, while unrecoverable protocol,
  storage, or child-process failure becomes `failed` or `interrupted` according
  to whether an external effect may be outstanding.

`completed`, `failed`, `cancelled`, and `interrupted` are terminal. An approval
or Continue request against a terminal or stale state has no effect.
`waiting_approval` does not consume model-step or tool-call budget.
`waiting_user` is used when the run reaches its budget. In
`reconciliation_required`, the UI/CLI shows the last known intent and a new
workspace/Git snapshot before accepting a reconcile response. An
`unknown_continue` response feeds a typed `outcome_unknown` result to the model
and requires its next tool call to be read-only inspection; Broker rejects a
mutation until at least one such inspection receipt is durable.

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
- `agent_conversation_outbox`: idempotent delivery of the visible user/final
  assistant messages to the existing session store;
- `agent_global_rules`: explicit user-managed rules shared across workspaces;
- `agent_project_rules`: user-approved rules keyed by `workspace_id`;
- `agent_run_rules`: rules owned by one `run_id` and deleted when that run
  reaches a terminal state;
- `agent_workspaces`: canonical root identity and trust state.

The event append and run-projection update occur in one transaction. Run rules
expire at the run terminal transition and cannot be loaded by another run.
Deleting a Ru session deletes its Agent runs, events, remaining run rules, rules
scoped only to that session, and encrypted payloads. Project/global rules remain
until removed from Agent settings.

### Sensitive content

Safe event envelopes may contain IDs, event type, status, duration, byte counts,
truncation state, stable error code, and bounded safe summary. They do not
contain commands, arguments, code, diffs, stdout, stderr, environment values,
provider bodies, credentials, or raw model-control metadata.

Payloads needed by the model or authenticated UI are encrypted with AES-GCM.
The local AES key is randomly generated and protected with Windows DPAPI
CurrentUser scope; key and database files receive current-user ACLs. Payload
size is bounded by the corresponding versioned tool manifest. Ru-owned bearer,
provider, and service secrets are never injected into Agent command
environments or intentionally persisted.

All model-facing, client-streamed, and persisted terminal content passes through
a streaming redactor seeded with known Ru-owned secret values and bounded
secret-pattern detection. Detected spans are replaced before forwarding or
encryption. This is defense in depth, not a guarantee that arbitrary user or
project output contains no unknown secret; Full Access can print data that Ru
does not know is sensitive. Documentation and acceptance tests use this narrower
claim.

Global/project/run-rule metadata such as decision, scope, creation time, and
version may be stored directly. Matcher values that contain paths, executable
arguments, or command text use the same encrypted payload boundary; Broker
receives only the decrypted, versioned snapshot for the active lease.

Ordinary Chat history receives only the user message and the final visible
assistant answer. Agent events and payloads never enter memory extraction, TTS,
VTS metadata, reviewed examples, or diagnostic logs.

### Conversation-history delivery

`POST /agent/v1/runs` is the canonical user-turn entry point for Agent clients;
Desktop must not also submit that prompt through the ordinary Chat WebSocket.
Run creation stores a visible-user-message outbox record with a stable message
ID derived from the run. Core does not start the first model step until the
existing session store acknowledges that message.

Harness SQLite owns outbox state and retry scheduling. An ASP.NET hosted
dispatcher reads `pending` records, calls an idempotent
`ISessionStore.AppendIfAbsent(messageId, ...)`, and marks the outbox row
`delivered` only after acknowledgement. If the host crashes after the session
append but before the acknowledgement transaction, retry observes the same
message ID and returns success without a second append. A permanently invalid
or deleted session fails the run before its first model step.

Final answer creation stores a visible-assistant-message outbox record in the
same Harness transaction as the final model event. The run publishes
`completed` only after the session store acknowledges the final message. The
session message contract gains an optional stable source message/run ID and
deduplicates it, so an outbox retry after a crash cannot append the same visible
message twice. The outbox contains only sanitized visible text; tool data and
private payload references never cross this boundary.

While final delivery is pending, the run remains `waiting_history` with a
completion subtype and no further model/tool execution. A transient session
store failure retries the same stable ID; a permanent failure surfaces a stable
Agent persistence error without rewriting the final answer or re-running tools.

Deleting a session first cancels or closes its active run, then removes the
session and related Harness records. It cannot leave a detached run writing a
new final message into a deleted session.

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

The model cannot label its own call as safe. The canonical resolver classifies
each final path as inside-workspace, outside-workspace, or protected instead of
rejecting it before policy can return `ask`. Relative paths are always anchored
to the workspace; outside access requires an explicit absolute target. File and
working-directory paths are re-resolved at execution time so a changed junction
or symlink does not reuse an earlier decision silently.

### Rule evaluation

Non-overridable protocol invariants run first: schema validity, request/session
binding, one-time token consumption, argument-intent hash, credential handling,
timeout, output bounds, and cancellation support.

The effective ordered rule list is then composed from the selected preset,
explicit global rules, persistent project rules, run-scoped rules, and the
current one-call decision. The last matching rule wins. A rule can match tool
ID, canonical relative or absolute path pattern, resolved executable, argv
pattern, cwd pattern, operation class, and network intent. Unknown or invalid
patterns fail closed.

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

These classifications produce `ask` in Standard; they are not an OS boundary.
Full Access can read or change any such path that the Windows user can access,
including through arbitrary shell code. The non-overridable credential
invariant means Ru does not inject its own secret values into the command
environment or safe event stream; it does not claim that a Full Access process
cannot reach user-owned files or Windows credential facilities.

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

For Allow-for-run and Always-for-project, Broker returns the normalized
candidate matcher that will be saved, and the approval surface displays that
scope before confirmation. Approval-derived rules are exact by default and may
not silently introduce broader globs. Broader reusable patterns must be created
explicitly through the policy Settings/CLI surface.

The currently pending call is authorized by its consumed one-time approval
under the old snapshot. Host persists any reusable rule, Broker executes that
already-authorized call, and only after its final receipt does Host bind the new
snapshot for future calls. Rebinding invalidates other pending decisions under
the prior snapshot but never invalidates the call whose approval initiated the
change. If reusable-rule persistence fails, the call may proceed once and the
result reports that the reusable rule was not saved.

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
- `files.restore` with a one-time recovery ID
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

`files.delete` refuses a file larger than its recoverable-payload cap instead of
performing an irreversible structured delete. Its receipt contains an opaque
recovery ID. `files.restore` is advertised only when the active run owns a valid
recovery ID; it routes through Core and Broker, checks that the target has not
been recreated or changed, and consumes the recovery ID once. Recovery content
is never exposed through diagnostic logs.

The Agent never performs an implicit reset, clean, force checkout, or whole-tree
stage in the managed local-commit workflow. A commit stages only the
run-attributed path set, shows the exact staged diff summary, and requires
approval. A non-empty index at run start disables managed commit rather than
risk committing pre-existing staged work. A path already dirty at run start may
be edited and tested, but it is excluded from managed commit unless an exact
Agent-only patch can be constructed and applied to a clean index without the
baseline hunks. If that proof fails, the run finishes without a commit.
Unrelated dirty files are neither reset nor included. Push and PR operations are
outside this milestone. Arbitrary shell commands under Full Access remain
capable of bypassing this managed workflow, which is another reason Full Access
is not a safety boundary.

### Terminal semantics

`terminal.run` accepts a resolved executable, an argv list, a working directory,
and bounded timeout. `terminal.shell` accepts an explicit PowerShell script and
is the escape hatch for commands that cannot be represented structurally. A
dedicated `AgentCommandEnvironment` supplies the non-secret Windows and
developer variables required by Git, .NET, Python, and package caches, including
the user profile/application-data roots and executable search path. It strips
Ru service credentials and secret-like inherited names such as tokens,
passwords, provider keys, and connection strings. Milestone 1 does not accept
model-provided environment overrides; later explicit user configuration must
pass through permission and secret-storage review.

Each call owns an independent process tree. There is no hidden persistent shell
state. Stdout and stderr stream separately with monotonic frame order. The final
receipt contains exit code, duration, canonical cwd, truncation metadata, and a
bounded head/tail result. The runner drains output after the retention cap so a
noisy child cannot deadlock or allocate unbounded memory.

Broker protocol v2 carries bounded `tool.output.delta` frames before the final
receipt. Public Host forwards them through a bounded backpressure channel; a
slow or disconnected client may lose transient frames but cannot block process
drain or receipt persistence.

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

Core estimates provider input size before every model step. Because compaction
is deferred, it never silently drops earlier tool results or messages. If the
next step would exceed the provider context limit, the run terminates with
`context_limit_reached`, persists a bounded continuation summary, and asks the
user to start a new run. Continue cannot override a real provider context limit.

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
- `POST /agent/v1/runs/{runId}/trust`
- `POST /agent/v1/runs/{runId}/cancel`
- `POST /agent/v1/runs/{runId}/continue`
- `POST /agent/v1/runs/{runId}/reconcile`
- `GET /agent/v1/sessions/{sessionId}/runs`
- `GET /agent/v1/policies`
- `POST /agent/v1/policies`
- `DELETE /agent/v1/policies/{ruleId}`

Run creation includes `session_id`, canonical workspace request, prompt, Agent
permission preset snapshot, and idempotency key. The Host validates the preset
against the Agent session selection. An untrusted workspace creates a waiting
trust decision rather than executing a model or tool.

The trust endpoint accepts the run's opaque trust-request ID, the displayed
canonical workspace identity hash, and approve/deny. Approve persists workspace
trust and advances the waiting run; deny cancels it. Repeating the same response
is idempotent, while a different, expired, moved-workspace, or replayed request
returns `trust_stale`. Full Access never bypasses workspace trust.

The reconcile endpoint is valid only in `reconciliation_required`. It records
the user-observed outcome (`applied`, `not_applied`, or `unknown_continue`) and
the displayed state-snapshot hash before transition. It never reissues the old
execution ID.

The events endpoint uses Server-Sent Events. Durable events carry SSE IDs equal
to the per-run sequence, and reconnect accepts `Last-Event-ID` or
`after_sequence`. Transient assistant/process deltas are explicitly marked and
do not advance the durable cursor. They carry a connection-local frame sequence
and parent durable event ID. After reconnect, the client receives the final
durable message/tool snapshot instead of replaying transient chunks. Approval,
cancel, Continue, and payload reads use ordinary authenticated HTTP.

Policy endpoints are user-control endpoints and are not advertised as model
tools. They list, create, and remove validated global/project rules. Creating a
global rule requires an interactive Settings or CLI confirmation; an approval
card can create only run- or project-scoped rules.

The safe event envelope includes protocol version, event ID, run/session IDs,
sequence, event type, occurrence time, status, safe summary, and optional opaque
payload reference. Only the payload endpoint decrypts private content for an
authenticated client bound to the same run.

The current pre-implementation contract remains 41 HTTP plus 2 WebSocket routes.
This design adds 13 HTTP routes and no WebSocket route, so the post-cutover
canonical matrix is 54 HTTP plus 2 WebSocket routes. It preserves every existing
Chat, voice, VTS, settings, and session route and updates the canonical route
data in the same implementation slice.

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
ru agent run --preset standard --workspace D:\LLM "fix the bug and run tests"
ru agent run --preset standard --session <session-id> --workspace D:\LLM "continue this task"
ru agent attach <run-id>
ru agent trust <run-id> --approve|--deny
ru agent approve <run-id> <approval-id> --scope once|run|project
ru agent deny <run-id> <approval-id>
ru agent cancel <run-id>
ru agent reconcile <run-id> --outcome applied|not-applied|unknown
ru agent status <run-id>
ru agent policy list [--global | --workspace PATH]
ru agent policy import --global FILE
ru agent policy remove <rule-id>
```

Interactive mode renders safe activity, fetches selected payload previews, and
may prompt for approval. `--json` emits a versioned NDJSON event stream and
never prompts or implicitly approves, making it suitable for automation. The
CLI starts or attaches to the Public Host but never launches a separate Worker
or Broker topology of its own.

When `--session` is omitted, the CLI uses the existing authenticated session API
to create a dedicated Ru session and prints both session and run IDs. It never
silently attaches to whichever Desktop conversation happens to be active.

`--preset` defaults to `standard`. Selecting `full` shows the Full Access
warning and requires interactive confirmation when creating a new Agent session.
`--json` cannot make Full Access the implicit preset or answer a workspace-trust
request automatically.

`policy import` accepts only the versioned policy JSON schema, renders the
normalized matchers and effective decision, and requires interactive
confirmation. `--json` does not bypass that confirmation or create a global
rule non-interactively.

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
other raw content never enter diagnostic JSONL. The private payload boundary
receives only post-redaction content, subject to the documented limitation that
Ru cannot recognize every arbitrary user/project secret.

## Migration strategy

Implementation proceeds as vertical slices:

1. Add the Core state machine and SQLite store using fake Worker/Broker ports.
2. Add the one-step Worker v2 contract and a read-only end-to-end tracer run.
3. Move runtime ownership below WinUI and add CLI run/attach/cancel.
4. Add rule evaluation, workspace trust, and file edit/patch/delete/restore.
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

- state transitions, terminal states, trust/approval/Continue/reconcile paths,
  and invalid transitions;
- idempotency keys, event sequence uniqueness, and projection transactions;
- ordered permission-rule precedence and invalid-pattern fail-closed behavior;
- run-rule expiry plus approval/snapshot replacement ordering;
- Git/.NET/Python/ripgrep command analyzers;
- approval intent binding, expiry, replay rejection, and argument changes;
- encrypted payload round trips and absence of secrets from safe envelopes;
- known-secret streaming redaction and explicit unknown-secret limitation;
- conversation-outbox retries and duplicate visible-message rejection;
- Agent command-environment toolchain variables and secret-name stripping;
- output head/tail truncation and bounded draining;
- workspace path, junction/symlink, and protected-path handling;
- file preimage hashes, atomic replacement, patch preflight, and recovery copy;
- one-time delete recovery, changed-target rejection, and oversized-delete
  refusal.

### Contract and integration tests

- provider-neutral Worker `AssistantStep` compatibility;
- Public Host to Broker policy/approval/receipt contracts;
- policy-snapshot binding, replacement, and stale-approval invalidation;
- SSE reconnect from every durable sequence;
- duplicate run creation and duplicate approval responses;
- trust-request idempotency, replay, workspace move, and Full Access non-bypass;
- backend crash boundaries before dispatch, after dispatch, and before receipt;
- temp Git repositories with unrelated staged and unstaged changes;
- branch and commit approval staging only run-attributed paths;
- real Windows cancellation that proves child and grandchild termination;
- Desktop and CLI attaching to the same run;
- runtime single-instance, stale descriptor, bearer rejection, and owned-child
  cleanup;
- CLI default-session creation and Agent prompt/history single-delivery.

### Full deterministic matrix

- Python 3.11 and 3.13: Ruff, Mypy, and pytest;
- Broker, Connector, Backend, and WinUI .NET suites;
- canonical 54-HTTP plus 2-WebSocket route-matrix verification;
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
- file list/glob/search/read/write/edit/patch/delete/restore;
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
- Retain the future OS-enforced filesystem/network sandbox as a separate backlog
  project;
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
