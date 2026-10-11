# Agent tools and truthful execution

This document is the operating contract for Ru's action execution kernel v1
plus the additive Phase-2 policy authority and selected Phase-3 headless Agent
v2 surfaces, updated 2026-10-10.
It distinguishes a Broker binding that can execute from a capability the model
is actually allowed to select. Those states are not interchangeable.

This checkpoint includes declarative Skills, bounded headless terminal execution
(`bash` with `mode=run` or `mode=shell`), session rewind, managed
Git commit workflow, and dynamic model context compaction. The MCP stdio bridge
is tested infrastructure but is not production-composed; connector libraries are
not advertised Agent tools.

> **Checkpoint (Agent-only generation, 2026-09-05).** Recorded at that date; the counts below are that snapshot's and the section that follows this one carries the current contract. The managed Runtime Worker it describes was retired on 2026-09-30 (model generation runs in the Host process, [ADR 0012](../adr/0012-in-process-model-runner.md)) and the Broker child was retired on 2026-10-10 (tool execution runs in process, [ADR 0014](../adr/0014-in-process-execution-and-terminal-sandbox.md)): the Host has no managed child.
> - Public Host (`RuBackend.exe --managed-runtime`) now owns Worker/Broker in
>   one Job Object; `RuApp` and `RuAgent.Cli` start-or-attach via
>   `RuAgent.Client`. There is no device/runtime v1 boundary; Agent
>   generation runs on the v2 one-step protocol at `/worker/v2` and the
>   `/agent/v1/*` (21 HTTP + SSE `GET /agent/v1/runs/{runId}/events` with
>   `id:<sequence>`/`Last-Event-ID`/`after_sequence` cursor, reconnect replay).
>   This checkpoint exposes Broker-backed model tools (`read` for one file with
>   offset/limit, `list` for one directory, `help` for the bundled docs,
>   `glob`/`grep` for bounded discovery,
>   `write` with `op=write`,
>   `edit`, one-time `write` with `op=restore`) through a durable Core (state machines incl.
>   `tool.execution_requested`, SQLite WAL `agent_harness.db`, AES-GCM
>   payloads, idempotent conversation outbox, workspace trust,
>   session permissions with monotonic version, encrypted `PolicySnapshot`
>   snapshots, `FileRecoveryStore`, approval ordering
>   `consume one-time approval → execute under N → receipt → N+1` with a
>   reusable project rule persisted only for the explicit `allow_always`
>   decision; every other approval is `allow_once`,
>   `allow_session_directory` (external folders only), or `deny`) and a thin
>   `RuAgent.Cli` tracer (safe envelope only, NDJSON `--json` `v:1`, no private
>   payload auto-fetch, `policy list`/`import --global FILE`/`remove`,
>   `approve <run-id> <approval-id> [--always]`/`deny`, `run --workspace PATH "prompt"`,
>   with no `--scope`/`--preset` flags exposed; the Host accepts
>   `allow_once`, `allow_session_directory` (external folders only),
>   `allow_always`, and `deny` — the server picks the right one-time token from the pending
>   binding — session-scoped directory grant
>   for `external_directory_required`, ephemeral token for
>   `terminal_delete_requires_approval`,
>   `terminal_external_mutation_requires_approval`,
>   `terminal_timeout_requires_approval`, `web_approval_required`,
>   `terminal_unknown_executable`, or
>   `terminal_protected_path_requires_approval`).
>   `--json` cannot confirm any approval.
>   Policy/approval/reconcile routes are active on the Agent v2 headless path.
>   `POST /agent/v1/runs/{runId}/reconcile` resolves ambiguous dispatch and
>   reconciliation outcomes. `--json` cannot create global rules or confirm
>   trust/approval.
>   Headless terminal requests (`bash` with `mode=run` or `mode=shell`) are available
>   under the policy/approval boundary; the managed Git commit workflow is fully shipped
>   with temporary index isolation and path attribution rollback.
>   Typed command dispatch is available through `POST /agent/v1/runs/{runId}/commands`;
>   `/compact`, `/help`, `/skills`, `/skill <name>`, and `/tasks [kill <execution_id>]` use the shared
>   Protocol parser and do not become model history. `/skills` lists workspace
>   skill name + description; `/skill <name>` loads one SKILL.md body as
>   untrusted prompt guidance. The `skill` tool lets the model load a body
>   on demand; neither path grants tools, scopes, paths, or approvals.
>   Managed startup enables standalone `web_search` and `web_fetch` as
>   approval-gated capabilities backed by the configured search engine
>   (Google HTML by default) raced against fixed free fallbacks (DuckDuckGo,
>   Bing, Mojeek); the first engine with usable results answers. Results are
>   bounded, sanitized, and explicitly
>   untrusted.
>   Workspace coordination is now opt-in per workspace through the authenticated
>   concurrency/activity/changes endpoints. Shared mode permits disjoint runs;
>   active Ru runs hold renewable workspace membership leases, and external file
>   changes are surfaced as `unknown_external` after an authoritative SHA-256
>   check and durably appended to the encrypted change journal. Terminal
>   `read_only` and `write_set` fields are additive protocol metadata; full
>   terminal write-set enforcement remains gated.
> - Public Contract is now **42 HTTP with no WebSocket route** (20 non-Agent + 22 `/agent/v1/*`).
> - Worker v2, Harness Core/Persistence (`PolicySnapshot`/`PolicyMatcher`/
>   `SqlitePolicyRuleStore`/`PolicySnapshotLoader`/`WorkspaceTrust`,
>   `FileRecoveryStore`), `RuAgent.Hosting`, `RuAgent.Client`, `RuAgent.Broker`
>   (`CanonicalPathResolver`/`ProtectedPathCatalog` `v1`, `PolicyContext`/
>   `PolicySnapshotEvaluator`, `PendingApprovalStore`/`ApprovalTokenService`,
>   `FileList`/`Glob`/`Search`/`Read`/`Write`/`Edit`/`Delete`/`Restore` file
>   adapters behind the `read`/`write`/`edit` ops,
>   `FileHashService`/`AtomicFileReplacement`/`FileMutationJournal`),
>   and `RuAgent.Cli` (`PolicyJsonCodec` versioned, exact scope preview) are
>   covered in `docs/architecture.md`, `CONTEXT.md`,
>   `backend-dotnet/README.md`, and `backend-dotnet/RuAgent.Cli/README.md`.

