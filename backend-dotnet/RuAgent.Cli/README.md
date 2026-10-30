# RuAgent CLI

Interactive and headless client for Ru's coding loop. It delegates runs to the
Public Host over authenticated HTTP/SSE; model execution and policy remain
Harness/Broker owned.

## Concurrent chats in one terminal

- `/new` or **Alt+N** opens another session while earlier sessions keep running.
  Alt+N also preserves the draft in the current tab.
- **F6** / **Shift+F6** switches next/previous. The tab strip appears with two or
  more tabs and marks running, waiting, completed or failed/held work.
- `/sessions` opens saved history or returns to a tab already open.
- `/sessions close` closes an idle tab without deleting history. Finish retained
  runs and pending work first; one tab always remains.
- **Esc** affects the selected session only. `/exit` stops this window's runs
  and drains pending acknowledgements; it detaches runs adopted from other clients.

Each tab keeps its own draft, attachments, queue, transcript and permission
selection. Background questions wait in their own tab. This first version permits
eight open tabs in the same workspace. Model/provider settings are shared defaults
for new runs; changing them never retargets an admitted run or queued prompt.
Native submissions retain their provider/model across a change made while
admission is pending. This requires the matching Host build; an older Host
reports `provider_selection_unsupported`, with no fallback to another model.

## Durable history and database maintenance

`ru session list --limit 100 --query TEXT` pages summaries; add `--cursor TOKEN`
to continue. `ru session history SESSION_ID --limit 200 --json` returns the
newest chronological message page and an older-page cursor. `ru session search
TEXT --limit 50` searches retained messages. The existing `agent session ...`
spelling remains supported. Plain history output is a bounded preview; JSON
contains the complete returned page. Rewind uses absolute zero-based indices.

`ru database status|backup|verify|restore|compact|prune` uses the matching Backend
binary directly without starting a model run. See [database maintenance](../../docs/operations/database-maintenance.md)
for bundle paths, the same-Windows-identity key constraint, offline operations
and explicit pruning. No automatic conversation deletion is enabled.

## Transcript and command navigation

Terminal.Gui is the default interactive surface on supported terminals. Ctrl+P
opens a searchable palette backed by the same slash catalog as help and command
completion. Enter selects a command into the composer; arguments can be completed
before submission. Escape closes the palette and restores the draft. While a
turn is busy, the palette can be browsed but selection is refused visibly.

Interactive transcript content reaches Terminal.Gui as semantic rows and styled
runs: Markdown previews/commits, recovered replies, tool updates and notices do
not round-trip through ANSI strings. The existing views retain their appearance,
scrolling and code gutters. TextWriter/ANSI import remains for command reports
and diagnostics; plain and JSON output retain their existing contracts.

Ru's terminal chrome uses a mint-accented signature, a quiet session divider and
a filled prompt surface in the panel tone — the opencode-style block, with no
frame outline and no accent rail. An empty chat centres the logo above the same
block. The empty prompt is capped at 80 columns. Active sessions use a padded
transcript and a full-width bottom prompt with the same side margins as chat; the
model, selected reasoning effort and provider sit on the meta row inside the block. The permission preset
appears in green beside the token cluster in the top header during chat; the
empty welcome screen hides it. The top workspace/path label is removed.
The reasoning label shows only its value, for example `max` or `high`.
The welcome logo uses green/blue RU and muted green AGENT colors, with
a warm light sweep that visibly brightens the darker AGENT face. Terminals that
report Sixel or enabled Kitty graphics show square tiles, small rims and layered
shadows behind every letter. A solid black backdrop keeps galaxy dots and meteors
from showing through the logo. Other terminals and small windows use the glyph
logo. The welcome decoration disappears when chat starts.
The selected workspace remains available as a wrapped full path in `/status`.
Removing its header label does not change workspace selection, tool cwd or
permissions. The composer
grows from one to six visible input rows, leaving room for
the transcript on short screens. It shows “Ask anything…” when empty and uses
a steady block cursor. Enter sends; Ctrl+J, Shift+Enter where the terminal
distinguishes it, or a trailing backslash followed by Enter inserts a newline.
Up/Down move within a multiline draft before navigating history at its edges.
Text paste preserves line breaks and indentation and replaces selected text or
inserts at the cursor. A paste exceeding the 16,000-character draft budget is
refused visibly with the draft unchanged. The empty-chat footer shows
`ctrl+p commands` and pending attachment context; active-session hints adapt to
available width. The footer normally shows only Ctrl+P; while running it keeps
Esc visible. The busy colour wave uses five medium block cells (`▬▬▬▬▬`)
whose colours sweep aurora green, mint, brand blue, brand2 violet and warn
amber once every 0.8 s, so the row carries more than two hues at any phase
without reading as a slab. Other shortcuts remain available through help.
The header puts the session title (without a product-name prefix)
title/known usage on the first row and workspace on the second. Live activity
occupies a row at the end of the transcript. A colored glyph precedes the readable action; targets
are muted and elapsed/tool count align right. Idle omits status text, response deltas
show “Writing response”, and parked states have no spinner or running clock.
Tool rows distinguish action, target and metrics; failure remains explicit even
when a long command is shortened. Frames
shorter than 20 rows use a single heading row. The feed
uses the available width without a side panel. Picker cards page their entries
to fit the current height while keeping the selection and footer visible.
Slash suggestions span the composer width, with descriptions aligned in a
stable column above it. Model/session, approval, secret and report panels
centre on the terminal; the command palette uses the same centred placement.
Approval cards preserve choices and action hints on short screens. Closing a
modal restores the draft, cursor and composer focus.

