using RuAgent.Protocol.Harness;

namespace RuApp.Models;

/// <summary>
/// A thinking row's whole reasoning. The row's own detail is an excerpt bounded by the
/// activity contract; when the Host had to cut it, the row names the stored reasoning and
/// the opened block reads it on demand, then shows it a slice at a time so one long
/// deliberation cannot stall the transcript's layout.
/// </summary>
public sealed partial class AgentActivityItem
{
    /// <summary>How much of a loaded reasoning the open block shows before "Show more".</summary>
    internal const int ThinkingSliceCharacters = 20_000;

    private string? _reasoningPayloadId;
    private string? _fullThinking;
    private int _shownThinkingCharacters = ThinkingSliceCharacters;
    private ThinkingLoad _thinkingLoad;

    private enum ThinkingLoad { Idle, Loading, Failed }

    /// <summary>The stored reasoning a cut excerpt names; null when the excerpt is the whole of it.</summary>
    internal string? ReasoningPayloadId
    {
        get => _reasoningPayloadId;
        private set
        {
            if (SetProperty(ref _reasoningPayloadId, value)) NotifyThinkingText();
        }
    }

    /// <summary>The run whose payloads hold this row's reasoning; set by whoever built the feed.</summary>
    internal string? OwnerRunId { get; set; }

    /// <summary>
    /// The durable sequence a replayed row was rebuilt at, so rows from separately read
    /// replay pages merge back into chronological order. Null for a live row.
    /// </summary>
    internal long? ReplaySequence { get; set; }

    /// <summary>Whether opening the block should read the whole reasoning now.</summary>
    internal bool NeedsFullThinking => IsThinking && ReasoningPayloadId is not null
        && _fullThinking is null && _thinkingLoad != ThinkingLoad.Loading;

    /// <summary>Claims the read; false when one is running or nothing is missing.</summary>
    internal bool TryBeginFullThinkingLoad()
    {
        if (!NeedsFullThinking) return false;
        _thinkingLoad = ThinkingLoad.Loading;
        NotifyThinkingText();
        return true;
    }

    /// <summary>Applies the read; null keeps the excerpt and says the rest could not be read.</summary>
    internal void CompleteFullThinkingLoad(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _thinkingLoad = ThinkingLoad.Failed;
        }
        else
        {
            _fullThinking = text.Trim();
            _thinkingLoad = ThinkingLoad.Idle;
            _shownThinkingCharacters = ThinkingSliceCharacters;
        }
        NotifyThinkingText();
    }

    /// <summary>What the open block shows: the excerpt, or a slice of the whole reasoning once read.</summary>
    public string ThinkingText
    {
        get
        {
            if (_fullThinking is not { } full) return Detail ?? string.Empty;
            if (full.Length <= _shownThinkingCharacters) return full;
            var length = _shownThinkingCharacters;
            if (char.IsHighSurrogate(full[length - 1])) length--;
            return full[..length];
        }
    }

    /// <summary>Whether the loaded reasoning has more than the block shows.</summary>
    public bool HasMoreThinking => _fullThinking is { } full && full.Length > _shownThinkingCharacters;

    /// <summary>Shows the next slice of the loaded reasoning.</summary>
    internal void ShowMoreThinking()
    {
        if (!HasMoreThinking) return;
        _shownThinkingCharacters += ThinkingSliceCharacters;
        NotifyThinkingText();
    }

    /// <summary>A note under the block while the whole reasoning loads or after it could not.</summary>
    public string ThinkingLoadStatus => _thinkingLoad switch
    {
        ThinkingLoad.Loading => "Loading the full thinking…",
        ThinkingLoad.Failed => "Couldn't load the rest of this thinking.",
        _ => string.Empty,
    };

    public bool HasThinkingLoadStatus => ThinkingLoadStatus.Length > 0;

    private void NotifyThinkingText()
    {
        OnPropertyChanged(nameof(ThinkingText));
        OnPropertyChanged(nameof(HasMoreThinking));
        OnPropertyChanged(nameof(ThinkingLoadStatus));
        OnPropertyChanged(nameof(HasThinkingLoadStatus));
    }
}
