# Model working environment — implementation plan

> **For agentic workers:** Use `superpowers:subagent-driven-development` with Luna, or `superpowers:executing-plans`, to implement the approved slices below. Checkboxes track work. Implementation was authorized by the user on 2026-10-04; verification is offline only, with no live provider spending.

status: in-progress

**Goal:** Giúp model làm việc hiệu quả trong Ru bằng ngữ cảnh liên tục, tool đúng protocol, kết quả có thể truy hồi và phản hồi lỗi có hành động rõ ràng.

**Architecture:** Cải thiện các owner hiện có của prompt, provider adapters, Harness context và client presentation. Broker vẫn là Execution Authority duy nhất; trạng thái công việc và bản chiếu ngữ cảnh không cấp thêm quyền. Mỗi giai đoạn phải có kết quả dùng được riêng và bằng chứng trước khi mở rộng.

**Tech Stack:** .NET 10, WinUI 3, Terminal.Gui, ASP.NET Core, SQLite encrypted Harness payloads, xUnit; Python chỉ dùng cho tooling/guard của repository.

**Baseline:** `979221ce0f714607041787337d9e93bf12b357b6`, đã push `origin/dev` ngày 2026-10-04. Đây là checkpoint trước triển khai sau bản sửa chat. Tasks 1–6 đã được kiểm chứng offline và nhận qua review, đến commit `850bbc43`; Task 7 (handoff, evidence, evaluator) đã triển khai và cả 12 scenario có executor thật với evidence trong `task-7-evidence/`; formal Task 7 review và matrix cuối đang chờ. Các nhận xét source bên dưới mô tả baseline, không phải capability hiện tại.

---

## 1. Phản hồi đã đọc và giới hạn của bằng chứng

Nguồn: Conversation Session `57efd63d083b4dfea8e640a63f72cee1`, tiêu đề **Hello**, cập nhật **2026-10-04 19:36:35 UTC+7**. Đã đọc trực tiếp JSON session trong dữ liệu Ru của người dùng. Phiên có bốn cặp hỏi–đáp, không có bằng chứng thực thi tool trong transcript; JSON không ghi provider/model. Không suy đoán model hoặc dùng lời tự thuật làm benchmark. Không đưa toàn bộ hội thoại riêng tư vào repository.

| Điều model nêu | Đối chiếu code hiện tại | Kết luận cho kế hoạch |
| --- | --- | --- |
| Không có continuity giữa các session; bắt đầu lạnh | `HarnessModelContextBootstrapper.cs` lấy tối đa tám cặp hỏi–đáp hoàn chỉnh của **session đang chọn**. `HarnessBaseContextStore.cs` và `SqliteModelContextSource.cs` bảo toàn cả lời user và assistant. | Cùng session đã có continuity; session mới độc lập. Thiếu handoff công việc có chọn lọc và lịch sử bằng chứng tool giữa các run. |
| Bounded output khiến làm việc chậm | `read` và `grep` có paging; `bash` có head/tail và `read_output`. Nhưng native conversation cắt mỗi block ở 6.000 ký tự; runtime reference cắt ở 32 KiB và chỉ giữ một tập kết quả mới nhất. | Giữ cap, cải thiện bản chiếu và đường đọc tiếp. Không giải quyết bằng output vô hạn. |
| Không có parallel exploration; một việc mỗi lần | `ToolCallScheduler.cs` cho tối đa tám read-only calls chạy đồng thời, giữ receipt theo model order và mutation/terminal barriers. `WorkerV2StepService.cs` chỉ bật native tool calling cho `chat_completions`; hai protocol còn lại dùng một JSON selection. | Nhận xét không đúng với toàn bộ Ru, nhưng phản ánh một hạn chế thật của Responses/Messages. Không khẳng định phiên này dùng protocol nào. |
| Muốn hiểu repo trước khi sửa và tự tin với nhiều file | Prompt nạp root `AGENTS.md`, `.ru/AGENTS.md`, `.ru/CONTEXT.md`; root `CONTEXT.md` cần model chủ động đọc. `edit` kiểm hash và thay thế atomic từng file. | Cải thiện định hướng repo, workflow và bằng chứng thay đổi. Không có giới hạn một file/run; ví dụ của model chỉ là giả định. Không hứa transaction nhiều file. |
| Muốn recovery tốt hơn khi test lỗi | Đã có durable `[HARNESS RECOVERY]`, edit-match hints, retry cùng provider và Continue. Nhiều loại lỗi vẫn có lời khuyên chung; note “use a different tool” thiếu phân biệt sửa lỗi với né denial. | Phản hồi cụ thể theo code, giữ nguyên bounds, ngăn lặp và không né policy. |
| Đánh giá bằng traces/tests thay vì lời khen | Hiện có deterministic headless loop và telemetry, chưa có baseline chất lượng từ provider thật. | Đặt đo lường thành điều kiện nhận mỗi giai đoạn. |

