# Ru current architecture

Status: current ownership plus accepted architecture-v2 targets, updated 2026-09-30.

Operational invariants for generation and live acceptance are maintained in
[`operations/runtime-behavior.md`](operations/runtime-behavior.md).

## Coding core and plugins

The coding core is the product: Public Host, Broker execution authority, the
in-process Agent v2 model runner, the durable Agent SSE stream, and the
`RuApp`/`RuAgent.Cli` clients. The coding core never depends on a media
surface, and the repository no longer ships one: the voice/STT, VTube Studio, and screen-observation surfaces
were removed in the 2026-09-13 passes (see the repository `CHANGELOG.md`). A host is fully
functional for coding without any media toolchain; `build.bat` builds only the
coding core and has no plugin stage, and the whole public route contract is
always mapped.

## Process topology

Public Host (`RuBackend.exe --managed-runtime`, ASP.NET Core) owns the
Broker child lifecycle in one Windows kill-on-close Job Object. `RuApp` (WinUI)
and the headless `RuAgent.Cli` start-or-attach the Public Host through the
shared `RuAgent.Client` (`PublicHostStarter`/`PublicHostLocator` with
ACL-restricted loopback descriptor and bearer probe). On first start the Public
Host creates a per-run desktop secret, starts the isolated `RuAgentBroker` on a
dynamic loopback port, binds ASP.NET Core on port 8000, waits for Broker and
public readiness, and assigns the Broker to one Job Object. Model generation
runs in the Host process itself (ADR-0012), so the Broker is the only managed
child process. Managed startup is unconditional.
The Host remains running until explicitly stopped (or the owned child exits
unexpectedly) and does not auto-exit on idle. The owned Broker enters one Job
Object, but ownership is Public Host, not WinUI. Startup succeeds only after
public readiness; shutdown, startup failure, or an unexpected child exit
terminates the owned Broker tree.

Closing the GUI sends an authenticated `POST /host/shutdown` so the Host and its owned Broker Job exit gracefully; CLI detach still does not force-kill the shared Host.
Startup is cancellation-scoped, with checks after asynchronous stages before
creating subsequent UI services or timers. Host shutdown remains Host-owned.

Per-run provider snapshot creation reads exactly one canonical credential account:
Custom OpenAI uses `custom_api_key`, OpenAI uses `openai_api_key`, and Anthropic
uses `anthropic_api_key` with fixed official endpoints `https://api.openai.com/v1`
and `https://api.anthropic.com/v1`. A `models.dev:<provider-id>` backend reads
`<provider-id>_api_key` and resolves its base URL from the Models.dev catalog. Missing keys stay missing; aliases and other
providers' keys are never tried. Each model step resolves a fresh typed
in-memory provider snapshot that pins protocol
(`chat_completions`, `openai_responses`, `anthropic_messages`), reasoning
options, and for Anthropic the bounded `max_tokens` (min documented max,
8192). No key migration or provider switching is performed. An endpoint that
requires a key will reject an unauthenticated request; this does not trigger a
fallback.

Public `GET /models` is Host-owned provider discovery, not a relay of any
model step's selected-model status. Its bounded cache has a five-minute
lifetime,
30-second refresh throttle and provider/endpoint/credential-revision identity.
OpenAI uses `Authorization: Bearer` plus `store:false`; Anthropic uses
`x-api-key` and pinned `anthropic-version: 2023-06-01` with internal 8192
`max_tokens`. Custom uses only its canonical key. The live catalog is
intersected with a source-dated (`2026-09-01`) exact capability registry; unknown
IDs are retained as unsupported and never selected. The response adds per-model
protocol/reasoning metadata, stale/error state and last-success time. See
[provider catalogs](operations/provider-catalog.md).

The Chat client composes the per-provider `GET /models` catalogs into one
grouped, searchable picker through `GroupedProviderCatalogController`, which
owns one child controller per provider and isolates child failures. The Host
serves `GET /models` for any provider whose credential is configured, active
or not; unknown aliases fail closed as `provider_not_active` and a missing
credential fails as `catalog_unauthorized`. Selecting
a model posts one strict two-field payload (`backend_choice` + `selected_model`)
through the existing atomic `/settings` transition; the Host expands it onto
the mapped provider model field inside the same transaction. There is no
aggregate endpoint, no second save path and no fallback provider or model.

