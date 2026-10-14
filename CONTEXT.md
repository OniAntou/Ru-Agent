# Ru Assistant

Ru is a local Vietnamese-English coding assistant whose desktop, public host,
in-process runtime, and persistent conversation state are maintained in this
repository. The media surfaces (voice/STT, VTube Studio, screen observation)
were removed on 2026-09-13.

## Distribution

Desktop and CLI ship as separate per-user Windows x64 packages. Each carries the
matching Public Host and runtime; both use the canonical Ru profile. Installed
clients require an exact Host software-version match. Update installation is user initiated
from Settings → About or `ru update` (`/update` in TUI), with Stable as the default and
Preview opt-in. The authenticated `when_idle` shutdown handshake denies unfinished
runs and prevents new requests during the durable-state check. See
[distribution and updates](docs/operations/distribution-and-updates.md) and
[ADR-0019](docs/adr/0019-product-packages-and-explicit-updates.md).

`ru uninstall` uses the same idle-only maintenance handshake, then opens the
registered Windows CLI uninstaller after the caller exits. Removal keeps the
shared profile and does not target Desktop, portable folders or source builds.

Installed Desktop checks release availability at startup and every 30 minutes;
the title-bar Update shortcut appears only for a newer compatible release on
the selected channel. About contains version/build/platform details and update
controls. Source builds do not run automatic release checks.

## Language

**Public Host**:
The ASP.NET Core process that exclusively owns RuApp's authenticated HTTP and
SSE contract and, when launched as `RuBackend.exe --managed-runtime`, runs
Agent model generation and the tool execution authority in process (ADR-0012,
ADR-0014; RuApp/CLI start-or-attach via descriptor).
_Avoid_: Edge, proxy, FastAPI backend

**Runtime Worker**:
The retired private model-generation process. It was an authenticated loopback
managed C# process that served Agent v2 on `/worker/v2`; model generation now
runs inside the Public Host through the typed `ModelStepService` behind
`IModelStepRunner`, so no worker process, port, or protocol exists (ADR-0012).
There is no device v1 boundary, no Python Worker startup, and no media adapter
graph.
_Avoid_: Python backend, upstream server

**Generation Kernel**:
The module that owns provider selection, cancellation, and generation across
hosted and explicitly configured OpenAI-compatible adapters.
_Avoid_: ModelRunner globals, backend core

**Public Contract**:
The stable authenticated surface of 43 HTTP method/path pairs with no WebSocket
route (20 non-Agent plus 23 additive `/agent/v1/*`; Agent v2 is
the current generation path at `/agent/v1/*`). Text turns use
the authenticated Agent HTTP/SSE path; there is no public Chat transport. An
external tool drives the same surface over stdio through the local JSON-RPC
bridge (`ru agent rpc`), wrapped by the TypeScript SDK at
[`sdk/ru-agent`](sdk/ru-agent/README.md); the bridge adds no HTTP route and
opens no transport beyond stdin/stdout.
_Avoid_: Compatibility routes

**Worker Contract**:
The retired versioned typed commands, events, ordering, cancellation, limits,
errors, and capabilities that the Host exchanged with the managed C# Runtime
Worker over `/worker/v2`. In-process model steps are typed calls, and no
private transport remains: tool execution is in process as well (ADR-0012,
ADR-0014).
_Avoid_: Runtime HTTP relay

**Agent Session**:
An explicit user session in which Ru may request bounded tools, skills,
session-scoped folder grants, and a stored preset selection. It does not grant
workspace trust: trust is an explicit decision bound to the canonical workspace
identity and is evaluated independently of the Agent session.
_Avoid_: permanent Agent mode permission or preset-implied trust