Thêm hai điểm thấy từ code, chưa được tái hiện bằng live provider:

- `RuPromptContextProvider.SelectModel` đọc settings thay đổi theo từng step, trong khi request thực thi dùng `ProviderBootstrap.Model` đã pin. `DynamicModelContextLimitProvider` cũng dùng settings hiện tại và suy luận context window theo tên model. Nhãn model và quyết định compaction có thể lệch khỏi run đang chạy sau khi đổi settings.
- `InProcessModelStepRunner.ClampConversationBlock` nói full output còn trong runtime references, nhưng reference cũng có cap/omission. Câu này không bảo đảm đúng với mọi kết quả lớn. Bản chiếu cần báo chính xác thứ model thực sự nhận.

## 2. Hướng chọn và phạm vi

| Hướng | Lợi ích | Đánh đổi |
| --- | --- | --- |
| Chỉ sửa prompt/help | Nhỏ, dễ đo, giúp dùng batching/paging hiện có | Không giải quyết native tools thiếu ở hai protocol hoặc mất trạng thái công việc |
| **Cải thiện từng phần của Harness và hiển thị GUI/TUI — chọn hướng này** | Khắc phục các hạn chế đã đối chiếu; cùng lõi phục vụ nhiều model | Cần adapter conformance, persistence và acceptance theo từng phần |
| Đồng thời thêm MCP, LSP, subagents và global memory | Mở thêm nhiều khả năng | Các dự án độc lập, phạm vi lớn; không có bằng chứng phiên Hello rằng chúng là nguyên nhân chính |

Thứ tự: dựng phép đo → làm rõ contract/nguồn metadata → native Responses → native Messages → continuity/compaction → output/recovery → GUI/TUI acceptance. Baseline đo trước mỗi phần, so sánh sau mỗi phần; không chờ xong cả roadmap mới đánh giá.

Không thay provider/model/protocol khi lỗi. Giữ 41 HTTP routes, bearer/loopback, không WebSocket; không tăng quyền hay tự bật Full Access. Native adapter chỉ dịch call/result, không thực thi tool. Không tự điều khiển GUI, nhập prompt vào session thật hoặc gọi API trả phí trong đợt lập kế hoạch này.

## 3. Các phần triển khai

### Task 1: A. Đo trải nghiệm làm việc trước khi thay đổi

**Owners:** `backend-dotnet/RuBackend.Tests/Harness/HeadlessCodingLoopTests.cs`, `backend-dotnet/RuBackend.Tests/Worker/InProcessModelConversationTests.cs`, `backend-dotnet/RuAgent.Harness.Core/Diagnostics/HarnessTelemetry.cs`, `docs/operations/coding-agent-capabilities.md`, `docs/operations/verification.md`.

**Artifact cần tạo khi triển khai:** `scripts/evaluate_agent_workflows.ps1` và `tests/fixtures/model-working-environment-scenarios.json`. Script gọi CLI/contract hiện có với data directory và workspace fixture riêng; không chạy trên project hoặc session thật của người dùng.

