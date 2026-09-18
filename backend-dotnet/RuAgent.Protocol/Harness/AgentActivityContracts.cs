using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Harness;

public enum AgentActivityKind
{
    Thinking,
    Explore,
    Search,
    Analyze,
    Edit,
    Terminal,
    Compaction,
    Retry,
    Status,
    Todo,
}

public enum AgentActivityState
{
    Started,
    Completed,
    Failed,
    Waiting,
}

/// <summary>
/// The statuses a task list row may carry.
/// </summary>
public static class AgentTodoStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Pending, InProgress, Completed],
        StringComparer.Ordinal);
}

/// <summary>
/// One row of the agent's task list.
/// </summary>
/// <remarks>
/// Deliberately minimal: a display line and a three-state status. The list is replaced
/// wholesale on every write, so a row needs no stable identity, and an id would be one
/// more field for the model to get wrong. Status is a plain string rather than an enum
/// so an unrecognised value is a parse failure instead of a silent fallback.
/// </remarks>
public sealed record AgentTodoWire(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("status")] string Status);

public sealed record AgentActivityWire(
    [property: JsonPropertyName("activity_id")] string ActivityId,
    [property: JsonPropertyName("kind")] AgentActivityKind Kind,
    [property: JsonPropertyName("state")] AgentActivityState State,
    [property: JsonPropertyName("file_name")] string? FileName = null,
    [property: JsonPropertyName("line_start")] int? LineStart = null,
    [property: JsonPropertyName("line_end")] int? LineEnd = null,
    [property: JsonPropertyName("symbol")] string? Symbol = null,
    [property: JsonPropertyName("count")] int? Count = null,
    [property: JsonPropertyName("added")] int? Added = null,
    [property: JsonPropertyName("removed")] int? Removed = null,
    [property: JsonPropertyName("duration_ms")] int? DurationMs = null,
    [property: JsonPropertyName("safe_code")] string SafeCode = "succeeded",
    [property: JsonPropertyName("tool_display")] string? ToolDisplay = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("todos")] IReadOnlyList<AgentTodoWire>? Todos = null);

public static class AgentActivityWireCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AgentActivityWire Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("Activity payload is empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Activity payload must be an object.");
            }

            AgentActivityWireValidator.ValidatePropertyNames(document.RootElement);
            AgentActivityWireValidator.ValidateRequiredFields(document.RootElement);
            AgentActivityWireValidator.ValidateEnumTokens(document.RootElement);
            var activity = JsonSerializer.Deserialize<AgentActivityWire>(json, JsonOptions)
                ?? throw new JsonException("Activity payload is null.");
            AgentActivityWireValidator.Validate(activity);
            return activity;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new JsonException("Activity payload is invalid.", ex);
        }
    }

    public static string Serialize(AgentActivityWire activity)
    {
        AgentActivityWireValidator.Validate(activity);
        return JsonSerializer.Serialize(activity, JsonOptions);
    }
}

