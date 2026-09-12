using System.Linq;
using RuAgent.Client;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunSummaryUiContractTests
{
    [Fact]
    public void Failure_surface_is_an_inflow_error_message_with_continue_guidance()
    {
        var xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        var code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        var model = File.ReadAllText(FindSource("Models", "AgentRunSummaryItem.cs"));

        Assert.DoesNotContain("AgentRunSummaryPanel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentContinueButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentRetryButton", xaml, StringComparison.Ordinal);
        Assert.Contains("SummaryReceived", code, StringComparison.Ordinal);
        Assert.Contains("ApplyAgentRunSummary", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearAgentRunSummary", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAgentRetryClick", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAgentContinueClick", code, StringComparison.Ordinal);
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
    public void Summary_message_only_renders_for_failures()
    {
        var code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        var applyStart = code.IndexOf("private void ApplyAgentRunSummary", StringComparison.Ordinal);
        var applyEnd = code.IndexOf("private void OnAgentAssistantDeltaReceived", StringComparison.Ordinal);
        Assert.True(applyStart >= 0, "ApplyAgentRunSummary was not found.");
        Assert.True(applyEnd > applyStart, "ApplyAgentRunSummary body boundary was not found.");

        var applyBody = code[applyStart..applyEnd];
        Assert.Contains("IsFailure", applyBody, StringComparison.Ordinal);
        Assert.Contains("HasRenderedAgentReply", applyBody, StringComparison.Ordinal);
        Assert.Contains("IsError = true", applyBody, StringComparison.Ordinal);
        Assert.Contains("Continue", applyBody, StringComparison.Ordinal);
        Assert.Contains("return;", applyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_failure_does_not_duplicate_above_the_composer()
    {
        var code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        var stateStart = code.IndexOf("private void UpdateAgentInlineState", StringComparison.Ordinal);
        Assert.True(stateStart >= 0, "UpdateAgentInlineState was not found.");
        var stateEnd = code.IndexOf("private static string ResolveFailureReason", stateStart, StringComparison.Ordinal);
        Assert.True(stateEnd > stateStart, "UpdateAgentInlineState body boundary was not found.");

        var stateBody = code[stateStart..stateEnd];
        Assert.Contains("keepTerminalFailureVisible", stateBody, StringComparison.Ordinal);
        Assert.Contains("safeStatus = string.Empty", stateBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Continue", true)]
    [InlineData("continue", true)]
    [InlineData("  CONTINUE  ", true)]
    [InlineData("Continue please", false)]
    [InlineData("", false)]
    public void Typed_continue_resumes_only_as_a_bare_command(string text, bool expected)
    {
        Assert.Equal(expected, RuApp.Views.ChatView.IsContinueResumeCommand(
            text,
            new List<ImageAttachmentItem>()));
    }

    [Fact]
    public void Typed_continue_with_attachments_is_a_normal_prompt()
    {
        var attachments = new List<ImageAttachmentItem>
        {
            new() { FileName = "shot.png" },
        };

        Assert.False(RuApp.Views.ChatView.IsContinueResumeCommand("Continue", attachments));
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
