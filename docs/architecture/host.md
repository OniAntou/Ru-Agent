# Host implementation

Current reference. [System ownership](../architecture.md) · [Domain glossary](../../CONTEXT.md) · [Tool policy](../operations/agent-tools.md).

## Coding core and plugins

HTTP handlers resolve resource services directly: run admission/creation,
queries, lifecycle, approvals, policies, workspace/trust, activities, commands
and inputs have separate owners. `AgentV1Service` retains pure projection helpers,
not an orchestration facade or a shared dependency bag. Policy snapshots, runtime
roots, inspection, workspace snapshots/journal protection and gateway composition
are required dependencies. Production-container composition tests cover these
owners; omission cannot silently create a run without its policy rules.

Shutdown uses asynchronous disposal for provider snapshots, structured diagnostic
writes and mutex leases. Synchronous compatibility disposal has a bounded wait
and reports incomplete cleanup. Mutex release stays on the dedicated owner
thread; deferred cleanup retains synchronization resources until admitted work
has quiesced.

Session lists and interchange exports share `SessionWorkspaceResolver` to resolve
the latest durable run's encrypted workspace metadata. Export resolves the requested
session directly, independently of the session-list cap. Sessions without a readable
workspace retain an unset working directory.

Interchange exports have a shared 128 MiB bound on the actual serialized JSON
envelope, including nested format escaping (`SessionInterchangeLimits`). The
Host measures it with the same serializer used for the response and returns
413 `export_too_large` before sending an oversized success. CLI and GUI use
`ResponseHeadersRead` and parse a bounded JSON stream, independently of the
general API reader's 8 MiB cap. Declared and actual stream lengths are checked.
Format file contents and the parsed JSON still live in memory; this is bounded
buffering, not a constant-memory export directly to disk.

The coding core is the product: Public Host, Broker execution authority, the
in-process Agent v2 model runner, the durable Agent SSE stream, and the
`RuApp`/`RuAgent.Cli` clients. The coding core never depends on a media
surface, and the repository no longer ships one: the voice/STT, VTube Studio, and screen-observation surfaces
were removed in the 2026-09-13 passes (see the repository `CHANGELOG.md`). A host is fully
functional for coding without any media toolchain; `build.bat` builds only the
coding core and has no plugin stage, and the whole public route contract is
always mapped.



Public Host (`RuBackend.exe --managed-runtime`, ASP.NET Core) is a single
process that owns model generation, the tool execution authority and the
durable Harness. `RuApp` (WinUI) and the headless `RuAgent.Cli` start-or-attach
the Public Host through the shared `RuAgent.Client` (`PublicHostStarter`/
`PublicHostLocator` with ACL-restricted loopback descriptor and bearer probe).
On first start the Public Host creates a per-run desktop secret, binds ASP.NET
Core to an available loopback port, and publishes its descriptor only after
Kestrel reports the bound address. Clients discover that address from the
descriptor. Model
generation and tool execution run in the Host process itself (ADR-0012,
ADR-0014): the retired Broker child, its authenticated WebSocket lease and
`RuAgentBroker.exe` are gone, so the Host starts no managed child. Managed
startup is unconditional.
The managed Host exits after ten minutes without an in-flight request, recent
request or registered run (`RU_HOST_IDLE_MINUTES`, `0` disables this timer), or
when explicitly stopped. Startup succeeds only after public readiness; a
managed startup failure exits with a bounded code.

Closing the GUI sends an authenticated `POST /host/shutdown` so the Host exits gracefully; CLI detach still does not force-kill the shared Host.

## Contracts and transport

The canonical Public Contract is
[`backend-dotnet/public-route-contract.json`](../../backend-dotnet/public-route-contract.json):
53 HTTP method/path pairs with no WebSocket route (28 `/agent/v1/*` and 25
non-Agent routes); REST and SSE clients authenticate with the desktop secret
(bearer for HTTP/SSE). ASP.NET Core maps
every public response and error; `GET /agent/v1/runs/{runId}/events` is SSE
with durable `id: <sequence>` and reconnect via `Last-Event-ID` or
`?after_sequence=` with `cursor_mismatch` rejection.

WinUI and CLI use the shared client event-feed reconnect path. It advances the
resume cursor only from durable event IDs and dispatches transient activity,
assistant-delta, summary, and tool-output frames without treating them as
durable history. Reconnect accounting preserves each client's presentation
policy: WinUI keeps a cumulative retry bound, while CLI progress can refill its
retry budget. Exceptions from event callbacks propagate to the caller; they
are not interpreted as transport failures and do not trigger a reconnect.

