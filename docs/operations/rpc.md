# The stdio RPC bridge (`ru agent rpc`)

`ru agent rpc` exposes a Ru run to an external process — an editor extension, a script,
a program in any language — as a JSON-RPC 2.0 conversation over stdin/stdout. It is a
thin client on top of the existing CLI: it resolves the same loopback host through
`runtime_descriptor.json` and the same bearer token, reuses the same HTTP/SSE clients,
adds no HTTP route, and opens no transport beyond stdio. It runs as the current Windows
user inside the local same-user trust boundary: anyone who can spawn the CLI can drive
the host, exactly as `ru agent run` can.

Run it with no further arguments — `ru agent rpc`. The bridge owns stdin/stdout for its
whole life, so it cannot be re-invoked between the phases of a run. The TypeScript SDK
at [`sdk/ru-agent`](../../sdk/ru-agent/README.md) wraps the spawn and exposes typed
session and run handles.

## Framing

- **NDJSON**: one complete JSON object per line, UTF-8, LF-terminated. A trailing
  `\r` from Windows text mode is stripped.
- **stdout carries JSON-RPC only**; diagnostics are written to stderr. Nothing else
  may write to stdout while the bridge runs.
- Inbound request lines are capped at **1 MiB**. An oversized line is answered with a
  `request_too_large` error (`-32600`) and skipped; the bridge does not guess at content
  it cannot read. Outbound frames are not measured against that cap — they are bounded
  only by the content each result carries (for example the payload `run.output`
  returns), so a large but legal result may exceed 1 MiB on the wire.
- A line that is not valid JSON is answered with `parse_error` (`-32700`) and skipped.
- Requests without an `id` are notifications and get no response.
- `$/cancelRequest` is accepted and ignored: v1 requests are short-lived, so there is no
  in-flight request to cancel.

Exit codes:

| Exit | Meaning |
| ---: | --- |
| `0` | stdin reached EOF; queued messages, including `bridge.shutdown`, were written. |
| `3` | The bounded outbound queue filled up. The bridge fails closed instead of dropping a durable event. |
| `130` | The bridge was cancelled. |

EOF on stdin shuts the bridge down cleanly; it **does not kill the run on the host**. A
run is durable, so a client that reconnects can attach again with `afterSequence` and
replay the durable events it missed.

## Handshake

