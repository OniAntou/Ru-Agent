# GUI Polling and Reply Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent unnecessary/overlapping refresh work and per-delta full-text/layout work without changing chat, session selection, or voice behavior.

**Architecture:** Own polling admission/cancellation in a small non-XAML module. Own reply correlation/dedup/text accumulation independently of a coalesced render callback. Keep voice delivery outside the render batch and respect its current threading requirements.

**Tech Stack:** C#, WinUI 3, xUnit, existing RuAgent.Client models.

## Global Constraints

Inherit [master constraints](2026-08-31-codebase-optimization.md). Phase 2, after phase 1. No GUI launch/UI Automation as part of this plan's default tests.

---

## P1 — Lifecycle-owned single-flight refresh

**Files:**

- Modify: `gui-winui3/Views/DiagnosticsView.xaml.cs`, `gui-winui3/MainWindow.xaml.cs`, `gui-winui3/Services/ApiService.cs`.
- Create: `gui-winui3/Services/RefreshController.cs`.
- Create: `gui-winui3.Tests/RefreshControllerTests.cs`, `gui-winui3.Tests/ApiRefreshCancellationTests.cs`.
- Neighbor tests: `DiagnosticsViewContractTests.cs`, `SessionDeletionApiTests.cs`, `AgentSessionSelectionTests.cs` in `gui-winui3.Tests`.

**New interface:** `RefreshController<T>` is a pure coordinator, not a timer framework.

```csharp
public RefreshController(
    Func<CancellationToken, Task<T>> fetch,
    Action<T> apply,
    Action<Action> dispatch);
public void SetActive(bool active);
public Task<bool> RefreshAsync(); // true only when this invocation applied a current result
public ValueTask DisposeAsync();
```

The first state is inactive. Busy/inactive calls return false, not an unbounded queue. Deactivation increments a generation and cancels the active request. Reactivation cannot overlap an old request that ignores cancellation; its late result is discarded. Apply checks both generation and active state INSIDE the dispatched callback. Dispose closes admission, cancels, and observes the owned task.

Add an optional final `CancellationToken cancellationToken = default` parameter to `ApiService.GetSessionsAsync`, `GetDiagnosticStatusAsync`, `GetDiagnosticLogsAsync`, and the health fetch actually used by MainWindow if necessary. Pass it to HttpClient; expected cancellation is not logged as an API failure.

- [ ] **Step 1 — RED on actual coordinator behavior.**

Use a delayed TaskCompletionSource fetch and queued dispatch delegate; fakes replace HTTP/dispatcher only. No sleeps or actual DispatcherQueue required. Basic inactive test:

```csharp
[Fact]
public async Task Inactive_refresh_does_not_fetch()
{
    int calls = 0;
    await using var refresh = new RefreshController<int>(
        _ => { calls++; return Task.FromResult(7); },
        _ => throw new InvalidOperationException("inactive apply"),
        action => action());
    Assert.False(await refresh.RefreshAsync());
    Assert.Equal(0, calls);
}
```

Add tests for: two ticks while fetch is held (max concurrent fetch = 1); deactivate before HTTP finishes; deactivate after callback queued but before it runs; reactivate while stale fetch is held; fetch exception followed by successful retry; dispose during fetch. Assert applied data and suppressed stale work, not the fake's existence.

An absent class is only an interface/setup failure. Observe the overlapping/stale-result assertions against the baseline behavior moved into this seam before enforcing the guard.

- [ ] **Step 2 — GREEN and production wiring.**

Use one short admission lock; cancellation outside the lock; release busy state in finally. Keep timer scheduling in the view/window.

Diagnostics Initialize must not start network work while the page is hidden. Use MainWindow navigation/visibility lifecycle, not Loaded alone if the page remains loaded offscreen. On activation, refresh promptly and start its existing 3-second timer; on leaving, stop/cancel. Existing pause semantics stay: visible health updates continue while log rows are paused. Preserve cursor, dedup, 1,000-row cap, filters and export.

Sidebar sessions/health remain active for the window's lifetime, including when Diagnostics is hidden. Wrap timer AND manual refresh paths through the same coordinator; stop on window close. Do not accidentally disable sidebar polling when Chat is not selected. Observation already has an in-flight guard; leave that path unchanged.

- [ ] **Step 3 — Focused verification and counters.**

Use fake HttpMessageHandler to prove token propagation and no stale application; run GUI tests without opening WinUI. Record fixed tick count, fetched/applied counts, concurrency, hidden-page zero-fetch behavior. Preserve deletion's application envelope `status: "ok"` rule and selection object identity.

