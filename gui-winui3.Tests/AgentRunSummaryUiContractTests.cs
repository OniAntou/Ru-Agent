using System.Reflection;
using RuAgent.Client;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Terminal run summary presentation. The summary model is asserted through its
/// public shape — a failure surface must not be able to carry the private payload
/// fields, and the wording must not claim success the host did not verify.
/// </summary>
public sealed class AgentRunSummaryUiContractTests
{
    [Fact]
    public void The_summary_model_exposes_no_private_payload_fields()
    {
        // The summary is UI copy. Anything the host keeps private (command text,
        // arguments, diffs, raw output) must have no property to travel in.
        var forbidden = new[] { "Command", "Arguments", "Diff", "Stdout", "Stderr", "TransientData", "Payload" };
        var properties = typeof(AgentRunSummaryItem)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        Assert.NotEmpty(properties);
        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(
                properties,
                property => property.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_shell_no_longer_renders_a_separate_summary_panel_or_retry_buttons()
    {
        // The failure surface is an inline message plus the transcript card; a
        // separate panel and its buttons were removed and must not come back.
        var named = GuiSource.NamedElements(GuiSource.Xml("Views", "ChatView.xaml")).Keys.ToList();

        Assert.DoesNotContain(named, name => name.Contains("AgentRunSummaryPanel", StringComparison.Ordinal));
        Assert.DoesNotContain(named, name => name.Contains("AgentContinueButton", StringComparison.Ordinal));
        Assert.DoesNotContain(named, name => name.Contains("AgentRetryButton", StringComparison.Ordinal));

        // The summary does still reach the view through a typed event.
        Assert.NotNull(typeof(AgentRunController).GetEvent(nameof(AgentRunController.SummaryReceived)));
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

    [Theory]
    [InlineData("failed", true)]
    [InlineData("interrupted", true)]
    [InlineData("succeeded", false)]
    [InlineData("cancelled", false)]
    [InlineData("running", false)]
    public void Only_failed_or_interrupted_runs_read_as_failures(string status, bool expected)
    {
        var item = new AgentRunSummaryItem(Model(status, "not_applicable"));

        Assert.Equal(expected, item.IsFailure);
        Assert.Equal(status == "cancelled", item.IsCancelled);
    }

    [Fact]
    public void A_success_that_was_not_verified_never_renders_the_success_glyph_colour_pair()
    {
        // Verified and unverified successes must be visually distinct; otherwise the
        // user cannot tell "done" from "done, unchecked".
        var verified = new AgentRunSummaryItem(Model("succeeded", "verified"));
        var unverified = new AgentRunSummaryItem(Model("succeeded", "unverified"));

        Assert.NotEqual(verified.DisplayText, unverified.DisplayText);
        Assert.NotEqual(verified.StatusGlyph, unverified.StatusGlyph);
    }

    [Fact]
    public void Metadata_text_stays_honest_when_the_host_reported_nothing()
    {
        // A summary with no file counters must not invent "0 files changed".
        var empty = new AgentRunSummaryItem(Model("succeeded", "verified"));

        Assert.True(string.IsNullOrWhiteSpace(empty.MetadataText) || !empty.MetadataText.Contains("0 ", StringComparison.Ordinal));
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
}
