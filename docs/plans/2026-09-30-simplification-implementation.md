---
status: pending
---

# Simplification D1+D2 Implementation Plan (dead-code excision + file splits)

> **For agentic workers:** Thực thi tuần tự từng task, verify bằng full suite sau mỗi task, commit riêng mỗi task. Đọc `docs/plans/2026-09-30-simplification-design.md` trước.

**Baseline:** `6b32294a` (dev). **Nguyên tắc:** xoá/refactor thuần — không đổi hành vi wire/route/UI; mọi claim "dead" phải VERIFY bằng rg + đọc consumer trước khi xoá; nếu claim sai → dừng, báo BLOCKED (không tự diễn giải).

**Verify chung mỗi task** (từ `D:\LLM`):
1. `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -v minimal` (nếu task đụng CLI) HOẶC project test liên quan
2. `.venv\Scripts\python.exe -m pytest tests -q`
3. Cuối mỗi task: `.\scripts\test_dotnet.ps1 -Configuration Debug` (một matrix một lúc; exit 75 = contention → retry sau 60s)

---

### Task 1: Xoá Connectors + AgentBrokerClient (dead projects/types)

**Files:**
- Delete: `backend-dotnet/RuAgent.Connectors/` (5 file), `backend-dotnet/RuAgent.Connectors.Tests/` (3 file)
- Modify: `backend-dotnet/RuAgent.slnx` (gỡ 2 project), `backend-dotnet/RuBackend/Hosting/PublicHostServiceRegistration.cs` (gỡ DI nếu có), mọi csproj còn ref
- Delete: `backend-dotnet/RuBackend/Harness/AgentBrokerClient.cs`? — verify path thật (`rg -l "class AgentBrokerClient" backend-dotnet`), `IAgentBrokerClient`, và registration trong `PublicHostServiceRegistration.cs`

- [ ] **Step 1: Verify dead trước khi xoá**
  - `rg -n "RuAgent.Connectors" backend-dotnet gui-winui3 scripts tests --glob "*.csproj" --glob "*.py" --glob "*.xml" --glob "*.slnx"` → chỉ thấy chính nó + Tests + slnx.
  - `rg -n "AgentBrokerClient" backend-dotnet` → xác nhận: chỉ định nghĩa + DI registration + tests; KHÔNG có call-site production. Nếu có call-site thật → BLOCKED báo lại.
- [ ] **Step 2: Xoá** các thư mục/method/registration; gỡ project khỏi `RuAgent.slnx`; gỡ `ProjectReference`/`InternalsVisibleTo` liên quan; gỡ test files tương ứng.
- [ ] **Step 3: Build sạch** — `dotnet build backend-dotnet/RuBackend/RuBackend.csproj -c Debug --nologo` (0 warning/error).
- [ ] **Step 4: Full verify** theo mục chung (pytest + `test_dotnet.ps1`).
- [ ] **Step 5: Commit** — `refactor(repo): drop the unused connectors project and v1 broker client`

---

### Task 2: Xoá dead workspace machinery (snapshot store, merge engine, coordinator service, path claims, TryRehydrateAsync)

**Files:**
- Modify: `backend-dotnet/RuAgent.Harness.Persistence/WorkspaceCoordinatorStore.cs` (xoá `CaptureSnapshotAsync` + records snapshot + SQL bảng nếu migration cho phép — xem Step 1)
- Delete: `backend-dotnet/RuAgent.Harness.Persistence/WorkspaceMergeEngine.cs` + `backend-dotnet/RuAgent.Harness.Persistence.Tests/WorkspaceMergeEngineTests.cs`
- Delete: `WorkspaceCoordinatorService.cs` (verify vị trí) + tests + DI registration `PublicHostServiceRegistration.cs:114`
- Modify: `WorkspaceCoordinatorStore.cs` — xoá `TryClaimPathsAsync`/`ReleasePathClaimsAsync`? (CHỈ nếu Step 1 xác nhận 0 caller production; thao tác release trong `AgentRuntimeLoopService` finally phải gỡ theo) — giữ `CountActiveClaimsAsync` (shape route)
- Modify: `RetainedApprovalRehydrator.cs` — xoá `TryRehydrateAsync` (0 caller; verify lại)
- Modify: `HarnessSqliteSchema.cs` — xoá bảng `workspace_file_snapshots` + code liên quan **chỉ khi** cơ chế schema version/migration cho phép xoá an toàn với DB cũ; nếu không → giữ bảng, chỉ xoá code đọc/ghi (ghi lý do trong commit/comment)

