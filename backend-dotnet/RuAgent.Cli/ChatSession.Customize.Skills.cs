using RuAgent.Client;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private async Task<string?> ShowCustomizeSkillsAsync(CancellationToken ct)
        {
            using var client = new SkillsApiClient(_host._session, _host._httpClient);
            var workspace = canonicalWorkspace;
            var catalog = await CustomizeCallAsync(() => client.ListAsync(workspace, cancellationToken: ct), ct)
                .ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var rows = new List<ChoiceItem>();
                if (catalog is { Items.Count: > 0 })
                    rows.AddRange(catalog.Items.Select(item => new ChoiceItem(item.Name,
                        item.Enabled ? item.ModelInvocable ? "On · Automatic" : "On · Manual" : "Off", "skill:" + item.Id)));
                else
                    rows.Add(new ChoiceItem(catalog is null ? "Catalog unavailable" : "No skills found", "", "", Enabled: false));
                rows.Add(new ChoiceItem("Reload", "Read the catalog again", "reload"));
                rows.Add(new ChoiceItem("Manage skill folders and packages", "", "manage", Enabled: !string.IsNullOrEmpty(workspace)));
                var selected = await PickCustomizeAsync(0, rows, ct).ConfigureAwait(false);
                if (selected is null or PreviousCustomizeTab or NextCustomizeTab) return selected;
                if (selected == "reload")
                {
                    catalog = await CustomizeCallAsync(() => client.ListAsync(workspace, refresh: true, cancellationToken: ct), ct)
                        .ConfigureAwait(false) ?? catalog;
                    continue;
                }
                if (selected == "manage")
                {
                    catalog = await ManageCustomizeSkillsAsync(client, workspace, ct).ConfigureAwait(false) ?? catalog;
                    continue;
                }
                var item = catalog?.Items.FirstOrDefault(skill => "skill:" + skill.Id == selected);
                if (item is null) continue;
                var action = await PickCustomizeActionAsync(item.Name,
                    [
                        new("Use in chat", "", "use", item.Enabled && item.UserInvocable),
                        new(item.Enabled ? "Turn off" : "Turn on", "", "enabled", workspace.Length > 0),
                        new(item.ModelInvocable ? "Require explicit request" : "Allow automatic use", "", "automatic",
                            item.Enabled && workspace.Length > 0),
                        new("Details", "", "details"),
                    ], ct).ConfigureAwait(false);
                if (action == "use")
                {
                    RunOnTgUi(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        tgWindow!.Composer.Input.Text = $"Use the {item.Id} skill.";
                        tgWindow.Composer.Input.InsertionPoint = tgWindow.Composer.Input.Text.Length;
                    });
                    return null;
                }
                if (action == "details")
                {
                    await TgPanelRequests.RequestDocumentAsync(tgWindow!, "Skill · " + item.Id,
                        [TuiText.SanitizeUntrusted(item.Description), TuiText.SanitizeUntrusted(item.SourcePath)], ct)
                        .ConfigureAwait(false);
                }
                if (action is "enabled" or "automatic")
                {
                    var request = action == "enabled"
                        ? new SkillSettingsRequest(workspace, "set_enabled", Id: item.Id, Enabled: !item.Enabled)
                        : new SkillSettingsRequest(workspace, "set_implicit", Id: item.Id, Enabled: !item.ModelInvocable);
                    catalog = await CustomizeCallAsync(() => client.UpdateAsync(request, ct), ct).ConfigureAwait(false) ?? catalog;
                }
            }
            return null;
        }

        private async Task<SkillCatalogResponse?> ManageCustomizeSkillsAsync(SkillsApiClient client, string workspace, CancellationToken ct)
        {
            var action = await PickCustomizeActionAsync("Manage skills",
                [
                    new("Add local skill folder", "", "add_root"),
                    new("Remove skill folder from catalog", "", "remove_root"),
                    new("Install from GitHub", "", "install"),
                    new("Update from GitHub", "", "update"),
                ], ct).ConfigureAwait(false);
            if (action is null) return null;

            var package = action is "install" or "update";
            var value = (await TgPanelRequests.RequestTextAsync(tgWindow!,
                package ? "Skill package" : "Skill folder", package
                    ? "Paste a GitHub skill folder URL pinned to a full commit SHA."
                    : "Enter the full path of the skill folder.", ct).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (action == "remove_root" && await PickCustomizeActionAsync("Remove this folder from the skill catalog?",
                [new("Cancel", "", "cancel"), new("Remove from catalog", value, "remove")], ct).ConfigureAwait(false) != "remove")
                return null;
            if (package)
                return (await CustomizeCallAsync(() => client.InstallPackageAsync(new(workspace, value, action), ct), ct)
                    .ConfigureAwait(false))?.Catalog;
            return await CustomizeCallAsync(() => client.UpdateAsync(new(workspace, action, Root: value), ct), ct).ConfigureAwait(false);
        }
    }
}
