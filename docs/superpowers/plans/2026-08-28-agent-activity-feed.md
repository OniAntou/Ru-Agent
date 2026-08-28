# Agent Activity Feed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đưa tiến trình Coding Agent an toàn vào transcript Chat theo thời gian thực, chỉ hiển thị summary được validate (thinking, explore, search, analyze, edit, terminal, status), tự ẩn khi run kết thúc và không làm thay đổi Chat mode, approval, workspace trust hay audit semantics.

**Architecture:** Giữ Core độc lập với Protocol và WinUI. Harness Core tạo activity typed và đẩy qua `IAgentActivitySink`; runtime map sang `AgentActivityWire` rồi publish transient `agent.activity` trên `HarnessLiveEventHub`; client parse frame transient riêng khỏi durable `AgentEventModel`; WinUI giữ `ObservableCollection<AgentActivityItem>` riêng trong cùng `ChatScrollHost`, không trộn vào `Messages`. Activity không persist, không đi vào history/TTS/VTS/memory/audit.

**Tech Stack:** C#/.NET 10, ASP.NET Core SSE, `System.Text.Json`, xUnit, WinUI 3/XAML, existing Ru brushes/fonts and dispatcher patterns.

---

## 0. Guardrails and baseline

- [x] Record the current baseline without touching existing user-owned edits. Run `git status --short --branch`, `git diff --check`, and the focused existing tests below; save the output in the implementation notes for comparison.
  - `dotnet test backend-dotnet/RuAgent.Client.Tests/RuAgent.Client.Tests.csproj --no-restore --nologo`
  - `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --nologo`
  - `dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --no-restore --nologo`
  - `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --nologo`
  - `.venv\Scripts\python.exe -m pytest -q tests/test_current_documentation.py`
  - Expected baseline output is a clean `git diff --check`; each test command reports `Failed: 0` (the existing dirty files must remain untouched unless a later step explicitly names them).

- [x] Before any XAML edit, run the already-loaded Impeccable UI preflight against `gui-winui3/Views/ChatView.xaml`, inspect the incumbent Ru tokens in `gui-winui3/App.xaml` and `gui-winui3/Views/ChatView.xaml`, and read `C:\Users\USER\.agents\skills\impeccable\reference\craft-floor.md` immediately before editing. Preserve the current far-right outer scrollbar and reading-column width contract; do not introduce a nested scrolling surface or a heavy card.

## 1. Protocol contract (red → green)

- [x] Add `backend-dotnet/RuBackend.Tests/Harness/AgentActivityProtocolTests.cs` with failing xUnit tests for `AgentActivityWireCodec.Parse`/`Serialize` and `AgentActivityWireValidator`. Cover: a valid thinking frame; every allowed kind/state; omission of null optional fields; basename-only `ChatView.xaml.cs` with ordered positive lines; bounded symbol/count/add/remove/duration; rejection of unknown fields, unknown kind/state, `command`, `arguments`, `argv`, `path`, `code`, `diff`, `output`, `script`, `stdout`, `stderr`, `body`, `environment`, exception fields, separators, control characters, reserved Windows names, reversed/zero/oversized ranges, negative/oversized counts and durations. Run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~AgentActivityProtocolTests`; it must initially fail to compile because the new Protocol types do not exist.

- [x] Implement `backend-dotnet/RuAgent.Protocol/Harness/AgentActivityContracts.cs` without adding a dependency from Core to Protocol. Define `AgentActivityKind` (`thinking`, `explore`, `search`, `analyze`, `edit`, `terminal`, `status`), `AgentActivityState` (`started`, `completed`, `failed`, `waiting`), immutable `AgentActivityWire` with the exact snake-case fields from the approved spec, and `AgentActivityWireCodec.Parse(string)`/`Serialize(AgentActivityWire)`. Use explicit JSON property allowlisting, reject unknown properties before deserialization, emit enums as their lowercase wire tokens, omit null optionals, and retain the existing forbidden-field policy rather than accepting free text.

- [x] Implement the validator in the same Protocol file with explicit constants: activity id `act_` plus a bounded token, filename/symbol maximum 128 characters, line values in `1..1_000_000` with `line_start <= line_end`, numeric fields in `0..1_000_000` and `duration_ms` in `0..86_400_000`, and `safe_code` using the existing lowercase token grammar. Reject `/`, `\\`, `:`, control characters, `.`/`..`, trailing dot/space and Windows device basenames (`CON`, `PRN`, `AUX`, `NUL`, `COM1..COM9`, `LPT1..LPT9`).

- [x] Re-run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~AgentActivityProtocolTests`; expect all protocol tests to pass. Run `git diff --check` and keep this step limited to the new Protocol contract and its tests.

