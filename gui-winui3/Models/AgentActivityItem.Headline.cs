using RuAgent.Protocol.Harness;

namespace RuApp.Models;

/// <summary>
/// The parts of a row's one-line headline: a verb for what happened, the subject it
/// happened to, and a short tail of numbers. <see cref="DisplayText"/> stays the whole
/// sentence for tooltips, screen readers and the compact summary.
/// </summary>
public sealed partial class AgentActivityItem
{
    private int? _exitCode;

    /// <summary>The process exit code a terminal row reported, when it reported one.</summary>
    public int? ExitCode
    {
        get => _exitCode;
        private set => SetProperty(ref _exitCode, value);
    }

    /// <summary>A non-zero exit is what a reader scans a run of commands for.</summary>
    public bool HasFailingExitCode => Kind == AgentActivityKind.Terminal && ExitCode is { } code && code != 0;

    public string ExitCodeText => ExitCode is { } code ? $"exit {code}" : string.Empty;

    /// <summary>
    /// What happened, in the tense of the row's state: "Running" while it runs, "Ran"
    /// once it settled. Rows that exist to explain themselves (status, a failed model
    /// call) keep their whole sentence here and have no subject.
    /// </summary>
    public string RowVerb
    {
        get
        {
            if (Kind is AgentActivityKind.Narration or AgentActivityKind.Steer or AgentActivityKind.Status or AgentActivityKind.Retry)
                return DisplayText;
            if (State == AgentActivityState.Waiting) return WaitingText;
            if (State == AgentActivityState.Failed && Kind == AgentActivityKind.Thinking) return FailedText;
            var live = State == AgentActivityState.Started;
            return Kind switch
            {
                AgentActivityKind.Thinking => live ? "Thinking" : "Thought",
                AgentActivityKind.Explore => ToolDisplay switch
                {
                    "List" => live ? "Listing" : FoundNothing ? "Listed an empty folder" : "Listed",
                    "Glob" => live ? "Finding files" : FoundNothing ? "Found no files" : "Found files",
                    _ => live ? "Exploring" : "Explored",
                },
                AgentActivityKind.Search => live ? "Searching" : "Searched",
                AgentActivityKind.Analyze => ToolDisplay == "Docs"
                    ? (live ? "Reading the docs" : "Read the docs")
                    : (live ? "Reading" : "Read"),
                AgentActivityKind.Edit => ToolDisplay switch
                {
                    "Delete" => live ? "Deleting" : "Deleted",
                    "Restore" => live ? "Restoring" : "Restored",
                    "Write" => live ? "Writing" : "Wrote",
                    _ => live ? "Editing" : "Edited",
                },
                AgentActivityKind.Terminal => ToolDisplay switch
                {
                    "Output" => live ? "Reading output" : "Read output",
                    "Stop" => live ? "Stopping" : "Stopped",
                    _ => live ? "Running" : "Ran",
                },
                AgentActivityKind.Web => ToolDisplay switch
                {
                    "Fetch" => live ? "Fetching" : "Fetched",
                    "Browser" => BrowserVerb(BrowserAction, live),
                    _ => live ? "Searching the web" : "Searched the web",
                },
                AgentActivityKind.Mcp => live ? "Calling" : "Called",
                AgentActivityKind.Computer => live ? "Using the computer" : "Used the computer",
                AgentActivityKind.Skill => Detail?.StartsWith("search: ", StringComparison.Ordinal) == true
                    ? (live ? "Searching skills" : "Searched skills")
                    : (live ? "Loading skill" : "Loaded skill"),
                AgentActivityKind.Program => live ? "Running a program" : "Ran a program",
                AgentActivityKind.Advisor => live ? "Consulting the advisor" : "Consulted the advisor",
                AgentActivityKind.Todo => live ? "Updating the plan" : "Updated the plan",
                AgentActivityKind.Compaction => live ? "Compacting context" : "Compacted context",
                _ => DisplayText,
            };
        }
    }

    /// <summary>What the row acted on: a file, a command, a query, a host, a tool.</summary>
    public string RowSubject => Kind switch
    {
        AgentActivityKind.Explore when ToolDisplay == "Glob" && Detail is { Length: > 0 } pattern =>
            FoundNothing ? $"for {pattern}" : pattern,
        AgentActivityKind.Explore when FoundNothing && ToolDisplay is "List" or "Glob" => string.Empty,
        AgentActivityKind.Explore or AgentActivityKind.Analyze or AgentActivityKind.Edit => FileName
            ?? (Count is { } items && Kind == AgentActivityKind.Explore ? Plural(items, "item") : string.Empty),
        AgentActivityKind.Search => string.Join(' ', new[]
        {
            Symbol is { Length: > 0 } symbol ? $"\"{symbol}\"" : string.Empty,
            Detail is { Length: > 0 } scope ? $"in {scope}" : string.Empty,
        }.Where(part => part.Length > 0)),
        AgentActivityKind.Web when ToolDisplay == "Browser" => BrowserSubject,
        AgentActivityKind.Terminal => CommandLine(TerminalInputPreview),
        AgentActivityKind.Web or AgentActivityKind.Mcp or AgentActivityKind.Computer
            or AgentActivityKind.Advisor => Detail?.Trim() ?? string.Empty,
        AgentActivityKind.Skill => Detail is { } skill && skill.StartsWith("search: ", StringComparison.Ordinal)
            ? $"\"{skill["search: ".Length..]}\"" : Detail?.Trim() ?? string.Empty,
        AgentActivityKind.Todo => Todos is { Count: > 0 } todos ? TodoSubject(todos) : Count is { } planned ? Plural(planned, "task") : string.Empty,
        AgentActivityKind.Compaction => Count is { } compacted ? Plural(compacted, "item") : string.Empty,
        // Rows that name no subject of their own still show the short context they carry.
        _ => ShowPlainDetail ? CommandLine(Detail) : string.Empty,
    };