## Current capability matrix

The immutable Protocol catalog contains 13 model-visible definitions (`read`,
`list`, `help`, `todo_write`, `grep`, `glob`, `write`, `edit`, `bash`,
`web_search`, `web_fetch`, `skill`, `ask_user`). Each tool has one purpose: `read` takes `path` with
optional `offset`/`limit` and reads one file; `list` takes `path` with optional
`max_entries` (1–512); `help` takes `query` (1–256) with optional `max_results`
(1–5) and searches the five bundled offline documents; `todo_write` replaces the
session task list (at most 64 rows of `content` plus a `status` of `pending`,
`in_progress` or `completed`) and touches neither disk nor network.
`glob` takes `pattern`
(required) plus optional `path` and `max_results` (1–128, default 128) and returns
stable sorted relative paths without content access; `grep` takes `query`
(1–2048 chars, required) plus optional `path`, `file_glob`, `case_sensitive`
(default false), `is_regex` (default false), `max_results` (1–128, default
128) and `offset` (0–50000, default 0) for paging the ordinal results,
and returns bounded match locations with previews up to 512 characters. `write`
carries `op=write`, `op=delete` or `op=restore`; `bash` carries `mode=run`,
`mode=shell`, `mode=read_output`, or `mode=kill`, plus an optional `background`
flag on `run`/`shell`. The schema has no sandbox selector; obsolete sandbox
arguments are rejected.
`web_search` takes `query` (1–512 chars) plus optional `max_results` (1–10,
default 10); `web_fetch` takes `url` plus optional `offset` (0–4194304,
default 0) and `limit` (1–32000, default 32000); `skill` takes `name` (1–96) and loads one
workspace SKILL.md body as untrusted prompt guidance. `ask_user` takes
`question` (1–2048) plus optional `options` (at most 8 strings of 512 chars) and
is Host-intercepted before gateway dispatch: it never reaches the Broker, parks
the run in `WaitingUser` with a durable `input.requested` event, and resumes when
the client answers through `POST /agent/v1/runs/{runId}/inputs/{inputId}` with
the answer as that call's durable tool result. Web tools are standalone with their own
`network.search`/`network.fetch` scopes and are automatic in the canonical
`plan`, `standard`, and `full_access` profiles. `web_approval_required` remains
available only when an explicit ordered rule asks; profile fallback does not
synthesize it.
Google HTML (`https://www.google.com/search`) is the primary search engine and
is raced against a fixed set of free fallback engines (DuckDuckGo HTML, Bing,
Mojeek); the first engine with usable results answers, so a slow or blocked
primary no longer holds the call hostage. Every engine request still passes
redirect and private-host validation, so a fallback never widens the network
boundary the call was granted under. When every engine fails, or when anchors
were present but none resolved to a usable target, the call fails closed as
`web_search_parse_failed` or the earliest engine's bounded error rather than
returning an empty answer. Known engines require their result markup; CAPTCHA
and navigation anchors cannot win the race. Bing links are decoded to the
public destination and titles/snippets come from the result block. This does
not guarantee upstream relevance or availability: free engines may still
block requests or return weak results.

Web downloads default to 4 MiB, with the existing configurable 4 MiB ceiling.
Fetch parses HTML locally through AngleSharp without loading resources or
executing scripts, prefers main/article content, removes navigation/scripts,
and preserves paragraphs, preformatted code and HTTP links. Markdown, JSON
and source text preserve newlines and indentation. HTTP charset and BOM are
honored; invalid encodings fail as `web_encoding_invalid`. Returned content
is explicitly untrusted and limited to 32000 extracted UTF-16 characters per
page, separate from the download bound. Output includes `offset`, `next_offset`
and `truncated`; pass `next_offset` as the next request's `offset`. Each page
fetches the source again, so a changed page can shift offsets. Offsets beyond
the extracted document fail as `web_offset_out_of_range`. Recognized challenge
forms fail as `web_page_blocked`; empty JavaScript-only shells fail as
`web_content_empty`. Authentication, JavaScript execution and binary/PDF text
extraction are not provided by these tools (`web_content_type_not_supported`).
The `network_intent`
argument on a bash request is descriptive input metadata, not a security
boundary.

