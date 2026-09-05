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

    public AgentSlashCommands(SettingsApiClient settingsClient, TextWriter outWriter, TextWriter errWriter)
    {
        _settingsClient = settingsClient ?? throw new ArgumentNullException(nameof(settingsClient));
        _out = outWriter ?? throw new ArgumentNullException(nameof(outWriter));
        _err = errWriter ?? throw new ArgumentNullException(nameof(errWriter));
    }

    /// <returns>True if the slash command was handled (caller should continue).</returns>
    public async Task<bool> TryHandleAsync(string slashCmd, string? arg, CancellationToken cancellationToken)
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
}
