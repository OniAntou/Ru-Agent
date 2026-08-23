using System.Globalization;
using System.Text.Json;

namespace RuApp.Services;

public sealed record AgentApprovalModel(
    string ApprovalId,
    string SessionId,
    string ToolId,
    string SafeAction,
    string Scope,
    DateTimeOffset ExpiresAt,
    string? Preview = null)
{
    public const int MaxPreviewLength = 512;

    public bool IsExpired(DateTimeOffset? now = null) => ExpiresAt <= (now ?? DateTimeOffset.UtcNow);

    public static bool TryParse(JsonElement root, out AgentApprovalModel approval)
    {
        approval = default!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string approvalId = ReadBoundedString(root, "approval_id", 128);
        string sessionId = ReadBoundedString(root, "session_id", 128);
        string toolId = ReadBoundedString(root, "tool_id", 128);
        string safeAction = ReadBoundedString(root, "content", 256);
        string scope = ReadBoundedString(root, "scope", 256);
        string expiresText = ReadBoundedString(root, "expires_at", 64);

        if (string.IsNullOrWhiteSpace(approvalId)
            || string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(toolId)
            || string.IsNullOrWhiteSpace(safeAction)
            || string.IsNullOrWhiteSpace(scope)
            || !DateTimeOffset.TryParse(
                expiresText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt)
            || expiresAt <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        approval = new AgentApprovalModel(
            approvalId,
            sessionId,
            toolId,
            safeAction,
            scope,
            expiresAt,
            ReadOptionalPreview(root));
        return true;
    }

    private static string ReadBoundedString(JsonElement root, string propertyName, int maxLength)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        string value = property.GetString() ?? string.Empty;
        return value.Length <= maxLength ? value : string.Empty;
    }

    private static string? ReadOptionalPreview(JsonElement root)
    {
        if (!root.TryGetProperty("preview", out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string value = property.GetString() ?? string.Empty;
        if (value.Length == 0)
        {
            return null;
        }

        return value.Length <= MaxPreviewLength ? value : value[..MaxPreviewLength];
    }
}

public sealed record AgentApprovalResult(
    string ApprovalId,
    string SessionId,
    string Status,
    string SafeSummary);
