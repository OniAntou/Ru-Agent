using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>How the compact summary line is tinted.</summary>
internal enum AgentActivitySummaryTone
{
    Quiet,
    Waiting,
    Failed,
}

/// <summary>
/// One turn's compact activity line: a leading glyph, a sentence title and a
/// short meta tail. <see cref="Label"/> is the whole line for screen readers.
/// </summary>
internal sealed record AgentActivitySummary(
    int Count,
    int Failed,
    int Waiting,
    string Title,
    string Meta,
    string Glyph,
    bool IsLive,
    AgentActivitySummaryTone Tone)
{
    internal static readonly AgentActivitySummary Empty =
        new(0, 0, 0, string.Empty, string.Empty, string.Empty, false, AgentActivitySummaryTone.Quiet);

    internal string Label => Meta.Length == 0 ? Title : $"{Title} · {Meta}";
}

/// <summary>
/// One turn's activity status; narration remains in the reply text.
/// </summary>
/// <remarks>
/// A finished turn reads as a sentence of what was done, built from its three
/// most frequent kinds of work (ties keep first appearance) without counts, so
/// the line stays short however long the turn ran. A live turn names the work in
/// progress instead. Only failures and waits add a tail; nothing here guesses a
/// duration the transcript does not record.
/// </remarks>
internal static class AgentActivitySummaryProjection
{
    internal const int LiveDetailMaxChars = 160;

    internal const string FailedGlyph = "\uE7BA";
    internal const string WaitingGlyph = "\uE823";

    internal static AgentActivitySummary ForMessage(ChatMessageItem? message)
    {
        if (message?.Role != "assistant" || message.AgentActivities is not { } activities)
            return AgentActivitySummary.Empty;

        int count = 0, failed = 0, waiting = 0;
        AgentActivityItem? running = null;
        var ranked = new List<(AgentActivityKind Kind, int Count, int First)>();
        foreach (var item in activities)
        {
            if (item.Kind == AgentActivityKind.Narration) continue;
            count++;
            if (item.IsFailed) failed++;
            if (item.IsWaiting) waiting++;
            if (item.IsRunning) running = item;
            if (!Ranks(item.Kind)) continue;
            var index = ranked.FindIndex(entry => entry.Kind == item.Kind);
            if (index < 0) ranked.Add((item.Kind, 1, ranked.Count));
            else ranked[index] = ranked[index] with { Count = ranked[index].Count + 1 };
        }
        if (count == 0) return AgentActivitySummary.Empty;

        var order = ranked.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.First)
            .Select(entry => entry.Kind).ToList();
        var live = message.IsStreaming;
        var title = live && waiting == 0 && running is not null
            ? Normalize(running.DisplayText)
            : live && waiting == 0 ? "Working…" : DoneTitle(order);
        var tail = new List<string>();
        if (waiting > 0) tail.Add("waiting for you");
        if (failed > 0) tail.Add(failed == 1 ? "1 failed" : $"{failed} failed");

        var tone = failed > 0 ? AgentActivitySummaryTone.Failed
            : waiting > 0 ? AgentActivitySummaryTone.Waiting : AgentActivitySummaryTone.Quiet;
        var glyph = tone switch
        {
            AgentActivitySummaryTone.Failed => FailedGlyph,
            AgentActivitySummaryTone.Waiting => WaitingGlyph,
            _ => KindGlyph(live && running is not null ? running.Kind : order.FirstOrDefault(AgentActivityKind.Thinking)),
        };
        return new(count, failed, waiting, title, string.Join(" · ", tail), glyph, live && waiting == 0, tone);
    }

    /// <summary>"A", "A and b", "A, b and c", or "A, b, c and more" past three kinds.</summary>
    internal static string DoneTitle(IReadOnlyList<AgentActivityKind> order)
    {
        var labels = order.Take(3).Select(DoneLabel).ToList();
        if (labels.Count == 0) return "Thought it through";
        for (var i = 1; i < labels.Count; i++) labels[i] = char.ToLowerInvariant(labels[i][0]) + labels[i][1..];
        if (order.Count > 3) return string.Join(", ", labels) + " and more";
        return labels.Count switch
        {
            1 => labels[0],
            2 => $"{labels[0]} and {labels[1]}",
            _ => $"{labels[0]}, {labels[1]} and {labels[2]}",
        };
    }

    internal static string DoneLabel(AgentActivityKind kind) => kind switch
    {
        AgentActivityKind.Explore => "Read files",
        AgentActivityKind.Search => "Searched code",
        AgentActivityKind.Edit => "Edited files",
        AgentActivityKind.Terminal => "Ran commands",
        AgentActivityKind.Analyze => "Analyzed",
        AgentActivityKind.Todo => "Updated the plan",
        AgentActivityKind.Compaction => "Compacted context",
        AgentActivityKind.Retry => "Retried",
        _ => "Worked",
    };

    /// <summary>Segoe Fluent glyph for a kind of work.</summary>
    internal static string KindGlyph(AgentActivityKind kind) => kind switch
    {
        AgentActivityKind.Explore => "\uE8A5",
        AgentActivityKind.Search => "\uE721",
        AgentActivityKind.Edit => "\uE70F",
        AgentActivityKind.Terminal => "\uE756",
        AgentActivityKind.Thinking => "\uE82F",
        AgentActivityKind.Todo => "\uE9D5",
        AgentActivityKind.Compaction => "\uE73F",
        AgentActivityKind.Retry => "\uE72C",
        _ => "\uE945",
    };

    /// <summary>Elapsed time as "0:34" or "1:02:05"; tabular in the view.</summary>
    internal static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
    }

    // Thinking, status and narration rows describe the run, not work done in it.
    private static bool Ranks(AgentActivityKind kind) =>
        kind is not (AgentActivityKind.Thinking or AgentActivityKind.Status or AgentActivityKind.Narration);

    private static string Normalize(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > LiveDetailMaxChars ? line[..(LiveDetailMaxChars - 1)].TrimEnd() + "…" : line;
    }
}
