using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Harness;

/// <summary>
/// Safe, transient terminal information for one Agent run. This contract is intentionally
/// typed and bounded; it is not a free-form model or tool output channel.
/// </summary>
public sealed record AgentRunSummaryWire(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("verification_state")] string VerificationState,
    [property: JsonPropertyName("workspace_label")] string? WorkspaceLabel = null,
    [property: JsonPropertyName("files_scanned")] int? FilesScanned = null,
    [property: JsonPropertyName("files_changed")] int? FilesChanged = null,
    [property: JsonPropertyName("files_created")] int? FilesCreated = null,
    [property: JsonPropertyName("files_deleted")] int? FilesDeleted = null);

public static class AgentRunSummaryWireCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AgentRunSummaryWire Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("Agent summary payload is empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Agent summary payload must be an object.");
            }

            AgentRunSummaryWireValidator.ValidatePropertyNames(document.RootElement);
            AgentRunSummaryWireValidator.ValidateRequiredFields(document.RootElement);
            var summary = JsonSerializer.Deserialize<AgentRunSummaryWire>(json, JsonOptions)
                ?? throw new JsonException("Agent summary payload is null.");
            AgentRunSummaryWireValidator.Validate(summary);
            return summary;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new JsonException("Agent summary payload is invalid.", error);
        }
    }

    public static string Serialize(AgentRunSummaryWire summary)
    {
        AgentRunSummaryWireValidator.Validate(summary);
        return JsonSerializer.Serialize(summary, JsonOptions);
    }
}

public static class AgentRunSummaryWireValidator
{
    public const int MaximumWorkspaceLabelLength = 128;
    public const int MaximumCount = 1_000_000;

    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "status",
        "verification_state",
        "workspace_label",
        "files_scanned",
        "files_changed",
        "files_created",
        "files_deleted",
    };

    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal)
    {
        "succeeded",
        "failed",
        "cancelled",
        "interrupted",
    };

    private static readonly HashSet<string> VerificationStates = new(StringComparer.Ordinal)
    {
        "verified",
        "unverified",
        "not_applicable",
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly string[] ForbiddenFields =
    [
        "command", "arguments", "argv", "path", "cwd", "code", "diff", "output",
        "script", "stdout", "stderr", "body", "environment", "exception",
        "exception_message", "prompt", "reply", "content",
    ];

    public static void ValidatePropertyNames(JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new JsonException($"Duplicate agent summary field '{property.Name}'.");
            }

            if (!AllowedProperties.Contains(property.Name)
                || ForbiddenFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new JsonException($"Agent summary field '{property.Name}' is not allowed.");
            }
        }
    }

    public static void ValidateRequiredFields(JsonElement root)
    {
        foreach (var field in new[] { "status", "verification_state" })
        {
            if (!root.TryGetProperty(field, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Agent summary field '{field}' is required.");
            }
        }
    }

    public static void Validate(AgentRunSummaryWire summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!Statuses.Contains(summary.Status))
        {
            throw new JsonException("Agent summary status is invalid.");
        }

        if (!VerificationStates.Contains(summary.VerificationState))
        {
            throw new JsonException("Agent summary verification state is invalid.");
        }

        ValidateWorkspaceLabel(summary.WorkspaceLabel);
        ValidateCount(summary.FilesScanned, "files_scanned");
        ValidateCount(summary.FilesChanged, "files_changed");
        ValidateCount(summary.FilesCreated, "files_created");
        ValidateCount(summary.FilesDeleted, "files_deleted");
    }

    private static void ValidateWorkspaceLabel(string? label)
    {
        if (label is null)
        {
            return;
        }

        if (label.Length is < 1 or > MaximumWorkspaceLabelLength
            || string.IsNullOrWhiteSpace(label)
            || label is "." or ".."
            || label.EndsWith(".", StringComparison.Ordinal)
            || label.EndsWith(" ", StringComparison.Ordinal)
            || label.Any(char.IsControl)
            || label.Contains('/', StringComparison.Ordinal)
            || label.Contains('\\', StringComparison.Ordinal)
            || label.Contains(':', StringComparison.Ordinal))
        {
            throw new JsonException("Workspace label must be a safe display label.");
        }

        var deviceStem = label.Split('.', 2, StringSplitOptions.None)[0];
        if (ReservedDeviceNames.Contains(deviceStem))
        {
            throw new JsonException("Workspace label is reserved.");
        }
    }

    private static void ValidateCount(int? value, string fieldName)
    {
        if (value is < 0 or > MaximumCount)
        {
            throw new JsonException($"Agent summary field '{fieldName}' is outside the allowed range.");
        }
    }
}