**Conversation Session**:
The conversation and history record owned by the Public Host in the shared
`agent_harness.db` SQLite database (ADR-0013). The
`DELETE /sessions/{sessionId}` route removes the session and its owned Harness
artifacts only when the application envelope returns `status: "ok"`; an HTTP
success status alone is not deletion proof.
The WinUI workspace `+` creates and opens this record immediately. Its empty
row is a UI draft named `New Session`, visible only while open. Leaving without
sending hides the row; the first accepted chat keeps the same session ID and
shows a prompt-derived title until durable history supplies the server title.
Hiding an empty draft does not delete the Host record.
The GUI's `ChatSessionPresentation` owns the active session identity, asynchronous
open/load cancellation, run-monitor lifetime and once-per-turn completion.
It keeps the latest session click authoritative; WinUI renders the transcript
and retains/restores composer drafts through that owner.
Sidebar placement is separate UI state: app-root sessions default to Recents;
manual grouping/reordering survives refresh and restart but never changes the
Conversation Session's actual working directory or execution permissions.
_Avoid_: treating an HTTP 200 response as a successful delete

**Session Work State**:
The bounded, encrypted workflow record attached to one Conversation Session and
canonical workspace identity: user goal and constraints, the latest task list,
receipt-backed observations and verification codes, blockers, and next actions
with provenance, capped at 16 KiB with at most 16 entries per list. User intent
remains linked to its original run and event; model task candidates are
untrusted and persisted only from a successful durable `todo_write` receipt,
and file observations and verification records require receipt provenance. It
is context guidance only: it carries no approvals, grants, credentials, raw
terminal output, or execution authority, and a workspace identity change makes
the state unavailable. A new session stays independent.
_Avoid_: global memory, cross-session authority, model prose as proof, or work
state as permission

**RuAgentBroker**:
The in-process execution runtime and class library that validates tool
manifests, session scopes, approval state, and adapter execution before a local
action. It no longer runs as a child process: the Host composes
`BrokerExecutionRuntime` behind `IToolGatewayFactory` (ADR-0014).
_Avoid_: public API route, unrestricted shell, separate broker process

**Execution Authority**:
The single code path that actually performs a model-visible tool call:
`BrokerExecutionRuntime`, reached through the `IToolGateway` seam created by
`IToolGatewayFactory` inside the Host process. The Host keeps orchestration and
the `ask_user` interception only; no second copy of tool semantics, policy, or
adapters may exist.
_Avoid_: dual execution paths, HarnessToolGateway

**Tool Manifest**:
The Broker policy declaration of a tool's identity, adapter, risk class, scopes,
execution schemas, timeout, output limit, and trust class. Its ID/version and
proposal schema digest must match the immutable definition in
`RuAgent.Protocol`; it does not itself make a tool model-visible.
_Avoid_: dynamically constructed tool ID

**Tool Definition**:
The immutable Protocol-owned declaration of a tool's ID/version, model-facing
description, canonical scope, exposure flags, strict proposal schema, example,
and stable ordering. The Host runtime projection selects enabled
`ModelVisible` definitions for both the prompt and the model-step `model_tools`.
_Avoid_: treating a Broker manifest or adapter list as the model contract

**Tool Output Projection**:
A bounded, untrusted model-visible view of a durable Broker receipt. Structured
`read`, `grep`, `web_fetch`, and terminal `read_output` projections retain their
own cursor units (UTF-8 bytes, match results, extracted UTF-16 characters, and
spool UTF-16 characters respectively); a cursor
continues the same source or spool and never changes the receipt, tool authority,
or approval state. Grep's `next_offset` means a known next page; a
`scan_incomplete` flag with no cursor describes incomplete coverage, not a
pagination promise. Text pages preserve character boundaries: file cursors are
UTF-8 byte boundaries, web cursors are UTF-16 boundaries outside surrogate
pairs, and a window too small to advance returns a named failure. Missing
source identity fails closed when clipping needs a
cursor. The bounded prompt may still omit older output exchanges. Native history is
limited to 1,024 messages / 8 MiB and tool pages to 128 Ki characters; exact
provider-token preflight remains authoritative. Runtime references are at most
128 KiB each and are collectively fitted to the 8 MiB serialized context cap;
outputs already present in retained native history are not duplicated in the
system prompt. Read windows default to 1 MiB and accept up to 4 MiB, with
serialized-output fitting before Broker normalization and byte cursors for
continuation.
_Avoid_: treating projected output as a new receipt, complete history, or authority

