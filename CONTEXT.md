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
The authenticated loopback Python process that contains model or hardware
implementations retained in Python.
_Avoid_: Python backend, upstream server

**Generation Kernel**:
The module that owns provider selection, cancellation, and generation across
hosted and explicitly configured OpenAI-compatible adapters.
_Avoid_: ModelRunner globals, backend core

**Voice Engine**:
The isolated OmniVoice service running inside `omnivoice_env`, spawned by the
Runtime Worker as a local HTTP loopback process and returning native WAV audio
over a private lifecycle/TTS contract.
_Avoid_: TTS web server, Flask voice server

**Public Contract**:
The stable authenticated surface of 54 HTTP method/path pairs plus `/chat` and
`/voice/stream` (41 legacy plus 13 additive `/agent/v1/*`; Agent v2 feature-flagged at
`/worker/v2` and `/agent/v1/*`, default `v1`).
_Avoid_: Compatibility routes

**Worker Contract**:
The versioned typed commands, events, ordering, cancellation, limits, errors,
and capabilities exchanged with the Runtime Worker.
_Avoid_: Runtime HTTP relay

**Agent Session**:
An explicit, in-memory user session in which Ru may request bounded tools and
skills. It expires with the session and never becomes a global permission.
_Avoid_: permanent Agent mode permission

**RuAgentBroker**:
The private, process-isolated .NET service that validates tool manifests,
session scopes, approval state, and adapter execution before a local action.
_Avoid_: public API route, unrestricted shell

**Tool Manifest**:
The versioned declaration of a tool's identity, adapter, risk class, scopes,
schemas, timeout, output limit, and trust class.
_Avoid_: dynamically constructed tool ID

**Skill Manifest**:
The declarative, bounded description of an allowed workflow, its named tools,
scopes, ordered steps, schemas, and approval policy.
_Avoid_: executable plugin or self-modifying workflow

**Session Grant**:
The Broker-checked set of scopes and skill IDs attached to one Agent Session.
It does not grant arbitrary filesystem paths, domains, credentials, or apps.
_Avoid_: global allowlist

**Capability Profile**:
The Chat, Agent, or Full Access session-scoped advertisement of
model-visible tool bindings; Chat is read-only, Full Access only loosens
execution policy, and every launch resets to Chat.
_Avoid_: permanent permission

**Approval Token**:
A short-lived, one-time value bound to the exact request identity and
canonical arguments for an external or destructive action.
_Avoid_: reusable confirmation flag

**Workspace Terminal**:
The non-admin terminal adapter constrained to the configured workspace,
allowlisted executables, bounded output, cancellation, and process-tree
cleanup. Raw shell and network use require a separate approval path.
_Avoid_: unrestricted Bash/PowerShell access

**Connector Adapter**:
An isolated calendar, task, or mail integration that separates preview from
side effect and requires one-time approval for writes or sends.
_Avoid_: ambient account access

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