- [x] Khai báo 12 scenario có oracle: chat tiếng Việt không gọi tool; nhớ ràng buộc cũ sau hơn tám lượt; tiếp tục run sau restart; handoff đã chọn sang session mới; khám phá ba file độc lập; đọc file UTF-8 dài; grep vượt một page; lấy phần giữa output terminal; sửa ba file có dependency; phục hồi `edit_no_match`; phục hồi test thất bại; báo blocker sau denial mà không né policy.
- [x] Cho mỗi scenario lưu input, expected files/assertions, workspace hash và preset. Các ca capability chưa có được đánh dấu unmet, không viết expected-pass giả.
- [x] Chạy scripted providers để chứng minh harness wiring. Báo cáo riêng kết quả scripted và live.
- [x] Khi có model/account và ngân sách được người dùng chọn, chạy trước/sau ba lần mỗi scenario trên cùng provider/model/protocol/preset. Khóa cấu hình; việc thay model là một lượt đánh giá riêng.
- [x] Ghi task pass rate, ràng buộc bị quên, tool errors, call lặp, số lần hỏi approval, model steps, latency, tokens và cost nếu có giá. Không biến missing usage/price thành số 0.

**Gate:** mọi scenario có oracle; không dùng lời tự thuật hay số test toàn repo làm task-quality score. Bất kỳ policy bypass, sửa file ngoài expected set, mất current user prompt hoặc báo thành công sai bằng chứng đều chặn nhận phần đó. Mẫu ba lần là kiểm tra ban đầu, không đủ để tuyên bố parity giữa các coding agent.

### Task 2: B. Contract rõ ràng và metadata đúng run

**Modify:** `backend-dotnet/RuAgent.Harness.Core/Prompting/RuAgentPromptBlocks.cs`, `backend-dotnet/RuAgent.Harness.Core/Prompting/SystemPromptComposer.cs`, `backend-dotnet/RuBackend/Harness/Prompting/RuPromptContextProvider.cs`, `backend-dotnet/RuBackend/Harness/DynamicModelContextLimitProvider.cs`.

**Persistence owner nếu cần pin metadata thêm:** `backend-dotnet/RuAgent.Harness.Persistence/SqliteAgentRunProviderSelectionStore.cs`, `backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteSchema.cs`. Không thêm cột vào JSON selection/options tùy tiện; migration phải transaction, drain SQL và rollback đầy đủ.

**Tests:** `SystemPromptComposerTests.cs`, `RuPromptContextProviderTests.cs`, `DynamicModelContextLimitProviderTests.cs`, `HarnessSqliteSchemaTests.cs` trong các test project tương ứng.

- [x] RED: pin run A, đổi settings sang B, kiểm tra label model và context budgeting vẫn thuộc A. Kiểm bootstrap đầu tiên và restart riêng để tránh phụ thuộc vòng khởi tạo. Pin context limit từ capability registry/catalog chính xác, source/version và output reserve; unknown limit có trạng thái unknown và conservative cap rõ ràng, không suy từ model khác.
- [x] GREEN: dùng cùng durable run selection cho model label, protocol/mode và budgeting. Settings chỉ là ý định của run tiếp theo. Không làm đổi model thực thi hoặc đọc credential vào prompt.
- [x] RED/GREEN: native contract phải nói rõ independent read-only calls có thể overlap, receipts theo model order, dependent calls chờ kết quả, mutations/terminal là barriers. Text-selection contract vẫn chính xác một JSON object.
- [x] Thêm hướng dẫn gọn: trả lời chat bình thường bằng text, không bắt buộc tool/todo cho lời chào; khi làm coding thì explore → edit → verify. Câu trả lời về “khả năng của tôi” phải phân biệt capability có trong contract, điều đã thực thi và điều chưa kiểm tra.

**Prompt wording dự kiến:**

```text
Group independent read-only calls in one native batch; up to eight may overlap.
Results return in model order. Inspect them before choosing dependent calls.
Mutations and terminal calls remain ordered barriers. Batch execution is not a
multi-file transaction. Ordinary conversation can be answered directly in text.
```

