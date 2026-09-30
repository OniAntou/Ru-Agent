# RuAgent CLI — Headless Coding-Loop Tracer (Phase 3)

Thin headless client for the Agent v2 headless coding loop (Phase 3). Delegates runtime to the Public Host over authenticated HTTP/SSE; contains no Worker, Broker, model-loop, policy, or adapter code.

## Commands

```text
agent chat [--preset plan|standard|full_access] [--workspace PATH] [--session <id>] [--json] [--classic] [--trust-workspace] [--confirm-full-access]
agent session list [--json]
agent session rewind <session-id> --index <n> [--json]
agent run [--preset plan|standard|full_access] --workspace PATH "prompt" [--session <id>] [--json] [--trust-workspace] [--confirm-full-access]
agent attach <run-id> [--json]
agent trust <run-id> --approve|--deny
agent approve <run-id> <approval-id>
agent deny <run-id> <approval-id>
agent cancel <run-id>
agent continue <run-id>
agent command <run-id> "<slash-command>" [--json]
agent reconcile <run-id> [--outcome applied|not-applied|unknown] [--hash SHA256] [--json]
agent output <run-id> <payload-id> [--json]
agent status <run-id> [--json]
agent policy list [--global | --workspace PATH] [--json]
agent policy import --global FILE
agent policy remove <rule-id>
```

