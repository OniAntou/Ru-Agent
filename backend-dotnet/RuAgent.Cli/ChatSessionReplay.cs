using System.Collections.Immutable;
using System.Text.Json;
using RuAgent.Client;

namespace RuAgent.Cli;

internal sealed record ChatSessionReplayMessage(string Role, string Text);

internal sealed record ChatSessionReplayResult(
    ImmutableArray<ChatSessionReplayMessage> Messages,
    int Hidden,
    int Shortened,
    string? Title,
    string? Error);

internal static class ChatSessionReplay
{
    public const int MessageLimit = 20;
    private const int MaxMessageChars = 2_000;
    private const int MaxTitleChars = 120;

    public static async Task<ChatSessionReplayResult> LoadAsync(
        SessionApiClient sessionClient,
        string sessionId,
        CancellationToken cancellationToken)
    {
        JsonElement root;
        try
        {
            root = await sessionClient.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentApiException ex)
        {
            return Failed($"{ex.ErrorCode}: {ChatDisplayText.Sanitize(CliErrorText.ShortMessage(ex.Message), CliErrorText.MaxMessageChars)}");
        }
        catch (Exception ex)
        {
            return Failed($"session_get_failed: {ChatDisplayText.Sanitize(CliErrorText.ShortMessage(ex.Message), CliErrorText.MaxMessageChars)}");
        }

        var title = ReadTitle(root);
        var all = ReadMessages(root);
        var tail = all.Count > MessageLimit
            ? all.GetRange(all.Count - MessageLimit, MessageLimit)
            : all;
        var messages = ImmutableArray.CreateBuilder<ChatSessionReplayMessage>(tail.Count);
        var hidden = 0;
        var shortened = 0;
        foreach (var (role, text) in tail)
        {
            if (role is not ("user" or "ai" or "assistant"))
            {
                hidden++;
                continue;
            }

            var bounded = text;
            if (bounded.Length > MaxMessageChars)
            {
                bounded = bounded[..MaxMessageChars].TrimEnd() + " …";
                shortened++;
            }

            messages.Add(new ChatSessionReplayMessage(role, bounded));
        }

        return new ChatSessionReplayResult(messages.ToImmutable(), hidden, shortened, title, null);
    }

    private static ChatSessionReplayResult Failed(string error) => new([], 0, 0, null, error);

    private static string? ReadTitle(JsonElement root)
    {
        if (!root.TryGetProperty("session", out var session)
            || session.ValueKind != JsonValueKind.Object
            || !session.TryGetProperty("title", out var title)
            || title.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(title.GetString()))
        {
            return null;
        }

        var safeTitle = ChatDisplayText.Sanitize(title.GetString(), MaxTitleChars);
        return safeTitle.Length == 0 ? null : safeTitle;
    }

    private static List<(string Role, string Text)> ReadMessages(JsonElement root)
    {
        var messages = new List<(string Role, string Text)>();
        if (!root.TryGetProperty("session", out var session)
            || session.ValueKind != JsonValueKind.Object
            || !session.TryGetProperty("messages", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var message in values.EnumerateArray())
        {
            var role = message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("role", out var roleElement)
                && roleElement.ValueKind == JsonValueKind.String
                    ? roleElement.GetString()!.Trim().ToLowerInvariant()
                    : string.Empty;
            var content = message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString() ?? string.Empty
                    : string.Empty;
            if (!string.IsNullOrWhiteSpace(content))
            {
                messages.Add((role, content.Trim()));
            }
        }

        return messages;
    }
}