Không biến đoạn trên thành một tuyên bố áp dụng cho text-selection. Bounds trong prompt phải lấy từ owner/capability chung, tránh một bảng hằng số khác trôi khỏi scheduler.

**Gate:** prompt và budget không đổi theo settings giữa run; không tiết lộ credential; protocol không có native support không bị quảng cáo có batching.

### Task 3: C1. Native tools cho OpenAI Responses

**Modify:** `backend-dotnet/RuAgent.Runtime.Adapters/Models/OpenAiResponsesModelProvider.cs`, `backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs`. Tách adapter request/history projection và stream assembly thành file cùng module nếu phần mới làm owner phình lớn; không đưa logic đó vào `AgentRuntimeLoopService.cs`.

**Tests:** `backend-dotnet/RuAgent.Runtime.Adapters.Tests/OpenAiResponsesModelProviderTests.cs`, `backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV2StepServiceTests.cs`, `backend-dotnet/RuBackend.Tests/Worker/InProcessModelConversationTests.cs`, `HeadlessCodingLoopTests.cs`.

- [x] RED: fake HTTP/SSE ghi nhận tool declarations, một hoặc nhiều `function_call`, stream arguments rời đoạn/interleaved, và lượt tiếp theo có `function_call_output` đúng `call_id`.
- [x] GREEN: dịch twelve Protocol Tool Definitions sang Responses functions; dịch typed assistant/tool history giữ identity. Narration đi riêng, JSON arguments không xuất hiện như lời chat. Các call chỉ qua scheduler → `IToolGateway` → Broker.
- [x] Bảo toàn stateless `store:false`. Với model yêu cầu reasoning items để tiếp tục tool loop, thiết kế typed, bounded continuation payload opaque riêng cho cùng run/provider; không tái dựng hidden reasoning bằng lời văn, không lưu credential hoặc dump prompt vào telemetry. Ca chưa hỗ trợ phải báo named capability error trước dispatch, không chuyển sang Chat Completions.
- [x] RED/GREEN: malformed arguments, duplicate/missing identities, unavailable tool, timeout/cancel, provider failure, replay sau approval; không thực thi trước durable acceptance và không thực thi hai lần.

**Gate:** request/response conformance và real Host/Broker loop qua; native mode chỉ được bật cho model/protocol đã khai báo hỗ trợ. Không chuyển sang text selection sau lỗi native.

### Task 4: C2. Native tools cho Anthropic Messages

**Modify:** `backend-dotnet/RuAgent.Runtime.Adapters/Models/AnthropicMessagesModelProvider.cs`, `backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs`; tách projection/stream assembly theo trách nhiệm như C1.

**Tests:** `backend-dotnet/RuAgent.Runtime.Adapters.Tests/AnthropicMessagesModelProviderTests.cs`, `WorkerV2StepServiceTests.cs`, `InProcessModelConversationTests.cs`, `HeadlessCodingLoopTests.cs`.

- [x] RED: `tools/input_schema`, `tool_use` content blocks, streamed input JSON, nhiều content blocks; trả `tool_result` trong user content với đúng `tool_use_id`, lỗi có error signal chính xác.
- [x] GREEN: thay việc từ chối mọi `tool_use` hiện có bằng decoder có bounds và identity checks. Giữ các đoạn text/thinking tách biệt khỏi tool arguments; protocol continuation blocks cần thiết phải được giữ đúng loại, không tự sinh thinking signature.
- [x] RED/GREEN: hai reads overlap tại scheduler nhưng ba mutations vẫn tuần tự; approval resume giữ nguyên proposal; interrupted partial batch không được coi là completed.

**Contract chung C1/C2:** persist calling mode của run hoặc migration version có cách diễn giải cố định tương đương. Run cũ giữ mode tương ứng hành vi trước nâng cấp; run mới dùng capability đã xác thực. Restart và lỗi không chọn mode mới từ settings. Legacy rows thiếu selection vẫn `provider_selection_missing`.

