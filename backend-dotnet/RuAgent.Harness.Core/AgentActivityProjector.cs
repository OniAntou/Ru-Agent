using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core;

/// <summary>
/// Converts private model/tool lifecycle data into a deliberately small activity shape.
/// Only allowlisted scalar metadata can cross the sink boundary.
/// </summary>
public static class AgentActivityProjector
{
    private const int MaximumLine = 1_000_000;
    private const int MaximumCount = 1_000_000;
    private const int MaximumDurationMilliseconds = 86_400_000;
    private const int MaximumLeafLength = 128;
    private const int MaximumSymbolLength = 128;
    public const int MaximumDetailLength = 4_000;

    public static AgentActivity ProjectModelStep(
        ModelStepId modelStepId,
        AgentActivityState state,
        int? durationMs = null,
        string? safeCode = null,
        string? detail = null) =>
        new(
            ActivityId(modelStepId.Value),
            AgentActivityKind.Thinking,
            state,
            DurationMs: ClampDuration(durationMs),
            SafeCode: SafeCodeFor(state, safeCode),
            ToolDisplay: null,
            Detail: ClampDetail(detail));

    /// <summary>
    /// What the model says while it works, as a row of its own.
    /// </summary>
    /// <remarks>
    /// Narration is speech, not deliberation, so it does not share the thinking row it
    /// arrives beside: the thinking block is private reasoning the reader may never open,
    /// while this is the sentence they were meant to read. The row's own text is the
    /// sentence, so it carries no tool metadata and no duration.
    /// </remarks>
    public static AgentActivity ProjectNarration(ModelStepId modelStepId, string text) =>
        new(
            ActivityId(modelStepId.Value) + "_narration",
            AgentActivityKind.Narration,
            AgentActivityState.Completed,
            ToolDisplay: null,
            Detail: ClampDetail(text));

    public static AgentActivity ProjectTool(
        ToolProposal proposal,
        AgentActivityState state,
        ToolExecutionReceipt? receipt = null,
        int? durationMs = null,
        string? safeCode = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var op = ReadOp(proposal);
        var metadata = ToolActivityMetadataExtractor.Extract(proposal, receipt);
        var kind = (proposal.ToolId, op) switch
        {
            ("read", "list") or ("glob", _) => AgentActivityKind.Explore,
            ("grep", _) or ("web_search", _) => AgentActivityKind.Search,
            ("read", "read") => AgentActivityKind.Analyze,
            ("write", _) or ("edit", _) => AgentActivityKind.Edit,
            ("bash", _) => AgentActivityKind.Terminal,
            // Without this the tool fell to the default below and was reported as a bare
            // status line with its metadata deliberately stripped, so the one row that
            // explains the plan said nothing about it.
            ("todo_write", _) => AgentActivityKind.Todo,
            _ => AgentActivityKind.Status,
        };

        if (kind == AgentActivityKind.Status)
        {
            metadata = ToolActivityMetadata.Empty;
        }

        var toolDisplay = ToolDisplayFor(proposal);

        // A terminal command stays with its row for the row's whole life: while it waits,
        // while it runs, and after it settles. Dropping it on completion left "Completed
        // Terminal task" rows that named no command, so two commands in a row read as one
        // row printed twice — the command is the only thing telling them apart.
        var detail = kind == AgentActivityKind.Terminal
            ? ExtractTerminalCommand(proposal)
            : proposal.ToolId is "web_search" or "web_fetch"
                ? ExtractWebTarget(proposal)
            : proposal.ToolId == "mcp"
                ? ExtractMcpTarget(proposal)
            : state == AgentActivityState.Started && kind == AgentActivityKind.Edit
                ? ExtractEditPreview(proposal)
                : null;

        return new AgentActivity(
            ActivityId(proposal.ToolCallId.Value),
            kind,
            state,
            metadata.FileName,
            metadata.LineStart,
            metadata.LineEnd,
            metadata.Symbol,
            metadata.Count,
            metadata.Added,
            metadata.Removed,
            ClampDuration(durationMs),
            SafeCodeFor(state, safeCode ?? receipt?.Code),
            ToolDisplay: toolDisplay,
            Detail: detail);
    }

