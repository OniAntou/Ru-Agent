namespace RuAgent.Cli.Tui;

/// <summary>
/// Pure builders for the TUI surfaces: the <c>/help</c> panel and the picker contents. Keeping
/// them free of I/O is what makes them testable, and keeps the panel grammar in one place.
/// </summary>
public static class TuiSurfaces
{
    public const string PanelHint = "esc · /help";

    /// <summary>Groups used by both the help panel and the slash dropdown's vocabulary.</summary>
    private static readonly (string Title, string[] Commands)[] HelpGroups =
    [
        ("session", ["/new", "/sessions", "/preset", "/computer", "/browser"]),
        ("model & provider", ["/model", "/provider", "/reasoning", "/advisor"]),
        ("terminal", ["/help", "/update", "/exit"]),
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
    /// <summary>
    /// What the Host last learned about a provider's stored key (<c>provider_key_status</c>
    /// in <c>/settings</c>, keyed by canonical backend), or null when nothing is known.
    /// </summary>
    internal static string? KeyStatusDetail(string choice, IReadOnlyDictionary<string, string>? keyStatus)
    {
        if (keyStatus is null || keyStatus.Count == 0) return null;
        var backend = RuAgent.Protocol.Providers.ProviderAliases.Normalize(choice) ?? choice;
        return keyStatus.TryGetValue(backend, out var state)
            ? state switch
            {
                "verified" => " · key ok",
                "unchecked" => " · key not checked yet",
                "rejected" => " · key refused",
                _ => null,
            }
            : null;
    }

    public static IReadOnlyList<ChoiceItem> BackendChoices(
        string? currentBackend,
        IReadOnlyDictionary<string, bool>? keyStates = null,
        IReadOnlyList<(string Id, string Name)>? providers = null,
        IReadOnlyDictionary<string, string>? keyStatus = null)
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
                else if (keyStates is not null && keyStates.TryGetValue(b.Value, out var stored) && stored
                    && KeyStatusDetail(b.Value, keyStatus) is { } status)
                {
                    // A verdict means something only while a key is stored.
                    detail += status;
                }

                return new ChoiceItem(b.Label, detail, b.Value);
            })
            .ToList();
    }

    /// <summary>
    /// Live models.dev rows under the base ones: the current provider, then the ones already
    /// signed in, then the rest by id. The order keeps the rows an operator can act on above
    /// the long tail; ids stay deduplicated.
    /// </summary>
    private static List<(string Id, string Name)> OrderedModelsDevEntries(
        string current,
        IReadOnlyDictionary<string, bool>? keyStates,
        IReadOnlyList<(string Id, string Name)> providers)
    {
        var all = new List<(string Id, string Name)>(providers.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, name) in providers)
        {
            var trimmed = id?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            all.Add((trimmed, name?.Trim() ?? string.Empty));
        }

        bool IsCurrent((string Id, string Name) entry) =>
            string.Equals("models.dev:" + entry.Id, current, StringComparison.OrdinalIgnoreCase);
        bool IsSignedIn((string Id, string Name) entry) =>
            keyStates is not null && keyStates.TryGetValue("models.dev:" + entry.Id, out var hasKey) && hasKey;

        return all
            .Where(IsCurrent)
            .Concat(all.Where(entry => !IsCurrent(entry) && IsSignedIn(entry)))
            .Concat(all
                .Where(entry => !IsCurrent(entry) && !IsSignedIn(entry))
                .OrderBy(entry => entry.Id, StringComparer.Ordinal))
            .ToList();
    }

    private static List<(string Value, string Label, string Detail)> ProviderRows(
        string current,
        IReadOnlyDictionary<string, bool>? keyStates,
        IReadOnlyList<(string Id, string Name)> providers) =>
        OrderedModelsDevEntries(current, keyStates, providers)
            .Select(entry => (
                $"models.dev:{entry.Id}",
                $"models.dev:{entry.Id}",
                "Models.dev: " + entry.Name))
            .ToList();

    /// <summary>
    /// The minimal <c>/provider</c> rows: friendly names and a green checkmark on the right of
    /// every provider that already has a stored key. No key verdicts are spelled out — the
    /// checkmark answers "signed in?", and a blank right side means "not signed in yet".
    /// The value stays the canonical choice, so selection and explicit arguments are unchanged.
    /// </summary>
    public static IReadOnlyList<ChoiceItem> ProviderChoices(
        string? currentBackend,
        IReadOnlyDictionary<string, bool>? keyStates = null,
        IReadOnlyList<(string Id, string Name)>? providers = null)
    {
        var current = NormalizeBackend(currentBackend);
        var baseRows = BaseBackends.ToList();
        var baseIndex = baseRows.FindIndex(row => string.Equals(row.Value, current, StringComparison.OrdinalIgnoreCase));
        if (baseIndex > 0)
        {
            (baseRows[0], baseRows[baseIndex]) = (baseRows[baseIndex], baseRows[0]);
        }

        var items = new List<ChoiceItem>(baseRows.Count + (providers?.Count ?? FallbackBackends.Length) + 2);
        foreach (var (value, _, detail) in baseRows)
        {
            items.Add(ProviderRow(
                string.Equals(value, "custom", StringComparison.OrdinalIgnoreCase) ? "Custom" : detail,
                value,
                current,
                keyStates));
        }

        if (providers is { Count: > 0 })
        {
            AppendProviderSection(
                items,
                current,
                keyStates,
                OrderedModelsDevEntries(current, keyStates, providers)
                    .Select(entry => ($"models.dev:{entry.Id}", entry.Name))
                    .ToList());
        }
        else
        {
            AppendProviderSection(
                items,
                current,
                keyStates,
                FallbackBackends.Select(row => (row.Value, row.Detail)).ToList());
        }

        return items;
    }

    /// <summary>One provider row: a checkmark when signed in, otherwise the current label.</summary>
    private static ChoiceItem ProviderRow(
        string label,
        string value,
        string current,
        IReadOnlyDictionary<string, bool>? keyStates)
    {
        var signedIn = keyStates is not null
            && keyStates.TryGetValue(value, out var hasKey)
            && hasKey;
        var detail = signedIn
            ? "✓"
            : string.Equals(value, current, StringComparison.OrdinalIgnoreCase) ? "current" : string.Empty;
        return new ChoiceItem(label, detail, value, DetailTone: signedIn ? TuiToken.Ok : null);
    }

    /// <summary>
    /// Adds the models.dev block under the fixed providers. A blank row separates the fixed
    /// providers from the catalog and the already-signed-in providers from the long tail, so
    /// the picker reads as groups instead of one dense wall.
    /// </summary>
    private static void AppendProviderSection(
        List<ChoiceItem> items,
        string current,
        IReadOnlyDictionary<string, bool>? keyStates,
        IReadOnlyList<(string Value, string Detail)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (items.Count > 0)
        {
            items.Add(BlankChoice());
        }

        var inTail = false;
        foreach (var (value, detail) in entries)
        {
            var signedIn = keyStates is not null
                && keyStates.TryGetValue(value, out var hasKey)
                && hasKey;
            var isCurrent = string.Equals(value, current, StringComparison.OrdinalIgnoreCase);
            if (!signedIn && !isCurrent && !inTail)
            {
                // The signed-in block just ended; one blank row gives the catalog tail room.
                inTail = true;
                if (items[^1].Value.Length != 0)
                {
                    items.Add(BlankChoice());
                }
            }

            const string prefix = "Models.dev: ";
            var label = detail.StartsWith(prefix, StringComparison.Ordinal)
                ? detail[prefix.Length..]
                : detail;
            if (string.IsNullOrWhiteSpace(label))
            {
                label = value;
            }

            items.Add(ProviderRow(label, value, current, keyStates));
        }
    }

    private static ChoiceItem BlankChoice() => new(string.Empty, string.Empty, string.Empty, Enabled: false);

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

    /// <summary>One provider's catalog in the grouped model picker.</summary>
    /// <param name="Backend">Canonical backend the Host stored (for display and reads).</param>
    /// <param name="WireChoice">Value a settings write sends as <c>backend_choice</c>.</param>
    /// <param name="Display">Provider label shown as the list heading.</param>
    /// <param name="Error">Why the catalog is missing or empty; null when it loaded.</param>
    /// <param name="IsCurrent">True for the active backend, the only one that may mark a model current.</param>
    public sealed record ModelGroup(
        string Backend,
        string WireChoice,
        string Display,
        IReadOnlyList<(string Id, bool Supported)> Models,
        string? Error = null,
        bool IsCurrent = false);

    /// <summary>Where a grouped picker row points: a model on one provider.</summary>
    public sealed record ModelChoiceTarget(string Backend, string WireChoice, string Model);

    /// <summary>
    /// One group in display order: the current model when it belongs to this catalog, then
    /// free supported models, the remaining supported ones, and unsupported models last.
    /// </summary>
    public static IReadOnlyList<(string Id, bool Supported)> OrderedGroupModels(
        string? currentModel,
        ModelGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<(string Id, bool Supported)>();
        void Add(string? id, bool supported)
        {
            var value = id?.Trim();
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
            {
                return;
            }

            ordered.Add((value, supported));
        }

        var current = group.IsCurrent && !string.IsNullOrWhiteSpace(currentModel)
            ? currentModel.Trim()
            : null;
        if (current is not null
            && group.Models.Any(model => model.Supported
                && string.Equals(model.Id?.Trim(), current, StringComparison.Ordinal)))
        {
            Add(current, true);
        }

        foreach (var model in group.Models
                     .Where(model => model.Supported && IsFreeModel(model.Id))
                     .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase))
        {
            Add(model.Id, true);
        }

        foreach (var model in group.Models
                     .Where(model => model.Supported && !IsFreeModel(model.Id))
                     .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase))
        {
            Add(model.Id, true);
        }

        foreach (var model in group.Models
                     .Where(model => !model.Supported)
                     .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase))
        {
            Add(model.Id, false);
        }

        return ordered;
    }

    /// <summary>The right-hand tags for one model row, shared by the picker and the panel.</summary>
    public static string GroupModelDetail(
        string id,
        bool supported,
        bool isCurrent,
        string wireChoice,
        IReadOnlyDictionary<string, bool>? keyStates = null)
    {
        var parts = new List<string>(3);
        if (isCurrent)
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
        else if (keyStates is not null
                 && keyStates.TryGetValue(wireChoice, out var hasKey)
                 && !hasKey)
        {
            parts.Add("no key");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Rows for <c>/model</c> across every signed-in provider: a disabled heading per provider
    /// followed by its models. Row values are positional targets (<c>#n</c>) so one pick can
    /// carry both the provider and the model; the caller resolves it with
    /// <paramref name="keyStates"/> against the returned target list.
    /// </summary>
    /// <remarks>
    /// Providers are displayed in the order the caller supplied (the active backend first).
    /// Headings are disabled so arrow keys and digits skip them; a filter that matches only a
    /// heading still cannot confirm it.
    /// </remarks>
    public static (IReadOnlyList<ChoiceItem> Items, IReadOnlyList<ModelChoiceTarget> Targets) GroupedModelChoices(
        string? currentModel,
        IReadOnlyList<ModelGroup> groups,
        IReadOnlyDictionary<string, bool>? keyStates = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var items = new List<ChoiceItem>();
        var targets = new List<ModelChoiceTarget>();
        var first = true;
        foreach (var group in groups)
        {
            if (!first)
            {
                // One blank row between provider groups so headings and rows breathe.
                items.Add(BlankChoice());
            }

            first = false;
            var supported = group.Models.Count(model => model.Supported);
            var heading = group.Error is null
                ? $"{supported} models"
                : "catalog unavailable";
            if (group.IsCurrent)
            {
                heading += " · current";
            }

            items.Add(new ChoiceItem(group.Display, heading, string.Empty, Enabled: false));
            if (group.Error is not null && group.Models.Count == 0)
            {
                continue;
            }

            foreach (var (id, isSupported) in OrderedGroupModels(currentModel, group))
            {
                var isCurrent = group.IsCurrent
                    && !string.IsNullOrWhiteSpace(currentModel)
                    && string.Equals(id, currentModel.Trim(), StringComparison.Ordinal);
                var detail = GroupModelDetail(id, isSupported, isCurrent, group.WireChoice, keyStates);
                var value = "#" + targets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                targets.Add(new ModelChoiceTarget(group.Backend, group.WireChoice, id));
                items.Add(new ChoiceItem(id, detail, value, isSupported));
            }
        }

        return (items, targets);
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
}