## 2. Core projection seam (red → green)

- [x] Add `backend-dotnet/RuAgent.Harness.Core.Tests/AgentActivityProjectorTests.cs` with a fake `IAgentActivitySink` and deterministic id/time delegates. Write failing tests for model-step start/completion duration; `files.list`/`files.glob` explore count; `files.search` safe symbol plus result count only when both validate; `files.read` analyze basename and line range; `files.write`/`files.edit` edit basename/range and optional non-negative add/remove counts; terminal generic activity with no command/cwd; generic status fallback; invalid metadata omission; and a sink that throws without failing projection. Run `dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~AgentActivityProjectorTests`; it must initially fail because the seam is absent.

- [x] Add small Core files `backend-dotnet/RuAgent.Harness.Core/AgentActivity.cs`, `AgentActivityProjector.cs`, and `IAgentActivitySink.cs`. Keep the Core DTO independent of Protocol but structurally equivalent to the safe fields; expose `Publish(RunId, AgentActivity)` synchronously so the runtime adapter can publish without SSE knowledge. Add a bounded, allowlisted `ToolActivityMetadataExtractor` in the projector: inspect only known tool identifiers and explicitly typed argument/result metadata, convert paths to basename, accept line/count fields only after numeric/range validation, and never retain or return raw arguments, patch, command, output or file content.

- [x] Make projection failure-isolated. Wrap sink calls in a Core helper that catches sink/serialization exceptions, increments no durable counters, and lets the existing run continue; do not reuse `TransientFrameBuffer` for activity because raw assistant/tool deltas remain a separate channel.

- [x] Re-run the projector filter; expect all tests to pass and no durable `HarnessEvent` count/order changes. Run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~HarnessReadOnlyTracerTests` to guard the existing privacy/event contract.

## 3. Core lifecycle instrumentation (red → green)

- [x] Extend `backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunCoordinatorTests.cs` (or the closest existing coordinator test file) with a fake sink and deterministic clock. Add failing assertions that a `thinking.started` activity is published after the model-step reservation, `thinking.completed` is published after the model returns with elapsed duration, tool activities publish in proposal order, approval changes a tool row to `waiting`, receipt outcome changes it to `completed`/`failed`, cancellation never creates a success state, and the durable event sequence remains byte-for-byte equivalent apart from unrelated test IDs.

- [x] Add an optional `IAgentActivitySink? activitySink = null` parameter at the end of `AgentRunCoordinator`'s constructor in `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`, store a no-op sink when absent, and pass it to `AgentToolExecutionPipeline`. Keep all existing constructor call sites source-compatible.

- [x] Instrument `AgentRunCoordinator.RunOneStepAsync` around the existing `ModelStepStarted` commit/model call/`HandleToolStepAsync` and `HandleFinalStepAsync` paths. Publish one `thinking` activity keyed by the model step, compute `duration_ms` from `ITimeProvider`, publish `failed` with a safe code on model exceptions, and do not include model text or reasoning.

- [x] Instrument `backend-dotnet/RuAgent.Harness.Core/AgentToolExecutionPipeline.cs` using the `ToolProposal`/approved execution/receipt lifecycle already present. Create one stable activity id per proposal; publish `started` before evaluation, `waiting` for approval/reconciliation waits, and terminal `completed`/`failed`/`waiting` based on the validated receipt/state. Route all tool metadata through `AgentActivityProjector`; preserve existing approval, retry, scheduler, cancellation, and durable event behavior.

- [x] Run the focused coordinator/projector filters plus the complete Core suite sequentially: `dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --no-restore --nologo`. Expected result: `Failed: 0`, with existing run/approval/reconciliation tests unchanged.

## 4. Runtime transient transport (red → green)

- [x] Add `backend-dotnet/RuBackend.Tests/Harness/AgentActivityLivePublisherTests.cs` with a fake/real `HarnessLiveEventHub` lease. Assert a valid Core activity becomes a transient frame with kind `agent.activity`, JSON inner data accepted by `AgentActivityWireCodec`, no forbidden keys, no durable store append, and bounded hub/drop behavior. Assert a sink/validator failure is swallowed and does not throw into the coordinator.

- [x] Add `backend-dotnet/RuBackend/Harness/AgentActivityLivePublisher.cs` implementing `IAgentActivitySink`. Map Core enums/fields to `AgentActivityWire`, validate before serialization, serialize with the same snake-case/null-omitting options used by `HarnessSseWriter`, and call `_liveEvents.TryPublish(runId, "agent.activity", json)`. Do not add persistence or a new cursor.

- [x] Wire the publisher in `AgentRuntimeLoopService.cs`/`AgentRuntimeCoordinatorFactory`: keep `TransientFrameBuffer` for `assistant.delta`/`tool.output.delta`, create the activity publisher only when `_liveEvents` is available, and pass it through the new optional coordinator parameter. Leave the endpoint's connection-local `tN` handling intact; the existing outer transient payload remains `{ "kind": "agent.activity", "data": "<inner-json>" }`.

- [x] Re-run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~AgentActivityLivePublisherTests`, then the existing transient suites `--filter FullyQualifiedName~AgentSseReconnectTests|FullyQualifiedName~HarnessOutputDeltaTests` using separate commands. Expected result: all pass, transient frames never advance the durable cursor and never appear in SQLite history.