Streaming defers reflow to drawing/scrolling rather than rebuilding a growing
line for every fragment. Completed tool metadata updates its activity by ID;
duplicate/late updates cannot turn into the next tool's result. Tool/diff cards
are enabled for Terminal.Gui; plain output keeps the sequential tool rows.

Markdown block state advances only on complete raw lines. Terminal.Gui replaces
the unfinished line preview at up to 30 updates per second, then commits its
final formatting; retry removes provisional text. Fenced code retains its full
source line and wraps to the viewport instead of permanently clipping its tail.
Native streaming retains settled Markdown chunks and reparses the changing tail;
late closers can still restyle unresolved emphasis, code or links. Settled chunks
reuse native rows instead of being sanitized, styled and wrapped again for each delta.
Plain line output waits for newline or Flush before committing Markdown.
Markdown tables are grouped from retained raw header/separator/body rows, align
their columns (numeric columns right-align even without a `:` marker, `:---:`
centres) and switch to labeled values below 40 cells. A failed tool row adds one
`└ <reason>` line from the event's safe summary. Reasoning remains behind Ctrl+O
without adding a Thought row for every model step. Web activity targets use
redacted query detail and host names; URLs do not expose credentials, query
parameters or fragments.

PageUp/PageDown scroll the transcript. Ctrl+Home reads the oldest retained output;
Ctrl+End returns to following new output. Streaming preserves a detached scroll
position and marks new output until Ctrl+End returns to the tail. Ctrl+O toggles
the selected tool/diff or reasoning block, defaulting to the newest retained block.
Ctrl+Alt+Up/Down select and reveal earlier/later blocks. Ctrl+Shift+C copies the
selected block's retained detail text, or the latest contiguous assistant text
when no block is selected; copied content includes any retained formatting or
code gutters. Ctrl+End clears block selection and resumes following output.
Running, waiting for approval and paused states show the queued prompt count;
submission refusals are feedback and do not start a running indicator. The visible history
retains at most 2,000 rows and 200,000 Unicode characters, with 16,000 UTF-16 units
per line, including collapsed content; a notice marks discarded history. These are
rendering limits and do not alter durable model context. Long native assistant
paragraphs use multiple bounded storage segments after Markdown styling and tab
expansion; copy joins those segments without adding newlines, and display reflow
continues across storage boundaries. Quote/list indentation and double-space layout
keep their original whole-row wrapping. Provisional segments
are replaced together, so preview growth and final commit do not duplicate text.

## Project context and run continuity

Opening fresh chat or using local commands such as `/model`, `/provider`, `/status`
and `/help` does not create a saved session. A session is created on the first real
message or explicit `/new`; `/sessions` can select an existing one. Local native
commands do not appear as user-message cards or show model-working status. Until
then status displays `not started`, and session-specific inspections ask you to
send a message or select a session first.

The TUI focuses on chat and coding, with inline tool activity and expandable
errors/diffs, approvals, attachments, session/model controls and run recovery.
The header keeps the live context meter. Plans show compact completed/total
counts in the transcript; use the GUI for dedicated activity and plan
inspection or file browsing. Drop a known file path or use Ctrl+V to add it to
the composer. Up/Down still recalls previous prompts. `/exit` is the only quit
command; Escape closes an overlay or interrupts an active turn and never exits
the chat. An idle Escape preserves the draft.

The interactive vocabulary omits `/delete`, `/resume`, `/retry`, `/release`, `/clear`, `/activity`, `/todos`, `/files`,
`/tree`, `/export`, `/stats`, `/tokens`, `/history`, `/attach`,
`/attachments`, `/paste`, `/detach`, `/continue` and `/quit`. Headless CLI
and RPC commands, including session interchange, remain available separately.

`/follow` uses the retained run's durable provider/model metadata. Unknown pins
show unknown and suppress catalog-based cost/context estimates. Pending approval
recovery reads a bounded tail of durable events, then rechecks the run version
before posting an explicit decision. Another client may resolve the approval first.

After a rejected submission, queued turns stay ordered and paused; exit and reopen the session before submitting fresh work.
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

- `--json` is the non-interactive NDJSON rendering, so it is only accepted
  together with `--trust-workspace` or `--confirm-full-access`: a scripted run
  cannot take, by omission, the authority those flags exist to confirm. Without
  one of them the parser refuses with `json_requires_explicit_flags`. The
  transcript renderer is muted in this mode, so stdout carries durable/transient
  NDJSON frames only; every human line goes to stderr.
