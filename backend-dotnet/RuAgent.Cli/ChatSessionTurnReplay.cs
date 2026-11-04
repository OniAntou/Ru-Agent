using System.Collections.Immutable;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

/// <summary>One finished or running turn of a reopened session, with its durable record.</summary>
internal sealed record ChatReplayTurn(
    string? RunId,
    ImmutableArray<string> Prompts,
    ImmutableArray<string> Replies,
    GetAgentRunResponse? Run = null,
    AgentRunActivityReplay? Activity = null);

/// <summary>
/// Loads the newest turns of a session with each run's activity record, so a reopened
/// session can redraw them through the live renderer instead of as bare text.
/// </summary>
internal static class ChatSessionTurnReplay
{
    // The same newest page the Desktop opens with. The transcript's own retention
    // budget (TgTranscriptView.MaxRetainedRows) decides how much of it stays on screen.
    private const int PageMessages = 200;
    private const int MaximumConcurrentReads = 4;
    private const int MaxMessageChars = 100_000;

    /// <returns>The turns, or null when the message page cannot be read (callers fall back to text).</returns>
    public static async Task<ImmutableArray<ChatReplayTurn>?> LoadAsync(
        SessionApiClient sessions, AgentApiClient agents, string sessionId, CancellationToken cancellationToken)
    {
        JsonElement page;
        try
        {
            page = await sessions.GetMessagePageAsync(sessionId, limit: PageMessages, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }

        var turns = Group(page);
        var loaded = new ChatReplayTurn[turns.Count];
        using var gate = new SemaphoreSlim(MaximumConcurrentReads);
        await Task.WhenAll(turns.Select((turn, index) => LoadTurnAsync(index, turn))).ConfigureAwait(false);
        return [.. loaded];

        async Task LoadTurnAsync(int index, ChatReplayTurn turn)
        {
            loaded[index] = turn;
            if (turn.RunId is null) return;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var run = await agents.GetRunAsync(turn.RunId, cancellationToken).ConfigureAwait(false);
                AgentRunActivityReplay? activity = null;
                if (run.TerminalAt is not null)
                    activity = await agents.ListRunActivityReplayAsync(turn.RunId, cancellationToken).ConfigureAwait(false);
                loaded[index] = turn with { Run = run, Activity = activity };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // A damaged record keeps the turn's text; the other turns still draw.
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>Groups ordered messages into turns by the run that produced them.</summary>
    internal static List<ChatReplayTurn> Group(JsonElement page)
    {
        var turns = new List<(string? RunId, List<string> Prompts, List<string> Replies)>();
        if (page.ValueKind != JsonValueKind.Object
            || !page.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object) continue;
            var role = Text(message, "role")?.Trim().ToLowerInvariant();
            var content = Text(message, "content")?.Trim();
            if (role is not ("user" or "ai" or "assistant") || string.IsNullOrWhiteSpace(content)) continue;
            var runId = Text(message, "source_run_id");
            if (string.IsNullOrWhiteSpace(runId)) runId = null;
            if (content.Length > MaxMessageChars) content = Tui.TuiText.SafeCut(content, MaxMessageChars).TrimEnd() + " …";

            var startsTurn = turns.Count == 0
                || !string.Equals(turns[^1].RunId, runId, StringComparison.Ordinal)
                || runId is null && role == "user";
            if (startsTurn) turns.Add((runId, [], []));
            (role == "user" ? turns[^1].Prompts : turns[^1].Replies).Add(content);
        }

        return turns.Select(turn => new ChatReplayTurn(turn.RunId, [.. turn.Prompts], [.. turn.Replies])).ToList();
    }

    internal static Tui.TuiTurnOutcome Outcome(string? status) => status?.ToLowerInvariant() switch
    {
        "completed" => Tui.TuiTurnOutcome.Completed,
        "cancelled" => Tui.TuiTurnOutcome.Cancelled,
        _ => Tui.TuiTurnOutcome.Failed,
    };

    internal static DateTimeOffset? Time(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
