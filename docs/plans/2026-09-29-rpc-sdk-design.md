---
status: pending
---

# Plan: S1 — `ru agent rpc` (stdio JSON-RPC 2.0) + SDK TypeScript

Ngày: 2026-09-29. Baseline: `9677f7e1` (dev).
Mục tiêu: parity với Pi RPC/SDK — cho process ngoài (editor extension, script đa ngôn ngữ) điều khiển Ru qua stdin/stdout; SDK TypeScript bọc ngoài. Đây là sub-project S1 của lộ trình "full parity với Pi" (S2 Extensions, S3 Packages, S4 Session tree, S5 Distribution).

## 0. Tuân thủ AGENTS.md / CONTRIBUTING.md

Invariants chạm tới và cách giữ:

- **Thin client** — chỉ mở rộng `RuAgent.Cli` + thư mục SDK mới; không đụng Host/Broker/Worker. Không thêm HTTP route: giữ nguyên 42 routes, guard `tests/test_aspnetcore_route_matrix.py` phải xanh.
- **Loopback + local process only** — bridge chạy như user hiện tại, nói chuyện với host qua `runtime_descriptor.json` + bearer sẵn có (`PublicHostLocator` / `PublicHostSession`). Không mở transport mới ra ngoài máy.
- **Fail closed** — framing/parse/limit lỗi trả error tường minh, không đoán, không drop durable event im lặng, không tự fetch private payload.
- **No fallbacks** — lỗi host/HTTP map trung thành code gốc (`ruCode` trong error data), không retry ngầm ngoài hành vi `PublicHostStarter` hiện có.
- **Minimal changes** — dùng lại `PublicHostStarter`, `AgentApiClient`, `AgentSseClient`; chỉ thêm module Rpc trong CLI và thư mục `sdk/` mới.
- **Bounded everything** — cap frame 1 MiB, cap queue outbound, cap base64 frame.
- **Docs là một phần của change** — `docs/operations/rpc.md`, SDK README, CHANGELOG (Keep a Changelog). README/CONTEXT counts không đổi (không thêm route/tool) nên guards giữ nguyên.
- **Verify** — `dotnet build backend-dotnet/RuBackend/RuBackend.csproj -c Debug`, `.\scripts\test_dotnet.ps1 -Configuration Debug` (một matrix một lúc, lock exit 75), `.venv\Scripts\python.exe -m pytest tests -q`, và mới: `npm test --prefix sdk/ru-agent`.
- Mỗi bug fix kèm regression test đỏ trên code cũ (CONTRIBUTING yêu cầu).

## 1. Hiện trạng đã verify (baseline `9677f7e1`)

- CLI là thin client HTTP/SSE qua `RuAgent.Client`; `PublicHostStarter.GetOrStartAsync` tự ensure host chạy (spawn hidden, health probe `/settings` với bearer).
- Descriptor `%LOCALAPPDATA%\Ru\runtime_descriptor.json` bind PID/start-time (`PublicHostLocator`), `PublicHostSession.FromDescriptor` dựng BaseUri + bearer.
- 42 HTTP routes, 22 routes `/agent/v1/*` gồm run lifecycle, approvals, trust, policies, workspaces. SSE durable events có `sequence`, replay theo `Last-Event-ID`/`after_sequence`; transient frames không replay (`HarnessLiveEventHub` không giữ buffer).
- NDJSON `{"v":1,...}` hiện có (`NdjsonEventRenderer`) là renderer trace an toàn cho `--json` — **không phải** RPC: một chiều, cố tình không chứa raw text/payload.
- Chưa có transport nào cho process ngoài điều khiển liên tục. Pi có RPC (JSON qua stdin/stdout) + SDK; đây là gap S1.
- Test hạ tầng sẵn có: `RuAgent.Cli.Tests` (xUnit), scripted model `RU_SCRIPTED_MODEL` chạy pipeline thật không cần provider (`docs/operations/scripted-model.md`).
- Launcher `bin/ru.cmd`. Repo chưa có file LICENSE.

## 2. Protocol v1

### Framing

- NDJSON: mỗi dòng một JSON object hoàn chỉnh, UTF-8 no BOM, LF. Chấp nhận và strip `\r` (Windows text-mode).
- stdout của bridge chỉ chứa JSON-RPC; stderr chỉ log chẩn đoán. Mọi write khác ra stdout bị cấm sau khi bridge khởi động.
- Cap 1 MiB mỗi dòng, cả hai chiều. Quá cap → error `request_too_large`, bỏ qua dòng đó.

### Handshake

