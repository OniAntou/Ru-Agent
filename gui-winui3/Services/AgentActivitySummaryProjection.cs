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
/// One turn's activity line: what the run is doing now while it works, and a counted
/// recap of what it did once it ends. <see cref="Label"/> is the whole line for screen
/// readers.
/// </summary>
/// <param name="Count">Steps the run took: tool actions, not thinking or narration.</param>
/// <param name="Recap">"Read 5 files, searched 3 times and edited 2 files"; empty when nothing counted.</param>
internal sealed record AgentActivitySummary(
    int Count,
    int Failed,
    int Waiting,
    string Title,
    string Meta,
    string Glyph,
    bool IsLive,
    AgentActivitySummaryTone Tone,
    string Recap = "")
{
    internal static readonly AgentActivitySummary Empty =
        new(0, 0, 0, string.Empty, string.Empty, string.Empty, false, AgentActivitySummaryTone.Quiet);

    internal string Label => Meta.Length == 0 ? Title : $"{Title} · {Meta}";

    internal string StepsText => Count == 1 ? "1 step" : $"{Count} steps";
}

/// <summary>
/// The one projection behind a turn's process header, for live runs and replay alike.
/// </summary>
/// <remarks>
/// A live turn names the step in progress with the same verb and subject its row shows
/// ("Reading ChatView.xaml…", "Running dotnet test…"), so the line changes as each step
/// starts and settles. A finished turn reads as a counted sentence of its three most
/// frequent kinds of work (ties keep first appearance). Subjects come from the row,
/// which the Host already redacted and bounded; the line cuts them again so it stays
/// one short line however long a command or path is.
/// </remarks>
internal static class AgentActivitySummaryProjection
{
    internal const int SubjectMaxChars = 60;

    internal const string FailedGlyph = "";
    internal const string WaitingGlyph = "";
    internal const string WorkingTitle = "Working…";

    internal static AgentActivitySummary ForMessage(ChatMessageItem? message) =>
        message?.Role != "assistant" || message.AgentActivities is not { } activities
            ? AgentActivitySummary.Empty
            : ForSteps(activities, message.IsStreaming);

    internal static AgentActivitySummary ForSteps(IEnumerable<AgentActivityItem> steps, bool live)
    {
        int count = 0, failed = 0, waiting = 0;
        bool waitingForUser = false, any = false;
        AgentActivityItem? current = null;
        var work = new List<(string Key, string Phrase, HashSet<string> Subjects, int Count, int First)>();
        foreach (var item in steps)
        {
            if (item.Kind is AgentActivityKind.Narration or AgentActivityKind.Steer) continue;
            any = true;
            if (item.IsRunning || item.IsWaiting) current = item;
            if (item.IsFailed) failed++;
            if (item.IsWaiting)
            {
                waiting++;
                if (item.Kind != AgentActivityKind.Status) waitingForUser = true;
            }
            if (!item.CountsAsStep) continue;
            count++;
            if (WorkOf(item) is not { } kind) continue;
            var index = work.FindIndex(entry => entry.Key == kind.Key);
            if (index < 0)
            {
                work.Add((kind.Key, kind.Phrase, new(StringComparer.OrdinalIgnoreCase), 0, work.Count));
                index = work.Count - 1;
            }
            var entry = work[index];
            // Files count once however often they were opened; other work counts each step.
            if (kind.Subject is null || entry.Subjects.Add(kind.Subject)) entry.Count++;
            work[index] = entry;
        }
        if (!any) return AgentActivitySummary.Empty;

        var recap = Recap(work.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.First)
            .Select(entry => Phrase(entry.Key, entry.Phrase, entry.Count)).ToList());
        var title = live ? LiveTitle(current) : recap.Length > 0 ? recap : "Thought it through";
        var tail = new List<string>();
        if (waitingForUser) tail.Add("waiting for you");
        if (failed > 0) tail.Add(failed == 1 ? "1 failed" : $"{failed} failed");

