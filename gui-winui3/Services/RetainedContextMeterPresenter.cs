using RuApp.Models;

namespace RuApp.Services;

public sealed class RetainedContextMeterPresenter
{
    private const string OpenAiChatProtocol = "chat_completions";
    private const string OpenAiResponsesProtocol = "openai_responses";
    private const string AnthropicMessagesProtocol = "anthropic_messages";

    private string? _runId;
    private string? _sessionId;
    private long _boundarySequence;
    private long _usageSequence;
    private int _visibleMessageStart;
    private int? _reportedPromptTokens;

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
        _visibleMessageStart = 0;
        _reportedPromptTokens = null;
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
        return true;
    }

    public (int Characters, int Images, int ReportedPromptTokens, int EstimatedTokens, int UsedTokens) Measure(
        IReadOnlyList<(string? Content, int ImageCount, int TextAttachmentCharacters)> messages,
        string? draftText,
        int pendingImages,
        int pendingTextAttachmentCharacters = 0)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int start = Math.Clamp(_visibleMessageStart, 0, messages.Count);
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
        int used = _reportedPromptTokens is { } promptTokens
            ? (int)Math.Min(int.MaxValue, (long)promptTokens + unsentEstimate)
            : estimate;
        return (historyCharacters + unsentCharacters, historyImages + unsentImages, reported, estimate, used);
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
            return (usage.InputTokens ?? 0)
                + (usage.CacheReadInputTokens ?? 0)
                + (usage.CacheWriteInputTokens ?? 0);
        }

        return null;
    }

    private bool Owns(string runId, string sessionId) =>
        string.Equals(_runId, runId, StringComparison.Ordinal)
        && string.Equals(_sessionId, sessionId, StringComparison.Ordinal);
}
