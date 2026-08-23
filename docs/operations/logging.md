# Ru Logging & Diagnostics Architecture

## 1. Overview & Canonical Roots

The Ru logging system is split into two cleanly separated subsystems:
1. **Activity Timeline**: User-facing activity events (chat previews, emotional states, VTS actions, observation reactions) with strict 160-character previews, control marker stripping, and secret masking (`[REDACTED_KEY]`, `[REDACTED_TOKEN]`).
2. **Technical Diagnostics**: Bounded structured events (`DiagnosticEvent`) for operational observability, timing, error codes, and correlation IDs without raw text, messages, or stack traces.

### Log Root Resolution
- Environment variable `RU_LOG_ROOT` (highest precedence).
- Canonical default: `%LOCALAPPDATA%\Ru\logs` on Windows (`~/.local/share/Ru/logs` on Linux/macOS).
- Bootstrap fallback: `<logRoot>\bootstrap\` contains bounded structured
  JSONL summaries for each owned backend child stream. Each record carries
  only the fixed role and stream, timestamp, saturated line/byte counts, and
  a truncation flag; raw stdout/stderr lines are never persisted or exposed.

## 2. Retention & Bounded Rotation

- **File rotation**: Daily JSONL files named `<prefix>-YYYY-MM-DD.jsonl` (e.g. `aspnetcore-2026-08-20.jsonl`, `worker-2026-08-20.jsonl`, `gui-2026-08-20.jsonl`).
- **Retention Limits**: Cleanup applies the seven-day age limit and 100 MB
  aggregate cap to recognized top-level technical JSONL files and recursively
  to `bootstrap\backend_*.jsonl` and `bootstrap\backend_*.log`.
- **Legacy cleanup**: Existing matching bootstrap `.log` files are eligible
  for the same retention cleanup, but are never parsed or returned by the
  diagnostics API. New bootstrap output is structured JSONL only.
- **Activity Log Retention**: Maximum 5,000 entries with duplicate `event_id` suppression.

## 3. Correlation ID Propagation

Every user turn or background action generates a correlation ID matching `^c_[a-zA-Z0-9_-]{8,64}$`:
- **WinUI GUI**: Generates `c_{Guid:N}` and sends it via `X-Correlation-Id` header or WebSocket payload.
- **ASP.NET Core**: `CorrelationMiddleware` extracts `X-Correlation-Id` and scopes async flow via `CorrelationContext.Current`.
- **Python Worker**: Received in `WorkerEnvelope`, entered via `correlation_scope(correlation_id)`.
- **Diagnostics & Outbox**: All emitted records inherit the ambient correlation ID.

## 4. Diagnostics API Routes

ASP.NET Core exposes three bounded authenticated diagnostic routes:
- `GET /diagnostics/logs`: Cursor-based paginated logs with filtering by `level`, `component`, `event_code`, `correlation_id`, `since`, and `limit` (default 100, max 5,000).
- `GET /diagnostics/export`: Returns an `application/x-ndjson` download up to 5,000 matching records and a 2 MB response cap.
- `GET /diagnostics/status`: Returns queue depth, enqueued/dropped count, write failure count, active file count, and disk size.

Query filters are bounded to 128 characters, cursors to 256 characters, and
`since` to 64 characters. Invalid values return a stable `400` response with
an `error_code`; oversized exports return `413` with
`diagnostic_export_too_large`. The reader skips records above 256 KB and scans
at most 4 MB per diagnostic file for one request.

The ASP.NET HTTP boundary emits fixed route events such as `http.stt`,
`http.voice`, `http.vts`, `http.observation`, `http.memory`, and
`http.business` with status and duration only. The Python worker and WinUI
processes use separate bounded writers under the same resolved `RU_LOG_ROOT`.
The `/diagnostics/*` routes are intentionally excluded from HTTP boundary
events so the diagnostics UI polling does not create a self-referential log
stream or cause unnecessary UI refreshes.
GUI crash handling writes only `gui_crash` with a safe exception category; it
does not create a raw crash file. The WinUI microphone boundary may also write
`gui_voice_capture` failure events with a bounded stage/status code and safe
exception category, never exception text, raw audio, or transcript data.
Activity Timeline data remains a separate privacy-safe outbox owned and
ingested by ASP.NET Core.

Automated tests cover schema/privacy, bounds, retries, and writer counters.
Desktop, microphone, speaker, and live VTube Studio behavior remain explicit
user-run acceptance checks rather than automated logging evidence.

## Action receipt diagnostics

The bounded action trail uses `tool.requested`, `tool.policy.denied`,
`tool.approval.required`, `tool.adapter.started`, `tool.adapter.completed`,
`tool.verification.succeeded`, `tool.verification.failed`,
`tool.receipt.delivered`, and `tool.receipt.rejected`.

Allowed action fields are correlation ID, tool ID/version, terminal status,
stable safe error code, duration, verification state, the fixed endpoint role,
and an ephemeral per-Broker endpoint hash. Arguments, output values, prompts,
replies, paths, scripts, stdout/stderr, credentials, device names, and native
exception text are forbidden.

### Intent-resolution diagnostics

Before dispatch, the Runtime Worker emits exactly one of these bounded event
codes when explicit volume wording reaches a deterministic terminal decision:

- `tool.intent.resolved`: one valid typed volume intent was created;
- `tool.intent.clarification`: the wording needs one deterministic
  clarification before any request is sent;
- `tool.intent.rejected`: explicit action wording was invalid or unsupported.

These events may carry correlation ID, tool ID/version, status, and a stable
reason code. They contain no prompt, arguments, level, amount, output, muted
state, reply, device name, endpoint hash, or exception data. Resolution occurs
before model selection, and logging failure cannot change the user-visible
action result.

### Selection and mode diagnostics

The Runtime Worker emits `selection_invalid` once per chat turn when a
braced candidate reply fails structured selection parsing or validation; it
carries no reply text or arguments. The Public Host emits `mode_clamped` once
when an unknown chat-request mode string is clamped to Chat; it carries only
level, component, code, and correlation ID. Both are bounded, fixed-field
events: no prompt, transcript, tool arguments, output, or exception data.
