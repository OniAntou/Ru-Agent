---
status: pending
---

# Plan: Xoá fork session + chuyển Ru.md → AGENTS.md

Baseline `54f5f8a3`. Quyết định: fork bỏ mọi entry point + write-path, GIỮ field metadata đọc-only (legacy display + `/tree`); instructions đổi `Ru.md` → `AGENTS.md` (root), không fallback, giữ `.ru/AGENTS.md` + `.ru/CONTEXT.md`.

**Guards chốt:** route 42→41 tại 4 hardcode (gồm `scripts/check_aspnetcore_route_matrix.py`); reserved 21 giữ nguyên (fork có winui_callers); test-count refresh ở wrap (xoá 4 xUnit + adapt).

## T1 — Backend fork removal

- `BusinessEndpoints.cs`: xoá route `:30`, `ForkSessionRequest :437-443`, `ForkSessionAsync :448-472` (gồm LogFailure).
- `SessionDocument.cs`: xoá `ISessionStore.ForkAsync :77-81`. GIỮ `ParentSessionId/ForkedFromMessageId` + list projection `:238-239` + đọc JSON.
- `SessionStore.cs`: xoá `ForkAsync :267-324` (giữ helpers).
- `SessionApiClient.cs`: xoá `ForkAsync :112-139`.
- `public-route-contract.json`: xoá entry fork.
- Guards: `scripts/check_aspnetcore_route_matrix.py` (42→41); `tests/test_aspnetcore_route_matrix.py` (:32 bỏ fork khỏi EXPECTED; :66,:115 →41); `tests/test_harness_routes.py` (:25-26 →41).
- Docs count 42→41: `README.md :15,:89` (41 + 20 non-Agent), `CONTEXT.md:32`, `AGENTS.md:44`, `CONTRIBUTING.md:50`, `docs/architecture.md:263,292,302`, `docs/aspnetcore-route-matrix.md:5,8` ("session rewind" thay "session fork/rewind"), `docs/operations/verification.md:3,4,8`.
- Tests: `SessionBranchingTests.cs` xoá 2 fork tests (giữ rewind :46-73,:83-89); `BusinessEndpointTests.cs:528-551` bỏ leg fork, rename; `AgentApprovalEndpointTests.cs:332-403` bỏ leg fork, giữ leg new-session, rename.
- Commit: `refactor(backend): remove the session fork endpoint and store write path`

## T2 — CLI + RPC + SDK fork removal

- Parser: `AgentCliParser.cs :24,:138-142`; `AgentCliParser.Sessions.cs :19,25,29,:134-189`.
- Commands: `AgentCommands.Sessions.cs :232-256`; `AgentCommands.Chat.cs :856-881` (slash `/fork`); `Program.cs :90,:159`; `TuiSurfaces.cs:27`; `InteractiveLineReader.cs:18`.
- RPC: `RpcDispatcher.cs :38,:116,:196-203`; `docs/operations/rpc.md :56,:81`; tests `RpcDispatchSurfaceTests.cs` xoá :65-81,:83-92, pin `:472`; `TuiInteractiveTests.cs :390-396,:409-412,:428`.
- SDK: `sdk/ru-agent/src/client.ts :65-66` (breaking).
- Wording: `RuAgent.Cli/README.md :10,28,52`; `agent-tools.md:10,152`; `runtime-behavior.md:294`; `architecture.md:293`; `shared-workspace-coordination.md:15`; `docs/plans/tui-image-attachments.md:17,18,67`.
- Commit: `refactor(cli,sdk): remove the session fork command surface`

## T3 — GUI fork removal

- `MainWindow.xaml :86-96`; `MainWindow.SessionActions.cs :22-43`; `MainWindow.xaml.cs :252,:433-445,:827`; `ChatView.xaml :78-84,:401-407`; `ChatView.SessionHistory.cs :1111-1135`; `ChatView.xaml.cs :105`; `ApiService.Sessions.cs :143-167`.
- Giữ `SessionModels` read-fields + `SidebarRowFingerprint` (legacy display).
- Verify GUI Release build + gui tests.
- Commit: `refactor(gui): remove the fork menu actions`

## T4 — AGENTS.md switch

- `RuInstructionLoader.cs :43` → `("root:AGENTS.md","AGENTS.md","AGENTS.md")` + comment `:31`.
- Tests pins: `RuInstructionLoaderTests.cs`; `RuPromptContextProviderTests.cs :20,52,55`; Harness.Core literals (`SystemPromptContractTests`, `SystemPromptComposerTests`, `AgentPromptReceiptTests`) → `root:AGENTS.md`.
- Fixtures: cập nhật literal `root:Ru.md` trong `tests/fixtures/*.json` khi an toàn (không auto-compare hash); nếu rủi ro → note.
- Docs: `CONTEXT.md:162-167`; `architecture.md:204,233`; `agent-tools.md:313`; `runtime-behavior.md:105`.
- CHANGELOG: Breaking — root instruction file giờ là `AGENTS.md`; `.ru/AGENTS.md` + `.ru/CONTEXT.md` giữ nguyên.
- Commit: `feat(harness): load AGENTS.md as the root instruction file`

## T5 — Wrap

Full matrix + pytest + GUI Release + smoke; refresh `verification.md` + README counts theo số thật; CHANGELOG entry fork removal; record+archive plan files; push dev.
