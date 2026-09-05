# Ru Assistant

Ru is a local Vietnamese-English assistant whose desktop, public host, runtime
processes, persistent conversation state, voice, and VTube embodiment are
maintained in this repository.

## Language

**Public Host**:
The ASP.NET Core process that exclusively owns RuApp's authenticated HTTP and
WebSocket contract and, when launched as `RuBackend.exe --managed-runtime`,
owns Worker/Broker children in one Job Object (RuApp/CLI start-or-attach via descriptor).
_Avoid_: Edge, proxy, FastAPI backend

**Runtime Worker**:
The authenticated loopback managed C# process that serves Agent v2 and the
runtime/device v1 boundary. Python remains only at isolated provider seams and
test-only Harness fixtures; it is not a production Worker startup.
_Avoid_: Python backend, upstream server

**Generation Kernel**:
The module that owns provider selection, cancellation, and generation across
hosted and explicitly configured OpenAI-compatible adapters.
_Avoid_: ModelRunner globals, backend core

**Voice Engine**:
The isolated OmniVoice service running inside `omnivoice_env`, spawned by the
managed Runtime Worker as a local HTTP loopback process and returning native WAV audio
over a private lifecycle/TTS contract.
_Avoid_: TTS web server, Flask voice server

**Public Contract**:
The stable authenticated surface of 64 HTTP method/path pairs plus the single
`/voice/stream` WebSocket (43 legacy plus 20 additive `/agent/v1/*` plus 1
`POST /host/shutdown`; Agent v2 is
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

**Session Grant**:
The Broker-checked set of scopes and skill IDs attached to one Agent Session.
It does not grant arbitrary filesystem paths, domains, credentials, or apps.
_Avoid_: global allowlist

**Capability Profile**:
The session-scoped Agent preset that controls Broker policy. The model-visible
Worker v2 list is the runtime projection of eleven core Protocol definitions
(including offline `help.docs`) plus independently enabled `web.search`/`web.fetch`; Standard and Full Access receive
the same enabled list, while Full Access only loosens policy fallbacks.
_Avoid_: permanent permission

**Approval Token**:
A short-lived, one-time value bound to the exact request identity and
canonical arguments for an external or destructive action.
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
resolution; a moved identity creates a fresh trust request and Full Access
never bypasses it.
_Avoid_: repository-marker trust

**Workspace Terminal**:
The non-admin terminal adapter constrained to the configured workspace,
allowlisted executables, bounded output, cancellation, and process-tree
cleanup. Raw shell and network use require a separate approval path.
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
Settings, sessions, history, activity, structured memory, credentials, and
reviewed examples used by the active product. Business records live under
`%LOCALAPPDATA%\Ru`; structured memory stays project-owned under
`data/conversations`. `RU_DATA_ROOT` isolates both for tests.
_Avoid_: Cleanup candidates, generated data

**Retired Local Model Assets**:
Source, configs, scripts, datasets, checkpoints, evaluations, and documents
whose only purpose was in-process conversational-model generation or training.
_Avoid_: Reintroducing local model or training dependencies
