# Ru Agent Tools and Skills

**Date:** 2026-08-22
**Status:** Approved
**Scope:** A process-isolated agent capability platform for Desktop & files, workspace Terminal, Web & research, and Personal workflow

## 1. Decision summary

Ru will become a full agent through a separate `RuAgentBroker` process. The
broker owns tool registration, skill registration, policy enforcement, tool
adapter execution, approval tokens, bounded workflow execution, and safe
result construction. The Public Host remains the authenticated owner of the
public HTTP/WebSocket contract and conversation session. The Runtime Worker
continues to own the Generation Kernel, Voice Engine integration, and existing
runtime implementations.

The model never receives direct access to the Windows API, filesystem, shell,
web credentials, or external connectors. It emits a structured tool request
through the existing typed worker seam. The Public Host forwards that request
to the private broker protocol. The broker validates the request again,
executes an allowlisted adapter, and returns a bounded, sanitized result.

The first rollout has three vertical slices in this order, with the workspace
Terminal included in the first Desktop slice:

1. Desktop & files plus workspace Terminal.
2. Web & research.
3. Personal workflow and authenticated connectors.

Permissions are granted per agent session. Read-only tools may run
automatically inside the granted session. Reversible tools require the matching
session scope. External and destructive actions require a one-time approval,
even when the session is in agent mode.

## 2. Context and existing boundaries

Ru already has the following ownership vocabulary and contracts:

- The **Public Host** owns RuApp's authenticated HTTP and WebSocket contract.
- The **Runtime Worker** owns Python model or hardware implementations and
  communicates through the versioned Worker Contract.
- The **Generation Kernel** owns provider selection, cancellation, and
  generation across hosted and explicitly configured OpenAI-compatible
  adapters.
- The **Voice Engine** remains the isolated OmniVoice process owned through the
  existing runtime lifecycle.
- The current public surface remains the existing 41 HTTP routes plus `/chat`
  and `/voice/stream`; this feature does not add a public tool route.

The current Python runtime has marker-based PC actions such as `[APP_OPEN: ...]`
and `[VOLUME: ...]`, and a search marker flow in
`src/ru_runtime/generation/orchestration.py`. These are compatibility seams,
not the target agent contract. The new structured broker protocol replaces
marker parsing incrementally while preserving current chat, voice, VTS,
memory, and privacy behavior.

The repository also contains a deterministic Harness with process ownership,
dynamic-port, session-descriptor, cleanup, and GUI acceptance patterns. The
agent broker must use those patterns rather than inventing an unowned process
lifecycle.

## 3. Goals

- Give Ru bounded, observable, cancellable access to local tools, web research,
  workspace Terminal, and authenticated personal workflows.
- Isolate tool execution from both the Public Host and Runtime Worker.
- Make every tool call typed, versioned, policy-checked, and auditable.
- Make skills declarative and composable without allowing arbitrary code to run
  from a prompt or skill manifest.
- Preserve the `anh`/`em` conversation contract and keep tool metadata private
  from visible chat, TTS, history, memory, and reviewed artifacts.
- Reuse existing process, token, session, cancellation, and Harness patterns.
- Make each rollout slice independently testable with fake adapters.
- Keep the design extensible to additional adapters or a later skill
  distribution system without changing the public chat contract.

## 4. Non-goals and acceptance boundary

- No unrestricted shell, admin/elevation, registry editor, or arbitrary delete
  tool in the first Desktop and workspace Terminal slice.
- No skill may contain executable Python, .NET, PowerShell, JavaScript, or
  native code in v1.
- No persistent global agent permission in v1. Grants end with the session.
- No unattended background task after the originating chat turn ends.
- No new public HTTP route solely for tool execution.
- No reintroduction of local conversational-model training or retired local
  model assets.
- No automatic sending of real email, calendar events, messages, purchases, or
  other external side effects in deterministic tests.
- No claim that deterministic connector tests prove live account, microphone,
  speaker, OmniVoice, or VTube Studio acceptance.

## 5. Process ownership and data flow

### 5.1 Public Host

The Public Host remains the public contract owner. It is responsible for:

- authenticated desktop sessions and conversation correlation;
- mapping internal agent events to safe `/chat` events for the GUI;
- storing the current session grant and approval state;
- forwarding structured requests between the Runtime Worker and Broker;
- exposing only safe activity, approval, and error summaries to the client;
- recording bounded audit events through the existing diagnostic boundary.