    /// <summary>"server › tool" for an MCP row; arguments stay out of the row.</summary>
    private static string? ExtractMcpTarget(ToolProposal proposal)
    {
        if (proposal.Arguments.ValueKind != JsonValueKind.Object) return null;
        string? Text(string name) => proposal.Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? new string(value.GetString()!.Where(c => !char.IsControl(c)).Take(64).ToArray())
            : null;
        return Text("op") switch
        {
            "list_servers" => "list servers",
            "list_tools" when Text("server") is { Length: > 0 } server => server + " › list tools",
            "call" when Text("server") is { Length: > 0 } server && Text("tool") is { Length: > 0 } tool => server + " › " + tool,
            _ => null,
        };
    }

    private static string? ExtractWebTarget(ToolProposal proposal)
    {
        var property = proposal.ToolId == "web_search" ? "query" : "url";
        if (proposal.Arguments.ValueKind != JsonValueKind.Object
            || !proposal.Arguments.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (proposal.ToolId == "web_fetch")
            return Uri.TryCreate(text, UriKind.Absolute, out var uri)
                && uri.Scheme is "https" or "http" ? uri.IdnHost : null;
        // Detail is redacted by AgentActivityLivePublisher before crossing the sink.
        // Reject an oversized query rather than cutting a possible credential in
        // half before the publisher's exact secret matcher can redact it.
        if (text.Length > 160) return null;
        var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length > 0 ? clean : null;
    }

    private static string? ExtractTerminalCommand(ToolProposal proposal)
    {
        if (!string.Equals(proposal.ToolId, "bash", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var arguments = proposal.Arguments;
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (arguments.TryGetProperty("script", out var script) && script.ValueKind == JsonValueKind.String)
            {
                return CleanCommand(script.GetString());
            }

            if (arguments.TryGetProperty("executable", out var exe) && exe.ValueKind == JsonValueKind.String)
            {
                var executable = exe.GetString();
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return null;
                }

                var parts = new List<string> { executable.Trim() };
                if (arguments.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in args.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { } arg)
                        {
                            parts.Add(arg);
                        }
                    }
                }

                return CleanCommand(string.Join(' ', parts));
            }

            if (arguments.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
            {
                return CleanCommand(cmd.GetString());
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? CleanCommand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = raw.Replace('\0', ' ').Trim();
        return cleaned.Length <= MaximumDetailLength ? cleaned : cleaned[..MaximumDetailLength];
    }

    /// <summary>
    /// Live two-pane preview for the Started frame: the exact old/new
    /// strings from the proposal, bounded and control-char safe. Lines stay
    /// escaped so the wire keeps its single-line detail shape; the client
    /// splits them back into two panes. Receipts carry only counts, so this
    /// is the only frame that can show the change itself.
    /// Write carries no old string, so its before pane stays empty and the
    /// after pane shows the written content.
    /// </summary>
    private static string? ExtractEditPreview(ToolProposal proposal)
    {
        try
        {
            var arguments = proposal.Arguments;
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (string.Equals(proposal.ToolId, "write", StringComparison.Ordinal))
            {
                if (!string.Equals(ReadOp(proposal), "write", StringComparison.Ordinal)
                    && ReadOp(proposal) is not null)
                {
                    return null;
                }

                if (!arguments.TryGetProperty("content", out var contentNode)
                    || contentNode.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var content = contentNode.GetString() ?? string.Empty;
                if (content.Length == 0)
                {
                    return null;
                }

                return RuAgent.Protocol.Harness.AgentEditPreviewCodec.Encode(string.Empty, content);
            }

            if (!string.Equals(proposal.ToolId, "edit", StringComparison.Ordinal))
            {
                return null;
            }

            if (!arguments.TryGetProperty("old_string", out var oldNode)
                || oldNode.ValueKind != JsonValueKind.String
                || !arguments.TryGetProperty("new_string", out var newNode)
                || newNode.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var oldString = oldNode.GetString() ?? string.Empty;
            var newString = newNode.GetString() ?? string.Empty;
            if (oldString.Length == 0 && newString.Length == 0)
            {
                return null;
            }

            return RuAgent.Protocol.Harness.AgentEditPreviewCodec.Encode(oldString, newString);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Projects completed context compaction. Only the bounded number of covered context
    /// entries and the elapsed time cross the sink boundary; summary text never does.
    /// </summary>
    public static AgentActivity ProjectCompaction(
        string runId,
        long sequence,
        AgentActivityState state,
        int? coveredEntries = null,
        int? durationMs = null,
        string? safeCode = null) =>
        new(
            CompactionActivityId(runId, sequence),
            AgentActivityKind.Compaction,
            state,
            Count: ClampCount(coveredEntries),
            DurationMs: ClampDuration(durationMs),
            SafeCode: SafeCodeFor(state, safeCode),
            ToolDisplay: null);

    /// <summary>
    /// The compaction activity identity shared by the live sink, durable replay, and the
    /// client event synthesis, so one compaction cannot render twice.
    /// </summary>
    public static string CompactionActivityId(string runId, long sequence) =>
        $"act_compaction_{runId}_{sequence}";

    /// <summary>
    /// Projects a tool retry attempt. The activity carries the same bounded metadata as
    /// the tool it belongs to, plus the attempt number, and never the failure text.
    /// </summary>
    public static AgentActivity ProjectRetry(
        ToolProposal proposal,
        int attempt,
        AgentActivityState state = AgentActivityState.Completed,
        int? durationMs = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var metadata = ToolActivityMetadataExtractor.Extract(proposal, receipt: null);
        return new AgentActivity(
            ActivityId(proposal.ToolCallId.Value) + "_retry",
            AgentActivityKind.Retry,
            state,
            metadata.FileName,
            metadata.LineStart,
            metadata.LineEnd,
            metadata.Symbol,
            ClampCount(attempt),
            metadata.Added,
            metadata.Removed,
            ClampDuration(durationMs),
            SafeCodeFor(state, "retrying"),
            ToolDisplay: ToolDisplayFor(proposal));
    }

    /// <summary>
    /// Projects a model-step retry: the run is stepping again after a bounded
    /// recovery note, so unlike the tool-proposal overload there is no tool to
    /// describe. The row keeps the same retry kind, count and safe code.
    /// </summary>
    public static AgentActivity ProjectRetry(
        ModelStepId modelStepId,
        int attempt,
        AgentActivityState state = AgentActivityState.Completed,
        int? durationMs = null) =>
        new(
            ActivityId(modelStepId.Value) + "_retry",
            AgentActivityKind.Retry,
            state,
            Count: ClampCount(attempt),
            DurationMs: ClampDuration(durationMs),
            SafeCode: SafeCodeFor(state, "retrying"));

    public static AgentActivity ProjectStatus(
        string activityId,
        AgentActivityState state,
        string? safeCode = null,
        string? detail = null) =>
        new(
            activityId,
            AgentActivityKind.Status,
            state,
            SafeCode: SafeCodeFor(state, safeCode),
            ToolDisplay: null,
            Detail: ClampDetail(detail));

    /// <summary>
    /// Projects the agent's task list after a write.
    /// </summary>
    /// <remarks>
    /// The whole list travels, never a delta. The model replaces the list wholesale, so
    /// the view's state is always exactly what the last write said, and a viewer that
    /// missed an earlier activity still draws the truth.
    /// </remarks>
    public static AgentActivity ProjectTodos(
        string activityId,
        IReadOnlyList<AgentTodoRow> todos)
    {
        ArgumentNullException.ThrowIfNull(activityId);
        ArgumentNullException.ThrowIfNull(todos);

        return new(
            activityId,
            AgentActivityKind.Todo,
            AgentActivityState.Completed,
            Count: todos.Count,
            SafeCode: "succeeded",
            ToolDisplay: null,
            Detail: null,
            Todos: todos.ToArray());
    }

    public static AgentActivity ProjectDropped(
        ModelStepId modelStepId,
        bool toolFrames,
        int droppedCount)
    {
        var boundedCount = droppedCount is > 0
            ? Math.Min(droppedCount, MaximumCount)
            : 1;
        var phase = toolFrames ? "tool" : "assistant";
        return new AgentActivity(
            $"{ActivityId(modelStepId.Value)}_{phase}_dropped",
            AgentActivityKind.Status,
            AgentActivityState.Completed,
            Count: boundedCount,
            SafeCode: "frames_dropped",
            ToolDisplay: null);
    }

    /// <summary>
    /// A transient sink is optional and must never be allowed to change run semantics.
    /// </summary>
    public static void PublishSafe(
        IAgentActivitySink? sink,
        RunId runId,
        AgentActivity activity)
    {
        if (sink is null)
        {
            return;
        }

        try
        {
            sink.Publish(runId, activity);
        }
        catch
        {
            // Activity is observational only. A disconnected or invalid live sink must
            // never fail a model/tool step or alter durable harness state. Count the
            // loss so a silent sink cannot hide forever (see DroppedPublishCount).
            Interlocked.Increment(ref _droppedPublishCount);
        }
    }

    /// <summary>
    /// Number of activity frames lost to sink failures via <see cref="PublishSafe"/>.
    /// Core stays dependency-free, so observability is a counter, not a log call.
    /// </summary>
    public static long DroppedPublishCount => Volatile.Read(ref _droppedPublishCount);

    private static long _droppedPublishCount;

    private static string? ToolDisplayFor(ToolProposal proposal) =>
        (proposal.ToolId, ReadOp(proposal)) switch
        {
            ("read", "list") or ("glob", _) => "Explore",
            ("grep", _) or ("web_search", _) => "Search",
            ("read", "read") => "View",
            ("write", "write") => "Write",
            ("edit", _) => "Edit",
            ("write", "delete") => "Delete",
            ("write", "restore") => "Restore",
            ("bash", _) => "Terminal",
            ("web_fetch", _) => "Fetch",
            ("read", "help") => "Docs",
            ("todo_write", _) => "Plan",
            _ => null,
        };

    private static string? ReadOp(ToolProposal proposal)
    {
        try
        {
            if (proposal.Arguments.TryGetProperty("op", out var op)
                && op.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return op.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    internal static string ActivityId(string sourceId)
    {
        var separator = sourceId.IndexOf('_');
        var suffix = separator >= 0 && separator + 1 < sourceId.Length
            ? sourceId[(separator + 1)..]
            : sourceId;
        return $"act_{suffix}";
    }

    private static string SafeCodeFor(AgentActivityState state, string? code) =>
        IsSafeCode(code)
            ? code!
            : state switch
            {
                AgentActivityState.Started => "in_progress",
                AgentActivityState.Waiting => "waiting",
                AgentActivityState.Failed => "failed",
                _ => "succeeded",
            };

    private static int? ClampDuration(int? durationMs) =>
        durationMs is >= 0 and <= MaximumDurationMilliseconds ? durationMs : null;

    private static string? ClampDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var trimmed = detail.Trim();
        if (trimmed.Contains('\0'))
        {
            return null;
        }

        return trimmed.Length <= MaximumDetailLength ? trimmed : trimmed[..MaximumDetailLength];
    }    private static int? ClampCount(int? value) =>
        value is >= 0 and <= MaximumCount ? value : null;

    private static bool IsSafeCode(string? value) =>
        value is { Length: >= 1 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '_'
                or '.');

    private sealed record ToolActivityMetadata(
        string? FileName,
        int? LineStart,
        int? LineEnd,
        string? Symbol,
        int? Count,
        int? Added,
        int? Removed)
    {
        public static ToolActivityMetadata Empty { get; } = new(null, null, null, null, null, null, null);
    }

    private static class ToolActivityMetadataExtractor
    {
        public static ToolActivityMetadata Extract(
            ToolProposal proposal,
            ToolExecutionReceipt? receipt)
        {
            try
            {
                var arguments = proposal.Arguments;
                var op = ReadOp(proposal);
                // Bounded leaf name for file AND folder targets (list/glob show
                // the listed folder instead of a bare verb). Same safety rules.
                var fileName = proposal.ToolId is "read" or "write" or "edit" or "glob"
                    ? ReadSafeFileName(arguments, "path")
                    : null;
                var (lineStart, lineEnd) = ReadLineRange(arguments);
                var symbol = (proposal.ToolId, op) is ("grep", _)
                    ? ReadSafeSymbol(arguments, "query")
                    : null;
                var count = ReadReceiptInt(receipt, "count");
                var added = ReadReceiptInt(receipt, "added");
                var removed = ReadReceiptInt(receipt, "removed");

                if (proposal.ToolId is "write" or "edit")
                {
                    added ??= ReadLineBreakCount(arguments, "new_string")
                        ?? ReadLineBreakCount(arguments, "content");
                    removed ??= ReadLineBreakCount(arguments, "old_string");
                }

                if (proposal.ToolId is "read" or "edit" or "write")
                {
                    (lineStart, lineEnd) = PreferReceiptLineRange(receipt, lineStart, lineEnd);
                }

                return new ToolActivityMetadata(
                    fileName,
                    lineStart,
                    lineEnd,
                    symbol,
                    ClampCount(count),
                    ClampCount(added),
                    ClampCount(removed));
            }
            catch
            {
                return ToolActivityMetadata.Empty;
            }
        }

        private static (int? Start, int? End) PreferReceiptLineRange(
            ToolExecutionReceipt? receipt,
            int? start,
            int? end)
        {
            if (start.HasValue && end.HasValue)
            {
                return (start, end);
            }

            var receiptStart = ReadReceiptInt(receipt, "line_start");
            var receiptEnd = ReadReceiptInt(receipt, "line_end");
            return IsLineRange(receiptStart, receiptEnd)
                ? (receiptStart, receiptEnd)
                : (null, null);
        }

        private static string? ReadSafeFileName(JsonElement arguments, string propertyName)
        {
            if (!TryGetString(arguments, propertyName, out var path)
                || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            var candidate = slash >= 0 ? normalized[(slash + 1)..] : normalized;
            if (candidate.Length is < 1 or > MaximumLeafLength
                || candidate is "." or ".."
                || candidate.EndsWith(".", StringComparison.Ordinal)
                || candidate.EndsWith(" ", StringComparison.Ordinal)
                || candidate.Any(char.IsControl)
                || candidate.Contains(':', StringComparison.Ordinal))
            {
                return null;
            }

            var deviceStem = candidate.Split('.', 2, StringSplitOptions.None)[0];
            if (deviceStem is "CON" or "PRN" or "AUX" or "NUL"
                || (deviceStem.Length == 4
                    && (deviceStem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                        || deviceStem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && deviceStem[3] is >= '1' and <= '9'))
            {
                return null;
            }

            return candidate;
        }

        private static string? ReadSafeSymbol(JsonElement arguments, string propertyName)
        {
            if (!TryGetString(arguments, propertyName, out var symbol)
                || symbol.Length is < 1 or > MaximumSymbolLength
                || !(symbol[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_'))
            {
                return null;
            }

            for (var index = 1; index < symbol.Length; index++)
            {
                var character = symbol[index];
                if (!(character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
                    or (>= '0' and <= '9') or '_' or '.' or ':' or '-'))
                {
                    return null;
                }
            }

            return symbol;
        }

        private static (int? Start, int? End) ReadLineRange(JsonElement arguments)
        {
            var start = ReadInt(arguments, "line_start");
            var end = ReadInt(arguments, "line_end");
            return IsLineRange(start, end) ? (start, end) : (null, null);
        }

        private static bool IsLineRange(int? start, int? end) =>
            start is >= 1 and <= MaximumLine
            && end is >= 1 and <= MaximumLine
            && end >= start;

        private static int? ReadLineBreakCount(JsonElement arguments, string propertyName)
        {
            if (!TryGetString(arguments, propertyName, out var value))
            {
                return null;
            }

            var count = value.Count(character => character == '\n');
            return count <= MaximumCount ? count : null;
        }

        private static int? ReadReceiptInt(ToolExecutionReceipt? receipt, string propertyName)
        {
            if (receipt is null || receipt.ModelContent.IsEmpty)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(receipt.ModelContent.ToArray());
                return ReadInt(document.RootElement, propertyName);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static int? ReadInt(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out var number))
            {
                return null;
            }

            return number;
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string value)
        {
            value = string.Empty;
            if (!element.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString() ?? string.Empty;
            return true;
        }

        private static int? ClampCount(int? value) =>
            value is >= 0 and <= MaximumCount ? value : null;
    }
}
