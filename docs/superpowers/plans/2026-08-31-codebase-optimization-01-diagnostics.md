# Diagnostic Writer Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the real Host diagnostic writer without disposing synchronization objects still in use, while keeping bounded, nonblocking diagnostics.

**Architecture:** Keep the bounded queue and event policy; isolate only append I/O behind a real sink adapter. One writer-owned shutdown operation closes admission, drains within a deadline, cancels outstanding work, and owns final resource cleanup. A wait timeout must not masquerade as completed shutdown.

**Tech Stack:** .NET 10, C#, xUnit, ASP.NET Core DI.

## Global Constraints

> Before executing, read the master plan's **Resume gate after concurrent implementation**.
> Several planned modules already exist in `e6fa40c`; inspect current code and
> verify missing acceptance evidence rather than recreating or replacing them.

Inherit all constraints from [the master plan](2026-08-31-codebase-optimization.md). This is rollout phase 1. Do not alter GUI logging or Python logging merely because they have similar names.

---

## L1 — Reproduce and fix Host writer lifetime

**Existing files:**

- Modify: `backend-dotnet/RuBackend/Diagnostics/StructuredDiagnosticLogWriter.cs`.
- Read/retain: `BoundedDiagnosticQueue.cs`, `DiagnosticEmitter.cs`, `DiagnosticEventPolicy.cs` in the same directory.
- Modify if needed for owned shutdown: `backend-dotnet/RuBackend/Program.cs`.
- Test: `backend-dotnet/RuBackend.Tests/Diagnostics/DiagnosticLogWriterTests.cs`.
- Neighbor tests: `DiagnosticEmitterTests.cs`, `DiagnosticEndpointsTests.cs`, `StructuredDiagnosticLoggerProviderTests.cs` in that test directory.
- Add focused production files: `Diagnostics/IDiagnosticLogSink.cs`, `Diagnostics/JsonlDiagnosticLogSink.cs`.
- Add focused test files: `Diagnostics/DiagnosticWriterShutdownTests.cs`, `Fakes/ControlledDiagnosticLogSink.cs`.
- Docs: `TODO.md`, `docs/operations/verification.md`, `docs/adr/0006-structured-logging-and-activity-boundaries.md`.

**Interfaces:**

Existing constructors and `TryEnqueue(DiagnosticEvent)`, `FlushAsync(TimeSpan)`, `Queue.GetSnapshot()`, `Dispose()` must remain source-compatible. New sink and shutdown boundary:

```csharp
public interface IDiagnosticLogSink
{
    ValueTask AppendAsync(
        string filePath, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);
}
public enum DiagnosticShutdownOutcome { Drained, Cancelled, Incomplete }
// New members on StructuredDiagnosticLogWriter:
public Task<DiagnosticShutdownOutcome> StopAsync(
    TimeSpan budget, CancellationToken cancellationToken = default);
public Task Completion { get; }
```

Append an optional `IDiagnosticLogSink? sink = null` constructor argument; default to the file adapter. Keep serializer, grouping, retry ordering, and queue policy in their existing owner; the adapter owns opening/writing/flushing/disposing a single FileStream. `Completion` means all writer-owned writes/flush users have ended AND gates/CTS are safely released, not simply that a wait expired.

**Required shutdown behavior:**

- First stop atomically closes enqueue/explicit-flush admission; repeated callers join the same shutdown.
- Drain uses a finite budget (default Dispose budget remains 2 seconds), including sink I/O and gate acquisition. Never use an uncancellable final drain.
- Deadline expiry requests cancellation of all admitted work, including explicit FlushAsync.
- Return Drained only when accepted work is persisted and ownership is quiescent. Return Cancelled if quiescent but deadline/cancellation prevented a full drain; preserve existing loss/retry counters truthfully.
- Return Incomplete when an uncooperative sink/task has not stopped by the caller's budget. Keep gates/CTS owned until it exits; a single observed completion path disposes them later. Do not report no handles/tasks in this outcome.
- `Dispose()` is idempotent and bounded; it must not unconditionally free gates after the wait. The Host must observe/log a bounded shutdown outcome without recursive diagnostic emission.
- `FlushAsync` after stop is a safe no-op; a flush admitted before stop is included in ownership tracking.
- Host DI disposal, explicit StopAsync, concurrent Dispose, and the background writer must not create competing final-drain loops.
- If this contract cannot be implemented without an unbounded sync wait or silent event-loss change, stop and explain; do not just increase 2 seconds.

