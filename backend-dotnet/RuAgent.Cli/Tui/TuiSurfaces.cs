namespace RuAgent.Cli.Tui;

/// <summary>Everything the <c>/status</c> card shows. Assembled by the CLI, rendered here.</summary>
public sealed record TuiStatusSnapshot(
    string SessionId,
    string Preset,
    string Backend,
    string Model,
    string Workspace,
    int Turn,
    long? TotalTokens,
    int ToolsSettled,
    bool? ApiKeyConfigured);

/// <summary>
/// Pure builders for the TUI surfaces: the <c>/help</c> panel, the <c>/status</c> card and the
/// picker contents. Keeping them free of I/O is what makes them testable, and keeps the panel
/// grammar in one place.
/// </summary>
public static class TuiSurfaces
{
    public const string PanelHint = "esc · /help";

    /// <summary>Groups used by both the help panel and the slash dropdown's vocabulary.</summary>
    private static readonly (string Title, string[] Commands)[] HelpGroups =
    [
        ("session & branching", ["/tree", "/status", "/stats", "/tokens", "/export", "/fork", "/rewind", "/history"]),
        ("model & backend", ["/model", "/backend"]),
        ("terminal", ["/clear", "/help", "/exit", "/quit"]),
    ];

    /// <summary>
    /// The command reference. Built from <see cref="InteractiveLineReader.KnownCommands"/> so a
    /// command can never be listed without its description, and a command added there shows up
    /// here even before it is filed into a group.
    /// </summary>
    public static IReadOnlyList<string> Help(int width)
    {
        var known = InteractiveLineReader.KnownCommands;
        var knownByCommand = known.ToDictionary(c => c.Command, c => c, StringComparer.OrdinalIgnoreCase);
        var filed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = new List<PanelSection>();

        foreach (var (title, commands) in HelpGroups)
        {
            var rows = new List<PanelRow>();
            foreach (var command in commands)
            {
                var info = knownByCommand.GetValueOrDefault(command)
                    ?? new InteractiveLineReader.SlashCommandInfo(command, string.Empty);
                filed.Add(command);
                rows.Add(new PanelRow(info.Syntax.Length > 0 ? info.Syntax : info.Command, info.Description));
            }

            sections.Add(new PanelSection(title, rows));
        }

        var unfiled = known.Where(c => !filed.Contains(c.Command)).ToList();
        if (unfiled.Count > 0)
        {
            sections.Add(new PanelSection(
                "more",
                unfiled.Select(c => new PanelRow(c.Syntax.Length > 0 ? c.Syntax : c.Command, c.Description)).ToList()));
        }

        return TuiPanel.Compose(width, "help", sections, PanelHint);
    }

    /// <summary>The session card, one section for what, one for where, one for the run.</summary>
    public static IReadOnlyList<string> Status(int width, TuiStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var session = new List<PanelRow>
        {
            new("session", Shorten(snapshot.SessionId, 20)),
            new("preset", string.IsNullOrWhiteSpace(snapshot.Preset) ? "standard" : snapshot.Preset),
        };

        var model = new List<PanelRow>
        {
            new("backend", string.IsNullOrWhiteSpace(snapshot.Backend) ? "unknown" : snapshot.Backend),
            new("model", string.IsNullOrWhiteSpace(snapshot.Model) ? "not set" : snapshot.Model),
        };

        if (snapshot.ApiKeyConfigured is { } configured)
        {
            model.Add(new PanelRow("api key", configured ? "configured" : "missing"));
        }

        var run = new List<PanelRow>
        {
            new("turns", snapshot.Turn.ToString()),
            new("tools", snapshot.ToolsSettled.ToString()),
            new("tokens", snapshot.TotalTokens is { } tokens ? tokens.ToString() : "—"),
            new("workspace", ShortenPath(snapshot.Workspace)),
        };

        return TuiPanel.Compose(
            width,
            "status",
            [
                new PanelSection("session", session),
                new PanelSection("model", model),
                new PanelSection("run", run),
            ],
            PanelHint);
    }