| Capability family | Model exposure | Broker behavior |
| --- | --- | --- |
| `glob`, `grep`, `list` | always advertised | `plan`: trusted-workspace read auto, external ask; `standard`: trusted/session-granted read auto, external ask; `full_access`: external structured read auto, harness-protected path deny |
| `read` | always advertised | same canonical read matrix; `read` carries no `op` discriminator |
| `write` with `op=write`, `edit` | always advertised | `plan`: mutation deny; `standard`: trusted-workspace mutation auto, external structured ask; `full_access`: external structured mutation auto, harness-protected path deny; preimage mismatch returns `workspace_changed` |
| `write` with `op=delete`, `op=restore` | always advertised | same canonical mutation matrix; delete remains recoverable and restore one-time |
| `bash` with `mode=run`, `mode=shell` | always advertised | `plan`: deny; `standard`: classifier-bounded local auto, external/risky intent asks under existing approval rules; `full_access`: arbitrary current-Windows-user terminal auto. Both presets run children as the current user without OS filesystem/network confinement; obsolete sandbox arguments are rejected by schema. `mode=read_output` pages retained full output of an earlier call in the same session (`execution_id` from its receipt `spool_id`, which is present on successes and on failed terminal receipts, with an `offset`/`limit` window) without re-running; unknown or foreign-session ids fail closed. Output past the 256K-character in-memory window spills to a session-scoped spool file under the Ru data directory (1 GiB UTF-8 byte retention per call, 2 GiB per-session spool quota); the receipt and read pages name `spool_path`, and output past the ceiling reports `spool_truncated`. `background: true` starts the command as a session-scoped background task: the call returns immediately with `execution_id` and `state=running`, `mode=read_output` pages live text plus `state`/`exit_code`, and `mode=kill` stops it by `execution_id` (`unknown_background_task`, `background_task_not_running`, and `background_task_limit_reached` fail closed). Background tasks skip the standard-window approval gate — they do not block the turn — and are bounded by a lifetime (30-minute default, 2-hour ceiling). A foreground command that outlives its approved window is moved to the background instead of being killed (`moved_to_background` with its `execution_id`) and then runs to the same lifetime. |
| `web_search`, `web_fetch` | advertised when managed Web is enabled | automatic in canonical `plan`/`standard`/`full_access`, bounded, sanitized, explicitly untrusted; an explicit ask rule may still return `web_approval_required`; the primary engine races the fixed free fallbacks and the first usable result answers, every engine's failure is bounded rather than silently empty, and redirect targets are revalidated |
| `help` | always advertised | offline, workspace-independent, approval-free search over five embedded documents; never reads workspace files |
| `skill` (workspace SKILL.md body) | always advertised (`system.read`) | loads one name + description-listed body as untrusted prompt guidance; grants nothing |
| `ask_user` | always advertised (`user.input`) | Host-intercepted: the Broker never receives the call. Parks the run in `WaitingUser` with a durable `input.requested` event; the client answers through `POST /agent/v1/runs/{runId}/inputs/{inputId}`, a replayed or foreign id fails as `input_stale`, and the answer completes the parked call durably before the run resumes |
| MCP and connector libraries | not advertised | experimental/unwired; require a separate production trust and credential design |

Large structured tool results are projected into bounded prompt references while
their durable Broker receipts stay unchanged. When a reference carries
`projection_has_more: true`, continue the same source using
`projection_next_offset`: `read` cursors count UTF-8 bytes, `grep` cursors count
matches from the original query offset (an omitted `offset` means zero),
`web_fetch` cursors count UTF-16 characters in extracted content (excluding
the untrusted wrapper/truncation marker), and `bash` `read_output` cursors count UTF-16 characters within the same
`execution_id`/spool. The projection names its visible unit and retains source,
receipt, and spool metadata; it does not claim older output exchanges remain in
the bounded model history. Missing call context fails closed if clipping needs
a cursor. Paging does not authorize a new operation or bypass Broker approval.

### File discovery output

Both
`glob` and `grep file_glob` share pattern semantics: `**/` matches zero or more
directories, including root files; `*` and `?` do not cross a path separator,
and filename-only patterns also match nested filenames. Literal pattern text
is escaped rather than replaced through wildcard placeholders.

Grep previews include the match instead of always showing the line's prefix.
`column` is the absolute 1-based line column; `preview_column` names the first
column of the preview. Results retain ordinal path/line ordering. Grep scans
ordered batches with bounded concurrency and bounds collection by the requested
offset/page plus one lookahead, stopping before later files once the page is known. Output
includes `offset`, `next_offset`, `scan_incomplete`, `discovered_files` and
`scanned_files` (the files actually searched). A final exact-size page has no
next cursor. `truncated` can also indicate a scan/time bound or regex timeout;
`scan_incomplete=true` means coverage is incomplete, not proof that another
page exists. Narrow the path/query in that case. Each page re-scans files;
changing files or hitting discovery bounds can change the ordered result set.
The 50000-offset ceiling can prevent continuation even when more matches are
known; narrow the query/path rather than exceeding it. If prompt projection
clips the returned page, prefer its `projection_next_offset` over the receipt's
`next_offset`.

### Session folder grants and bash approval

Terminal launch resolves the executable or shell runtime before registering a
foreground or background task. Resolution errors such as `executable_not_found`
and `executable_not_trusted` retain their code instead of becoming
`process_start_failed`; an unresolved background call cannot report a task as
started. Windows children retain a native process handle before resume, so an
immediate exit still yields its actual exit code.