- [ ] **Step 1 — Baseline and I/O seam characterization.**

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~Diagnostics"
```

Extract AppendAsync without changing behavior. Test the default adapter using real temporary JSONL files; preserve FileShare and serialization. Keep the existing endpoint fixture's discarding emitter until a separate real-Host test proves lifecycle.

- [ ] **Step 2 — Controlled reproducer and RED.**

Implement a sink fake in the test assembly with `TaskCompletionSource` signals `Entered`, `Release`, and `Exited`, using `RunContinuationsAsynchronously`. It must optionally honor cancellation or deliberately ignore it; always release it in test `finally`.

With one accepted event held inside AppendAsync, invoke the current Dispose and keep the sink held beyond its 2-second wait. Then release it and observe the actual writer task/gate failure, not a mocked Dispose. If observing an existing private task is necessary for this temporary reproduction, keep reflection in test utilities and replace it with the owned Completion boundary in final tests.

Record the failing assertion/exception before changing shutdown. After the seam exists, test these exact cases:

| Input/race | Expected observable result |
| --- | --- |
| Normal event, cooperative file sink | Drained; one line; immediate exclusive reopen after Completion. |
| Sink stalls and honors cancellation | Finite return; Cancelled; Completion eventually finishes; no disposed-gate exception. |
| Sink ignores cancellation | Incomplete by budget; no premature cleanup; release fixture, then Completion finishes. |
| Flush in flight while Dispose starts | One owner; no ObjectDisposedException, deadlock, or duplicate line. |
| Multiple Dispose/StopAsync callers | Same terminal cleanup; no double release. |
| Event/Flush after admission closes | TryEnqueue false; Flush safe no-op; no further append. |
| Sink write fails | Bounded retry/failure accounting; no infinite final drain. |

Final regression using the new lifecycle contract and the existing event type:

```csharp
[Fact]
public async Task Stop_drains_and_releases_the_daily_file()
{
    var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        using var writer = new StructuredDiagnosticLogWriter(root);
        Assert.True(writer.TryEnqueue(new DiagnosticEvent(
            "e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));
        Assert.Equal(DiagnosticShutdownOutcome.Drained,
            await writer.StopAsync(TimeSpan.FromSeconds(2)));
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(writer.TryEnqueue(new DiagnosticEvent(
            "e2", 1720000001, "2024-07-03T09:46:41Z", "info", "host", "host_startup")));
        var file = Assert.Single(Directory.GetFiles(root, "aspnetcore-*.jsonl"));
        Assert.Single(await File.ReadAllLinesAsync(file));
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
    }
    finally { Directory.Delete(root, recursive: true); }
}
```

This example is one normal-path test, not a substitute for the held-sink reproducer.

- [ ] **Step 3 — GREEN and Host integration.**

Implement the admission/shutdown state once, using a shared task under a short lock, not a lock around I/O. Use monotonic deadline accounting, cancellation-aware waits, and one completion cleanup path. Include admitted FlushAsync tasks in quiescence; background completion alone is insufficient.

Have the Host's owned shutdown await/observe StopAsync before DI resource disposal, using an existing appropriate lifetime hook or one small `DiagnosticWriterLifetime` IHostedService. Add a real writer Host teardown test using an isolated data root, no provider/device; do not replace the writer with a discard sink in that regression.

Run the Diagnostics filter plus the affected Host business/teardown tests. Remove neither production logging nor its dedicated tests to achieve GREEN.

- [ ] **Step 4 — Evidence and docs.**

Record normal/timeout/late-completion outcomes, enqueue behavior and bounded queue counters. Only close the diagnostic TODO after the real writer regression and Host teardown pass. Explain timeout vs completed ownership in verification docs. No commit/push.

**Exit:** RED/GREEN plus real-file exclusivity proof, correct incomplete outcome, no unowned task/gate cleanup, unchanged public diagnostic payload/privacy policy.
