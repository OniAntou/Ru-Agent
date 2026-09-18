namespace RuApp.Models
{
    /// <summary>
    /// One row of the agent's task list, as the transcript shows it.
    /// </summary>
    /// <remarks>
    /// Read-only by design: the agent plans and the user watches, so there is no editing
    /// affordance and no command to issue. The marks are the same three glyphs a
    /// checklist uses anywhere else, and the status name is separate from the glyph
    /// because a screen reader has to read the state, not the symbol.
    /// </remarks>
    public sealed class TodoItemModel
    {
        public const string Pending = "pending";
        public const string InProgress = "in_progress";
        public const string Completed = "completed";

        public TodoItemModel(string content, string status)
        {
            Content = content ?? string.Empty;
            Status = status ?? string.Empty;
        }

        public string Content { get; }

        public string Status { get; }

        public bool IsPending => Status == Pending;

        public bool IsInProgress => Status == InProgress;

        public bool IsCompleted => Status == Completed;

        /// <summary>Segoe Fluent Icons glyph for the row's state.</summary>
        public string MarkGlyph => Status switch
        {
            Completed => "\uE73E",
            InProgress => "\uE73A",
            _ => "\uE739",
        };

        /// <summary>The state in words, for the accessibility name.</summary>
        public string StatusName => Status switch
        {
            Completed => "Completed",
            InProgress => "In progress",
            _ => "Pending",
        };

        /// <summary>What a screen reader announces for the row.</summary>
        public string AccessibleName => $"{StatusName}: {Content}";
    }
}