The Public Host does not execute Windows tools or connector code.

### 5.2 Runtime Worker

The Runtime Worker continues to own generation, voice, and existing runtime
implementations. The Worker Contract gains structured agent messages, but the
worker does not receive tool credentials or a direct adapter handle.

When generation requires an action, the worker emits a `tool_request` event and
waits for a matching `tool_result`, cancellation, denial, or timeout. The
worker must ignore results whose request, session, or correlation identity does
not match the active turn.

### 5.3 RuAgentBroker

The broker is a separately launched, privately authenticated process. It owns:

- the versioned private protocol;
- `ToolRegistry` and `SkillRegistry`;
- session policy revalidation;
- one-time approval token validation;
- bounded skill execution;
- adapter process lifecycle;
- input validation and output redaction;
- cleanup of broker-owned temporary data;
- safe agent audit events.

The broker must not own conversation history, credentials, TTS, VTS state, or
the public HTTP contract.

### 5.4 Tool adapters

Built-in read-only adapters may run inside the broker process when their
implementation has no unbounded native or external dependency. Filesystem,
connector, and third-party adapters use an explicit adapter seam and may run
as child processes when their risk or dependency surface warrants isolation.

An adapter receives only the scoped session descriptor and validated tool
arguments. It does not receive the model prompt, hidden system instructions,
full conversation history, or unrelated environment variables.

### 5.5 End-to-end flow

```text
User prompt
    -> Public Host
    -> Runtime Worker generation
    -> structured tool_request
    -> Public Host correlation and session check
    -> RuAgentBroker private protocol
    -> policy and approval check
    -> tool adapter
    -> bounded sanitized tool_result
    -> Runtime Worker continuation
    -> Public Host visible response
```

No adapter output is treated as an instruction. Web pages, documents, and
connector responses are untrusted data and are wrapped as data before being
returned to the Generation Kernel.

## 6. Private broker protocol

The initial protocol is versioned JSON envelopes over an authenticated local
WebSocket connection, matching the repository's existing typed worker style.
It is loopback-only and is not exposed through the Public Contract.

Every envelope contains:

```json
{
  "protocol_version": 1,
  "message_type": "tool_request",
  "request_id": "opaque-request-id",
  "session_id": "opaque-session-id",
  "correlation_id": "chat-correlation-id",
  "deadline_ms": 10000,
  "payload": {}
}
```

The protocol supports these message types:

- `hello` and `hello_ok` for version and capability negotiation;
- `session_open` and `session_close`;
- `skill_start`, `skill_event`, and `skill_result`;
- `tool_request`, `approval_required`, `approval_response`, and `tool_result`;
- `cancel`;
- `health` and `health_ok`.

`tool_request` contains only the tool ID, exact version, validated arguments,
scope requirements, idempotency key, and bounded deadline. It never carries the
full prompt or hidden instructions.

`tool_result` has one of these statuses:

- `succeeded`;
- `failed`;
- `cancelled`;
- `denied`;
- `approval_required`;
- `timed_out`.

The result contains a safe summary, typed output when allowed, a stable error
code when unsuccessful, duration, and bounded metrics. It never contains
credentials, raw request/response bodies, unrestricted child output, or raw
audio.

The broker is started with a unique session descriptor containing its loopback
endpoint, token, protocol version, session root, and expiry. The descriptor is
restricted to the current user, consumed once, and deleted after startup. It is
never placed in command-line arguments, chat content, logs, or result artifacts.

## 7. Tool contract and policy

### 7.1 Tool manifest

Every registered tool has a manifest equivalent to:

```json
{
  "id": "files.write",
  "version": "1.0",
  "adapter": "builtin.files",
  "risk": "reversible",
  "scopes": ["workspace.write"],
  "input_schema": {},
  "output_schema": {},
  "timeout_ms": 10000,
  "max_output_bytes": 32768,
  "filesystem_roots": ["session_workspace"],
  "network": "none",
  "supports_dry_run": true,
  "trust": "built_in"
}
```

Required manifest fields are `id`, `version`, `adapter`, `risk`, `scopes`,
input/output schemas, timeout, output limit, and trust class. The registry
rejects duplicate IDs, unsupported schemas, missing limits, unknown scopes,
and adapters that are not installed in the allowlisted catalog.

### 7.2 Risk classes

