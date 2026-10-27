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
        ("session", ["/new", "/sessions", "/resume", "/delete", "/status", "/preset", "/computer"]),
        ("run", ["/follow", "/retry", "/release"]),
        ("model & backend", ["/model", "/backend", "/key", "/reasoning"]),
        ("terminal", ["/clear", "/help", "/update", "/exit"]),
    ];

    /// <summary>
    /// The command reference. Built from <see cref="ChatCommandCatalog.KnownCommands"/> so a
    /// command can never be listed without its description, and a command added there shows up
    /// here even before it is filed into a group.
    /// </summary>
    public static IReadOnlyList<string> Help(int width, bool framed = true)
    {
        var known = ChatCommandCatalog.KnownCommands;
        var knownByCommand = known.ToDictionary(c => c.Command, c => c, StringComparer.OrdinalIgnoreCase);
        var filed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = new List<PanelSection>();

        foreach (var (title, commands) in HelpGroups)
        {
            var rows = new List<PanelRow>();
            foreach (var command in commands)
            {
                var info = knownByCommand.GetValueOrDefault(command)
                    ?? new ChatCommandCatalog.SlashCommandInfo(command, string.Empty);
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

        return TuiPanel.Compose(width, "help", sections, PanelHint, framed);
    }

    /// <summary>The session card, one section for what, one for where, one for the run.</summary>
    public static IReadOnlyList<string> Status(int width, TuiStatusSnapshot snapshot, bool framed = true)
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
            PanelHint, framed);
    }

    /// <summary>Backends the CLI can switch between, in the order the docs list them.</summary>
    private static readonly (string Value, string Label, string Detail)[] BaseBackends =
    [
        ("openai", "openai", "OpenAI"),
        ("anthropic", "anthropic", "Anthropic"),
        ("custom", "custom", "Custom OpenAI"),
    ];

    /// <summary>
    /// The models.dev rows used when the live list cannot be fetched, so a network problem
    /// never empties the picker.
    /// </summary>
    private static readonly (string Value, string Label, string Detail)[] FallbackBackends =
    [
        ("models.dev:opencode", "models.dev:opencode", "Models.dev: OpenCode Zen"),
        ("models.dev:kilo", "models.dev:kilo", "Models.dev: Kilo Gateway"),
        ("models.dev:openrouter", "models.dev:openrouter", "Models.dev: OpenRouter"),
        ("models.dev:cloudflare-workers-ai", "models.dev:cloudflare-workers-ai", "Models.dev: Cloudflare Workers AI"),
    ];

    /// <summary>The backend picker rows, with the current backend marked and a
    /// signed-out provider called out so its absence from a run is not a surprise.</summary>
    /// <param name="providers">The live models.dev catalog. Null or empty falls back to the
    /// four providers the CLI has always known.</param>
    public static IReadOnlyList<ChoiceItem> BackendChoices(
        string? currentBackend,
        IReadOnlyDictionary<string, bool>? keyStates = null,
        IReadOnlyList<(string Id, string Name)>? providers = null)
    {
        var current = NormalizeBackend(currentBackend);
        var rows = BaseBackends.ToList();
        var baseIndex = rows.FindIndex(r => string.Equals(r.Value, current, StringComparison.OrdinalIgnoreCase));
        if (baseIndex > 0)
        {
            (rows[0], rows[baseIndex]) = (rows[baseIndex], rows[0]);
        }

        if (providers is { Count: > 0 })
        {
            rows.AddRange(ProviderRows(current, keyStates, providers));
        }
        else
        {
            rows.AddRange(FallbackBackends);
        }

        return rows
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
    }

    /// <summary>
    /// Live models.dev rows under the base ones: the current provider, then the ones
    /// already signed in, then the rest by id. The rows an operator can act on sit above
    /// the long tail, which is why the current provider is duplicated in priority order
    /// rather than swapped — the list has to stay deduplicated.
    /// </summary>
    private static List<(string Value, string Label, string Detail)> ProviderRows(
        string current,
        IReadOnlyDictionary<string, bool>? keyStates,
        IReadOnlyList<(string Id, string Name)> providers)
    {
        var all = new List<(string Value, string Label, string Detail)>(providers.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, name) in providers)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add("models.dev:" + id.Trim()))
            {
                continue;
            }

            all.Add(($"models.dev:{id}", $"models.dev:{id}", "Models.dev: " + name));
        }

        var isCurrent = new Func<(string Value, string Label, string Detail), bool>(
            row => string.Equals(row.Value, current, StringComparison.OrdinalIgnoreCase));
        var isSignedIn = new Func<(string Value, string Label, string Detail), bool>(
            row => keyStates is not null && keyStates.TryGetValue(row.Value, out var hasKey) && hasKey);

        var ordered = new List<(string Value, string Label, string Detail)>(all.Count);
        ordered.AddRange(all.Where(isCurrent));
        ordered.AddRange(all.Where(row => !isCurrent(row) && isSignedIn(row)));
        ordered.AddRange(all
            .Where(row => !isCurrent(row) && !isSignedIn(row))
            .OrderBy(row => row.Value, StringComparer.Ordinal));
        return ordered;
    }

    /// <summary>
    /// Reasoning-effort rows for the active model: <c>default</c> always leads, then the
    /// levels the catalog actually offers. An empty list means the model has no effort to
    /// choose, so the caller reports that instead of opening a picker.
    /// </summary>
    public static IReadOnlyList<ChoiceItem> ReasoningChoices(string? currentEffort, IReadOnlyList<string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var current = string.IsNullOrWhiteSpace(currentEffort) ? "default" : currentEffort.Trim();
        var seen = new HashSet<string>(StringComparer.Ordinal) { "default" };
        var levels = new List<string> { "default" };
        foreach (var option in options)
        {
            var value = option?.Trim();
            if (!string.IsNullOrEmpty(value) && value.Length <= 32 && seen.Add(value))
            {
                levels.Add(value);
            }
        }

        if (levels.Count == 1)
        {
            return [];
        }

        return levels
            .Select(level => new ChoiceItem(
                level,
                string.Equals(level, current, StringComparison.OrdinalIgnoreCase) ? "current" : string.Empty,
                level))
            .ToList();
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

    /// <summary>
    /// Model picker rows from a live catalog: the current model first, then free
    /// supported models, then the rest alphabetically, then unsupported ones dimmed
    /// and unpickable. Free models carry a <c>free</c> tag (OpenRouter marks them
    /// with a <c>:free</c> suffix); a provider without a credential is marked too.
    /// Labels keep both ends of long ids so providers sharing a prefix stay apart.
    /// </summary>
    public static IReadOnlyList<ChoiceItem> ModelCatalogChoices(
        string? currentBackend,
        string? currentModel,
        IReadOnlyList<(string Id, bool Supported)> catalog,
        IReadOnlyDictionary<string, bool>? keyStates = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var current = NormalizeBackend(currentBackend);
        var currentId = currentModel?.Trim() ?? string.Empty;
        var noKey = keyStates is not null && keyStates.TryGetValue(current, out var hasKey) && !hasKey;
        var items = new List<ChoiceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string id, bool supported)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                return;
            }

            var parts = new List<string>();
            if (string.Equals(id, currentId, StringComparison.Ordinal))
            {
                parts.Add("current");
            }

            if (IsFreeModel(id))
            {
                parts.Add("free");
            }

            if (!supported)
            {
                parts.Add("unsupported");
            }
            else if (noKey)
            {
                parts.Add("no key");
            }

            items.Add(new ChoiceItem(id.Trim(), string.Join(" · ", parts), id, supported));
        }

        if (currentId.Length > 0)
        {
            Add(currentId, true);
        }

        foreach (var id in catalog.Where(entry => entry.Supported && IsFreeModel(entry.Id)).Select(entry => entry.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            Add(id, true);
        }

        foreach (var id in catalog.Where(entry => entry.Supported && !IsFreeModel(entry.Id)).Select(entry => entry.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            Add(id, true);
        }

        foreach (var id in catalog.Where(entry => !entry.Supported).Select(entry => entry.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            Add(id, false);
        }

        return items;
    }

    /// <summary>OpenRouter marks free-tier models with a <c>:free</c> id suffix.</summary>
    public static bool IsFreeModel(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Trim().EndsWith(":free", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBackend(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return string.Empty;
        }

        var trimmed = backend.Trim();
        if (trimmed.StartsWith("models.dev:", StringComparison.OrdinalIgnoreCase))
        {
            return "models.dev:" + trimmed["models.dev:".Length..].Trim().ToLowerInvariant();
        }

        return trimmed.ToLowerInvariant() switch
        {
            "openai" or "openai api" => "openai",
            "anthropic" or "anthropic claude" or "claude" => "anthropic",
            "custom" or "custom openai" or "custom openai api" => "custom",
            _ => string.Empty,
        };
    }

    private static string Shorten(string value, int maximum) =>
        value.Length <= maximum ? value : TuiText.SafeCut(value, maximum - 1) + "…";

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
