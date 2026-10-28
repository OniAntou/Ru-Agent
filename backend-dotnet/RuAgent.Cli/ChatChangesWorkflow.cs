using System.Globalization;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Cli.Tui;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

internal sealed record ChatChangedFile(string Name, long? Added, long? Removed, IReadOnlyList<AgentActivityModel> Edits);
internal sealed record ChatChangesSummary(IReadOnlyList<ChatChangedFile> Files, long? Added, long? Removed);

/// <summary>Completed edit activities grouped by recorded filename, not physical path identity or net Git diff.</summary>
internal sealed class ChatChangesWorkflow(AgentApiClient client)
{
    internal const int MaxActivities = 4096;
    internal const int MaxVisibleFiles = 100;
    internal const string ScopeNote = "Available recorded edits; history may be limited. File names only: equal names may refer to different paths. Not net Git diff.";

    internal async Task ExecuteAsync(string session, string? preferredRun, string? argument,
        Func<ChoiceList, CancellationToken, Task<string?>>? choose,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? document,
        TextWriter output, bool json, CancellationToken ct)
    {
        var run = string.IsNullOrWhiteSpace(argument) ? preferredRun : argument.Trim();
        if (!string.IsNullOrEmpty(argument) && (!run!.StartsWith("run_", StringComparison.Ordinal) || run.Any(char.IsWhiteSpace)))
            throw new AgentApiException("invalid_changes_arguments", "Use /changes [run-id].");
        if (string.IsNullOrWhiteSpace(run)) run = (await client.ListSessionRunIdsAsync(session, ct).ConfigureAwait(false)).LastOrDefault();
        if (string.IsNullOrWhiteSpace(run))
        {
            await output.WriteLineAsync(json ? JsonSerializer.Serialize(new { v = 1, type = "changes", files = Array.Empty<object>() })
                : "No recorded run in this session yet.").ConfigureAwait(false);
            return;
        }
        while (true)
        {
            var snapshot = await client.GetRunAsync(run, ct).ConfigureAwait(false);
            if (snapshot.RunId != run || snapshot.SessionId != session)
                throw new AgentApiException("run_session_mismatch", "Changes belong to another session.");
            var summary = Project(await client.ListRunActivitiesAsync(run, ct).ConfigureAwait(false));
            var live = snapshot.TerminalAt is null;
            if (json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, type = "changes", run_id = run, in_progress = live,
                    scope = "available_recorded_edit_operations", grouped_by = "recorded_file_name", history_may_be_limited = true, full_paths_available = false, added = summary.Added, removed = summary.Removed,
                    files = summary.Files.Select(file => new { file_name = file.Name, added = file.Added, removed = file.Removed,
                        edits = file.Edits.Count, previews = file.Edits.Count(edit => AgentEditPreviewCodec.TryDecode(edit.Detail, out _, out _)) }) })).ConfigureAwait(false);
                return;
            }
            var caption = $"Recorded changes · {run}" + (live ? " · in progress, refresh for new edits" : "");
            var totals = $"{summary.Files.Count} file names · {Count(summary.Added, "+")} {Count(summary.Removed, "−")}";
            if (choose is null)
            {
                await output.WriteLineAsync(caption + "\n" + totals + "\n" + ScopeNote).ConfigureAwait(false);
                foreach (var file in summary.Files) await output.WriteLineAsync($"{ChatDisplayText.Sanitize(file.Name, 512)} · {Count(file.Added, "+")} {Count(file.Removed, "−")} · {file.Edits.Count} edits").ConfigureAwait(false);
                if (summary.Files.Count == 0) await output.WriteLineAsync("No completed edit activities available.").ConfigureAwait(false);
                return;
            }
            var visible = summary.Files.Take(MaxVisibleFiles).ToArray();
            var items = visible.Select((file, index) => new ChoiceItem(file.Name,
                $"{Count(file.Added, "+")} {Count(file.Removed, "−")} · {file.Edits.Count} edits", index.ToString(CultureInfo.InvariantCulture))).ToList();
            items.Add(new ChoiceItem("Refresh", totals + (summary.Files.Count > MaxVisibleFiles ? " · showing first 100 names" : "")
                + (live ? " · in progress" : "") + (summary.Files.Count == 0 ? " · no completed edits" : ""), "refresh"));
            var choice = await choose(new ChoiceList("Changes · " + run, items, footer: "↑↓ · enter preview · esc close · names only · limited history"), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (choice is null) return;
            if (choice == "refresh") continue;
            if (!int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var selected) || selected < 0 || selected >= visible.Length)
                throw new AgentApiException("changes_selection_invalid", "Refresh the changes list.");
            if (document is not null) await document("Recorded preview", Preview(visible[selected], run), ct).ConfigureAwait(false);
        }
    }

    internal static ChatChangesSummary Project(IReadOnlyList<AgentActivityModel> activities)
    {
        if (activities.Count > MaxActivities) throw new AgentApiException("changes_limit_exceeded", "Recorded activity list exceeded its display budget.");
        var latest = new Dictionary<string, AgentActivityModel>(StringComparer.Ordinal);
        foreach (var activity in activities)
        {
            if (string.IsNullOrWhiteSpace(activity.ActivityId)) continue;
            if (!latest.TryGetValue(activity.ActivityId, out var previous)
                || activity.DurableSequence is null || previous.DurableSequence is null || activity.DurableSequence >= previous.DurableSequence)
                latest[activity.ActivityId] = activity;
        }
        var files = latest.Values.Where(activity => activity.Kind == AgentActivityKind.Edit && activity.State == AgentActivityState.Completed
                && !string.IsNullOrWhiteSpace(activity.FileName))
            .GroupBy(activity => activity.FileName!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ChatChangedFile(group.Key, Sum(group.Select(edit => edit.Added)), Sum(group.Select(edit => edit.Removed)),
                group.OrderBy(edit => edit.DurableSequence ?? long.MaxValue).ToArray())).ToArray();
        return new(files, Sum(files.Select(file => file.Added)), Sum(files.Select(file => file.Removed)));
    }

    internal static IReadOnlyList<string> Preview(ChatChangedFile file, string run)
    {
        const int maxRows = 2000;
        var rows = new List<string> { ChatDisplayText.Sanitize(file.Name, 512), "Run: " + run, ScopeNote,
            "Bounded historical previews; they may omit unchanged context or be truncated.", "" };
        foreach (var edit in file.Edits)
        {
            rows.Add($"Edit {ChatDisplayText.Sanitize(edit.ActivityId, 128)} · " + (edit.LineStart is > 0 ? $"line {edit.LineStart}" : "recorded edit"));
            if (AgentEditPreviewCodec.TryDecode(edit.Detail, out var before, out var after))
            {
                rows.Add("Before:");
                AddLines(before); rows.Add("After:"); AddLines(after);
            }
            else rows.Add("No recorded preview available.");
            rows.Add("");
            if (rows.Count >= maxRows) break;
        }
        if (rows.Count >= maxRows) { rows = rows.Take(maxRows).ToList(); rows.Add("Preview display limit reached; remaining content omitted."); }
        return rows;

        void AddLines(string text)
        {
            foreach (var line in TuiText.SanitizeUntrusted(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')).Split('\n'))
            { if (rows.Count >= maxRows) break; rows.Add("  " + line); }
        }
    }

    internal static string Count(long? value, string sign) => value is { } count ? sign + count.ToString("N0", CultureInfo.InvariantCulture) : sign + "?";
    private static long? Sum(IEnumerable<int?> values) => Sum(values.Select(value => (long?)value));
    private static long? Sum(IEnumerable<long?> values)
    {
        long total = 0;
        foreach (var value in values) { if (value is null or < 0) return null; total += value.Value; }
        return total;
    }
}
