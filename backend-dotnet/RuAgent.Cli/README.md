# RuAgent CLI — Headless Coding-Loop Tracer (Phase 3)

Thin headless client for the Agent v2 headless coding loop (Phase 3). Delegates runtime to the Public Host over authenticated HTTP/SSE; contains no Worker, Broker, model-loop, policy, or adapter code.

## Commands

```text
agent chat [--preset plan|standard|full_access] [--workspace PATH] [--session <id>] [--json] [--classic] [--trust-workspace] [--confirm-full-access]
agent session list [--json]
agent session fork <session-id> [--index <n>] [--title <title>] [--json]
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

- `chat` (or `repl`) launches an interactive Terminal User Interface (TUI) with real-time ANSI-styled streaming, command history (Up/Down arrow navigation), Tab autocompletion for slash commands, a live status row (state, current activity, elapsed, turn/token/tool counters), multiline prompt continuation (`\`), inline tool approvals (`approval.requested`), and slash commands (`/model`, `/backend`, `/fork`, `/rewind`, `/status`, `/history`, `/clear`, `/help`, `/exit`).
- While a turn streams, keystrokes are captured in the pinned footer: Enter queues one prompt (`PendingPromptSlot`) and sends it when the turn ends, Escape clears the draft then cancels the run, `Ctrl+O` expands the last collapsed tool/diff block. A pending approval takes over the footer with a choice list (`↑↓`/digits to move, Enter to confirm, Escape to deny).
- `--classic` opts out of the sticky footer and keeps the sequential layout where the composer only appears between turns. The sticky footer is also disabled automatically when stdout is redirected, when `TERM=dumb`, and with `--json`.
- Information surfaces share one framed panel: `/help` lists every command grouped by area (generated from the reader's own command table), and `/status` (also `/stats`, `/tokens`) prints the session card — session id, preset, backend, model, API-key state, turns, tools, tokens and the workspace leaf.
- `/model` and `/backend` without an argument open a picker (`↑↓`, digits, `Enter`, `Esc`) listing the models configured on this machine, or the fixed backends; choosing one updates the settings immediately. With no interactive console they print the same information as a panel, and `Esc` cancels silently.
- Terminal resizes are debounced (180 ms); once the window settles the whole screen is rebuilt (region reset, fresh header, transcript tail reprinted from a bounded line history) rather than patched, because the terminal has already reflowed the rows the UI addressed absolutely. Nothing is drawn while the layout is known to be stale, and a window too short for a footer leaves sticky mode instead of painting rows that do not exist. `RU_TUI_DEBUG=<path>` writes a layout trace for diagnosing this on a real terminal.
- `session list`, `session fork`, and `session rewind` provide explicit command-line controls for managing session branch trees and time-travel rewinds.
- `run` creates a single-shot Agent run. If `--session` is omitted, the tracer creates one dedicated session via `POST /sessions/new`, prints `session_id` and `run_id`, and sends a fresh `idempotency_key` (32-char hex GUID). Workspace is canonicalized and bounded to 512 chars; prompt is bounded to 8192 chars; max argument count is 64.
- `--preset` selects one canonical, session-scoped profile: `plan` allows workspace read/Web and denies terminal/mutation; `standard` auto-allows bounded-local in-workspace mutation and asks for external/escape/risky intent; `full_access` (alias `full`) allows external structured files and arbitrary terminal while structured protected paths remain denied. Repository cleanliness is not an input.
- Workspace trust is explicit once per canonical identity. `--trust-workspace` approves the returned identity and continues the same run; without it, an untrusted identity returns `workspace_trust_required` without prompting. A moved or recreated identity requires trust again.
- `--preset full_access` requires `--confirm-full-access`; without it the command returns `full_access_confirmation_required` before session creation or preset mutation. With it, the existing policy route receives `X-Interactive-Confirmed: true`. Full-access terminal runs as the current Windows user, can access data outside the workspace, and is not a network/path sandbox; timeout, output, cancellation, and process-tree controls remain.
- Preset updates retry expected versions `0..3` and bind the positive returned version to the run. The CLI creates a durable snapshot token before mutation; the Host persists prior/applied selection and token atomically, enforces capacity without evicting active snapshots, and restores with CAS/single-use semantics. A malformed or lost mutation response uses the known token for safe restore; a lost restore response can replay only while the restored state still matches, otherwise the command fails closed before streaming. Failed creation or trust handling restores the exact prior selection, including a new session with no prior selection.
- `--json` is accepted with explicit `--trust-workspace` and, for Full Access, explicit `--confirm-full-access`; it never auto-approves an approval or workspace.
- `attach` streams an existing run's durable events from `after_sequence=0` via `AgentSseClient`.
- `trust` wraps `POST /agent/v1/runs/{runId}/trust`.
- `approve` and `deny` wrap `POST /agent/v1/runs/{runId}/approvals/{approvalId}` with exactly `allow_once`, `allow_session_directory` (external folders only), or `deny`. The Host picks the right one-time token from the pending binding (session-scoped directory grant for `external_directory_required`, ephemeral token for `terminal_delete_requires_approval`, `terminal_external_mutation_requires_approval`, or `terminal_unknown_executable`). `terminal_timeout_requires_approval` and `web_approval_required` remain compatibility reasons for explicit ask rules; timeout above the protocol hard maximum is denied. No `--scope` flag is exposed and no reusable approval token is ever created. Approval is first-writer wins; second concurrent response returns `approval_stale` (409). `--json` cannot confirm approval.
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

The Agent Worker v2 advertisement contains the twelve Protocol definitions:
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
`files.apply_patch`, connectors, and `app.open` are not model-usable. Managed
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
