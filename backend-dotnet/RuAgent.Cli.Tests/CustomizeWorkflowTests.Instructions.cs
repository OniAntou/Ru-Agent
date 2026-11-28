using RuAgent.Client;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class CustomizeWorkflowTests
{
    [Fact]
    public async Task CustomizeSwitchesTabsAndReadsOnlyTheOpenedCatalogs()
    {
        var workspace = Path.GetTempPath();
        using var host = new CustomizeHost((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/agent/v1/skills" => new SkillCatalogResponse([], [], false, 0),
            "/agent/v1/plugins" => new PluginListResponse(workspace, [], []),
            _ => new { status = "ok" },
        });
        await RunCustomizeAsync(host, workspace, "/customize", (stage, app, page, screen) =>
        {
            if (stage == 1 && page.IsModalPanelOpen && screen.Contains("Choose a section"))
            {
                Assert.Contains("Skills", screen);
                Assert.Contains("Plugins", screen);
                Assert.Contains("Instructions", screen);
                Assert.DoesNotContain(host.Requests, request => request.Contains("/agent/v1/skills")
                    || request.Contains("/agent/v1/plugins"));
                app.InjectKey(Key.Enter);
            }
            else if (stage == 2 && page.IsModalPanelOpen && screen.Contains("[Skills]"))
            {
                Assert.DoesNotContain(host.Requests, request => request.Contains("/agent/v1/plugins"));
                app.InjectKey(Key.CursorRight);
            }
            else if (stage == 3 && page.IsModalPanelOpen && screen.Contains("[Plugins]"))
                app.InjectKey(Key.CursorRight);
            else if (stage == 4 && page.IsModalPanelOpen && screen.Contains("[Instructions]"))
            {
                app.InjectKey(Key.Esc);
                return true;
            }
            else return null;
            return false;
        });
        Assert.Single(host.Requests, request => request.StartsWith("GET /agent/v1/skills ", StringComparison.Ordinal));
        Assert.Single(host.Requests, request => request.StartsWith("GET /agent/v1/plugins ", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Requests, request => request.StartsWith("POST ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomizeMenuCanCloseOrOpenInstructionsWithoutReadingCatalogs(bool openInstructions)
    {
        var workspace = Path.GetTempPath();
        using var host = new CustomizeHost((_, _) => new { status = "ok" });
        await RunCustomizeAsync(host, workspace, "/customize", (stage, app, page, screen) =>
        {
            if (stage == 1 && page.IsModalPanelOpen && screen.Contains("Choose a section"))
            {
                app.InjectKey(openInstructions ? Key.D3 : Key.Esc);
                return !openInstructions;
            }
            if (stage == 2 && page.IsModalPanelOpen && screen.Contains("[Instructions]"))
            {
                app.InjectKey(Key.Esc);
                return true;
            }
            return null;
        });
        Assert.DoesNotContain(host.Requests, request => request.Contains("/agent/v1/skills")
            || request.Contains("/agent/v1/plugins") || request.StartsWith("POST ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("f2")]
    [InlineData("ctrl+s")]
    [InlineData("discard")]
    public async Task InstructionsSaveOrDiscardOnlyTheSelectedScope(string action)
    {
        var workspace = Directory.CreateTempSubdirectory("ru-customize-instructions-");
        try
        {
            var path = InstructionFileStore.WorkspacePath(workspace.FullName)!;
            var save = action != "discard";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const string original = "Old instructions";
            const string edited = "Use English for docs.\nGiữ nguyên checklist.";
            File.WriteAllText(path, original);
            using var host = new CustomizeHost((_, _) => new { status = "ok" });
            var editorClosed = false;
            await RunCustomizeAsync(host, workspace.FullName, "/customize instructions", (stage, app, page, screen) =>
            {
                if (stage == 1 && page.IsModalPanelOpen && screen.Contains("Shared project files"))
                    app.InjectKey(Key.D2);
                else if (stage == 2 && page.IsModalPanelOpen && screen.Contains("Instructions · This workspace"))
                {
                    Assert.Equal(original, page.QuestionPanel.Editor.Text);
                    page.QuestionPanel.Editor.Text = edited;
                    app.InjectKey(action == "f2" ? Key.F2 : save ? Key.S.WithCtrl : Key.Esc);
                    editorClosed = save;
                }
                else if (!editorClosed && stage == 3 && screen.Contains("Unsaved"))
                {
                    Assert.Equal(original, File.ReadAllText(path));
                    app.InjectKey(Key.D);
                    editorClosed = true;
                }
                else if (editorClosed && page.IsModalPanelOpen && screen.Contains("Shared project files"))
                {
                    Assert.Equal(save ? edited : original, File.ReadAllText(path));
                    app.InjectKey(Key.Esc);
                    return true;
                }
                else return null;
                return false;
            });
            Assert.DoesNotContain(host.Requests, request => request.StartsWith("POST ", StringComparison.Ordinal));
        }
        finally { workspace.Delete(recursive: true); }
    }
}
