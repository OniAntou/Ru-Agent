---
status: pending
---

# D3 Implementation Plan — Gộp Worker vào Host

> **For agentic workers:** Thực thi tuần tự T1→T7; mỗi task commit riêng, review riêng; suite xanh trước/sau mỗi task. Spec: `docs/plans/2026-09-30-worker-merge-design.md`. Baseline `36aae1c3`.

**Verify chung mỗi task** (từ `D:\LLM`): project test liên quan → `.venv\Scripts\python.exe -m pytest tests -q` → `.\scripts\test_dotnet.ps1 -Configuration Debug` (green; exit 75 = retry).

---

### T1: Typed StepService + Worker project thành class library

**Files:** `RuAgent.Runtime.Worker/WorkerV2StepService.cs`, `WorkerV2Options.cs`, `RuAgent.Runtime.Worker.csproj`, tests.

- Thêm records typed (trong `RuAgent.Runtime.Worker`): `ModelGenerationRequest(ModelMessages, SystemPromptContext, ModelGenerationSettings, ProviderBootstrapData ProviderBootstrap, Images)`, `ModelGenerationSettings(ModelTools, MaxOutputTokens?)`, giữ `WorkerV2StepResult` (đổi tên `ModelGenerationResult` nếu tiện — nếu đổi phải sửa mọi consumer; khuyến nghị GIỮ tên cũ để giảm churn).
- `WorkerV2StepService.RunAsync(typed request, Func<string,ValueTask>? onDelta, ct, modelStepId)`: thay `ReadStart` bằng nhận typed trực tiếp; xoá toàn bộ nhánh bootstrap file (`:103-107, :771-811, :834-849`) — dùng `request.ProviderBootstrap`; xoá `WorkerV2Options` (giữ `DefaultOptions`-tương đương cho test qua ctor overload `(IModelProviderResolver)`).
- Di chuyển hằng còn dùng từ contracts vào `ModelGenerationBounds` static (giá trị y nguyên).
- Xoá files worker-only: `Program.cs`, `WorkerV2Server.cs`, `WorkerRequestHistory.cs`, `ManagedWorkerComposition.cs`, `WorkerV2PromptCodec.cs`, `WorkerV2Options.cs`.
- csproj → `Microsoft.NET.Sdk`, bỏ Exe/R2R publish bits.
- Tests: xoá `WorkerV2ServerContractTests`, `WorkerRequestHistoryTests`, `Phase1WorkerIntegrationTests`, `ManagedWorkerCompositionTests`, `WorkerV2PromptCodecTests`; adapt `WorkerV2StepServiceTests` (40 → đọc lại, giữ các case logic; case bootstrap-file → typed bootstrap; các case validate context/messages chuyển thành typed validation tests).
- **Gate:** không xoá `DeltaHoldback`/caps/receipt validation; `WorkerV2StepException` codes giữ.
- Commit: `refactor(worker): make the model step service typed and drop the worker transport`

### T2: `InProcessModelStepRunner` + registration + snapshot direct

**Files:** `RuBackend/Harness/Worker/InProcessModelStepRunner.cs` (mới), `AgentProviderSnapshotFactory.cs`, `PublicHostServiceRegistration.cs`, xoá `LoopbackModelStepRunner.cs` + `WorkerClientOptions.cs` + Host worker tests tương ứng.

- Runner mới implement `IModelStepRunner`: move từ Loopback **verbatim logic** các nhóm: BuildConversation/BuildTypedConversation/FitModelMessagesToBudget/ClampConversationBlock/ResolveTypedPrompt/BuildSettings/BuildModelTool/__selection feedback__; giữ retry loop 3 attempts backoff 2s/5s cho provider transient (`WorkerUnavailableException` → new `ModelProviderTransientException`? nếu cần type mới đặt trong runner file; durable code giữ `model_failed` cho exhausted transient); giữ sequence/IsReset từ `:256-377`; bỏ toàn bộ socket/hello/probe/cancel-frame.
- `AgentProviderSnapshotFactory`: thêm `Task<ProviderBootstrapData> GetAsync(runId, ct)` (giữ cache/fail-fast; bỏ ghi file). Xoá `ProtectedProviderBootstrap` nếu rg xác nhận 0 consumer (trừ tests sẽ xoá).
- Registration: thay khối `:201-246` bằng `IModelProviderResolver` (3 adapters + HttpClient infinite, y như `ManagedWorkerComposition` cũ) + `WorkerV2StepService` singleton + `InProcessModelStepRunner`; scripted branch giữ nguyên.
- Tests: xoá `WorkerV2ClientTests` (transport) nhưng **port sang runner mới**: retry/backoff cases, reset-frame, delta mapping; xoá `WorkerUnavailableRetryTests` → thay `InProcessRetryTests`; `LoopbackModelConversationTests` đổi chủ sang runner mới (nội dung verbatim); xoá `WorkerClientOptionsTests`, `WorkerV2ContractTests` các case transport (đọc kỹ — phần `ValidateStart`/golden adapt sang typed nếu còn giá trị logic).
- **Gate:** scripted runner branch không đổi; `AgentModelInvocation` không đổi.
- Commit: `refactor(host): run model steps in process`