        var tone = failed > 0 ? AgentActivitySummaryTone.Failed
            : waiting > 0 ? AgentActivitySummaryTone.Waiting : AgentActivitySummaryTone.Quiet;
        var glyph = tone switch
        {
            AgentActivitySummaryTone.Failed => FailedGlyph,
            AgentActivitySummaryTone.Waiting => WaitingGlyph,
            _ => KindGlyph(live && current is not null ? current.Kind
                : work.Count > 0 ? KindOfWork(work.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.First).First().Key)
                : AgentActivityKind.Thinking),
        };
        return new(count, failed, waiting, title, string.Join(" · ", tail), glyph, live && waiting == 0, tone, recap);
    }

    /// <summary>
    /// The step in progress as its row says it, cut to one short line: "Reading
    /// ChatView.xaml…", "Searching for "steer"…", "Waiting for your approval".
    /// </summary>
    internal static string LiveTitle(AgentActivityItem? current)
    {
        if (current is null) return WorkingTitle;
        if (current.IsWaiting) return current.RowVerb;
        if (current.Kind == AgentActivityKind.Thinking) return "Thinking…";
        var verb = current.RowVerb.TrimEnd('…', '.', ' ');
        // A plan, a compaction or a consultation reads as its verb; its subject is a count or a model.
        var subject = current.Kind is AgentActivityKind.Todo or AgentActivityKind.Compaction or AgentActivityKind.Advisor
            ? string.Empty : Shorten(current.RowSubject);
        if (subject.Length == 0) return verb + "…";
        var end = subject.EndsWith('…') ? "" : "…";
        // "Searching for "steer" in src…", but "Searching in src…" when no term is named.
        return current.Kind == AgentActivityKind.Search && current.Symbol is { Length: > 0 }
            ? $"{verb} for {subject}{end}" : $"{verb} {subject}{end}";
    }

    /// <summary>"A", "A and b", "A, b and c", or "A, b, c and more" past three kinds of work.</summary>
    internal static string Recap(IReadOnlyList<string> phrases)
    {
        if (phrases.Count == 0) return string.Empty;
        var shown = phrases.Take(3).ToList();
        var line = phrases.Count > 3 ? string.Join(", ", shown) + " and more"
            : shown.Count switch
            {
                1 => shown[0],
                2 => $"{shown[0]} and {shown[1]}",
                _ => $"{shown[0]}, {shown[1]} and {shown[2]}",
            };
        return char.ToUpperInvariant(line[0]) + line[1..];
    }

    /// <summary>The kind of work a step counts toward, and the file it touched when files count once.</summary>
    private static (string Key, string Phrase, string? Subject)? WorkOf(AgentActivityItem item) => item.Kind switch
    {
        AgentActivityKind.Analyze when item.ToolDisplay == "Docs" => ("docs", "read the docs", null),
        AgentActivityKind.Analyze => ("read", "file", item.FileName ?? item.ActivityId),
        AgentActivityKind.Explore when item.FileName is { Length: > 0 } file => ("read", "file", file),
        AgentActivityKind.Explore when item.ToolDisplay == "Glob" => ("glob", "searched for files", null),
        AgentActivityKind.Explore => ("list", "listed files", null),
        AgentActivityKind.Search => ("search", "searched", null),
        AgentActivityKind.Edit => ("edit", "file", item.FileName ?? item.ActivityId),
        AgentActivityKind.Terminal when item.ToolDisplay is null or "Terminal" => ("command", "command", null),
        AgentActivityKind.Web when item.ToolDisplay == "Fetch" => ("fetch", "page", null),
        AgentActivityKind.Web when item.ToolDisplay == "Browser" => ("browser", "used the browser", null),
        AgentActivityKind.Web => ("web", "searched the web", null),
        AgentActivityKind.Mcp => ("mcp", "MCP call", null),
        AgentActivityKind.Computer => ("computer", "used the computer", null),
        AgentActivityKind.Skill => ("skill", "skill", null),
        AgentActivityKind.Program => ("program", "program", null),
        AgentActivityKind.Advisor => ("advisor", "consulted the advisor", null),
        AgentActivityKind.Todo => ("todo", "updated the plan", null),
        AgentActivityKind.Status when item.ToolDisplay == "Spawn subagent" => ("subagent", "subagent", null),
        AgentActivityKind.Status when item.ToolDisplay == "Question" => ("question", "question", null),
        _ => null,
    };

    private static string Phrase(string key, string phrase, int count) => key switch
    {
        "read" => Counted("read", count, phrase),
        "edit" => Counted("edited", count, phrase),
        "command" => Counted("ran", count, phrase),
        "fetch" => Counted("fetched", count, phrase),
        "mcp" => Counted("made", count, phrase),
        "skill" => Counted("loaded", count, phrase),
        "program" => Counted("ran", count, phrase),
        "subagent" => Counted("started", count, phrase),
        "question" => Counted("asked", count, phrase),
        "todo" => phrase,
        _ => count == 1 ? phrase : count == 2 ? phrase + " twice" : $"{phrase} {count} times",
    };

    private static string Counted(string verb, int count, string noun) =>
        count == 1 ? $"{verb} 1 {noun}" : $"{verb} {count} {noun}s";

    private static AgentActivityKind KindOfWork(string key) => key switch
    {
        "read" or "list" or "glob" or "docs" => AgentActivityKind.Explore,
        "search" => AgentActivityKind.Search,
        "edit" => AgentActivityKind.Edit,
        "command" => AgentActivityKind.Terminal,
        "todo" => AgentActivityKind.Todo,
        _ => AgentActivityKind.Status,
    };

    /// <summary>Segoe Fluent glyph for a kind of work.</summary>
    internal static string KindGlyph(AgentActivityKind kind) => kind switch
    {
        AgentActivityKind.Explore => "",
        AgentActivityKind.Search => "",
        AgentActivityKind.Edit => "",
        AgentActivityKind.Terminal => RuApp.Controls.ActivityRowGlyphs.TerminalPrompt,
        AgentActivityKind.Thinking => "",
        AgentActivityKind.Todo => "",
        AgentActivityKind.Compaction => "",
        AgentActivityKind.Retry => "",
        AgentActivityKind.Steer => "",
        _ => "",
    };

    /// <summary>Elapsed time as "0:34" or "1:02:05"; tabular in the view.</summary>
    internal static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
    }

    /// <summary>One line of a subject, cut at a code-point boundary.</summary>
    private static string Shorten(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > SubjectMaxChars
            ? TextTruncation.TakeAtMost(line, SubjectMaxChars - 1).TrimEnd() + "…"
            : line;
    }
}