**Gate:** cùng internal `AssistantStep`/receipt semantics cho ba protocol; không có interpreter, execution path hay public route mới. Hai phần nhận độc lập, không chờ cả hai mới có ích.

### Task 5: D. Giữ trạng thái công việc qua lượt và compaction

**Owners hiện có:** `HarnessModelContextBootstrapper.cs`, `HarnessBaseContextStore.cs`, `Context/SqliteModelContextSource.cs`, `RuAgent.Harness.Core/Context/ContinuationSummaryBuilder.cs`, `ContextCompactionPlanner.cs`, `AgentTodoPayload.cs`, `RuBackend/Harness/Prompting/RuInstructionLoader.cs`.

**Đơn vị mới khi triển khai:** một `SessionWorkState` và codec trong Harness Core/Context; một persistence owner riêng trong Harness Persistence. Không nhồi orchestration vào GUI hoặc bootstrapper.

**Contract đề xuất:** work state tối đa 16 KiB UTF-8, mỗi danh sách tối đa 16 entries, nguồn bound theo session + canonical workspace identity + source run/version. Gồm user goal/constraints, task list gần nhất, file đã sửa và hash đã quan sát, verification code/trạng thái, blocker và next action có provenance. Goal/constraints giữ nguồn user; phần suy diễn của model là untrusted. Không chứa approval token, grant, credential hay raw terminal dump.

- [x] RED: ràng buộc ở lượt 1 vẫn được trình bày ở lượt 10; task chưa xong và test thất bại không biến thành completed sau compact/restart; cả user và assistant history sống qua persistence round-trip.
- [x] GREEN: bootstrap lấy bounded work state cùng tám turns gần nhất. Cùng Conversation Session có trạng thái công việc; session mới vẫn độc lập cho đến khi user chọn handoff. Không âm thầm quét mọi session.
- [x] Tạo handoff từ state đã commit, kèm nguồn và thời điểm. User preview/chọn đưa sang lượt/session mới như context attachment hiện có. Handoff không chuyển quyền, không biến hash cũ thành hash hiện tại; model phải đọc lại file trước sửa. Workspace khác báo stale/mismatch rõ ràng.
- [x] RED/GREEN: compaction ưu tiên goal, constraints, verified changes, pending work, blockers thay vì chỉ đầu đoạn mới nhất. Giữ current user prompt, whole call/result groups và covered ranges. Nội dung không có nguồn phải mang nhãn unknown; không suy “test pass” từ văn bản assistant.
- [x] Tính work state trong tổng budget của model đã pin, cùng system instructions, output reserve và history. Không chỉ tăng số turns hoặc thêm 16 KiB vào mọi request bất kể context window; khi các phần được bảo vệ không còn fit, park/fail với code rõ ràng theo contract, không âm thầm cắt current prompt.
- [x] Tự động nạp root `CONTEXT.md` bằng cùng bounds/path/encoding checks của instruction loader nếu có; không tự đọc toàn bộ architecture/docs. Mark guidance là workflow, không phải policy. Các file hướng dẫn lớn giữ lazy discovery thay vì tăng prompt cap.
- [x] Atomic write/version check, restart/delete và corruption tests: state của session đã xóa không được dùng lại; cạnh tranh hai clients không đè state mới bằng bản cũ; không mang nguồn run/session khác vào state.

**Tests:** `HarnessModelContextBootstrapperTests.cs`, `ModelContextSourceTests.cs`, `ContextCompactionPlannerTests.cs`, `ContextCompactionCoordinatorTests.cs`, `ModelContextLimitTests.cs`, `RuInstructionLoaderTests.cs`; thêm `SessionWorkStateTests.cs` vào Core và persistence tests theo owner mới.

**Gate:** quên chi tiết thô vẫn có thể xảy ra nhưng model biết phần bị omitted; goal/task/blocker sống qua các scenario. Không tuyên bố có global memory hay multi-file rollback.

