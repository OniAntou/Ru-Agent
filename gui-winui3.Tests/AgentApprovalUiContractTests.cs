using System.Xml.Linq;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Inline approval panel: what the user is told, and the bounded surface that tells
/// it. The copy is exercised through the presentation the view calls, so a reason
/// code that loses its sentence — or a panel that grows a control it should not have
/// — fails here rather than in production.
/// </summary>
public sealed class AgentApprovalUiContractTests
{
    [Theory]
    [InlineData(AgentApprovalSummaryPresentation.ExternalDirectoryRequired, "outside the project")]
    [InlineData(AgentApprovalSummaryPresentation.TerminalDeleteRequiresApproval, "delete files")]
    [InlineData(AgentApprovalSummaryPresentation.TerminalExternalMutationRequiresApproval, "outside the workspace")]
    [InlineData(AgentApprovalSummaryPresentation.TerminalTimeoutRequiresApproval, "longer than the standard window")]
    [InlineData(AgentApprovalSummaryPresentation.WebApprovalRequired, "fetch this web content")]
    [InlineData(AgentApprovalSummaryPresentation.TerminalUnknownExecutable, "has not recognized the executable")]
    public void Every_broker_ask_reason_has_its_own_sentence(string reasonCode, string expectedFragment)
    {
        string rendered = AgentApprovalSummaryPresentation.Render(reasonCode);

        Assert.NotEqual(reasonCode, rendered);
        Assert.Contains(expectedFragment, rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_mapped_reason_set_matches_the_brokers_ask_reasons()
    {
        // RuAgentBroker produces exactly these ask reasons; a new one has to be
        // given copy, otherwise the panel shows the user a bare token.
        var expected = new[]
        {
            "external_directory_required",
            "terminal_delete_requires_approval",
            "terminal_external_mutation_requires_approval",
            "terminal_timeout_requires_approval",
            "web_approval_required",
            "terminal_unknown_executable",
        };

        Assert.Equal(
            expected.OrderBy(code => code, StringComparer.Ordinal),
            AgentApprovalSummaryPresentation.MappedReasonCodes.OrderBy(code => code, StringComparer.Ordinal));
    }

    [Fact]
    public void An_unknown_reason_stays_visible_instead_of_becoming_a_generic_line()
    {
        // A reason this build does not know is still the only explanation the user
        // has; replacing it with boilerplate would hide why the action is held.
        string rendered = AgentApprovalSummaryPresentation.Render("future_reason_code");

        Assert.Contains("future_reason_code", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_summary_falls_back_to_a_generic_request_line()
    {
        Assert.Equal(
            "Agent is requesting an action that requires approval.",
            AgentApprovalSummaryPresentation.Render(null));
        Assert.Equal(
            "Agent is requesting an action that requires approval.",
            AgentApprovalSummaryPresentation.Render("   "));
    }

    [Fact]
    public void A_hostile_summary_cannot_grow_the_panel()
    {
        // The summary comes off the wire, so it is collapsed and truncated before
        // it is ever set as text.
        string rendered = AgentApprovalSummaryPresentation.Render(new string('x', 500));

        Assert.True(rendered.Length <= AgentApprovalSummaryPresentation.MaximumSummaryLength + 1);
        Assert.EndsWith("…", rendered, StringComparison.Ordinal);

        string multiline = AgentApprovalSummaryPresentation.Render("line one\r\nline two");
        Assert.DoesNotContain('\n', multiline);
        Assert.DoesNotContain('\r', multiline);
    }

    [Fact]
    public void The_panel_carries_exactly_the_approval_surface()
    {
        // The prompt lives in its own control now; ChatView only hosts it.
        Assert.Contains(
            GuiSource.Xml("Views", "ChatView.xaml").Descendants(),
            element => element.Name.LocalName == "AgentApprovalPanelControl");

        var named = GuiSource.NamedElements(GuiSource.Xml("Controls", "AgentApprovalPanelControl.xaml"));

        // One panel, its summary, the command block, the session-scope hint, and
        // the three decisions (Always only appears for external folders).
        foreach (var required in new[]
                 {
                     "Panel", "SummaryText", "CommandContainer", "CommandText",
                     "ApproveButton", "AlwaysButton", "DenyButton", "SessionScopeHint",
                 })
        {
            Assert.True(named.ContainsKey(required), $"{required} must exist.");
        }

        // The retired trust panel and scope pickers are gone.
        foreach (var retired in new[] { "AgentTrustPanel", "AgentApprovalScopeBox", "AgentApprovalScopePanel" })
        {
            Assert.DoesNotContain(named.Keys, name => name.Contains(retired, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_approval_summary_announces_updates_politely()
    {
        // The summary is what changes when a decision is requested; a polite live
        // region is how a screen reader learns a decision is waiting.
        var summary = GuiSource.NamedElements(GuiSource.Xml("Controls", "AgentApprovalPanelControl.xaml"))["SummaryText"];
        Assert.Equal("Polite", GuiSource.AttributeValue(summary, "LiveSetting"));
    }

    [Fact]
    public void The_approval_prompt_reports_the_decision_instead_of_deciding()
    {
        // The control raises which decision was made; the view is what talks to the
        // run controller, so the prompt cannot grow its own transport.
        var decision = typeof(RuApp.Controls.AgentApprovalPanelControl).GetEvent("DecisionRequested");

        Assert.NotNull(decision);
        var args = decision!.EventHandlerType!.GetMethod("Invoke")!.GetParameters()[1].ParameterType;
        Assert.Equal(typeof(RuApp.Controls.AgentApprovalDecidedEventArgs), args);
        Assert.NotNull(args.GetProperty("Approved"));
        Assert.NotNull(args.GetProperty("Scope"));
    }

    [Fact]
    public void Session_scope_is_offered_for_external_folders_only()
    {
        Assert.True(AgentApprovalPresentation.CanAllowSessionDirectory(
            AgentApprovalSummaryPresentation.ExternalDirectoryRequired));
        Assert.False(AgentApprovalPresentation.CanAllowSessionDirectory(
            AgentApprovalSummaryPresentation.TerminalDeleteRequiresApproval));
        Assert.False(AgentApprovalPresentation.CanAllowSessionDirectory(
            AgentApprovalSummaryPresentation.WebApprovalRequired));
        Assert.False(AgentApprovalPresentation.CanAllowSessionDirectory(null));
        Assert.False(AgentApprovalPresentation.CanAllowSessionDirectory("future_reason_code"));
    }

    [Fact]
    public void The_composer_preset_offers_standard_and_trusted_only()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var named = GuiSource.NamedElements(document);

        Assert.True(named.ContainsKey("AgentPresetBox"), "AgentPresetBox must exist.");

        var tags = document.Descendants()
            .Select(element => GuiSource.AttributeValue(element, "Tag"))
            .Where(tag => tag is not null)
            .ToList();

        Assert.Contains("standard", tags);
        Assert.Contains("trusted", tags);
        Assert.DoesNotContain("full_access", tags);
    }

    [Fact]
    public void Agent_decisions_go_through_the_shared_controller()
    {
        // Approve/deny/cancel are controller methods; there is no second approval
        // transport and no WebSocket path.
        foreach (var method in new[]
                 {
                     nameof(AgentRunController.ApproveAsync),
                     nameof(AgentRunController.ApproveSessionDirectoryAsync),
                     nameof(AgentRunController.DenyAsync),
                     nameof(AgentRunController.CancelAsync),
                 })
        {
            Assert.NotNull(typeof(AgentRunController).GetMethod(method));
        }

        var assembly = typeof(AgentRunController).Assembly;
        Assert.Null(assembly.GetType("RuApp.Services.ChatWebSocketClient"));
    }
}