**Skill Manifest**:
The declarative, bounded description of an allowed workflow, its named tools,
scopes, ordered steps, schemas, and approval policy.
_Avoid_: executable plugin or self-modifying workflow

**Markdown Skill**:
A directory containing a case-insensitive `SKILL.md` under one of the supported
skill roots: `<workspace-root>/skills`, `<workspace-root>/.ru/skills`, or
`%LOCALAPPDATA%\Ru\skills`. It is bounded, untrusted prompt guidance. For
example:

```markdown
---
name: pdf-analyzer
description: Analyze and summarize PDF files
allowed-tools: read, bash
---

# PDF Analyzer

Describe the review workflow here.
```

`allowed-tools` is descriptive metadata only; it never grants a tool, scope,
path, network permission, approval, or Broker capability. An optional
`requires:` field names a capability the workflow needs; only `python` is
recognized today. Every surface that returns a body enforces it: the model's
`skill` tool and the Host's `/skill` command fail closed with
`python_unavailable` when a Python requirement is unmet, and an unrecognized
token fails closed as `unsupported_skill_requirement` naming the token;
skills without `requires` are unaffected. The Host discovers
only direct child skill directories and lists each skill lazily as
name + description + `SKILL.md` path with the body version pinned in the
receipt digest; the full workflow is pulled on demand with the read tool,
never inlined. Adjacent
`references/`, `assets/`, and `scripts/` files are ignored by discovery and are
never automatically read or executed. Copying a package requires no downloader
or package manager: the Host re-reads the roots when building a new prompt
context, including a new Agent run or after restart. Markdown skills never
become `SkillManifest`/`skill.json` executable workflows and never enter
`/skills` or `/skill`; `RuAgentBroker` remains the sole authority for path
canonicalization, policy, approvals, and execution.
_Avoid_: treating `SKILL.md` as a permission manifest or executable plugin

**Session Grant**:
The Broker-checked set of scopes and skill IDs attached to one Agent Session.
It does not grant arbitrary filesystem paths, domains, credentials, or apps.
_Avoid_: global allowlist

**Capability Profile**:
The session-scoped Agent preset (`plan`, `standard`, opt-in `full_access`) mapped
to the immutable `AgentPolicyProfileCatalog` in `RuAgent.Protocol`. The catalog,
not prompt text or a client switch, is the canonical source used by both the
effective-policy prompt projector and Broker fallback evaluation. `plan` is a
read-only preset: workspace reads and Web are automatic, terminal and mutation
are denied outright (no per-change asks), and external structured files ask like
Standard. Profile version 2 defines `standard` by the canonical launch directory:
workspace reads, Web, structured workspace mutations, and executable/shell launches
from a workspace cwd are automatic. External structured files and terminal launch
directories outside the workspace ask; an approved external folder can be
remembered as a durable project-scope path pattern (or kept as a session-scoped
Broker grant), and remembered ask decisions store a bounded reviewed pattern
(see Approval Token). There is no language, command-form,
argv-path, network-declaration or executable-install-location approval list.
Both terminal modes run as the Windows user and can affect outside files and the
network; workspace launch permission is not an OS confinement boundary.
`full_access` also permits external structured files and arbitrary terminal
launches automatically; structured protected paths and hard-invalid requests
remain denied.
Explicit deny/ask rules, exact single-use authorization, protected structured
paths, hard-invalid requests and process lifetime/output bounds remain enforced.
Foreground launches above fifteen minutes still ask in Standard; background
launches use their bounded lifetime instead. Every running terminal task reserves
a session slot at launch, retained when foreground moves to background. The
Broker's `RU_MAX_BACKGROUND_TERMINAL_TASKS` setting defaults to 8 (range 1–64);
invalid explicit values fail initialization. A full session refuses new launches
as `background_task_limit_reached` without cancelling existing tasks.
API keys remain in credential storage
and diagnostics redact secrets. See [ADR 0017](docs/adr/0017-workspace-terminal-policy.md).
Structured protected paths and hard-invalid requests remain
denied. Standard is the
default; `full_access` requires a GUI confirmation or CLI
`--confirm-full-access`. The model-visible tool list remains the runtime projection of
thirteen Protocol definitions, one purpose per tool: `read` (`offset`/`limit`),
`list` (`max_entries`), `grep`, `glob`, `help` (five bundled offline documents),
`todo_write`, `write` with `op=write|delete|restore`, `edit`, `bash`, `skill`,
standalone `web_search`/`web_fetch`, and Host-intercepted `ask_user` (it parks
the run on a user question and never reaches the Broker). There is no provider,
model, protocol, tool, or workspace-cleanliness *substitution*: a failure is a
bounded, named
code, never a retry on a different backend. Bounded self-correction is not
substitution: a `[HARNESS RECOVERY]` note is durable feedback returned to the
same model, provider, and tool, and spent bounds fall back to the original
fail-closed code; an exhausted transient provider parks the run in
`WaitingUser` for an explicit Continue that retries the same provider. The one
exception is that
`web_search` races its configured engine against a fixed set of public HTML
engines inside the same tool decision, every request still passing `WebPolicy`.
_Avoid_: permission inferred from prompt text, repository cleanliness, or a
client-local preset table