- Client gửi `initialize { protocolVersion: 1, clientInfo: { name, version } }`.
- Bridge trả `{ protocolVersion: 1, bridge: { version }, host: { version }, methods: [...], notifications: [...] }`.
- Gọi method khác trước `initialize` → error `not_initialized`. `protocolVersion` không hỗ trợ → `unsupported_protocol`.
- Chỉ một `initialize` cho một process; gọi lại → `already_initialized`.

### JSON-RPC 2.0

- Request/response có `id` (string hoặc number); notifications không có `id`.
- Cancel kiểu LSP: notification `$/cancelRequest { id }` — bridge hủy best-effort HTTP call in-flight; nếu đã xong thì no-op.
- Tất cả method đều async; không có server-initiated request trong v1.

### Methods v1 (subset có kiểm soát — không phơi hết 42 routes)

| Method | Params (chính) | HTTP nội bộ |
|---|---|---|
| `initialize` | protocolVersion, clientInfo | nội bộ |
| `host.status` | — | `GET /settings` (health + version) |
| `session.create` | workspace, preset? | `POST /sessions/new` |
| `session.list` | — | `GET /sessions` |
| `session.fork` | sessionId, index?, title? | `POST /sessions/{sessionId}/fork` |
| `session.rewind` | sessionId, index | `POST /sessions/{sessionId}/rewind` |
| `run.start` | workspace, preset, prompt, sessionId?, trustWorkspace?, confirmFullAccess? | `POST /agent/v1/runs` |
| `run.attach` | runId, afterSequence?, includeTransient? | `GET /agent/v1/runs/{runId}/events` (SSE) |
| `run.detach` | subscriptionId | nội bộ |
| `run.trust` | runId, approve | `POST /agent/v1/runs/{runId}/trust` |
| `run.approve` | runId, approvalId, decision (`allow_once`/`allow_session_directory`/`allow_always`/`deny`) | `POST /agent/v1/runs/{runId}/approvals/{approvalId}` |
| `run.cancel` / `run.continue` | runId | route tương ứng |
| `run.status` | runId | `GET /agent/v1/runs/{runId}` |
| `run.command` | runId, command | `POST /agent/v1/runs/{runId}/commands` |
| `run.reconcile` | runId, outcome?, hash? | `POST /agent/v1/runs/{runId}/reconcile` |
| `run.output` | runId, payloadId | `GET /agent/v1/runs/{runId}/payloads/{payloadId}` → base64 |
| `policy.list` | scope? | `GET /agent/v1/policies` |
| `policy.import` | file, confirm | `POST /agent/v1/policies` (từng rule) |
| `policy.remove` | ruleId, confirm | `DELETE /agent/v1/policies/{ruleId}` |

Ngoài v1 (ghi rõ để không trôi scope): settings/API keys (secret — giữ GUI/TUI), remote/máy khác, binary streaming ngoài base64, SDK Python, subagent (thuộc S2+).

### Notifications

- `run.event` — envelope durable: `{ subscriptionId, sequence, eventId, eventType, occurredAt, status, safeSummary, payloadRef }`. Replay theo `afterSequence`; không bao giờ tự fetch payload.
- `run.transient` — chỉ khi attach `includeTransient: true`: các frame `assistant.delta` / `tool.output.delta` / `transient.frames_dropped`. Không replay; opt-in tường minh (mặc định tắt, khớp nguyên tắc `--json` không mang raw text).
- `bridge.shutdown { reason }` — best-effort trước khi exit.

### Errors

- Chuẩn: `-32700` parse, `-32600` invalid request, `-32601` method not found, `-32602` invalid params, `-32603` internal.
- Ứng dụng: `-32000` + `data.ruCode` (catalog hiện có như `approval_stale`, `workspace_trust_required`, `public_host_start_timeout`, `full_access_confirmation_required`...) + `data.httpStatus?`. Message một dòng, ≤ 200 chars, như CLI.
- Frame cap: `request_too_large`; output base64 vượt frame: `output_frame_exceeds_limit` (kèm hướng dẫn dùng `agent output`).

### Lifecycle / backpressure

- stdin EOF → bridge thoát sạch (exit 0). Run trên host **không** bị kill — durable; client attach lại bằng `afterSequence`.
- Lỗi host không hồi phục khi đang chạy → gửi `bridge.shutdown { reason }` rồi exit non-zero.
- Outbound queue bounded 4,096 messages / 64 MiB; tràn → exit non-zero (fail closed, không drop im lặng).
- `ru agent rpc [--workspace PATH]` — flag tối thiểu, giống CLI hiện tại.