## 5. Client parser and controller bridge (red → green)

- [x] Add `backend-dotnet/RuAgent.Client.Tests/AgentActivityModelTests.cs` with failing tests for the exact outer transient wrapper emitted by `AgentV1Endpoints`: parse a valid `agent.activity` inner JSON from `data` (support the existing string form and reject an object with wrong shape), preserve `TransientSequence` separately, reject forbidden/unknown/malformed values by returning `false`/null, ignore `assistant.delta` and `tool.output.delta`, and prove no durable sequence/cursor is changed. Run `dotnet test backend-dotnet/RuAgent.Client.Tests/RuAgent.Client.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~AgentActivityModelTests` and expect the new parser to be missing.

- [x] Add `backend-dotnet/RuAgent.Client/AgentActivityModel.cs` with immutable validated fields matching the safe wire contract and `TryParse(SseFrame, out AgentActivityModel?)`. Require `frame.IsTransient`, unwrap outer `kind`/`data`, require `kind == "agent.activity"`, call the Protocol codec, and never expose raw `TransientData`. Unknown/malformed activity returns `false` so callers can ignore it without failing the run.

- [x] Extend `gui-winui3/Services/IAgentRunClient.cs` and `AgentRunClientAdapter.cs` with a separate `Func<AgentActivityModel, Task> onActivity` callback. During SSE iteration, deliver durable frames to the existing `AgentEventModel` callback, deliver only parsed `agent.activity` frames to `onActivity`, and drop all other transient frames before they reach `AgentRunController.Events`. Preserve dispatcher marshalling and cancellation behavior.

- [x] Extend `gui-winui3/Services/AgentRunController.cs` with `OnActivityAsync` plumbing/event (or an explicit activity callback supplied by `ChatView`). Keep `Events`/`AgentAuditLog` durable-only; update status from durable safe summaries only. Clear activity delivery on a new run, terminal refresh, cancellation and dispose, and ensure an activity callback exception cannot fail the SSE/run task.

- [x] Update all fake `IAgentRunClient` implementations in `gui-winui3.Tests/AgentRunControllerTests.cs` and adapter tests to the new callback signature. Add tests that a valid activity reaches the callback, raw transient output never enters `Events`, durable audit writes remain unchanged, and malformed activities are ignored. Run the focused Client and GUI controller filters; expect `Failed: 0`.

## 6. WinUI activity model and transcript feed (red → green)

- [x] Add `gui-winui3.Tests/AgentActivityUiSourceContractTests.cs` first. Read `Views/ChatView.xaml`, `Views/ChatView.xaml.cs`, and the new model source; fail on absence of a separate `AgentActivities` collection, `AgentActivityFeed` inside `ChatScrollHost`, a temporary Agent user bubble, state visuals for spinner/check/error/waiting, basename/line-range-only rendering, and lifecycle clears for terminal/cancel/reset/disconnect. Assert the XAML has no nested vertical `ScrollViewer`, no command/args/path/diff/output binding, and retains `ChatContentGrid`/outer scrollbar tokens.

