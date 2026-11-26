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

Session lists use `SessionWorkspaceResolver` to resolve the latest durable run's
encrypted workspace metadata. Sessions without a readable workspace retain an
unset working directory. External session import/export routes and format
adapters are retired; native Ru fork, rewind and retained history remain.
Run-list entries expose the same safe pinned provider, model, protocol, context
window and tool-profile metadata as individual run reads. One bounded batch
reads selections for the returned page of at most 100 runs. Missing or unpinned
historical selections retain empty metadata. Both query paths share their public
projection, and saved context usage follows the run's pin across later settings
changes. Credential accounts, endpoint addresses and raw request options remain
inside the Host.
Both run-create routes (`/agent/v1/runs` and `/agent/v1/runs/selected`) use the
larger run body bound; only the selected route's path check used to fall back to
the general 8 MiB cap.

Workspace change restore first checks the expected hash, then validates it again
after journal decryption. Existing targets are hashed and rewritten through one
write-excluding handle, with bounded original bytes retained for rollback on an
I/O failure. Missing targets use the all-zero SHA marker and an exclusive
no-overwrite move; an intervening creation is stale. Empty files use SHA-256 of
empty bytes. A target changed since the expected hash returns
`workspace_restore_stale`. These guards do not provide crash or power-loss atomicity.

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

## Hooks

