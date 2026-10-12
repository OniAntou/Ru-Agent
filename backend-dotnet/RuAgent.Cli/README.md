# RuAgent CLI

Interactive and headless client for Ru's coding loop. It delegates runs to the
Public Host over authenticated HTTP/SSE; model execution and policy remain
Harness/Broker owned.

## Transcript and command navigation

Terminal.Gui is the default interactive surface on supported terminals. Ctrl+P
opens a searchable palette backed by the same slash catalog as help and command
completion. Enter selects a command into the composer; arguments can be completed
before submission. Escape closes the palette and restores the draft. While a
turn is busy, the palette can be browsed but selection is refused visibly.

PageUp/PageDown scroll the transcript. Ctrl+Home reads the oldest retained output;
Ctrl+End returns to following new output. Streaming preserves a detached scroll
position and marks new output until Ctrl+End returns to the tail. Ctrl+O expands
the last collapsed tool/diff block. The visible history
retains at most 2,000 rows and 200,000 Unicode characters, with 16,000 UTF-16 units
per line, including collapsed content; a notice marks discarded history. These are
rendering limits and do not alter durable model context.

## Project context and run continuity

`/files [query]` searches the selected workspace and asks which file to attach.
Search stops after 5,000 entries, two seconds or 50 matches; refine the query
when the list is incomplete. Private/generated paths, reparse points and multiple
hard links are refused. Selection validates the opened file's final path and
reads through that same handle with the existing image/text size caps.
Pending context retains at most eight attachments and 32 MiB of source bytes;
queued submissions have their own retained-content budget.

`/follow` uses the retained run's durable provider/model metadata. Unknown pins
show unknown and suppress catalog-based cost/context estimates. Pending approval
recovery reads a bounded tail of durable events, then rechecks the run version
before posting an explicit decision. Another client may resolve the approval first.

When a submission is held for `/retry`, queued turns stay ordered and paused.
Model/backend/key/reasoning repair commands and help/status remain available;
they leave draft attachments intact. Settings are checked again before a queued
run is created. The Host pins provider selection at the first successful model
bootstrap; a client preflight is not an atomic lock on global settings.

## Commands

```text
agent chat [--preset plan|standard|full_access] [--workspace PATH] [--session <id>] [--image PATH]... [--json] [--classic] [--trust-workspace] [--confirm-full-access]
agent session list [--json]
agent session rewind <session-id> --index <n> [--json]
agent session delete <session-id> [--yes]
agent session export <session-id> --harness <claude-code|codex> [--out <dir>] [--json]
agent session import <file> [--harness <claude-code|codex|antigravity|opencode>] [--session <id>] [--json]
agent run [--preset plan|standard|full_access] --workspace PATH "prompt" [--session <id>] [--image PATH]... [--json] [--trust-workspace] [--confirm-full-access]
agent attach <run-id> [--json]
agent trust <run-id> --approve|--deny
agent approve <run-id> <approval-id>
agent deny <run-id> <approval-id>
agent answer <run-id> <input-id> [answer] [--json]
agent cancel <run-id>
agent continue <run-id>
agent command <run-id> "<slash-command>" [--json]
agent reconcile <run-id> [--outcome applied|not-applied|unknown] [--hash SHA256] [--json]
agent output <run-id> <payload-id> [--json]
agent status <run-id> [--json]
agent policy list [--global | --workspace PATH] [--json]
agent policy import --global FILE
agent policy remove <rule-id>
agent rpc
```