### Task 6: E. Output có đường đọc tiếp và recovery có hành động

**Modify:** `RuBackend/Harness/Prompting/RuPromptContextProvider.cs`, `RuBackend/Harness/Worker/InProcessModelStepRunner.cs`, `RuAgent.Harness.Core/Prompting/AgentRecoveryNotes.cs`, `RuAgent.Protocol/AgentToolDefinitionCatalog.cs`. Dùng projection helper riêng để tránh nhân đôi truncation rules.

**Broker owners:** `Tools/Files/FileReadAdapter.cs`, `FileSearchAdapter.cs`, `FileGlobAdapter.cs`, `Tools/Terminal/TerminalOutputSpool.cs`, `Tools/ToolExecutionService.cs`; chỉ sửa result contract khi có test proving cần thiết, không tăng caps/authority để làm scenario pass.

- [x] RED: nội dung lớn không làm mất `path`, `truncated`, offset/bytes, hash, error/code, provider/tool call identity hoặc spool metadata trên bản model nhận. JSON projection vẫn parse được; UTF-8/UTF-16 không cắt giữa ký tự.
- [x] GREEN: project nội dung theo ngân sách nhưng giữ metadata cần đọc tiếp. `read` dùng byte offset + valid `bytes_read`; `grep` dùng result offset; `bash read_output` dùng đúng đơn vị spool. Ghi rõ unit, còn dữ liệu hay hết; không đoán một cursor chung cho các tool.
- [x] Dùng structured projection cho payload đã nhận diện schema; generic `FitReferenceContent` vẫn giữ contract prefix/bound hiện có. Thêm tests cho structured projector, không nới hoặc bỏ tests UTF-8/bisection cũ.
- [x] Truncation message nói đúng cả giới hạn conversation lẫn reference; bỏ tuyên bố full output luôn còn trong prompt. Tránh gửi cùng body hai lần nếu gây lãng phí; giữ nguyên durable payload và pairing/codes, chỉ thay model projection.
- [x] RED/GREEN: lỗi `edit_no_match` → đọc lại cùng file; `edit_not_unique` → cụ thể hóa đoạn hoặc `expected_occurrences`; `workspace_changed` → kiểm tra/reconcile, không overwrite; test failure → đọc diagnostics, sửa và chạy lại test cần thiết; denied → báo blocker hoặc hành động khác chỉ nếu không né cùng restriction. `side_effects_possible` phải inspect trước retry.
- [x] Phân biệt model chủ động chọn một advertised tool cho việc khác với harness âm thầm thay tool/backend. Sửa câu “use a different tool” theo code/hint để không mời né denial; không cấm mọi workflow có nhiều loại tool.
- [x] Giữ nguyên giới hạn recovery 2/1/2 và tổng 6 theo semantics hiện có; lặp thất bại hết budget vẫn fail closed. Kế hoạch này không cho phép tự reset budget.

**Tests:** `PromptReferenceFittingTests.cs`, `InProcessModelConversationTests.cs`, `Prompting/FailedToolCompletionGuardTests.cs`, `AgentRunCoordinatorTests.cs`, `HeadlessCodingLoopTests.cs`, Broker file/terminal adapter tests.

**Gate:** tìm được marker ở phần dữ liệu bị cắt bằng paging và không re-run command mutation để lấy output; không báo thành công khi verification chưa đạt; nhận định multi-file partial progress chính xác.

### Task 7: F. GUI và TUI cho user hiểu model đang làm việc với gì

**Owners:** `gui-winui3/Services/AgentContextMeterPolicy.cs`, `AgentRunController.cs`, các partial `gui-winui3/Views/ChatView.*.cs`; `backend-dotnet/RuAgent.Cli/Tui/TuiStatusLine.cs`, command dispatcher hiện có. Chia logic context presentation thành service/presenter dùng lại; views chỉ render và forward intent.

