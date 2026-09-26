using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;

namespace RuAgent.Cli;

/// <summary>
/// Slash handlers extracted from AgentCommands.ChatAsync (slice 1).
/// Owns /backend + /model display/update. Business logic unchanged.
/// </summary>
public sealed class AgentSlashCommands
{
    private readonly SettingsApiClient _settingsClient;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly SessionApiClient? _sessionClient;

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

    /// <summary>Live run counters for the status card. Wired by the CLI; may be absent in tests.</summary>
    public Func<Tui.TuiStatusSnapshot?>? SnapshotProvider { get; set; }

    public int PanelWidth { get; set; } = Tui.TuiViewport.FallbackWidth;

    public AgentSlashCommands(SettingsApiClient settingsClient, TextWriter outWriter, TextWriter errWriter)
        : this(settingsClient, null, outWriter, errWriter)
    {
    }

    public AgentSlashCommands(SettingsApiClient settingsClient, SessionApiClient? sessionClient, TextWriter outWriter, TextWriter errWriter)
    {
        _settingsClient = settingsClient ?? throw new ArgumentNullException(nameof(settingsClient));
        _sessionClient = sessionClient;
        _out = outWriter ?? throw new ArgumentNullException(nameof(outWriter));
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

        if (slashCmd is "/model" or "/provider")
        {
            await HandleModelAsync(arg, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/key")
        {
            await HandleKeyAsync(arg, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/tree")
        {
            await HandleTreeAsync(currentSessionId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/status" or "/stats" or "/tokens")
        {
            await HandleStatsAsync(currentSessionId, preset, workspacePath, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/help")
        {
            WriteHelpPanel();
            await _out.FlushAsync().ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/export")
        {
            await HandleExportAsync(arg, currentSessionId, cancellationToken).ConfigureAwait(false);
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

    private async Task HandleBackendAsync(string? arg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            // No argument: offer the picker instead of making the operator recall the names.
            var settings = await TryReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            var current = settings.HasValue ? ReadBackend(settings.Value) : null;
            var keyStates = settings.HasValue
                ? await ReadAllKeyStatesAsync(settings.Value, cancellationToken).ConfigureAwait(false)
                : null;
            var list = new Tui.ChoiceList("backend", Tui.TuiSurfaces.BackendChoices(current, keyStates));
            var picked = Picker is null ? null : await Picker(list, cancellationToken).ConfigureAwait(false);
            if (picked is null)
            {
                // Cancelled (nothing to add) or not interactive (list the options).
                if (Picker is null || !IsInteractiveConsole())
                {
                    WriteBackendPanel(current, keyStates);
                }

                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            arg = picked;
        }

        await SwitchBackendAsync(arg, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsInteractiveConsole() => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    private void WriteBackendPanel(string? current, IReadOnlyDictionary<string, bool>? keyStates)
    {
        var rows = Tui.TuiSurfaces.BackendChoices(current, keyStates)
            .Select(item => new Tui.PanelRow(item.Label, item.Detail))
            .ToList();

        foreach (var line in Tui.TuiPanel.Compose(PanelWidth, "backend", [new Tui.PanelSection(null, rows)], "/backend <name>"))
        {
            _out.WriteLine(line);
        }
    }

    private async Task SwitchBackendAsync(string arg, CancellationToken cancellationToken)
    {
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
            await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Switched backend to: {targetBackend}")).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"backend_error: {ex.Message}").ConfigureAwait(false);
        }

        await _out.FlushAsync().ConfigureAwait(false);
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
                string? candidatesFooter = null;
                try
                {
                    var catalog = await _settingsClient.GetModelsAsync(backend, cancellationToken).ConfigureAwait(false);
                    if (catalog.Count > 0)
                    {
                        candidates = Tui.TuiSurfaces.ModelCatalogChoices(backend, ReadModel(root, backend), catalog, keyStates);
                        candidatesFooter = backend;
                    }
                }
                catch (Exception)
                {
                    // Live catalog is best-effort; configured models still answer.
                }

                candidates ??= Tui.TuiSurfaces.ModelChoices(backend, configured, keyStates);

                // Fewer than two candidates means there is nothing to choose between: show
                // the card instead of a one-row picker.
                if (candidates.Count > 1 && Picker is not null)
                {
                    var list = new Tui.ChoiceList("model", candidates, footer: candidatesFooter);
                    var picked = await Picker(list, cancellationToken).ConfigureAwait(false);
                    if (picked is not null)
                    {
                        await SetModelAsync(root, backend, picked, cancellationToken).ConfigureAwait(false);
                        await _out.FlushAsync().ConfigureAwait(false);
                        return;
                    }
                }

                WriteModelCard(root, backend);
                await _out.FlushAsync().ConfigureAwait(false);
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

        await _out.FlushAsync().ConfigureAwait(false);
    }

    private async Task SetModelAsync(JsonElement settings, string? backend, string model, CancellationToken cancellationToken)
    {
        var saveBody = new JsonObject { [ModelFieldFor(backend)] = model };
        await _settingsClient.UpdateAsync(saveBody, cancellationToken).ConfigureAwait(false);
        await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Switched active model to: {model}")).ConfigureAwait(false);
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
            rows.Add(new Tui.PanelRow("api key", "use /key <provider> to sign in"));
        }

        foreach (var line in Tui.TuiPanel.Compose(PanelWidth, "model", [new Tui.PanelSection(null, rows)], "/model <name>"))
        {
            _out.WriteLine(line);
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
        if (parts.Length == 2 && string.Equals(parts[0], "remove", StringComparison.OrdinalIgnoreCase))
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
            await _out.WriteLineAsync(Tui.TuiPalette.DimPaint("  no key entered — key unchanged.")).ConfigureAwait(false);
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
            await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Signed in to {provider.DisplayName}")).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync(CliErrorText.Format(ex)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"key_error: {ex.Message}").ConfigureAwait(false);
        }

        await _out.FlushAsync().ConfigureAwait(false);
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
            await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ {message}")).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync(CliErrorText.Format(ex)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"key_error: {ex.Message}").ConfigureAwait(false);
        }

        await _out.FlushAsync().ConfigureAwait(false);
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
            _out.WriteLine(line);
        }

        await _out.FlushAsync().ConfigureAwait(false);
    }

    private async Task HandleTreeAsync(string? currentSessionId, CancellationToken cancellationToken)
    {
        if (_sessionClient == null)
        {
            await _err.WriteLineAsync("tree_unavailable: session client not configured").ConfigureAwait(false);
            return;
        }

        try
        {
            var listJson = await _sessionClient.ListAsync(cancellationToken).ConfigureAwait(false);
            if (!listJson.TryGetProperty("sessions", out var sessionsProp) || sessionsProp.ValueKind != JsonValueKind.Array)
            {
                await _out.WriteLineAsync(Tui.TuiPalette.DimPaint("  (No sessions found)")).ConfigureAwait(false);
                return;
            }

            var sessions = new List<SessionTreeNode>();
            foreach (var item in sessionsProp.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                var title = item.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "Untitled" : "Untitled";
                var parentId = item.TryGetProperty("parent_session_id", out var pProp) && pProp.ValueKind == JsonValueKind.String ? pProp.GetString() : null;
                var count = item.TryGetProperty("message_count", out var mcProp) && mcProp.TryGetInt32(out var mc) ? mc : 0;
                sessions.Add(new SessionTreeNode(id, title, parentId, count));
            }

            if (sessions.Count == 0)
            {
                await _out.WriteLineAsync(Tui.TuiPalette.DimPaint("  (No sessions found)")).ConfigureAwait(false);
                return;
            }

            await _out.WriteLineAsync(Tui.TuiPalette.BoldPaint(Tui.TuiPalette.Accent("Session Tree (DAG):"))).ConfigureAwait(false);

            var sessionMap = sessions.ToDictionary(s => s.Id);
            var childrenMap = new Dictionary<string, List<SessionTreeNode>>();
            var roots = new List<SessionTreeNode>();

            foreach (var s in sessions)
            {
                if (!string.IsNullOrEmpty(s.ParentId) && sessionMap.ContainsKey(s.ParentId))
                {
                    if (!childrenMap.TryGetValue(s.ParentId, out var chList))
                    {
                        chList = new List<SessionTreeNode>();
                        childrenMap[s.ParentId] = chList;
                    }
                    chList.Add(s);
                }
                else
                {
                    roots.Add(s);
                }
            }

            void PrintNode(SessionTreeNode node, string indent, bool isLast)
            {
                var marker = isLast ? "└── " : "├── ";
                bool isCurrent = string.Equals(node.Id, currentSessionId, StringComparison.Ordinal);
                var currentTag = isCurrent ? Tui.TuiPalette.Ok(" * (current)") : "";
                var shortId = node.Id.Length > 8 ? node.Id[..8] : node.Id;
                var titleText = string.IsNullOrWhiteSpace(node.Title) ? "Untitled" : node.Title;

                var line = $"{indent}{marker}{Tui.TuiPalette.BoldPaint(shortId)} - {titleText} ({node.MessageCount} msgs){currentTag}";
                _out.WriteLine(line);

                if (childrenMap.TryGetValue(node.Id, out var children))
                {
                    var childIndent = indent + (isLast ? "    " : "│   ");
                    for (int i = 0; i < children.Count; i++)
                    {
                        PrintNode(children[i], childIndent, i == children.Count - 1);
                    }
                }
            }

            for (int i = 0; i < roots.Count; i++)
            {
                PrintNode(roots[i], "", i == roots.Count - 1);
            }
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"tree_error: {ex.Message}").ConfigureAwait(false);
        }
        await _out.FlushAsync().ConfigureAwait(false);
    }

    private async Task HandleStatsAsync(string? currentSessionId, string? preset, string? workspacePath, CancellationToken cancellationToken)
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
        await _out.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// One card for <c>/status</c>, <c>/stats</c> and <c>/tokens</c>: what the session is, what
    /// it is running on, and what this run has done so far.
    /// </summary>
    private void WriteStatusCard(string? currentSessionId, string? preset, string? workspacePath, JsonElement? settings)
    {
        var backend = settings is { } s ? ReadBackend(s) : null;
        var model = settings is { } s2 ? ReadModel(s2, backend) : null;
        var hasKey = settings is { } s3 ? ReadApiKeyState(s3, backend) : null;
        var snapshot = SnapshotProvider?.Invoke();

        var card = new Tui.TuiStatusSnapshot(
            SessionId: currentSessionId ?? "—",
            Preset: preset ?? "standard",
            Backend: backend ?? snapshot?.Backend ?? "unknown",
            Model: model ?? snapshot?.Model ?? string.Empty,
            Workspace: workspacePath ?? snapshot?.Workspace ?? string.Empty,
            Turn: snapshot?.Turn ?? 0,
            TotalTokens: snapshot?.TotalTokens,
            ToolsSettled: snapshot?.ToolsSettled ?? 0,
            ApiKeyConfigured: hasKey ?? snapshot?.ApiKeyConfigured);

        foreach (var line in Tui.TuiSurfaces.Status(PanelWidth, card))
        {
            _out.WriteLine(line);
        }
    }

    private void WriteHelpPanel()
    {
        foreach (var line in Tui.TuiSurfaces.Help(PanelWidth))
        {
            _out.WriteLine(line);
        }
    }

    private async Task HandleExportAsync(string? arg, string? currentSessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentSessionId) || _sessionClient == null)
        {
            await _err.WriteLineAsync("export_failed: no active session to export").ConfigureAwait(false);
            return;
        }

        try
        {
            var sessionDoc = await _sessionClient.GetSessionAsync(currentSessionId, cancellationToken).ConfigureAwait(false);
            var title = "RuAgent Conversation";
            var messages = new List<(string Role, string Content, string Time)>();

            if (sessionDoc.TryGetProperty("session", out var sessProp))
            {
                if (sessProp.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                {
                    title = tProp.GetString() ?? title;
                }

                if (sessProp.TryGetProperty("messages", out var msgsProp) && msgsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in msgsProp.EnumerateArray())
                    {
                        var role = m.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
                        var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                        var time = m.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                        messages.Add((role, content, time));
                    }
                }
            }

            var exportPath = string.IsNullOrWhiteSpace(arg)
                ? $"session-{(currentSessionId.Length > 8 ? currentSessionId[..8] : currentSessionId)}.md"
                : arg.Trim();

            var sb = new StringBuilder();
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.AppendLine($"- **Session ID**: `{currentSessionId}`");
            sb.AppendLine($"- **Exported At**: `{DateTime.UtcNow:u}`");
            sb.AppendLine($"- **Total Messages**: {messages.Count}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            foreach (var msg in messages)
            {
                var roleIcon = msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "🤖 Assistant" : "👤 User";
                var timeInfo = string.IsNullOrWhiteSpace(msg.Time) ? "" : $" *({msg.Time})*";
                sb.AppendLine($"### {roleIcon}{timeInfo}");
                sb.AppendLine();
                sb.AppendLine(msg.Content);
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(exportPath, sb.ToString(), cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Conversation exported to: {exportPath}")).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"export_error: {ex.Message}").ConfigureAwait(false);
        }
        await _out.FlushAsync().ConfigureAwait(false);
    }

    private sealed record SessionTreeNode(string Id, string Title, string? ParentId, int MessageCount);
}
