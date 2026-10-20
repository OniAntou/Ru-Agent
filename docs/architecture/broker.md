# Broker implementation

Current reference. [System ownership](../architecture.md) · [Domain glossary](../../CONTEXT.md) · [Tool policy](../operations/agent-tools.md).

## Agent autonomy and trust boundary

The immutable profile catalog in `RuAgent.Protocol` is the single semantic
source for the effective-policy prompt projection and Broker fallback:

| Preset | Canonical behavior |
| --- | --- |
| `plan` | Workspace read and Web are automatic; terminal and mutation are denied outright (read-only, no per-change asks); external structured access asks like Standard. |
| `standard` | Workspace reads, Web, workspace mutations and executable/shell launches from a workspace cwd are automatic; external structured access and external terminal cwd ask. Explicit rules and long foreground approval still apply. |
| `full_access` | The skip-all mode: external structured files, arbitrary terminal, and structured protected paths (harness key/database files and runtime descriptors, plus user-added `RU_PROTECTED_PATHS` entries) are automatic; hard-invalid requests remain denied. |

These are preset defaults. Explicit deny and ask rules still take precedence,
including for external files covered by a session folder grant or Full Access.
Hard invariants, including Plan's read-only gate, remain ahead of ask rules.

Preset selection is session-scoped. Workspace trust is a separate explicit
decision, once per canonical `WorkspaceIdentity`; the GUI presents a guarded
confirmation and CLI `run`/`chat` require `--trust-workspace`. A moved or
recreated identity must be trusted again. Full Access requires the GUI gate or
CLI `--confirm-full-access`, and neither implies workspace trust.

On Windows, trust resolution requires a real directory, its native final path,
volume serial and file ID. Query-only handles avoid requiring directory listing
access. An unverifiable native identity fails with `workspace_identity_unverified`
inside the resolver; a path hash never substitutes for Windows trust identity.

A native model step may contain optional narration and one or more ordered tool
calls. If any call asks, the complete non-denied batch is retained durably and
resumes by re-evaluation without a new model proposal. Read-only executions
may overlap up to eight at a time while mutation/terminal barriers and receipt
order remain intact.

`ToolArgumentsCodec` owns the durable tool-call envelope serialization and
interpretation. Retained input and approval recovery share exact identity and
argument matching; HTTP approval binding keeps its existing client-audience
and legacy compatibility checks. Codec sharing does not move policy decisions
or tool execution out of their existing owners.

Terminal children in `standard` and `full_access` run as the current Windows
user and may access files outside the workspace and network resources available
to that user; there
is no OS filesystem or network sandbox (ADR-0015). Standard authorizes the canonical launch directory rather than command syntax
(ADR-0017); Full Access also permits external launch directories. The `bash` schema rejects obsolete sandbox arguments. Hard
timeout, output cap, cancellation, redaction, and process-tree cleanup remain
active. Structured file tools independently retain canonical/symlink/junction/
hard-link containment, and the protected-path denial holds in `plan` and
`standard` (`full_access` is literally full). API keys remain in credential
storage and diagnostics redact secrets.

The current managed process flow (Phase 2 cutover) is:

```text
RuApp / RuAgent.Cli  --start-or-attach via RuAgent.Client-->  ASP.NET Core Public Host 127.0.0.1:<ephemeral>
                                                                (RuBackend.exe --managed-runtime, one process, no managed child)
                                                                  |-- direct business, persistence, and durable Harness (Core + SQLite agent_harness.db + encrypted payloads)
                                                                  |-- in-process typed model step (ModelStepService behind IModelStepRunner, one-step AssistantStep)
                                                                  |     `-- bounded provider adapters (OpenAI-compatible Chat Completions, OpenAI Responses, Anthropic Messages) via exact protocol dispatch
                                                                  `-- in-process execution authority (BrokerExecutionRuntime behind IToolGatewayFactory)
                                                                        |-- policy, approvals, and local action adapters (structured files)
                                                                        `-- current-user terminal/git children, bounded by Job Object and policy