Failed terminal starts, nonzero exits and timeouts retain `exit_code` when
available and already-redacted stdout/stderr head/tail excerpts, each bounded
to 2,048 characters, in the private model-visible receipt. Failure status and
code remain unchanged. The bounded `spool_id` supports `mode=read_output` for
more detail; public SSE summaries do not expose these diagnostic excerpts.

On Windows, `bash` with `mode=shell` defaults to Windows PowerShell 5.1
(`powershell.exe`); scripts use PowerShell syntax. `mode=run` takes a separate
executable and an optional argument vector (omission means no arguments).
Missing mode-specific fields such as `executable` or `script` return
`invalid_arguments` rather than `adapter_error`. The tool description states this explicitly so
the model does not assume Unix `head`/`tail` commands are available.

Canonical `plan` denies terminal and mutation. Canonical `standard` allows only
classifier-bounded local terminal intent inside the trusted workspace; deletion,
unknown executables, arbitrary shell, external cwd/argv, and other risky intent
ask with the existing reason codes. Standard and full-access children run as
the current Windows user, without OS filesystem or network confinement. The
`bash` schema rejects obsolete sandbox arguments. Repository cleanliness does
not change the decision. Canonical `full_access` delegates arbitrary terminal
execution. Hard timeout, output cap, cancellation, redaction, and process-tree
cleanup still apply. API keys remain in credential storage and diagnostics
redact secrets.

An explicit absolute structured-file path outside the trusted root asks for
`external_directory_required` under `standard`. Approval records a
session-scoped `CanonicalDirectoryIdentity` for structured descendants; it is
not shared with another session, lost on restart, and never authorizes
terminal intent. Full Access instead selects an ephemeral nearest external
ancestor for that structured call and does not persist a folder grant.
Structured protected paths (harness key/database files and runtime descriptors,
plus user-added `RU_PROTECTED_PATHS` entries) remain denied in every preset,
and canonical, symlink, junction, and hard-link checks stay active. An ambiguous
mutation pauses for inspection until explicit Continue.

`files.apply_patch` is intentionally absent from the runtime capability catalog
and is not model-usable at this checkpoint. Any internal adapter or test fixture
for patch application does not create a public model capability.

## Model-first selection and native batches

The Host advertises the same canonical tool definitions to the prompt and the model step.
On a provider-native path, one model generation may emit optional narration and
one or more ordered tool calls. On the compatibility text-selection path, the
model emits exactly one JSON object with `tool_id`, `tool_version`, and
`arguments`, without selection prose. The text path also accepts the existing
strict wrapper forms; malformed markup remains ordinary text and never executes.

The runtime evaluates a complete batch before executing any call. If any
non-denied call asks, prepared authorization is discarded and the whole retained
batch pauses for one explicit decision. Resolution re-evaluates the exact durable
proposals under the current lease; the model is not asked to reissue an approved
call. An `ask_user` call parks the same way without a Broker decision: the asked
call is completed by the answer route, and the durable rehydrator resumes the
remaining retained proposals without a new model proposal. A denial may return
a repair hint. An exact repeat or short failed-call cycle is suppressed before
duplicate dispatch; up to two `[REPETITION GUARD]` notes request changed arguments,
then `repeated_tool_call` parks the run for explicit Continue. Suppressed calls
remain audit-only and do not create unmatched native-history tool calls. The
failed receipt remains visible behind advice, so prose alone cannot clear it. An empty or whitespace-only
final step is answered up to two times with a bounded `[HARNESS RECOVERY]` note before the run fails as
`empty_model_reply`. A runner-rejected tool call or selection
(`model_tool_call_invalid`, `model_tool_call_unavailable`, `invalid_selection`) is
likewise answered up to four times before failing with the original runner code.
A `Length` finish is not a completed turn: the entire step, including partial
tool calls, is suppressed before any execution and gets up to two recovery notes
requesting smaller output, then fails as `model_output_truncated`. A
`ContentFilter` or `Error` finish fails immediately as `model_content_filtered`
or `model_reply_error`. All recovery kinds share a cumulative 12-note run cap;
provider-transient failures retain their existing retry/Continue behavior, and
prompt failures never take the note path.

The shared budget remains 256 model-initiated tool calls per window; there is no
new low per-step cap. Read-only calls may execute concurrently in chunks of at
most eight while receipts retain model order. Mutations and terminal calls are
sequential and act as barriers; an unknown mutation outcome stops later work
until reconciliation. A model cannot claim success before durable receipts.

Use the `help` tool when the user asks how Ru's own features, architecture,
configuration, tools, providers, or operating boundaries work. It returns
bounded excerpts from the five current bundled documents (`ru-overview`,
`architecture`, `runtime`, `agent-tools`, and `provider-catalog`). Use runtime
context or an advertised terminal tool for live machine, process, device, and
workspace state. There is no second docs alias or fallback tool; the only
model-visible bundled-help contract is `help` on tool
`1.0.0`.

## One execution path

Agent text and voice turns share the same execution ownership chain:

```text
WinUI authenticated Agent composer
        -> ASP.NET Core Public Host and session grant
        -> in-process typed model step (ModelStepService)
        -> in-process execution authority policy and canonical binding (BrokerExecutionRuntime)
        -> bounded adapter plus readback verification (terminal OS confinement retired)
        -> typed receipt back to the Host
        -> visible reply
```

The Public Host keeps one active execution grant. It reuses a valid matching
session, and closes the previous grant before opening a different Agent
session. Close or open failure stops before adapter execution; the authority
continues to reject concurrent owners with `session_conflict`.