public static class AgentActivityWireValidator
{
    public const int MaximumActivityIdLength = 128;
    public const int MaximumFileNameLength = 128;
    public const int MaximumSymbolLength = 128;
    public const int MaximumLine = 1_000_000;
    public const int MaximumCount = 1_000_000;
    public const int MaximumDurationMilliseconds = 86_400_000;
    public const int MaximumSafeCodeLength = 64;
    public const int MaximumToolDisplayLength = 32;
    public const int MaximumDetailLength = 4_000;
    public const int MaximumTodos = 64;
    public const int MaximumTodoContentLength = 200;

    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "activity_id",
        "kind",
        "state",
        "file_name",
        "line_start",
        "line_end",
        "symbol",
        "count",
        "added",
        "removed",
        "duration_ms",
        "safe_code",
        "tool_display",
        "detail",
        "todos",
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    };

    private static readonly string[] ForbiddenFields =
    [
        "command",
        "arguments",
        "argv",
        "path",
        "code",
        "diff",
        "output",
        "script",
        "stdout",
        "stderr",
        "body",
        "environment",
        "exception",
        "exception_message",
    ];

    public static void ValidatePropertyNames(JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new JsonException($"Duplicate activity field '{property.Name}'.");
            }

            if (!AllowedProperties.Contains(property.Name)
                || ForbiddenFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new JsonException($"Activity field '{property.Name}' is not allowed.");
            }
        }
    }

    public static void ValidateEnumTokens(JsonElement root)
    {
        foreach (var propertyName in new[] { "kind", "state" })
        {
            if (root.TryGetProperty(propertyName, out var value)
                && value.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Activity field '{propertyName}' must be a string token.");
            }
        }

        ValidateTodoShape(root);
    }

    /// <summary>
    /// Rejects any key on a task list row besides content and status.
    /// </summary>
    /// <remarks>
    /// The top-level allowlist does not reach into nested objects, so without this a row
    /// could carry a field the contract does not define and the deserializer would
    /// quietly drop it. A dropped `priority` is a field the model believes is working,
    /// which is worse than a rejection it can see.
    /// </remarks>
    private static void ValidateTodoShape(JsonElement root)
    {
        if (!root.TryGetProperty("todos", out var todos))
        {
            return;
        }

        if (todos.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Activity field 'todos' must be an array.");
        }

        foreach (var row in todos.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Activity todo row must be an object.");
            }

            foreach (var property in row.EnumerateObject())
            {
                if (property.Name is not ("content" or "status"))
                {
                    throw new JsonException($"Activity todo field '{property.Name}' is not allowed.");
                }
            }
        }
    }

    public static void ValidateRequiredFields(JsonElement root)
    {
        if (!root.TryGetProperty("activity_id", out var activityId)
            || activityId.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Activity id is required.");
        }

        foreach (var propertyName in new[] { "kind", "state" })
        {
            if (!root.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Activity field '{propertyName}' is required.");
            }
        }
    }

    public static void Validate(AgentActivityWire activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (string.IsNullOrWhiteSpace(activity.ActivityId)
            || activity.ActivityId.Length > MaximumActivityIdLength
            || !activity.ActivityId.StartsWith("act_", StringComparison.Ordinal)
            || !IsSafeToken(activity.ActivityId.AsSpan(4), allowDash: false))
        {
            throw new JsonException("Activity id is invalid.");
        }

        if (!Enum.IsDefined(activity.Kind) || !Enum.IsDefined(activity.State))
        {
            throw new JsonException("Activity kind or state is invalid.");
        }

        ValidateFileName(activity.FileName);
        ValidateSymbol(activity.Symbol);

        if ((activity.LineStart is null) != (activity.LineEnd is null))
        {
            throw new JsonException("Line range must contain both boundaries.");
        }

        if (activity.LineStart is { } lineStart
            && (lineStart < 1 || lineStart > MaximumLine))
        {
            throw new JsonException("Line start is outside the allowed range.");
        }

        if (activity.LineEnd is { } lineEnd
            && (lineEnd < 1 || lineEnd > MaximumLine
                || activity.LineStart is null
                || lineEnd < activity.LineStart.Value))
        {
            throw new JsonException("Line end is outside the allowed range.");
        }

        ValidateNonNegative(activity.Count, MaximumCount, "count");
        ValidateNonNegative(activity.Added, MaximumCount, "added");
        ValidateNonNegative(activity.Removed, MaximumCount, "removed");
        ValidateNonNegative(activity.DurationMs, MaximumDurationMilliseconds, "duration_ms");

        if (string.IsNullOrWhiteSpace(activity.SafeCode)
            || activity.SafeCode.Length > MaximumSafeCodeLength
            || !IsSafeToken(activity.SafeCode.AsSpan(), allowDash: false, allowDot: true))
        {
            throw new JsonException("Safe code is invalid.");
        }

        ValidateToolDisplay(activity.ToolDisplay);
        ValidateDetail(activity.Detail);
        ValidateTodos(activity.Todos);
    }

    /// <summary>
    /// Validates the task list carried by a todo activity.
    /// </summary>
    /// <remarks>
    /// Each row is checked rather than trusted. The list is model-authored, so it is
    /// input like any other tool argument: a blank line cannot be displayed, an
    /// unrecognised status cannot be drawn, and an unbounded list would be an
    /// unbounded allocation in the view.
    /// </remarks>
    private static void ValidateTodos(IReadOnlyList<AgentTodoWire>? todos)
    {
        if (todos is null)
        {
            return;
        }

        if (todos.Count > MaximumTodos)
        {
            throw new JsonException("Activity todo list is too long.");
        }

        foreach (var todo in todos)
        {
            if (todo is null
                || string.IsNullOrWhiteSpace(todo.Content)
                || todo.Content.Length > MaximumTodoContentLength
                || !AgentTodoStatuses.All.Contains(todo.Status))
            {
                throw new JsonException("Activity todo item is invalid.");
            }
        }
    }

    private static void ValidateFileName(string? fileName)
    {
        if (fileName is null)
        {
            return;
        }

        if (fileName.Length is < 1 or > MaximumFileNameLength
            || string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || fileName.EndsWith(".", StringComparison.Ordinal)
            || fileName.EndsWith(" ", StringComparison.Ordinal)
            || fileName.Any(char.IsControl)
            || fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal)
            || fileName.Contains(':', StringComparison.Ordinal))
        {
            throw new JsonException("File name must be a safe basename.");
        }

        var deviceStem = fileName.Split('.', 2, StringSplitOptions.None)[0];
        if (ReservedDeviceNames.Contains(deviceStem))
        {
            throw new JsonException("File name is reserved.");
        }
    }

    private static void ValidateSymbol(string? symbol)
    {
        if (symbol is null)
        {
            return;
        }

        if (symbol.Length is < 1 or > MaximumSymbolLength
            || !IsSafeSymbol(symbol.AsSpan()))
        {
            throw new JsonException("Symbol is invalid.");
        }
    }

    private static void ValidateToolDisplay(string? toolDisplay)
    {
        if (toolDisplay is null)
        {
            return;
        }

        if (toolDisplay.Length is < 1 or > MaximumToolDisplayLength)
        {
            throw new JsonException("Tool display is invalid.");
        }

        foreach (var c in toolDisplay)
        {
            var ok = (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c is ' ' or '_' or '-' or '.';
            if (!ok)
            {
                throw new JsonException("Tool display is invalid.");
            }
        }
    }

    private static void ValidateDetail(string? detail)
    {
        if (detail is null)
        {
            return;
        }

        if (detail.Length is < 1 or > MaximumDetailLength || detail.Contains('\0'))
        {
            throw new JsonException("Detail is invalid.");
        }
    }

    private static void ValidateNonNegative(int? value, int maximum, string fieldName)
    {
        if (value is { } numeric && (numeric < 0 || numeric > maximum))
        {
            throw new JsonException($"{fieldName} is outside the allowed range.");
        }
    }

    private static bool IsSafeSymbol(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !(IsAsciiLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!(IsAsciiLetter(character)
                || char.IsAsciiDigit(character)
                || character is '_' or '.' or ':' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeToken(ReadOnlySpan<char> value, bool allowDash, bool allowDot = false)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(IsAsciiLetter(character)
                || char.IsAsciiDigit(character)
                || character == '_'
                || (allowDash && character == '-')
                || (allowDot && character == '.')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