- `read`: observes bounded local or remote data without mutation.
- `reversible`: changes local state with a defined undo or recovery path.
- `external`: sends data or creates a record in an external service.
- `destructive`: deletes data, executes privileged behavior, changes
  credentials, or makes an irreversible system change.

The risk class is declared by the manifest and verified by the adapter. An
adapter cannot downgrade its risk at runtime.

### 7.3 Scopes

Initial scopes are explicit and narrow:

- `system.read` and `system.write`;
- `app.open`;
- `clipboard.read` and `clipboard.write`;
- `workspace.read` and `workspace.write`;
- `terminal.execute` and `terminal.network`;
- `network.read` and `network.write`;
- `calendar.read` and `calendar.write`;
- `tasks.read` and `tasks.write`;
- `mail.read`, `mail.draft`, and `mail.send`.

The broker rejects a tool whose required scope is absent from the session
grant. A scope never implies access to every path, domain, account, or app;
those are separately constrained by adapter configuration.

### 7.4 Session grant and approval

The GUI creates a session grant when the user enables Agent mode and selects
Desktop & files, Web & research, or Personal workflow. The grant:

- is held by the Public Host and revalidated by the Broker;
- expires when the session closes or its explicit expiry is reached;
- is not persisted as a global permission;
- lists scopes and enabled skill IDs rather than arbitrary code paths.

Default behavior:

- `read` tools run automatically inside the matching session scope;
- `reversible` tools run only with the matching session scope;
- `external` and `destructive` tools always require a one-time approval;
- `terminal.run` remains workspace-only and non-admin; raw `terminal.shell`,
  network-enabled commands, credential access, unrestricted delete, and
  privilege escalation require one-time approval or are denied by the catalog.

An approval response contains a one-time token bound to the exact request hash,
session ID, tool version, arguments, and expiry. Reusing the token, changing
arguments, or approving after expiry fails closed.

## 8. Skill Registry and Orchestrator

### 8.1 Skill package

A v1 skill package contains only declarative data:

```text
skills/
  research.and_note/
    skill.json
    instructions.md
```

The manifest declares:

- stable skill ID and version;
- description and supported language labels;
- input and output schemas;
- required tool IDs and scopes;
- ordered steps and bounded branch conditions;
- maximum steps and total timeout;
- approval policy;
- trust class and source metadata.

Steps may call a registered tool, validate a typed result, select a bounded
branch, or request a named text transformation. They may not execute source
code, construct a tool ID dynamically, change their own scopes, or create a
background task.

The registry accepts built-in skills and allowlisted signed packages. It
rejects unsigned executable packages, duplicate versions, undeclared tools,
undeclared scopes, unbounded loops, and manifests exceeding the global step or
data limits.

### 8.2 Orchestrator state machine

```text
skill_selected
    -> input_validated
    -> step_ready
    -> tool_requested
    -> approval_required | tool_running
    -> result_sanitized
    -> next_step | completed | failed | cancelled
```

The default per-skill limit is 8 steps and 120 seconds, with a hard broker
ceiling of 32 steps and 5 minutes. A skill that reaches a limit returns
`timed_out` or `failed` and cannot silently continue.

The model may select a skill and provide typed arguments, but the Orchestrator
chooses the next valid step from the manifest. Tool results are data, not new
workflow instructions.

### 8.3 Initial skills

- `desktop.organize-files`: list, classify, preview move, request approval,
  move within the session workspace.
- `research.and-note`: search, fetch, extract source metadata, summarize, and
  request approval before writing a note.
- `personal.create-calendar-event`: validate a proposed event, show a preview,
  request approval, and create it through the connector.
- `desktop.open-workspace`: validate a known workspace and open an allowlisted
  app or file.

## 9. Initial tool catalog and migration

### 9.1 Core and Desktop tools

The first catalog contains:

- `system.time`;
- `system.status`;
- `app.list` and `app.open`;
- `files.list`, `files.read`, `files.write`, and `files.move`;
- `workspace.search`;
- `clipboard.read` and `clipboard.write`;
- `system.volume.preview` and `system.volume.set`.

### 9.1.1 Workspace Terminal

Terminal is a broker tool, not direct model access. The session descriptor
exposes only installed and explicitly selected runtimes: PowerShell, Git Bash,
or WSL. The selected runtime is never silently replaced by another shell.

The safe typed tool is:

