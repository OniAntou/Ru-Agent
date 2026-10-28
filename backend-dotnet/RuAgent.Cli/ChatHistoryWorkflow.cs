using System.Globalization;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

internal sealed record ChatHistoryTurn(int Number, int Start, int End, string Prompt);
internal sealed record ChatHistoryBranch(string SourceId, string Revision, int KeepMessages,
    string? Draft, JsonElement Messages, int MessageOffset = 0);

/// <summary>Turn-based browsing stages a branch; only the next ordinary send creates it.</summary>
internal sealed class ChatHistoryWorkflow(SessionApiClient sessions, AgentApiClient agents)
{
    internal const string Scope = "New chat branch · current files unchanged · original conversation kept. Old image attachments are not restored.";

    internal async Task<ChatHistoryBranch?> SelectAsync(string session, string? argument, Func<bool> ownsIdle,
        Func<ChoiceList, CancellationToken, Task<string?>>? choose,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? document,
        TextWriter output, bool json, CancellationToken ct, IReadOnlyCollection<string>? knownRuns = null)
    {
        if (!ownsIdle()) throw new AgentApiException("session_busy", "Finish pending work before opening history.");
        foreach (var id in (await agents.ListSessionRunIdsAsync(session, ct)).Concat(knownRuns ?? []).Distinct())
        {
            var run = await agents.GetRunAsync(id, ct);
            if (run.SessionId != session || run.TerminalAt is null)
                throw new AgentApiException("session_busy", "Finish or cancel the existing run before branching.");
        }
        var root = await sessions.GetSessionAsync(session, ct);
        var messages = ChatRewindWorkflow.ReadMessages(root, session);
        var offset = root.GetProperty("session").TryGetProperty("message_offset", out var offsetValue)
            ? offsetValue.GetInt32() : 0;
        if (offset < 0 || offset > int.MaxValue - messages.GetArrayLength())
            throw new AgentApiException("history_branch_unsupported", "Saved history has an invalid page offset.");
        var turns = Turns(messages);
        var parts = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2 || (parts.Length > 0 && (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1))
            || (parts.Length == 2 && parts[1] is not ("view" or "edit" or "continue")))
            throw new AgentApiException("invalid_history_arguments", "Use /history [turn [view|edit|continue]] or /history cancel.");
        if (turns.Count == 0)
        {
            await output.WriteLineAsync(json ? JsonSerializer.Serialize(new { v = 1, type = "history", session_id = session, turns = Array.Empty<object>() }) : "No saved turns yet.");
            return null;
        }
        if (choose is null && parts.Length == 0)
        {
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, type = "history", session_id = session, scope = Scope,
                turns = turns.TakeLast(100).Reverse().Select(t => new { turn = t.Number, excerpt = ChatDisplayText.Sanitize(t.Prompt, 160) }) }));
            else
            {
                await output.WriteLineAsync(Scope);
                foreach (var turn in turns.TakeLast(100).Reverse()) await output.WriteLineAsync($"Turn {turn.Number} · {ChatDisplayText.Sanitize(turn.Prompt, 160)}");
            }
            return null;
        }
        var selected = parts.Length > 0 ? parts[0] : await choose!(new ChoiceList("History · turns",
            turns.TakeLast(100).Reverse().Select(t => new ChoiceItem($"Turn {t.Number} · {ChatDisplayText.Sanitize(t.Prompt, 160)}",
                "View, edit or continue · current files", t.Number.ToString(CultureInfo.InvariantCulture))).ToArray(),
            footer: "↑↓ select · enter actions · esc cancel · original kept"), ct);
        ct.ThrowIfCancellationRequested();
        if (selected is null) return null;
        var target = turns.FirstOrDefault(t => t.Number.ToString(CultureInfo.InvariantCulture) == selected)
            ?? throw new AgentApiException("invalid_history_turn", "Choose an available turn.");
        while (true)
        {
            var action = parts.Length == 2 ? parts[1] : choose is null ? "view" : await choose(new ChoiceList($"Turn {target.Number}",
                [new("View turn", "Saved prompt, reply and available run evidence", "view"),
                 new("Edit and try again", "Restore prompt; create branch on next send", "edit"),
                 new("Continue from here", "Keep this turn; create branch on next send", "continue")], footer: Scope), ct);
            ct.ThrowIfCancellationRequested();
            if (action is null) return null;
            if (action == "view")
            {
                var rows = Preview(messages, target).ToList();
                await AddEvidenceAsync(session, messages, target, rows, ct);
                if (document is not null) await document($"Turn {target.Number} · saved history", rows, ct);
                else if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, type = "history_turn", turn = target.Number, lines = rows }));
                else foreach (var row in rows) await output.WriteLineAsync(row);
                if (parts.Length > 0 || choose is null) return null;
                continue;
            }
            if (action is not ("edit" or "continue")) throw new AgentApiException("invalid_history_action", "Choose a history action.");
            if (!ownsIdle()) throw new AgentApiException("session_busy", "Session changed while choosing history.");
            if (!root.GetProperty("session").TryGetProperty("history_revision", out var revision) || revision.ValueKind != JsonValueKind.String
                || revision.GetString() is not { Length: 64 } token)
                throw new AgentApiException("history_branch_unsupported", "Update the Host to use history branches.");
            if (action == "edit" && (target.Prompt.Length > 16_000 || TuiText.SanitizeUntrusted(target.Prompt) != target.Prompt))
                throw new AgentApiException("history_prompt_unavailable", "Saved prompt exceeds the editor limit or contains control sequences. Use Continue from here.");
            return new(session, token, offset + (action == "edit" ? target.Start : target.End),
                action == "edit" ? target.Prompt : null, messages.Clone(), offset);
        }
    }

    internal async Task<JsonElement> CommitAsync(ChatHistoryBranch plan, CancellationToken ct)
    {
        try
        {
            var root = await sessions.ForkAsync(plan.SourceId, plan.KeepMessages, plan.Revision, ct);
            if (!root.TryGetProperty("parent_session_id", out var parent) || parent.GetString() != plan.SourceId)
                throw new InvalidOperationException("invalid branch parent");
            var id = root.GetProperty("session").GetProperty("id").GetString();
            if (id == plan.SourceId || !ChatSessionWorkflow.IsValidSessionId(id)) throw new InvalidOperationException("invalid branch identity");
            var kept = ChatRewindWorkflow.ReadMessages(root, id!);
            var snapshot = root.GetProperty("session");
            var childOffset = snapshot.TryGetProperty("message_offset", out var start) ? start.GetInt32() : 0;
            var childCount = snapshot.TryGetProperty("total_message_count", out var total) ? total.GetInt32() : kept.GetArrayLength();
            var knownCount = plan.KeepMessages - plan.MessageOffset;
            var knownStart = plan.MessageOffset - childOffset;
            if (childOffset < 0 || childCount != plan.KeepMessages
                || (long)childOffset + kept.GetArrayLength() != childCount
                || knownCount < 0 || knownCount > plan.Messages.GetArrayLength()
                || knownStart < 0 || (long)knownStart + knownCount > kept.GetArrayLength()
                || Enumerable.Range(0, knownCount).Any(i => !JsonElement.DeepEquals(kept[knownStart + i], plan.Messages[i])))
                throw new InvalidOperationException("invalid branch prefix");
            return root;
        }
        catch (AgentApiException ex) when (ex.ErrorCode is "history_stale" or "session_not_found" or "invalid_fork_boundary" or "invalid_fork_request" or "session_fork_unsupported" or "unauthorized") { throw; }
        catch (Exception ex)
        {
            throw new AgentApiException("history_branch_unknown", "No run started. Branch creation was not confirmed; choose a session before sending again. Do not auto-repeat the request.", inner: ex);
        }
    }

    internal static IReadOnlyList<ChatHistoryTurn> Turns(JsonElement messages)
    {
        var starts = Enumerable.Range(0, messages.GetArrayLength()).Where(i => messages[i].TryGetProperty("role", out var role) && role.GetString() == "user").ToArray();
        return starts.Select((start, i) => new ChatHistoryTurn(i + 1, start, i + 1 < starts.Length ? starts[i + 1] : messages.GetArrayLength(),
            messages[start].TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString()! : "")).ToArray();
    }

    private async Task AddEvidenceAsync(string session, JsonElement messages, ChatHistoryTurn turn, List<string> rows, CancellationToken ct)
    {
        var runs = Enumerable.Range(turn.Start, turn.End - turn.Start)
            .Select(i => messages[i].TryGetProperty("source_run_id", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .OfType<string>().Distinct().Take(10).ToArray();
        rows.Add("");
        rows.Add("Available recorded tools and changes · bounded history, not current file state");
        if (runs.Length == 0) { rows.Add("No run evidence linked to this saved turn."); return; }
        foreach (var id in runs)
        {
            try
            {
                var run = await agents.GetRunAsync(id, ct);
                if (run.RunId != id || run.SessionId != session)
                { rows.Add("Inherited run evidence belongs to another session; inspect the original conversation."); continue; }
                var activities = await agents.ListRunActivitiesAsync(id, ct);
                rows.Add("Run " + ChatDisplayText.Sanitize(id, 128));
                foreach (var activity in activities.GroupBy(a => a.ActivityId).Select(g => g.Last()).Take(200))
                    rows.Add(ChatDisplayText.Sanitize($"{activity.Kind} · {activity.State} · {activity.ToolDisplay ?? activity.SafeCode} · {activity.FileName}", 512));
                var changes = ChatChangesWorkflow.Project(activities);
                foreach (var file in changes.Files.Take(30))
                    rows.Add($"File {ChatDisplayText.Sanitize(file.Name, 512)} · {ChatChangesWorkflow.Count(file.Added, "+")} {ChatChangesWorkflow.Count(file.Removed, "−")}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AgentApiException) { rows.Add("Recorded run evidence unavailable; saved text remains available."); }
        }
    }

    internal static IReadOnlyList<string> Preview(JsonElement messages, ChatHistoryTurn turn)
    {
        var rows = new List<string> { Scope, "Recorded text only; image payloads are not restored.", "" };
        for (var i = turn.Start; i < turn.End && rows.Count < 2000; i++)
        {
            var message = messages[i];
            rows.Add(message.GetProperty("role").GetString() ?? "message");
            foreach (var line in TuiText.SanitizeUntrusted(message.GetProperty("content").GetString() ?? "").Replace("\r\n", "\n").Split('\n'))
            { if (rows.Count >= 2000) break; rows.Add(ChatDisplayText.Sanitize(line, 2048)); }
        }
        if (rows.Count >= 2000) rows.Add("Display limit reached; remaining text omitted.");
        return rows;
    }
}
