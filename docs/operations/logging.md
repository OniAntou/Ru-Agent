# Ru Logging & Diagnostics Architecture

## 1. Overview & Canonical Roots

The Ru logging system is split into two cleanly separated subsystems:
1. **Activity Timeline**: User-facing activity events (chat previews, tool traffic summaries) with strict 160-character previews, control marker stripping, and secret masking (`[REDACTED_KEY]`, `[REDACTED_TOKEN]`).
2. **Technical Diagnostics**: Bounded structured events (`DiagnosticEvent`) for operational observability, timing, error codes, and correlation IDs without raw text, messages, or stack traces.

### Log Root Resolution
- Environment variable `RU_LOG_ROOT` (highest precedence).
- Canonical default: `%LOCALAPPDATA%\Ru\logs` on Windows (`~/.local/share/Ru/logs` on Linux/macOS).
- Bootstrap fallback: `<logRoot>\bootstrap\` contains bounded structured
  JSONL summaries for each owned backend child stream. Each record carries
  only the fixed role and stream, timestamp, saturated line/byte counts, a
  trailing content tail (up to 2,000 characters, so a dying child can explain
  why), and a truncation flag; the head of the stream is not persisted.

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
- **Managed Worker**: Carries the correlation ID through typed Worker envelopes;
  the deterministic test fixture follows the same bounded shape.
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

The ASP.NET HTTP boundary emits fixed route events such as `http.agent`,
`http.business`, `http.diagnostics`, and `http.settings` with status and
duration only. The managed Worker and WinUI
processes use separate bounded writers under the same resolved `RU_LOG_ROOT`;
the managed test suites use temporary writer roots and never launch a
production Python Worker.
The `/diagnostics/*` routes are intentionally excluded from HTTP boundary
events so the diagnostics UI polling does not create a self-referential log
stream or cause unnecessary UI refreshes.
GUI crash handling writes `gui_crash` only for unhandled fatal failures, with a
safe exception category; recoverable WinUI exceptions are handled without a
false critical crash record, and no raw crash file is created.
Activity Timeline data remains a separate privacy-safe outbox owned and
ingested by ASP.NET Core.

Automated tests cover schema/privacy, bounds, retries, and writer counters.
Live provider behaviour
remains an explicit user-run acceptance check rather than automated
logging evidence.

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

Before dispatch, the managed Worker emits exactly one of these bounded event
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

### Selection diagnostics

The C# Agent Worker emits `selection_invalid` once per Agent turn when a
braced candidate reply fails structured selection parsing or validation; it
carries no reply text or arguments. There is no mode-clamping or Chat request
compatibility event. The remaining diagnostic is a bounded, fixed-field event:
no prompt, transcript, tool arguments, output, or exception data.

## OpenTelemetry traces (parallel to JSONL)

The host and broker processes expose OpenTelemetry spans alongside JSONL:
- Sources `RuAgent.Harness` (`agent.model_step`, outcome tag `step.outcome`)
  and `RuAgent.Broker` (`tool.execute`, tags `tool.id`, `tool.status`,
  `error.code`, `verification.state`).
- Tag allowlist mirrors the JSONL privacy bounds: bounded codes and tool
  identity only. Ids, prompts, arguments, outputs, paths, endpoints and
  payloads are never tagged. Unknown values are dropped fail-closed.
- Without a listener every helper is a no-op. OTLP export is added only when
  `OTEL_EXPORTER_OTLP_ENDPOINT` is set; otherwise traces stay in-process.
