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
    private readonly IRunHandoffModelFacts? _modelFacts;

    /// <param name="modelFacts">
    /// The run's pinned model. Without it the handoff keeps its default size and every run
    /// reads earlier runs through the text handoff.
    /// </param>
    public HarnessModelContextBootstrapper(
        ISessionStore sessions,
        HarnessPayloadStore payloads,
        IIdGenerator ids,
        TimeProvider clock,
        IRunHandoffModelFacts? modelFacts = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _modelFacts = modelFacts;
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

        // The current conversation snapshot is also the visibility boundary:
        // rewind/pruning must not resurrect discarded runs through the handoff.
        var visibleSourceRunIds = session.Messages
            .Where(message => message.Role == "user" && message.SourceRunId is not null)
            .Select(message => message.SourceRunId!).Distinct(StringComparer.Ordinal).ToArray();
        var facts = _modelFacts is null ? null
            : await _modelFacts.ReadAsync(runId, cancellationToken).ConfigureAwait(false);
        var nativeSource = facts is null ? null
            : await ChooseNativeSourceAsync(runId, sessionId, visibleSourceRunIds, facts, cancellationToken)
                .ConfigureAwait(false);

        // A natively continued run reads its source's own prompt and reply as native entries,
        // so that turn is not repeated as dialogue.
        var turns = SessionContextTurns.Select(session.Messages, MaximumContextTurns)
            .Where(turn => nativeSource is null || turn.SourceRunId != nativeSource.Value.Value).ToArray();
        var imageCounts = await _payloads.ReadSessionImageCountsAsync(sessionId,
            turns.Where(turn => turn.SourceRunId is not null).Select(turn => turn.SourceRunId!)
                .Distinct(StringComparer.Ordinal).ToArray(), cancellationToken).ConfigureAwait(false);
        var entries = new List<HarnessBaseContextEntry>(turns.Length * 2 + 3);
        long sequence = 1;
        foreach (var turn in turns)
        {
            int liveImageCount = turn.SourceRunId is { } source && imageCounts.TryGetValue(source, out var count) ? count : 0;
            int imageCount = Math.Max(turn.ImageAttachmentCount, liveImageCount);
            if (string.IsNullOrWhiteSpace(turn.Prompt) && imageCount == 0) continue;
            var prompt = turn.Prompt;
            if (imageCount > 0)
            {
                var fact = $"[Earlier user attachment: {imageCount} image attachment(s) were received with this message. "
                    + "Image contents are not included in this historical turn. This confirms receipt, not image understanding.]";
                prompt = string.IsNullOrWhiteSpace(prompt) ? fact : prompt + "\n\n" + fact;
            }
            entries.Add(new HarnessBaseContextEntry(
                _ids.NewPayloadId(),
                $"base:turn:{sequence}:user",
                sequence++,
                ModelContextRole.User,
                prompt));
            entries.Add(new HarnessBaseContextEntry(
                _ids.NewPayloadId(),
                $"base:turn:{sequence}:assistant",
                sequence++,
                ModelContextRole.Assistant,
                turn.Response));
        }

        var handoff = await _payloads.ReadSessionRunHandoffContextAsync(runId, sessionId, visibleSourceRunIds,
                cancellationToken, SessionRunHandoff.CharacterBudget(facts?.KnownContextWindowTokens), nativeSource)
            .ConfigureAwait(false);
        if (handoff is not null)
        {
            if (handoff.RenderedHandoff.Length > 0)
                entries.Add(new HarnessBaseContextEntry(_ids.NewPayloadId(), SessionRunHandoff.ContextSourceId,
                    sequence++, ModelContextRole.User, handoff.RenderedHandoff));
            var scopeHints = SessionScopeHints.Encode(handoff.ObservedPaths);
            if (scopeHints.Length > 0)
                entries.Add(new HarnessBaseContextEntry(_ids.NewPayloadId(), SessionScopeHints.SourceId,
                    sequence++, ModelContextRole.User, scopeHints));
        }
        // The decision is recorded once: every later load reads the same source natively.
        if (nativeSource is { } continued)
            entries.Add(new HarnessBaseContextEntry(_ids.NewPayloadId(), NativeRunContinuation.MarkerSourceId,
                sequence++, ModelContextRole.User, NativeRunContinuation.EncodeMarker(continued)));

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

    /// <summary>
    /// The newest earlier root run this run continues natively (ADR 0049): same preset and
    /// pinned model, an ending that was not a context overflow, and a closed history that
    /// leaves at least half the window free. Anything else reads it through the text handoff.
    /// </summary>
    private async Task<RunId?> ChooseNativeSourceAsync(RunId runId, SessionId sessionId,
        IReadOnlyCollection<string> visibleSourceRunIds, RunHandoffModelFacts facts, CancellationToken cancellationToken)
    {
        var candidate = await _payloads.ReadNativeContinuationCandidateAsync(runId, sessionId, visibleSourceRunIds,
            cancellationToken).ConfigureAwait(false);
        if (candidate is null
            || !await _modelFacts!.SharesModelAsync(runId, candidate.RunId, cancellationToken).ConfigureAwait(false))
            return null;
        NativeRunContinuation.Slice slice;
        try
        {
            slice = await _payloads.ReadNativeContinuationSliceAsync(candidate.RunId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HarnessStoreException)
        {
            // A source this build cannot project natively (a run from before base capture,
            // say) still reaches the new run through the text handoff.
            return null;
        }
        return NativeRunContinuation.Fits(slice, facts.KnownContextWindowTokens) ? candidate.RunId : null;
    }
}
