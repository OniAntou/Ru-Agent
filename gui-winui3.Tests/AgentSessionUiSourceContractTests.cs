using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionUiSourceContractTests
{
    [Fact]
    public void Chat_displays_only_the_actual_Host_grant_on_the_existing_activity_surface()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        string serviceSource = File.ReadAllText(FindSource("Services", "AgentSessionService.cs"));

        Assert.Contains("OnAgentSessionReceived", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Quyền đã cấp: {string.Join(\", \", grant.Scopes)}", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace and terminal tools", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_scopes.Add(\"workspace.read\")", serviceSource, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentActivity\"", xaml, StringComparison.Ordinal);
    }

    private static string FindSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string sourceRoot = Path.Combine(directory.FullName, "gui-winui3");
            if (Directory.Exists(sourceRoot))
            {
                return Path.Combine(new[] { sourceRoot }.Concat(parts).ToArray());
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }
}