- [ ] **Step 1: Verify từng mục** bằng rg các symbol: `CaptureSnapshotAsync|workspace_file_snapshots|WorkspaceMergeEngine|WorkspaceCoordinatorService|TryClaimPathsAsync|ReleasePathClaimsAsync|TryRehydrateAsync` trên `backend-dotnet` + `gui-winui3`. Xác nhận consumer duy nhất là test (liệt kê). Kiểm tra `HarnessSqliteSchema` có migration/version để biết xoá bảng được không. Ghi kết quả vào report; mục nào còn consumer thật → giữ nguyên + báo.
- [ ] **Step 2: Xoá code + SQL + test wrapper tương ứng** (tests của chính snapshot/merge/service; giữ tests của phần còn sống).
- [ ] **Step 3: Full verify** theo mục chung.
- [ ] **Step 4: Commit** — `refactor(harness): drop dead workspace coordination machinery`

---

### Task 3: GUI workspace scaffold + route contract json + doc drift

**Files:**
- Delete: `gui-winui3/Services/ApiService.Workspace.cs`, `gui-winui3/Models/WorkspaceModels.cs` (verify paths; chỉ khi 0 UI caller)
- Modify: `backend-dotnet/public-route-contract.json` — cập nhật `winui_callers` cho các route workspace nếu scaffold bị xoá (giữ nguyên giá trị hợp lệ để checker python xanh)
- Modify: `docs/operations/shared-workspace-coordination.md:58-62` — sửa câu sai ("Chat surface shows mode, active claim count, peer/external entries") thành hiện trạng thật

- [ ] **Step 1: Verify** `rg -n "ApiService\.Workspace|WorkspaceModels" gui-winui3` → chỉ thấy định nghĩa + registration, không call-site UI.
- [ ] **Step 2: Xoá/sửa.**
- [ ] **Step 3: Verify** — `dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64 -p:Platform=x64 -p:SelfContained=true --nologo` + pytest guards (route matrix).
- [ ] **Step 4: Commit** — `refactor(gui,api): drop the unused workspace client scaffold and fix the coordination doc`

---

### Task 4: Split `WorkspaceCoordinatorStore.cs`

- [ ] **Step 1:** Đọc file; tách partial theo nhóm: `WorkspaceCoordinatorStore.cs` (core + policy + changes), `WorkspaceCoordinatorStore.Membership.cs` (Acquire/Renew/Release/Reconcile), `WorkspaceCoordinatorStore.Journal.cs` (changes/snapshot-if-kept). Thuần di chuyển — không đổi một ký tự logic.
- [ ] **Step 2:** Full verify.
- [ ] **Step 3:** Commit — `refactor(harness): split WorkspaceCoordinatorStore into partials by responsibility`

---

### Task 5: Split `AgentCommands.cs`

- [ ] **Step 1:** Tách partial theo nhóm lệnh: `.Run.cs` (run/attach/output), `.Chat.cs` (chat + slash handlers nếu còn trong file), `.Policy.cs`, `.Sessions.cs`, `.Approvals.cs` (trust/approve/deny/cancel/continue/status/command/reconcile). Giữ constructor + fields + `RpcAsync` trong file chính. Thuần di chuyển.
- [ ] **Step 2:** Full Cli.Tests + full verify.
- [ ] **Step 3:** Commit — `refactor(cli): split AgentCommands into partials by command family`

---

### Task 6: Split `BrokerToolGateway.cs`

- [ ] **Step 1:** Tách partial: `.Connection.cs` (WS/lease/hello), `.Tools.cs` (evaluate/prepare/execute), `.Approvals.cs` (approval/token). Thuần di chuyển.
- [ ] **Step 2:** Full verify (Broker + Host-side tests chạy trong test_dotnet).
- [ ] **Step 3:** Commit — `refactor(host): split BrokerToolGateway into partials by responsibility`

---

### Task 7: Split `AgentCliParser.cs`

- [ ] **Step 1:** Tách partial theo nhóm parse: `.Run.cs`, `.Policy.cs`, `.Sessions.cs`, `.Approvals.cs`; giữ helpers chung trong file chính. Thuần di chuyển.
- [ ] **Step 2:** Full verify.
- [ ] **Step 3:** Commit — `refactor(cli): split AgentCliParser into partials by command family`

---

## Sau D1+D2

- Cập nhật CHANGELOG entry (mục Removed/Changed) trong Task 3 hoặc commit tổng cuối.
- Lập spec + plan riêng cho **D3 (gộp Worker vào Host)** — thiết kế in-process `IModelStepRunner`, xoá IPC layer, ADR mới, cập nhật docs.