- `chat` (or `repl`) uses Terminal.Gui for interactive streaming, tool/diff rows, approvals, command history and slash completion. `/new` creates a session; `/sessions [query]` searches a bounded list; `/resume [id]` switches explicitly and preserves the current session on failure. Reopening a session prints its newest 20 text messages and discovers retained running, approval and parked runs. `/follow [run-id]` attaches to running output or recovers pending approval interaction; `/continue [run-id]` resumes a parked run. Session switching keeps the operator-selected workspace and warns about a different retained workspace; reopen with `--workspace PATH --session ID` to select that root explicitly.
- While a turn streams, Enter queues up to eight immutable submissions. Each owns the prompt, session/workspace/preset/model intent, attachment contents and idempotency key captured at admission. The queue retains at most 32 MiB of estimated UTF-16 content; a composed prompt is capped at 16,000 UTF-16 units. Refusal leaves the draft and attachments intact. State-changing commands are refused while busy or queued. `/retry` resubmits the exact rejected submission with its original key. Escape/Ctrl+C interrupts through the existing run-cancel endpoint; pending tool approval takes over the composer with explicit approve/deny choices.
- Terminal.Gui is the only interactive screen engine. `--classic` retains compatibility as append-only text with line input between turns; the same fallback serves redirected stdin/stdout, `--json`, `NO_COLOR`, `TERM=dumb` and terminals below 80×24. It has no cursor positioning, screen clearing, resize rebuild, arrow-key picker or framed composer. Bare picker commands show their options; select with an explicit argument. Input history keeps the latest 100 submissions. A trailing backslash continues the prompt on the next line; ordinary prompt quotes are preserved. In text mode `/clear` records a reset marker without erasing terminal scrollback.
- Attachments travel with the next turn. Images: `--image PATH` (repeatable, max 4), `/attach <path>`, a dropped path, or `Ctrl+V` (clipboard file drop, a screenshot via DIB → temp PNG, a text path, and plain-text paste when no image is present). Images are validated, resized (longest side 2048, `>4MB` re-encoded, BMP/GIF normalised to JPEG, PNG alpha kept, EXIF orientation applied; WebP passes through only when its header proves it fits) and capped exactly like the GUI: 10MB read, 4MB decoded per image, 20MB per turn, 5.6M-char data URL. Text/code files (5MB) inline as fenced blocks ahead of the prompt; a composed prompt over 16k chars is refused before the run is POSTed. `/attachments` lists every pending item, `/detach <n> | --all` removes one or all; the list clears when the run owns it and a declined workspace-trust challenge restores it intact. Temp clipboard files (`%TEMP%\ru-img-*`) are deleted on send, detach, or chat exit. A vision-name pre-check only warns; the backend remains the source of truth and fails closed with `vision_unsupported`.
- Office documents (`.docx`, `.xlsx`, `.pptx`) are unsupported as attachments. Keep them in the workspace and ask Ru to process them through installed terminal tools or optional skills. Ru does not bundle an Office converter or authoring suite; tool permissions still apply.
- The pinned header shows the OpenCode-style usage cluster (`39,413  20%  ($0.29)`) when the active model's catalog publishes a context window and rates: the percent tracks the largest model step this turn (like the GUI meter) and the cost accumulates at the published per-million rates; both need a `models.dev`-style catalog and stay off when it publishes nothing. Session cost is session-level (a `/clear` resets the percent, not the cost).
- The backend `chat`/`run` start is a **managed host** shared by CLI, GUI and RPC: it stops itself after 10 minutes with no request in flight, no recent request and no registered run (`RU_HOST_IDLE_MINUTES`, default `10`, `0` disables), so exiting the terminal no longer leaves a detached backend running. The next command starts it again automatically; a run still in progress (even parked) keeps it alive.
- An untrusted workspace requires an explicit trust decision before execution. Terminal.Gui shows trust/cancel in the composer with cancel selected by default; Enter or Escape never grants access implicitly. Declining cancels the run and releases its workspace lock. Trust is scoped to the canonical workspace identity; moved or recreated roots require a new decision. Redirected/JSON output reports `workspace_trust_required`; resolve it explicitly with `ru agent trust <run> --approve` or `--deny`.
- A trust challenge expires after ten minutes, and the host now notices while it is running rather than only at the next startup: the runtime loop cancels the expired run with `terminal_error_code = workspace_trust_expired` and releases the lock. Trust is a pre-execution gate, so cancelling cannot roll back a tool side effect. Previously only the startup sweep could do this, so a long-lived desktop host left the run — and the workspace lock — parked indefinitely.
- A turn that completes with no assistant text and no tool row says so: `▲ no text arrived from the model · the run completed but its reply never reached this terminal · retry, or switch model with /model`. Reply text is persisted even when its delta never reaches this client, so a finished run that delivered nothing used to end in total silence — just a `✓ idle · 10.6s` row, which reads exactly like a very short answer. The note is deliberately withheld when the turn did stream text or did run tools, because there the operator can already see what happened. This reports the observation; it does not recover the missing text.
- Assistant text arrives only on transient frames, and the live event hub keeps no replay buffer: a frame published before the client subscribes is dropped. So every finished turn is reconciled against the run's own durable `assistanttext` payload, and a missing tail is reprinted. A missing **head** cannot be repaired — a terminal cannot unprint, and appending the head after the tail would interleave into a mangled sentence — so the turn reports `assistant_text_truncated: N/M characters reached this terminal` and points at the session, which always holds the whole reply.
- A model's own deliberation never enters the transcript: a completed `Thinking` activity leaves one `· thought · ctrl+o` row and keeps the text behind `ctrl+o`, matching the WinUI surface, where the thinking block is closed by default.
- Assistant prose is rendered, not printed: fenced code becomes verbatim rows behind a `│` gutter, inline emphasis and links render in place, and headings, bullets, ordered items, quotes and rules lose their markers. The inline markers are:

