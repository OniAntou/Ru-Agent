# Ru Assistant

Ru is a local Vietnamese-English coding assistant whose desktop, public host,
runtime processes, and persistent conversation state are maintained in this
repository. The media surfaces (voice/STT, VTube Studio, screen observation)
were removed on 2026-09-13.

## Language

**Public Host**:
The ASP.NET Core process that exclusively owns RuApp's authenticated HTTP and
SSE contract and, when launched as `RuBackend.exe --managed-runtime`,
owns Worker/Broker children in one Job Object (RuApp/CLI start-or-attach via descriptor).
_Avoid_: Edge, proxy, FastAPI backend

**Runtime Worker**:
The authenticated loopback managed C# process that serves Agent v2 on
`/worker/v2`. There is no device v1 boundary, no Python Worker startup, and no
media adapter graph.
_Avoid_: Python backend, upstream server

**Generation Kernel**:
The module that owns provider selection, cancellation, and generation across
hosted and explicitly configured OpenAI-compatible adapters.
_Avoid_: ModelRunner globals, backend core

**Public Contract**:
The stable authenticated surface of 41 HTTP method/path pairs with no WebSocket
route (20 non-Agent plus 21 additive `/agent/v1/*`; Agent v2 is
the current generation path at `/worker/v2` and `/agent/v1/*`). Text turns use
the authenticated Agent HTTP/SSE path; there is no public Chat transport. The
legacy `RuBackend:AgentHarness:Version` setting is compatibility-only and is
not a product mode switch.
_Avoid_: Compatibility routes

**Worker Contract**:
The versioned typed commands, events, ordering, cancellation, limits, errors,
and capabilities exchanged with the managed C# Runtime Worker.
_Avoid_: Runtime HTTP relay

**Agent Session**:
An explicit, in-memory user session in which Ru may request bounded tools and
skills. It expires with the session and never becomes a global permission.
_Avoid_: permanent Agent mode permission

**Conversation Session**:
The persisted conversation and history record owned by the Public Host. The
`DELETE /sessions/{sessionId}` route removes the session and its owned Harness
artifacts only when the application envelope returns `status: "ok"`; an HTTP
success status alone is not deletion proof.
_Avoid_: treating an HTTP 200 response as a successful delete

**RuAgentBroker**:
The private, process-isolated .NET service that validates tool manifests,
session scopes, approval state, and adapter execution before a local action.
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
`ModelVisible` definitions for both the prompt and Worker `model_tools`.
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
The session-scoped Agent preset (`plan`, `standard`, opt-in `full_access`).
The model-visible Worker v2 list is the runtime projection of eleven Protocol
definitions, one purpose per tool: `read` (`offset`/`limit`), `list`
(`max_entries`), `grep`, `glob`, `help` (five bundled offline documents),
`todo_write` (the session task list), `write`
with `op=write|delete|restore`, `edit`, `bash`, and the independently enabled
standalone `web_search`/`web_fetch` tools with `network.search`/`network.fetch`
scopes, Google HTML primary with DuckDuckGo HTML fallback (plan-ask,
automatic on `standard`/`full_access`).
Standard is the default: every
run starts standard unless the stored session selection says `plan` or
`full_access`. `plan` is read-only: every mutation asks or denies, nothing
auto-allows. `full_access` is opt-in per session with interactive
confirmation; only hard denies (protected paths, invalid requests) still
apply. On a clean workspace `standard` auto-allows the in-workspace
terminal/external-directory/delete/unknown-executable subset
(`standard_clean_workspace_allow`), while timeouts, rule asks, and
invariants still ask or deny; there is no provider, model, protocol, or
tool fallback.
_Avoid_: permanent permission

**Agent Persona Source**:
The root-selected `Ru.md` instruction is the sole Agent persona/style source.
The built-in prompt is a neutral execution contract. Root instructions may
guide persona and workflow but cannot grant tools, folders, network access, or
override Broker policy.
_Avoid_: embedded fallback persona

**Approval Token**:
A short-lived, one-time value bound to the exact request identity and
canonical arguments for an external or destructive action.
Approval proposals survive approval: the pipeline stashes them in memory and
resumes without a new model proposal after `approval_resolved`; a repeated
identical deny still terminates the run. A transport failure retries once for
any access kind before reporting terminally.
_Avoid_: reusable confirmation flag

**Policy Snapshot**:
The immutable, versioned, SHA-256-canonical binding of preset, trusted
workspace identity, ordered `PolicyRule`/`PolicyMatcher` list, and creation
time that Broker v2 validates before advertising capabilities; a new
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
The binding of a canonical root to its Windows volume serial and root file
identity (`WorkspaceIdentity` `ws_*` + `DisplayIdentityHash`), persisted as
one active trusted identity per workspace and validated via final-path
resolution; a moved identity creates a fresh trust request and no preset
ever bypasses it.
_Avoid_: repository-marker trust

**Workspace Terminal**:
The non-admin terminal adapter constrained to the configured workspace,
allowlisted executables, bounded output, cancellation, and process-tree
cleanup. `bash` with `mode=run` auto-forms (read/test/build/lint/local-Git) run on an
authorization token; external mutation, deletion, extended timeouts, every
`bash` with `mode=shell` script, unknown executables, a git directory override
(`-C`), a non-inert `git -c key=value` config override, and global/system git
config writes require a one-time approval token. A clean-workspace `standard`
session auto-allows the in-workspace subset above; networked calls,
timeouts, and protected paths stay gated. Network use is audit metadata
plus an approval classifier, never a separate scope gate. An ambiguous outcome
pauses with a waiting status detail and `Continue` clears the inspection gate
with a `gate_cleared_by_user` audit event.
_Avoid_: unrestricted Bash/PowerShell access

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
providers through Worker v2 and `ModelRequest.Images`.
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
