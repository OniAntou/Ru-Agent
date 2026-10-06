using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Sessions;

namespace RuBackend.Harness;

public enum ContextBootstrapOutcome
{
    Captured,
    AlreadyCaptured,
    SessionNotFound,
}

public interface IHarnessModelContextBootstrapper
{
    Task<ContextBootstrapOutcome> CaptureAsync(
        RunId runId,
        SessionId sessionId,
        CancellationToken cancellationToken);
}

public sealed class HarnessModelContextBootstrapper : IHarnessModelContextBootstrapper
{
    private const int MaximumContextTurns = 32;

    private readonly ISessionStore _sessions;
    private readonly HarnessPayloadStore _payloads;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _clock;

    public HarnessModelContextBootstrapper(
        ISessionStore sessions,
        HarnessPayloadStore payloads,
        IIdGenerator ids,
        TimeProvider clock)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ContextBootstrapOutcome> CaptureAsync(
        RunId runId,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        _ = sessionId.Value;
        var session = await _sessions.GetAsync(sessionId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return ContextBootstrapOutcome.SessionNotFound;
        }

        var turns = SessionContextTurns.Select(session.Messages, MaximumContextTurns);
        var entries = new List<HarnessBaseContextEntry>(turns.Count * 2);
        long sequence = 1;
        foreach (var turn in turns)
        {
            entries.Add(new HarnessBaseContextEntry(
                _ids.NewPayloadId(),
                $"base:turn:{sequence}:user",
                sequence++,
                ModelContextRole.User,
                turn.Prompt));
            entries.Add(new HarnessBaseContextEntry(
                _ids.NewPayloadId(),
                $"base:turn:{sequence}:assistant",
                sequence++,
                ModelContextRole.Assistant,
                turn.Response));
        }

        var outcome = await _payloads.CaptureBaseContextAsync(
                runId,
                entries,
                _clock.GetUtcNow().ToUniversalTime(),
                cancellationToken)
            .ConfigureAwait(false);
        return outcome == BaseContextCaptureOutcome.Captured
            ? ContextBootstrapOutcome.Captured
            : ContextBootstrapOutcome.AlreadyCaptured;
    }
}
