# RuAgent CLI — Read-Only Tracer (Phase 1)

Thin headless tracer for Agent v2 read-only runs. Delegates all runtime behavior to the Public Host over authenticated HTTP/SSE; contains no Worker, Broker, model-loop, policy, or adapter code.

## Commands

```text
agent run [--preset standard] --workspace PATH "prompt" [--session <id>] [--json]
agent attach <run-id> [--json]
agent trust <run-id> --approve|--deny
agent cancel <run-id>
agent continue <run-id>
agent status <run-id> [--json]
```

- `run` creates a new Agent run. If `--session` is omitted, the tracer creates one dedicated session via `POST /sessions/new` using `RuAgent.Client.SessionApiClient`, prints `session_id` and `run_id`, uses `preset=standard`, and sends a fresh `idempotency_key` (32-char hex GUID). Workspace is canonicalized (`Path.GetFullPath`) and bounded to 512 chars; prompt is bounded to 8192 chars; max argument count is 64.
- `--preset full` is rejected with `phase_not_available` in this phase (only `standard` allowed).
- `attach` streams an existing run's durable events from `after_sequence=0` via `AgentSseClient`.
- `trust`/`cancel`/`continue`/`status` are thin HTTP wrappers over `/agent/v1/runs/{runId}/...`.

All commands ensure the Public Host is running via `PublicHostStarter.GetOrStartAsync` before any API call and use `PublicHostSession` bearer auth.

## Rendering

- **Interactive** (`SafeEventRenderer`): displays only safe envelope fields (`protocol_version`, `event_id`, `run_id`, `session_id`, `sequence`, `event_type`, `occurred_at`, `status`, `safe_summary`, `payload_ref`). Never fetches private payloads; `payload_ref` is shown opaque.
- **JSON** (`NdjsonEventRenderer` with `--json`): versioned NDJSON `{"v":1,"type":"event",...safe fields}`. Never prompts, never auto-approves, never auto-fetches private payload, and never emits `prompt`/`command`/`argv`/`path`/`code`/`diff`/`output`/`secret`/`stdout`/`stderr`/`environment` fields. Fetching decrypted payload requires an explicit later `GET /agent/v1/runs/{runId}/payloads/{payloadId}` call.

## Cancellation

`Ctrl+C` cancels the SSE stream and `POST`s `/agent/v1/runs/{runId}/cancel` before exit.

## Bounds

- Max arguments: 64
- Workspace path: 1..512
- Prompt: 1..8192
- Session id: 1..64 (alphanum, `_`, `-`)
- Run id: `run_` + 32 lower hex (36 total)

## References

- `RuAgent.Client` only (`RuAgent.Protocol` for contracts). No `RuBackend`, `Broker`, `Worker`, or `System.CommandLine`.
