# Ru Conversation and Agent Reliability Implementation Plan

> **Status:** Historical implementation record. The reliability fixes shipped;
> current runtime behavior lives in `docs/operations/runtime-behavior.md`.
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task with test-first checkpoints.

**Goal:** Làm cho Chat và Coding Agent giữ đúng thứ tự lượt, bỏ stale result, định tuyến câu ngắn tự nhiên, truy cập thư mục ngoài qua approval một lần và hiển thị kết quả Agent an toàn.

**Architecture:** Giữ ASP.NET Core Conversation, Harness Core/Broker và WinUI là các boundary riêng. Bổ sung correlation-aware event contract và queue/presenter nhỏ ở Chat; đặt intent resolver trước model; tái sử dụng Broker external-directory approval và activity feed hiện có; thêm terminal summary transient từ typed safe fields. OS-enforced filesystem/network sandbox vẫn là dự án riêng.

**Tech Stack:** C#/.NET 10, ASP.NET Core WebSocket/SSE, WinUI 3/XAML, xUnit, Python pytest.

---

## Guardrails

- Thực hiện trực tiếp trên `main` vì người dùng đã cho phép; không reset/checkout các file dirty có sẵn.
- Mỗi nhóm bắt đầu bằng test đỏ, chạy test hẹp, rồi mới sửa production code.
- Chỉ stage các file/hunks của nhóm đang làm; activity-feed dirty hiện tại thuộc baseline khác.
- Không đưa đường dẫn thô, lệnh shell, token, VTS/voice metadata vào chat history, activity feed hoặc summary.
- Sau mỗi nhóm cập nhật tài liệu hiện hành nếu contract thay đổi; không xoá plan/spec lịch sử chỉ vì cũ.

## Task 0 — Baseline and scope safety

- [x] Ghi nhận `git status --short --branch`, current commit và solution/test entrypoints vào log làm việc.
- [x] Kiểm tra plan này không đè lên `docs/superpowers/plans/2026-08-28-agent-activity-feed.md` và inventory các file dirty trước đó.
- [x] Chạy smoke compile/test nhỏ hiện có để phân biệt lỗi nền với lỗi mới:
  `.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q` và `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~ChatContractFixtureTests`.

## Task 1 — Correlation-aware Chat event contract

- [x] Tạo `backend-dotnet/RuBackend.Tests/Conversation/ChatCorrelationTests.cs` kiểm tra ID hợp lệ, ID thiếu, ID sai định dạng và event JSON giữ `correlation_id` nullable.
- [x] Cập nhật fixture `backend-dotnet/RuBackend.Tests/Conversation/ChatContractFixtureTests.cs` để assert correlation của `session`, `reset`, `chunk`, `done`, `error`.
- [x] Thêm `backend-dotnet/RuBackend/Conversation/ChatCorrelation.cs` với `Generate`, `IsValid`, `Normalize`; sửa `ChatEvent.cs` thêm `CorrelationId` ở cuối constructor để giữ compatibility.
- [x] Chạy RED rồi GREEN:
  `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~ChatCorrelationTests`.

## Task 2 — Server routing, session gate and stale protection

- [x] Thêm test endpoint/coordinator cho correlation propagation, second request cùng session trả busy có correlation đúng, request khác session chạy song song, và disconnect không phát event uncorrelated.
- [x] Tạo `backend-dotnet/RuBackend/Conversation/ConversationSessionGate.cs` keyed semaphore có capacity/cleanup; đăng ký singleton trong `backend-dotnet/RuBackend/Program.cs`.
- [x] Sửa `ChatWebSocketEndpoint.cs` và `ConversationCoordinator.cs` để truyền correlation qua toàn bộ event; validate ở boundary; buffer inbound deferred tối đa 8, reject busy gắn đúng correlation, terminal event đúng một lần.
- [x] Chạy:
  `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~ChatWebSocketEndpointTests` và `... --filter FullyQualifiedName~ConversationCoordinatorTests`.

## Task 3 — Client typed turn queue

- [x] Tạo `gui-winui3/Services/ChatEventFrame.cs`, `ChatTurn.cs`, `ChatTurnQueue.cs`, `ChatTurnPresenter.cs`; thêm test `gui-winui3.Tests/ChatTurnQueueTests.cs`, `ChatTurnPresenterTests.cs`, `ChatWebSocketClientContractTests.cs` cho FIFO, capacity 8, stale generation drop và terminal-once.
- [x] Sửa `gui-winui3/Services/ChatWebSocketClient.cs` để parse typed frame, expose correlation, nhận correlation ID từ caller và không tự làm mất association.
- [x] Bổ sung `TurnId`/`SessionId` nullable cho `gui-winui3/Models/Models.cs` để history cũ vẫn đọc được.
- [x] Chạy focused WinUI tests; nếu build native yêu cầu toolchain thì ghi rõ blocker thay vì sửa test giả.