- `chat` (or `repl`) uses Terminal.Gui for interactive streaming, tool/diff rows, approvals, command history and slash completion. `/new` creates a session; `/sessions [query]` searches a bounded list; `/sessions id:<session-id>` switches explicitly and preserves the current session on failure. Reopening a session prints its newest 20 text messages and discovers retained running, approval and parked runs. `/follow [run-id]` attaches to running output or recovers pending approval interaction; typing `Continue` resumes the tracked parked run. Use `ru agent continue <run-id>` from another terminal for an explicit run target. Use `ru agent cancel <run-id>` outside chat to abandon a parked run. A repeated blocker after Continue offers cancellation guidance. Session switching keeps the operator-selected workspace and warns about a different retained workspace; reopen with `--workspace PATH --session ID` to select that root explicitly.
- While a turn streams, Enter queues up to eight immutable submissions. Each owns the prompt, session/workspace/preset/model intent, attachment contents and idempotency key captured at admission. The queue retains at most 32 MiB of estimated UTF-16 content; a composed prompt is capped at 16,000 UTF-16 units. Refusal leaves the draft and attachments intact. State-changing commands are refused while busy or queued. Rejected submissions are never automatically resubmitted; queued work remains paused until the chat is closed or an existing explicit run continuation settles it. Escape/Ctrl+C interrupts through the existing run-cancel endpoint; pending tool approval takes over the composer with explicit approve/deny choices.
- Terminal.Gui is the only interactive screen engine. Windows uses its .NET console driver to preserve emoji across split input packets. The append-only path is a printer, not a second renderer: it writes rows and keeps a bounded history, and the cursor tracking, sticky footer, live-row repaint, resize rebuild and framed composer that used to shadow the interactive engine were unreachable (the CLI always suppressed repaints) and have been removed. Bracketed paste targets the active command palette, secret prompt or composer without changing a hidden draft. `--classic` retains compatibility as append-only text with line input between turns; the same fallback serves redirected stdin/stdout, `--json`, `NO_COLOR`, `TERM=dumb` and terminals below 80×24. It has no cursor positioning, screen clearing, resize rebuild, arrow-key picker or framed composer. Bare picker commands show their options; select with an explicit argument. Input history keeps the latest 100 submissions. A trailing backslash continues the prompt on the next line; ordinary prompt quotes are preserved.
- Attachments travel with the next turn. Images: `--image PATH` (repeatable, max 4), a dropped path, or `Ctrl+V` (clipboard file drop, a screenshot via DIB → temp PNG, a text path, and plain-text paste when no image is present). Images are validated, resized (longest side 2048, `>4MB` re-encoded, BMP/GIF normalised to JPEG, PNG alpha kept, EXIF orientation applied; WebP passes through only when its header proves it fits) and capped exactly like the GUI: 10MB read, 4MB decoded per image, 20MB per turn, 5.6M-char data URL. Text/code files (5MB) inline as fenced blocks ahead of the prompt; a composed prompt over 16k chars is refused before the run is POSTed. The composer shows the pending attachment summary; the list clears when the run owns it and a declined workspace-trust challenge restores it intact. Temp clipboard files (`%TEMP%\ru-img-*`) are deleted on send or chat exit. A vision-name pre-check only warns; the backend remains the source of truth and sends an explicit unavailable-image notice to text-only models while keeping the chat and stored attachments.
- Office documents (`.docx`, `.xlsx`, `.pptx`) are unsupported as attachments. Keep them in the workspace and ask Ru to process them through installed terminal tools or optional skills. Ru does not bundle an Office converter or authoring suite; tool permissions still apply.
- The pinned header shows the OpenCode-style usage cluster (`39,413  20%  ($0.29)`) when the active model's catalog publishes a context window and rates: the percent tracks the largest model step this turn (like the GUI meter) and the cost accumulates at the published per-million rates; both need a `models.dev`-style catalog and stay off when it publishes nothing. Session cost is session-level.
- The backend `chat`/`run` start is a **managed host** shared by CLI, GUI and RPC: it stops itself after 10 minutes with no request in flight, no recent request and no registered run (`RU_HOST_IDLE_MINUTES`, default `10`, `0` disables), so exiting the terminal no longer leaves a detached backend running. The next command starts it again automatically; a run still in progress (even parked) keeps it alive.
- An untrusted workspace requires an explicit trust decision before execution. Terminal.Gui shows trust/cancel in the composer with cancel selected by default; Enter or Escape never grants access implicitly. Declining cancels the run, so it stops holding its session. Trust is scoped to the canonical workspace identity; moved or recreated roots require a new decision. Redirected/JSON output reports `workspace_trust_required`; resolve it explicitly with `ru agent trust <run> --approve` or `--deny`.
- A trust challenge expires after ten minutes, and the host now notices while it is running rather than only at the next startup: the runtime loop cancels the expired run with `terminal_error_code = workspace_trust_expired` and frees the session. Trust is a pre-execution gate, so cancelling cannot roll back a tool side effect. Previously only the startup sweep could do this, so a long-lived desktop host left the run — and its session — parked indefinitely.
- A turn that completes with no assistant text and no tool row says so: `▲ no text arrived from the model · the run completed but its reply never reached this terminal · retry, or switch model with /model`. Reply text is persisted even when its delta never reaches this client, so a finished run that delivered nothing used to end in total silence — just a `✓ idle · 10.6s` row, which reads exactly like a very short answer. The note is deliberately withheld when the turn did stream text or did run tools, because there the operator can already see what happened. This reports the observation; it does not recover the missing text.
- Assistant text arrives only on transient frames, and the live event hub keeps no replay buffer: a frame published before the client subscribes is dropped. So every finished turn is reconciled against the run's own durable `assistanttext`/`utf8text` payload, and a missing tail is reprinted. Payload kind controls filtering, so JSON answers with `tool_calls` and `finish_reason` keys remain valid prose. A missing **head** cannot be repaired — a terminal cannot unprint, and appending the head after the tail would interleave into a mangled sentence — so the turn reports `assistant_text_truncated: N/M characters reached this terminal` and points at the session, which always holds the whole reply.
- A model's own deliberation never enters the transcript: the classic renderer leaves one `· thought · ctrl+o` row and keeps the text behind `ctrl+o`; the Terminal.Gui transcript keeps the text behind `ctrl+o` without adding a row, matching the WinUI surface, where the thinking block is closed by default.
- Assistant prose is rendered, not printed: fenced code becomes verbatim rows behind a `│` gutter, inline emphasis and links render in place (CommonMark, via Markdig), and headings, bullets, ordered items, quotes and rules lose their markers. The inline markers are:

