# Ru Assistant

Ru is a local Vietnamese-English coding assistant whose desktop, public host,
runtime processes, and persistent conversation state are maintained in this
repository. The media surfaces (voice/STT, VTube Studio, screen observation)
were removed on 2026-09-13.

## Language

**Public Host**:
The ASP.NET Core process that exclusively owns RuApp's authenticated HTTP and
SSE contract and, when launched as `RuBackend.exe --managed-runtime`,
owns the Broker child in one Job Object and runs Agent model generation
in process (ADR-0012; RuApp/CLI start-or-attach via descriptor).
_Avoid_: Edge, proxy, FastAPI backend

**Runtime Worker**:
The retired private model-generation process. It was an authenticated loopback
managed C# process that served Agent v2 on `/worker/v2`; model generation now
runs inside the Public Host through the typed `WorkerV2StepService` behind
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
route (21 non-Agent plus 21 additive `/agent/v1/*`; Agent v2 is
the current generation path at `/agent/v1/*`). Text turns use
the authenticated Agent HTTP/SSE path; there is no public Chat transport. The
legacy `RuBackend:AgentHarness:Version` setting is compatibility-only and is
not a product mode switch.
_Avoid_: Compatibility routes

**Worker Contract**:
The retired versioned typed commands, events, ordering, cancellation, limits,
errors, and capabilities that the Host exchanged with the managed C# Runtime
Worker over `/worker/v2`. In-process model steps are typed calls; the only
private transport left is the Broker lease (ADR-0012).
_Avoid_: Runtime HTTP relay

**Agent Session**:
An explicit user session in which Ru may request bounded tools, skills,
session-scoped folder grants, and a stored preset selection. It does not grant
workspace trust: trust is an explicit decision bound to the canonical workspace
identity and is evaluated independently of the Agent session.
_Avoid_: permanent Agent mode permission or preset-implied trust

**Conversation Session**:
The persisted conversation and history record owned by the Public Host. The
`DELETE /sessions/{sessionId}` route removes the session and its owned Harness
artifacts only when the application envelope returns `status: "ok"`; an HTTP
success status alone is not deletion proof.
_Avoid_: treating an HTTP 200 response as a successful delete

**RuAgentBroker**:
The private, process-isolated .NET service — the Host's only managed child
process — that validates tool manifests, session scopes, approval state, and
adapter execution before a local action.
_Avoid_: public API route, unrestricted shell

**Execution Authority**:
The single module that actually performs a model-visible tool call:
RuAgentBroker, reached through the `IToolGateway` seam. No in-process Host
copy of tool semantics may exist.
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
path, network permission, approval, or Broker capability. The Host discovers
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
effective-policy prompt projector and Broker fallback evaluation. `plan` allows
workspace reads and Web, and denies terminal and mutation. `standard` allows
workspace reads, Web, in-workspace mutation, and bounded-local terminal; it asks
for external structured files and risky terminal intent. `full_access` allows
external structured files and risky/arbitrary terminal intent, but structured
protected paths and hard-invalid requests remain denied. Standard is the
default; `full_access` requires a GUI confirmation or CLI
`--confirm-full-access`. The model-visible tool list remains the runtime projection of
twelve Protocol definitions, one purpose per tool: `read` (`offset`/`limit`),
`list` (`max_entries`), `grep`, `glob`, `help` (five bundled offline documents),
`todo_write`, `write` with `op=write|delete|restore`, `edit`, `bash`, `skill`,
and standalone `web_search`/`web_fetch`. There is no provider, model, protocol,
tool, or workspace-cleanliness *substitution*: a failure is a bounded, named
code, never a retry on a different backend. The one exception is that
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

**Agent Persona Source**:
The root-selected `Ru.md` instruction is the sole Agent persona/style source.
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
the run, and there is no provider/model/protocol/tool fallback. The explicit
`allow_always` decision additionally remembers the exact intent as a
project-scope allow rule; `allow_once` and session grants stay single-use.
_Avoid_: reusable confirmation flag or approval-time reissue

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
The terminal adapter runs under Broker policy with bounded output, hard timeout,
cancellation, redaction, and process-tree cleanup. `plan` denies terminal
authority. `standard` allows only classifier-bounded local intent inside the
trusted workspace; external cwd/argv, arbitrary shell, deletion, unknown
executables, and other risky intent ask. `full_access` delegates arbitrary
terminal execution as the current Windows user. Full Access can read or change
data outside the workspace and use the network; it is not a path or network
sandbox. Structured protected-path denial does not become terminal confinement,
and clean/dirty repository state does not change a decision. An ambiguous
mutation outcome pauses for inspection until explicit Continue.
_Avoid_: treating repository cleanliness as authorization or Full Access
terminal as sandboxed

**Connector Adapter**:
An isolated calendar, task, or mail integration that separates preview from
side effect and requires one-time approval for writes or sends.
_Avoid_: ambient account access

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
