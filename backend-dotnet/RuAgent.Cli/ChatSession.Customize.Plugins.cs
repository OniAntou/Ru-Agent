using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Protocol;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private sealed record CustomizePluginOptions(bool Computer, bool Browser);

        private async Task<string?> ShowCustomizePluginsAsync(CancellationToken ct)
        {
            using var client = new PluginsApiClient(_host._session, _host._httpClient);
            var catalog = await CustomizeCallAsync(() => client.ListAsync(ct), ct).ConfigureAwait(false);
            var options = await CustomizeCallAsync(() => ReadCustomizePluginOptionsAsync(ct), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var rows = new List<ChoiceItem>();
                if (catalog is { Plugins.Count: > 0 })
                    rows.AddRange(catalog.Plugins.Select(plugin => new ChoiceItem(plugin.Name,
                        (plugin.Enabled ? "On" : "Off") + (plugin.NeedsConsent ? " · Commands require review" : ""),
                        "plugin:" + plugin.Id)));
                else
                    rows.Add(new(catalog is null ? "Plugins unavailable" : "No plugins found", "", "", Enabled: false));
                rows.Add(new("Add or update plugin", "", "manage"));
                rows.Add(new("Computer use", OnOff(options?.Computer), "computer", Enabled: options is not null));
                rows.Add(new("Keep browser sign-ins", OnOff(options?.Browser), "browser", Enabled: options is not null));
                rows.Add(new("Reload", "Read plugins and options again", "reload"));

                var selected = await PickCustomizeAsync(1, rows, ct).ConfigureAwait(false);
                if (selected is null or PreviousCustomizeTab or NextCustomizeTab) return selected;
                if (selected == "reload")
                {
                    catalog = await CustomizeCallAsync(() => client.ListAsync(ct), ct).ConfigureAwait(false) ?? catalog;
                    options = await CustomizeCallAsync(() => ReadCustomizePluginOptionsAsync(ct), ct).ConfigureAwait(false) ?? options;
                }
                else if (selected == "manage")
                {
                    catalog = await AddCustomizePluginAsync(client, ct).ConfigureAwait(false) ?? catalog;
                }
                else if (options is not null && selected is ("computer" or "browser"))
                {
                    var updated = selected == "computer" ? options with { Computer = !options.Computer }
                        : options with { Browser = !options.Browser };
                    options = await CustomizeCallAsync(async () =>
                    {
                        var key = selected == "computer" ? "computer_use_enabled" : "browser_persistent_profile";
                        await _settingsClient.UpdateAsync(new JsonObject { [key] = selected == "computer" ? updated.Computer : updated.Browser }, ct)
                            .ConfigureAwait(false);
                        return updated;
                    }, ct).ConfigureAwait(false) ?? options;
                }
                else if (catalog?.Plugins.FirstOrDefault(plugin => "plugin:" + plugin.Id == selected) is { } plugin)
                {
                    catalog = await EditCustomizePluginAsync(client, plugin, ct).ConfigureAwait(false) ?? catalog;
                }
            }
            return null;
        }

        private async Task<CustomizePluginOptions> ReadCustomizePluginOptionsAsync(CancellationToken ct)
        {
            var settings = await _settingsClient.GetAsync(ct).ConfigureAwait(false);
            return new(
                settings.TryGetProperty("computer_use_enabled", out var computer) && computer.ValueKind == JsonValueKind.True,
                settings.TryGetProperty("browser_persistent_profile", out var browser) && browser.ValueKind == JsonValueKind.True);
        }

        private static string OnOff(bool? enabled) => enabled switch { true => "On", false => "Off", null => "Unavailable" };

        private async Task<PluginListResponse?> EditCustomizePluginAsync(PluginsApiClient client, PluginItem plugin, CancellationToken ct)
        {
            var action = await PickCustomizeActionAsync(plugin.Name,
                [
                    new(plugin.Enabled ? "Turn off" : "Turn on", "", "toggle"),
                    new("Review commands", "", "commands", Enabled: plugin.Executable is { Count: > 0 }),
                    new("Details", "", "details"),
                ], ct).ConfigureAwait(false);
            if (action == "details")
            {
                await TgPanelRequests.RequestDocumentAsync(tgWindow!, "Plugin · " + plugin.Id,
                    new[] { plugin.Description ?? "", plugin.Directory, plugin.Version ?? "", plugin.Consent }
                        .Concat(plugin.Diagnostics).Select(TuiText.SanitizeUntrusted).ToArray(), ct).ConfigureAwait(false);
                return null;
            }
            if (action is not ("toggle" or "commands")) return null;

            var enabling = action == "toggle" && !plugin.Enabled;
            PluginListResponse? approval = null;
            if (action == "commands" || enabling && plugin.NeedsConsent)
            {
                var commands = plugin.Executable ?? [];
                if (commands.Count == 0 || plugin.Digest is not { Length: 64 }) return null;
                // JSON escaping keeps control characters visible instead of letting a command redraw the consent screen.
                await TgPanelRequests.RequestDocumentAsync(tgWindow!, "Commands · " + plugin.Id,
                    commands.Select(command => JsonSerializer.Serialize(command)).ToArray(), ct).ConfigureAwait(false);
                var choices = new List<ChoiceItem> { new("Cancel", "", "cancel") };
                if (plugin.Consent != "approved")
                    choices.Add(new(enabling ? "Allow commands and turn on" : "Allow commands", "", "approve"));
                if (enabling)
                    choices.Add(new("Turn on without commands", "", "without"));
                if (!enabling && plugin.Consent == "approved")
                    choices.Add(new("Stop allowing commands", "", "revoke"));
                var consent = await PickCustomizeActionAsync("Plugin command permissions", choices, ct).ConfigureAwait(false);
                if (consent is null or "cancel") return null;
                if (consent is "approve" or "revoke")
                {
                    approval = await CustomizeCallAsync(() => client.UpdateAsync(new(consent, plugin.Id,
                        Digest: consent == "approve" ? plugin.Digest : null), ct), ct).ConfigureAwait(false);
                    if (!enabling || approval is null) return approval;
                }
            }

            return await CustomizeCallAsync(() => client.UpdateAsync(new("set_enabled", plugin.Id, Enabled: enabling), ct), ct)
                .ConfigureAwait(false) ?? approval;
        }

        private async Task<PluginListResponse?> AddCustomizePluginAsync(PluginsApiClient client, CancellationToken ct)
        {
            var action = await PickCustomizeActionAsync("Add or update plugin",
                [
                    new("Create a plugin", "Start with instructions and a skill", "create"),
                    new("Add from folder", "", "import"),
                    new("Install from GitHub", "", "install"),
                    new("Update from GitHub", "", "update"),
                ], ct).ConfigureAwait(false);
            if (action is null) return null;
            if (action == "create")
            {
                var name = await ReadCustomizeValueAsync("Plugin name", "Enter a display name.", ct).ConfigureAwait(false);
                if (name is null) return null;
                var id = await ReadCustomizeValueAsync("Plugin ID",
                    "Enter 1–24 lowercase letters, digits or dashes, starting with a letter.", ct).ConfigureAwait(false);
                if (id is null) return null;
                if (!UserPlugins.IsValidId(id))
                {
                    await TgPanelRequests.RequestDocumentAsync(tgWindow!, "Plugin ID", ["The ID is invalid."], ct).ConfigureAwait(false);
                    return null;
                }
                return await CustomizeCallAsync(() => client.UpdateAsync(new("create", id, Name: name,
                    WithInstructions: true, WithSkill: true), ct), ct).ConfigureAwait(false);
            }
            var value = await ReadCustomizeValueAsync("Plugin source", action == "import"
                ? "Enter the full path to a folder containing ru-plugin.json."
                : "Paste a GitHub plugin folder URL pinned to a full commit SHA.", ct).ConfigureAwait(false);
            if (value is null) return null;
            return action == "import"
                ? await CustomizeCallAsync(() => client.ImportAsync(value, ct), ct).ConfigureAwait(false)
                : await CustomizeCallAsync(() => client.InstallPackageAsync(value, update: action == "update", ct), ct)
                    .ConfigureAwait(false);
        }

        private async Task<string?> ReadCustomizeValueAsync(string title, string description, CancellationToken ct)
        {
            var value = await TgPanelRequests.RequestTextAsync(tgWindow!, title, description, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
