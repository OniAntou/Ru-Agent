using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Todo;

/// <summary>One validated row, ready to store.</summary>
public sealed record TodoRow(string Content, string Status);

/// <summary>The outcome of validating a submitted list.</summary>
public sealed record TodoValidationResult(
    bool IsValid,
    IReadOnlyList<TodoRow> Items,
    string? ErrorCode)
{
    public static TodoValidationResult Ok(IReadOnlyList<TodoRow> items) => new(true, items, null);

    public static TodoValidationResult Fail(string code) =>
        new(false, Array.Empty<TodoRow>(), code);
}

/// <summary>
/// The rules a submitted task list must satisfy before it is stored.
/// </summary>
/// <remarks>
/// The list the model believes it wrote must equal the list that was stored, or the
/// model plans against a fiction. Each rule below closes a way the two can diverge:
/// a blank row cannot be referred to, two identical rows make the model count two
/// tasks where one exists, an unknown status is a state the view cannot draw, and
/// more than one active row reports a concurrency the default configuration does not
/// have while one row would be drawn emphasised.
///
/// Pure and separately testable: no store, no session, no clock.
/// </remarks>
public static class TodoListValidator
{
    public const string StatusPending = "pending";
    public const string StatusInProgress = "in_progress";
    public const string StatusCompleted = "completed";

    public const int MaximumItems = 64;
    public const int MaximumContentLength = 200;

    private static readonly HashSet<string> KnownStatuses =
        new([StatusPending, StatusInProgress, StatusCompleted], StringComparer.Ordinal);

    public static TodoValidationResult Validate(JsonNode? todos)
    {
        if (todos is not JsonArray array)
        {
            return TodoValidationResult.Fail("todo_list_required");
        }

        if (array.Count > MaximumItems)
        {
            return TodoValidationResult.Fail("todo_too_many");
        }

        var rows = new List<TodoRow>(array.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in array)
        {
            if (node is not JsonObject item)
            {
                return TodoValidationResult.Fail("todo_item_invalid");
            }

            var content = item["content"]?.GetValue<string>()?.Trim() ?? string.Empty;
            var status = item["status"]?.GetValue<string>() ?? string.Empty;

            if (content.Length == 0)
            {
                return TodoValidationResult.Fail("todo_content_empty");
            }

            if (content.Length > MaximumContentLength)
            {
                return TodoValidationResult.Fail("todo_content_too_long");
            }

            if (!KnownStatuses.Contains(status))
            {
                return TodoValidationResult.Fail("todo_status_unknown");
            }

            // Compared after trimming, so "Same" and "  Same  " are one task. The
            // model sending two spellings of one row means one task, and storing both
            // would make it count two.
            if (!seen.Add(content))
            {
                return TodoValidationResult.Fail("todo_content_duplicate");
            }

            if (string.Equals(status, StatusInProgress, StringComparison.Ordinal)
                && rows.Count(row => string.Equals(row.Status, StatusInProgress, StringComparison.Ordinal)) >= 1)
            {
                return TodoValidationResult.Fail("todo_multiple_active");
            }

            rows.Add(new TodoRow(content, status));
        }

        return TodoValidationResult.Ok(rows);
    }
}
