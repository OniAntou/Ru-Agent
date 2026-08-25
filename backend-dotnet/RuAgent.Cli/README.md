# RuAgent CLI — Policy-Aware Tracer (Phase 2)

Thin headless client for Agent v2. Delegates runtime to the Public Host over authenticated HTTP/SSE; contains no Worker, Broker, model-loop, or adapter code.

## Commands

```text
agent run [--preset standard|full_access] --workspace PATH "prompt" [--session <id>] [--json]
agent attach <run-id> [--json]
agent trust <run-id> --approve|--deny
agent approve <run-id> <approval-id> --scope once|run|project
agent deny <run-id> <approval-id>
agent cancel <run-id>
agent continue <run-id>
agent status <run-id> [--json]
agent policy list [--global | --workspace PATH] [--json]
agent policy import --global FILE
agent policy remove <rule-id>
```

- `run` creates a new Agent run. If `--session` is omitted, the tracer creates one dedicated session via `POST /sessions/new`, prints `session_id` and `run_id`, and sends a fresh `idempotency_key` (32-char hex GUID). Workspace is canonicalized and bounded to 512 chars; prompt is bounded to 8192 chars; max argument count is 64.
- `--preset full_access` (alias `full`) shows an interactive warning and requires confirmation; it changes the session's stored preset via `POST /agent/v1/policies` with `kind=session_preset` and requires header `X-Interactive-Confirmed: true`. `--json` cannot be used with `full_access`; the preset change affects the next run only and is not sticky across new sessions.
- `attach` streams an existing run's durable events from `after_sequence=0` via `AgentSseClient`.
- `trust` wraps `POST /agent/v1/runs/{runId}/trust`.
- `approve` and `deny` wrap `POST /agent/v1/runs/{runId}/approvals/{approvalId}` with `--scope once|run|project`. Approval is first-writer wins; second concurrent response returns `approval_stale` (409). Exact scope preview strips globs and is displayed before confirmation. `global` scope via approval is rejected (`approval_cannot_create_global`). `--json` cannot confirm approval.
- `policy list` wraps `GET /agent/v1/policies` (scope filter). `policy import --global FILE` validates versioned JSON (rejects unknown version/fields, normalizes and displays matchers/decision before confirmation) and posts each rule with `X-Interactive-Confirmed: true`; sensitive matcher values are encrypted at rest via Host API. `--json` cannot bypass confirmation. `policy remove` wraps `DELETE /agent/v1/policies/{ruleId}` and requires interactive confirmation for global rules.
- `cancel`/`continue`/`status` are thin HTTP wrappers.

All commands ensure the Public Host is running via `PublicHostStarter.GetOrStartAsync` before any API call and use `PublicHostSession` bearer auth.

## Rendering

- **Interactive** (`SafeEventRenderer`): displays only safe envelope fields (`protocol_version`, `event_id`, `run_id`, `session_id`, `sequence`, `event_type`, `occurred_at`, `status`, `safe_summary`, `payload_ref`). Never fetches private payloads; `payload_ref` is shown opaque.
- **JSON** (`NdjsonEventRenderer` with `--json`): versioned NDJSON `{"v":1,"type":"event",...safe fields}`. Never prompts, never auto-approves, never auto-fetches private payload, and never emits `prompt`/`command`/`argv`/`path`/`code`/`diff`/`output`/`secret`/`stdout`/`stderr`/`environment` fields. Interactive confirmations (Full Access, trust, approval, global policy) are rejected when `--json` is set.

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
        "tool_id": "files.read",
        "canonical_path_pattern": "src/app.ts"
      }
    }
  ]
}
```
Unknown version or fields are rejected. Matcher values are encrypted at rest; only decrypted snapshot contains them.