**Native Tool Batch**:
One model step may contain optional narration and one or more ordered native
tool calls. The Host persists the assistant call batch and every durable result;
approval resume rehydrates and re-evaluates the retained proposals without a new
model proposal. Initial execution, approval/input resume and approval refresh
share proposal-batch classification, pause and abort cleanup in the Harness;
initial proposal persistence stays interleaved with evaluation, and refresh
cannot execute the batch. Read-only calls may execute concurrently in model order with at
most eight active, while mutations and terminal calls retain sequential
barriers. A text-selection provider path still emits exactly one JSON selection.
_Avoid_: one-call-per-step rule, reissuing approved calls

**Recovery Note**:
A durable `ModelContextNote` beginning with `[HARNESS RECOVERY]`, addressed to
the `ModelContext` audience, that answers a dead-end model step instead of
failing the run; the same model takes the next step with that feedback in
context. Bounds are per kind — 4 `unverified_after_tool_failure` notes, 4
`model_repairable` notes, 2 `empty_model_reply` notes and 2
`model_output_truncated` notes — under a global cap of
12 notes per run, measured over the recovery notes visible in the assembled
context. A note is persisted with the system role and is therefore never folded
away by compaction, so the cap is cumulative for the whole run rather than
per budget window. Every note is
same-model bounded self-correction, never provider/model/tool substitution;
when a bound is spent the run fails closed with the original code. A model
may instead emit a final `[BLOCKED]` report describing the incomplete/partial
outcome. The marker is removed from durable visible history, and the run ends
as failed with the receipt code (or `model_reported_blocker` without a failed
receipt), never verified success. This report does not spend further recovery
notes. Its durable outbox remains deliverable after runtime cleanup. An exhausted
provider-transient step is not failed either: it parks in `WaitingUser` with a
`ContinueRequested` preserving the provider's code, and an explicit Continue
retries the same provider. An approval-decision deny never enters recovery: a
user deny keeps its fail-closed rule and an ambiguous mutation still pauses
until explicit reconciliation. A repeated `policy_denied` tool receipt followed
by prose is a tool outcome, not an approval decision, so it may still take the
bounded `unverified_after_tool_failure` notes with the denial code as the
terminal code.
Provider `Length` finishes suppress the whole incomplete step, including any
partial tool calls, before execution; bounded recovery requests smaller output.
`ContentFilter` and `Error` finishes fail as `model_content_filtered` and
`model_reply_error` without recovery. Whitespace-only final text counts as empty.
_Avoid_: fallback answer, substitute model, retry on another backend