    /// <summary>Backends the CLI can switch between, in the order the docs list them.</summary>
    private static readonly (string Value, string Label, string Detail)[] Backends =
    [
        ("kilo", "kilo", "Kilo Gateway"),
        ("opencode", "opencode", "OpenCode Zen"),
        ("ollama", "ollama", "Ollama"),
        ("lmstudio", "lmstudio", "LM Studio"),
        ("openai", "openai", "OpenAI"),
        ("anthropic", "anthropic", "Anthropic"),
    ];

    /// <summary>The backend picker rows, with the current backend marked and a
    /// signed-out provider called out so its absence from a run is not a surprise.</summary>
    public static IReadOnlyList<ChoiceItem> BackendChoices(
        string? currentBackend,
        IReadOnlyDictionary<string, bool>? keyStates = null)
    {
        var current = NormalizeBackend(currentBackend);
        var items = Backends
            .Select(b =>
            {
                var detail = b.Detail;
                if (string.Equals(b.Value, current, StringComparison.OrdinalIgnoreCase))
                {
                    detail += " · current";
                }

                if (keyStates is not null && keyStates.TryGetValue(b.Value, out var hasKey) && !hasKey)
                {
                    detail += " · no key";
                }

                return new ChoiceItem(b.Label, detail, b.Value);
            })
            .ToList();

        var index = items.FindIndex(i => string.Equals(i.Value, current, StringComparison.OrdinalIgnoreCase));
        if (index > 0)
        {
            (items[0], items[index]) = (items[index], items[0]);
        }

        return items;
    }

    /// <summary>
    /// Model candidates: the model configured for the active backend first, then the ones
    /// configured for the other backends. The CLI has no catalog endpoint, so these are the
    /// models this machine has actually been configured with. A model whose provider lost
    /// its credential stays selectable but is marked, because the CLI cannot store a key.
    /// </summary>
    public static IReadOnlyList<ChoiceItem> ModelChoices(
        string? currentBackend,
        IReadOnlyList<(string Backend, string? Model)> configured,
        IReadOnlyDictionary<string, bool>? keyStates = null)
    {
        ArgumentNullException.ThrowIfNull(configured);
        var current = NormalizeBackend(currentBackend);
        var items = new List<ChoiceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (backend, model) in configured)
        {
            var value = model?.Trim();
            if (string.IsNullOrEmpty(value) || !seen.Add(value))
            {
                continue;
            }

            var alias = NormalizeBackend(backend);
            var detail = string.Equals(alias, current, StringComparison.OrdinalIgnoreCase)
                ? backend + " · current"
                : backend;
            if (keyStates is not null && keyStates.TryGetValue(alias, out var hasKey) && !hasKey)
            {
                detail += " · no key";
            }

            items.Add(new ChoiceItem(Shorten(value, 24), detail, value));
        }

        return items;
    }

    private static string NormalizeBackend(string? backend) => backend?.Trim().ToLowerInvariant() switch
    {
        "kilo" or "kilo gateway" => "kilo",
        "opencode" or "opencode zen" => "opencode",
        "ollama" => "ollama",
        "lm studio" or "lmstudio" => "lmstudio",
        "openai" => "openai",
        "anthropic" => "anthropic",
        _ => string.Empty,
    };

    private static string Shorten(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)] + "…";

    /// <summary>Workspace path trimmed from the left so the leaf stays readable.</summary>
    private static string ShortenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "—";
        }

        var trimmed = path.TrimEnd('\\', '/');
        var leafIndex = trimmed.LastIndexOfAny(['\\', '/']);
        var leaf = leafIndex >= 0 && leafIndex < trimmed.Length - 1 ? trimmed[(leafIndex + 1)..] : trimmed;
        return leafIndex > 0 ? "…" + Path.DirectorySeparatorChar + leaf : leaf;
    }
}