The Host validates model/reasoning against that catalog at the first model
step and pins model, endpoint, credential and allowlisted reasoning options
for the run. Each step resolves a fresh in-memory provider snapshot; the typed
step service rejects missing/invalid snapshots and strips untrusted reasoning
fields. The Host reads credentials directly from its own store, so no per-step
handoff file exists. Settings changes affect the next run;
there is no retry with another provider, model, key, protocol or effort.

## Agent autonomy and trust boundary

The immutable profile catalog in `RuAgent.Protocol` is the single semantic
source for the effective-policy prompt projection and Broker fallback:

| Preset | Canonical behavior |
| --- | --- |
| `plan` | Workspace read and Web are automatic; terminal and mutation are denied. |
| `standard` | Bounded-local in-workspace mutation is automatic; external structured access, path escape, and risky terminal intent ask. |
| `full_access` | External structured files and arbitrary terminal are automatic; structured protected paths and hard-invalid requests remain denied. |

Preset selection is session-scoped. Workspace trust is a separate explicit
decision, once per canonical `WorkspaceIdentity`; the GUI presents a guarded
confirmation and CLI `run`/`chat` require `--trust-workspace`. A moved or
recreated identity must be trusted again. Full Access requires the GUI gate or
CLI `--confirm-full-access`, and neither implies workspace trust.

A native model step may contain optional narration and one or more ordered tool
calls. If any call asks, the complete non-denied batch is retained durably and
resumes by re-evaluation without a new model proposal. Read-only executions
may overlap up to eight at a time while mutation/terminal barriers and receipt
order remain intact.

Full-access terminal authority delegates execution to the current Windows user.
It can read or change data outside the workspace and use the network; it is not
a network/path sandbox. Hard timeout, output cap, cancellation, redaction, and
process-tree cleanup remain active. Structured file tools independently retain
canonical/symlink/junction/hard-link containment and protected-path denial.

The current managed process flow (Phase 2 cutover) is:

```text
RuApp / RuAgent.Cli  --start-or-attach via RuAgent.Client-->  ASP.NET Core Public Host :8000
                                                                (RuBackend.exe --managed-runtime, owns one kill-on-close Job Object)
                                                                  |-- direct business, persistence, and durable Harness (Core + SQLite agent_harness.db + encrypted payloads)
                                                                  |-- in-process typed model step (WorkerV2StepService behind IModelStepRunner, one-step AssistantStep)
                                                                  |     `-- bounded provider adapters (OpenAI-compatible Chat Completions, OpenAI Responses, Anthropic Messages) via exact protocol dispatch
                                                                  `-- private call to RuAgentBroker execution boundary (profile-scoped file/terminal tools)
                                                                 Broker owned child:
                                                                   `-- RuAgentBroker
                                                                         `-- policy, approvals, and local action adapters
```

The public Agent path uses the in-process typed model runner. The legacy
`RuBackend:AgentHarness:Version` setting remains accepted by compatibility
Harness construction, but it is not a product mode switch and cannot re-enable
Chat. Durable policy/file-tool work is complete, with headless terminal and
reconciliation surfaces from Phase 3:

