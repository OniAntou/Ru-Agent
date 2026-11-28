using RuAgent.Client;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class CustomizeWorkflowTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("allow")]
    [InlineData("without")]
    public async Task PluginOptionsKeepEnableAndDigestApprovalSeparate(string choice)
    {
        var digest = new string('a', 64);
        var workspace = Path.GetTempPath();
        var enabled = false;
        var approved = false;
        using var host = new CustomizeHost((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/agent/v1/plugins/settings")
            {
                if (body.Contains("\"action\":\"approve\"")) approved = true;
                if (body.Contains("\"action\":\"set_enabled\"")) enabled = true;
            }
            if (request.RequestUri!.AbsolutePath.StartsWith("/agent/v1/plugins", StringComparison.Ordinal))
                return new PluginListResponse(workspace,
                    [new("fixture", "Fixture plugin", "", "1", "local", workspace, enabled, false, false, true, false, [],
                        approved ? "approved" : "needs_approval", ["Write-Output fixture"], digest)], []);
            return new { status = "ok" };
        });
        await RunCustomizeAsync(host, workspace, "/customize plugins", (stage, app, page, screen) =>
        {
            if (stage == 1 && page.IsModalPanelOpen && screen.Contains("Fixture plugin"))
                app.InjectKey(Key.Enter);
            else if (stage == 2 && page.IsModalPanelOpen && screen.Contains("Turn on"))
                app.InjectKey(Key.Enter);
            else if (stage == 3 && page.IsDocumentPanelOpen && screen.Contains("Write-Output fixture"))
            {
                Assert.DoesNotContain(host.Requests, r => r.StartsWith("POST /agent/v1/plugins", StringComparison.Ordinal));
                app.InjectKey(Key.Enter);
            }
            else if (stage == 4 && page.IsModalPanelOpen && screen.Contains("Plugin command permissions"))
                app.InjectKey(choice == "allow" ? Key.D2 : choice == "without" ? Key.D3 : Key.Enter);
            else if (stage == 5 && page.IsModalPanelOpen && screen.Contains("Add or update plugin"))
            {
                app.InjectKey(Key.Esc);
                return true;
            }
            else return null;
            return false;
        });
        var changes = host.Requests.Where(r => r.StartsWith("POST /agent/v1/plugins/settings ", StringComparison.Ordinal)).ToArray();
        if (choice == "cancel") Assert.Empty(changes);
        else if (choice == "without")
            Assert.Contains("\"action\":\"set_enabled\"", Assert.Single(changes));
        else
        {
            Assert.Equal(2, changes.Length);
            Assert.Contains("\"action\":\"approve\"", changes[0]);
            Assert.Contains($"\"digest\":\"{digest}\"", changes[0]);
            Assert.Contains("\"action\":\"set_enabled\"", changes[1]);
        }
    }
}