### T3: Xoá IPC/hosting/spawn còn lại

**Files:** `RuAgent.Protocol/Harness/WorkerV2Contracts.cs`+`WorkerV2Envelope.cs` (xoá; move hằng theo T1), `RuAgent.Hosting/{WorkerBrokerLaunchPlan,WorkerBrokerProcessManager,WorkerBrokerStartupOptions,ChildProcessPolicy,RuAppLayout,PublicHostRuntimeOptions}.cs`, `RuBackend/{Program.cs,Hosting/ManagedRuntimeBootstrapper.cs,Hosting/PublicHostServiceRegistration.cs}`, `RuAgent.Client/PublicHostStarter.cs` (comment).

- Theo inventory khảo sát mục 5/6: bỏ worker spec/env/token/port; readiness còn 1 cổng; watchdog giữ cho Broker; `IsFlatLayout` 2 siblings; role env `dotnet-agent-worker` bỏ; `RU_HARNESS_WORKER_TOKEN` branch bỏ.
- Tests: xoá/adapt `WorkerV2EnvelopeCodecTests`, `WorkerV2ContractHardeningTests`, `UsageModelOnTheWireTests` (adapt phần usage còn giá trị), `Phase1GoldenParityTests` (adapt native multi-call), `StepStartCapabilityDigestTests`, `AgentPolicyProfileParityTests`; Hosting adapt: `WorkerBrokerLaunchPlanTests`, `ChildProcessPolicyTests`, `RuAppLayoutTests`, `OwnedProcessTreeTests`.
- Commit: `refactor(host): drop the worker ipc layer and its launcher`

### T4: Copy/failure-code + deterministic harness

- Bỏ `RU_HARNESS_WORKER_TOKEN` hoàn toàn (rg trước; test nào dùng thì adapt).
- CLI `TuiFailureCatalog` + tests, `TuiTranscriptTests:158`, GUI `AgentFailurePresentation` + tests, `MainWindow.xaml.cs` copy: sửa chữ "worker process" → "model backend" nơi sai sự thật; **giữ entries code `worker_unavailable`/`worker_failed`** (lịch sử) trừ khi test pin mutual-exclusion yêu cầu ngược — đọc test trước khi sửa.
- Commit: `refactor(cli,gui): retire worker-process copy and the deterministic worker token`

### T5: Build/scripts + guards

- `build.bat`: bỏ stage publish worker (13→12 stage; sửa đánh số/flatten/checks/output), `start_dev.bat`, `stop_dev.bat` (4 process).
- `tests/test_build_script.py`: adapt theo stage mới (bỏ stub worker, publish order, flat list 5→4 exe, start_dev).
- Commit: `build(repo): drop the worker executable from the publish layout`

### T6: Docs/ADR + counts

- ADR mới `docs/adr/0012-in-process-model-runner.md` (supersede ADR-0002 + phần Worker ADR-0001; ghi trade-off isolation đã chấp nhận).
- Cập nhật inventory khảo sát mục 8 (architecture/runtime-behavior/CONTEXT/backend README/agent-tools/logging/python-runtime-boundary/tui-known-limitations/docs README).
- Sau full run cuối: cập nhật `verification.md:9,11` + README badge/line counts theo số thật; CHANGELOG entry Changed/Removed.
- Commit: `docs(repo): document the in-process model runner`

### T7: Final verify + phase review

- Full matrix + pytest + GUI Release + RPC smoke + scripted e2e; grep sạch các symbol đã xoá; `git status` sạch.
- Final whole-branch review (spec D3) — findings → 1 fix wave + scoped re-review.
- Push dev theo rule repo (báo chủ nhân).
