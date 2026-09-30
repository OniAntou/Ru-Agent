---
status: pending
---

# Spec: Đơn giản hoá Ru Agent (giữ 100% tính năng)

Ngày: 2026-09-30. Baseline: `6b32294a` (dev, sau S1).
Mục tiêu: giảm complexity thật (dòng code, process, tầng trung gian) **không phá sản phẩm** — toàn bộ tính năng user-visible giữ nguyên, test suite là lưới an toàn xanh trước/sau từng bước.

Nguồn dữ liệu: 2 khảo sát rất thorough ngày 2026-09-30 (process architecture + coordination/policy/projects/tests).

## Nguyên tắc

- **Mỗi bước là refactor/xoá thuần** — không đổi hành vi wire/route/UI. Test hiện có xanh trước và sau; test nào chỉ phục vụ code bị xoá thì xoá kèm (ghi rõ trong task).
- **Không xoá bảo hiểm race thật** (first-writer-wins, retained approval rehydrate chính, membership lease còn consumer, journal/monitor còn prompt injection).
- Commit Conventional Commits có scope; mỗi task một commit; verify `scripts/test_dotnet.ps1` + pytest sau mỗi đợt.

## Giai đoạn D1 — Dead-code excision (an toàn, giá trị rõ)

| # | Hạng mục | Bằng chứng khảo sát |
|---|---|---|
| D1.1 | Xoá `RuAgent.Connectors` + `Connectors.Tests` | 0 project ref (chỉ tests); docs tự nhận "not production-composed" |
| D1.2 | Xoá `AgentBrokerClient` + `IAgentBrokerClient` (+ DI registration) | v1 wire, không call-site production (verify lại bằng rg trước khi xoá) |
| D1.3 | Xoá snapshot store (`CaptureSnapshotAsync` + record + bảng `workspace_file_snapshots` nếu migration cho phép) | comment trong code: "no production caller"; 0 test dùng |
| D1.4 | Xoá `WorkspaceMergeEngine` + tests | 192 dòng, chỉ tests |
| D1.5 | Xoá `WorkspaceCoordinatorService` + DI + tests | test-only wrapper của `EvaluateClaim` |
| D1.6 | Xoá path-claim acquisition (`TryClaimPathsAsync` + call release trong loop) | không caller production; `active_claims` luôn 0 (giữ `CountActiveClaimsAsync` cho shape route) |
| D1.7 | Xoá `RetainedApprovalRehydrator.TryRehydrateAsync` (0 caller) | giữ `RehydrateAsync` — consumer thật |
| D1.8 | Xoá GUI `ApiService.Workspace.cs` + `Models\WorkspaceModels.cs`; cập nhật `public-route-contract.json` winui_callers nếu cần; sửa doc drift `shared-workspace-coordination.md:58-62` | GUI không có UI gọi; doc nói sai hiện trạng |

Ước lượng: -2.5k dòng code, -2k dòng test, -1 project.

## Giai đoạn D2 — Split file lớn (thuần di chuyển)

Theo mẫu `RunStarter` (Task 3 của S1): tách partial theo nhóm method rõ ràng, không đổi hành vi, chạy full suite sau mỗi file.

1. `WorkspaceCoordinatorStore.cs` (sau D1 còn ~800) → partial Membership / Journal.
2. `AgentCommands.cs` (~2,321) → partial theo nhóm lệnh (run / chat / policy / session).
3. `BrokerToolGateway.cs` (1,577) → partial theo nhóm (rpc / tool / approval).
4. `AgentCliParser.cs` (1,360) → partial theo nhóm parser.

Không đụng test files khối trong đợt này (tách sau nếu cần).

## Giai đoạn D3 — Gộp Worker vào Host (lớn nhất, đợt riêng)

- In-process `IModelStepRunner` mới tái dùng `WorkerV2StepService` (1,053 dòng) + phần `BuildConversation` của `LoopbackModelStepRunner`; truyền provider bootstrap trực tiếp (bỏ 2 tầng DPAPI handoff file).
- Xoá: `WorkerV2Server`, `WorkerRequestHistory`, `WorkerV2PromptCodec`, `WorkerV2Options`, `Program`, `ManagedWorkerComposition`, `WorkerV2Contracts` (1,251), `WorkerV2Envelope` (256), `LoopbackModelStepRunner` (1,025), `WorkerClientOptions` (80), phần Worker của `WorkerBrokerLaunchPlan`/`WorkerBrokerProcessManager`, env/token/port 8005/`RU_AGENT_WORKER_EXE`, publish stage Worker; tests IPC tương ứng.
- Giữ: Broker tách (ADR-0009/0011), descriptor Host (GUI/CLI attach), `ScriptedModelStepRunner` seam không đổi.
- Trade-off chấp nhận: crash trong đường generation giết Host (SQLite durable — restart sạch); mất "worker receives no persistent credential" (Host vốn đã giữ credential).
- Docs/ADR: viết ADR mới superseding phần worker tách process của ADR-0001/0002; cập nhật `architecture.md`, `runtime-behavior.md`, `verification.md`.

Ước lượng: -4.2k dòng code, -4k dòng test, -1 process, -1 exe (107MB), latency bỏ socket.

## Không đụng (có lý do)

- `PolicySnapshotEvaluator` + canonical hash (broker/reconcile boundary).
- Retained approval rehydrate (`RehydrateAsync`) + resume/refresh + race reconcile.
- HostApprovalLedger/PendingApprovalLedger first-writer-wins + TTL.
- Membership lease + heartbeat (đường shared qua HTTP thô).
- External-change monitor + encrypted journal + prompt injection.
- 4 exe layout + GUI publish (`RuAppLayout`, build.bat), `bin/ru.cmd`, Core BCL-only.
- GUI source-contract tests (thay bằng runtime không được — WinUI cần UI thread).
- Broker process (gộp = di chuyển 15k dòng + phá invariants; không phải đơn giản hoá).

## Verify

Mỗi task: full CLI suite filter + `scripts/test_dotnet.ps1` cuối đợt; pytest guards; với D3 thêm smoke RPC + GUI build.
Đích cuối: tất cả xanh, tính năng giữ nguyên, và các con số giảm: ~-7k dòng code, ~-6k dòng test, 5→4 process, 27→25 project (Connectors -1, Worker giữ project nhưng không còn process/publish riêng).
