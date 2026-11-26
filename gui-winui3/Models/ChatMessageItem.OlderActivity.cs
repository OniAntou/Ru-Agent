namespace RuApp.Models;

/// <summary>
/// Where a reopened run's replay can continue into older activity. The Host answers a long
/// run one page at a time, newest first; this names the next older page and the persisted
/// reply text the bubble is rebuilt around when that page arrives.
/// </summary>
public sealed class RunActivityPaging
{
    public RunActivityPaging(string runId, long nextBeforeSequence, int olderCount, string? persistedText)
    {
        RunId = runId;
        NextBeforeSequence = nextBeforeSequence;
        OlderCount = olderCount;
        PersistedText = persistedText;
    }

    public string RunId { get; }

    /// <summary>The cursor for the page just older than what is shown.</summary>
    public long NextBeforeSequence { get; }

    /// <summary>Older rows still unread, as the Host counted them.</summary>
    public int OlderCount { get; }

    /// <summary>The session's own reply text, which a rebuilt bubble keeps as its answer.</summary>
    public string? PersistedText { get; }

    /// <summary>Set while a page is being read, so a sentinel seen twice loads once.</summary>
    public bool IsLoading { get; set; }
}

public partial class ChatMessageItem
{
    private RunActivityPaging? _olderActivity;

    /// <summary>
    /// Set on the first reply part of a replayed run that has older activity still unread.
    /// The transcript shows a sentinel at the top of that part's activity and reads the next
    /// older page when the sentinel scrolls into view.
    /// </summary>
    public RunActivityPaging? OlderActivity
    {
        get => _olderActivity;
        set
        {
            if (SetProperty(ref _olderActivity, value))
            {
                OnPropertyChanged(nameof(ShowOlderActivity));
                OnPropertyChanged(nameof(OlderActivityLabel));
                OnOlderActivityLayoutChanged();
            }
        }
    }

    public bool ShowOlderActivity => OlderActivity is not null;

    /// <summary>The sentinel above the reply's interleaved timeline.</summary>
    public bool ShowOlderActivityInSegments => OlderActivity is not null && HasSegments;

    /// <summary>The sentinel above the grouped feed a reply without narration falls back to.</summary>
    public bool ShowOlderActivityInLegacyFeed => OlderActivity is not null && ShowLegacyActivityFeed;

    private void OnOlderActivityLayoutChanged()
    {
        OnPropertyChanged(nameof(ShowOlderActivityInSegments));
        OnPropertyChanged(nameof(ShowOlderActivityInLegacyFeed));
    }

    public string OlderActivityLabel => OlderActivity is { } paging
        ? paging.OlderCount == 1 ? "Loading 1 earlier step…" : $"Loading {paging.OlderCount:N0} earlier steps…"
        : string.Empty;
}
