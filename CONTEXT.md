# Ru domain language

Ru is a native Windows coding agent. This glossary owns definitions and authority boundaries; component pages own implementation details, limits and lifecycle ordering.

Distribution and setup: [Windows packages](docs/operations/distribution-and-updates.md). Architecture and ownership: [system overview](docs/architecture.md).

## Language

**File Preview**:
Read-only desktop presentation of a workspace file selected by the user. Local
renderers or installed Windows document preview handlers own its display; this
surface adds no model tool, execution permission or document-upload service.
Implementation: [GUI file preview](docs/architecture/gui.md#file-preview).
_Avoid_: document editor, model execution authority, cloud Office viewer


**Public Host**:
The authenticated loopback ASP.NET Core process that owns orchestration, persistence, in-process model generation and composition of the execution authority.
Implementation: [host](docs/architecture/host.md#public-host).
_Avoid_: Edge, proxy, FastAPI backend


**Runtime Worker**:
The retired private model-generation process. Current model steps run inside the Public Host; no worker transport is active.
Implementation: [host](docs/architecture/host.md#runtime-worker).
_Avoid_: Python backend, upstream server


**Generation Kernel**:
The module owning pinned provider selection, request projection, generation and cancellation for a model step.
Implementation: [providers](docs/architecture/providers.md#generation-kernel).
_Avoid_: ModelRunner globals, backend core


**Public Contract**:
The canonical authenticated HTTP/SSE routes declared in `backend-dotnet/public-route-contract.json`, shared by Desktop, CLI and the stdio bridge. No WebSocket or second chat-generation transport exists.
Implementation: [host](docs/architecture/host.md#public-contract).
_Avoid_: Compatibility routes


**Worker Contract**:
The retired Host-to-worker wire protocol. Current model steps use typed in-process requests and results.
Implementation: [host](docs/architecture/host.md#worker-contract).
_Avoid_: Runtime HTTP relay


**Agent Session**:
A session scope for presets, permissions, grants and skill use. Its preset does not grant workspace trust.
Implementation: [broker](docs/architecture/broker.md#agent-session).
_Avoid_: permanent Agent mode permission or preset-implied trust


**Conversation Session**:
The Host-owned conversation and message/source history. A view may detach or switch sessions without cancelling Host work; only a confirmed application-level delete removes its artifacts.
Implementation: [gui](docs/architecture/gui.md#conversation-session).
_Avoid_: treating an HTTP 200 response as a successful delete


**Conversation Branch**:
A new conversation identity copied from a selected whole-turn prefix. The source
conversation remains intact. Branches use current workspace files and do not
inherit execution grants, approvals, active runs or workflow authority.
Implementation: [TUI history](docs/architecture/tui.md).
_Avoid_: file checkpoint, Git branch, restored execution state


**Session Work State**:
A bounded encrypted workflow record for one conversation and canonical workspace identity, linking intent, tasks and receipt-backed observations. It supplies context, never authority or proof of current file state.
Implementation: [harness](docs/architecture/harness.md#session-work-state).
_Avoid_: global memory, cross-session authority, model prose as proof, or work state as permission


**Session Run Handoff**:
A bounded encrypted snapshot of earlier model-visible root-run evidence in the same conversation/workspace, eligible only while its user messages remain visible. Historical calls are quoted context, never replayed execution or inherited approval.
Implementation: [harness](docs/architecture/harness.md#session-run-handoff).
_Avoid_: complete memory, cross-session history, inherited approvals, automatic re-execution of unfinished calls


**RuAgentBroker**:
The in-process execution authority class library composed as `BrokerExecutionRuntime` behind `IToolGatewayFactory`. It validates policy and executes bounded tools without a public transport.
Implementation: [broker](docs/architecture/broker.md#ruagentbroker).
_Avoid_: public API route, unrestricted shell, separate broker process


**Context Checkpoint**:
A bounded, encrypted summary replacing selected model-context history while
preserving its covered ranges and original durable evidence. Provider-backed
checkpoints use the run's pinned model and consume a model-call budget; they
never transfer approvals or turn quoted instructions into execution authority.
Implementation: [context management](docs/architecture/context-management.md).
_Avoid_: complete memory, verified success inferred from model prose, inherited approval


**Execution Authority**:
The single path through `IToolGateway` to `BrokerExecutionRuntime` that can perform model-visible file, terminal and network actions. Host orchestration intercepts `ask_user` without dispatching it to that authority.
Implementation: [broker](docs/architecture/broker.md#execution-authority).
_Avoid_: dual execution paths, HarnessToolGateway


**Tool Manifest**:
The versioned, digested declaration of executable Broker bindings. Identity or digest mismatch refuses authority initialization.
Implementation: [broker](docs/architecture/broker.md#tool-manifest).
_Avoid_: dynamically constructed tool ID


**Tool Definition**:
The Protocol-owned model-visible schema and metadata for one tool. Advertisement does not grant execution permission.
Implementation: [broker](docs/architecture/broker.md#tool-definition).
_Avoid_: treating a Broker manifest or adapter list as the model contract


**Tool Output Projection**:
The bounded, paged representation of a durable tool result supplied to model context and clients. Presentation excerpts preserve receipt provenance without claiming to contain the complete result.
Implementation: [harness](docs/architecture/harness.md#tool-output-projection).
_Avoid_: treating projected output as a new receipt, complete history, or authority


**Skill Manifest**:
A typed declaration for an executable skill workflow, distinct from untrusted Markdown instructions. Broker policy remains the execution authority.
Implementation: [broker](docs/architecture/broker.md#skill-manifest).
_Avoid_: executable plugin or self-modifying workflow


**MCP Server**:
A stdio Model Context Protocol program the user declares in their own `~/.ru/mcp.json` and the Broker runs on first use, reached by the model through the single `mcp` tool. Starting it or calling one of its tools has terminal-launch authority outside the workspace (`ExternalTerminal`); its output is untrusted and grants nothing.
Implementation: [agent tools](docs/operations/agent-tools.md#mcp-servers), [ADR 0025](docs/adr/0025-mcp-servers-through-the-broker.md).
_Avoid_: workspace-declared servers, a per-server tool definition, or a second execution authority

**Subagent**:
A delegated internal run owned and controlled by the main agent, with bounded family resources and isolated child-private history. Users can observe/export it; only the main agent sends work, cancels or applies results.
Implementation: [harness](docs/architecture/harness.md#subagent).
_Avoid_: a new execution authority, a user conversation, or an OS sandbox


**Markdown Skill**:
A discovered `SKILL.md` package supplying bounded untrusted workflow guidance. Metadata and referenced files never grant execution authority or run automatically.
Implementation: [harness](docs/architecture/harness.md#markdown-skill).
_Avoid_: treating `SKILL.md` as a permission manifest or executable plugin


**Session Grant**:
A Broker-checked set of scopes and skill identities bound to one Agent Session, without arbitrary filesystem, network or credential authority.
Implementation: [broker](docs/architecture/broker.md#session-grant).
_Avoid_: global allowlist


**Capability Profile**:
The session-scoped preset projected from the immutable Protocol policy catalog. `plan` is read-only, `standard` uses scoped launch/path authority, and `full_access` allows structured protected paths as ordinary targets; explicit rules and hard-invalid denials still apply.
Implementation: [broker](docs/architecture/broker.md#capability-profile).
_Avoid_: permission inferred from prompt text, repository cleanliness, or a client-local preset table


**Native Tool Batch**:
An ordered group of native calls from one model step, with optional narration. Retained approval/input work resumes the original batch through re-evaluation, never a replacement model proposal.
Implementation: [harness](docs/architecture/harness.md#native-tool-batch).
_Avoid_: one-call-per-step rule, reissuing approved calls


**Recovery Note**:
Durable bounded feedback asking the same pinned model to recover from an eligible failed or incomplete step. It cannot soften an approval decision, reconciliation requirement or execution-policy denial.
Implementation: [harness](docs/architecture/harness.md#recovery-note).
_Avoid_: fallback answer, substitute model, retry on another backend


**Receipt-backed Verification**:
A bounded projection of original run-local terminal proposals and execution receipts.
Declared required checks remain outstanding through unrelated reads, compaction
and restart. Exploratory failures remain failed receipts without becoming mandatory
checks; known run-local mutations invalidate passed required checks
and pending checks that would certify older code. New terminal receipts identify
the canonical executable/argv/cwd; this fingerprint never grants authority. Explicit
expected nonzero exits can satisfy a negative test without rewriting its failed
receipt. Intent metadata grants no authority and does not prove complete correctness.
Implementation: [harness](docs/architecture/harness.md#receipt-backed-verification).
_Avoid_: treating a successful inspection, summary or model claim as a passed check


**Repeated Tool Recovery**:
A bounded repetition guard suppressing duplicate dispatch and asking the same model to change its proposal. It cannot turn a failed receipt into successful verification.
Implementation: [harness](docs/architecture/harness.md#repeated-tool-recovery).
_Avoid_: treating suppressed calls as executed or verified


**Agent Persona Source**:
The selected workspace/user instruction sources governing conversational style and workflow. Instructions remain untrusted for execution policy.
Implementation: [harness](docs/architecture/harness.md#agent-persona-source).
_Avoid_: embedded fallback persona


**Approval Token**:
A short-lived single-use authorization bound to exact canonical arguments and policy identity. Retained proposals are re-evaluated before execution; a token never transfers to a new request or run.
Implementation: [broker](docs/architecture/broker.md#approval-token).
_Avoid_: reusable confirmation flag or approval-time reissue


**Session Approval Memory**:
A bounded in-process ledger of approved intent for one Agent Session. It remains subject to Broker evaluation and protected-path policy and expires on session/Host lifetime boundaries.
Implementation: [broker](docs/architecture/broker.md#session-approval-memory).
_Avoid_: global approval cache, persisted permission


**Run Provider Selection**:
The durable secret-free provider/model/protocol and request-option pin for one run. Reopening retains that choice; user selection changes apply to a new run.
Implementation: [providers](docs/architecture/providers.md#run-provider-selection).
_Avoid_: current settings as restart fallback


**Provider Continuation**:
Encrypted internal provider-native state bound to an exact run, model step and native call batch. It is excluded from visible history, clients, portable handoffs and telemetry.
Implementation: [providers](docs/architecture/providers.md#provider-continuation).
_Avoid_: hidden reasoning in chat history or reconstructed prose


**Runtime Context Snapshot**:
A bounded, encrypted model-only record of current runtime and workflow context, admitted before a prepared native text model step. Retained snapshots preserve their historical position; new or cleared state is appended and never grants authority.
Implementation: [providers](docs/architecture/providers.md#runtime-context-snapshots).
_Avoid_: mutable context rewritten ahead of retained history or historical observations treated as current permissions


**Policy Snapshot**:
An immutable versioned canonical binding of preset, workspace identity and ordered rules, validated by the execution authority. A new version invalidates stale pending decisions.
Implementation: [broker](docs/architecture/broker.md#policy-snapshot).
_Avoid_: mutable rule set


**Policy Rule**:
A scoped allow/ask/deny record with stable identity, canonical matcher and ordering. Sensitive matcher values are encrypted at rest; repository instructions do not create rules.
Implementation: [broker](docs/architecture/broker.md#policy-rule).
_Avoid_: repository-stored authority


**Workspace Trust**:
An explicit decision bound to the canonical Windows directory identity, independent of preset or repository cleanliness. Recreated or moved identities require fresh trust.
Implementation: [broker](docs/architecture/broker.md#workspace-trust).
_Avoid_: repository-marker, path-only, or preset-implied trust


**Workspace Terminal**:
A policy-authorized executable or shell launched under the current Windows user, bounded by timeout, output, cancellation and process-tree cleanup. Launch-directory permission is not OS filesystem/network confinement.
Implementation: [broker](docs/architecture/broker.md#workspace-terminal).
_Avoid_: treating repository cleanliness as authorization or terminal approvals as OS confinement


**Run Event Feed**:
The shared client subscription and reconnect path for durable SSE events and transient frames. Only durable IDs advance the resume cursor, and consumer failures remain observable.
Implementation: [host](docs/architecture/host.md#run-event-feed).
_Avoid_: replaying transient frames as durable history or hiding callback errors


**Session Side Surface**:
The GUI session summary and detail surfaces for edit history, processes, subagents and a user-owned terminal. Presentation state never changes workspace identity or execution permission.
Implementation: [gui](docs/architecture/gui.md#session-side-surface).
_Avoid_: treating the background-task list as the agent's todo plan


**Turn Change Summary**:
The read-only card beneath a settled assistant turn, grouping its completed edit
activities by recorded file path. Line totals sum recorded edits rather than a
net Git diff. The reader preserves each edit's before/after preview; missing
evidence remains unavailable. It never modifies files or grants authority.
Implementation: [GUI turn summary](docs/architecture/gui.md#turn-change-summary).
_Avoid_: mixing session-wide edits or current filesystem content into an older turn.


**Multimodal Image Payload**:
A validated bounded client image persisted encrypted for authenticated-client and model-context audiences, then projected to a vision-capable provider. Unsupported models fail by name.
Implementation: [host](docs/architecture/host.md#multimodal-image-payload).
_Avoid_: Unbounded base64 uploads, client-side raw file passthrough


**Tool Image**:
A workspace PNG, JPEG or WebP image that `read` returned beside its JSON metadata, persisted encrypted for the model-context audience only and attached to its tool result in later requests of the same run. Only a run whose pinned model accepts image input receives one; otherwise the read fails by name.
Implementation: [host](docs/architecture/host.md#multimodal-image-attachments), [ADR 0026](docs/adr/0026-read-returns-workspace-images.md).
_Avoid_: Returning image bytes as text, server-side resizing, silently dropping an image from a request


**Core TUI**:
The compact Terminal.Gui coding surface for chat, tool activity, approvals, session/model selection and recovery. Headless CLI/RPC capabilities are independent of its presentation scope.
Implementation: [tui](docs/architecture/tui.md#core-tui).
_Avoid_: Requiring GUI feature parity or treating presentation metadata as authority.


**Protected User Data**:
Settings, conversation history, activity, credentials and other active product records. Generated-output cleanup never authorizes removing these records.
Implementation: [host](docs/architecture/host.md#protected-user-data).
_Avoid_: Cleanup candidates, generated data


**Retired Local Model Assets**:
Artifacts whose sole purpose was the removed in-process conversational-model/training stack. Their retirement does not permit deleting active user data.
Implementation: [host](docs/architecture/host.md#retired-local-model-assets).
_Avoid_: Reintroducing local model or training dependencies