- [ ] Hiển thị provider/model của retained run, pause/blocker/recovery và compaction/omission bằng evidence hiện có. Không lấy next-turn settings để gắn nhãn run cũ; không hiện phần trăm context khi chưa có denominator đúng.
- [ ] Cho preview handoff với source/date, goal, unfinished tasks và stale files; đưa vào lượt mới qua attachment flow hiện có, không thêm route. Khi submit thất bại vẫn giữ draft/context.
- [ ] Timeline gọn cho read batch, mutation, verify: cho thấy file set và receipt/code, phân biệt verified/unverified/partial. Không hiển thị hidden reasoning hoặc plaintext prompt dump làm “transparency”.
- [ ] Dùng cùng acceptance cases trên GUI và CLI: gửi chat, queue/interrupt, approval, Continue sau restart, xem diff và tìm output bị cắt. Keyboard, resize và native Enter được user hoặc tester chạy chủ động; automation không tự điều khiển máy người dùng.

**Tests:** existing GUI context-meter/controller/source-contract tests và CLI command/status tests; behavioral tests cho session ownership/stale data. Không thêm hàng loạt snapshot chỉ sao chép implementation.

**Gate:** user phân biệt được run chờ model, approval hay chính mình; không mất draft hoặc gắn evidence sang session khác. Model quality và native UI acceptance báo cáo riêng.

## 4. RED/GREEN và verification theo phần

Khi triển khai, thêm regression test của phần trước, chạy filter để thấy lỗi cụ thể, sửa tối thiểu, rồi chạy lại cùng filter. Không thay expected values của contract cũ chỉ để xanh. Các dòng dưới chạy **tuần tự**, chỉ chọn project liên quan phần đang sửa; không chạy nhiều matrix cùng lúc.

```powershell
# A/B/D: bootstrap, prompt identity and context limits
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Debug --filter 'FullyQualifiedName~HarnessModelContextBootstrapperTests|FullyQualifiedName~RuPromptContextProviderTests|FullyQualifiedName~DynamicModelContextLimitProviderTests|FullyQualifiedName~InProcessModelConversationTests'
# B/D/E: prompt, compaction and existing scheduler barriers
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj -c Debug --filter 'FullyQualifiedName~SystemPromptComposerTests|FullyQualifiedName~ContextCompactionPlannerTests|FullyQualifiedName~ModelContextLimitTests|FullyQualifiedName~ToolCallSchedulerTests|FullyQualifiedName~FailedToolCompletionGuardTests'
# C1/C2: adapter wire conformance, then common step service
dotnet test backend-dotnet/RuAgent.Runtime.Adapters.Tests/RuAgent.Runtime.Adapters.Tests.csproj -c Debug --filter 'FullyQualifiedName~OpenAiResponsesModelProviderTests|FullyQualifiedName~AnthropicMessagesModelProviderTests'
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj -c Debug --filter 'FullyQualifiedName~WorkerV2StepServiceTests'
# D: durable history, migration and state ownership
dotnet test backend-dotnet/RuAgent.Harness.Persistence.Tests/RuAgent.Harness.Persistence.Tests.csproj -c Debug --filter 'FullyQualifiedName~ModelContextSourceTests|FullyQualifiedName~HarnessSqliteSchemaTests|FullyQualifiedName~SessionWorkState'
# C/E: real Host/Broker edit, verify and failure paths
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Debug --filter 'FullyQualifiedName~HeadlessCodingLoopTests|FullyQualifiedName~PromptReferenceFittingTests'
```

GREEN: tất cả selected tests phải qua, không zero selected tests; failure/cancel vẫn có bounded named code. Trước khi nhận code delivery:

```powershell
dotnet build backend-dotnet/RuBackend/RuBackend.csproj -c Debug
.\scripts\test_dotnet.ps1 -Configuration Debug
.venv\Scripts\python.exe -m pytest tests -q
```

Nếu sửa GUI/TUI thêm các behavioral tests liên quan trong matrix và explicit native acceptance. Không chạy live provider, build Release hoặc suite lớn cho một thay đổi tài liệu thuần túy.

