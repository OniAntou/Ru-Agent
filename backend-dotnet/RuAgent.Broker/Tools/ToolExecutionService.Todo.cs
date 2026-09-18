using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Todo;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private readonly TodoStore _todos = new();

    /// <summary>
    /// Replaces the session's task list with the one the model sent.
    /// </summary>
    /// <remarks>
    /// A rejected list never reaches the store, so a malformed write cannot erase the
    /// plan the user is reading. The summary returned to the model reports counts
    /// rather than the list, because it already sent the list and what it cannot derive
    /// is how much is left.
    /// </remarks>
    private ValueTask<ToolExecutionOutcome> ExecuteTodoWriteAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (string.IsNullOrWhiteSpace(context.SessionId))
        {
            return ValueTask.FromResult(ToolExecutionOutcome.Failed(
                "todo_session_required",
                "The task list belongs to a session and this call carried none."));
        }

        var validation = TodoListValidator.Validate(arguments["todos"]);
        if (!validation.IsValid)
        {
            return ValueTask.FromResult(ToolExecutionOutcome.Failed(
                validation.ErrorCode!,
                DescribeRejection(validation.ErrorCode!)));
        }

        _todos.Replace(context.SessionId, validation.Items);

        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["todos"] = TodoPayload(validation.Items),
                ["summary"] = TodoStore.Describe(validation.Items),
            },
            ToolVerificationStates.NotRequired,
            TodoStore.Describe(validation.Items)));
    }

    /// <summary>
    /// What the model is told when its list is rejected.
    /// </summary>
    /// <remarks>
    /// Each rejection names the rule it broke, so the next attempt can fix it instead of
    /// resending the same list. The code travels beside this as the stable handle.
    /// </remarks>
    private static string DescribeRejection(string errorCode) => errorCode switch
    {
        "todo_list_required" => "A todo_write call needs a todos array.",
        "todo_too_many" => $"A task list holds at most {TodoListValidator.MaximumItems} items.",
        "todo_item_invalid" => "Every task must be an object with content and status.",
        "todo_content_empty" => "Every task needs a non-empty content line.",
        "todo_content_too_long" =>
            $"A task line holds at most {TodoListValidator.MaximumContentLength} characters.",
        "todo_status_unknown" => "Status must be pending, in_progress, or completed.",
        "todo_content_duplicate" => "Two tasks cannot share the same content.",
        "todo_multiple_active" => "At most one task may be in_progress.",
        _ => "The task list was rejected.",
    };

    private static JsonArray TodoPayload(IReadOnlyList<TodoRow> rows) =>
        new(rows.Select(row => (JsonNode)new JsonObject
        {
            ["content"] = row.Content,
            ["status"] = row.Status,
        }).ToArray());

    /// <summary>The task list for a session, for a caller that needs to read it.</summary>
    public IReadOnlyList<TodoRow> ReadTodos(string sessionId) => _todos.Read(sessionId);
}