The Host starts user-configured hook commands itself: `HookToolGateway` decorates
the tool gateway for `PreToolUse`/`PostToolUse`, and `RunLifecycleHooks` runs
`UserPromptSubmit` and `Stop`. `HookProcessRunner` launches each one through
`cmd.exe` with the credential-stripped terminal environment, a hard timeout and a
kill-on-close Job Object. These are commands the user (or an approved plugin,
[ADR 0044](../adr/0044-user-plugins.md)) declared, not model-visible tools: they
never pass through `IToolGateway` policy evaluation and add no tool, grant or
approval. A workspace `hooks.json` loads only after workspace trust, and never in a `plan` run; a run without workspace hooks starts the user's hooks in the profile folder. This is the
one process launch the Host owns outside the execution authority; model-requested
file, terminal and network actions stay in the Broker. Behaviour and limits:
[Hooks](../operations/agent-tools.md#hooks).

## Contracts and transport

The canonical Public Contract is
[`backend-dotnet/public-route-contract.json`](../../backend-dotnet/public-route-contract.json):
59 HTTP method/path pairs with no WebSocket route (35 `/agent/v1/*` and 24
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
ordered native calls; a call written out as text is salvaged when it names an
advertised tool.

Text turns are authenticated Agent runs consumed through the durable Agent SSE
stream. There is no public WebSocket and no second text-generation transport.

External tools get a local-only stdio surface: `ru agent rpc` speaks JSON-RPC
2.0 over stdin/stdout, resolving the same `runtime_descriptor.json` and bearer
token and reusing the same HTTP/SSE clients as the CLI. It adds no HTTP route
and opens no transport beyond stdio; the TypeScript SDK at
[`sdk/ru-agent`](../../sdk/ru-agent/README.md) wraps the bridge with typed
host-status, session, run, subagent and policy handles.

### Multimodal image attachments

The Agent harness supports end-to-end multimodal image input across WinUI 3, ASP.NET Core Public Host, the in-process model step, and LLM providers:
- **Client preparation:** WinUI 3 `ImageAttachmentResizer` accepts a source image file up to 100 MB, rejects a source canvas above 64 megapixels, scales pixels exceeding 2576px on their longest dimension down proportionally, and compresses them to JPEG (quality 0.85) or PNG (if alpha present) with a 4 MiB encoded bound; when the first encode exceeds that bound it retries with lower quality and then a smaller long edge instead of rejecting a valid photo. The larger input cap decides only what the client may read and decode; the normalized image is still bounded by the client's 2576 px long edge and the 4 MiB wire bound enforced by the Host, payload store and provider adapters. Up to 10 images can be attached per user turn or steer (ADR 0056). The CLI mirrors the same input cap in `CliImagePolicy`.
- **Backend validation & persistence:** `POST /agent/v1/runs` has a 24 MiB JSON request-body bound for encoded `image_attachments` data URLs, distinct from decoded image bounds. ASP.NET Core validates MIME types (`image/png`, `image/jpeg`, `image/webp`), canonical decodable Base64 (including padding and trailing bits), actual decoded per-image size (<=4 MiB), total decoded size (<=16 MiB), and retains valid images even when the selected model accepts only text. Validation is bounded before allocation and is repeated by `ModelImage` at the adapter boundary. Image data URLs are persisted as AES-GCM encrypted `agent_payloads` of kind `UserImage` (value 11) with audience `AuthenticatedClient | ModelContext`. `GET /agent/v1/runs/{runId}` lists them in `image_payloads`, each with the steer that brought it (null for the run's own prompt), and the payload route returns an image up to the 5.6M-character data URL bound so a reopened chat can show it. Prompts may be empty when images are attached.
  An empty `workspace_path` is also valid after the session is verified to exist: the Host allocates its scratch workspace under the data root (`workspaces/<session id>`). A folderless chat that attaches a file before its first run asks `POST /sessions/{sessionId}/workspace/scratch` for the same Host-owned path, so the client can stage the file there; the run then sends that path and still gets `workspace_kind: "scratch"` from the ownership marker. Existing directories require a matching ownership marker; the Host does not adopt unmarked or foreign folders. Because the agent's own file tools can rewrite anything inside the workspace, including that marker, each allocation is also recorded outside it (`workspaces/.owners/<session id>.json`, naming the session and the exact workspace path): allocation accepts a folder either witness proves, repairs the lost one, and still refuses a folder neither claims or a marker naming another session. A path a client echoes back is resolved through the session's recorded allocation before classification, so the repair happens first. Run responses and session listings return `workspace_kind: "scratch"` only when the canonical workspace matches the same session's marked non-reparse directory. A session delete removes that folder only after successful or confirmed-absent primary deletion, and a startup sweeper reclaims folders whose session is gone. A folder without any witness is never deleted, and a failed allocation answers `scratch_workspace_unavailable` with bounded copy naming the cause rather than a raw response body ([ADR 0041](../adr/0041-scratch-workspaces.md), [ADR 0054](../adr/0054-folderless-attachments-use-scratch-workspace.md)).
- **Model step integration:** the typed `ModelGenerationRequest.Images` carries decrypted `UserImage` payloads. `ModelStepService` projects these into `ModelRequest.Images` (`IReadOnlyList<ModelImage>`).
- **Provider projection:** Model adapters project images into provider-native payloads:
  - OpenAI Responses: `{"type": "input_image", "image_url": "data:image/..."}`
  - Anthropic Messages: `{"type": "image", "source": {"type": "base64", "media_type": mime, "data": b64}}`
  - OpenAI-Compatible: `{"type": "image_url", "image_url": {"url": "data:image/..."}}`
  Text-only pinned models receive a textual attachment notice instead of image wire parts, allowing the chat to continue and the model to explain that it cannot view the image. Stored images remain available to the authenticated client; MIME/size limits and payload-read failures remain enforced ([ADR 0040](../adr/0040-text-only-image-attachments.md)).
  Follow-up context preserves image receipt facts for retained user messages,
  including image-only turns, and a vision-capable follow-up re-attaches the most
  recent visible conversation images (newest turn first, within the request's image bound —
  ten for a hosted provider, four for one on this machine or the local network — and 16 MiB
  decoded in total) as visual reference for
  those earlier turns ([ADR 0051](../adr/0051-reattach-recent-session-images.md)).
  A bounded prompt note names the re-attached count and says they are earlier
  context, not attachments of the current message. The prompt environment
  declares the pinned model's image capability on every turn. Receipt facts
  contain no image bytes, rewound or pruned history is not resurrected, and
  reading an earlier image never fails the run.
- **Workspace images through `read`:** the agent can also look at an image file in the workspace ([ADR 0026](../adr/0026-read-returns-workspace-images.md)). The run's pinned selection records whether its model accepts image input; the in-process gateway sends that decision on the `tool.execute` envelope, and the Broker returns the image beside small JSON metadata or fails with `image_unsupported_by_model`. The pipeline stores it as a model-only, opaque, encrypted `ToolImage` payload (kind 18) whose id is derived from the tool result's payload id, so no event or context row names it. `SqliteModelContextSource` attaches the newest four to their tool results; older results carry an omitted note. See [Providers](providers.md#tool-images) for the per-protocol projection.



## Public Host

The ASP.NET Core process that exclusively owns RuApp's authenticated HTTP and
SSE contract and, when launched as `RuBackend.exe --managed-runtime`, runs
Agent model generation and the tool execution authority in process (ADR-0012,
ADR-0014; RuApp/CLI start-or-attach via descriptor).
_Avoid_: Edge, proxy, FastAPI backend

## Public Contract

The stable authenticated surface of HTTP method/path pairs with no WebSocket
route (24 non-Agent plus 35 `/agent/v1/*`; Agent v2 is
the current generation path at `/agent/v1/*`). Text turns use
the authenticated Agent HTTP/SSE path; there is no public Chat transport. An
external tool drives the same surface over stdio through the local JSON-RPC
bridge (`ru agent rpc`), wrapped by the TypeScript SDK at
[`sdk/ru-agent`](../../sdk/ru-agent/README.md); the bridge adds no HTTP route and
opens no transport beyond stdin/stdout.
_Avoid_: Compatibility routes

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

Live text wakes drain the transient queue without reloading run state for every
chunk. Activity boundaries and explicit run-change notifications trigger an
immediate check. An independent monotonic clock retains the 100 ms active and
one-second client-wait fallback, so continuous text cannot postpone a silent
durable transition. The durable tail is queried only when the run watermark
advances beyond the connection cursor.

Replay and usage seeding read at most 128 events per batch and close the store
iterator before payload enrichment or network writes. SSE frames are serialized
once into the response pipe; transient drains use batches of at most 32 frames.
Control/state boundaries and final snapshots flush promptly. A terminal run
drains a bounded snapshot of its queued frames in publication order before
closing, so a late producer cannot hold the stream open indefinitely.

Protocol v1 durable envelopes expose optional `model_step_id`, copied from the
stored event when present. It correlates per-request context usage with transient
request estimates without comparing durable and connection-local transient cursors.
Old envelopes may omit it; old clients ignore the added field. Envelope token counters
remain run-cumulative billing totals; request occupancy comes from the referenced
`modelusage` payload (`json` encoding), not those totals.
_Avoid_: replaying transient frames as durable history or hiding callback errors

## Activity replay

Activity replay reads event metadata to recover proposal/receipt links, then
rebuilds one page of activity rows from newest to oldest. `GET
/agent/v1/runs/{runId}/activities` answers the newest page (500 rows by default);
`before_sequence=<n>` answers the page just older than durable sequence `n`, and
`limit` (1..500) bounds a page. Each response carries `has_more`,
`next_before_sequence` and `older_count`, so a client walks back page by page and
nothing is cut for good. A page takes a candidate whole (a `todo_write` completion
keeps both its rows) and its cursor moves past every candidate it considered,
including one that could not be rebuilt. The newest page leads with the latest
checklist even when it is older than the page, as an extra row; that row appears
again on its own older page, and clients keep one copy by activity id. Reasoning
payloads are decrypted only for retained thinking rows. Once the tail is full,
older successful tool proposals are inspected for the latest valid checklist;
other tool results are skipped. Checklist clears remain meaningful, and an
unreadable or malformed update preserves the preceding valid snapshot.

The metadata scan remains necessary for historical runs whose tool identity lives
inside encrypted arguments. Narration is read per page, for the sequence window
between the page's oldest considered row and its cursor (the oldest page also takes
everything before its first row), with a per-page budget of 512 segments and 524,288
characters and the same chronological merge; over budget drops only that page's
narration. Payload audience checks, read failures,
live/interrupted tool state and durable sequence ordering keep their existing
behavior. No decrypted replay cache is retained between requests.

## Multimodal Image Payload

A client-attached image (`image/png`, `image/jpeg`, `image/webp`) resized to at
most 2576px on client, validated against strict MIME and size bounds (max 4
images, 4 MB decoded per image, 16 MB total decoded), persisted atomically in
SQLite `agent_payloads` under kind `UserImage` (value 11) with dual audience
`AuthenticatedClient | ModelContext`, and projected to vision-capable model
providers through the typed model step and `ModelRequest.Images`. Text-only models receive an explicit unavailable-image notice while the original payload remains stored.
_Avoid_: Unbounded base64 uploads, client-side raw file passthrough

## Protected User Data

Settings, sessions and their message history, activity, credentials, and
reviewed examples used by the active product. Business records live under
`%LOCALAPPDATA%\Ru`, and `RU_DATA_ROOT` isolates them for tests.
_Avoid_: Cleanup candidates, generated data