```json
{
  "tool": "terminal.run",
  "shell": "git-bash",
  "working_directory": "relative/workspace/path",
  "executable": "git",
  "arguments": ["status", "--short"],
  "timeout_ms": 120000
}
```

`terminal.run` accepts an executable and argument vector. It runs only below
the selected workspace root, rejects symlink escapes, uses a sanitized
environment, has no admin token, does not allocate a TTY, and kills the full
process tree on cancellation or timeout. Its default timeout is 120 seconds,
its hard maximum is 5 minutes, and stdout/stderr are bounded before a result
is returned.

The raw script tool is `terminal.shell`. It accepts a Bash or PowerShell
script only after explicit user approval for that exact request. It has the
same workspace, environment, timeout, output, network, and process-tree
limits as `terminal.run`; the raw script is never an automatically generated
skill step.

The first command catalog includes `git`, `rg`, `dotnet test`, `dotnet build`,
`pytest`, and other explicitly registered workspace executables. Package
restore, clone, fetch, upload, or other network activity requires the separate
`terminal.network` scope and one-time approval. The broker records only a safe
execution summary by default: executable, relative working directory, exit
code, duration, and status. It does not persist raw command text or shell
output when they may contain secrets.

Named tools such as `git.status`, `workspace.test`, `workspace.build`, and
`files.search` remain available so skills can use predictable, easily tested
operations instead of raw shell when possible.

All filesystem tools are scoped to an explicitly resolved workspace/session
root. The first slice excludes arbitrary delete, registry modification, and
unrestricted shell execution, and admin/elevation.

The existing `[APP_OPEN: ...]` and `[VOLUME: ...]` marker execution becomes a
compatibility adapter during migration. The structured path is authoritative;
marker parsing is removed after the host, worker, and GUI clients use the
versioned request contract. The current search marker becomes `web.search`.

### 9.2 Web tools

The second catalog contains:

- `web.search`;
- `web.fetch`;
- `web.extract`;
- `web.download` with explicit approval when it writes to disk;
- `notes.create` with workspace scope and approval.

Every fetched page is labeled untrusted. The adapter removes scripts and
enforces response size, content type, domain policy, and timeout limits. The
skill retains source title and URL for citations without persisting the full
raw response.

### 9.3 Personal connectors

The third catalog is implemented as connector adapters with isolated OAuth
credentials:

- `calendar.list`, `calendar.preview_create`, and `calendar.create`;
- `tasks.list`, `tasks.create`, and `tasks.complete`;
- `mail.search`, `mail.draft`, and `mail.send`.

Connector credentials remain in the OS credential store or connector-owned
secret boundary. They never enter the model context, tool result, chat,
history, memory, or artifact. Sending mail, creating calendar events, and
completing tasks always require one-time approval.

## 10. GUI behavior

The existing Chat view adds a compact Agent mode control. Enabling it opens a
session grant panel with the three capability groups. The selection applies to
the current session only.

The GUI shows bounded activity events such as:

- `Ru đang tìm trong workspace...`;
- `Ru đang đọc 2 nguồn web...`;
- `Ru đang chờ anh duyệt việc lưu ghi chú...`.

Terminal activity shows the selected shell, relative working directory, safe
executable preview, exit status, duration, and truncated output in an
expandable diagnostic surface. It does not place an unbounded terminal log in
the conversation transcript.

An approval card shows the action, tool/skill, target, data leaving the device,
undo availability, and expiry. It offers `Duyệt lần này`, `Từ chối`, and `Xem
chi tiết`. It never displays credentials or raw connector payloads.

The `Dừng` action cancels the current skill run, the matching Runtime Worker
request, the Broker request, and any child adapter process. A cancelled or
stale result cannot update visible chat, memory, history, VTS, or playback.

The GUI does not expose a permanent global permission switch or a background
task dashboard in v1.

## 11. Failure, privacy, and cleanup semantics

- Broker startup failure blocks agent execution but does not prevent ordinary
  chat from working.
- A worker, broker, or adapter that exits before its readiness checkpoint fails
  the relevant run.
- A ready child that exits before requested shutdown is an unexpected failure.
- Every request has a bounded timeout and cancellation path.
- Public Host and Broker both reject mismatched session, request, correlation,
  or idempotency identities.
- Prompt injection in web/file content is recorded as untrusted data and never
  changes scopes or workflow structure.
- Tool results are redacted before crossing back into the model context.
- Broker-owned descriptors, temporary files, ports, child processes, and
  connector sessions are cleaned in a `finally` path.