```text
**bold**   *italic*   `code`   ~~struck~~   [label](https://example.com)   \*escaped\*
```

  Tables render as aligned columns (numeric columns right-align); nested lists fall back to indented text. Consecutive prose lines are reflowed into one paragraph, and a blank line or any other block form ends it — a model that hard-wraps at 80 columns no longer leaves a ragged column in a wider terminal. Because a streamed delta can cut a marker in half, only the trailing run of marker characters is held back for the next delta — so the text always arrives complete and in order, even when an emphasis spanning a delta boundary loses its styling.
- Tool rows read as a sentence: provider names are nouns (`View`, `Terminal`), so a table of verbs turns them into `read`, `listed`, `searched`, `edited`, `wrote`, `ran`, `loaded` and so on. The feed groups them: consecutive settled calls share a two-cell indent and close with a summary row (`4 tools · 9.6s`, prefixed `✗` when one failed), each row orders outcome glyph, muted name, brightest target and right-aligned metrics with a unit that follows the tool (`132 lines`, `3 matches`, `+18 −2`, `1.2s`), and the live row uses the same geometry so it settles in place. The summary trails the block because a terminal can only append; nothing is inserted above rows already printed. The live row uses the present tense (`⠋ reading app.cs`) and the settled row the past (`✓ read app.cs`). An unrecognised name keeps its own name, and a provider's name outranks the activity kind so precision is not lost.
- Information surfaces share one framed panel: `/help` lists every command grouped by area (generated from the reader's own command table), and `/status` prints the session card — session id, preset, backend, model, API-key state, turns, tools, tokens and the workspace leaf.
- `/model` without an argument lists the live provider catalog for the active backend (current model first, then free models, then the rest A–Z, then dimmed unsupported rows; the footer names the backend and the row count as `backend · N rows`), falling back to configured models when the catalog is unreachable with a dim `catalog unavailable (<reason>) - showing configured models` line, the same reasoning `/backend` prints; pick with `↑↓`/digits/`Enter`, `Esc` cancels. Free-tier models carry a `free` tag (OpenRouter `:free` suffix) and long ids keep both ends so shared prefixes stay apart. Dropdown label columns size to the longest label on wide terminals (narrow screens clip with `…`); short lists keep the classic 12-cell column.
- `/model` and `/backend` without an argument open a picker (`↑↓`, digits, `Enter`, `Esc`); choosing one updates the settings immediately. `/backend openrouter` and `/model <id>` also work with explicit arguments. With no interactive console they print the same information as a panel; `Esc` cancels, and the Terminal.Gui frame reprints a cancelled bare form's options because its picker has no scrollback behind it.
- Starting `chat` or `run` against a `models.dev:*` backend makes one best-effort catalog check. A model id the live catalog does not list prints one bounded `model_not_in_catalog: <id> - it may fail with a 404; choose another with /model` notice; an unreadable catalog prints `model_check_unavailable: <reason>`. The run continues either way — the catalog can be stale — and OpenAI, Anthropic and custom backends, whose id spaces are not catalog-bound, are not checked.
- `/provider` reads the live provider catalog (`GET /models-dev`), with built-in and fallback choices when the catalog is unavailable. Choose a provider first, then enter its API key in the separate masked box. `/provider <name>` opens the key box directly.
- Every picker can be filtered by typing: letters and the id separators `-` `_` `.` narrow the rows by substring across label, detail and value, `Backspace` drops one character, and the footer reports `filter: <query> · <matched>/<total>`. Digits stay the numbered-pick shortcut, and with an empty filter every picker behaves exactly as before. A query that matches nothing shows one disabled `no match` row instead of an empty list.
- `/reasoning` shows or sets the active model's reasoning effort from the live catalog. Bare `/reasoning` opens a picker of `default` plus the levels that model actually publishes, or explains that the model offers none; `/reasoning <effort>` writes it directly and, like `/backend`, needs no terminal. The save is `POST /settings` carrying exactly `reasoning_effort`, `reasoning_model` and `reasoning_identity` — the identity is the catalog snapshot the picker drew, so a save can never land on a different selection. `default` is always accepted, because the host only resolves a non-default effort against the catalog.
- Provider setup saves the selected provider and key together through one `POST /settings` only after confirmation. `Ctrl+V` pastes the key into the masked box; Escape cancels without changing either. An empty entry keeps a known saved key; otherwise it saves nothing, except for custom endpoints where a key is optional. Keys use existing credential storage and never enter the transcript or command history. `/provider remove <name>` signs out. JSON refuses interactive key entry; without a picker the sequential path lists providers. Legacy `/backend` and `/key` commands remain callable but are hidden from help/completion. `/model` continues to select the model; `/provider` no longer aliases it. Saving a key does not verify it with the provider.
- Scrollback uses PageUp/PageDown, Ctrl+Up/Down for individual rows, Ctrl+Home/End for top/tail, and the mouse wheel. The scrollback hint stays visible while a model turn runs; Session replay resets it with the transcript. Retention limits and queued-submission ownership remain the existing ones.
- A workspace-busy rejection names the blocking run in a persistent hint with `ru agent cancel <run-id>`. Typing and failed submissions do not dismiss it; an accepted run or successful session switch does. Transcript diagnostics also retain the blocking run and follow/continue/cancel advice.
- Explicit OpenAI HTTP 429 quota codes (including insufficient quota and exhausted credits/spend limits) report `provider_quota_exhausted` and park without automatic retries. Restore provider credits or limits before Continue; a zero remaining-request header alone remains ordinary throttling.
- Provider HTTP 402 and Responses streamed payment errors report `provider_payment_required` without transient retries. The remedy names provider billing/credit instead of assuming a bad key or model. Provider/model selection stays pinned for the run.
- Terminal.Gui relayouts and redraws after screen-size changes. Startup selection remains 80×24; an already active frame stays available while resizing and hands control to append-only output only after remaining below 20×12 for 400 ms. Transcript bounds and detached scrolling survive layout changes. `RU_TUI_DEBUG=1` writes diagnostic events to `%TEMP%\ru-tui-debug.log`; a file path selects another diagnostic destination. Native terminal resize and IME acceptance are recorded separately from headless tests.
- `session list`, `session rewind` and `session delete` provide explicit command-line controls for listing sessions, time-travel rewinds, and deletion. `session delete <session-id>` asks for a `y/N` confirmation (default No) and only `--yes` skips it; a partial cleanup from the backend (`Session đã xoá nhưng artifacts còn sót…`) surfaces as `session_delete_failed` bounded to one line instead of reading as success.
- `session import <file>` reads Claude Code and Codex JSONL transcripts, Antigravity's `transcript.jsonl`, and opencode's `.db` store (naming the session when more than one is present). JSONL imports are size-checked before read: a file above **16 MiB** is refused with one bounded `import_too_large` line; an opencode `.db` store is checked on the payload after extraction, never on the whole database, and only the CLI reads it. `session export` writes Claude Code and Codex formats only, by design. The desktop picker imports `.jsonl` and points opencode users at `ru agent session import <opencode.db> --harness opencode`.
- `rpc` runs the stdio JSON-RPC 2.0 bridge for external tools (`ru agent rpc` takes no further arguments); it reuses the same loopback host and bearer as every other command and adds no HTTP route. See [the RPC bridge contract](../../docs/operations/rpc.md).
- `run` creates a single-shot Agent run. If `--session` is omitted, the tracer creates one dedicated session via `POST /sessions/new`, prints `session_id` and `run_id`, and sends a fresh `idempotency_key` (32-char hex GUID). Workspace is canonicalized and bounded to 512 chars; prompt is bounded to 8192 chars; max argument count is 64.
- `--preset` selects one canonical, session-scoped profile: `plan` is read-only — workspace read/Web auto, terminal/mutation denied outright, external structured access asks; `standard` auto-allows workspace mutation and executable/shell launches from a workspace cwd; external structured paths and external launch directories ask; `full_access` (alias `full`) is the skip-all mode — external structured files, arbitrary terminal, and structured protected paths (harness key/database files and runtime descriptors) are automatic, while hard-invalid requests remain denied. Repository cleanliness is not an input.
- Workspace trust is explicit once per canonical identity. `--trust-workspace` approves the returned identity and continues the same run; without it, an untrusted identity returns `workspace_trust_required` without prompting, and the notice names both recovery options. A moved or recreated identity requires trust again: `--trust-workspace` re-approves the new identity in one step, so unattended runs recover the same way.
- `--preset full_access` requires `--confirm-full-access`; without it the command returns `full_access_confirmation_required` before session creation or preset mutation. With it, the existing policy route receives `X-Interactive-Confirmed: true`. Terminal children run as the current Windows user, can access data outside the workspace and network resources, and have no OS filesystem/network confinement; timeout, output, cancellation, and process-tree controls remain.
- Inside Ru, `/preset` opens a picker, and `/preset plan` or `/preset standard` selects directly. `/preset full_access` requires a separate confirmation that defaults to keeping the current preset. The choice applies to the next new turn through the same policy update/rollback path; running, queued or retained runs cannot have their permission intent changed in place. The top header and `/status` show the selected next-turn preset.
- Preset updates retry expected versions `0..3` and bind the positive returned version to the run. The CLI creates a durable snapshot token before mutation; the Host persists prior/applied selection and token atomically, enforces capacity without evicting active snapshots, and restores with CAS/single-use semantics. A malformed or lost mutation response uses the known token for safe restore; a lost restore response can replay only while the restored state still matches, otherwise the command fails closed before streaming. Failed creation or trust handling restores the exact prior selection, including a new session with no prior selection.
- `--json` is accepted with explicit `--trust-workspace` and, for Full Access, explicit `--confirm-full-access`; it never auto-approves an approval or workspace.
- `attach` streams an existing run's durable events from `after_sequence=0` via `AgentSseClient`.
- `trust` wraps `POST /agent/v1/runs/{runId}/trust`.
- `approve` and `deny` wrap `POST /agent/v1/runs/{runId}/approvals/{approvalId}` with `allow_once`, `allow_session_directory` (external folders only), `allow_always` (authorizes once and remembers a bounded pattern as a durable project rule — a terminal command prefix, or an approved external folder as a path pattern; `rule_asks` and `terminal_protected_path_requires_approval` save no rule and degrade to `allow_once`), or `deny`. The Host sends the command that matches the pending kind (session-scoped directory grant for `external_directory_required`, ephemeral token for `terminal_external_cwd_requires_approval`, `terminal_protected_path_requires_approval`, or `rule_asks`). An allow of a rememberable call also remembers the approved intent in the Host's in-process session ledger (the durable pattern when one was saved, the exact intent otherwise), so a later matching call in the same session authorizes without another prompt (the memory dies with the session or a host restart; external-directory approvals keep the session grant plus the durable rule and write no ledger entry). The interactive approval offers the session folder and the project remember for `external_directory_required`. `terminal_timeout_requires_approval` remains the standard-window gate below `full_access` and `web_approval_required` remains a compatibility reason for explicit ask rules; timeout above the protocol hard maximum is denied. `agent approve --always` sends `allow_always`; no reusable approval token is ever created. Approval is first-writer wins; second concurrent response returns `approval_stale` (409). `approve` and `deny` accept `--json`: approval is answered explicitly by id, so `--json` only selects the output format (a versioned `approval` object with `decision`, `scope`, `authorized` and the matcher preview when the Host returns one). `agent run --json` prints the pending `approval_id` on its `approval.requested` event, which is the handle a headless orchestrator passes back to `agent approve`/`deny`.
- `answer` wraps `POST /agent/v1/runs/{runId}/inputs/{inputId}` with the user's answer to an `ask_user` question (1..8192 characters). Without the answer argument it prompts; `--json` requires the argument because it must never prompt, and prints a versioned envelope carrying `run_state`. A replayed, foreign, or already-answered input id returns `input_stale` (409), an unknown run `run_not_found` (404), and an invalid body `invalid_answer` (400).

Interactive Terminal.Gui chat opens `ask_user` questions inside Ru. Tab/Shift+Tab
cycles suggestions into an editable answer; Enter sends, Ctrl+J/Shift+Enter adds a
newline, and PageUp/PageDown scrolls a long question. Free text and text paste are
supported without a suggested option. Escape closes the question without answering
or cancelling the run and pauses queued prompts. `/follow [run-id]` reopens the
pending question, including after reopening its session. Answers use the existing
run/input endpoint and never create a new prompt or change the run's model/preset.
Stale or uncertain submissions stop for explicit `/follow` recovery instead of
retrying automatically. Sequential and JSON chat keep the external `agent answer`
flow; JSON never opens an input panel.

`/tasks` opens the session's background terminal task list, including while a model
turn streams. Rows show the process label, execution ID, state and available exit
code. Select a running row, choose **Stop task** (the default is **Keep running**),
and refresh to observe its final state. Finished rows have no stop action. **Refresh**
reloads on demand; there is no background polling. Escape closes the current panel.
`/tasks kill <execution-id>` targets one task directly with the same interactive
confirmation; `/tasks <run-id>` selects a run in the current session, useful after
reopening a session. With no retained target, Ru discovers the newest session run.
Missing Broker/gateway state is reported as unavailable, not an empty task list.
The task control lane preserves pending attachments and the active model run/queue.
While task control is open, finish it before submitting another command or prompt.
Sequential/JSON chat lists tasks as text/JSON; an explicit `kill` argument sends the
stop without reading stdin. All operations use the existing Host command endpoint
and Broker session registry; the CLI never kills local process IDs itself.

`/changes [run-id]` summarizes available completed edit activities for the current
run, or the newest session run when no target is retained. An explicit run must
belong to the current session. Select a filename group to inspect its recorded
before/after previews, then Escape returns to the list. Refresh reloads on demand;
there is no polling, filesystem read, Git diff, restore or model request. It shares
the task inspection lane and works during streaming without consuming attachments
or changing the model turn/queue. Preview and question/approval panels are serialized.
Sequential/JSON chat prints a text/structured summary without opening a panel.

Counts describe completed edit operations, not net changes on disk. A missing
count stays unknown (`+?`/`−?`, JSON null); duplicate activity IDs use their newest
state before filtering failures. The existing Host activity API returns filenames
only and currently caps replay at 200 activities. Groups can combine different
paths with the same filename; they are not a unique physical-file count. This
scope is explicit in the UI/JSON. Terminal/external mutations may be absent, and
previews can be truncated or unavailable. Ru never substitutes current disk data
for missing historical evidence. The picker shows up to 100 filename groups;
preview display is bounded to 2000 rows plus an omission notice.

`/history` browses saved **user turns**, newest first (up to 100). Select a turn:

- **View turn** shows saved prompt/reply text and available linked tool/change
  activity. Evidence is bounded and historical, never a claim about current files.
- **Edit and try again** restores the saved prompt into the composer and stages
  a branch **before** that turn. Edit it, then send to create the branch.
- **Continue from here** stages a branch **after** the selected turn; write your
  next request and send to create it.

The original session stays intact. Opening, viewing, or cancelling the picker
creates no session. `/history cancel` discards the staged branch, keeping your
current input. `/history <turn> view|edit|continue` supports explicit selection
in sequential/JSON chat; a staged edit prints/returns the saved draft for the
caller to edit and send. Bare sequential/JSON `/history` only lists turns.
Numbers refer to available saved user turns, not hidden tool/message rows.

**Branches use current files and current selected model/provider/preset.** They
copy conversation text only: no old images, runs, approvals, grants, background
processes or work state. Reattach images when needed. Oversized/control-bearing
saved prompts cannot be restored into the editor; Continue remains available.
Finish pending runs/queues first. The hidden `/rewind` TUI alias opens the same
history workflow; its former numeric `--yes` truncation syntax is refused.
The separate headless `ru agent session rewind` API remains unchanged.

An authenticated `POST /sessions/{sessionId}/fork` compares `history_revision`
from session activation and writes the new prefix in one SQLite transaction.
A changed history is rejected; select again. A lost/invalid acknowledgement is
never automatically retried and starts no model run. Choose/reload a session via
`/sessions id:<id>` before sending again; an unacknowledged child may be visible
in `/sessions`. Current attachments survive recovery and are not copied from old
turns. A failed branch send requires a validated reload before choosing again.
The same-session id reload works while paused and returns the failed draft and
attachments for manual resubmission; it never replays queued prompts. Native
inspection and branch creation block overlapping sends before attachment admission.

- `policy list` wraps `GET /agent/v1/policies` (scope filter). `policy import --global FILE` validates versioned JSON (rejects unknown version/fields, normalizes and displays matchers/decision before confirmation) and posts each rule with `X-Interactive-Confirmed: true`; sensitive matcher values are encrypted at rest via Host API. `--json` cannot bypass confirmation. `policy remove` wraps `DELETE /agent/v1/policies/{ruleId}` and requires interactive confirmation for global rules.
- `cancel`/`continue`/`status` are thin HTTP wrappers.
- `run` and `attach` return exit 2 at a current durable `waiting_user` or `reconciliation_required` boundary. The run stays parked on the Host; use `ru agent continue <run-id>` or `ru agent cancel <run-id>`. Historical park events from a resumed run do not detach the follower. Under `--json`, stdout remains NDJSON and human recovery guidance goes to stderr.
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

The Agent model-step advertisement selects Protocol definitions from its pinned
`coding_v2`, `patch_v2` or `coding_code_v1` profile. See the
[current tool inventory](../../docs/operations/agent-tools.md#current-capability-matrix)
for explicit file, Windows terminal, Web, question and orchestration tools.
A provider-native model step may return
optional narration and one or more ordered calls; the compatibility text path
still emits exactly one JSON selection. The shared budget is 256 tool calls per
window, read-only concurrency is capped at eight, and mutation/terminal calls
remain sequential. Durable approval resume re-evaluates retained proposals
without a new model proposal, and an `ask_user` park resumes the same way once
`ru agent answer` completes the asked call.

The [canonical tool policy](../../docs/operations/agent-tools.md#current-capability-matrix)
owns preset decisions, explicit-rule precedence and session/project approval scope.
Terminal children run as the current Windows user without an OS filesystem or
network sandbox. Timeout, output, cancellation and process-tree bounds remain.
Time, machine status, app discovery,
and optional volume commands use the terminal; notes use `write_file`.
`apply_patch` is available in the patch and programmatic profiles; `app.open`
is not model-usable. Managed
Web results are bounded and marked untrusted. Workspace SKILL.md bodies are
prompt guidance only and grant nothing; a `requires: python` frontmatter entry
fails the model's `skill` load closed with `python_unavailable` when Python is
not on PATH. LSP, extensions, plugins,
and MCP are not part of this CLI surface. All commands ensure the Public Host
is running through `PublicHostStarter.GetOrStartAsync` and authenticated
`PublicHostSession`.

## Rendering

- **Interactive** (`SafeEventRenderer`): displays only safe envelope fields (`protocol_version`, `event_id`, `run_id`, `session_id`, `sequence`, `event_type`, `occurred_at`, `status`, `safe_summary`, `payload_ref`). Never fetches private payloads; `payload_ref` is shown opaque.
- **JSON** (`NdjsonEventRenderer` with `--json`): versioned NDJSON `{"v":1,"type":"event",...safe fields}`. Never prompts, never auto-approves, never auto-fetches private payload, and never emits `prompt`/`command`/`argv`/`path`/`code`/`diff`/`output`/`secret`/`stdout`/`stderr`/`environment` fields. Full Access is accepted only with explicit `--confirm-full-access`; `--trust-workspace` is the explicit non-interactive trust confirmation and is allowed with `--json`, while approval and global-policy confirmations remain rejected with `--json`. Under `--json`, slash-command surfaces write their human text to stderr, bare picker commands refuse with a bounded `interactive_required`, and a chat run that reaches an approval request ends the chat (exit 0) with a bounded `approval_required` notice on stderr naming `ru agent approve <run-id> <approval-id>` or `ru agent deny <run-id> <approval-id>` — the run stays waiting server-side and is never auto-denied; an `ask_user` park ends it the same way with an `input_required` notice naming `ru agent answer <run-id> <input-id> "<answer>"`. A `--json` chat stream may include blank separator lines between turns, so consumers should skip empty lines. A transient frame's raw `data` is therefore withheld and reported as `data_bytes` — its bounded size, not its text — so the machine-readable trace never carries streamed assistant text or tool output.
- **Live terminal frames**: transient `tool.output.delta` frames render as `[transient:<seq>] <type> <preview>` with the preview bounded to 200 chars; any forbidden field (`command`/`output`/`secret`/...) is replaced with `[redacted]`. Terminal output redaction also seeds from every nonempty secret environment value stripped before child launch, including provider API keys; child processes do not inherit those values. A `transient.frames_dropped` event renders an explicit `WARNING dropped-frames` notice. Final tool output is referenced only by opaque `payload_ref`; the actual head/tail bytes are fetched solely by the explicit `agent output` command.

## Cancellation

Interrupting a chat turn is server-side, not just a local detach: Escape during an active turn, after an open overlay handles the key, and `Ctrl+C` cancel the turn and `POST` `/agent/v1/runs/{runId}/cancel` (best-effort), so the run releases its session. Idle Escape never exits the chat or discards the draft. Success prints `Run interrupted by user · cancelled <run-id>.`; a failed cancel prints `run_cancel_failed` plus the manual `ru agent cancel <run-id>` fallback; a run that parked itself is left for typed `Continue` instead of being cancelled. `Ctrl+C` also cancels the SSE stream and `POST`s `/agent/v1/runs/{runId}/cancel` before exit for `attach`/`run`. Under `--json` there is no footer to prompt in: an approval request ends the chat (exit 0) with the bounded `approval_required` notice instead, and the run stays `waiting_approval` server-side until `ru agent approve`/`deny` answers it from another terminal.

## Bounds

- Session export: 128 MiB serialized JSON envelope, shared with Host and GUI.
  JSON is read from a bounded stream; the general 8 MiB API cap does not apply.
  Oversized exports return `export_too_large` before any file is written.
- Running terminal tasks: default 8 per session, configurable from 1–64 with
  `RU_MAX_BACKGROUND_TERMINAL_TASKS` before Host startup. Foreground launches
  reserve a slot and keep it on background promotion; a full session refuses
  new launches without stopping existing tasks.
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
`use /provider <name> to sign in` instead of leaving a silent absence.

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

## Installed CLI updates

`ru update --check` checks the selected channel; `ru update` asks before
installation and `ru update --yes` explicitly installs. `/update` in TUI is
check-only unless `--yes` is supplied. Stable is the default;
`--channel preview` opts into prereleases. Source builds use `build.bat` instead.
See [distribution and updates](../../docs/operations/distribution-and-updates.md).

### Subagent tasks

Use `/subagents [run-id]` to inspect coding/review children and their pinned models,
results and usage. Add `agent-id result [version]` to read a versioned result.
Only the main agent can send, cancel or integrate child work. Generic child run
mutations and approvals are refused. RPC exposes `subagent.list` and `subagent.get`.
In the Terminal.Gui chat a live strip on the header's workspace row lists the root's children (`◐` running, `◇` waiting, `✓` finished, `✗` failed, `○` stopped; active ones first, `+N` for those that do not fit), and the transcript gets one line when a child starts, finishes, fails or is stopped. Sequential chat prints the same lines; `--json` stays silent. Both follow the loss-tolerant `subagent.changed` hints, so a missed hint only skips an intermediate line; `/subagents` reads the durable list.
See [subagent operations](../../docs/operations/subagents.md).

## Custom slash commands

Put a Markdown template at `<workspace>/.ru/commands/<name>.md` (or `~/.ru/commands/<name>.md`) and run it as `/<name> [arguments]`. Names are lowercase letters, digits, `_` and `-`. `$ARGUMENTS` in the template is replaced by the text after the command; without it, the text is appended after the template. An optional `---` front matter may carry `description:`. Built-in commands win over templates, workspace templates win over user templates, and files over 64 KB, links or non-UTF-8 files are ignored. The expanded text is sent as an ordinary prompt: a template grants no tool, path or approval authority.