```text
**bold**   *italic*   `code`   ~~struck~~   [label](url)   \*escaped\*
```

  Tables and nested lists are out of scope on purpose. Because a streamed delta can cut a marker in half, only the trailing run of marker characters is held back for the next delta, and a leading `*`/`_` is read as an opener only when a word character precedes it — so the text always arrives complete and in order, even when an emphasis spanning a delta boundary loses its styling.
- Tool rows read as a sentence: provider names are nouns (`View`, `Terminal`), so a table of verbs turns them into `read`, `listed`, `searched`, `edited`, `wrote`, `ran`, `loaded` and so on. The live row uses the present tense (`⠋ reading app.cs`) and the settled row the past (`✓ read app.cs`). An unrecognised name keeps its own name, and a provider's name outranks the activity kind so precision is not lost.
- Information surfaces share one framed panel: `/help` lists every command grouped by area (generated from the reader's own command table), and `/status` (also `/stats`, `/tokens`) prints the session card — session id, preset, backend, model, API-key state, turns, tools, tokens and the workspace leaf.
- `/model` without an argument lists the live provider catalog for the active backend (current model first, then free models, then the rest A–Z, then dimmed unsupported rows; the footer names the backend and the row count as `backend · N rows`), falling back to configured models when the catalog is unreachable with a dim `catalog unavailable (<reason>) - showing configured models` line, the same reasoning `/backend` prints; pick with `↑↓`/digits/`Enter`, `Esc` cancels. Free-tier models carry a `free` tag (OpenRouter `:free` suffix) and long ids keep both ends so shared prefixes stay apart. Dropdown label columns size to the longest label on wide terminals (narrow screens clip with `…`); short lists keep the classic 12-cell column.
- `/model` and `/backend` without an argument open a picker (`↑↓`, digits, `Enter`, `Esc`); choosing one updates the settings immediately. `/backend openrouter` and `/model <id>` also work with explicit arguments. With no interactive console they print the same information as a panel, and `Esc` cancels silently.
- Starting `chat` or `run` against a `models.dev:*` backend makes one best-effort catalog check. A model id the live catalog does not list prints one bounded `model_not_in_catalog: <id> - it may fail with a 404; choose another with /model` notice; an unreadable catalog prints `model_check_unavailable: <reason>`. The run continues either way — the catalog can be stale — and OpenAI, Anthropic and custom backends, whose id spaces are not catalog-bound, are not checked.
- `/backend` reads the provider list live from `GET /models-dev` instead of a fixed list, so every models.dev provider is reachable; rows run base providers first (`openai`, `anthropic`, `custom`), then the current provider, then the ones already signed in, then the rest by id. A line above the picker says either `N providers from models.dev` or `models.dev list unavailable (<reason>) - showing the known providers only`, because a silently short list is indistinguishable from a failed fetch. A failed fetch falls back to the four providers the CLI has always known rather than emptying the picker.
- Every picker can be filtered by typing: letters and the id separators `-` `_` `.` narrow the rows by substring across label, detail and value, `Backspace` drops one character, and the footer reports `filter: <query> · <matched>/<total>`. Digits stay the numbered-pick shortcut, and with an empty filter every picker behaves exactly as before. A query that matches nothing shows one disabled `no match` row instead of an empty list.
- `/reasoning` shows or sets the active model's reasoning effort from the live catalog. Bare `/reasoning` opens a picker of `default` plus the levels that model actually publishes, or explains that the model offers none; `/reasoning <effort>` writes it directly and, like `/backend`, needs no terminal. The save is `POST /settings` carrying exactly `reasoning_effort`, `reasoning_model` and `reasoning_identity` — the identity is the catalog snapshot the picker drew, so a save can never land on a different selection. `default` is always accepted, because the host only resolves a non-default effort against the catalog.
- `/key` manages provider API keys without leaving the terminal: bare `/key` shows the sign-in state (legacy providers plus signed-in `models.dev` ids), `/key <provider>` opens a masked Terminal.Gui panel (one `•` per character, count only, never content); text fallback uses a minimal no-echo prompt and prompts for the key with no echo (`openai`, `anthropic`, `custom`, `openrouter`, or `models.dev:<id>`; `Ctrl+V` pastes from the clipboard since raw console input swallows the terminal's own paste binding, pasted text is trimmed to the first line, `Esc` cancels, empty input saves nothing), and `/key remove <provider>` signs out via `POST /settings/provider/remove`. Keys travel only inside the `POST /settings` body to the Windows Credential Manager and are never printed, logged, or stored in history. Non-interactive runs report `interactive_required` instead of prompting. Choose `/backend`, configure its `/key`, then select a supported `/model` from the current catalog.
- Scrollback uses PageUp/PageDown, Ctrl+Up/Down for individual rows, Ctrl+Home/End for top/tail, and the mouse wheel. The scrollback hint stays visible while a model turn runs; `/clear` and session replay reset it with the transcript. Retention limits and queued-submission ownership remain the existing ones.
- A workspace-busy rejection names the blocking run in a persistent hint with `ru agent cancel <run-id>`. Typing, failed retries and `/clear` do not dismiss it; an accepted run or successful session switch does. Transcript diagnostics also retain the blocking run and follow/continue/cancel advice.
- Provider HTTP 402 and Responses streamed payment errors report `provider_payment_required` without transient retries. The remedy names provider billing/credit instead of assuming a bad key or model. Provider/model selection stays pinned for the run.
- Terminal.Gui relayouts and redraws after screen-size changes. Startup selection remains 80×24; an already active frame stays available while resizing and hands control to append-only output only after remaining below 20×12 for 400 ms. Transcript bounds and detached scrolling survive layout changes. `RU_TUI_DEBUG=1` writes diagnostic events to `%TEMP%\ru-tui-debug.log`; a file path selects another diagnostic destination. Native terminal resize and IME acceptance are recorded separately from headless tests.
- `session list`, `session rewind` and `session delete` provide explicit command-line controls for listing sessions, time-travel rewinds, and deletion. `session delete <session-id>` asks for a `y/N` confirmation (default No) and only `--yes` skips it; a partial cleanup from the backend (`Session đã xoá nhưng artifacts còn sót…`) surfaces as `session_delete_failed` bounded to one line instead of reading as success. The TUI's `/delete [session-id]` asks through the same confirm list (default `keep`) and refuses the **current** session with a bounded line — switch first, then delete it.
- `session import <file>` reads Claude Code and Codex JSONL transcripts, Antigravity's `transcript.jsonl`, and opencode's `.db` store (naming the session when more than one is present). JSONL imports are size-checked before read: a file above **16 MiB** is refused with one bounded `import_too_large` line; an opencode `.db` store is checked on the payload after extraction, never on the whole database, and only the CLI reads it. `session export` writes Claude Code and Codex formats only, by design. The desktop picker imports `.jsonl` and points opencode users at `ru agent session import <opencode.db> --harness opencode`.
- `rpc` runs the stdio JSON-RPC 2.0 bridge for external tools (`ru agent rpc` takes no further arguments); it reuses the same loopback host and bearer as every other command and adds no HTTP route. See [the RPC bridge contract](../../docs/operations/rpc.md).
- `run` creates a single-shot Agent run. If `--session` is omitted, the tracer creates one dedicated session via `POST /sessions/new`, prints `session_id` and `run_id`, and sends a fresh `idempotency_key` (32-char hex GUID). Workspace is canonicalized and bounded to 512 chars; prompt is bounded to 8192 chars; max argument count is 64.
- `--preset` selects one canonical, session-scoped profile: `plan` is read-only — workspace read/Web auto, terminal/mutation denied outright, external structured access asks; `standard` auto-allows workspace mutation and executable/shell launches from a workspace cwd; external structured paths and external launch directories ask; `full_access` (alias `full`) is the skip-all mode — external structured files and arbitrary terminal auto while only structured protected paths (harness key/database files and runtime descriptors) remain denied. Repository cleanliness is not an input.
- Workspace trust is explicit once per canonical identity. `--trust-workspace` approves the returned identity and continues the same run; without it, an untrusted identity returns `workspace_trust_required` without prompting. A moved or recreated identity requires trust again.
- `--preset full_access` requires `--confirm-full-access`; without it the command returns `full_access_confirmation_required` before session creation or preset mutation. With it, the existing policy route receives `X-Interactive-Confirmed: true`. Terminal children run as the current Windows user, can access data outside the workspace and network resources, and have no OS filesystem/network confinement; timeout, output, cancellation, and process-tree controls remain.
- Preset updates retry expected versions `0..3` and bind the positive returned version to the run. The CLI creates a durable snapshot token before mutation; the Host persists prior/applied selection and token atomically, enforces capacity without evicting active snapshots, and restores with CAS/single-use semantics. A malformed or lost mutation response uses the known token for safe restore; a lost restore response can replay only while the restored state still matches, otherwise the command fails closed before streaming. Failed creation or trust handling restores the exact prior selection, including a new session with no prior selection.
- `--json` is accepted with explicit `--trust-workspace` and, for Full Access, explicit `--confirm-full-access`; it never auto-approves an approval or workspace.
- `attach` streams an existing run's durable events from `after_sequence=0` via `AgentSseClient`.
- `trust` wraps `POST /agent/v1/runs/{runId}/trust`.
- `approve` and `deny` wrap `POST /agent/v1/runs/{runId}/approvals/{approvalId}` with `allow_once`, `allow_session_directory` (external folders only), `allow_always` (authorizes once and remembers the exact intent as a durable project rule), or `deny`. The Host picks the right one-time token from the pending binding (session-scoped directory grant for `external_directory_required`, ephemeral token for `terminal_external_cwd_requires_approval`, `terminal_protected_path_requires_approval`, or `rule_asks`). Any allow also remembers the exact intent in the Host's in-process session ledger, so a later identical call in the same session authorizes without another prompt (the memory dies with the session or a host restart; external-directory approvals keep the session grant only). `terminal_timeout_requires_approval` remains the standard-window gate below `full_access` and `web_approval_required` remains a compatibility reason for explicit ask rules; timeout above the protocol hard maximum is denied. `agent approve --always` sends `allow_always`; no reusable approval token is ever created. Approval is first-writer wins; second concurrent response returns `approval_stale` (409). `--json` cannot confirm approval.
- `answer` wraps `POST /agent/v1/runs/{runId}/inputs/{inputId}` with the user's answer to an `ask_user` question (1..8192 characters). Without the answer argument it prompts; `--json` requires the argument because it must never prompt, and prints a versioned envelope carrying `run_state`. A replayed, foreign, or already-answered input id returns `input_stale` (409), an unknown run `run_not_found` (404), and an invalid body `invalid_answer` (400).
- `policy list` wraps `GET /agent/v1/policies` (scope filter). `policy import --global FILE` validates versioned JSON (rejects unknown version/fields, normalizes and displays matchers/decision before confirmation) and posts each rule with `X-Interactive-Confirmed: true`; sensitive matcher values are encrypted at rest via Host API. `--json` cannot bypass confirmation. `policy remove` wraps `DELETE /agent/v1/policies/{ruleId}` and requires interactive confirmation for global rules.
- `cancel`/`continue`/`status` are thin HTTP wrappers.
- `command` forwards `/compact normal|aggressive`, `/help`, `/skills`,
  `/skill <name>`, or `/tasks [kill <execution_id>]` through
  `AgentApiClient.DispatchCommandAsync`.
  Slash parsing is shared with the Public Host; malformed or unknown commands
  return stable `command_invalid`/`command_unknown` codes and are never sent as
  prompts. Human output prints only bounded metadata; `--json` emits the
  versioned response envelope without raw tool output.
- `reconcile` wraps `POST /agent/v1/runs/{runId}/reconcile`. Without `--outcome` it fetches the run (showing the `intent_code` and a fresh `snapshot_hash`), prompts for `applied|not-applied|unknown`, then posts the decision with the freshly read hash. `--outcome applied|not-applied|unknown` maps to wire tokens `applied`/`not_applied`/`unknown_continue`. `--hash` must be a 64-char hex SHA-256; a stale `--hash` returns `reconcile_stale` and exits non-zero. `--json` never prompts: it prints the info payload and exits without sending a decision; an `--outcome` given without `--hash` in `--json` mode fails locally (exit 1) and sends no request.
- `output` wraps `GET /agent/v1/runs/{runId}/payloads/{payloadId}` — the explicit user action that fetches final head/tail tool output bytes. Nothing in the tracer fetches private payloads implicitly; the event stream only references payloads by opaque `payload_ref`. The command prints the payload's `content` field just as the host returned it: UTF-8 text for `utf8text`/`json` payloads, and the base64 wire form for `opaque` payloads (it does not decode them); `--json` is accepted for symmetry with the other commands but adds no envelope, because the payload is already the answer.
## Current model tool surface

The Agent model-step advertisement contains the thirteen Protocol definitions:
`read`, `list`, `help`, `todo_write`, `grep`, `glob`, `write` with
`op=write|delete|restore`, `edit`, `bash` with `mode=run|shell|read_output|kill`
plus a `background` flag on run/shell, standalone
`web_search`/`web_fetch`, `skill`, and Host-intercepted `ask_user`. A provider-native model step may return
optional narration and one or more ordered calls; the compatibility text path
still emits exactly one JSON selection. The shared budget is 256 tool calls per
window, read-only concurrency is capped at eight, and mutation/terminal calls
remain sequential. Durable approval resume re-evaluates retained proposals
without a new model proposal, and an `ask_user` park resumes the same way once
`ru agent answer` completes the asked call.

Canonical `plan` is read-only: workspace read/Web auto, terminal/mutation denied
outright, external structured access asks.
`standard` auto-allows workspace mutation and executable/shell launches from a workspace cwd;
external structured paths and external launch directories ask. Terminal children
in both execution presets run as the current Windows user without OS filesystem
or network confinement; obsolete sandbox arguments are rejected by schema.
`full_access` (the skip-all mode) allows external
structured files and arbitrary terminal while only structured protected paths
(harness key/database files and runtime descriptors) remain denied. Web is
automatic in all profiles; `web_approval_required` is only an explicit-rule
compatibility reason. Full-access terminal runs as the current Windows user and
has no OS filesystem/network confinement. Time, machine status, app discovery,
and optional volume commands use the terminal; notes use `write` with
`op=write`.
`files.apply_patch` and `app.open` are not model-usable. Managed
Web results are bounded and marked untrusted. Workspace SKILL.md bodies are
prompt guidance only and grant nothing; a `requires: python` frontmatter entry
fails the model's `skill` load closed with `python_unavailable` when Python is
not on PATH. Subagents, LSP, extensions, plugins,
and MCP are not part of this CLI surface. All commands ensure the Public Host
is running through `PublicHostStarter.GetOrStartAsync` and authenticated
`PublicHostSession`.

## Rendering

- **Interactive** (`SafeEventRenderer`): displays only safe envelope fields (`protocol_version`, `event_id`, `run_id`, `session_id`, `sequence`, `event_type`, `occurred_at`, `status`, `safe_summary`, `payload_ref`). Never fetches private payloads; `payload_ref` is shown opaque.
- **JSON** (`NdjsonEventRenderer` with `--json`): versioned NDJSON `{"v":1,"type":"event",...safe fields}`. Never prompts, never auto-approves, never auto-fetches private payload, and never emits `prompt`/`command`/`argv`/`path`/`code`/`diff`/`output`/`secret`/`stdout`/`stderr`/`environment` fields. Full Access is accepted only with explicit `--confirm-full-access`; `--trust-workspace` is the explicit non-interactive trust confirmation and is allowed with `--json`, while approval and global-policy confirmations remain rejected with `--json`. Under `--json`, slash-command surfaces write their human text to stderr, bare picker commands refuse with a bounded `interactive_required`, and a chat run that reaches an approval request ends the chat (exit 0) with a bounded `approval_required` notice on stderr naming `ru agent approve <run-id> <approval-id>` or `ru agent deny <run-id> <approval-id>` — the run stays waiting server-side and is never auto-denied; an `ask_user` park ends it the same way with an `input_required` notice naming `ru agent answer <run-id> <input-id> "<answer>"`. A `--json` chat stream may include blank separator lines between turns, so consumers should skip empty lines. A transient frame's raw `data` is therefore withheld and reported as `data_bytes` — its bounded size, not its text — so the machine-readable trace never carries streamed assistant text or tool output.
- **Live terminal frames**: transient `tool.output.delta` frames render as `[transient:<seq>] <type> <preview>` with the preview bounded to 200 chars; any forbidden field (`command`/`output`/`secret`/...) is replaced with `[redacted]`. Terminal output redaction also seeds from every nonempty secret environment value stripped before child launch, including provider API keys; child processes do not inherit those values. A `transient.frames_dropped` event renders an explicit `WARNING dropped-frames` notice. Final tool output is referenced only by opaque `payload_ref`; the actual head/tail bytes are fetched solely by the explicit `agent output` command.

## Cancellation

Interrupting a chat turn is server-side, not just a local detach: Escape with an empty draft (the first Escape only clears a non-empty draft) and `Ctrl+C` cancel the turn and `POST` `/agent/v1/runs/{runId}/cancel` (best-effort), so the run releases its workspace lock. Success prints `Run interrupted by user · cancelled <run-id>.`; a failed cancel prints `run_cancel_failed` plus the manual `ru agent cancel <run-id>` fallback; a run that parked itself is left for `/continue` instead of being cancelled. `Ctrl+C` also cancels the SSE stream and `POST`s `/agent/v1/runs/{runId}/cancel` before exit for `attach`/`run`. Under `--json` there is no footer to prompt in: an approval request ends the chat (exit 0) with the bounded `approval_required` notice instead, and the run stays `waiting_approval` server-side until `ru agent approve`/`deny` answers it from another terminal.

## Bounds

- Max arguments: 64
- Workspace path: 1..512
- Prompt: 1..8192 (the composed prompt with inlined attachments is refused above 16,000 chars)
- Image attachments: max 4, 10MB per file, 4MB decoded each, 20MB per turn, png/jpeg/webp/bmp
- Text/code attachments: 5MB per file; Office/binary documents are unsupported
- Session id: 1..64 (alphanum, `_`, `-`)
- Run id: `run_` + 32 lower hex (36 total)
- Approval id: `apr_` + 32 lower hex
- Rule id: `rule_` + 32 lower hex
- Policy file: max 1 MiB, version 1 only, max 64 rules
- Error text: one-shot failures print `code: message` (server message, max 200 chars, single line), never raw JSON

## Failure catalog

Failed turns print a three-line card: title plus code, cause, and fix
(`TuiFailureCatalog`, which resolves the shared `AgentFailureCopy` table in
`RuAgent.Client`; the desktop client resolves the same table).
Covered families: provider auth/key/model (`catalog_unauthorized`,
`provider_not_active`, `provider_configuration_changed`, `forbidden`),
rate limits (`catalog_rate_limited` / `rate_limited`, HTTP 429),
transport (`timeout` / `transport_error`), provider errors
(`server_error` / `http_error` / `malformed_payload`), catalog reachability
(`catalog_unavailable` / `catalog_http_error`) and shape
(`catalog_malformed` / `catalog_too_large` / `catalog_configuration_error`),
`response_too_large`, `vision_unsupported` (`image_limit_exceeded`,
`image_too_large`, `invalid_image_mime`, `invalid_image_base64`),
`invalid_prompt`, `empty_model_reply`,
`model_failed` / `model_timeout` / `prompt_too_large` / `model_unavailable`,
model-backend reachability (historical `worker_unavailable` / `worker_failed`
codes, kept apart from
`provider_failed` / `generation_failed` / `drive_failed` execution failures),
run lifecycle (`repeated_tool_call`, `prompt_contract_invalid`,
`run_not_runnable`, `workspace_busy`, `reconciliation_required`,
`reconcile_stale`, approvals, `tool_failed`, store/preset/session/workspace
failures, `compact_failed`, `command_invalid` / `command_unknown`),
budget and context (`policy_denied`, `context_limit_reached`,
`model_step_budget_exhausted`, `tool_call_budget_exhausted`,
`commit_conflict`, `unverified_after_tool_failure`), tool execution
(`workspace_changed`, `process_timed_out`, `git_timed_out`,
`invalid_terminal_write_set`), web tools (`web_response_too_large`,
`web_search_parse_failed`, `web_redirect_limit`, `web_redirect_invalid`,
`web_content_type_not_supported`, `web_json_invalid`, `web_encoding_invalid`,
`web_uri_not_allowed`, `web_private_host`, `web_connect_failed`,
`web_disabled`, `web_uri_invalid`, `web_search_endpoint_not_allowed`),
recovery and approvals (`permission_profile_changed`, `recovery_pre_dispatch`,
`recovery_read_only_incomplete`, `broker_gateway_unavailable`,
`approval_authorization_failed`), and client/backend lifecycle
(`invalid_preset`, `full_access_confirmation_required`, `trust_failed`,
`run_not_parked`, `output_frame_exceeds_limit`, `unsupported_protocol`,
`unsupported_protocol_version`, `reconcile_failed`).
Codes outside the table still name themselves and point at
`%LOCALAPPDATA%\Ru\logs` plus `/status` instead of rendering bare.
When a `models.dev:*` backend has no stored credential, the `/model` card says
`use /key <provider> to sign in` instead of leaving a silent absence.

## References

- `RuAgent.Client` only (`RuAgent.Protocol` for contracts). No `RuBackend`, `Broker`, `Worker`, or `System.CommandLine`.

Policy JSON (version 1):
```json
{
  "version": 1,
  "rules": [
    {
      "scope": "global",
      "decision": "allow",
      "matcher": {
        "tool_id": "read",
        "canonical_path_pattern": "src/app.ts"
      }
    }
  ]
}
```
Unknown version or fields are rejected. Matcher values are encrypted at rest; only decrypted snapshot contains them.