## 3. SDK TypeScript

- Vị trí `sdk/ru-agent/` trong repo; npm name tạm `@ru/agent-sdk`; repo chưa có LICENSE nên giữ private, publish sau.
- Build TypeScript + tsup; test vitest; Node >= 22.
- Cấu trúc: `src/transport.ts` (spawn bridge, NDJSON framing, id↔promise map, cancel), `src/client.ts` (`RuAgent.start()`/`close()`), `src/run.ts` (Run handle: `events()` async iterator, `approve()`, `cancel()`, `output()`...), `src/types.ts` (event + error types v1 viết tay), `src/errors.ts` (`RuRpcError` giữ `ruCode`).
- `run.events({ includeTransient?, afterSequence? })` gọi `run.attach` đúng một lần rồi trả async iterator; gọi lần hai khi đang subscribe → error rõ. Iterator kết thúc khi run đạt trạng thái terminal hoặc `run.detach()`.
- API mẫu:

```ts
const ru = await RuAgent.start({ workspace: "C:\\proj" });
const run = await ru.runs.start({ prompt: "fix the failing test", preset: "standard" });
for await (const ev of run.events()) { ... }
await run.approve(approvalId, "allow_once");
await ru.close();
```

- Resolve bridge theo thứ tự: `options.cliPath` → env `RU_CLI_PATH` → `bin/ru.cmd` → `ru` trên PATH. Trên Windows, `.cmd` spawn qua `cmd /c`; `.exe` spawn trực tiếp.
- SDK đối chiếu `methods[]` từ handshake với bảng method nó hỗ trợ để phát hiện lệch protocol sớm.

## 4. Test

- C# unit (`RuAgent.Cli.Tests/Rpc/`): framing (dòng cắt đôi, dòng quá lớn, JSON hỏng, thứ tự), handshake + version lạ + double init, dispatch từng method (fake HTTP handler), notification mapping SSE → JSON-RPC (replay theo sequence), `$/cancelRequest`, EOF shutdown, error mapping `ruCode`, queue overflow fail-closed.
- C# integration: scripted model e2e (`RU_SCRIPTED_MODEL`) — `run.start` → `run.event*` → tool call → `run.approve` → completed; hai nhánh `includeTransient` on/off; `run.output` base64.
- SDK vitest: transport (partial lines, cancel, bridge crash → pending reject), integration chạy bridge thật với scripted model.
- Contract: test C# đếm methods v1 khớp bảng; route matrix python không đổi.
- CI: thêm job Node cho `sdk/ru-agent` (npm ci, build, test) bên cạnh job Python/.NET hiện có.

## 5. Verify + docs

- `dotnet build backend-dotnet/RuBackend/RuBackend.csproj -c Debug`
- `.\scripts\test_dotnet.ps1 -Configuration Debug`
- `.venv\Scripts\python.exe -m pytest tests -q`
- `npm test --prefix sdk/ru-agent`
- Docs: `docs/operations/rpc.md` (protocol spec v1; tránh các cụm bị guard ban trong `docs/operations`), SDK README + 2 ví dụ (one-shot, streaming chat), CHANGELOG entry, cập nhật index `docs/README.md` (thêm link plan này cạnh plan TUI đang mở).

## 6. Thứ tự làm

1. Contracts + framing + handshake + tests.
2. Dispatcher: `host.status`, `session.*`.
3. `run.start` / `run.attach` / `run.event` / `run.detach`.
4. `run.transient` (opt-in) + `cancel`/`continue`/`status`/`command`/`reconcile`/`output`.
5. `run.trust` / `run.approve` + `policy.*`.
6. Error mapping + lifecycle/backpressure + shutdown.
7. SDK TS: transport → client → run handle → types/errors → tests.
8. CI job Node + docs + CHANGELOG.
9. E2E scripted model + ví dụ mẫu chạy được.

## 7. Rủi ro

- stdio Windows text-mode (CRLF/BOM): dùng raw stream UTF-8 no BOM, strip `\r`; test trên Windows Terminal thật.
- Output lẫn stdout từ dependency (Console.WriteLine sót lại): cô lập writer sau khi bridge khởi động + test bắt stdout sạch.
- base64 payload lớn: cap + `output_frame_exceeds_limit` rõ ràng.
- Client chậm làm tràn pipe: bounded queue + fail closed; không drop durable event.
- Protocol drift tương lai: `protocolVersion` + `methods[]` trong handshake; thay đổi breaking → bump version, SDK đối chiếu.
