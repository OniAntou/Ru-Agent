using System.Globalization;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

internal sealed record ChatRewindResult(JsonElement Root, int Kept, int Removed, bool Applied);
internal sealed record ChatRewindTarget(int Index, string Role, string Label);

/// <summary>Persisted conversation rows only; the existing endpoint does not restore files.</summary>
internal sealed class ChatRewindWorkflow(SessionApiClient sessions, AgentApiClient agents)
{
    internal const int MaxMessages = 10_000;
    internal const int PickerLimit = 100;
    internal const string Scope = "Later conversation messages are removed. Files, Git and running processes are unchanged.";

    internal async Task<ChatRewindResult?> ExecuteAsync(string session, string? argument, Func<bool> ownsIdleSession,
        Func<ChoiceList, CancellationToken, Task<string?>>? choose, TextWriter output, bool json, CancellationToken ct,
        IReadOnlyCollection<string>? knownRunIds = null)
    {
        var parts = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int? requested = null;
        var yes = false;
        if (parts.Length > 0)
        {
            if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1
                || (parts.Length == 2 && parts[1] != "--yes"))
                throw new AgentApiException("invalid_rewind_arguments", "Use /rewind [message-number [--yes]]. Message numbers start at 1.");
            requested = number - 1;
            yes = parts.Length == 2;
        }
        await EnsureIdleAsync(session, ownsIdleSession, knownRunIds, ct).ConfigureAwait(false);
        var before = await sessions.GetSessionAsync(session, ct).ConfigureAwait(false);
        var messages = ReadMessages(before, session);
        var targets = Targets(messages);
        if (targets.Count == 0)
        {
            await output.WriteLineAsync(json ? JsonSerializer.Serialize(new { v = 1, type = "rewind_targets", messages = Array.Empty<object>() })
                : "No saved user/assistant messages to rewind to.").ConfigureAwait(false);
            return null;
        }
        if (requested is null)
        {
            var recent = targets.TakeLast(PickerLimit).Reverse().ToArray();
            if (choose is null || json)
            {
                if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, type = "rewind_targets", session_id = session,
                    total_messages = messages.GetArrayLength(), scope = "conversation_only", targets = recent.Select(target => new
                    { message_number = target.Index + 1, role = target.Role, excerpt = target.Label }) })).ConfigureAwait(false);
                else
                {
                    await output.WriteLineAsync(Scope).ConfigureAwait(false);
                    foreach (var target in recent) await output.WriteLineAsync($"#{target.Index + 1} · {target.Role} · {target.Label}").ConfigureAwait(false);
                    await output.WriteLineAsync("Apply explicitly with /rewind <message-number> --yes.").ConfigureAwait(false);
                }
                return null;
            }
            var selected = await choose(new ChoiceList("Rewind conversation · " + session,
                recent.Select(target => new ChoiceItem($"#{target.Index + 1} · {target.Role} · {target.Label}",
                    $"Keep through #{target.Index + 1}; remove {messages.GetArrayLength() - target.Index - 1} later messages",
                    (target.Index + 1).ToString(CultureInfo.InvariantCulture))).ToArray(),
                footer: "↑↓ select · enter choose · esc cancel · conversation only"), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (selected is null) return null;
            if (!int.TryParse(selected, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new AgentApiException("invalid_rewind_target", "Select a saved message.");
            requested = number - 1;
        }
        var targetIndex = requested.Value;
        if (!targets.Any(target => target.Index == targetIndex))
            throw new AgentApiException("invalid_rewind_target", "Target is not a saved user/assistant message.");
        var remove = messages.GetArrayLength() - targetIndex - 1;
        if (remove == 0) return new(before, targetIndex + 1, 0, false);
        if (!yes)
        {
            if (choose is null || json) throw new AgentApiException("interactive_required", "Use /rewind <message-number> --yes to confirm conversation truncation.");
            var decision = await choose(new ChoiceList("Remove later conversation messages?",
                [new ChoiceItem("Keep history", "Files unchanged · cancel", "keep"), new ChoiceItem($"Rewind to #{targetIndex + 1}",
                    $"Remove {remove} later messages. Files/processes unchanged.", "rewind")]), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (decision != "rewind") return null;
        }
        await EnsureIdleAsync(session, ownsIdleSession, knownRunIds, ct).ConfigureAwait(false);
        var fresh = ReadMessages(await sessions.GetSessionAsync(session, ct).ConfigureAwait(false), session);
        if (!JsonElement.DeepEquals(messages, fresh))
            throw new AgentApiException("rewind_stale", "Conversation changed while selecting the target. Open /rewind again.");
        if (!ownsIdleSession()) throw new AgentApiException("session_busy", "Session changed or has pending work.");
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = await sessions.RewindAsync(session, targetIndex, ct).ConfigureAwait(false);
            var kept = ReadMessages(result, session);
            if (kept.GetArrayLength() != targetIndex + 1
                || Enumerable.Range(0, targetIndex + 1).Any(index => !JsonElement.DeepEquals(messages[index], kept[index])))
                throw new AgentApiException("invalid_response", "Host did not acknowledge the selected history prefix.");
            return new(result, targetIndex + 1, remove, true);
        }
        catch (Exception ex)
        {
            // A failed/cancelled response can follow a committed rewind. Do not repeat the POST.
            throw new AgentApiException("rewind_outcome_unknown", "Rewind was not confirmed. Reload this session before sending another task.", inner: ex);
        }
    }

    private async Task EnsureIdleAsync(string session, Func<bool> ownsIdle, IReadOnlyCollection<string>? knownRunIds, CancellationToken ct)
    {
        if (!ownsIdle()) throw new AgentApiException("session_busy", "Finish pending work before rewinding.");
        var discovered = await agents.ListSessionRunIdsAsync(session, ct).ConfigureAwait(false);
        foreach (var runId in discovered.Concat(knownRunIds ?? []).Distinct(StringComparer.Ordinal))
        {
            var run = await agents.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run.SessionId != session) throw new AgentApiException("run_session_mismatch", "Run does not belong to this session.");
            if (run.TerminalAt is null || run.Status is not ("completed" or "failed" or "cancelled" or "interrupted"))
                throw new AgentApiException("session_busy", "Finish, cancel or release the existing run before rewinding.");
        }
    }

    internal static JsonElement ReadMessages(JsonElement root, string expectedSession)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("session", out var session) || session.ValueKind != JsonValueKind.Object
            || !session.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != expectedSession
            || !session.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() > MaxMessages)
            throw new AgentApiException("rewind_history_invalid", "Saved history identity/shape is unavailable or exceeds the display budget.");
        return messages;
    }

    internal static IReadOnlyList<ChatRewindTarget> Targets(JsonElement messages)
    {
        var targets = new List<ChatRewindTarget>();
        var index = 0;
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("role", out var roleNode) && roleNode.ValueKind == JsonValueKind.String)
            {
                var role = roleNode.GetString();
                if (role is "user" or "assistant" or "ai")
                {
                    var text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null;
                    targets.Add(new(index, role, string.IsNullOrWhiteSpace(text) ? "[non-text message]" : ChatDisplayText.Sanitize(text, 160)));
                }
            }
            index++;
        }
        return targets;
    }
}
