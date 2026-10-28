using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore
{
    private static SessionDocument? ParseDocument(byte[] bytes, string sessionId)
    {
        try
        {
            var root = JsonNode.Parse(bytes) as JsonObject;
            if (root is null || root["messages"] is not JsonArray messagesNode)
            {
                return null;
            }

            var storedId = ReadString(root, "id", sessionId);
            if (!IsValidSessionId(storedId) || !string.Equals(storedId, sessionId, StringComparison.Ordinal))
            {
                return null;
            }

            var messages = new List<SessionMessage>();
            foreach (var node in messagesNode)
            {
                if (node is not JsonObject messageNode)
                {
                    continue;
                }

                var role = ReadString(messageNode, "role", string.Empty);
                var content = ReadNullableString(messageNode, "content");
                if ((role == "user" || role == "ai") && content is not null)
                {
                    var message = new SessionMessage(
                        role,
                        content,
                        ReadNullableString(messageNode, "source_message_id"),
                        ReadNullableString(messageNode, "source_run_id"));
                    if (IsValidMessage(message))
                    {
                        messages.Add(message);
                    }
                }
            }

            if (!TryReadSourceLedger(root, messages, out var ledger))
            {
                return null;
            }

            var storedTitle = ReadString(root, "title", DefaultTitle);
            if (storedTitle.Length is 0 or > MaximumTitleCharacters
                || storedTitle.Any(char.IsControl))
            {
                storedTitle = DefaultTitle;
            }

            var parentSessionId = ReadNullableString(root, "parent_session_id");
            var forkedFromMessageId = ReadNullableString(root, "forked_from_message_id");
            var clientSource = ReadNullableString(root, "client_source");

            return new SessionDocument(
                storedId,
                storedTitle,
                ReadTimestamp(root, "created_at"),
                ReadTimestamp(root, "updated_at"),
                messages,
                ledger.Count == 0 ? null : ledger,
                parentSessionId,
                forkedFromMessageId,
                TryReadTimestamp(root, "last_agent_activity_at"),
                clientSource is "gui" or "cli" ? clientSource : null);
        }
        catch (JsonException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    private static string ReadString(JsonObject node, string key, string fallback) =>
        ReadNullableString(node, key) ?? fallback;

    private static string? ReadNullableString(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static DateTimeOffset ReadTimestamp(JsonObject node, string key)
    {
        var raw = ReadNullableString(node, key);
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : DateTimeOffset.UnixEpoch;
    }

    private static DateTimeOffset? TryReadTimestamp(JsonObject node, string key)
    {
        var raw = ReadNullableString(node, key);
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }
}