No private wire contract remains: the Broker lease was retired with the child
process ([ADR 0014](../adr/0014-in-process-execution-and-terminal-sandbox.md)),
and `RuAgent.Cli` is a thin HTTP/SSE client. The typed Worker protocol v2
(`/worker/v2`) and its WebSocket transport were retired with the worker
executable ([ADR 0012](../adr/0012-in-process-model-runner.md)); one model step is
now a typed in-process call that returns `AssistantStep(text, tool_calls,
finish_reason)` with bounded payloads, ordering, cancellation, and stable
error codes. One generation may contain optional narration and one or more
ordered native calls; text-selected calls remain exactly one JSON object.

Text turns are authenticated Agent runs consumed through the durable Agent SSE
stream. There is no public WebSocket and no second text-generation transport.

External tools get a local-only stdio surface: `ru agent rpc` speaks JSON-RPC
2.0 over stdin/stdout, resolving the same `runtime_descriptor.json` and bearer
token and reusing the same HTTP/SSE clients as the CLI. It adds no HTTP route
and opens no transport beyond stdio; the TypeScript SDK at
[`sdk/ru-agent`](../../sdk/ru-agent/README.md) wraps the bridge with typed session
and run handles.

### Multimodal image attachments

The Agent harness supports end-to-end multimodal image input across WinUI 3, ASP.NET Core Public Host, the in-process model step, and LLM providers:
- **Client preparation:** WinUI 3 `ImageAttachmentResizer` scales images exceeding 2048px on their longest dimension down proportionally and compresses them to JPEG (quality 0.85) or PNG (if alpha present). Up to 4 images can be attached per user turn.
- **Backend validation & persistence:** `POST /agent/v1/runs` has a 26 MiB JSON request-body bound for encoded `image_attachments` data URLs, distinct from decoded image bounds. ASP.NET Core validates MIME types (`image/png`, `image/jpeg`, `image/webp`), base64 structure, decoded per-image size (<=4 MiB), total decoded size (<=20 MiB), and vision compatibility of the selected model. Image data URLs are persisted as AES-GCM encrypted `agent_payloads` of kind `UserImage` (value 11) with audience `AuthenticatedClient | ModelContext`. Prompts may be empty when images are attached.
- **Model step integration:** the typed `ModelGenerationRequest.Images` carries decrypted `UserImage` payloads. `ModelStepService` projects these into `ModelRequest.Images` (`IReadOnlyList<ModelImage>`).
- **Provider projection:** Model adapters project images into provider-native payloads:
  - OpenAI Responses: `{"type": "input_image", "image_url": "data:image/..."}`
  - Anthropic Messages: `{"type": "image", "source": {"type": "base64", "media_type": mime, "data": b64}}`
  - OpenAI-Compatible: `{"type": "image_url", "image_url": {"url": "data:image/..."}}`
  Non-vision models fail closed with `vision_unsupported`.