    public bool HasRowSubject => RowSubject.Length > 0;

    /// <summary>The browser action a row names, the first word of its Host-written detail.</summary>
    private string BrowserAction => Detail?.Split(' ', 2)[0] ?? string.Empty;

    /// <summary>What the browser action acted on: a host, an element ref, a key, a character count.</summary>
    private string BrowserSubject => Detail?.Split(' ', 2) is [_, var subject] ? subject.Trim() : string.Empty;

    private static string BrowserVerb(string action, bool live) => action switch
    {
        "open" or "navigate" => live ? "Opening" : "Opened",
        "back" => live ? "Going back" : "Went back",
        "forward" => live ? "Going forward" : "Went forward",
        "reload" => live ? "Reloading the page" : "Reloaded the page",
        "snapshot" => live ? "Reading the page" : "Read the page",
        "screenshot" => live ? "Taking a screenshot" : "Took a screenshot",
        "click" => live ? "Clicking" : "Clicked",
        "hover" => live ? "Hovering" : "Hovered",
        "type" or "fill" => live ? "Typing" : "Typed",
        "select" => live ? "Selecting" : "Selected",
        "press" => live ? "Pressing" : "Pressed",
        "scroll" => live ? "Scrolling" : "Scrolled",
        "find" => live ? "Finding on the page" : "Searched the page",
        "upload" => live ? "Uploading files" : "Uploaded files",
        "eval" => live ? "Running a page script" : "Ran a page script",
        "console" => live ? "Reading the console" : "Read the console",
        "network" => live ? "Reading network activity" : "Read network activity",
        "tabs" or "new_tab" or "switch_tab" or "close_tab" => live ? "Managing tabs" : "Managed tabs",
        "wait" => live ? "Waiting on the page" : "Waited on the page",
        "dialog" => live ? "Handling a dialog" : "Handled a dialog",
        "batch" => live ? "Running browser steps" : "Ran browser steps",
        "close" => live ? "Closing the browser" : "Closed the browser",
        _ => live ? "Using the browser" : "Used the browser",
    };

    /// <summary>A settled listing or file search that matched nothing; the verb says so.</summary>
    private bool FoundNothing => Kind == AgentActivityKind.Explore && State == AgentActivityState.Completed
        && Count == 0 && string.IsNullOrEmpty(FileName);

    /// <summary>
    /// The numbers after the subject: a line range, a result count, how long it took.
    /// Line deltas have their own coloured pair and are not repeated here.
    /// </summary>
    public string RowMeta
    {
        get
        {
            var parts = new List<string>();
            if (Kind is AgentActivityKind.Analyze or AgentActivityKind.Explore
                && LineStart is { } start && LineEnd is { } end)
                parts.Add(start == end ? $"L{start}" : $"L{start}–{end}");
            if (Kind == AgentActivityKind.Search && Count is { } results)
                parts.Add(Plural(results, "result"));
            if (Kind == AgentActivityKind.Explore && ToolDisplay == "Glob" && Detail is { Length: > 0 } && Count is > 0 and var found)
                parts.Add(Plural(found, "file"));
            if (Kind == AgentActivityKind.Thinking && EffectiveDurationMs is { } thought && State != AgentActivityState.Started)
                parts.Add(ShortDuration(thought));
            else if (Kind is AgentActivityKind.Terminal or AgentActivityKind.Program or AgentActivityKind.Web
                     or AgentActivityKind.Mcp or AgentActivityKind.Advisor && DurationShortText is { } took)
                parts.Add(took);
            return string.Join(" · ", parts);
        }
    }

    public bool HasRowMeta => RowMeta.Length > 0;

    private static string Plural(int count, string noun) => count switch
    {
        0 => $"no {noun}s",
        1 => $"1 {noun}",
        _ => $"{count} {noun}s",
    };

    private static string ShortDuration(int ms) => ms < 1000
        ? $"{ms}ms"
        : ms < 60_000
            ? (ms / 1000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s"
            : $"{ms / 60_000}m {ms / 1000 % 60}s";

    private static string CommandLine(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
        var line = detail.Trim().Split('\n')[0].Trim();
        return line.Length <= 200 ? line : line[..199] + "…";
    }

    private static string TodoSubject(IReadOnlyList<TodoItemModel> todos)
    {
        var active = todos.Count(todo => todo.IsInProgress);
        var tasks = Plural(todos.Count, "task");
        return active > 0 ? $"{tasks} · {active} in progress" : tasks;
    }
}
