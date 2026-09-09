# RuAgent CLI — Headless Coding-Loop Tracer (Phase 3)

Thin headless client for the Agent v2 headless coding loop (Phase 3). Delegates runtime to the Public Host over authenticated HTTP/SSE; contains no Worker, Broker, model-loop, policy, or adapter code.

## Commands

```text
agent chat [--preset standard|full_access] [--workspace PATH] [--session <id>] [--json]
agent session list [--json]
agent session fork <session-id> [--index <n>] [--title <title>] [--json]
agent session rewind <session-id> --index <n> [--json]
agent run [--preset standard|full_access] --workspace PATH "prompt" [--session <id>] [--json]
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

- `chat` (or `repl`) launches an interactive Terminal User Interface (TUI) with real-time ANSI-styled streaming, command history (Up/Down arrow navigation), Tab autocompletion for slash commands, live animated thinking spinner, multiline prompt continuation (`\`), inline tool approvals (`approval.requested`), and slash commands (`/model`, `/backend`, `/fork`, `/rewind`, `/status`, `/history`, `/clear`, `/help`, `/exit`).
- `session list`, `session fork`, and `session rewind` provide explicit command-line controls for managing session branch trees and time-travel rewinds.
- `run` creates a single-shot Agent run. If `--session` is omitted, the tracer creates one dedicated session via `POST /sessions/new`, prints `session_id` and `run_id`, and sends a fresh `idempotency_key` (32-char hex GUID). Workspace is canonicalized and bounded to 512 chars; prompt is bounded to 8192 chars; max argument count is 64.
- `--preset full_access` (alias `full`) shows an interactive warning and requires confirmation; it changes the session's stored preset via `POST /agent/v1/policies` with `kind=session_preset` and requires header `X-Interactive-Confirmed: true`. `--json` cannot be used with `full_access`; the preset change affects the next run only and is not sticky across new sessions.
- `attach` streams an existing run's durable events from `after_sequence=0` via `AgentSseClient`.
- `trust` wraps `POST /agent/v1/runs/{runId}/trust`.
- `approve` and `deny` wrap `POST /agent/v1/runs/{runId}/approvals/{approvalId}` with exactly `allow_once`, `allow_session_directory` (external folders only), or `deny`. The Host picks the right one-time token from the pending binding (session-scoped directory grant for `external_directory_required`, ephemeral token for `terminal_delete_requires_approval`, `terminal_external_mutation_requires_approval`, `terminal_timeout_requires_approval`, `web_approval_required`, or `terminal_unknown_executable`); no `--scope` flag is exposed and no reusable rule is ever created. Approval is first-writer wins; second concurrent response returns `approval_stale` (409). `--json` cannot confirm approval.
- `policy list` wraps `GET /agent/v1/policies` (scope filter). `policy import --global FILE` validates versioned JSON (rejects unknown version/fields, normalizes and displays matchers/decision before confirmation) and posts each rule with `X-Interactive-Confirmed: true`; sensitive matcher values are encrypted at rest via Host API. `--json` cannot bypass confirmation. `policy remove` wraps `DELETE /agent/v1/policies/{ruleId}` and requires interactive confirmation for global rules.
- `cancel`/`continue`/`status` are thin HTTP wrappers.
- `command` forwards `/compact normal|aggressive`, `/help`, `/skills`, or
  `/skill <id> <json-object>` through `AgentApiClient.DispatchCommandAsync`.
  Slash parsing is shared with the Public Host; malformed or unknown commands
  return stable `command_invalid`/`command_unknown` codes and are never sent as
  prompts. Human output prints only bounded metadata; `--json` emits the
  versioned response envelope without raw tool output.
- `reconcile` wraps `POST /agent/v1/runs/{runId}/reconcile`. Without `--outcome` it fetches the run (showing the `intent_code` and a fresh `snapshot_hash`), prompts for `applied|not-applied|unknown`, then posts the decision with the freshly read hash. `--outcome applied|not-applied|unknown` maps to wire tokens `applied`/`not_applied`/`unknown_continue`. `--hash` must be a 64-char hex SHA-256; a stale `--hash` returns `reconcile_stale` and exits non-zero. `--json` never prompts: it prints the info payload and exits without sending a decision; an `--outcome` given without `--hash` in `--json` mode fails locally (exit 1) and sends no request.
- `output` wraps `GET /agent/v1/runs/{runId}/payloads/{payloadId}` — the explicit user action that fetches final head/tail tool output bytes. Nothing in the tracer fetches private payloads implicitly; the event stream only references payloads by opaque `payload_ref`. `--json` prints the versioned payload envelope.

## Current model tool surface

The Agent Worker v2 advertisement contains four core bindings: `read`
with `op=list|glob|search|read|help|web_search|web_fetch`, `write` with
`op=write|delete|restore`, `edit`, and `bash` with `mode=run|shell` in both
Standard and Full Access. Standard applies the stricter
analyzer/rule/approval policy; Full Access normally uses its broader fallback
for well-formed calls, but neither preset is an OS-enforced sandbox. Terminal
calls run as the current Windows user and can execute host code or open
sockets. Time, machine status, app discovery, and optional volume commands use
the terminal; notes use `write` with `op=write`. `files.apply_patch`, connectors, and `app.open` are not
model-usable. Managed startup enables `read` with `op=web_search` (`network.search`) and
`read` with `op=web_fetch` (`network.fetch`) with the fixed DuckDuckGo HTML endpoint
`https://html.duckduckgo.com/html/`; results are bounded and marked untrusted.
Declarative Skills appear only when explicitly granted and retain normal
Broker policy/approval checks. Managed startup keeps skill package roots and
skill bindings opt-in, so a caller still needs an explicit session grant before
`/skills` or `/skill` can run.
Subagents, LSP, extensions, plugins, and MCP are not part of this CLI surface.

All commands ensure the Public Host is running via `PublicHostStarter.GetOrStartAsync` before any API call and use `PublicHostSession` bearer auth.

## Rendering

- **Interactive** (`SafeEventRenderer`): displays only safe envelope fields (`protocol_version`, `event_id`, `run_id`, `session_id`, `sequence`, `event_type`, `occurred_at`, `status`, `safe_summary`, `payload_ref`). Never fetches private payloads; `payload_ref` is shown opaque.
- **JSON** (`NdjsonEventRenderer` with `--json`): versioned NDJSON `{"v":1,"type":"event",...safe fields}`. Never prompts, never auto-approves, never auto-fetches private payload, and never emits `prompt`/`command`/`argv`/`path`/`code`/`diff`/`output`/`secret`/`stdout`/`stderr`/`environment` fields. Interactive confirmations (Full Access, trust, approval, global policy) are rejected when `--json` is set.
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