```

The public Agent path uses the in-process typed model runner. Durable policy/file-tool work is complete, with headless terminal and
reconciliation surfaces from Phase 3:

- In-process model runner: one typed `ModelGenerationRequest` → one provider generation → `AssistantStep(text, tool_calls, finish_reason)`. A native provider may return optional narration plus one or more ordered calls; a text-selection provider still emits exactly one JSON object with no surrounding selection prose. Provider errors map to bounded stable codes without provider/model fallback. The runner composes one system message with composer `2.0.0` from seven ordered blocks: persona, execution contract, environment, profile/effective policy, instruction blocks, capabilities, and runtime context. It returns a receipt whose version, block ids/order, and per-instruction digests Core validates before completion or tool dispatch. What that receipt attests is bounded: the `instruction.*` digests bind the exact instruction bytes in the context, while the other block digests are checked for shape and the caller does not receive the rendered prompt text. The typed step result carries text, tool calls, finish reason, and the prompt receipt, plus optional bounded `usage` and `reasoning_text`.
- Native capability listings summarize the first sentence; complete descriptions, schemas and argument examples travel in declarations for Chat Completions, Responses and Anthropic. Legacy text selection receives full descriptions, schemas and examples in the system prompt and does not advertise native functions. The environment date is converted to the advertised local timezone. Native runtime references omit only tool outputs already present in retained native history; unmatched references and paging notices remain available. Protocol schemas may carry string-valued `description` annotations, included in their canonical digests; Broker treats these as guidance metadata and retains required/type/enum/bounds/additional-property checks and rejection of unsupported validation keywords. An annotation never grants execution authority.
- Agent API/SSE: 25 `/agent/v1/*` routes plus durable `HarnessEvent` SSE (`id:<sequence>`, `Last-Event-ID`/`after_sequence` cursors, reconnect replay, transient non-durable deltas) with bearer auth, idempotency 409, `workspace_busy` 409, `preset_mismatch` 409, typed command dispatch (`/compact`, `/help`, `/skills`, `/skill`, `/tasks [kill <execution_id>]`) without history insertion, active `GET`/`POST`/`DELETE /agent/v1/policies` (global requires `X-Interactive-Confirmed: true`, versioned JSON, encrypted matcher values) plus the stored session-preset read (`GET /agent/v1/sessions/{sessionId}/preset`, 404 when the session has no stored selection), workspace concurrency/activity/changes reads and policy update, continuously renewed workspace membership while a run is driven, encrypted external-change journal entries, and `POST /agent/v1/runs/{runId}/approvals/{approvalId}` (exactly `allow_once`, `allow_session_directory` for external folders only, `allow_always` which additionally remembers a bounded pattern as a durable project rule — a terminal command prefix or an approved external folder's canonical path pattern — or `deny`; any allow also remembers the approved intent for the Agent session in an in-process, expiring ledger that the runtime loop materializes into the session's later policy snapshots — the durable pattern when one was saved, the exact intent otherwise, and external-directory approvals keep the Broker folder grant and write no entry — so a matching call — including later in the same run — evaluates as allowed without another prompt; first-writer-wins `approval_stale`, matcher preview, 30-min expiry, old-snapshot execution ordering, explicit rules deny before ask before allow); `reconcile` is active on the Agent v2 headless path (Phase 3). Different sessions always share a workspace; a non-terminal parked run (`waiting_user`, `reconciliation_required`) retains its session ownership until the client continues or reconciles it, without blocking other sessions; after restart the startup recovery sweep re-registers `ReconciliationRequired` runtime roots so a later Continue actually resumes the run (the drive loop starts `Running` runs and rehydrates `WaitingApproval` ones). Exact consecutive tool calls terminate as `repeated_tool_call` before another Broker dispatch. Ordinary assistant prose immediately following any failed or unverified tool receipt is suppressed from conversation outbox, recorded internally for audit as an `InternalOnly` payload, and answered with a bounded durable `[HARNESS RECOVERY]` note in the model context so the run can redo, reroute, or report the blocker; a final report explicitly prefixed `[BLOCKED]` is stored as visible assistant history with the marker removed and ends the run as failed, preserving the receipt error code (or `model_reported_blocker` when no failed receipt exists), without another recovery note; terminal outbox delivery does not recreate runtime or transition the run to success. Otherwise, after the case budget (4 unverified notes) or the global cap (24 notes per run) is spent the run fails as before with the tool error code. An empty or whitespace-only final step is answered the same way up to 3 notes before failing as `empty_model_reply`; a `Length` finish suppresses the whole incomplete step before tool dispatch and gets up to 3 consecutive same-model notes requesting smaller output before `model_output_truncated`; `ContentFilter` and `Error` finishes fail immediately as `model_content_filtered` and `model_reply_error`; a model step the runner rejects with a repairable tool-call/selection code (`model_tool_call_invalid`, `model_tool_call_unavailable`, `invalid_selection`) is answered the same way up to 4 notes before failing with the original code, while a provider-transient code whose bounded retries were spent (`provider_rate_limited`, `provider_timeout`, `provider_server_error`, `provider_transport_error`) parks the run in `waiting_user` with a `ContinueRequested` naming the provider code so an explicit Continue retries a fresh step, and the remaining provider/prompt/history/image codes still fail immediately. Subsequent different recovery tool calls remain allowed.
- Broker v2: immutable `PolicySnapshot` and `BrokerRunLease` bind the run, session, canonical workspace identity, profile version, preset, and SHA-256 snapshot before capabilities are advertised. `PolicySnapshotEvaluator` applies hard invariants, ordered explicit rules, then the canonical `AgentPolicyProfileCatalog`; repository cleanliness and prompt text are never fallback inputs. Standard external structured access uses session-scoped `CanonicalDirectoryIdentity` grants or, when the user clicked "always", a durable project allow rule whose canonical path pattern (`<folder>\**`) the evaluator honors for external targets (`external_directory_rule_allow`; deny and ask still outrank it and the plan read-only gate still applies). Full Access selects an ephemeral per-call external root and never creates a reusable folder grant. Structured `CanonicalPathResolver`/`ProtectedPathCatalog` and adapter hard-link checks apply to structured file tools; catalog protection is the preset guard that `plan` and `standard` deny and `full_access` is literally full about. Canonical fallback ask reasons are `external_directory_required`, `terminal_external_cwd_requires_approval`, `terminal_protected_path_requires_approval` and `terminal_timeout_requires_approval`; `rule_asks` identifies explicit ask rules. Legacy reason codes remain readable in retained activity; timeout above the protocol hard maximum is denied. Pending approvals and exact single-use authorization tokens bind to the active lease/snapshot and cannot be replayed.
- Tool Definition ownership: `RuAgent.Protocol` contains exactly 13 immutable, model-visible definitions (`read`, `list`, `help`, `todo_write`, `grep`, `glob`, `write`, `edit`, `bash`, `web_search`, `web_fetch`, `skill`, `ask_user`). `read`, `list` and `help` are separate tools: `read` carries no discriminator, and `list` and `help` take the argument shapes that `read op=list` and `read op=help` accepted, so the split added no capability. `todo_write` replaces the session task list and touches neither disk nor network. `skill` loads one workspace SKILL.md body on demand; the prompt lists name + description only, and skill bodies never grant tools, scopes, paths, or approvals. `ask_user` is Host-intercepted before gateway dispatch: it parks the run in `WaitingUser` with a durable `input.requested` event and never reaches the Broker. `AgentModelToolContractCatalog` directly projects that catalog; the Host feeds the same ID/version/scope/strict proposal schema/example/digest to the prompt and the model-step `model_tools`. `ToolCatalog` retains Broker risk/approval/timeout/output policy and validates every manifest against the definition catalog, deliberately excluding the Host-intercepted `ask_user`. `RuAgentBroker` is the only tool execution authority.
- File tools: `read` returns one file's UTF-8 content with `offset`/`limit` and no discriminator, defaults to a 1 MiB source window and accepts up to 4 MiB, fitting serialized output into 4 MiB before Broker normalization; clipped Unicode or escaped text carries source-byte continuation metadata; `list` enumerates one directory (streaming enumeration, ordinal relative paths, hidden/binary/protected skip with counts, result/byte/time caps, bounded `truncated` metadata, junction/symlink containment, no unbounded allocation); and `help` searches the five bundled offline documentation resources, is workspace-independent and never reads workspace files. `grep` (regex content search with `file_glob`/`case_sensitive`/`is_regex` filters and bounded results) and `glob` (ordinal path-pattern results with traversal caps) remain standalone tools, alongside Host-prepared hash-guarded `write` with `op=write`/`edit` and recoverable `write` with `op=delete`/`op=restore` (preimage hashes are captured internally, never supplied in model proposal JSON; encrypted recovery copy remains bounded, opaque, one-time, and never in diagnostics). Model-facing definitions require preservation of user-supplied absolute paths inside a session-granted directory; Broker canonicalization and containment remain authoritative. `edit` preserves CRLF style when LF markers are supplied; `grep` uses canonical `is_regex`, bounded concurrent reads, cancellation, and ordinal output ordering; recovery caps are configurable below a 4 MiB hard maximum. `files.apply_patch` remains an internal/non-advertised adapter and is not model-usable.
- Model surface: the Protocol tool definitions are projected into the prompt and native `model_tools`; the Broker alone decides and executes each request, except Host-intercepted `ask_user` which never dispatches. Plan, Standard, and Full Access use the matrix above. Managed Web remains bounded, redirect-validated, sanitized, and untrusted; Plan Web is automatic. Terminal children run as the current Windows user and have no OS filesystem/network confinement. Timeout, output cap, cancellation, and process-tree cleanup remain enforced. The managed Git workflow retains temporary-index isolation and rollback attribution. MCP stdio bridge code remains test infrastructure and is not production-composed; mail/calendar/task connectors are not advertised Agent tools.
- Terminal authority: [ADR 0017](../adr/0017-workspace-terminal-policy.md) replaces
  command-form approval with canonical launch-directory permission. `run` preserves
  literal argv; `shell` explicitly selects PowerShell syntax. Both are automatic
  from a workspace cwd in Standard. External cwd and long foreground calls ask;
  explicit deny/ask rules precede automatic policy. Network and analyzer metadata
  are not permission gates. User-installed tools resolve through sanitized absolute
  PATH directories without an install-directory allowlist. The real executable,
  argv, cwd, timeout and classified metadata are rebound before consuming each
  authorization token. No OS confinement is claimed. The structured
  protected-path denial holds in `plan` and `standard`, and credential
  storage/redaction are unchanged.
  Terminal children retain the trusted parent's `PSModulePath` for PowerShell
  module discovery; model input cannot supply or override environment entries.
  Secret variables remain stripped and seeded into output redaction.
  Glob traversal also checks its elapsed request budget directly, so delayed
  cancellation timers cannot turn an expired scan into a successful receipt.
  Read-only Git preparation and the managed-commit temporary-index workflow remain
  execution behavior, not separate authority. Legacy adapter-only callers retain
  their older approval contract; production v2 calls use Broker authorization.
  `RU_MAX_CONCURRENT_MODEL_STEPS` still bounds concurrent provider generation.
- Managed Web: `web_search` resolves the configured engine against a fixed set
  of public HTML engines and returns the first usable result, rather than
  substituting a second tool or a second provider. It is the one documented
  exception to the no-substitution invariant, and every request still passes
  `WebPolicy`, so it cannot widen the network boundary or bypass a denial.
  Managed Web disables environment/system proxies and connects directly to a
  public address validated by the connection policy.
- Structured reads bind content and SHA-256 to one opened, authorized file
  handle. Windows final-path and hard-link checks inspect that handle before
  reading. Full-file hashing observes caller cancellation and a five-second
  deadline (`read_hash_timeout`); mutation preimage hashes remain Host/Broker
  owned and cannot be supplied by the model.
- Existing-file mutation: canonical ancestor handles are retained; one validated target handle excludes competing writes and rename-over through hash validation and update/delete. Updates retain at most 32 MiB of rollback bytes (`file_too_large` above the cap), write in place and flush; ordinary write failures attempt restoration, but power-loss crash atomicity is not claimed. Missing-file creation stays staged with no overwrite. Patch rollback restores only still-matching applied output, preserving intervening editor changes.
- Client attachments accept images and text/code files only. GUI picker, paste and drop share the same type checks; CLI attachment loading refuses unsupported formats. DOCX/XLSX/PPTX are not extracted or inlined. Document processing uses workspace files through ordinary terminal tools and optional skills with user-provided dependencies; it has no built-in converter or separate execution authority.
- Bundled help path: `help` (`help@1.0.0`) is a model-visible `system.read` tool backed by five explicitly embedded Markdown resources. It is offline, workspace-independent, approval-free, and bounded; it returns stable document IDs and excerpts only. It never reads workspace files or live process/device state, and its reference text cannot override higher-priority instructions.
- Interactive and headless CLI: `RuAgent.Cli` delegates start/attach to `RuAgent.Client` and all run execution to HTTP/SSE. Its Terminal.Gui TUI owns terminal presentation, command navigation, attachment submission and run-following interaction; it also exposes headless commands and safe event envelopes. Model execution and execution policy remain Harness/Broker owned. Commands include `agent approve`/`deny`, policy management, and run/chat preset selection. `run` and `chat` require `--trust-workspace` for an untrusted canonical identity and `--confirm-full-access` for `full_access`; `--json` is accepted only with the explicit trust/full-access flags and cannot confirm an approval. The canonical Plan profile does not synthesize `web_approval_required`; an explicit ask rule may still surface that compatibility reason.
  Terminal.Gui is the sole interactive screen engine. Redirected input/output,
  `--classic`, `--json`, `NO_COLOR`, dumb and undersized terminals use
  `PlainChatInput`: append-only line output with bounded submission history,
  no cursor key loop and no screen/resize reconstruction. Shared command
  metadata belongs to `ChatCommandCatalog`, not the line reader. Minimal
  credential input remains separate and never echoes the secret; Terminal.Gui
  retains its own masked panels. Startup selection uses 80×24; an active frame
  hands over only below 20×12 after 400 ms. Handover disposes the frame before
  using plain output and preserves the session/run state. `TgTranscriptView`
  remains the single bounded scroll owner and emits follow-tail state changes
  for busy/idle hints, including clear. Workspace-busy hints persist until Host
  acceptance or a successful session switch, independent of composer keystrokes.
  `TgChatLayout` owns responsive positioning: centred landing and a padded session
  column with a multiline `TgPromptEditor`. Composer height grows within the
  screen budget and uses the chat column's full width in active sessions (80 at home).
  `TgRuleView` separates session context from chat without consuming another row.
  The prompt is the opencode-style filled block in the panel tone: no frame
  outline and no accent rail, with the preset, model and provider on the meta row
  inside the block.
  Slash suggestions match composer width; modal picker, approval, secret,
  report and command-palette surfaces centre on the whole terminal.
  The footer exposes one contextual shortcut and a short five-cell medium block wave,
  leaving space for feedback and the queue status.
  Transcript and slash-suggestion bounds follow the composer frame. Modal
  dismissal restores source-text cursor position and draft focus. `TgHintsView`
  presents running, approval and paused states independently of feedback and
  queue count. Retained tool/reasoning blocks share the transcript's row/character
  bounds; selection, expansion and explicit copy remain presentation operations.
  The session column sits below a two-row header: title/usage, then workspace. The live activity
  moved out of the header into the transcript: `TgTranscriptView` keeps one live
  row pinned after the last committed row (opencode-style), drawn from the shared
  activity presentation, and a settled tool row replaces it in place. The activity
  projection separates glyph/action, target and right-aligned metrics;
  response deltas show writing, parked states stay still, and queued tools retain
  their own action after another tool settles. Settled rows preserve outcome text
  and metrics when shortening targets. Frames below
  20 rows keep a single heading row. The header projects session data and catalog
  metadata; optional title probes read the session only while its title is missing.
  Every animation phase (braille spinner, running-tool
  glyph rotation, busy-hint colour wave, elapsed clock) is a pure function of
  elapsed seconds in
  `TuiSpinner`, so the 18 ms meteor ticker and any slower cadence yield the same
  motion; the ticker runs only while the frame is live, and the live row's
  spinner pauses on parked states. Streaming reflows a dirty tail at draw/scroll
  boundaries. Unchanged wrapped rows are cached by retained text, paint and viewport
  width, including while tool details are expanded; evicted text does not stay alive
  through the cache. Turn-end assistant reconciliation reads at most four payloads
  concurrently and preserves their model-step order and types. Optional header-title
  reads have a 250 ms cancellation deadline. Reopened sessions and `/follow` treat
  `waiting_subagents` parents as active without posting Continue.
  Tool activity metadata is updated by ID and consumed in durable proposal order;
  the transient durable sequence is a stream anchor, not a tool identity.
  Silent durable run/model metadata does not flush unfinished assistant prose.
  Step-rewind callbacks read their row marker on the UI loop after the queued
  step-start callback, so a worker-thread retry removes the superseded reply.
  When durable text reveals a missing streamed head, the screen replaces the
  current turn's prose with the complete step texts, separated by paragraphs,
  retaining tool rows and earlier turns. Recovery declines after screen-retention
  eviction or reset; sequential output keeps its explicit truncation notice.
  Tool-result badges use typed completion activity: the public envelope's status
  is a run status and `running` alone is not a tool failure. Without outcome
  metadata the tool row remains neutral and says outcome unavailable.
  Late completion details update that existing tool row in place. Row markers
  are invalidated by retention, reset and rewinds rather than guessing at a
  shifted row. Document panels own their paging keys while focused.
  The TUI intentionally keeps core chat, inline tools/diffs, approvals, attachments,
  session/model controls, run recovery, help and updates. The live header context
  meter remains. Plans render only completed/total counts at existing transcript
  boundaries, without splitting streamed prose or retaining a separate task list.
  Activity/todo/change inspectors, the project-file browser, session tree, rewind,
  Markdown export and separate token/prompt-history reports are absent from the
  interactive command surface. Up/Down prompt recall remains. Headless CLI and RPC
  retain their own capabilities. File intake uses clipboard/drop or `--image`;
  parked chat runs resume on typed `Continue`. `/attach`, `/attachments`, `/paste`,
  `/detach`, `/continue` and `/quit` are absent from dispatch, help and completion.
  `/exit` is the sole quit command. Escape dismisses overlays or interrupts an
  active turn; idle Escape keeps the chat and draft open. Conversation replay retains the latest
  20 nonempty messages, with a 100,000-character bound per message; screen
  retention is independently bounded by `TgTranscriptView`.
  Command overlays follow the composer and remain bounded within the frame.
  Prose reflows across the full viewport width like the classic renderer; verified
  Markdown table blocks reflow
  from raw rows and preserve values on narrow screens.
  A model step the host suppresses for recovery drops the rows it painted, so the
  answer that replaces it never reads as the same answer printed twice.
  One flat card frame backs every popup — picker, approval, workspace trust,
  secret key, document window and slash suggestions — with the title and its
  dismiss hint, a dim header rule, the filled selection bar, footer keys and a
  scroll position when a list is clipped. The session card switches on Enter and
  deletes behind a confirm on Delete, moving off the open session when it is the
  one removed. Reasoning uses the same
  bounded collapsed-detail owner without repeated feed summaries. Web tool detail
  includes queries up to 160 characters or fetched host names. Detail passes the
  existing known-secret redactor before the public activity sink; URL credentials,
  path, query and fragment do not enter the fetched-host label.

- CLI submission ownership: `ChatSubmissionState` owns pending attachments, snapshot admission, held retry identity and accepted attachment transfers in both terminal paths. A declined trust gate returns the transfer to pending state once; cancellation and failure release its temporary images. `ChatSubmissionSnapshot` retains immutable intent and the original idempotency key. Rendering and run orchestration remain in the existing dispatcher.
- CLI turn ownership: `ChatRunSession` owns turn cancellation, active/parked/followable run identities and unique assistant payload references. A failed Continue retains the parked identity; parking cancels following without requesting server cancellation. Interrupting another active run still targets that run. The dispatcher renders and performs the HTTP operations through this state owner.

The Harness tool pipeline uses one proposal-batch implementation for classification,
input/approval pause selection, execution and abort cleanup. Initial proposal
persistence remains interleaved with evaluation. Retained approval/input entrypoints
validate their distinct identities before common re-evaluation and preparation;
approval refresh retains its waiting state and never executes the batch.

The current Agent flow is:

```text
RuApp -> ASP.NET Core public contract -> in-process typed model step -> bounded provider adapters
ASP.NET Core public contract -> IToolGateway -> BrokerExecutionRuntime (tool execution; current-user terminal bounded by policy and Job Object)
```

The in-process typed model step is the active Agent generation path; the typed
worker protocol v2 and the worker executable were retired with it (ADR-0012),
and the device v1 protocol was removed with the media surfaces. The shipped
OpenCode-style system-prompt composer does not introduce a
second conversation transport: text, coding, and tool-selection steps all use
the same Agent run and model-step path.



## Privacy boundary

Only data needed for a requested operation crosses a private boundary. Visible
assistant text may enter Agent generation and approved persistence, but private
markers and model-control metadata must not enter history, reviewed examples,
cleanup manifests, or logs.

Automated checks may
record statuses, timings, counts, event types, and cleanup state; they never
record credentials, bearer tokens, request bodies, session identifiers,
transcripts, or private marker metadata.


## Agent Session

An explicit user session in which Ru may request bounded tools, skills,
session-scoped folder grants, and a stored preset selection. It does not grant
workspace trust: trust is an explicit decision bound to the canonical workspace
identity and is evaluated independently of the Agent session.
_Avoid_: permanent Agent mode permission or preset-implied trust

## RuAgentBroker

The in-process execution runtime and class library that validates tool
manifests, session scopes, approval state, and adapter execution before a local
action. It no longer runs as a child process: the Host composes
`BrokerExecutionRuntime` behind `IToolGatewayFactory` (ADR-0014).
_Avoid_: public API route, unrestricted shell, separate broker process

## Execution Authority

The single code path that actually performs a model-visible tool call:
`BrokerExecutionRuntime`, reached through the `IToolGateway` seam created by
`IToolGatewayFactory` inside the Host process. The Host keeps orchestration and
the `ask_user` interception only; no second copy of tool semantics, policy, or
adapters may exist.
_Avoid_: dual execution paths, HarnessToolGateway

## Tool Manifest

The Broker policy declaration of a tool's identity, adapter, risk class, scopes,
execution schemas, timeout, output limit, and trust class. Its ID/version and
proposal schema digest must match the immutable definition in
`RuAgent.Protocol`; it does not itself make a tool model-visible.
_Avoid_: dynamically constructed tool ID

## Tool Definition

The immutable Protocol-owned declaration of a tool's ID/version, model-facing
description, canonical scope, exposure flags, strict proposal schema, example,
and stable ordering. The Host runtime projection selects enabled
`ModelVisible` definitions for both the prompt and the model-step `model_tools`.
_Avoid_: treating a Broker manifest or adapter list as the model contract

## Skill Manifest

The declarative, bounded description of an allowed workflow, its named tools,
scopes, ordered steps, schemas, and approval policy.
_Avoid_: executable plugin or self-modifying workflow

## Session Grant

The Broker-checked set of scopes and skill IDs attached to one Agent Session.
It does not grant arbitrary filesystem paths, domains, credentials, or apps.
_Avoid_: global allowlist

## Capability Profile

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
`full_access` also permits external structured files, arbitrary terminal
launches, and the structured protected paths (harness key/database files and
runtime descriptors, plus user-added `RU_PROTECTED_PATHS` entries)
automatically; hard-invalid requests remain denied.
Explicit deny/ask rules, exact single-use authorization, hard-invalid requests
and process lifetime/output bounds remain enforced.
Foreground launches above fifteen minutes still ask in Standard; background
launches use their bounded lifetime instead. Every running terminal task reserves
a session slot at launch, retained when foreground moves to background. The
Broker's `RU_MAX_BACKGROUND_TERMINAL_TASKS` setting defaults to 8 (range 1–64);
invalid explicit values fail initialization. A full session refuses new launches
as `background_task_limit_reached` without cancelling existing tasks.
API keys remain in credential storage
and diagnostics redact secrets. See [ADR 0017](../adr/0017-workspace-terminal-policy.md).
Hard-invalid requests remain denied; structured protected paths stay denied in
`plan` and `standard`. Standard is the
default; `full_access` requires a GUI confirmation or CLI
`--confirm-full-access`. The model-visible tool list remains the runtime projection of
Protocol definitions, one purpose per tool: `read` (`offset`/`limit`),
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
`WaitingUser` for an explicit Continue that retries the same provider. The explicit
account-limit code, `provider_quota_exhausted`, parks without automatic retries;
the operator must restore credits or limits before Continue. HTTP 429 without
an explicit quota code remains a transient throttle. The substitution exception is that
`web_search` races its configured engine against a fixed set of public HTML
engines inside the same tool decision, every request still passing `WebPolicy`.
_Avoid_: permission inferred from prompt text, repository cleanliness, or a
client-local preset table

## Approval Token

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
restart, and a session allow still passes every protected-path denial (in
`plan` and `standard`) and Broker evaluation. The explicit `allow_always` decision additionally remembers
a bounded pattern as a durable project-scope allow rule: a terminal run call
stores its requested executable plus at most two leading non-flag argv tokens
with the exact launch directory, while flag-first invocations and shell-mode
scripts stay exact; an external-directory ask stores the approved folder as a
path pattern that covers its subtree and survives restarts and later sessions
of the workspace. `rule_asks` and `terminal_protected_path_requires_approval`
never become a durable rule (the Host refuses the save and degrades to
`allow_once`); `allow_once` and session directory grants create no durable rule.
_Avoid_: reusable confirmation flag or approval-time reissue

## Session Approval Memory

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

## Policy Snapshot

The immutable, versioned, SHA-256-canonical binding of preset, trusted
workspace identity, and ordered `PolicyRule`/`PolicyMatcher` list that Broker v2
validates before advertising capabilities; creation time stays metadata on the
wire and is deliberately excluded from the hash, so a snapshot re-loaded after a
restart still matches its durable hash. A new
snapshot `N+1` invalidates other pending `N` decisions after the
authorizing call completes.
_Avoid_: mutable rule set

## Policy Rule

The `Global|Project|Run|Once` scoped record with stable ID, decision
`Allow|Ask|Deny`, matcher (tool ID/version, canonical path pattern,
resolved executable, argv/cwd/operation/network intent), creation order,
and optional expiry; matcher values with paths/argv/cwd are encrypted at
rest and only the active decrypted snapshot contains them.
_Avoid_: repository-stored authority

## Workspace Trust

An explicit decision, with source `explicit`, binding one canonical root to its
Windows volume serial, root-file identity, and display hash
(`WorkspaceIdentity` `ws_*` + `DisplayIdentityHash`). Trust is requested once
per canonical identity, validated by final-path resolution, and independent of
the session preset. The GUI requires an explicit confirmation; CLI `run` and
`chat` require `--trust-workspace`. A moved or recreated identity is untrusted
again, and the GUI presents that re-approval as a one-click "trust again" prompt
naming the identity change; `--trust-workspace` re-approves the same way for
automation. No preset or clean-repository state grants trust.
_Avoid_: repository-marker, path-only, or preset-implied trust

## Workspace Terminal

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