- In-process model runner: one typed `ModelGenerationRequest` → one provider generation → `AssistantStep(text, tool_calls, finish_reason)`. A native provider may return optional narration plus one or more ordered calls; a text-selection provider still emits exactly one JSON object with no surrounding selection prose. Provider errors map to bounded stable codes without provider/model fallback. The runner composes one system message with composer `2.0.0` from seven ordered blocks: persona, execution contract, environment, profile/effective policy, instruction blocks, capabilities, and runtime context. It returns a receipt whose version, block ids/order, and per-instruction digests Core validates before completion or tool dispatch. What that receipt attests is bounded: the `instruction.*` digests bind the exact instruction bytes in the context, while the other block digests are checked for shape and the caller does not receive the rendered prompt text. The typed step result carries text, tool calls, finish reason, and the prompt receipt, plus optional bounded `usage` and `reasoning_text`.
- Agent API/SSE: 21 `/agent/v1/*` routes plus durable `HarnessEvent` SSE (`id:<sequence>`, `Last-Event-ID`/`after_sequence` cursors, reconnect replay, transient non-durable deltas) with bearer auth, idempotency 409, `workspace_busy` 409, `preset_mismatch` 409, typed command dispatch (`/compact`, `/help`, `/skills`, `/skill`) without history insertion, active `GET`/`POST`/`DELETE /agent/v1/policies` (global requires `X-Interactive-Confirmed: true`, versioned JSON, encrypted matcher values), workspace concurrency/activity/changes reads and policy update, continuously renewed workspace membership while a run is driven, encrypted external-change journal entries, and `POST /agent/v1/runs/{runId}/approvals/{approvalId}` (exactly `allow_once`, `allow_session_directory` for external folders only, `allow_always` which additionally remembers the exact intent as a project rule, or `deny`; first-writer-wins `approval_stale`, exact matcher preview, 30-min expiry, old-snapshot execution ordering, explicit rules deny before ask before allow); `reconcile` is active on the Agent v2 headless path (Phase 3). Exact consecutive tool calls terminate as `repeated_tool_call` before another Broker dispatch. Ordinary assistant prose immediately following any failed or unverified tool receipt is suppressed from conversation outbox, recorded internally for audit as an `InternalOnly` payload, and terminates the run as failed with the tool error code; subsequent different recovery tool calls remain allowed.
- Broker v2: immutable `PolicySnapshot` and `BrokerRunLease` bind the run, session, canonical workspace identity, profile version, preset, and SHA-256 snapshot before capabilities are advertised. `PolicySnapshotEvaluator` applies hard invariants, ordered explicit rules, then the canonical `AgentPolicyProfileCatalog`; repository cleanliness and prompt text are never fallback inputs. Standard external structured access uses session-scoped `CanonicalDirectoryIdentity` grants, while Full Access selects an ephemeral per-call external root and never creates a reusable folder grant. Structured `CanonicalPathResolver`/`ProtectedPathCatalog` and adapter hard-link checks apply to structured file tools in every preset. Canonical fallback ask reasons are `external_directory_required`, `terminal_delete_requires_approval`, `terminal_external_mutation_requires_approval`, and `terminal_unknown_executable`. `terminal_timeout_requires_approval` and `web_approval_required` remain compatibility reasons for explicit rules; timeout above the protocol hard maximum is denied. Pending approvals and exact single-use authorization tokens bind to the active lease/snapshot and cannot be replayed.
- Tool Definition ownership: `RuAgent.Protocol` contains exactly 12 immutable, model-visible definitions (`read`, `list`, `help`, `todo_write`, `grep`, `glob`, `write`, `edit`, `bash`, `web_search`, `web_fetch`, `skill`). `read`, `list` and `help` are separate tools: `read` carries no discriminator, and `list` and `help` take the argument shapes that `read op=list` and `read op=help` accepted, so the split added no capability. `todo_write` replaces the session task list and touches neither disk nor network. `skill` loads one workspace SKILL.md body on demand; the prompt lists name + description only, and skill bodies never grant tools, scopes, paths, or approvals. `AgentModelToolContractCatalog` directly projects that catalog; the Host feeds the same ID/version/scope/strict proposal schema/example/digest to the prompt and the model-step `model_tools`. `ToolCatalog` retains Broker risk/approval/timeout/output policy and validates every manifest against the definition catalog. `RuAgentBroker` is the only managed tool execution authority.
- File tools: `read` returns one file's UTF-8 content with `offset`/`limit` and no discriminator; `list` enumerates one directory (streaming enumeration, ordinal relative paths, hidden/binary/protected skip with counts, result/byte/time caps, bounded `truncated` metadata, junction/symlink containment, no unbounded allocation); and `help` searches the five bundled offline documentation resources, is workspace-independent and never reads workspace files. `grep` (regex content search with `file_glob`/`case_sensitive`/`is_regex` filters and bounded results) and `glob` (ordinal path-pattern results with traversal caps) remain standalone tools, alongside Host-prepared hash-guarded `write` with `op=write`/`edit` and recoverable `write` with `op=delete`/`op=restore` (preimage hashes are captured internally, never supplied in model proposal JSON; encrypted recovery copy remains bounded, opaque, one-time, and never in diagnostics). Model-facing definitions require preservation of user-supplied absolute paths inside a session-granted directory; Broker canonicalization and containment remain authoritative. `edit` preserves CRLF style when LF markers are supplied; `grep` uses canonical `is_regex`, bounded concurrent reads, cancellation, and ordinal output ordering; recovery caps are configurable below a 4 MiB hard maximum. `files.apply_patch` remains an internal/non-advertised adapter and is not model-usable.
- Model surface: the twelve Protocol definitions are projected into the prompt and native `model_tools`; the Broker alone decides and executes each request. Plan, Standard, and Full Access use the matrix above. Managed Web remains bounded, redirect-validated, sanitized, and untrusted; Plan Web is automatic. Full-access terminal runs as the current Windows user, can access data outside the workspace, and can open sockets: it is not a network/path sandbox. Timeout, output cap, cancellation, and process-tree cleanup remain enforced. The managed Git workflow retains temporary-index isolation and rollback attribution. MCP stdio bridge code remains test infrastructure and is not production-composed; mail/calendar/task connectors are not advertised Agent tools.
- Accepted limitation — executable identity on the terminal path: the policy
  classifies a `bash` command by the executable token the model supplied
  (`PolicyContextFactory` keeps it verbatim and the analyzers match on
  `Path.GetFileName`), while `CommandIntentResolver` later resolves that token
  through the process `PATH`. A program placed in a directory already on `PATH`
  can therefore be the one that runs. Closing this needs a policy decision, not
  a patch: resolving the real path inside the policy would make
  `HasPathQualifiedExecutable` reject every path-qualified command and turn all
  bounded-local terminal execution into an approval, and refusing a bare name
  that resolves into a user-writable directory would break toolchains installed
  under the user profile (scoop, chocolatey, portable installs). Both options
  change who may run what without approval, so neither is taken unilaterally.
  The exposure already requires write access to a directory on `PATH`, which is
  strictly more than the same actor needs to run the program directly.
  The same shape applies to a repository's own git configuration on the
  terminal path: `GitCli` pins `core.fsmonitor` for the reads the Broker does
  itself, but `StreamingProcessRunner` deliberately does not, because it also
  carries the managed-commit calls that verify a `git diff --cached` hash
  before committing, and an extra `-c` would break that check rather than
  harden it. Measured on git 2.55, a repository-supplied `core.fsmonitor`
  program is not invoked by `git status` at all, so the terminal path is
  accepted as-is rather than hardened at the cost of that verification.
  Writing repository-local configuration is no longer automatic: a model `git
  config` write can install the same program-selecting keys, so writes ask
  while `--get`/`--list` reads stay auto-executable.
