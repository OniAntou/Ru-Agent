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

        if (slashCmd is "/tree")
        {
            await HandleTreeAsync(currentSessionId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/stats" or "/tokens")
        {
            await HandleStatsAsync(currentSessionId, preset, workspacePath, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (slashCmd is "/export")
        {
            await HandleExportAsync(arg, currentSessionId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task HandleBackendAsync(string? arg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _out.WriteLineAsync("Usage: /backend <kilo|opencode|ollama|lmstudio|openai|anthropic>").ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            var targetBackend = arg.Trim().ToLowerInvariant() switch
            {
                "kilo" => "Kilo Gateway",
                "opencode" => "OpenCode Zen",
                "ollama" => "Ollama",
                "lmstudio" => "LM Studio",
                "openai" => "OpenAI",
                "anthropic" => "Anthropic",
                _ => arg.Trim(),
            };

            var saveBody = new JsonObject { ["backend"] = targetBackend };
            await _settingsClient.UpdateAsync(saveBody, cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync(Tui.AnsiTheme.Green($"✓ Switched backend to: {targetBackend}")).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"backend_error: {ex.Message}").ConfigureAwait(false);
        }

        await _out.FlushAsync().ConfigureAwait(false);
    }

    private async Task HandleModelAsync(string? arg, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                var current = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
                var curBackend = current.TryGetProperty("backend", out var b) ? b.GetString() : "Kilo Gateway";
                var modelField = curBackend switch
                {
                    "Kilo Gateway" => "kilo_model",
                    "OpenCode Zen" => "opencode_model",
                    "Ollama" => "ollama_model",
                    "LM Studio" => "lmstudio_model",
                    "OpenAI" => "openai_model",
                    "Anthropic" => "anthropic_model",
                    _ => "model",
                };

                var saveBody = new JsonObject { [modelField] = arg };
                await _settingsClient.UpdateAsync(saveBody, cancellationToken).ConfigureAwait(false);
                await _out.WriteLineAsync(Tui.AnsiTheme.Green($"✓ Switched active model to: {arg}")).ConfigureAwait(false);
            }
            else
            {
                var root = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
                var backend = root.TryGetProperty("backend", out var b) ? b.GetString() : "Unknown";
                var model = backend switch
                {
                    "Kilo Gateway" => root.TryGetProperty("kilo_model", out var km) ? km.GetString() : "",
                    "OpenCode Zen" => root.TryGetProperty("opencode_model", out var om) ? om.GetString() : "",
                    "Ollama" => root.TryGetProperty("ollama_model", out var olm) ? olm.GetString() : "",
                    "LM Studio" => root.TryGetProperty("lmstudio_model", out var lmm) ? lmm.GetString() : "",
                    "OpenAI" => root.TryGetProperty("openai_model", out var oam) ? oam.GetString() : "",
                    "Anthropic" => root.TryGetProperty("anthropic_model", out var anm) ? anm.GetString() : "",
                    _ => root.TryGetProperty("model", out var m) ? m.GetString() : "",
                };
                var hasKey = backend switch
                {
                    "Kilo Gateway" => root.TryGetProperty("has_kilo_key", out var hk) && hk.GetBoolean(),
                    "OpenCode Zen" => root.TryGetProperty("has_opencode_key", out var hok) && hok.GetBoolean(),
                    "OpenAI" => root.TryGetProperty("has_openai_api_key", out var hoak) && hoak.GetBoolean(),
                    "Anthropic" => root.TryGetProperty("has_anthropic_api_key", out var haak) && haak.GetBoolean(),
                    _ => true,
                };

                await _out.WriteLineAsync(Tui.AnsiTheme.Cyan($"  • Backend Provider: {backend}")).ConfigureAwait(false);
                await _out.WriteLineAsync(Tui.AnsiTheme.Cyan($"  • Active Model:     {model}")).ConfigureAwait(false);
                await _out.WriteLineAsync(hasKey
                    ? Tui.AnsiTheme.Green("  • API Key:          Configured (OK)")
                    : Tui.AnsiTheme.Red("  • API Key:          MISSING (Please configure in RuApp or settings)")).ConfigureAwait(false);
                await _out.WriteLineAsync(Tui.AnsiTheme.Dim("  • Tip: Use '/model <name>' to switch models on the fly.")).ConfigureAwait(false);
            }
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"settings_error: {ex.Message}").ConfigureAwait(false);
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
                await _out.WriteLineAsync(Tui.AnsiTheme.Dim("  (No sessions found)")).ConfigureAwait(false);
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
                await _out.WriteLineAsync(Tui.AnsiTheme.Dim("  (No sessions found)")).ConfigureAwait(false);
                return;
            }

            await _out.WriteLineAsync(Tui.AnsiTheme.Bold(Tui.AnsiTheme.Cyan("Session Tree (DAG):"))).ConfigureAwait(false);

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
                var currentTag = isCurrent ? Tui.AnsiTheme.Green(" * (current)") : "";
                var shortId = node.Id.Length > 8 ? node.Id[..8] : node.Id;
                var titleText = string.IsNullOrWhiteSpace(node.Title) ? "Untitled" : node.Title;

                var line = $"{indent}{marker}{Tui.AnsiTheme.Bold(shortId)} - {titleText} ({node.MessageCount} msgs){currentTag}";
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
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"tree_error: {ex.Message}").ConfigureAwait(false);
        }
        await _out.FlushAsync().ConfigureAwait(false);
    }

    private async Task HandleStatsAsync(string? currentSessionId, string? preset, string? workspacePath, CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
            var backend = settings.TryGetProperty("backend", out var b) ? b.GetString() : "Unknown";
            var model = backend switch
            {
                "Kilo Gateway" => settings.TryGetProperty("kilo_model", out var km) ? km.GetString() : "",
                "OpenCode Zen" => settings.TryGetProperty("opencode_model", out var om) ? om.GetString() : "",
                "Ollama" => settings.TryGetProperty("ollama_model", out var olm) ? olm.GetString() : "",
                "LM Studio" => settings.TryGetProperty("lmstudio_model", out var lmm) ? lmm.GetString() : "",
                "OpenAI" => settings.TryGetProperty("openai_model", out var oam) ? oam.GetString() : "",
                "Anthropic" => settings.TryGetProperty("anthropic_model", out var anm) ? anm.GetString() : "",
                _ => settings.TryGetProperty("model", out var m) ? m.GetString() : "",
            };

            await _out.WriteLineAsync(Tui.AnsiTheme.Bold(Tui.AnsiTheme.Cyan("Session & Token Stats:"))).ConfigureAwait(false);
            await _out.WriteLineAsync(Tui.AnsiTheme.Dim($"  • Session ID:       ") + (currentSessionId ?? "N/A")).ConfigureAwait(false);
            await _out.WriteLineAsync(Tui.AnsiTheme.Dim($"  • Preset:           ") + (preset ?? "standard")).ConfigureAwait(false);
            await _out.WriteLineAsync(Tui.AnsiTheme.Dim($"  • Backend Provider: ") + backend).ConfigureAwait(false);
            await _out.WriteLineAsync(Tui.AnsiTheme.Dim($"  • Model:            ") + model).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(workspacePath))
            {
                await _out.WriteLineAsync(Tui.AnsiTheme.Dim($"  • Workspace:        ") + workspacePath).ConfigureAwait(false);
            }
            await _out.WriteLineAsync(Tui.AnsiTheme.Dim($"  • Status:           ") + Tui.AnsiTheme.Green("Active & Connected")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"stats_error: {ex.Message}").ConfigureAwait(false);
        }
        await _out.FlushAsync().ConfigureAwait(false);
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
            await _out.WriteLineAsync(Tui.AnsiTheme.Green($"✓ Conversation exported to: {exportPath}")).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"export_error: {ex.Message}").ConfigureAwait(false);
        }
        await _out.FlushAsync().ConfigureAwait(false);
    }

    private sealed record SessionTreeNode(string Id, string Title, string? ParentId, int MessageCount);
}