## C1 — Reply state and bounded render scheduling

**Files:**

- Modify: `gui-winui3/Services/AgentRunController.cs`, `gui-winui3/Views/ChatView.xaml.cs`.
- Read: `backend-dotnet/RuAgent.Client/AgentAssistantDeltaModel.cs`, `gui-winui3/Models/Models.cs`, `gui-winui3/Services/VoicePlaybackService.cs`.
- Create: `gui-winui3/Services/AgentReplyAccumulator.cs`.
- Create: `gui-winui3.Tests/AgentReplyAccumulatorTests.cs`, `gui-winui3.Tests/AgentReplyRenderSchedulingTests.cs`.
- Neighbor tests: `AgentSessionRecoveryTests.cs`, `VoicePlaybackSubtitleIsolationTests.cs`, `VoiceSubtitleEventPumpTests.cs`, `VoicePlaybackQueueTests.cs`.

**New interface** (pure state, separate from UI objects):

```csharp
public sealed record AgentReplySnapshot(
    string SessionId, string RunId, long Generation, string Text);
public void Begin(string sessionId, string runId);
public bool TryAppend(string runId, string modelStepId, long sequence, string text);
public AgentReplySnapshot? TakeSnapshot(); // null when there is no new text
public void Invalidate(); // discard pending text and all old identities
```

An accumulator instance belongs to one current reply. It uses a StringBuilder and typed dedup key; does not create the full Content string on each append. No new reply/token cap or visible truncation. Thread safety must cover the actual ingress path.

The controller ALREADY performs run/cancel checks and dedup inside a queued UI callback. ChatView repeats dedup. Do not add a third set. Choose one authority for delta admission and keep its state valid through replacement/cancellation; remove the redundant set only after end-to-end tests prove equivalent behavior.

- [ ] **Step 1 — Characterization and regression.**

Characterize controller callback ordering, lazy run binding, terminal completion, replacement sessions, and AutoReadReplies. Then test accumulator directly:

```csharp
[Fact]
public void Reply_dedup_preserves_unicode_and_rejects_an_old_run()
{
    var reply = new AgentReplyAccumulator();
    reply.Begin("s1", "r1");
    Assert.True(reply.TryAppend("r1", "m1", 1, "Chào "));
    Assert.False(reply.TryAppend("r1", "m1", 1, "duplicate"));
    Assert.True(reply.TryAppend("r1", "m1", 2, "anh"));
    Assert.Equal("Chào anh", reply.TakeSnapshot()!.Text);
    Assert.Null(reply.TakeSnapshot());
    reply.Begin("s2", "r2");
    Assert.False(reply.TryAppend("r1", "m1", 3, "stale"));
    Assert.Null(reply.TakeSnapshot());
}
```

Use 10,000 synthetic deltas with duplicates and multibyte text; explicitly assert text and terminal flush. A fake render scheduler holds callbacks while deltas arrive. Assert at most one outstanding render callback, no lost text, and no callback applying to a replaced session.

- [ ] **Step 2 — GREEN: coalesce render, not voice.**

Enqueue at most one render callback per pending batch; snapshot only when it runs. Terminal success/cancel/error must deterministically flush or discard according to current visible behavior; replacement invalidates old callbacks via generation.

Deliver each accepted voice delta promptly through the existing safe threading boundary. Do NOT call VoicePlaybackService from an arbitrary SSE thread without establishing its thread-safety. A held render callback must not hold accepted voice delivery. Audio Play remains before subtitle notifications.

Initially coalesce Content updates and ScrollChatToBottom/layout work; do not claim fewer total UI dispatches if voice/controller still dispatch once per delta. Remove unconditional UpdateLayout only after tests/manual acceptance cover initial layout and content resizing; do not alter user's scroll-follow behavior.

Move reply state into the module; leave unrelated preset, approval, welcome layout, recovery and styling alone. Do not create giant partial files that simply relocate unrelated view code.

- [ ] **Step 3 — Verification, build and docs.**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --nologo
dotnet build gui-winui3.Harness/RuApp.Harness.csproj -c Release --nologo
```

For every external command, inspect exit code before proceeding. Build does not authorize starting the Harness.

Measure 10,000 deltas before/after: string snapshots, layout calls, pending render callbacks and allocations. Keep voice order/accepted text exact. Synthetic delivery latency is not live first-audio acceptance. Update architecture/verification and TODO only for the behavior actually changed.

**Exit:** Hidden Diagnostics generates no refreshes; slow fetch cannot overlap; old results/deltas cannot leak; rendering is batched and voice is not gated by rendering; GUI tests/build pass.
