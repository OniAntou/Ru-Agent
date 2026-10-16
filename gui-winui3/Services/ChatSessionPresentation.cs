namespace RuApp.Services;

internal readonly record struct ChatSessionOpen(long Generation, CancellationToken Cancellation);
internal readonly record struct ChatTurnCompletion(long SessionGeneration, long TurnGeneration);

/// <summary>
/// Owns the active conversation's asynchronous work. Called on the presentation
/// context, including after awaits; WinUI rendering stays with its caller.
/// </summary>
internal sealed class ChatSessionPresentation : IDisposable
{
    private readonly Func<CancellationToken, Task> _waitForRefresh;
    private CancellationTokenSource? _openCancellation;
    private CancellationTokenSource? _monitorCancellation;
    private long _openRequest;
    private long _turnGeneration;
    private bool _completionStarted;
    private bool _disposed;

    internal ChatSessionPresentation(Func<CancellationToken, Task>? waitForRefresh = null)
    {
        _waitForRefresh = waitForRefresh ?? (token => Task.Delay(UiTiming.AgentRetryDelay, token));
    }

    internal string SessionId { get; private set; } = "default";
    internal long Generation { get; private set; }

    internal async Task<ChatSessionOpen?> OpenAsync(
        string sessionId,
        Func<bool> canRetainDraft,
        Func<Task> detachOutgoingRun,
        Func<bool> retainDraft)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = ++_openRequest;
        if (!canRetainDraft()) return null;

        // The latest click owns the switch, irrespective of detach completion order.
        // Retain the outgoing draft only after its follow is detached and still owned.
        await detachOutgoingRun();
        if (_disposed || request != _openRequest || !retainDraft()) return null;

        Activate(sessionId);
        _openCancellation = new CancellationTokenSource();
        return new ChatSessionOpen(Generation, _openCancellation.Token);
    }

    internal void Activate(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Bootstrap/recovery may replace the outgoing session while a user's
        // click waits for detach. Only another open request supersedes that click.
        SessionId = sessionId;
        Generation++;
        BeginTurn();
        StopMonitoring();
        var previous = _openCancellation;
        _openCancellation = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    internal bool IsCurrent(long generation) => !_disposed && generation == Generation;

    internal void BeginTurn()
    {
        _turnGeneration++;
        _completionStarted = false;
    }

    internal ChatTurnCompletion? TryBeginCompletion()
    {
        if (_disposed || _completionStarted) return null;
        _completionStarted = true;
        return new ChatTurnCompletion(Generation, _turnGeneration);
    }

    internal bool IsCurrent(ChatTurnCompletion completion) =>
        IsCurrent(completion.SessionGeneration) && completion.TurnGeneration == _turnGeneration;

    internal Task StartMonitoring(
        AgentRunController controller,
        Func<CancellationToken, Task> completeTurn,
        Action runChanged,
        Action<Exception> reportFailure)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopMonitoring();
        if (controller.CurrentRun is not { } run || run.SessionId != SessionId)
            return Task.CompletedTask;

        var cancellation = new CancellationTokenSource();
        _monitorCancellation = cancellation;
        return MonitorAsync(controller, run.RunId, Generation, _turnGeneration, cancellation,
            completeTurn, runChanged, reportFailure);
    }

    internal void StopMonitoring()
    {
        var previous = _monitorCancellation;
        _monitorCancellation = null;
        // The loop disposes its own source after the in-flight refresh finishes.
        previous?.Cancel();
    }

    private async Task MonitorAsync(
        AgentRunController controller,
        string runId,
        long generation,
        long turnGeneration,
        CancellationTokenSource cancellation,
        Func<CancellationToken, Task> completeTurn,
        Action runChanged,
        Action<Exception> reportFailure)
    {
        var token = cancellation.Token;
        bool OwnsRun() => IsCurrent(generation) && turnGeneration == _turnGeneration && !token.IsCancellationRequested
            && ReferenceEquals(_monitorCancellation, cancellation)
            && controller.CurrentRun is { } run && run.RunId == runId && run.SessionId == SessionId;
        try
        {
            while (OwnsRun())
            {
                await _waitForRefresh(token);
                if (!OwnsRun()) return;
                if (IsTerminalStatus(controller.CurrentRun?.Status))
                {
                    await completeTurn(token);
                    return;
                }

                await controller.RefreshAsync(token);
                if (!OwnsRun()) return;
                runChanged();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // A superseded refresh must not report into the new conversation.
            if (OwnsRun()) reportFailure(exception);
        }
        finally
        {
            if (ReferenceEquals(_monitorCancellation, cancellation)) _monitorCancellation = null;
            cancellation.Dispose();
        }
    }

    internal static bool IsTerminalStatus(string? status) => status is
        "completed" or "failed" or "cancelled" or "interrupted";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _openRequest++;
        StopMonitoring();
        _openCancellation?.Cancel();
        _openCancellation?.Dispose();
        _openCancellation = null;
    }
}
