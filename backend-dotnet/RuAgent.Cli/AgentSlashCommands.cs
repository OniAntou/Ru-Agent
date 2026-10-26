using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;

namespace RuAgent.Cli;

/// <summary>
/// Provider credentials, model selection and report commands for terminal chat.
/// </summary>
public sealed partial class AgentSlashCommands
{
    private readonly SettingsApiClient _settingsClient;
    private TextWriter _humanOut;
    private readonly TextWriter _err;
    private readonly SessionApiClient? _sessionClient;

    /// <summary>
    /// Writer for human-facing text. Defaults to the construction writer; the chat REPL
    /// repoints it at stderr when --json owns stdout, so no panel ever lands on stdout.
    /// </summary>
    public TextWriter HumanOut
    {
        get => _humanOut;
        set => _humanOut = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// True when --json owns stdout. Human text still renders (on <see cref="HumanOut"/>),
    /// but commands whose bare form needs a picker or a secret entry refuse with a bounded
    /// <c>interactive_required</c> on stderr instead of prompting or reading stdin.
    /// </summary>
    public bool Json { get; set; }

    /// <summary>
    /// Runs an interactive picker under the composer. Wired by the CLI to the line reader;
    /// when absent (or not interactive) the handler falls back to a text listing, which is
    /// also what --json and redirected runs get.
    /// </summary>
    public Func<Tui.ChoiceList, CancellationToken, Task<string?>>? Picker { get; set; }

    /// <summary>
    /// Reads one secret without echo or history. Wired by the CLI to the line reader;
    /// null means cancelled or non-interactive. Secrets never reach output, history, or logs.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? SecretPrompt { get; set; }

    /// <summary>
    /// Masked panel variant of <see cref="SecretPrompt"/>: takes the provider display
    /// name and renders the framed bullet input. Preferred when wired; otherwise the
    /// CLI falls back to <see cref="SecretPrompt"/>.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? SecretPanelPrompt { get; set; }

    /// <summary>
    /// Set by the Terminal.Gui wiring: the TG transcript has no scrollback behind the
    /// floating picker, so a cancelled bare form reprints the classic option panel instead
    /// of leaving the operator with a silent no-op. The classic full-screen and sequential
    /// paths keep their behaviour (Escape closes the picker and nothing else is printed).
    /// </summary>
    public bool ReprintPanelOnPickerCancel { get; set; }

    /// <summary>Live run counters for the status card. Wired by the CLI; may be absent in tests.</summary>
    public Func<Tui.TuiStatusSnapshot?>? SnapshotProvider { get; set; }

    public int PanelWidth { get; set; } = Tui.TuiViewport.FallbackWidth;
    public bool FramelessReports { get; set; }

    public AgentSlashCommands(SettingsApiClient settingsClient, TextWriter outWriter, TextWriter errWriter)
        : this(settingsClient, null, outWriter, errWriter)
    {
    }

    public AgentSlashCommands(SettingsApiClient settingsClient, SessionApiClient? sessionClient, TextWriter outWriter, TextWriter errWriter)
    {
        _settingsClient = settingsClient ?? throw new ArgumentNullException(nameof(settingsClient));
        _sessionClient = sessionClient;
        _humanOut = outWriter ?? throw new ArgumentNullException(nameof(outWriter));
        _err = errWriter ?? throw new ArgumentNullException(nameof(errWriter));
    }

    /// <returns>True if the slash command was handled (caller should continue).</returns>
    public Task<bool> TryHandleAsync(string slashCmd, string? arg, CancellationToken cancellationToken) =>
        TryHandleAsync(slashCmd, arg, null, null, null, cancellationToken);

    public async Task<bool> TryHandleAsync(string slashCmd, string? arg, string? currentSessionId, string? preset, string? workspacePath, CancellationToken cancellationToken)
    {
        if (slashCmd is "/backend")
        {
            await HandleBackendAsync(arg, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/provider")
        {
            await HandleProviderAsync(arg, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/model")
        {
            await HandleModelAsync(arg, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/key")
        {
            await HandleKeyAsync(arg, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/reasoning")
        {
            await HandleReasoningAsync(arg, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/status")
        {
            await HandleStatusAsync(currentSessionId, preset, workspacePath, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/help")
        {
            WriteHelpPanel();
            await _humanOut.FlushAsync().ConfigureAwait(false);
            return true;
        }


        return false;
    }

    private static readonly (string Backend, string Alias, string Field, string HasKeyField)[] KnownBackends =
    [
        ("OpenAI", "openai", "openai_model", "has_openai_api_key"),
        ("Anthropic", "anthropic", "anthropic_model", "has_anthropic_api_key"),
        ("Custom OpenAI", "custom", "custom_model", "has_custom_api_key"),
    ];

    private static string ModelFieldFor(string? backend) =>
        KnownBackends.FirstOrDefault(b => string.Equals(b.Backend, backend, StringComparison.OrdinalIgnoreCase)).Field
        ?? "model";

    private static string? ReadModel(JsonElement settings, string? backend)
    {
        var field = ModelFieldFor(backend);
        return settings.TryGetProperty(field, out var value) ? value.GetString() : null;
    }

    private static bool? ReadApiKeyState(JsonElement settings, string? backend)
    {
        var field = KnownBackends.FirstOrDefault(b => string.Equals(b.Backend, backend, StringComparison.OrdinalIgnoreCase)).HasKeyField;
        if (string.IsNullOrEmpty(field))
        {
            return null;
        }

        return settings.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static string? ReadBackend(JsonElement settings) =>
        settings.TryGetProperty("backend", out var backend) ? backend.GetString() : null;

    private static bool IsModelsDevBackend(string? backend) =>
        !string.IsNullOrWhiteSpace(backend)
        && backend.Trim().StartsWith("models.dev:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Credential presence per provider alias. Local providers have no key field and are
    /// omitted, so callers only ever mark providers that can actually be signed out.
    /// </summary>
    private static IReadOnlyDictionary<string, bool> ReadKeyStates(JsonElement settings)
    {
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, alias, _, hasKeyField) in KnownBackends)
        {
            if (string.IsNullOrEmpty(hasKeyField))
            {
                continue;
            }

            if (settings.TryGetProperty(hasKeyField, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                states[alias] = value.GetBoolean();
            }
        }

        return states;
    }

    /// <summary>
    /// Built-in key states plus signed-in Models.dev ids, so pickers can mark a
    /// provider without a credential instead of letting a run discover it.
    /// Best-effort: a failed lookup keeps the settings-derived states.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, bool>> ReadAllKeyStatesAsync(JsonElement settings, CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, bool>(ReadKeyStates(settings), StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var id in await _settingsClient.GetModelsDevKeysAsync(cancellationToken).ConfigureAwait(false))
            {
                states["models.dev:" + id.Trim().ToLowerInvariant()] = true;
            }
        }
        catch (Exception)
        {
        }

        return states;
    }

    /// <summary>
    /// The live models.dev catalog for the backend picker, plus the reason it is missing.
    /// A failed lookup must reach the operator: the WinUI settings surface had the same
    /// fetch failure and swallowed it into a silently short list, so the caller prints
    /// the reason and falls back to the providers the CLI has always known.
    /// </summary>
    private async Task<(IReadOnlyList<(string Id, string Name)>? Providers, string? Error)> ReadModelsDevProvidersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var providers = await _settingsClient.GetModelsDevAsync(cancellationToken).ConfigureAwait(false);
            return providers.Count > 0
                ? (providers, null)
                : (null, "provider list empty");
        }
        catch (Exception ex)
        {
            return (null, CliErrorText.ShortMessage(ex.Message));
        }
    }

    private async Task HandleBackendAsync(string? arg, CancellationToken cancellationToken)
    {
        if (Json && string.IsNullOrWhiteSpace(arg))
        {
            // The bare form is a picker; --json must not prompt or read stdin, and
            // picking for the user would mutate settings they never chose.
            await _err.WriteLineAsync("interactive_required: /backend needs a picker; pass '/backend <name>' or run without --json.").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            // No argument: offer the picker instead of making the operator recall the names.
            var settings = await TryReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            var current = settings.HasValue ? ReadBackend(settings.Value) : null;
            var keyStates = settings.HasValue
                ? await ReadAllKeyStatesAsync(settings.Value, cancellationToken).ConfigureAwait(false)
                : null;
            var (providers, providerError) = await ReadModelsDevProvidersAsync(cancellationToken).ConfigureAwait(false);
            await WriteProviderListNoticeAsync(providers, providerError).ConfigureAwait(false);

            var choices = Tui.TuiSurfaces.BackendChoices(current, keyStates, providers);
            var list = new Tui.ChoiceList("backend", choices);
            var picked = Picker is null ? null : await Picker(list, cancellationToken).ConfigureAwait(false);
            if (picked is null)
            {
                // Cancelled (nothing to add) or not interactive (list the options). The TG
                // engine also lists them: its picker panel leaves no scrollback to read.
                if (Picker is null || !IsInteractiveConsole() || ReprintPanelOnPickerCancel)
                {
                    WriteBackendPanel(current, keyStates, providers);
                }

                await _humanOut.FlushAsync().ConfigureAwait(false);
                return;
            }

            arg = picked;
        }

        await SwitchBackendAsync(arg, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Says which provider list is on screen. Without this the operator cannot tell a
    /// short catalog from a failed fetch, which is exactly the confusion the WinUI
    /// provider list hit with the same call.
    /// </summary>
    private async Task WriteProviderListNoticeAsync(
        IReadOnlyList<(string Id, string Name)>? providers,
        string? error)
    {
        if (providers is not null)
        {
            await _humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint(
                $"  {providers.Count} providers from models.dev")).ConfigureAwait(false);
            return;
        }

        await _humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint(
            $"  models.dev list unavailable ({ChatDisplayText.Sanitize(error, 160)}) - showing the known providers only")).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows or sets the active model's reasoning effort. With no argument it offers a
    /// picker when there is something to choose, matching <c>/backend</c>: only the bare
    /// form needs a terminal, the argument form writes either way.
    /// </summary>
    private async Task HandleReasoningAsync(string? arg, CancellationToken cancellationToken)
    {
        if (Json && string.IsNullOrWhiteSpace(arg))
        {
            // The bare form is a picker; --json must not prompt or read stdin, and
            // picking for the user would update settings they never chose.
            await _err.WriteLineAsync("interactive_required: /reasoning needs a picker; pass '/reasoning <effort>' or run without --json.").ConfigureAwait(false);
            return;
        }

        ModelReasoningSnapshot snapshot;
        try
        {
            var settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
            snapshot = await _settingsClient.GetReasoningAsync(ReadBackend(settings), cancellationToken).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"reasoning_unavailable: {CliErrorText.Format(ex)}").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"reasoning_unavailable: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }

        var choices = Tui.TuiSurfaces.ReasoningChoices(snapshot.SelectedEffort, snapshot.Options);
        if (string.IsNullOrWhiteSpace(arg))
        {
            if (choices.Count == 0)
            {
                await _humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint(
                    $"  {snapshot.SelectedModel ?? "(no model)"} offers no reasoning effort ({snapshot.State}).")).ConfigureAwait(false);
                await _humanOut.FlushAsync().ConfigureAwait(false);
                return;
            }

            if (Picker is null || !IsInteractiveConsole())
            {
                WriteReasoningPanel(snapshot, choices);
                return;
            }

            var picked = await Picker(new Tui.ChoiceList("reasoning", choices), cancellationToken).ConfigureAwait(false);
            if (picked is null)
            {
                if (ReprintPanelOnPickerCancel)
                {
                    WriteReasoningPanel(snapshot, choices);
                }

                return;
            }

            arg = picked;
        }

        await SaveReasoningAsync(snapshot, choices, arg.Trim(), cancellationToken).ConfigureAwait(false);
    }

    private void WriteReasoningPanel(ModelReasoningSnapshot snapshot, IReadOnlyList<Tui.ChoiceItem> choices)
    {
        var rows = choices
            .Select(item => new Tui.PanelRow(item.Label, item.Detail))
            .ToList();

        foreach (var line in Tui.TuiPanel.Compose(PanelWidth, "reasoning", [new Tui.PanelSection(snapshot.SelectedModel, rows)], "/reasoning <effort>"))
        {
            _humanOut.WriteLine(line);
        }

        _humanOut.Flush();
    }

    private async Task SaveReasoningAsync(
        ModelReasoningSnapshot snapshot,
        IReadOnlyList<Tui.ChoiceItem> choices,
        string effort,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SelectedModel) || string.IsNullOrEmpty(snapshot.Identity))
        {
            await _err.WriteLineAsync("reasoning_unavailable: the host returned no model to bind the effort to.").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }

        // default is always a legal save: the host only resolves a non-default effort
        // against the catalog, so a model with no published level can still be reset.
        var allowed = new HashSet<string>(choices.Select(item => item.Value), StringComparer.Ordinal) { "default" };
        if (!allowed.Contains(effort))
        {
            await _err.WriteLineAsync($"reasoning_unsupported: '{effort}' is not offered by {snapshot.SelectedModel}. Try: {string.Join(", ", allowed)}").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            // Exactly these three fields: the host rejects a reasoning save that carries
            // anything else, and it checks identity/model against the catalog snapshot.
            var body = new JsonObject
            {
                ["reasoning_effort"] = effort,
                ["reasoning_model"] = snapshot.SelectedModel,
                ["reasoning_identity"] = snapshot.Identity,
            };
            await _settingsClient.UpdateAsync(body, cancellationToken).ConfigureAwait(false);
            await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ reasoning effort: {effort}")).ConfigureAwait(false);
            await _humanOut.FlushAsync().ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync(CliErrorText.Format(ex)).ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"reasoning_save_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
        }
    }

    private static bool IsInteractiveConsole() => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    private void WriteBackendPanel(
        string? current,
        IReadOnlyDictionary<string, bool>? keyStates,
        IReadOnlyList<(string Id, string Name)>? providers)
    {
        var rows = Tui.TuiSurfaces.BackendChoices(current, keyStates, providers)
            .Select(item => new Tui.PanelRow(item.Label, item.Detail))
            .ToList();

        foreach (var line in Tui.TuiPanel.Compose(PanelWidth, "backend", [new Tui.PanelSection(null, rows)], "/backend <name>"))
        {
            _humanOut.WriteLine(line);
        }
    }

    private async Task SwitchBackendAsync(string arg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _err.WriteLineAsync("invalid_backend: no backend selected").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            var trimmed = arg.Trim();
            var targetBackend = trimmed.ToLowerInvariant() switch
            {
                "openai" or "openai api" => "OpenAI",
                "anthropic" or "anthropic claude" or "claude" => "Anthropic",
                "custom" or "custom openai" or "custom openai api" => "Custom OpenAI",
                "openrouter" => "models.dev:openrouter",
                _ when trimmed.StartsWith("models.dev:", StringComparison.OrdinalIgnoreCase)
                    => "models.dev:" + trimmed["models.dev:".Length..].Trim().ToLowerInvariant(),
                _ => trimmed,
            };

            var saveBody = new JsonObject { ["backend"] = targetBackend };
            await _settingsClient.UpdateAsync(saveBody, cancellationToken).ConfigureAwait(false);
            await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Switched backend to: {targetBackend}")).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"backend_error: {ex.Message}").ConfigureAwait(false);
        }

        await _humanOut.FlushAsync().ConfigureAwait(false);
    }

    private async Task<JsonElement?> TryReadSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task HandleModelAsync(string? arg, CancellationToken cancellationToken)
    {
        if (Json && string.IsNullOrWhiteSpace(arg))
        {
            // The bare form offers the model picker; --json must not prompt or read
            // stdin, and picking for the user would update settings they never chose.
            await _err.WriteLineAsync("interactive_required: /model needs a picker; pass '/model <name>' or run without --json.").ConfigureAwait(false);
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                var root = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
                var backend = ReadBackend(root);
                var configured = KnownBackends
                    .Select(b => (b.Backend, ReadModel(root, b.Backend)))
                    .ToList();
                if (IsModelsDevBackend(backend))
                {
                    configured.Insert(0, (backend ?? string.Empty, ReadModel(root, backend)));
                }

                var keyStates = await ReadAllKeyStatesAsync(root, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<Tui.ChoiceItem>? candidates = null;
                string? catalogError = null;
                try
                {
                    var catalog = await _settingsClient.GetModelsAsync(backend, cancellationToken).ConfigureAwait(false);
                    if (catalog.Count > 0)
                    {
                        candidates = Tui.TuiSurfaces.ModelCatalogChoices(backend, ReadModel(root, backend), catalog, keyStates);
                    }
                    else
                    {
                        catalogError = "model list empty";
                    }
                }
                catch (Exception ex)
                {
                    // Live catalog is best-effort; configured models still answer. The
                    // reason is kept so a short fallback list cannot read as a live catalog.
                    catalogError = CliErrorText.ShortMessage(ex.Message);
                }

                candidates ??= Tui.TuiSurfaces.ModelChoices(backend, configured, keyStates);
                if (catalogError is not null)
                {
                    await _humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint(
                        $"  catalog unavailable ({catalogError}) - showing configured models")).ConfigureAwait(false);
                }

                // The footer names the backend and the row count on both paths, so the
                // picker never looks clipped; null backend stays on the default hint.
                var candidatesFooter = string.IsNullOrWhiteSpace(backend) ? null : backend;

                // Fewer than two candidates means there is nothing to choose between: show
                // the card instead of a one-row picker. The sequential engine has no
                // picker, so it always prints the candidate panel.
                if (candidates.Count > 1 && Picker is not null)
                {
                    var list = new Tui.ChoiceList("model", candidates, footer: candidatesFooter, showRowCount: true);
                    var picked = await Picker(list, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(picked))
                    {
                        await SetModelAsync(root, backend, picked, cancellationToken).ConfigureAwait(false);
                        await _humanOut.FlushAsync().ConfigureAwait(false);
                        return;
                    }
                }

                if (candidates.Count > 0)
                {
                    WriteModelPanel(root, backend, candidates);
                }
                else
                {
                    WriteModelCard(root, backend);
                }

                await _humanOut.FlushAsync().ConfigureAwait(false);
                return;
            }

            var current = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
            await SetModelAsync(current, ReadBackend(current), arg, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"settings_error: {ex.Message}").ConfigureAwait(false);
        }

        await _humanOut.FlushAsync().ConfigureAwait(false);
    }

    private async Task SetModelAsync(JsonElement settings, string? backend, string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            await _err.WriteLineAsync("invalid_model: no model selected").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }

        var saveBody = new JsonObject { [ModelFieldFor(backend)] = model };
        await _settingsClient.UpdateAsync(saveBody, cancellationToken).ConfigureAwait(false);
        await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Switched active model to: {model}")).ConfigureAwait(false);
    }

    /// <summary>
    /// The non-picker model surface: the active selection plus the same catalog rows the
    /// picker would show, so a sequential terminal (or a cancelled TG picker) still names
    /// every choice instead of only the current model.
    /// </summary>
    private void WriteModelPanel(JsonElement settings, string? backend, IReadOnlyList<Tui.ChoiceItem> candidates)
    {
        var model = ReadModel(settings, backend) ?? string.Empty;
        var hasKey = ReadApiKeyState(settings, backend);
        var current = new List<Tui.PanelRow>
        {
            new("backend", string.IsNullOrWhiteSpace(backend) ? "unknown" : backend),
            new("model", string.IsNullOrWhiteSpace(model) ? "not set" : model),
        };

        if (hasKey is { } configured)
        {
            current.Add(new Tui.PanelRow("api key", configured ? "configured" : "missing"));
        }
        else if (IsModelsDevBackend(backend))
        {
            current.Add(new Tui.PanelRow("api key", "use /provider <name> to sign in"));
        }

        var rows = candidates
            .Select(item => new Tui.PanelRow(item.Label, item.Detail))
            .ToList();
        var sections = new List<Tui.PanelSection> { new("session", current) };
        if (rows.Count > 0)
        {
            sections.Add(new Tui.PanelSection("catalog", rows));
        }

        foreach (var line in Tui.TuiPanel.Compose(PanelWidth, "model", sections, "/model <name>"))
        {
            _humanOut.WriteLine(line);
        }
    }

    private void WriteModelCard(JsonElement settings, string? backend)
    {
        var model = ReadModel(settings, backend) ?? string.Empty;
        var hasKey = ReadApiKeyState(settings, backend);
        var rows = new List<Tui.PanelRow>
        {
            new("backend", string.IsNullOrWhiteSpace(backend) ? "unknown" : backend),
            new("model", string.IsNullOrWhiteSpace(model) ? "not set" : model),
        };

        if (hasKey is { } configured)
        {
            rows.Add(new Tui.PanelRow("api key", configured ? "configured" : "missing"));
        }
        else if (IsModelsDevBackend(backend))
        {
            rows.Add(new Tui.PanelRow("api key", "use /provider <name> to sign in"));
        }

        foreach (var line in Tui.TuiPanel.Compose(PanelWidth, "model", [new Tui.PanelSection(null, rows)], "/model <name>"))
        {
            _humanOut.WriteLine(line);
        }
    }

    private async Task HandleKeyAsync(string? arg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await WriteKeyStatusAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var isRemove = parts.Length == 2 && string.Equals(parts[0], "remove", StringComparison.OrdinalIgnoreCase);
        if (Json && !isRemove)
        {
            // /key <provider> opens the masked secret panel; --json never prompts and
            // never reads stdin. Signing out stays available: it needs no secret.
            await _err.WriteLineAsync("interactive_required: /key needs a terminal to enter the secret; run without --json or use '/key remove <provider>'.").ConfigureAwait(false);
            return;
        }

        if (isRemove)
        {
            await RemoveKeyAsync(parts[1], cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parts.Length != 1)
        {
            await _err.WriteLineAsync("usage: /key [provider] or /key remove <provider>").ConfigureAwait(false);
            return;
        }

        await SaveKeyAsync(parts[0], cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveKeyAsync(string providerArg, CancellationToken cancellationToken)
    {
        // Provider names and key fields live in one table shared with the desktop app;
        // see ProviderCredential for why a second copy is not allowed.
        var provider = ProviderCredential.Resolve(providerArg);
        if (provider is null)
        {
            await _err.WriteLineAsync($"unknown_provider: '{providerArg.Trim()}' is not a provider. Use openai, anthropic, custom, or models.dev:<id>.").ConfigureAwait(false);
            return;
        }

        if (SecretPrompt is null && SecretPanelPrompt is null)
        {
            await _err.WriteLineAsync("interactive_required: /key <provider> needs an interactive terminal.").ConfigureAwait(false);
            return;
        }

        string? secret;
        try
        {
            secret = SecretPanelPrompt is not null
                ? await SecretPanelPrompt(provider.DisplayName, cancellationToken).ConfigureAwait(false)
                : await SecretPrompt!($"API key for {provider.DisplayName} (ctrl+v to paste, esc to cancel): ", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (secret is null)
        {
            await _humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint("  no key entered — key unchanged.")).ConfigureAwait(false);
            return;
        }

        secret = secret.Trim();
        if (secret.Length == 0)
        {
            await _err.WriteLineAsync("key must be non-empty — nothing was saved.").ConfigureAwait(false);
            return;
        }

        try
        {
            JsonObject body = new();
            foreach (var (field, value) in ProviderCredential.KeyPayload(provider, secret))
            {
                body[field] = value;
            }

            await _settingsClient.UpdateAsync(body, cancellationToken).ConfigureAwait(false);
            await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Signed in to {provider.DisplayName}")).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync(CliErrorText.Format(ex)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"key_error: {ex.Message}").ConfigureAwait(false);
        }

        await _humanOut.FlushAsync().ConfigureAwait(false);
    }

    private async Task RemoveKeyAsync(string providerArg, CancellationToken cancellationToken)
    {
        var provider = ProviderCredential.Resolve(providerArg);
        if (provider is null)
        {
            await _err.WriteLineAsync($"unknown_provider: '{providerArg.Trim()}' is not a provider. Use openai, anthropic, custom, or models.dev:<id>.").ConfigureAwait(false);
            return;
        }

        try
        {
            var message = await _settingsClient.RemoveProviderAsync(provider.Choice, cancellationToken).ConfigureAwait(false);
            await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ {message}")).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync(CliErrorText.Format(ex)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"key_error: {ex.Message}").ConfigureAwait(false);
        }

        await _humanOut.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteKeyStatusAsync(CancellationToken cancellationToken)
    {
        JsonElement? settings;
        try
        {
            settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync(CliErrorText.Format(ex)).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"settings_error: {ex.Message}").ConfigureAwait(false);
            return;
        }

        var rows = new List<Tui.PanelRow>();
        if (settings.HasValue)
        {
            var states = ReadKeyStates(settings.Value);
            foreach (var (backend, alias, _, _) in KnownBackends)
            {
                var state = states.TryGetValue(alias, out var hasKey) && hasKey ? "signed in" : "no key";
                rows.Add(new Tui.PanelRow(backend, state));
            }

            try
            {
                var signedIn = await _settingsClient.GetModelsDevKeysAsync(cancellationToken).ConfigureAwait(false);
                foreach (var id in signedIn)
                {
                    var normalized = "models.dev:" + id.Trim().ToLowerInvariant();
                    if (!rows.Any(r => string.Equals(r.Label, normalized, StringComparison.OrdinalIgnoreCase)))
                    {
                        rows.Add(new Tui.PanelRow(normalized, "signed in"));
                    }
                }
            }
            catch (Exception)
            {
                // Key presence is best-effort; the legacy rows above still answer.
            }
        }

        foreach (var line in Tui.TuiPanel.Compose(PanelWidth, "keys", [new Tui.PanelSection(null, rows)], "/key <provider> · /key remove <provider>"))
        {
            _humanOut.WriteLine(line);
        }

        await _humanOut.FlushAsync().ConfigureAwait(false);
    }

    private async Task HandleStatusAsync(string? currentSessionId, string? preset, string? workspacePath, CancellationToken cancellationToken)
    {
        JsonElement? settings = null;
        try
        {
            settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The card falls back to the cached snapshot the CLI provides.
        }

        WriteStatusCard(currentSessionId, preset, workspacePath, settings);
        await _humanOut.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>/status</c> card: what the session is, what
    /// it is running on, and what this run has done so far.
    /// </summary>
    private void WriteStatusCard(string? currentSessionId, string? preset, string? workspacePath, JsonElement? settings)
    {
        var backend = settings is { } s ? ReadBackend(s) : null;
        var model = settings is { } s2 ? ReadModel(s2, backend) : null;
        var hasKey = settings is { } s3 ? ReadApiKeyState(s3, backend) : null;
        var snapshot = SnapshotProvider?.Invoke();

        var card = new Tui.TuiStatusSnapshot(
            SessionId: currentSessionId ?? snapshot?.SessionId ?? "not started",
            Preset: preset ?? "standard",
            Backend: backend ?? snapshot?.Backend ?? "unknown",
            Model: model ?? snapshot?.Model ?? string.Empty,
            Workspace: workspacePath ?? snapshot?.Workspace ?? string.Empty,
            Turn: snapshot?.Turn ?? 0,
            TotalTokens: snapshot?.TotalTokens,
            ToolsSettled: snapshot?.ToolsSettled ?? 0,
            ApiKeyConfigured: hasKey ?? snapshot?.ApiKeyConfigured);

        foreach (var line in Tui.TuiSurfaces.Status(PanelWidth, card, !FramelessReports))
        {
            _humanOut.WriteLine(line);
        }
    }

    private void WriteHelpPanel()
    {
        foreach (var line in Tui.TuiSurfaces.Help(PanelWidth, !FramelessReports))
        {
            _humanOut.WriteLine(line);
        }
    }

}