**Repeated Tool Recovery**:
Identical calls and short failed-call cycles are suppressed before duplicate
dispatch. Up to two `[REPETITION GUARD]` notes ask the same model to change its
arguments; exhaustion parks for explicit Continue. Suppressed assistant steps
remain audit-only, because they have no tool receipts to pair in native model
history. Trailing guard notes preserve the failed receipt for both repetition
checks and the final-answer verification guard. A note cannot turn a failed
verification into successful completion.

**Agent Persona Source**:
The root-selected `AGENTS.md` instruction is the sole Agent persona/style source.
The built-in prompt is a neutral execution contract. Root instructions may
guide persona and workflow but cannot grant tools, folders, network access, or
override Broker policy.
_Avoid_: embedded fallback persona

**Approval Token**:
A short-lived, single-use value bound to the exact request, canonical arguments,
lease, and policy snapshot for an ask decision. Approval proposals are retained
in memory while live and reconstructed from durable events and encrypted
argument payloads after restart; resume re-evaluates the complete batch and
never asks the model to reissue it. A repeated identical deny still terminates
the run, and there is no provider/model/protocol/tool fallback. Bounded
auto-recovery never applies to approval decisions or reconciliation: those
paths keep their exact fail-closed rules and no recovery note can soften them.
A repeated `policy_denied` tool receipt is not an approval decision: it follows
the Recovery Note bound above, and when that bound is spent the run terminates
with the denial code. Any allow decision remembers the approved intent for
the Agent session — materialized into the session's later policy
snapshots, so a later matching call authorizes without a
new prompt; the memory is in-process and dies with the session or a host
restart, and a session allow still passes every protected-path denial and
Broker evaluation. The explicit `allow_always` decision additionally remembers
a bounded pattern as a durable project-scope allow rule: a terminal run call
stores its requested executable plus at most two leading non-flag argv tokens
with the exact launch directory, while flag-first invocations and shell-mode
scripts stay exact; an external-directory ask stores the approved folder as a
path pattern that covers its subtree and survives restarts and later sessions
of the workspace. `rule_asks` and `terminal_protected_path_requires_approval`
never become a durable rule (the Host refuses the save and degrades to
`allow_once`); `allow_once` and session directory grants create no durable rule.
_Avoid_: reusable confirmation flag or approval-time reissue

**Session Approval Memory**:
The Host-side, in-process ledger that remembers the approved intent of
every allow decision for the lifetime of one Agent Session — the bounded
pattern when `allow_always` saved one, the exact canonical intent otherwise.
The runtime loop
materializes its entries as run-scope allow rules on top of each run's durable
`PolicySnapshot`, so a later matching call evaluates as allowed
without a new prompt; protected-path denials and Broker evaluation still decide
every call. It creates no durable rule, never outlives its session or a host
restart, is bounded by a 30-minute entry lifetime and a per-session cap, and is
cleared when the session is deleted. External-directory approvals keep the
Broker session directory grant (plus the durable project rule when
`allow_always` saved one) and write no entry.
Shell policy contexts and Host approval bindings carry the same selected runtime
and literal invocation prefix/script. Executable resolution stays in execution.
Nonempty shell argv allow matchers compare the whole invocation literally and
with case sensitivity; script wildcards cannot widen permission. Deny/ask rules
retain pattern matching.
_Avoid_: global approval cache, persisted permission

**Run Provider Selection**:
The secret-free durable choice pinned after successful bootstrap validation and
before a run's first model request: backend, alias, endpoint, protocol, model
and display label, credential account name, reasoning options, calling mode,
catalog context-window metadata and output-token reserve. Reopening a run
rebuilds its in-memory credential snapshot from this selection; current
settings cannot substitute a different provider or model. A legacy run without
the selection fails closed. Credential values never enter this record.
_Avoid_: current settings as restart fallback

**Provider Continuation**:
The bounded, typed, encrypted `InternalOnly` payload holding exact provider
items required to continue a successful native Responses or Anthropic Messages
tool step, bound to the run, provider, protocol, model, and model-step ID. For
Anthropic this preserves ordered text, signed thinking, redacted thinking, and
tool-use blocks exactly. It remains outside the
visible AssistantStep, canonical model context, client payload reads, and
telemetry; rehydration pairs it with the exact ordered normalized tool-call
batch before dispatch. A provider-native capability is enabled only by an
exact registry declaration, never by a model-name pattern. Broker remains the
sole tool Execution Authority.
_Avoid_: hidden reasoning in chat history or reconstructed prose