The first request must be `initialize`:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientInfo":{"name":"my-editor","version":"1.0"}}}
```

```json
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,"bridge":{"version":"1.0.0.0"},"methods":["initialize","host.status","session.create","session.list","session.fork","session.rewind","run.start","run.attach","run.detach","run.trust","run.approve","run.cancel","run.continue","run.status","run.command","run.reconcile","run.output","policy.list","policy.import","policy.remove"],"notifications":["run.event","run.transient","bridge.shutdown"]}}
```

- `protocolVersion` must be `1`. Any other value is rejected with `-32000` and
  `data.ruCode: "unsupported_protocol"`.
- `clientInfo` is accepted and ignored by v1.
- Any method other than `initialize` before the handshake is rejected with
  `not_initialized` (`-32001`), including its own retry path.
- Calling `initialize` a second time in the same process is rejected with
  `already_initialized` (`-32002`).
- `methods` and `notifications` are the exact v1 surface. Clients use them to detect
  protocol drift early instead of discovering it on the first failing call.

## Methods v1

`runId` is always `run_` followed by 32 lowercase hex characters. String parameters are
bounded; the bridge rejects anything over the limit as `invalid_params` rather than
truncating it.

| Method | Params (main) | Result | Host call |
| --- | --- | --- | --- |
| `initialize` | `protocolVersion` | handshake payload above | internal |
| `host.status` | — | `{ "ready": true, "base_url": "http://127.0.0.1:..." }` | `GET /settings` |
| `session.create` | — | `{ "session_id": "..." }` | `POST /sessions/new` |
| `session.list` | — | session list (passthrough) | `GET /sessions` |
| `session.fork` | `sessionId`, `index`, `title`? | fork payload (passthrough) | `POST /sessions/{sessionId}/fork` |
| `session.rewind` | `sessionId`, `index` | rewind payload (passthrough) | `POST /sessions/{sessionId}/rewind` |
| `run.start` | `workspace`, `prompt`, `preset`?, `sessionId`?, `trustWorkspace`?, `confirmFullAccess`? | `{ "run_id", "session_id", "status", "workspace_id" }` | `POST /agent/v1/runs` |
| `run.attach` | `runId`, `afterSequence`?, `includeTransient`? | `{ "subscription_id", "run_id", "include_transient" }` | `GET /agent/v1/runs/{runId}/events` (SSE) |
| `run.detach` | `subscriptionId` | `{ "detached": "<subscriptionId>" }` | internal |
| `run.trust` | `runId`, `approve`? | `{ "run_id", "status" }` | `POST /agent/v1/runs/{runId}/trust` |
| `run.approve` | `runId`, `approvalId`, `decision` | approval payload (passthrough) | `POST /agent/v1/runs/{runId}/approvals/{approvalId}` |
| `run.cancel` | `runId` | `{ "cancelled": "<runId>" }` | `POST /agent/v1/runs/{runId}/cancel` |
| `run.continue` | `runId` | `{ "continued": "<runId>" }` | `POST /agent/v1/runs/{runId}/continue` |
| `run.status` | `runId` | run record (passthrough) | `GET /agent/v1/runs/{runId}` |
| `run.command` | `runId`, `command` | command payload (passthrough) | `POST /agent/v1/runs/{runId}/commands` |
| `run.reconcile` | `runId`, `outcome`, `hash` | reconcile payload (passthrough) | `POST /agent/v1/runs/{runId}/reconcile` |
| `run.output` | `runId`, `payloadId` | `{ "encoding": "base64", "data", "byte_length" }` | `GET /agent/v1/runs/{runId}/payloads/{payloadId}` |
| `policy.list` | `scope`? | array of policy rules | `GET /agent/v1/policies` |
| `policy.import` | `file`, `confirm` | `{ "imported": [ ... ] }` | `POST /agent/v1/policies` (one request per rule) |
| `policy.remove` | `ruleId`, `confirm` | `{ "removed": "<ruleId>" }` | `DELETE /agent/v1/policies/{ruleId}` |

Details that are easy to get wrong:

- `run.start` — `preset` defaults to `standard` and accepts `plan`, `standard` or
  `full_access`. `full_access` requires `confirmFullAccess: true`; without it the call
  fails with `full_access_confirmation_required`. `trustWorkspace` defaults to `false`.
  The returned `status` is the host's state at creation time —
  `waiting_workspace_trust` on a workspace that is not trusted yet, `waiting_history`
  when it already is; granting trust only moves the run forward afterwards, so the
  example below still reports the creation state.
- `run.attach` — `afterSequence` defaults to `0` and must be a non-negative integer;
  `includeTransient` defaults to `false`. Each call returns a fresh
  `subscription_id`; `run.detach` takes that id, not the run id.
- `run.trust` — `approve` defaults to `false`, which sends `deny`.
- `run.approve` — `decision` is one of `allow_once`, `allow_session_directory`,
  `allow_always`, `deny`.
- `run.command` — `command` text is bounded to 8192 characters.
- `run.reconcile` — both `outcome` and `hash` are required; `hash` must be exactly 64
  characters. Accepted outcomes are `applied`, `not_applied` (or `not-applied`) and
  `unknown_continue` (or `unknown`).
- `policy.import` and `policy.remove` require `confirm: true`; without it the call is
  rejected as `invalid_params`.

`run.start`:

```json
{"jsonrpc":"2.0","id":3,"method":"run.start","params":{"workspace":"C:\\work\\repo","prompt":"fix the failing test","preset":"standard","sessionId":"7b1e4f5a9c2d4e6f8a0b1c2d3e4f5061","trustWorkspace":true}}
```

```json
{"jsonrpc":"2.0","id":3,"result":{"run_id":"run_0123456789abcdef0123456789abcdef","session_id":"7b1e4f5a9c2d4e6f8a0b1c2d3e4f5061","status":"waiting_workspace_trust","workspace_id":"a1b2c3d4e5f60718293a4b5c6d7e8f90"}}
```

`run.attach`, then the first durable event arrives as an unsolicited notification:

```json
{"jsonrpc":"2.0","id":4,"method":"run.attach","params":{"runId":"run_0123456789abcdef0123456789abcdef","afterSequence":0}}
```

```json
{"jsonrpc":"2.0","id":4,"result":{"subscription_id":"sub_1","run_id":"run_0123456789abcdef0123456789abcdef","include_transient":false}}
```

```json
{"jsonrpc":"2.0","method":"run.event","params":{"subscription_id":"sub_1","protocol_version":1,"event_id":"4d5e6f708192a3b4c5d6e7f8091a2b3c","run_id":"run_0123456789abcdef0123456789abcdef","session_id":"7b1e4f5a9c2d4e6f8a0b1c2d3e4f5061","sequence":1,"event_type":"run.state_changed","occurred_at":"2026-09-30T00:00:00Z","status":"running","safe_summary":"Run is running.","payload_ref":null,"input_tokens":null,"output_tokens":null,"total_tokens":null}}
```

## Notifications

- **`run.event`** — one durable event per run, replayed from `afterSequence`. Params:
  `subscription_id`, `protocol_version`, `event_id`, `run_id`, `session_id`, `sequence`,
  `event_type`, `occurred_at`, `status`, `safe_summary`, `payload_ref`, `input_tokens`,
  `output_tokens`, `total_tokens`. The envelope is safe by construction: it carries a
  summary and a payload reference, never raw model or tool text. The bridge never
  follows `payload_ref` on its own — a client that wants the bytes calls `run.output`.
- **`run.transient`** — live frames delivered **only** when the subscription was
  created with `includeTransient: true`. The SSE wire tags every one of them
  `event: transient`, so `event_type` is always `"transient"`; the actual kind
  (`assistant.delta`, `tool.output.delta`, `agent.activity`, `agent.summary`) rides
  inside `data` as a `{"kind":"...","data":"..."}` JSON string. Params:
  `subscription_id`, `transient_sequence`, `event_type`, `data`. Transient frames are
  never replayed and never buffered, so a reconnect starts without them.
  `transient.frames_dropped` is **not** one of these — it is a durable event and
  arrives as `run.event`.
- **`bridge.shutdown`** — written best-effort immediately before the process exits.
  Params: `reason` (`stdin_closed`, `backpressure`, or `cancelled`), matching the exit
  code.

## Errors

Standard JSON-RPC codes:

| Code | Meaning |
| ---: | --- |
| `-32700` | Parse error. |
| `-32600` | Invalid request, including `request_too_large`. |
| `-32601` | Unknown method. |
| `-32602` | Invalid or missing params. |
| `-32603` | Internal bridge failure. |

Bridge-level codes: `-32001` `not_initialized`, `-32002` `already_initialized`.

Application errors use `-32000` with `data.ruCode` carrying the host's error catalog
code and, when the failure came from an HTTP response, `data.httpStatus`. The
`message` mirrors the host's bounded error body rather than a reworded summary:

```json
{"jsonrpc":"2.0","id":9,"error":{"code":-32000,"message":"{\"error_code\":\"approval_stale\",\"message\":\"Approval is missing, expired, or already used.\"}","data":{"ruCode":"approval_stale","httpStatus":409}}}
```

Codes such as `approval_stale`, `workspace_trust_required`, `trust_failed`,
`reconcile_failed`, `full_access_confirmation_required`,
`invalid_payload_encoding` and `unsupported_payload_encoding` all arrive this way. The
bridge does not retry, rewrite or fall back on host failures; the mapped code is the
original one.

`-32602` can also carry `data.ruCode` when the rejection has a named cause: `run.start`
rejects an unknown preset with `-32602` plus `data.ruCode: "invalid_preset"`, and
`policy.import` maps file failures the same way (`file_error` and the policy codec's
own codes).

## Security notes

- **The bearer token never crosses the bridge.** No method returns it; `host.status`
  exposes only the loopback `base_url`.
- **`run.output` is an explicit action.** Payload bytes are fetched only when the client
  asks for a specific `payloadId` it already saw in `payload_ref`, and are returned as
  base64. Nothing is fetched in the background.
- **Transient text is opt-in.** Model deltas and tool output stay off the wire unless the
  client attaches with `includeTransient: true` — the same deliberate default as the
  `--json` trace renderer, which never carries raw text.
- **Fail closed.** Oversized frames are rejected, the outbound queue is bounded, and a
  queue overflow exits instead of dropping a durable event silently.

## TypeScript SDK

```ts
import { RuAgent } from "@ru/agent-sdk";

const ru = await RuAgent.start();
const run = await ru.runs.start({ workspace: "C:\\work\\repo", prompt: "fix the failing test" });

for await (const event of run.events()) {
  console.log(event.sequence, event.event_type);
}

await ru.close();
```

The SDK spawns the bridge, manages request ids and cancellation, and surfaces
`data.ruCode` as a typed error field. See
[`sdk/ru-agent/README.md`](../../sdk/ru-agent/README.md) for the full API.