- `chat` (or `repl`) launches an interactive Terminal User Interface (TUI) with real-time ANSI-styled streaming, command history (Up/Down arrow navigation), Tab autocompletion for slash commands, a live status row (state, current activity, elapsed, turn/token/tool counters), multiline prompt continuation (`\`), inline tool approvals (`approval.requested`), and slash commands (`/model`, `/backend`, `/rewind`, `/status`, `/history`, `/clear`, `/help`, `/exit`).
- While a turn streams, keystrokes are captured in the pinned footer: Enter queues one prompt (`PendingPromptSlot`) and sends it when the turn ends, Escape clears the draft then cancels the run, `Ctrl+O` expands the last collapsed tool/diff block. A pending approval takes over the footer with a choice list (`↑↓`/digits to move, Enter to confirm, Escape to deny).
- `--classic` opts out of the sticky footer and keeps the sequential layout where the composer only appears between turns. The sticky footer is also disabled automatically when stdout is redirected, when `TERM=dumb`, and with `--json`.
- A new run whose workspace identity has not been trusted **asks in the composer**, on the same frame and keyboard a tool approval uses: the status row reads `▲ workspace needs trust · <path>` and the choice list offers `1 · trust` or `2 · cancel`. Nothing is executing while the challenge is open, so the type-ahead loop can hand the keyboard over safely — the earlier design wrote an instruction into the transcript and left the turn live, which made the row spin `Thinking` over a run that could not reply, and the instruction itself was repainted away. Trust is still an explicit human decision: the prompt starts on **cancel**, a bare Enter or Escape never widens what the agent may read, and nothing is auto-approved. Declining cancels the run, because the challenge is single-use and a parked run holds its workspace's active-run lock. Trust is a one-time grant per canonical workspace identity and the host stores it, so `bin/ru.cmd` runs normally afterwards and never asks again; a moved or recreated identity asks once more. Without a composer (redirected stdout, `--json`, `--classic`, a terminal too small for the sticky footer) the CLI stays non-interactive, never reads stdin, and reports the bounded `workspace_trust_required` with the `ru agent trust <run> --approve` fallback.
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
- `/model` without an argument lists the live provider catalog for the active backend (current model first, then free models, then the rest A–Z, then dimmed unsupported rows; the footer names the backend and the row count), falling back to configured models when the catalog is unreachable; pick with `↑↓`/digits/`Enter`, `Esc` cancels. Free-tier models carry a `free` tag (OpenRouter `:free` suffix) and long ids keep both ends so shared prefixes stay apart. Dropdown label columns size to the longest label on wide terminals (narrow screens clip with `…`); short lists keep the classic 12-cell column.
- `/model` and `/backend` without an argument open a picker (`↑↓`, digits, `Enter`, `Esc`); choosing one updates the settings immediately. `/backend openrouter` and `/model <id>` also work with explicit arguments. With no interactive console they print the same information as a panel, and `Esc` cancels silently.
- `/backend` reads the provider list live from `GET /models-dev` instead of a fixed list, so every models.dev provider is reachable; rows run base providers first (`openai`, `anthropic`, `custom`), then the current provider, then the ones already signed in, then the rest by id. A line above the picker says either `N providers from models.dev` or `models.dev list unavailable (<reason>) - showing the known providers only`, because a silently short list is indistinguishable from a failed fetch. A failed fetch falls back to the four providers the CLI has always known rather than emptying the picker.
- Every picker can be filtered by typing: letters and the id separators `-` `_` `.` narrow the rows by substring across label, detail and value, `Backspace` drops one character, and the footer reports `filter: <query> · <matched>/<total>`. Digits stay the numbered-pick shortcut, and with an empty filter every picker behaves exactly as before. A query that matches nothing shows one disabled `no match` row instead of an empty list.
- `/reasoning` shows or sets the active model's reasoning effort from the live catalog. Bare `/reasoning` opens a picker of `default` plus the levels that model actually publishes, or explains that the model offers none; `/reasoning <effort>` writes it directly and, like `/backend`, needs no terminal. The save is `POST /settings` carrying exactly `reasoning_effort`, `reasoning_model` and `reasoning_identity` — the identity is the catalog snapshot the picker drew, so a save can never land on a different selection. `default` is always accepted, because the host only resolves a non-default effort against the catalog.
- `/key` manages provider API keys without leaving the terminal: bare `/key` shows the sign-in state (legacy providers plus signed-in `models.dev` ids), `/key <provider>` opens a masked panel (one `•` per character, count only, never content) and prompts for the key with no echo (`openai`, `anthropic`, `custom`, `openrouter`, or `models.dev:<id>`; `Ctrl+V` pastes from the clipboard since raw console input swallows the terminal's own paste binding, pasted text is trimmed to the first line, `Esc` cancels, empty input saves nothing), and `/key remove <provider>` signs out via `POST /settings/provider/remove`. Keys travel only inside the `POST /settings` body to the Windows Credential Manager and are never printed, logged, or stored in history. Non-interactive runs report `interactive_required` instead of prompting. Free-tier example: `/backend openrouter`, then `/key openrouter`, then `/model cohere/north-mini-code:free`.
- Terminal resizes are debounced (180 ms); once the window settles the whole screen is rebuilt (region reset, fresh header, transcript tail reprinted from a bounded line history) rather than patched, because the terminal has already reflowed the rows the UI addressed absolutely. Nothing is drawn while the layout is known to be stale, and a window too short for a footer leaves sticky mode instead of painting rows that do not exist. `RU_TUI_DEBUG=<path>` writes a layout trace for diagnosing this on a real terminal.
- `session list` and `session rewind` provide explicit command-line controls for listing sessions and time-travel rewinds.
- `run` creates a single-shot Agent run. If `--session` is omitted, the tracer creates one dedicated session via `POST /sessions/new`, prints `session_id` and `run_id`, and sends a fresh `idempotency_key` (32-char hex GUID). Workspace is canonicalized and bounded to 512 chars; prompt is bounded to 8192 chars; max argument count is 64.
- `--preset` selects one canonical, session-scoped profile: `plan` allows workspace read/Web and denies terminal/mutation; `standard` auto-allows bounded-local in-workspace mutation and asks for external/escape/risky intent; `full_access` (alias `full`) allows external structured files and arbitrary terminal while structured protected paths remain denied. Repository cleanliness is not an input.
- Workspace trust is explicit once per canonical identity. `--trust-workspace` approves the returned identity and continues the same run; without it, an untrusted identity returns `workspace_trust_required` without prompting. A moved or recreated identity requires trust again.
- `--preset full_access` requires `--confirm-full-access`; without it the command returns `full_access_confirmation_required` before session creation or preset mutation. With it, the existing policy route receives `X-Interactive-Confirmed: true`. Full-access terminal runs as the current Windows user, can access data outside the workspace, and is not a network/path sandbox; timeout, output, cancellation, and process-tree controls remain.
- Preset updates retry expected versions `0..3` and bind the positive returned version to the run. The CLI creates a durable snapshot token before mutation; the Host persists prior/applied selection and token atomically, enforces capacity without evicting active snapshots, and restores with CAS/single-use semantics. A malformed or lost mutation response uses the known token for safe restore; a lost restore response can replay only while the restored state still matches, otherwise the command fails closed before streaming. Failed creation or trust handling restores the exact prior selection, including a new session with no prior selection.
- `--json` is accepted with explicit `--trust-workspace` and, for Full Access, explicit `--confirm-full-access`; it never auto-approves an approval or workspace.
- `attach` streams an existing run's durable events from `after_sequence=0` via `AgentSseClient`.
- `trust` wraps `POST /agent/v1/runs/{runId}/trust`.
- `approve` and `deny` wrap `POST /agent/v1/runs/{runId}/approvals/{approvalId}` with `allow_once`, `allow_session_directory` (external folders only), `allow_always` (authorizes once and remembers the exact intent as a project rule), or `deny`. The Host picks the right one-time token from the pending binding (session-scoped directory grant for `external_directory_required`, ephemeral token for `terminal_delete_requires_approval`, `terminal_external_mutation_requires_approval`, or `terminal_unknown_executable`). `terminal_timeout_requires_approval` and `web_approval_required` remain compatibility reasons for explicit ask rules; timeout above the protocol hard maximum is denied. `agent approve --always` sends `allow_always`; no reusable approval token is ever created. Approval is first-writer wins; second concurrent response returns `approval_stale` (409). `--json` cannot confirm approval.
- `policy list` wraps `GET /agent/v1/policies` (scope filter). `policy import --global FILE` validates versioned JSON (rejects unknown version/fields, normalizes and displays matchers/decision before confirmation) and posts each rule with `X-Interactive-Confirmed: true`; sensitive matcher values are encrypted at rest via Host API. `--json` cannot bypass confirmation. `policy remove` wraps `DELETE /agent/v1/policies/{ruleId}` and requires interactive confirmation for global rules.
- `cancel`/`continue`/`status` are thin HTTP wrappers.
- `command` forwards `/compact normal|aggressive`, `/help`, `/skills`, or
  `/skill <name>` through `AgentApiClient.DispatchCommandAsync`.
  Slash parsing is shared with the Public Host; malformed or unknown commands
  return stable `command_invalid`/`command_unknown` codes and are never sent as
  prompts. Human output prints only bounded metadata; `--json` emits the
  versioned response envelope without raw tool output.
- `reconcile` wraps `POST /agent/v1/runs/{runId}/reconcile`. Without `--outcome` it fetches the run (showing the `intent_code` and a fresh `snapshot_hash`), prompts for `applied|not-applied|unknown`, then posts the decision with the freshly read hash. `--outcome applied|not-applied|unknown` maps to wire tokens `applied`/`not_applied`/`unknown_continue`. `--hash` must be a 64-char hex SHA-256; a stale `--hash` returns `reconcile_stale` and exits non-zero. `--json` never prompts: it prints the info payload and exits without sending a decision; an `--outcome` given without `--hash` in `--json` mode fails locally (exit 1) and sends no request.
- `output` wraps `GET /agent/v1/runs/{runId}/payloads/{payloadId}` — the explicit user action that fetches final head/tail tool output bytes. Nothing in the tracer fetches private payloads implicitly; the event stream only references payloads by opaque `payload_ref`. The command writes the payload bytes exactly as the host returns them; `--json` is accepted for symmetry with the other commands but adds no envelope, because the payload is already the answer.
## Current model tool surface

The Agent model-step advertisement contains the twelve Protocol definitions:
`read`, `list`, `help`, `todo_write`, `grep`, `glob`, `write` with
`op=write|delete|restore`, `edit`, `bash` with `mode=run|shell`, standalone
`web_search`/`web_fetch`, and `skill`. A provider-native model step may return
optional narration and one or more ordered calls; the compatibility text path
still emits exactly one JSON selection. The shared budget is 256 tool calls per
window, read-only concurrency is capped at eight, and mutation/terminal calls
remain sequential. Durable approval resume re-evaluates retained proposals
without a new model proposal.

Canonical `plan` allows workspace read/Web and denies terminal/mutation.
`standard` auto-allows bounded-local in-workspace mutation and asks for
external/escape/risky intent. `full_access` allows external structured files
and arbitrary terminal while structured protected paths remain denied. Web is
automatic in all profiles; `web_approval_required` is only an explicit-rule
compatibility reason. Full-access terminal runs as the current Windows user and
is not a path/network sandbox. Time, machine status, app discovery, and optional
volume commands use the terminal; notes use `write` with `op=write`.
`files.apply_patch` and `app.open` are not model-usable. Managed
Web results are bounded and marked untrusted. Workspace SKILL.md bodies are
prompt guidance only and grant nothing. Subagents, LSP, extensions, plugins,
and MCP are not part of this CLI surface. All commands ensure the Public Host
is running through `PublicHostStarter.GetOrStartAsync` and authenticated
`PublicHostSession`.

## Rendering

- **Interactive** (`SafeEventRenderer`): displays only safe envelope fields (`protocol_version`, `event_id`, `run_id`, `session_id`, `sequence`, `event_type`, `occurred_at`, `status`, `safe_summary`, `payload_ref`). Never fetches private payloads; `payload_ref` is shown opaque.
- **JSON** (`NdjsonEventRenderer` with `--json`): versioned NDJSON `{"v":1,"type":"event",...safe fields}`. Never prompts, never auto-approves, never auto-fetches private payload, and never emits `prompt`/`command`/`argv`/`path`/`code`/`diff`/`output`/`secret`/`stdout`/`stderr`/`environment` fields. Full Access is accepted only with explicit `--confirm-full-access`; `--trust-workspace` is the explicit non-interactive trust confirmation and is allowed with `--json`, while approval and global-policy confirmations remain rejected with `--json`. A transient frame's raw `data` is therefore withheld and reported as `data_bytes` — its bounded size, not its text — so the machine-readable trace never carries streamed assistant text or tool output.
- **Live terminal frames**: transient `tool.output.delta` frames render as `[transient:<seq>] <type> <preview>` with the preview bounded to 200 chars; any forbidden field (`command`/`output`/`secret`/...) is replaced with `[redacted]`. A `transient.frames_dropped` event renders an explicit `WARNING dropped-frames` notice. Final tool output is referenced only by opaque `payload_ref`; the actual head/tail bytes are fetched solely by the explicit `agent output` command.

## Cancellation

`Ctrl+C` cancels the SSE stream and `POST`s `/agent/v1/runs/{runId}/cancel` before exit.

## Bounds

- Max arguments: 64
- Workspace path: 1..512
- Prompt: 1..8192
- Session id: 1..64 (alphanum, `_`, `-`)
- Run id: `run_` + 32 lower hex (36 total)
- Approval id: `apr_` + 32 lower hex
- Rule id: `rule_` + 32 lower hex
- Policy file: max 1 MiB, version 1 only, max 64 rules
- Error text: one-shot failures print `code: message` (server message, max 200 chars, single line), never raw JSON

## Failure catalog

Failed turns print a three-line card: title plus code, cause, and fix
(`TuiFailureCatalog`, the single source of failure copy for the terminal).
Covered families: provider auth/key/model (`catalog_unauthorized`,
`provider_not_active`, `provider_configuration_changed`, `forbidden`),
rate limits (`catalog_rate_limited` / `rate_limited`, HTTP 429),
transport (`timeout` / `transport_error`), provider errors
(`server_error` / `http_error` / `malformed_payload`), catalog reachability
(`catalog_unavailable` / `catalog_http_error`) and shape
(`catalog_malformed` / `catalog_too_large` / `catalog_configuration_error`),
`response_too_large`, `vision_unsupported`, `empty_model_reply`,
`model_failed` / `model_timeout` / `prompt_too_large` / `model_unavailable`,
model-backend reachability (historical `worker_unavailable` / `worker_failed`
codes, kept apart from
`provider_failed` / `generation_failed` / `drive_failed` execution failures),
run lifecycle (`repeated_tool_call`, `prompt_contract_invalid`,
`run_not_runnable`, `workspace_busy`, `reconciliation_required`,
`reconcile_stale`, approvals, `tool_failed`, store/preset/session/workspace
failures, `compact_failed`, `command_invalid` / `command_unknown`).
Codes outside the table still name themselves and point at
`%LOCALAPPDATA%\Ru\logs` plus `/status` instead of rendering bare.
The TUI cannot store keys yet: on a `models.dev:*` backend the `/model` card
points at GUI Settings → Models instead of a silent absence.

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