## 5. Documentation và lifecycle

- [x] Mỗi phần hoàn tất cập nhật `CHANGELOG.md` cho hành vi user-visible và các owner tương ứng trong `docs/architecture.md`, `docs/operations/runtime-behavior.md`, `docs/operations/agent-tools.md`, capability map, verification.
- [x] Chỉ thêm thuật ngữ `SessionWorkState` vào `CONTEXT.md` sau khi design được chấp nhận và code có owner rõ ràng; không trình bày đề xuất như capability đã tồn tại.
- [ ] Baseline và acceptance records ghi date, commit, model/protocol, preset, outcome và giới hạn. Không ghi secrets, raw session hay hidden reasoning.
- [ ] Commit nhỏ, scope rõ, stage đúng file của phần; chỉ commit/push theo authorization tại thời điểm triển khai. Không đưa `logo1.png` vào commit.
- [ ] Khi mọi phần được nhận, chuyển các quy tắc vận hành cần giữ sang docs hiện hành rồi xóa kế hoạch khỏi checkout; Git history giữ design/evidence. Không xóa docs còn đúng chỉ vì tên file có ngày.

## 6. Nguồn protocol đã kiểm tra ngày 2026-10-04

Responses function calling dùng native call/result identity, có thể trả nhiều calls trong một response: [OpenAI function calling](https://developers.openai.com/api/docs/guides/function-calling#handling-function-calls).

Reasoning continuation trong tool loop cần giữ các provider items liên quan, khác với summary hiển thị: [OpenAI reasoning items](https://developers.openai.com/api/docs/guides/reasoning#keeping-reasoning-items-in-context).

Anthropic client tools trả `tool_use` và nhận `tool_result`; application sở hữu execution: [Anthropic tool use](https://platform.claude.com/docs/en/agents-and-tools/tool-use/overview). Trong Ru, application execution phải luôn là Broker, không dùng SDK tool runner tự thực thi.

Những nguồn này chứng minh protocol có cơ chế cần tích hợp; không chứng minh adapter Ru đã hỗ trợ hay mọi model có cùng capability. Chi tiết streaming/reasoning continuation cần đối chiếu official protocol tests trong C1/C2 trước khi bật native mode.

## 7. Các điểm kiểm tra source ở baseline

| Quyết định trong kế hoạch | Source pointer |
| --- | --- |
| Native calling chỉ bật cho Chat Completions | `backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs:85` |
| Tối đa tám read-only calls concurrent | `backend-dotnet/RuAgent.Harness.Core/ToolCallScheduler.cs:7` |
| Tám completed turns trong bootstrap | `backend-dotnet/RuBackend/Harness/HarnessModelContextBootstrapper.cs:25` |
| Assistant base context được rehydrate đúng loại | `backend-dotnet/RuAgent.Harness.Persistence/Context/SqliteModelContextSource.cs:308` |
| Summary 4.000 chars, head 400 chars/entry | `backend-dotnet/RuAgent.Harness.Core/Context/ContinuationSummaryBuilder.cs:15` |
| Conversation block 6.000 chars | `backend-dotnet/RuBackend/Harness/Worker/InProcessModelStepRunner.cs:26` |
| Root CONTEXT chưa nằm trong instruction inputs | `backend-dotnet/RuBackend/Harness/Prompting/RuInstructionLoader.cs:43` |
| Prompt model lấy từ settings | `backend-dotnet/RuBackend/Harness/Prompting/RuPromptContextProvider.cs:74` |
| Model thực thi lấy từ bootstrap | `backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs:108` |

Line pointers là evidence của baseline, phải đọc lại surrounding code khi triển khai sau thay đổi khác. Đã self-review: mỗi nhóm phản hồi có phần triển khai và gate, assistant bootstrap không bị báo sai là mất lịch sử, khác biệt native/text-selection được giữ, mọi cải thiện execution vẫn qua Broker.