- Managed Web: `web_search` resolves the configured engine against a fixed set
  of public HTML engines and returns the first usable result, rather than
  substituting a second tool or a second provider. It is the one documented
  exception to the no-substitution invariant, and every request still passes
  `WebPolicy`, so it cannot widen the network boundary or bypass a denial.
- Bundled help path: `help` (`help@1.0.0`) is a model-visible `system.read` tool backed by five explicitly embedded Markdown resources. It is offline, workspace-independent, approval-free, and bounded; it returns stable document IDs and excerpts only. It never reads workspace files or live process/device state, and its reference text cannot override higher-priority instructions.
- CLI tracer: thin headless `RuAgent.Cli` delegates start/attach to `RuAgent.Client` and all run behavior to HTTP/SSE; it renders only safe envelope fields and exposes `agent approve`/`deny`, policy commands, and run/chat preset selection. `run` and `chat` require `--trust-workspace` for an untrusted canonical identity and `--confirm-full-access` for `full_access`; `--json` is accepted only with the explicit trust/full-access flags and cannot confirm an approval. The canonical Plan profile does not synthesize `web_approval_required`; an explicit ask rule may still surface that compatibility reason.

The current Agent flow is:

```text
RuApp -> ASP.NET Core public contract -> in-process typed model step -> bounded provider adapters
ASP.NET Core public contract -> private Broker lease -> RuAgentBroker (tool execution)
```

