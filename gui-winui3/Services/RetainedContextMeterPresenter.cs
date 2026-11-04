using RuApp.Models;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed class RetainedContextMeterPresenter
{
    private string? _runId;
    private string? _sessionId;
    private string? _conversationId;
    private int _visibleMessageStart;
    private bool _hasKnownHistoryBoundary = true;
    private readonly RequestContextReading _reading = new();
    public ContextTokenBreakdown? ContextTokens => _reading.Breakdown;

    /// <summary>Whether the provider reported real prompt usage for this scope.</summary>
    public bool HasReportedUsage => _reading.HasReportedUsage;

    public void RestoreUsage(string runId, string sessionId, string? protocol, AgentModelUsage usage)
    {
        if (!Owns(runId, sessionId)) return;
        _reading.Restore(ResolvePromptTokens(protocol, usage), usage.ContextTokens);
    }

    public bool ObserveEstimate(string runId, string sessionId, AgentUsageEstimateModel estimate)
    {
        if (!Owns(runId, sessionId) || !_reading.ObserveEstimate(estimate)) return false;
        _hasKnownHistoryBoundary = true;
        return true;
    }

    public bool ObserveModelStep(string runId, string sessionId, long sequence, string? modelStepId) =>
        Owns(runId, sessionId) && _reading.BeginStep(modelStepId, sequence);

    public void ScopeToRun(AgentRunModel? run, string? activeSessionId)
    {
        bool isCurrent = run is not null
            && string.Equals(run.SessionId, activeSessionId, StringComparison.Ordinal);
        var runId = isCurrent ? run!.RunId : null;
        var sessionId = isCurrent ? run!.SessionId : null;
        if (string.Equals(_runId, runId, StringComparison.Ordinal)
            && string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        // A new prompt in the same conversation starts a new run whose request still
        // carries the whole history, so the last reading is the right floor until the
        // new request's estimate or usage arrives. Clearing it dropped the meter from
        // the real context (hundreds of thousands of tokens) to the size of the visible
        // messages. The TUI tracker already keeps it the same way.
        // The conversation is the session on screen, not the run: between two runs no
        // run is current, and that gap must not erase the reading either.
        bool sameConversation = activeSessionId is not null
            && string.Equals(_conversationId, activeSessionId, StringComparison.Ordinal);
        _conversationId = activeSessionId;
        _runId = runId;
        _sessionId = sessionId;
        _reading.Reset(preserveReading: sameConversation);
        if (!sameConversation)
        {
            _visibleMessageStart = 0;
            _hasKnownHistoryBoundary = true;
        }
    }

    public void MarkHistoryBoundaryUnknown(string runId, string sessionId, long sequence = 0)
    {
        if (!Owns(runId, sessionId))
        {
            return;
        }

        _visibleMessageStart = int.MaxValue;
        _reading.Invalidate(sequence);
        _hasKnownHistoryBoundary = false;
    }

    public bool ObserveCompaction(string runId, string sessionId, long sequence, int visibleMessageCount)
    {
        if (!Owns(runId, sessionId) || !_reading.Compact(sequence))
        {
            return false;
        }

        _visibleMessageStart = Math.Max(0, visibleMessageCount);
        _hasKnownHistoryBoundary = true;
        return true;
    }

    public bool ObserveUsage(string runId, string sessionId, long? sequence, string? protocol, AgentModelUsage usage,
        string? modelStepId = null)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (!Owns(runId, sessionId)
            || sequence is null
            || !_reading.ObserveUsage(sequence.Value, modelStepId, ResolvePromptTokens(protocol, usage), usage.ContextTokens))
        {
            return false;
        }

        if (_reading.Tokens is not null)
        {
            _hasKnownHistoryBoundary = true;
        }
        return true;
    }

    public (int Characters, int Images, int ReportedPromptTokens, int EstimatedTokens, int UsedTokens, bool HasKnownHistoryBoundary) Measure(
        IReadOnlyList<(string? Content, int ImageCount, int TextAttachmentCharacters)> messages,
        string? draftText,
        int pendingImages,
        int pendingTextAttachmentCharacters = 0)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int start = _hasKnownHistoryBoundary
            ? Math.Clamp(_visibleMessageStart, 0, messages.Count)
            : messages.Count;
        int historyCharacters = 0;
        int historyImages = 0;
        for (int index = start; index < messages.Count; index++)
        {
            var message = messages[index];
            historyCharacters += message.Content?.Length ?? 0;
            historyCharacters += Math.Max(0, message.TextAttachmentCharacters);
            historyImages += Math.Max(0, message.ImageCount);
        }

        int unsentCharacters = (string.IsNullOrEmpty(draftText) ? 0 : draftText.Length)
            + Math.Max(0, pendingTextAttachmentCharacters);
        int unsentImages = Math.Max(0, pendingImages);
        int historyEstimate = ModelContextSpecs.EstimateTokens(historyCharacters, historyImages);
        int unsentEstimate = ModelContextSpecs.EstimateTokens(unsentCharacters, unsentImages);
        int reported = _reading.HasReportedUsage ? (int)Math.Min(int.MaxValue, _reading.Tokens ?? 0) : 0;
        int estimate = historyEstimate + unsentEstimate;
        int used = _reading.Tokens is { } promptTokens
            ? (int)Math.Min(int.MaxValue, Math.Min(int.MaxValue, promptTokens) + unsentEstimate)
            : estimate;
        return (historyCharacters + unsentCharacters, historyImages + unsentImages, reported, estimate, used, _hasKnownHistoryBoundary);
    }

    public static int? ResolvePromptTokens(string? protocol, AgentModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return ProviderPromptTokens.Resolve(protocol, usage.InputTokens, usage.CacheReadInputTokens,
            usage.CacheWriteInputTokens, usage.InputTokensIncludeCache) is { } tokens ? (int)Math.Min(int.MaxValue, tokens) : null;
    }

    private bool Owns(string runId, string sessionId) =>
        string.Equals(_runId, runId, StringComparison.Ordinal)
        && string.Equals(_sessionId, sessionId, StringComparison.Ordinal);
}