- **Workspace images through `read`:** the agent can also look at an image file in the workspace ([ADR 0026](../adr/0026-read-returns-workspace-images.md)). The run's pinned selection records whether its model accepts image input; the in-process gateway sends that decision on the `tool.execute` envelope, and the Broker returns the image beside small JSON metadata or fails with `image_unsupported_by_model`. The pipeline stores it as a model-only, opaque, encrypted `ToolImage` payload (kind 18) whose id is derived from the tool result's payload id, so no event or context row names it. `SqliteModelContextSource` attaches the newest four to their tool results; older results carry an omitted note. See [Providers](providers.md#tool-images) for the per-protocol projection.



## Public Host

The ASP.NET Core process that exclusively owns RuApp's authenticated HTTP and
SSE contract and, when launched as `RuBackend.exe --managed-runtime`, runs
Agent model generation and the tool execution authority in process (ADR-0012,
ADR-0014; RuApp/CLI start-or-attach via descriptor).
_Avoid_: Edge, proxy, FastAPI backend

## Runtime Worker

The retired private model-generation process. It was an authenticated loopback
managed C# process that served Agent v2 on `/worker/v2`; model generation now
runs inside the Public Host through the typed `ModelStepService` behind
`IModelStepRunner`, so no worker process, port, or protocol exists (ADR-0012).
There is no device v1 boundary, no Python Worker startup, and no media adapter
graph.
_Avoid_: Python backend, upstream server

## Public Contract

The stable authenticated surface of HTTP method/path pairs with no WebSocket
route (25 non-Agent plus 28 additive `/agent/v1/*`; Agent v2 is
the current generation path at `/agent/v1/*`). Text turns use
the authenticated Agent HTTP/SSE path; there is no public Chat transport. An
external tool drives the same surface over stdio through the local JSON-RPC
bridge (`ru agent rpc`), wrapped by the TypeScript SDK at
[`sdk/ru-agent`](../../sdk/ru-agent/README.md); the bridge adds no HTTP route and
opens no transport beyond stdin/stdout.
_Avoid_: Compatibility routes

## Worker Contract

The retired versioned typed commands, events, ordering, cancellation, limits,
errors, and capabilities that the Host exchanged with the managed C# Runtime
Worker over `/worker/v2`. In-process model steps are typed calls, and no
private transport remains: tool execution is in process as well (ADR-0012,
ADR-0014).
_Avoid_: Runtime HTTP relay

## Approval flow

`AgentApprovalsService` orchestrates a decision and derives no policy. It reads the command
(`allow_once`, `allow_session_directory`, `allow_always` or `deny`), then:

1. Checks the approval against durable state: the run exists and is `WaitingApproval`, and the
   approval id is the run's latest persisted `approval.requested` and is unexpired. Otherwise the reply
   is `409 approval_stale`. The in-process first-writer ledger only guards concurrent HTTP retries.
2. For an allow with a configured execution authority, probes the Broker without consuming the
   approval. An approval the Broker no longer holds as pending for the run returns `409 approval_stale`,
   and nothing is committed. `allow_always` on a tool class the Broker does not remember returns
   `400 invalid_approval_command`, also before any commit. A deny skips the probe, so a run whose Broker
   approval has expired can still be ended.
3. Commits the durable `approval_resolved` transition.
4. Asks the Broker to consume the approval. The command follows the pending kind: an outside folder
   always becomes `allow_session_directory`, otherwise `allow_always` or `allow_once`. The Broker
   returns the authorization token and the remember plan from that same call. Without a token the reply
   is `502 approval_authorization_failed`, and the committed resolution stands.
5. Only after authorization: an `allow_always` with a durable matcher saves a project-scope allow rule.
   A failed save leaves `rule_saved: false` and the session matcher is still remembered. The Host
   remembers the saved durable matcher, or else the plan's session matcher, in the in-process session
   ledger. An outside-folder approval writes no ledger entry.

With no configured execution authority, an allow returns `503 broker_gateway_unavailable` before any commit. `allow_session_directory` on an approval that is not for an outside folder returns `400 invalid_approval_command`, also before any commit.

The reply carries `authorization_token` (60 seconds), a `matcher_preview` of the matcher actually
remembered, `rule_saved`, `reusable_rule_not_saved` and `rule_id`. The matchers come from the Broker
([Remembered Approval](broker.md#remembered-approval)). The Host copies them into its own matcher type
and does not parse tool arguments.

## Run Event Feed

One client-side SSE subscription path owns frame dispatch and bounded reconnect
for WinUI and CLI. Durable event IDs advance the resume cursor; transient frames
remain outside durable history. WinUI keeps its cumulative reconnect retry
bound, while CLI progress can refill its retry budget. Consumer callback errors
propagate and are not retried as transport failures.
_Avoid_: replaying transient frames as durable history or hiding callback errors

## Multimodal Image Payload

A client-attached image (`image/png`, `image/jpeg`, `image/webp`) resized to at
most 2048px on client, validated against strict MIME and size bounds (max 4
images, 4 MB decoded per image, 20 MB total decoded), persisted atomically in
SQLite `agent_payloads` under kind `UserImage` (value 11) with dual audience
`AuthenticatedClient | ModelContext`, and projected to vision-capable model
providers through the typed model step and `ModelRequest.Images`.
_Avoid_: Unbounded base64 uploads, client-side raw file passthrough

## Protected User Data

Settings, sessions and their message history, activity, credentials, and
reviewed examples used by the active product. Business records live under
`%LOCALAPPDATA%\Ru`, and `RU_DATA_ROOT` isolates them for tests.
_Avoid_: Cleanup candidates, generated data

## Retired Local Model Assets

Source, configs, scripts, datasets, checkpoints, evaluations, and documents
whose only purpose was in-process conversational-model generation or training.
_Avoid_: Reintroducing local model or training dependencies