The in-process typed model step is the active Agent generation path; the typed
worker protocol v2 and the worker executable were retired with it (ADR-0012),
and the device v1 protocol was removed with the media surfaces. The shipped
OpenCode-style system-prompt composer does not introduce a
second conversation transport: text, coding, and tool-selection steps all use
the same Agent run and model-step path.

## Agent prompt contract

For every accepted model step, the Host rebuilds a bounded context and the
in-process step service composes exactly one `system` message. The seven ordered
blocks are the neutral Ru Agent runtime contract, Agent execution contract,
environment, profile plus canonical effective policy, root-selected Ru/skill
instruction blocks, host-derived capabilities, and runtime context.
The Host persists only the receipt digest metadata in an `InternalOnly`
`PromptReceipt` payload referenced by `model.prompt_composed`; the raw prompt,
instruction text, tool results, arguments, paths, and provider data do
not enter history, diagnostics, or public SSE. Public SSE advances over the
internal event without emitting it or its payload reference. The persisted
receipt carries block ids, block order, and digests, not the prompt, so it
records which instruction bytes the step was composed against; it is not an
attestation of the rendered system message (see the in-process model runner
note above).

The instruction loader reads the fixed `Ru.md`, `.ru/AGENTS.md`, and
`.ru/CONTEXT.md` files below the selected run root, then discovers direct-child
skill directories from these roots in precedence order:
`<workspace-root>/skills`, `<workspace-root>/.ru/skills`, and
`%LOCALAPPDATA%\Ru\skills`. A package is recognized only when it contains a
case-insensitive `SKILL.md`; for example:

```markdown
---
name: pdf-analyzer
description: Analyze and summarize PDF files
allowed-tools: read, bash
---

# PDF Analyzer

Describe the review workflow here.
```

The loader applies final-path containment, strict UTF-8 decoding, NFC and
newline normalization, a 64 KiB per-file cap, and a 256 KiB combined
instruction cap. It re-reads the roots whenever a prompt context is built, so
copying or editing a package is picked up on the next Agent run (and after a
restart) without a downloader or package manager. Only `SKILL.md` is loaded;
nearby `references/`, `assets/`, and `scripts/` files are ignored by discovery
and are never automatically read or executed. The typed context has bounded
references (32 KiB each), and a 512 KiB serialized-context
cap.

`Ru.md` remains the sole persona/style source. Skills are rendered as
`ExternalSkill` untrusted guidance. Their `allowed-tools` metadata is
descriptive only and cannot add capabilities, grants, paths, network access,
or approvals. Workspace `SKILL.md` bodies are prompt guidance only and never
execute tools.
`RuAgentBroker` remains authoritative for path
canonicalization, tool policy, approval, and execution; runtime references and
tool results stay untrusted.
Invalid context or receipt metadata fails closed with the bounded
`prompt_contract_invalid` path; there is no static-prompt fallback or reduced-
context retry. Managed runtime wiring enables the standalone `web_search`/`web_fetch`
tools by default with their `network.search`/`network.fetch` scope labels; no
settings field controls their exposure. Returned web text is bounded,
sanitized, and wrapped with the `[untrusted_web_reference]` marker.

Prompt composition is not an authority boundary. Broker policy, approvals,
workspace trust, protected paths, external-directory grants, and cancellation
remain authoritative. Workspace `SKILL.md` bodies are untrusted prompt
guidance only and never execute tools or grant anything; subagent
orchestration, LSP, extension/plugin/MCP discovery,
arbitrary system overrides, Chat compatibility, UI redesign, and the future
OS-enforced filesystem/network sandbox are outside this implementation.

## Harness ownership