## Task 4 — ChatView deterministic rendering

- [x] Tạo `gui-winui3.Tests/ChatViewTurnRoutingContractTests.cs` bắt lỗi nếu handler còn tìm assistant bằng `LastOrDefault`.
- [x] Sửa `gui-winui3/Views/ChatView.xaml.cs` dùng queue/presenter và association theo `TurnId`/`CorrelationId`; session change, reconnect, reset và error tăng generation rồi drop stale frame.
- [x] Giữ `ChatScrollHost` là outer scrollbar sát mép phải và activity feed hiện có; không đưa VTS/voice metadata vào UI.
- [x] Chạy focused tests và source-contract tests.

## Task 5 — Natural intent resolution

- [x] Tạo `backend-dotnet/RuBackend/Conversation/ConversationIntentResolution.cs` và `ConversationIntentResolver.cs`; thêm tests `ConversationIntentResolverTests.cs` cho greeting/ack high-confidence, correction, path/task/search/current-fact và NoMatch.
- [x] Sửa coordinator/DI để local reply chỉ bypass model/memory khi confidence cao, nhưng vẫn append history; các intent còn lại giữ full model context.
- [x] Chạy focused coordinator/intent tests và cập nhật `docs/operations/runtime-behavior.md` nếu wording contract đổi.

## Task 6 — Agent session recovery and external-directory UX

- [x] Tạo `gui-winui3/Services/AgentSessionRecovery.cs` và `AgentSessionRecoveryTests.cs` cho stale valid session retry đúng một lần rồi fail rõ ràng.
- [x] Sửa `ChatView.xaml.cs` để Agent route không rơi vào Chat, tạo/recover real session trước run, reuse approval cho cùng session, hỏi lại một lần khi folder ngoài cần quyền.
- [x] Tái sử dụng và chạy các test hiện hữu `ExternalDirectoryAccessTests`, `AgentApprovalEndpointTests`, `AgentSessionUiSourceContractTests`; không thêm workspace editor vào dropdown.

## Task 7 — Typed transient Agent terminal summary

- [x] Tạo contracts/models `backend-dotnet/RuAgent.Protocol/Harness/AgentRunSummaryContracts.cs`, `backend-dotnet/RuAgent.Harness.Core/AgentRunSummary.cs`, `backend-dotnet/RuBackend/Harness/AgentRunSummaryLivePublisher.cs`, `gui-winui3/Services/AgentRunSummaryModel.cs`.
- [x] Sửa runtime/endpoint/client boundaries để publish transient `agent.summary` với typed safe fields: status, verification_state, workspace_label, files_scanned/changed/created/deleted; không thêm durable cursor/persistence.
- [x] Thêm protocol, publisher, adapter/controller tests; reject unknown/unsafe fields và không render raw command/path.

## Task 8 — Summary rendering and lifecycle

- [x] Tạo `gui-winui3.Tests/AgentRunSummaryUiContractTests.cs` kiểm tra summary chỉ xuất hiện một lần sau terminal, success wording chỉ khi verified, cancel/error rõ ràng.
- [x] Sửa `ChatView.xaml(.cs)` append một assistant summary typed sau history reload/terminal; clear summary/activity khi session đổi, reconnect hoặc gửi turn mới.
- [x] Chạy focused UI tests và giữ scrollbar/activity layout không đổi ngoài phần summary.

## Task 9 — Current documentation

- [x] Cập nhật `README.md`, `docs/README.md`, `docs/operations/runtime-behavior.md`, `docs/operations/agent-tools.md`, `docs/operations/verification.md` cho correlation/FIFO, session recovery, transient summary và external-directory approval.
- [x] Rà `tests/test_current_documentation.py`; chỉ sửa assertion để phản ánh contract hiện hành, không biến plan/spec lịch sử thành tài liệu runtime.

## Task 10 — Full sequential verification

- [x] Chạy tuần tự các suite .NET client/core/backend/gui phù hợp, không chạy song song; sau đó `.venv\Scripts\python.exe -m pytest -q`.
- [x] Chạy `build.bat` hoặc test build script, kiểm tra `git diff --check`, source-contract tests và status dirty.
- [x] Chỉ báo “hoàn tất” khi output verification sạch; manual GUI/real folder test do người dùng tự chạy, không tự điều khiển máy.

## Self-review checklist

- [x] Mọi mục trong design spec đã có test hoặc lý do rõ ràng nếu chỉ là tài liệu.
- [x] Không còn placeholder/TODO trong code mới; tên type và JSON field nhất quán giữa C# layers.
- [x] Không stage/commit/xoá file dirty baseline ngoài scope.
- [x] Rà lại documents sau implementation và ghi rõ phần nào để dự án sandbox OS riêng.