- Cleanup failure fails the agent run and produces a safe error code; it never
  deletes existing project files, model assets, user data, or unrelated logs.
- A user-selected diagnostic retention directory may retain bounded safe logs,
  but retention is never the default and never occurs inside the source tree.

## 12. Verification and acceptance

### 12.1 Automated contracts

The implementation must add focused tests for:

1. Protocol versioning, authentication, capability negotiation, and malformed
   envelopes.
2. Tool manifest schema, duplicate registration, unknown scopes, and adapter
   trust validation.
3. Risk policy for read, reversible, external, and destructive tools.
4. Session grant expiry, missing scope, approval token binding, replay, and
   rejection.
5. Skill max steps, timeout, undeclared tool, undeclared scope, and loop
   prevention.
6. Path/domain allowlists, output limits, secret redaction, and untrusted web
   content handling.
7. Cancellation, stale-result suppression, idempotency, adapter crash, broker
   crash, and cleanup.
8. Deterministic fake adapters for Desktop, Web, and Personal workflow slices.
9. Terminal workspace restriction, shell selection, argument validation,
   symlink escape rejection, environment sanitization, output limits,
   cancellation, timeout, process-tree cleanup, and network-scope approval.
10. Existing chat, voice, VTS, memory, no-emoji, and metadata privacy
    contracts.

### 12.2 Harness modes

The existing Harness gains:

- a backend mode that launches Broker, Public Host, and Runtime Worker with
  owned ports and credentials;
- a deterministic mode that uses fake adapters and produces no external side
  effects;
- a GUI mode that checks Agent mode, activity, approval, stop, cancellation,
  and error presentation;
- a safe schema-versioned artifact that reports component status, check status,
  tool IDs, timing, and cleanup without credentials or content.

The Harness must prove that an incorrect token, wrong session, wrong request
hash, or stale result is rejected. It must verify broker port closure, child
termination, descriptor deletion, fixture cleanup, and temporary-root cleanup.

### 12.3 Manual acceptance

Manual acceptance remains separate for:

- real web accounts and OAuth consent;
- actual email/calendar side effects;
- live microphone and speaker behavior;
- OmniVoice playback;
- VTube Studio actions and subtitle behavior.

Deterministic success must never be reported as live hardware or external
account success.

## 13. Implementation slices

The eventual implementation plan will keep these slices independently
testable:

1. **Broker foundation:** process descriptor, private protocol, readiness,
   token ownership, health, shutdown, fake adapter, and safe artifact.
2. **Public Host and Worker seam:** structured tool request/result messages,
   correlation, cancellation, session grants, and safe `/chat` agent events.
3. **Agent policy and skill core:** manifest schemas, registry, risk matrix,
   approval token, bounded Orchestrator, and deterministic skill fixtures.
4. **Desktop & files plus workspace Terminal:** core system/app/file/workspace
   adapters, `terminal.run`, explicitly approved `terminal.shell`, the first
   two built-in skills, and migration of old PC/search marker execution behind
   adapter compatibility.
5. **Web & research:** search/fetch/extract/download/note adapters, untrusted
   content handling, citations, and research skill.
6. **Personal workflow:** isolated OAuth connector process, calendar/tasks/mail
   preview and approval flows, and connector cleanup.
7. **GUI and Harness acceptance:** Agent mode UX, activity/approval/stop states,
   deterministic GUI scenarios, CI backend mode, and explicit manual gates.

No slice may modify unrelated voice, VTS, memory, theme, or current Harness
work without an explicit contract update and matching tests.

## 14. Documentation impact

When implementation begins, update these documents in the same contract slice:

- `CONTEXT.md` with the approved terms `Agent Session`, `RuAgentBroker`,
  `Tool Manifest`, `Skill Manifest`, `Session Grant`, and `Approval Token`.
- `docs/operations/runtime-behavior.md` with agent session, terminal policy,
  privacy, and cancellation behavior.
- `docs/operations/verification.md` with Broker/Harness commands, artifacts,
  deterministic modes, and the separate live acceptance boundary.
- `README.md` with supported Agent mode capabilities and the fact that
  external/destructive actions require approval.
- An ADR if the Broker process ownership or connector credential boundary
  changes an existing documented architecture decision.

The old marker behavior remains documented only as migration history; the
structured broker protocol is the current contract once the migration slice is
complete.