The vNext Harness is a test-only owner. Its CLI creates a
temporary data/log root, two dynamically leased loopback ports, memory-only
bearers, and one Broker and ASP.NET Core edge child. While the port
leases are still held, it prebuilds Broker and edge sequentially; both are then
started with `--no-build` so their shared `RuAgent.Protocol` output cannot race
during a cold build. It classifies all 42 HTTP contract entries
(21 non-Agent plus 21 additive `/agent/v1/*`), uses deterministic fixtures for
safe mutations plus additive Agent v2 tracer fixtures (idempotency,
workspace trust via `WindowsWorkspaceIdentityResolver` final-path/volume/file
identity, policy snapshots, SSE cursor, payload binding, file discovery/read
and hash-guarded mutations/patch/delete/restore with configurable bounded recovery caps, approval
first-writer-wins and snapshot ordering), records
operator-required `POST /host/shutdown` as an explicit skip, and removes the
temporary root during idempotent
cleanup. An early child exit is recorded only as its
role, exit code, and lifecycle classification; raw child output is not copied
into the public artifact.
The Harness does not change the Public Host's normal production ownership of
the Broker child in its Job Object, nor the start-or-attach `RuAgent.Client`
contract for RuApp/CLI; credentials and fixed runtime defaults remain
Public Host-owned.

The GUI Harness extends that ownership boundary through an explicit
RuAppComposition: production uses the managed backend provider, while a
short-lived loopback descriptor selects an external Harness edge and a bounded
JSONL event sink. The WinUI shell, API clients, Agent SSE client, session
history path, and reply lifecycle remain the same. Harness mode never takes over
an existing RuApp instance and drives only the `agent-*` scenarios.

## Ownership

| Owner | Responsibilities |
| --- | --- |
| RuApp / RuAgent.Cli (via RuAgent.Client) | Desktop views (RuApp) or headless rendering (CLI), public client calls via `AgentApiClient`/`SessionApiClient`/`AgentSseClient`, and user-visible state; start-or-attach Public Host through validated descriptor + `GET /status` bearer, but do not directly own the Broker child. |
| ASP.NET Core Public Host (`RuBackend.exe --managed-runtime`) | Authentication, limits, all 42 HTTP routes (21 non-Agent + 21 `/agent/v1/*`), settings transactions, credentials, sessions and their message history, activity, in-process model generation (`WorkerV2StepService` behind `IModelStepRunner`), Agent run ordering, public error mapping, metadata filtering, durable Harness (Core state machines incl. `tool.execution_requested`, SQLite WAL + AES-GCM payloads, conversation outbox, workspace trust with valid `WorkspaceIdentity`, session permissions with monotonic version, encrypted policy snapshots `PolicySnapshot`/`PolicyRule`/`PolicyMatcher` with `Sha256` and `PolicySnapshotCanonicalizer` ordering, `FileRecoveryStore`/`RunChangeSetStore`, approval ordering `validate explicit trust → re-evaluate exact retained proposals → consume single-use token or validate session folder grant → execute under N → receipt → N+1`), Agent SSE (`id:<sequence>` cursors, `cursor_mismatch`, transient `t` IDs), and Broker child ownership in one kill-on-close Job Object. The Host does not auto-exit on idle. |
| RuAgentBroker | Private execution boundary for model-visible tools, capability policy, approvals, bounded adapters, and verified action receipts; private `run.open` lease binds the immutable `PolicySnapshot` (effective profile, workspace identity, ordered rules, SHA-256) and canonical workspace root, then validates the hash before capabilities. The Host and the Broker project the same twelve Protocol definitions: separate `read`, `list`, `help`, `todo_write`, `grep`, `glob`, `write`, `edit`, `bash`, `web_search`, `web_fetch`, and `skill` tools. Standard outside-root structured targets resolve through a session-scoped `CanonicalDirectoryIdentity`; the grant is lost on restart, never shared with new/forked sessions, and never authorizes terminal. Full Access instead selects an ephemeral nearest external ancestor for each structured call and does not retain a directory grant. Structured hard-link/protected-path checks remain active. `files.apply_patch` is not advertised. The Broker has no public HTTP contract. |

Unknown public paths stop in ASP.NET Core. There is no forwarding catch-all,
route ownership selector, or alternate host mode.

## Contracts and transport

