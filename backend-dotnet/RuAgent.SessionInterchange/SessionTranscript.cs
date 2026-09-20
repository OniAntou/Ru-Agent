namespace RuAgent.SessionInterchange;

/// <summary>What one step of a conversation is.</summary>
public enum TranscriptEntryKind
{
    UserMessage,
    AssistantMessage,
    Reasoning,
    ToolCall,
    ToolResult,
    System,
}

/// <summary>
/// One step of a conversation, in the order the source file recorded it.
/// </summary>
/// <remarks>
/// The shape is deliberately flat. Every partner format nests the same facts
/// differently (Claude Code puts a tool call inside an assistant message's content
/// array, Codex gives it a line of its own), and a transcript that kept the source's
/// nesting would be one format wearing another's clothes: the readers would have to
/// know which format they came from to walk it.
/// </remarks>
public sealed record TranscriptEntry(
    TranscriptEntryKind Kind,
    DateTimeOffset Timestamp,
    string? Text = null,
    string? ToolName = null,
    string? ToolCallId = null,
    string? ToolArguments = null,
    string? ToolResult = null);

/// <summary>
/// A conversation lifted out of one agent's file format, with nothing of that
/// format left on it.
/// </summary>
public sealed record SessionTranscript(
    string Id,
    string? Title,
    string? WorkingDirectory,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TranscriptEntry> Entries)
{
    /// <summary>How many turns a reader would count in a session list.</summary>
    public int MessageCount => Entries.Count(entry =>
        entry.Kind is TranscriptEntryKind.UserMessage or TranscriptEntryKind.AssistantMessage);
}

/// <summary>
/// One file an export produces.
/// </summary>
/// <remarks>
/// A format can need more than the transcript file itself. Codex keeps its picker
/// index in a sibling file, so an export that wrote only the rollout would land on
/// disk without ever appearing in the list of threads.
/// </remarks>
public sealed record SessionOutputFile(string FileName, string Content);
