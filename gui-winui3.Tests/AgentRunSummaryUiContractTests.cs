using System.Linq;
using RuAgent.Client;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunSummaryUiContractTests
{
    [Fact]
    public void Summary_surface_is_separate_from_activity_and_uses_typed_fields_only()
    {
        var xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        var code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        var model = File.ReadAllText(FindSource("Models", "AgentRunSummaryItem.cs"));

        Assert.Contains("x:Name=\"AgentRunSummaryPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AgentRunSummaryText", xaml, StringComparison.Ordinal);
        Assert.Contains("AgentRunSummaryMetadata", xaml, StringComparison.Ordinal);
        Assert.Contains("SummaryReceived", code, StringComparison.Ordinal);
        Assert.Contains("ApplyAgentRunSummary", code, StringComparison.Ordinal);
        Assert.Contains("ClearAgentRunSummary", code, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "Command", "Arguments", "Diff", "Stdout", "Stderr", "TransientData" })
        {
            Assert.DoesNotContain(forbidden, model, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Only_verified_success_uses_completed_wording()
    {
        var verified = new AgentRunSummaryItem(Model("succeeded", "verified"));
        var unverified = new AgentRunSummaryItem(Model("succeeded", "unverified"));
        var cancelled = new AgentRunSummaryItem(Model("cancelled", "not_applicable"));

        Assert.True(verified.IsVerifiedSuccess);
        Assert.Contains("completed", verified.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completed", unverified.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unverified", unverified.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stopped", cancelled.DisplayText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_card_only_renders_for_failures()
    {
        var code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        var applyStart = code.IndexOf("private void ApplyAgentRunSummary", StringComparison.Ordinal);
        var applyEnd = code.IndexOf("private void OnAgentAssistantDeltaReceived", StringComparison.Ordinal);
        Assert.True(applyStart >= 0, "ApplyAgentRunSummary was not found.");
        Assert.True(applyEnd > applyStart, "ApplyAgentRunSummary body boundary was not found.");

        var applyBody = code[applyStart..applyEnd];
        Assert.Contains("IsFailure", applyBody, StringComparison.Ordinal);
        Assert.Contains("HasRenderedAgentReply", applyBody, StringComparison.Ordinal);
        Assert.Contains("return;", applyBody, StringComparison.Ordinal);
    }

    private static AgentRunSummaryModel Model(string status, string verification) =>
        new(status, verification, "Workspace", 5, 1, 0, 0, null, 1);

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
