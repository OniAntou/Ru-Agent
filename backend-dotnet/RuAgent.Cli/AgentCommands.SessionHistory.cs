using System.Text;
using System.Text.Json;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    public async Task<int> SessionHistoryAsync(SessionHistoryCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var element = await _sessionClient.GetMessagePageAsync(
                cmd.SessionId, cmd.Cursor, cmd.Limit, cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonEnvelope("session_history", element)).ConfigureAwait(false);
                return 0;
            }

            var startIndex = element.TryGetProperty("start_index", out var start) && start.TryGetInt32(out var parsedStart)
                ? parsedStart : 0;
            var total = element.TryGetProperty("total_messages", out var count) && count.TryGetInt32(out var parsedCount)
                ? parsedCount : 0;
            var messages = element.TryGetProperty("messages", out var pageMessages)
                && pageMessages.ValueKind == JsonValueKind.Array
                ? pageMessages : default;
            var pageCount = messages.ValueKind == JsonValueKind.Array ? Math.Min(messages.GetArrayLength(), 200) : 0;
            var range = pageCount == 0 ? "no messages" : $"messages {startIndex + 1}–{Math.Min(startIndex + pageCount, total)} of {total}";
            await _out.WriteLineAsync(Tui.TuiPalette.BoldPaint(
                $"Session {SanitizeHistoryText(cmd.SessionId, 64)} · {range}:"))
                .ConfigureAwait(false);
            if (messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray().Take(200))
                {
                    var role = message.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : null;
                    var content = message.TryGetProperty("content", out var contentValue) ? contentValue.GetString() : null;
                    await _out.WriteLineAsync($"[{SanitizeHistoryText(role, 16)}] {SanitizeHistoryText(content, 1000)}")
                        .ConfigureAwait(false);
                }
            }
            if (element.TryGetProperty("next_cursor", out var cursor)
                && cursor.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(cursor.GetString()))
            {
                var nextCursor = cursor.GetString()!;
                var visibleCursor = nextCursor.Length <= 2048
                    && nextCursor.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
                    ? nextCursor
                    : JsonSerializer.Serialize(nextCursor);
                await _out.WriteLineAsync($"Next cursor: {visibleCursor}").ConfigureAwait(false);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_history_error: {CliErrorText.ShortMessage(ex.Message)}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> SessionListAsync(SessionListCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var element = await _sessionClient.ListPageAsync(
                cmd.Cursor, cmd.Limit, cmd.Query, cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonEnvelope("session_list", element)).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync(Tui.TuiPalette.BoldPaint("Sessions:")).ConfigureAwait(false);
            if (element.TryGetProperty("sessions", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    var title = item.GetProperty("title").GetString();
                    var msgCount = item.TryGetProperty("message_count", out var mc) ? mc.GetInt32() : 0;
                    var safeId = SanitizeHistoryText(id, 64);
                    var safeTitle = SanitizeHistoryText(title, 100);
                    await _out.WriteLineAsync($"  • {Tui.TuiPalette.Accent(safeId)} - {safeTitle} ({msgCount} messages)").ConfigureAwait(false);
                }
            }
            if (element.TryGetProperty("next_cursor", out var cursor)
                && cursor.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(cursor.GetString()))
            {
                var nextCursor = cursor.GetString()!;
                var visibleCursor = nextCursor.Length <= 2048
                    && nextCursor.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
                    ? nextCursor
                    : JsonSerializer.Serialize(nextCursor);
                await _out.WriteLineAsync($"Next cursor: {visibleCursor}").ConfigureAwait(false);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_list_error: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> SessionSearchAsync(SessionSearchCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var element = await _sessionClient.SearchMessagesAsync(cmd.Query, cmd.Limit, cancellationToken)
                .ConfigureAwait(false);
            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonEnvelope("session_search", element)).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync(Tui.TuiPalette.BoldPaint($"Search results for: {SanitizeHistoryText(cmd.Query, 120)}"))
                .ConfigureAwait(false);
            if (element.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
            {
                foreach (var hit in hits.EnumerateArray())
                {
                    var sessionId = hit.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
                    var index = hit.TryGetProperty("message_index", out var messageIndex) ? messageIndex.GetInt32() : 0;
                    var role = hit.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : null;
                    var snippet = hit.TryGetProperty("snippet", out var snippetValue) ? snippetValue.GetString() : null;
                    await _out.WriteLineAsync(
                        $"  • {SanitizeHistoryText(sessionId, 64)} #{index} [{SanitizeHistoryText(role, 16)}] {SanitizeHistoryText(snippet, 240)}")
                        .ConfigureAwait(false);
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_search_error: {CliErrorText.ShortMessage(ex.Message)}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static string SanitizeHistoryText(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var output = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value)
        {
            if (char.IsControl(character)) continue;
            if (output.Length >= maximumLength) break;
            output.Append(character);
        }
        return output.ToString();
    }

}