The canonical Public Contract is
[`backend-dotnet/public-route-contract.json`](../backend-dotnet/public-route-contract.json):
42 HTTP method/path pairs with no WebSocket route (21 `/agent/v1/*` and 21
non-Agent routes); REST and SSE clients authenticate with the desktop secret
(bearer for HTTP/SSE). ASP.NET Core maps
every public response and error; `GET /agent/v1/runs/{runId}/events` is SSE
with durable `id: <sequence>` and reconnect via `Last-Event-ID` or
`?after_sequence=` with `cursor_mismatch` rejection.

The only private contract left is the Broker lease: the Host opens one
profile-scoped lease per turn loop, and `RuAgent.Cli` never speaks it directly;
the CLI is a thin HTTP/SSE client. The typed Worker protocol v2 (`/worker/v2`)
and its WebSocket transport were retired with the worker executable
([ADR 0012](adr/0012-in-process-model-runner.md)); one model step is now a
typed in-process call that returns `AssistantStep(text, tool_calls,
finish_reason)` with bounded payloads, ordering, cancellation, and stable
error codes. One generation may contain optional narration and one or more
ordered native calls; text-selected calls remain exactly one JSON object.

Text turns are authenticated Agent runs consumed through the durable Agent SSE
stream. There is no public WebSocket and no second text-generation transport.

### Multimodal image attachments

The Agent harness supports end-to-end multimodal image input across WinUI 3, ASP.NET Core Public Host, the in-process model step, and LLM providers:
- **Client preparation:** WinUI 3 `ImageAttachmentResizer` scales images exceeding 2048px on their longest dimension down proportionally and compresses them to JPEG (quality 0.85) or PNG (if alpha present). Up to 4 images can be attached per user turn.
- **Backend validation & persistence:** `POST /agent/v1/runs` accepts up to 1 MB JSON requests containing `image_attachments` (data URLs). ASP.NET Core validates MIME types (`image/png`, `image/jpeg`, `image/webp`), base64 structure, decoded per-image size (<=4 MB), total decoded size (<=20 MB), and vision compatibility of the selected model. Image data URLs are persisted as AES-GCM encrypted `agent_payloads` of kind `UserImage` (value 11) with audience `AuthenticatedClient | ModelContext`. Prompts may be empty when images are attached.
- **Model step integration:** the typed `ModelGenerationRequest.Images` carries decrypted `UserImage` payloads. `WorkerV2StepService` projects these into `ModelRequest.Images` (`IReadOnlyList<ModelImage>`).
- **Provider projection:** Model adapters project images into provider-native payloads:
  - OpenAI Responses: `{"type": "input_image", "image_url": "data:image/..."}`
  - Anthropic Messages: `{"type": "image", "source": {"type": "base64", "media_type": mime, "data": b64}}`
  - OpenAI-Compatible: `{"type": "image_url", "image_url": {"url": "data:image/..."}}`
  Non-vision models fail closed with `vision_unsupported`.

## Data ownership

ASP.NET Core owns business persistence. Settings, sessions, activity,
and credentials default to the canonical `%LOCALAPPDATA%\Ru` location,
with `RU_DATA_ROOT` available for isolated runs. Secrets are stored through Windows Credential
Manager rather than JSON files.

Session deletion is coordinated by the Public Host through
`DELETE /sessions/{sessionId}`. The route keeps its stable HTTP 200 response
envelope: only `status: "ok"` confirms that the session and its owned Harness
artifacts were removed; a missing or failed delete returns `status: "error"`
even with HTTP 200. WinUI refreshes the sidebar only after application-level
success and creates a replacement session only after successfully deleting the
last one.

`GET /sessions` additionally carries `workspace_id`, `workspace_label`, and
`workspace_path` per session, resolved from the session's latest Harness run
(null when the session has no runs or the workspace identity changed), so WinUI
can group the sidebar by workspace without a second round trip.

The canonical data-location contract is documented in
[ADR 0004](adr/0004-canonical-ru-data-location.md).

## Privacy boundary

Only data needed for a requested operation crosses a private boundary. Visible
assistant text may enter Agent generation and approved persistence, but private
markers and model-control metadata must not enter history, reviewed examples,
cleanup manifests, or logs.

Automated checks may
record statuses, timings, counts, event types, and cleanup state; they never
record credentials, bearer tokens, request bodies, session identifiers,
transcripts, or private marker metadata.