**Policy Snapshot**:
The immutable, versioned, SHA-256-canonical binding of preset, trusted
workspace identity, and ordered `PolicyRule`/`PolicyMatcher` list that Broker v2
validates before advertising capabilities; creation time stays metadata on the
wire and is deliberately excluded from the hash, so a snapshot re-loaded after a
restart still matches its durable hash. A new
snapshot `N+1` invalidates other pending `N` decisions after the
authorizing call completes.
_Avoid_: mutable rule set

**Policy Rule**:
The `Global|Project|Run|Once` scoped record with stable ID, decision
`Allow|Ask|Deny`, matcher (tool ID/version, canonical path pattern,
resolved executable, argv/cwd/operation/network intent), creation order,
and optional expiry; matcher values with paths/argv/cwd are encrypted at
rest and only the active decrypted snapshot contains them.
_Avoid_: repository-stored authority

**Workspace Trust**:
An explicit decision, with source `explicit`, binding one canonical root to its
Windows volume serial, root-file identity, and display hash
(`WorkspaceIdentity` `ws_*` + `DisplayIdentityHash`). Trust is requested once
per canonical identity, validated by final-path resolution, and independent of
the session preset. The GUI requires an explicit confirmation; CLI `run` and
`chat` require `--trust-workspace`. A moved or recreated identity is untrusted
again. No preset or clean-repository state grants trust.
_Avoid_: repository-marker, path-only, or preset-implied trust

**Workspace Terminal**:
The terminal adapter runs under execution-authority policy with bounded output,
hard timeout, cancellation, redaction, and process-tree cleanup. `plan` denies
terminal authority. `standard` automatically permits executable and shell
launches from a workspace cwd; external cwd and explicit ask rules prompt.
`full_access` also permits external cwd automatically. Both launch child processes as the current Windows user, without OS
filesystem or network confinement, including outside the workspace. The `bash`
schema has no sandbox selector
and rejects obsolete sandbox arguments. Structured protected-path denial does
not confine terminal commands, and clean/dirty repository state does not change
a decision. An ambiguous mutation outcome pauses for inspection until explicit
Continue. API keys remain in credential storage and are redacted from
diagnostics.
_Avoid_: treating repository cleanliness as authorization or terminal
approvals as OS confinement

**Run Event Feed**:
One client-side SSE subscription path owns frame dispatch and bounded reconnect
for WinUI and CLI. Durable event IDs advance the resume cursor; transient frames
remain outside durable history. WinUI keeps its cumulative reconnect retry
bound, while CLI progress can refill its retry budget. Consumer callback errors
propagate and are not retried as transport failures.
_Avoid_: replaying transient frames as durable history or hiding callback errors

**Multimodal Image Payload**:
A client-attached image (`image/png`, `image/jpeg`, `image/webp`) resized to at
most 2048px on client, validated against strict MIME and size bounds (max 4
images, 4 MB decoded per image, 20 MB total decoded), persisted atomically in
SQLite `agent_payloads` under kind `UserImage` (value 11) with dual audience
`AuthenticatedClient | ModelContext`, and projected to vision-capable model
providers through the typed model step and `ModelRequest.Images`.
_Avoid_: Unbounded base64 uploads, client-side raw file passthrough

**Protected User Data**:
Settings, sessions and their message history, activity, credentials, and
reviewed examples used by the active product. Business records live under
`%LOCALAPPDATA%\Ru`, and `RU_DATA_ROOT` isolates them for tests.
_Avoid_: Cleanup candidates, generated data

**Retired Local Model Assets**:
Source, configs, scripts, datasets, checkpoints, evaluations, and documents
whose only purpose was in-process conversational-model generation or training.
_Avoid_: Reintroducing local model or training dependencies
