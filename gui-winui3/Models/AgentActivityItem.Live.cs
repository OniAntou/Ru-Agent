using RuAgent.Protocol.Harness;

namespace RuApp.Models;

/// <summary>
/// What the transcript knows about a row only because it watched the row happen: when
/// it started running and whether it has just arrived. Replayed history has neither, so
/// a reopened transcript never ticks a clock it cannot measure or replays an entrance.
/// </summary>
public sealed partial class AgentActivityItem
{
    /// <summary>A row realized later than this after it arrived enters without motion.</summary>
    internal const long EntranceWindowMilliseconds = 2_000;

    private const int MaximumTickerCharacters = 200;

    private long? _liveStartedTick;
    private long? _arrivedTick;

    /// <summary>
    /// Records a frame the live feed applied. <paramref name="created"/> marks a row that
    /// arrived now; <paramref name="wasRunning"/> is the state before the frame, so the
    /// clock starts only at a start the feed saw, never for a row adopted mid-run.
    /// </summary>
    internal void ObserveLiveFrame(bool created, bool wasRunning, long nowTick)
    {
        if (created) _arrivedTick = nowTick;
        var started = _liveStartedTick;
        if (!IsRunning) _liveStartedTick = null;
        else if ((created || !wasRunning) && _liveStartedTick is null) _liveStartedTick = nowTick;
        // The frame's own notifications fired before this ran, so say so again.
        if (started != _liveStartedTick) OnPropertyChanged(nameof(HasLiveClock));
    }

    /// <summary>How long the row has been running, when the feed saw it start.</summary>
    internal TimeSpan? LiveElapsedAt(long nowTick) =>
        IsRunning && _liveStartedTick is { } started
            ? TimeSpan.FromMilliseconds(Math.Max(0, nowTick - started))
            : null;

    public bool HasLiveClock => IsRunning && _liveStartedTick is not null;

    /// <summary>
    /// True once, for a row that arrived live within the entrance window. Recycling or
    /// realizing the row again later returns false.
    /// </summary>
    internal bool TryTakeEntrance(long nowTick)
    {
        if (_arrivedTick is not { } arrived) return false;
        _arrivedTick = null;
        return nowTick - arrived <= EntranceWindowMilliseconds;
    }

    /// <summary>A running clock: seconds under a minute, then minutes and padded seconds.</summary>
    internal static string FormatLiveElapsed(TimeSpan elapsed)
    {
        var seconds = (long)elapsed.TotalSeconds;
        return seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60:00}s";
    }

    /// <summary>
    /// The newest non-empty output line of a running command, for the line under its
    /// collapsed row. Empty when the row is settled, open, or has printed nothing yet.
    /// </summary>
    public string TerminalTickerText
    {
        get
        {
            if (Kind != AgentActivityKind.Terminal || !IsRunning || IsEditPreviewExpanded) return string.Empty;
            return LastOutputLine(TerminalOutputText);
        }
    }

    public bool HasTerminalTicker => TerminalTickerText.Length > 0;

    internal static string LastOutputLine(string output)
    {
        var end = output.Length;
        while (end > 0)
        {
            var start = output.LastIndexOf('\n', end - 1);
            var line = output.AsSpan(start + 1, end - start - 1).Trim();
            if (line.Length > 0)
            {
                // A carriage-return progress bar redraws in place: show what it shows last.
                var lastRedraw = line.LastIndexOf('\r');
                if (lastRedraw >= 0) line = line[(lastRedraw + 1)..].Trim();
                if (line.Length > 0)
                {
                    var cut = MaximumTickerCharacters;
                    if (line.Length > cut && char.IsHighSurrogate(line[cut - 1])) cut--;
                    var text = line.Length > MaximumTickerCharacters
                        ? line[..cut].ToString() + "…"
                        : line.ToString();
                    // Tabs, bells and escape bytes would draw as boxes or shift the line.
                    return string.Create(text.Length, text, static (span, source) =>
                    {
                        for (var i = 0; i < source.Length; i++)
                            span[i] = char.IsControl(source[i]) ? ' ' : source[i];
                    }).Trim();
                }
            }

            if (start < 0) break;
            end = start;
        }

        return string.Empty;
    }
}