- [x] Add `gui-winui3/Models/AgentActivityItem.cs` as a UI-only `INotifyPropertyChanged` model. Store the activity id, kind, state and safe metadata; expose localized bounded display properties (`StartedText`, `CompletedText`, `WaitingText`, `FailedText`, `MetadataText`, `IsRunning`, `IsCompleted`, `IsFailed`, `IsWaiting`) and map missing metadata to generic Vietnamese labels. Do not store raw JSON, command, path, diff, stdout/stderr, model text or reasoning.

- [x] Update `gui-winui3/Views/ChatView.xaml` under the existing `ChatScrollHost`/`ChatContentGrid`: wrap `ChatListView` and a collapsed `ItemsControl x:Name="AgentActivityFeed"` in one vertical transcript panel; bind/set it to `AgentActivities`; use the existing Ru font/brush resources, a light single-row layout with icon/state glyph and text, `AutomationProperties.LiveSetting="Polite"`, and no nested scroll container. Keep the activity panel in the same centered width so the outer scrollbar stays flush to the chat viewport's right edge.

- [x] Update `gui-winui3/Views/ChatView.xaml.cs`: create `ObservableCollection<AgentActivityItem> AgentActivities` and an id lookup, attach the controller activity callback on the UI dispatcher, add the user prompt to `Messages` before `CreateAndRunAsync`, show the feed only once the run starts, merge start/completion updates by activity id, scroll the outer `ChatScrollHost` after each row, and keep the existing inline stop/trust/approval/continue controls above the composer. Clear/hide the feed on successful terminal before `ReloadSessionHistoryAsync`, cancel, failure, reset, session replacement, disconnect and dispose; preserve the idle welcome hero and existing `ApplyInputWidth` behavior.

- [x] Add a bounded dropped-frame/status row path using only the localized safe text `Một số bước tạm thời đã bị bỏ qua`; add generic `Đang làm…` fallback when metadata is missing. Verify the feed never writes to `Messages`, `AgentAuditLog`, history, TTS or VTS. Run `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~AgentActivityUiSourceContractTests`, then the full GUI suite sequentially.

## 7. Documentation and full verification

- [x] Update only current documentation: `README.md`, `docs/README.md`, `docs/operations/agent-tools.md`, `docs/operations/runtime-behavior.md`, and `docs/operations/verification.md`. State that the feed is transient, safe-summary-only, basename/line-range bounded, separate from the durable audit log, auto-cleared on terminal/cancel/disconnect, and does not expose commands, arguments, paths, diffs, output or chain-of-thought. Do not rewrite historical specs/plans or the separate OS-enforced sandbox note.

- [x] Extend `tests/test_current_documentation.py` with assertions for the new safety/lifecycle wording and run `.venv\Scripts\python.exe -m pytest -q tests/test_current_documentation.py`; expected `0 failed`.

- [x] Run all required gates sequentially from `D:\LLM`: `dotnet test backend-dotnet/RuAgent.Client.Tests/RuAgent.Client.Tests.csproj --no-restore --nologo`, `dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --no-restore --nologo`, `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --nologo`, `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --nologo`, then `.venv\Scripts\python.exe -m pytest -q`. Run `git diff --check` last; every command must report `Failed: 0`/`0 failed` and no whitespace errors.

- [ ] Perform manual acceptance against a running local backend: (1) Standard Agent list/search/read shows live explore/search/analyze rows with counts, basename and line range; (2) edit shows bounded basename and optional `+N −M`, never patch/content; (3) terminal shows only generic terminal activity; (4) approval/trust wait preserves rows while the existing card remains actionable; (5) complete/cancel/disconnect hides the feed and successful history contains only the final answer. Record any inability to test real desktop/audio/VTube acceptance separately; those are not proven by deterministic suites.

- [x] Before any commit, inspect `git diff --stat`, `git diff --check`, and `git status --short`; stage only files created/modified by this feature, never the pre-existing dirty files listed in the baseline. Do not commit or push unless the user explicitly authorizes integration after review.

## Self-review checklist

- [x] Every spec decision is represented: safe summaries, transient `agent.activity`, no raw payload/CoT, basename+line range, realtime spinner/check/error, thought duration only, transcript placement, auto-hide/clear lifecycle, separate collection, unchanged controls/audit, and no OS sandbox scope.
- [x] Core does not reference Protocol or WinUI; runtime is the only Core→wire adapter; client parser is separate from durable `AgentEventModel`; UI does not bind raw transient data.
- [x] Tests are written before each implementation slice, each command names an exact project/filter, no task contains unfinished markers or vague implementation gaps, and all existing dirty work is preserved.
