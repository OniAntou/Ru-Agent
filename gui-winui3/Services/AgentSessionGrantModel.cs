using System.Text.Json;

namespace RuApp.Services;

public sealed record AgentSessionGrantModel
{
    private const int MaximumSessionIdLength = 128;
    private const int MaximumScopes = 16;
    private const int MaximumScopeLength = 64;

    private static readonly HashSet<string> AllowedChatEventFields = new(StringComparer.Ordinal)
    {
        "type",
        "content",
        "session_id",
        "error_code",
        "status",
        "approval_id",
        "tool_id",
        "scope",
        "expires_at",
        "scopes",
    };

    private static readonly AgentSessionGrantModel EmptySentinel =
        new("invalid", Array.Empty<string>());

    public string SessionId { get; }
    public IReadOnlyList<string> Scopes { get; }

    public AgentSessionGrantModel(string sessionId, IEnumerable<string> scopes)
    {
        SessionId = ValidateSessionId(sessionId);
        Scopes = Array.AsReadOnly(ValidateScopes(scopes));
    }

    public static bool TryParse(JsonElement root, out AgentSessionGrantModel grant)
    {
        grant = EmptySentinel;
        try
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var observedFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!AllowedChatEventFields.Contains(property.Name)
                    || !observedFields.Add(property.Name))
                {
                    return false;
                }
            }

            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !string.Equals(typeElement.GetString(), "agent.session", StringComparison.Ordinal)
                || !root.TryGetProperty("session_id", out var sessionElement)
                || sessionElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("scopes", out var scopesElement)
                || scopesElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var scopes = new List<string>();
            foreach (var item in scopesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                scopes.Add(item.GetString() ?? string.Empty);
                if (scopes.Count > MaximumScopes)
                {
                    return false;
                }
            }

            grant = new AgentSessionGrantModel(sessionElement.GetString() ?? string.Empty, scopes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.Length > MaximumSessionIdLength
            || !string.Equals(sessionId, sessionId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Agent session ID is invalid.", nameof(sessionId));
        }

        return sessionId;
    }

    private static string[] ValidateScopes(IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var copy = scopes.ToArray();
        if (copy.Length > MaximumScopes
            || copy.Any(scope => string.IsNullOrWhiteSpace(scope)
                || scope.Length > MaximumScopeLength
                || !string.Equals(scope, scope.Trim(), StringComparison.Ordinal))
            || copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("Agent session scopes are invalid.", nameof(scopes));
        }

        Array.Sort(copy, StringComparer.Ordinal);
        return copy;
    }
}
