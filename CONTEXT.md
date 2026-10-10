# Ru Assistant

Ru is a local Vietnamese-English coding assistant whose desktop, public host,
in-process runtime, and persistent conversation state are maintained in this
repository. The media surfaces (voice/STT, VTube Studio, screen observation)
were removed on 2026-09-13.

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
The stable authenticated surface of 42 HTTP method/path pairs with no WebSocket
route (20 non-Agent plus 22 additive `/agent/v1/*`; Agent v2 is
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
`read`, `grep`, and terminal `read_output` projections retain their own cursor
units (UTF-8 bytes, match results, and UTF-16 characters respectively); a cursor
continues the same source or spool and never changes the receipt, tool authority,
or approval state. Missing source identity fails closed when clipping needs a
cursor. The bounded prompt may still omit older output exchanges.
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
Standard. `standard` allows workspace reads, Web, in-workspace mutation, and
bounded-local terminal; it asks for external structured files and risky
terminal intent. `full_access` is the skip-all mode: external structured files
and risky/arbitrary terminal intent are automatic, and structured protected
paths (the harness key/database files and runtime descriptors, plus any
user-added `RU_PROTECTED_PATHS` entries) and hard-invalid requests remain
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
model proposal. Read-only calls may execute concurrently in model order with at
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
when a bound is spent the run fails closed with the original code. An exhausted
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
with the denial code. Any allow decision remembers the exact approved intent for
the Agent session — the intent is materialized into the session's later policy
snapshots, so a later call with the same canonical intent authorizes without a
new prompt; the memory is in-process and dies with the session or a host
restart, and a session allow still passes every protected-path denial and
Broker evaluation. The explicit `allow_always` decision additionally remembers
the exact intent as a durable project-scope allow rule; `allow_once` and session
directory grants create no durable rule.
_Avoid_: reusable confirmation flag or approval-time reissue

**Session Approval Memory**:
The Host-side, in-process ledger that remembers the exact canonical intent of
every allow decision for the lifetime of one Agent Session. The runtime loop
materializes its entries as run-scope allow rules on top of each run's durable
`PolicySnapshot`, so a later call with the same intent evaluates as allowed
without a new prompt; protected-path denials and Broker evaluation still decide
every call. It creates no durable rule, never outlives its session or a host
restart, is bounded by a 30-minute entry lifetime and a per-session cap, and is
cleared when the session is deleted. External-directory approvals keep the
Broker session directory grant only and write no entry.
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
terminal authority. `standard` retains classifier-bounded intent and its
existing approval rules; `full_access` retains its existing arbitrary-terminal
policy. Both launch child processes as the current Windows user, without OS
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
