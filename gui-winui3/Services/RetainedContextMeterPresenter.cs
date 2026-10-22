using RuApp.Models;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed class RetainedContextMeterPresenter
{
    private const string OpenAiChatProtocol = "chat_completions";
    private const string OpenAiResponsesProtocol = "openai_responses";
    private const string AnthropicMessagesProtocol = "anthropic_messages";
    private const int MaximumPromptTokens = int.MaxValue;

    private string? _runId;
    private string? _sessionId;
    private long _boundarySequence;
    private long _usageSequence;
    private int _visibleMessageStart;
    private int? _reportedPromptTokens;
    private bool _hasKnownHistoryBoundary = true;
    private long _estimateSequence;
    private int? _preparedInputTokens;
    public ContextTokenBreakdown? ContextTokens { get; private set; }

    public void RestoreUsage(string runId, string sessionId, string? protocol, AgentModelUsage usage)
    {
        if (!Owns(runId, sessionId) || _usageSequence > 0 || _estimateSequence > 0) return;
        _reportedPromptTokens = ResolvePromptTokens(protocol, usage);
        ContextTokens = usage.ContextTokens;
    }

    public bool ObserveEstimate(string runId, string sessionId, AgentUsageEstimateModel estimate)
    {
        if (!Owns(runId, sessionId) || estimate.TransientSequence <= _estimateSequence) return false;
        _estimateSequence = estimate.TransientSequence;
        _preparedInputTokens = (int)Math.Min(int.MaxValue, estimate.EstimatedInputTokens);
        _reportedPromptTokens = null;
        ContextTokens = estimate.ContextTokens;
        _hasKnownHistoryBoundary = true;
        return true;
    }

    private void ClearBreakdown()
    {
        _preparedInputTokens = null;
        ContextTokens = null;
    }

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

        _runId = runId;
        _sessionId = sessionId;
        _boundarySequence = 0;
        _usageSequence = 0;
        _estimateSequence = 0;
        ClearBreakdown();
        _visibleMessageStart = 0;
        _reportedPromptTokens = null;
        _hasKnownHistoryBoundary = true;
    }

    public void MarkHistoryBoundaryUnknown(string runId, string sessionId, long sequence = 0)
    {
        if (!Owns(runId, sessionId))
        {
            return;
        }

        _visibleMessageStart = int.MaxValue;
        _boundarySequence = Math.Max(_boundarySequence, sequence);
        _usageSequence = Math.Max(_usageSequence, sequence);
        _reportedPromptTokens = null;
        _hasKnownHistoryBoundary = false;
        ClearBreakdown();
    }

    public bool ObserveCompaction(string runId, string sessionId, long sequence, int visibleMessageCount)
    {
        if (!Owns(runId, sessionId) || sequence <= _boundarySequence)
        {
            return false;
        }

        _boundarySequence = sequence;
        _usageSequence = sequence;
        _visibleMessageStart = Math.Max(0, visibleMessageCount);
        _reportedPromptTokens = null;
        _hasKnownHistoryBoundary = true;
        ClearBreakdown();
        return true;
    }

    public bool ObserveUsage(string runId, string sessionId, long? sequence, string? protocol, AgentModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (!Owns(runId, sessionId)
            || sequence is null
            || sequence <= _boundarySequence
            || sequence <= _usageSequence)
        {
            return false;
        }

        _reportedPromptTokens = ResolvePromptTokens(protocol, usage);
        _usageSequence = sequence.Value;
        _preparedInputTokens = null;
        ContextTokens = usage.ContextTokens;
        if (_reportedPromptTokens is not null)
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
        int reported = _reportedPromptTokens ?? 0;
        int estimate = historyEstimate + unsentEstimate;
        int used = (_reportedPromptTokens ?? _preparedInputTokens) is { } promptTokens
            ? (int)Math.Min(int.MaxValue, (long)promptTokens + unsentEstimate)
            : estimate;
        return (historyCharacters + unsentCharacters, historyImages + unsentImages, reported, estimate, used, _hasKnownHistoryBoundary);
    }

    public static int? ResolvePromptTokens(string? protocol, AgentModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (protocol is OpenAiChatProtocol or OpenAiResponsesProtocol)
        {
            return usage.InputTokens;
        }

        if (protocol == AnthropicMessagesProtocol
            && (usage.InputTokens is not null || usage.CacheReadInputTokens is not null || usage.CacheWriteInputTokens is not null))
        {
            long total = (long)(usage.InputTokens ?? 0)
                + (usage.CacheReadInputTokens ?? 0)
                + (usage.CacheWriteInputTokens ?? 0);
            return (int)Math.Min(MaximumPromptTokens, total);
        }

        return null;
    }

    private bool Owns(string runId, string sessionId) =>
        string.Equals(_runId, runId, StringComparison.Ordinal)
        && string.Equals(_sessionId, sessionId, StringComparison.Ordinal);
}