The desktop session switch also waits for the previous Harness run's
cancellation commit before creating a run for the replacement session. A
workspace-trust run that was abandoned past its ten-minute trust window is
cancelled (startup recovery and the running host's drive loop, safe code
`workspace_trust_expired`), so an interrupted prompt cannot keep the
workspace's active-run lock indefinitely. The drive loop covers the long-lived
desktop host, where no restart would otherwise arrive to notice the expired
challenge. The same rule covers approvals: a run
still parked in `WaitingApproval` past the 30-minute approval lifetime fails as
`approval_expired` (startup recovery and the running host's drive loop), because
no client can resolve an older approval — the endpoint refuses it as
`approval_stale`. The gated tool call is pre-dispatch, so failing that run cannot
roll back a side effect. A `409 workspace_busy` (or the session-scoped variant)
names the run that owns the resource in `blocking_run_id`/`blocking_run_status`,
so a client can point the operator at the exact run to cancel
(`ru agent cancel <run-id>`), attach to (`ru agent attach <run-id>`), or — when
`blocking_run_status` is `waiting_user` or `reconciliation_required` — resume
with `ru agent continue <run-id>`.

While a run is actively driven, a dedicated heartbeat renews its workspace
membership independently of model and tool step duration. A failed renewal
cancels the active step and fails closed with `workspace_membership_expired`;
slow provider responses alone do not let a healthy membership expire.

Agent turns continue through normal streamed generation. Unrelated
conversation is never forced through a tool-selection path.

The Public Host derives `model_tools` from canonical model exposure and runtime
availability, while the execution session owns executable scopes and publishes the actual
resulting grant as `agent.session`. Managed startup enables the Host web
configuration and the default headless model loop advertises both read-only
Web bindings through the in-process gateway. Workspace `SKILL.md` roots are
prompt guidance and grant nothing by themselves. WinUI renders a received grant;
it does not infer workspace, terminal, Web, connector, or skill access.

A model step may return one or more native calls, and the Host preserves their
provider IDs and order. The Host sends only bounded enabled capabilities, exact
tool versions, and typed arguments; narration is display text, never execution.
The scheduler allows at most eight overlapping read-only executions, keeps
mutation/terminal calls sequential, and appends receipts in model order. A
visible success reply is legal only after matching durable success receipts.
Timeout, cancellation, policy denial, malformed context, or adapter failure
terminates or reconciles the action with a deterministic receipt.

If the model produces ordinary assistant prose immediately following a committed
tool batch that contains any failed or unverified receipt, the coordinator suppresses
the prose from the conversation outbox, retains the assistant step internally for audit
as an `InternalOnly` payload, and answers with a bounded durable `[HARNESS RECOVERY]`
note in the model context naming the failed receipt code, so the model can redo the
call, use a different tool, or report the blocker. The unverified case is bounded to
2 notes and every recovery case shares a global cap of 6 notes per window; once the
bound is spent the run fails with the receipt error code (falling back to `tool_failed`)
exactly as before. An unverified success claim is never displayed to the user as a
completed answer. A subsequent different tool call remains eligible for normal execution
to allow recovery. A first `policy_denied` is the exception: the run stays alive so
the model can read the failed receipt plus repair hint and adjust; only a repeated
identical deny terminates the run.

A non-success outcome, including a failure or clarification, is also kept as
one bounded in-memory value. It is isolated by session, consumed once by the
next Agent turn, and removed on session replacement, disconnect, or deletion.
It is never written to conversation history, reviewed
examples, or public payload metadata. This lets Ru answer an immediate “why?” from
the verified reason without asking the model to reconstruct or invent the
previous action state.

Legacy action-marker prose is stripped from visible output and never translated
into desktop side effects; structured selection is the only action channel.

## File and terminal boundaries

File and terminal bindings use the canonical matrix above. `plan` terminal
authority is denied. `standard` limits automatic terminal execution to classifier-bounded
local intent and asks for external/escape/risky intent. `full_access` may run
arbitrary `bash` as the current Windows user against any path and may open
sockets, including outside the workspace; terminal execution has no OS
filesystem/network confinement. Repository
cleanliness is irrelevant. Full Access still has hard timeout, output cap,
cancellation, redaction, and process-tree cleanup. Structured protected paths
(harness key/database files and runtime descriptors) remain denied, but they do
not re-impose terminal confinement.

Background terminal tasks are session-scoped and owned by the execution authority: at most
`TerminalLimits.MaxBackgroundTasksPerSession` run at once, a task that reaches
its lifetime is killed (`expired`), `mode=kill` stops it (`killed`), and a
host restart drops every task (a stale id fails closed). The kill path kills
the process tree by parentage first and then the Job Object, so a descendant
created before the job assignment landed cannot survive. The model context
carries one bounded `background_tasks` reference per step naming the tasks that
appeared or changed state since the previous step, so a task that finished
while the model was working is noticed without polling; the GUI's compact
right-hand card lists the same session tasks, and its details panel stops one
through the `/tasks` command.

The managed Git commit workflow retains temporary-index isolation, path rollback
attribution, and refusal when the real index already contains staged user
changes. A zero exit is `succeeded`; non-zero exit and start failure are
`failed`; timeout is `timed_out`. Standard structured-file tools use the trusted
root or a session-scoped canonical folder grant; Full Access uses an ephemeral
per-call external root. Every structured mutation binds a Broker-captured
preimage and fails with `workspace_changed` on mismatch without writing.
Existing targets are held against competing writes and rename-over; hash
validation and update or delete use that same handle. Updates retain at most
32 MiB of original bytes for ordinary failure recovery (`file_too_large` above
that bound), write in place and flush. This does not provide crash-atomic
replacement. Missing-target creation remains staged with no overwrite.
Patch rollback restores only an applied target whose current hash still
matches that patch's result; it preserves intervening editor changes.
`write` with `op=delete` keeps its bounded recovery cap and opaque recovery ID.
Bash children receive redirected stdin that is closed immediately; TTY/editor
requests remain rejected by canonical command policy.

### Skill guidance (`SKILL.md`)

Workspace `SKILL.md` guidance follows the Claude/OpenCode/Pi shape: one
directory per skill, `SKILL.md` with `name` + `description` frontmatter and a
markdown workflow body. The prompt lists name + description only (with the
body digest pinning the pointed-at version). The model loads the full body
on demand with the `skill` tool (`name`), then acts with its normal tools
under Broker policy. The user-facing `/skills` lists name + description;
`/skill <name>` loads one body. Neither executes tools by itself.

Skill bodies are untrusted guidance. `allowed-tools` is descriptive metadata
only: it never grants a tool, scope, path, network permission, approval, or
Broker capability.

An optional `requires:` frontmatter entry names a capability the workflow
needs; only `python` is recognized today. It is checked wherever a body is
returned: the model's `skill` tool (Broker) and the `/skill <name>` command
(Host) both fail closed with `python_unavailable` when Python is absent, an
unrecognized token fails closed as `unsupported_skill_requirement` naming the
token, and the body is not returned. Skills without `requires` are unaffected.

`AGENTS.md`, `CONTEXT.md`, `.ru/AGENTS.md`, and `.ru/CONTEXT.md` are the fixed
user-workspace instruction candidates below the selected run root, not repo
templates. The loader applies final-path containment, strict UTF-8 decoding,
normalization, a 64 KiB per-file limit, and a 256 KiB combined instruction
limit. Their contents are untrusted guidance only; they cannot grant tools,
scopes, paths, network access, approvals, or Broker authority.
Example `AGENTS.md` format:

```markdown
# Ru workspace instructions

- Follow repo conventions.
- Keep changes minimal and verified.
```

Workspace skill roots holding that same `SKILL.md` shape. Copy a directory
containing a case-insensitive `SKILL.md` into one of the supported roots:
`<workspace-root>/skills`, `<workspace-root>/.ru/skills`, or
`%LOCALAPPDATA%\Ru\skills`. A minimal package can look like this:

```markdown
---
name: pdf-analyzer
description: Analyze and summarize PDF files
allowed-tools: read, bash
---

# PDF Analyzer

Describe the review workflow here.
```

The Host discovers direct child directories in that precedence order and
re-reads them for each newly built prompt context, including a new Agent run
or after restart. `allowed-tools` is descriptive metadata only: it does not
advertise or grant tools, scopes, paths, network access, or approvals. Only
`SKILL.md` is read by discovery. Ancillary `references/`, `assets/`, and
`scripts/` files are ignored and never automatically read or executed; any
later access still has to use an advertised tool under Broker policy.

Skill guidance is rendered as untrusted `ExternalSkill` prompt content. It
uses `SKILL.md` only and never executes tools by itself.
The in-process execution authority remains the only owner
for canonical paths, capabilities, scopes, approvals, and execution.

## Typed command channel

`POST /agent/v1/runs/{runId}/commands` accepts only the bounded commands
`/compact normal|aggressive`, `/help`, `/skills`, `/skill <name>`, and
`/tasks [kill <execution_id>]`.
The shared `AgentActionRouter` rejects unknown or malformed slash commands with
stable `command_unknown`/`command_invalid` codes, without creating a prompt,
history entry, or outbox message. `/compact` reuses the durable compaction
contract; `/help` is static; `/skills` and `/skill` are session-scoped; `/tasks`
lists the session's background tasks and stops one on request. The CLI
adapter uses the same parser and renders only bounded result metadata.

## Web tool gates

For the managed Agent path, Web tools use the configured Google HTML search
engine, `https://www.google.com/search`, raced against fixed free fallbacks
(DuckDuckGo HTML, Bing, Mojeek); the first engine with usable results answers,
so one blocked or slow engine no longer delays the answer. The remaining
in-flight engine requests are cancelled once a winner is known, and every
request -- winner or loser -- still passes redirect and
private-host validation. Canonical
`plan`, `standard`, and
`full_access` all allow Web; an explicit ordered ask rule may still return the
legacy `web_approval_required` reason. Redirects are validated hop-by-hop and
private/reserved targets are rejected. Responses retain byte, UTF-8,
content-type, sanitizer, and citation caps, and all returned content stays
explicitly untrusted. These are bounded Web-tool controls: web tools run
Host-side. Terminal children also run as the current user without OS-level
filesystem/network confinement (ADR-0015).

## Agent activity shown in WinUI

Agent is the only supported generation path. The desktop composer uses the
known project root and exposes one bounded preset selector (`Plan` /
`Standard` / `Full Access`) plus the model picker; there is no Chat mode switch
and no workspace editor. The `Full Access` choice needs one explicit
confirmation per session, is stored server-side with a monotonic version, and
flows into runs;
a version conflict surfaces as `preset_mismatch` / `preset_version_conflict`.
The sidebar
keeps only primary conversation surfaces; Log, Diagnostics, Policy, and
Audit remain reachable from the `Công cụ` flyout. Agent prompts are
created through the authenticated `/agent/v1/*` API and observed over
SSE. The inline surface never displays raw commands, arguments, stdout,
or private payload content.

## Agent permission UX

Every Agent run is session-scoped. The chat composer offers canonical `Plan`,
`Standard` (default), and opt-in `Full Access`; the in-process execution
authority remains the only authority. Plan is read-only: workspace read/Web
auto, terminal/mutation denied outright, external structured access asks.
Standard auto-allows bounded-local in-workspace mutation and asks for
external/escape/risky intent under its existing rules. Terminal/git children
in both execution presets run as the current Windows user without OS
filesystem/network confinement. Full
Access is the skip-all mode: external structured files and arbitrary terminal
auto while only structured protected paths (harness key/database files and
runtime descriptors) remain denied.

Workspace trust is not a preset. It is explicit once per canonical identity:
the GUI requires a guarded Approve action, while CLI `run`/`chat` require
`--trust-workspace`. A moved or recreated identity asks again. Full Access
requires its GUI confirmation or CLI `--confirm-full-access`. Host and UI only
display the server-selected pending approval kind with one bounded message per
reason code:

- `external_directory_required` — Ru cần dùng thư mục ngoài project
  trong đoạn chat này, cho đến khi ứng dụng đóng. The approval opens a
  session-scoped directory grant in the Broker; the same session
  re-uses the grant on subsequent calls, and new sessions ask again.
- `terminal_delete_requires_approval` — Lệnh terminal này sẽ xóa file.
  The approval returns an ephemeral one-time terminal token and remembers
  the exact intent for the session; no durable rule is created.
- `terminal_unknown_executable` — Ru chưa nhận diện được executable của
  lệnh terminal này. The approval returns an ephemeral one-time
  terminal token and remembers the exact intent for the session.
- `terminal_external_mutation_requires_approval` — Lệnh terminal này gây
  mutation ngoài workspace (push/fetch mạng, cài package, restore).
  The approval returns an ephemeral one-time terminal token and remembers
  the exact intent for the session; no durable rule is created.
- `terminal_protected_path_requires_approval` — Lệnh terminal này trỏ vào một
  **file tự bảo vệ của harness** (`agent_harness*`, `harness_key*`, runtime
  descriptor) hoặc một path user tự thêm qua `RU_PROTECTED_PATHS`. Terminal vốn
  **không** bị protected-path confinement — `full_access` vẫn chạy như user
  hiện tại, đúng như `README.md` đã ghi — nên reason này **không** nâng quyền
  lên deny; nó chỉ để người duyệt thấy đúng câu hỏi thay vì một thư mục "ngoài"
  trung tính. Approval này **không thể nhớ vĩnh viễn**: GUI và CLI đều không hiện
  lựa chọn `allow_always` cho reason này, nên một lần duyệt không biến thành
  rule dự án; đúng như mọi allow khác, intent chính xác chỉ được nhớ cho session
  (mất khi đóng session hoặc restart Host).
- `terminal_timeout_requires_approval` — the standard-window gate: a terminal
  call above 15 minutes asks under `standard`, is automatic under `full_access`
  (the skip-all mode), and is denied under `plan` with the rest of terminal; a
  timeout above the protocol hard maximum is denied in every preset.
- `web_approval_required` — compatibility reason emitted only when an explicit
  ordered rule asks for Web. Canonical `plan`, `standard`, and `full_access`
  otherwise allow Web automatically; the returned authorization is
  single-use and exact-intent bound.

Standard auto-allows only classifier-bounded local terminal intent; arbitrary
shell, deletion, unknown executables, external cwd/argv, and other risky forms
ask even if the repository is clean. Both presets launch terminal children as
the current Windows user without OS filesystem/network confinement. Full Access
removes those profile asks. The CLI exposes the same model through `agent approve` /
`agent deny`; `agent run` and `agent chat` accept `--preset
plan|standard|full_access`, `--trust-workspace`, and
`--confirm-full-access`. `--json` is accepted only with explicit trust and
Full Access confirmation.

The transcript activity feed is connection-local and transient. Runtime maps the
Core projection to an `agent.activity` SSE frame whose inner JSON contains only
the typed fields `activity_id`, `kind`, `state`, optional safe `file_name`
(basename/line range only), `line_start`/`line_end`, `symbol`, `count`, `added`, `removed`,
`duration_ms`, and bounded `safe_code`. It does not advance the durable event
cursor, append to the Harness event store, or enter session history.

The feed projects nine kinds: `thinking`, `explore`, `search`, `analyze`, `edit`,
`terminal`, `compaction`, `retry`, and a generic `status` fallback. `compaction`
reports the bounded number of covered context entries and the elapsed time of a
context compaction (the summary text never crosses the sink boundary); `retry`
reports a bounded tool transport retry attempt (any access kind, at most once
per call). Status rows can carry a bounded plain-text `detail` (for example the
inspection-gate pause with its Continue guidance). Completed tool kinds
carry a bounded `duration_ms` measured from the scheduled execution.

Assistant text uses a separate typed `assistant.delta` transient frame. The
client validates its bounded UTF-8 text and step/execution identities, then
delivers it on the UI dispatcher with `(run, model_step, transient_sequence)`
deduplication. Raw transient JSON and tool-output deltas are never exposed to
the UI or durable audit.

Each activity item exposes one of four visual states: a spinner while started,
a check when completed, an error mark when failed, and an ellipsis while waiting
for approval or continuation. Thinking items may show only their elapsed
duration after completion. File edits and analysis show a basename and optional
line range; search and exploration show bounded symbols or counts. The temporary
user prompt and activity items use separate collections, so durable assistant
history remains unchanged when the feed is cleared.

The desktop surface renders every kind. The TUI renders `thinking` through the
live status row and `compaction`, `retry` and non-`frames_dropped` `status` kinds
as transient status lines; tool kinds are settled by the durable tool events,
which remain authoritative when transient frames are dropped. Because the
durable `safe_summary` for a tool event is a bounded safe code rather than a
name, the TUI keeps the activity captured for each tool and prints one row per
tool when the durable completion arrives (`verb`, basename, line range and
`duration_ms` when the activity was delivered); a dropped frame degrades to a
code-only row instead of duplicating the event. Completed tool kinds
additionally expose their bounded `duration_ms` on the desktop item.

Before an Agent final reply is persisted, the reply policy strips private
markers. The resulting
visible text is the only assistant payload sent to the authenticated client,
model context, and conversation outbox. Assistant
activity is a post-commit effect; failures are bounded observations and do not
turn a committed run into a failed run. User activity is projected from its
outbox intent with the same preview-sanitized `agent` shape.

The feed is shown only after the first valid activity frame and is cleared on
terminal completion, cancellation, failure, disconnect, session replacement,
or reset. The inline surface still owns bounded status, workspace trust,
approval, Stop, and Continue controls. Malformed, unknown, stale, or other
content-bearing transient frames are ignored. Raw commands, arguments, stdout,
or private payload content never reach the feed, durable audit, conversation
history, or public payload metadata.
The desktop entry point also creates a real 32-character session automatically
for the first Agent prompt when the empty conversation surface has no active
session; users do not need to send a preliminary message.

Agent requests use an opaque `correlation_id`, durable run identity, and SSE
cursor. The UI presents one active run, ignores stale or uncorrelated frames,
and reconnects by replaying durable events without duplicating history. A
disconnect marks an ambiguous request as a bounded retryable error; it never
silently replays a request that might already have committed. An approved run
rehydrates and re-evaluates its durable retained batch without a new model
proposal or duplicate proposal event; the model is never asked to reissue an
approved call. An ambiguous mutation outcome pauses with a waiting status
detail, and explicit `Continue` clears the inspection gate with a
`gate_cleared_by_user` audit event in the same commit.

The short-intent resolver may answer only an exact, high-confidence greeting
or acknowledgement locally. Corrections, filesystem/path, search, network,
current-fact, and uncertain text continue through the full model context; the
resolver does not select Agent capabilities. A stale Agent session is recovered
by creating a replacement session and retrying at most once before a run exists.

## Approval, privacy, and diagnostics

External-folder and dangerous-terminal requests use a short-lived approval bound to
session, request, tool version, canonical path/executable/argv/cwd/operation/
network, snapshot version, and intent hash with 30-min expiry. Consumption is
first-writer-wins `approval_stale` (including `PendingApprovalStore` and Host
`HostApprovalLedger`). A first deny keeps the run alive with a repair hint so
the model can adjust; only a repeated identical deny terminates the current
run with `approval_denied`,
so the model cannot immediately request the same permission again. Changed
fields and expired or moved-workspace replays return `approval_stale`. The
Broker reply carries the exact matcher preview plus one `authorization_token`
(60s); a durable project rule is produced only for the explicit
`allow_always` decision. Any allow additionally remembers the exact intent in
the Host's in-process session ledger, and the runtime loop materializes those
entries as run-scope allow rules when it builds the next policy snapshot for
the session, so a later identical call — including later in the same run —
evaluates as allowed without another prompt. The ledger never widens the
approval: protected-path denials still apply, the Broker still decides every
call, external-directory approvals keep the session directory grant only, each
entry expires after 30 minutes under a per-session cap, and the memory dies
with the session or a host restart. Ordering is
`consume one-time approval under snapshot N
→ execute already-authorized call under N → persist final receipt → build/bind
N+1 → invalidate other pending N decisions`; a host restart before dispatch
invalidates the ephemeral decision and causes safe re-evaluation, never
adapter invocation. File tools follow the same ordering with `tool.execution_requested`
persisted before dispatch and `FileRecoveryStore` for
recoverable deletes. The deterministic Broker approval probe uses
`write` with `op=write` (and file delete/restore ops) only inside a fresh temporary
workspace, approves once, expects the replay to be denied as `approval_stale`,
closes the private session, and removes the workspace. Production WinUI
approval-card behavior remains covered independently by its UI contract tests.

Public Agent events and diagnostics may contain correlation ID, tool
ID/version, terminal status, stable error code, duration, verification state,
the fixed endpoint role, and an ephemeral endpoint hash. They must not contain
arguments, output values, prompts, replies, paths, scripts, stdout/stderr,
credentials, device names, or native exception text.

See [Verification](verification.md) for the separate contract, adapter, and
explicit opt-in native evidence classes. Deterministic evidence does not prove
that an arbitrary terminal command changed a live external application or
device.
