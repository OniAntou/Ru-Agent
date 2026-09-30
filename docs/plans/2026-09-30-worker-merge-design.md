---
status: pending
---

# Spec: D3 — Gộp Runtime Worker vào Host (giữ Broker tách)

Ngày: 2026-09-30. Baseline: `36aae1c3`. Thuộc đợt đơn giản hoá (`2026-09-30-simplification-design.md`).

## Quyết định thiết kế đã chốt

1. **Xoá hẳn IPC contracts** (`WorkerV2Contracts.cs` 1,251 + `WorkerV2Envelope.cs` 256): in-process không cần wire validation. Các hằng số còn sống (`MaxPromptChars`, `MaxModelMessages`, `MaxModelMessagesBytes`, `MaxToolArgumentsBytes`...) chuyển thành `ModelGenerationBounds` static trong `RuAgent.Runtime.Worker` (hoặc nơi dùng), giữ nguyên giá trị.
2. **StepService nhận typed request** (`ModelGenerationRequest` record mới trong `RuAgent.Runtime.Worker`): `ModelMessages` (IReadOnlyList<ModelTurn>), `SystemPromptContext`, settings typed (model tools + max output tokens), `ProviderBootstrapData` trực tiếp, `Images` (IReadOnlyList<ModelImage>). Xoá đường `JsonObject`/codec (`WorkerV2PromptCodec` die). Receipt validation + DeltaHoldback + caps giữ nguyên.
3. **Retry/backoff + selection feedback + sequence/reset chuyển vào `InProcessModelStepRunner`** (runner mới trong `RuBackend/Harness/Worker/`): 3 attempts, backoff 2s/5s, reset frame semantics giữ nguyên. Failure codes durable giữ như hiện tại (`model_failed`, `IStableStepError` passthrough); `worker_unavailable`/`worker_failed` trở thành code lịch sử — **giữ copy trong catalog UI/CLI** (harmless) nhưng không còn đường phát sinh; copy nào nói "worker process riêng" thì sửa wording.
4. **Bỏ deterministic harness branch** (`RU_HARNESS_WORKER_TOKEN`); scripted runner (`RU_SCRIPTED_MODEL`) là đường deterministic duy nhất — đã tách biệt từ trước, e2e không đổi.
5. **Giữ project `RuAgent.Runtime.Worker` như class library** (StepService + tests sống ở đó; đổi `Microsoft.NET.Sdk.Web`→`Microsoft.NET.Sdk`, bỏ `OutputType=Exe`, bỏ publish stage). Số project backend không đổi (11 + GUI = 12 test project); exe giảm 5→4, process 5→4.

## Phạm vi thay đổi

**Giữ & chuyển** (từ `LoopbackModelStepRunner` 1,025 dòng → runner mới):
- BuildConversation/BuildTypedConversation/FitModelMessagesToBudget/ClampConversationBlock/ResolveTypedPrompt/BuildSettings/BuildModelTool/BuildSelectionFeedback/ApplySelectionFeedback — chuyển nguyên logic sang `InProcessModelStepRunner`.
- Retry loop + backoff + `WorkerUnavailableException` retry semantics (transient provider error vẫn retry 3 lần) — chuyển sang runner mới với provider exception thay socket exception.
- Sequence monotonic + IsReset frame — giữ trong runner mới.

**Xoá:**
- Worker exe: `Program.cs`, `WorkerV2Server.cs`, `WorkerRequestHistory.cs`, `ManagedWorkerComposition.cs`, `WorkerV2PromptCodec.cs`, `WorkerV2Options.cs`.
- Protocol: `WorkerV2Contracts.cs`, `WorkerV2Envelope.cs` (+ tests).
- Host: `LoopbackModelStepRunner.cs`, `WorkerClientOptions.cs`; delegate `CreateProviderBootstrapAsync`; `BuildAgentWorkerClientOptions`.
- Bootstrap file handoff: `ProtectedProviderBootstrap` (nếu 0 consumer còn lại — verify; Broker không dùng), `ManagedProviderBootstrapFactory`, `WorkerBrokerStartupOptions.ProviderBootstrapPath`, nhánh read/delete trong StepService (`:771-811, :834-849`).
- Hosting: phần Worker của `WorkerBrokerLaunchPlan`, `WorkerBrokerProcessManager` (1 cổng + 1 watchdog), `ChildProcessPolicy` role worker, `RuAppLayout.ResolveWorkerExecutable`, `PublicHostRuntimeOptions` worker port; `ManagedRuntimeBootstrapper` phần worker (port 8005, startup bootstrap, start/cleanup worker).
- Scripts: `build.bat` stage publish worker (13→12 stage), `start_dev.bat`/`stop_dev.bat` worker refs.

**Provider snapshot**: `AgentProviderSnapshotFactory` thêm API trả `ProviderBootstrapData` trực tiếp (giữ cache + fail-fast codes); runner mới gọi API đó; Host vốn giữ credential nên không mất gì thêm về bảo mật (trade-off đã chấp nhận trong design tổng).

## Hành vi không đổi (bắt buộc)

- Wire/route/UI/session format/policy giữ nguyên.
- Scripted e2e + RPC smoke giữ nguyên đường chạy.
- Run fail-closed như cũ; Broker tách nguyên vẹn (ADR-0009/0011 untouched).
- CLI/GUI: mọi copy còn đúng nghĩa; code names lịch sử giữ trong catalog, chỉ sửa chữ "worker process" khi sai sự thật.

## Test fate (theo khảo sát)

- DIE: `WorkerV2ServerContractTests`, `WorkerRequestHistoryTests`, `Phase1WorkerIntegrationTests`, `ManagedWorkerCompositionTests`, `WorkerV2ClientTests` (transport phần lớn), `WorkerUnavailableRetryTests` (thay bằng test retry provider ở runner mới), `WorkerClientOptionsTests`, `WorkerV2EnvelopeCodecTests`, `ProviderCredentialIsolationTests`, phần codec của `WorkerV2PromptCodecTests`.
- SURVIVE (adapt chủ): `WorkerV2StepServiceTests` (~35/40), `LoopbackModelConversationTests` (15 — đổi gọi sang runner mới), retry tests mới (3), `WorkerBrokerLaunchPlanTests` (bỏ phần worker), `ChildProcessPolicyTests`, `RuAppLayoutTests`, `OwnedProcessTreeTests` (role broker).
- SURVIVE nguyên: `RuntimeDescriptorTests`, scripted e2e suite, route matrix, checks khác.

## Docs/ADR

- ADR mới `0012-in-process-model-runner.md` supersede ADR-0002 + phần Worker của ADR-0001.
- Cập nhật: `architecture.md`, `runtime-behavior.md`, `CONTEXT.md`, `backend-dotnet/README.md`, `agent-tools.md`, `logging.md`, `python-runtime-boundary.md`, `tui-known-limitations.md`, `verification.md` (+ số test cuối), README counts, CHANGELOG; guards `test_build_script.py`, `test_readme_counts.py`, `test_current_documentation.py` phải xanh (giữ từ khoá guard yêu cầu).
- `docs/README.md`: link ADR-0002 → thay bằng ADR-0012.

## Verify cuối

Full `test_dotnet.ps1` + pytest + GUI Release build + `scripts/rpc_smoke.ps1` + scripted e2e; grep sạch `WorkerV2Server|LoopbackModelStepRunner|RU_AGENT_WORKER|8005|ProtectedProviderBootstrap`; 5→4 process trong docs/scripts khớp build.bat mới.
