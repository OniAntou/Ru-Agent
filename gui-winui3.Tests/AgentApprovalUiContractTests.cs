using System.Linq;
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
    [InlineData(AgentApprovalSummaryPresentation.TerminalExternalCwdRequiresApproval, "folder outside the workspace")]
    [InlineData(AgentApprovalSummaryPresentation.TerminalCwdUntrusted, "cannot verify")]
    [InlineData(AgentApprovalSummaryPresentation.TerminalNetworkRequiresApproval, "network access")]
    [InlineData(AgentApprovalSummaryPresentation.TerminalExternalArgvRequiresApproval, "points outside the workspace")]
    [InlineData(AgentApprovalSummaryPresentation.TerminalExternalExecutableRequiresApproval, "executable outside the workspace")]
    [InlineData(AgentApprovalSummaryPresentation.TerminalUnparsedShellSyntaxRequiresApproval, "cannot fully verify")]
    [InlineData(AgentApprovalSummaryPresentation.RecognizedExecutableUnclassifiedForm, "not the shape")]
    [InlineData(AgentApprovalSummaryPresentation.GitSubmoduleRequiresApproval, "submodules")]
    [InlineData(AgentApprovalSummaryPresentation.GitDirectoryOverrideRequiresApproval, "different directory")]
    [InlineData(AgentApprovalSummaryPresentation.GitConfigOverrideRequiresApproval, "for one run")]
    [InlineData(AgentApprovalSummaryPresentation.GitConfigMutationRequiresApproval, "changes git configuration")]
    [InlineData(AgentApprovalSummaryPresentation.RuleAsks, "project rules")]
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
            "terminal_protected_path_requires_approval",
            "terminal_external_cwd_requires_approval",
            "terminal_cwd_untrusted",
            "terminal_network_requires_approval",
            "terminal_external_argv_requires_approval",
            "terminal_external_executable_requires_approval",
            "terminal_unparsed_shell_syntax_requires_approval",
            "recognized_executable_unclassified_form",
            "git_submodule_requires_approval",
            "git_directory_override_requires_approval",
            "git_config_override_requires_approval",
            "git_config_mutation_requires_approval",
            "rule_asks",
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
        // the four decisions (Always only appears for external folders, Remember
        // for every known ask reason that may become a project rule — including
        // an external folder, which remembers the folder itself).
        foreach (var required in new[]
                 {
                     "Panel", "SummaryText", "CommandContainer", "CommandText",
                     "ApproveButton", "AlwaysButton", "RememberButton", "RememberButtonLabel",
                     "DenyButton", "SessionScopeHint",
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
    public void The_approval_decisions_name_their_exact_scope()
    {
        // Each decision button says what its command actually does: a session-remembered
        // approval, a one-time folder call, or a remembered project rule. The badge
        // wording flips on the external-directory case, which is the same predicate
        // that decides whether the session button is offered.
        var named = GuiSource.NamedElements(GuiSource.Xml("Controls", "AgentApprovalPanelControl.xaml"));

        Assert.Equal("Allow (remember for session)", GuiSource.AttributeValue(named["ApproveButtonLabel"], "Text"));
        Assert.Equal("Allow this folder for session", ButtonText(named["AlwaysButton"]));
        Assert.Equal("Always allow", ButtonText(named["RememberButton"]));
        Assert.Equal("Deny", ButtonText(named["DenyButton"]));

        string code = GuiSource.Text("Controls", "AgentApprovalPanelControl.xaml.cs");
        Assert.Contains("scope: \"once\"", GuiSource.MethodBody(code, "OnApproveClick"), StringComparison.Ordinal);
        Assert.Contains("scope: \"session\"", GuiSource.MethodBody(code, "OnAlwaysClick"), StringComparison.Ordinal);
        Assert.Contains("scope: \"always\"", GuiSource.MethodBody(code, "OnRememberClick"), StringComparison.Ordinal);

        // The external-directory case keeps the one-time label; every other ask
        // whose memory the evaluator honours is remembered by the session ledger.
        // The branch reads the same predicates the visibility rules use, which
        // the chat view fills from the pinned CanAllowSessionDirectory /
        // CanRememberForSession rules.
        string show = GuiSource.MethodBody(code, "Show");
        int predicate = show.IndexOf("allowSessionDirectory", StringComparison.Ordinal);
        int sessionMemory = show.IndexOf("allowSessionMemory", StringComparison.Ordinal);
        int folderOnce = show.IndexOf("\"Allow once\"", StringComparison.Ordinal);
        int sessionRemembered = show.IndexOf("\"Allow (remember for session)\"", StringComparison.Ordinal);
        Assert.True(predicate >= 0, "The label must key off the external-directory predicate.");
        Assert.True(sessionMemory >= 0, "The label must key off the session-memory predicate.");
        Assert.True(folderOnce > predicate, "The one-time label must follow the predicates.");
        Assert.True(sessionRemembered > folderOnce, "The session-remembered label must be the fallback.");

        // The remember button flips its label for an external folder: it then
        // remembers the folder for the project, not one action pattern.
        Assert.Contains("RememberButtonLabel.Text", show, StringComparison.Ordinal);
        Assert.Contains("\"Remember folder for project\"", show, StringComparison.Ordinal);

        string chat = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "UpdateAgentInlineState");
        Assert.Contains("ApprovalPanel.Show(", GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OnRunPanelPropertyChanged"), StringComparison.Ordinal);
        Assert.Contains("CanAllowSessionDirectory(", chat, StringComparison.Ordinal);
        Assert.Contains("CanRememberForSession(", chat, StringComparison.Ordinal);

        // The removed policy-management surface must not be referenced from the card.
        Assert.DoesNotContain("Policies", code, StringComparison.Ordinal);
    }

    private static string ButtonText(XElement button) =>
        button.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => GuiSource.AttributeValue(element, "Text"))
            .First(text => !string.IsNullOrWhiteSpace(text))!;

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
    public void Remember_scope_is_offered_for_known_reasons_including_external_folders()
    {
        Assert.True(AgentApprovalPresentation.CanRememberAllowAlways(
            AgentApprovalSummaryPresentation.TerminalDeleteRequiresApproval));
        Assert.True(AgentApprovalPresentation.CanRememberAllowAlways(
            AgentApprovalSummaryPresentation.TerminalUnknownExecutable));
        Assert.True(AgentApprovalPresentation.CanRememberAllowAlways(
            AgentApprovalSummaryPresentation.GitConfigMutationRequiresApproval));
        Assert.True(AgentApprovalPresentation.CanRememberAllowAlways(
            AgentApprovalSummaryPresentation.TerminalNetworkRequiresApproval));
        Assert.True(AgentApprovalPresentation.CanRememberAllowAlways(
            AgentApprovalSummaryPresentation.ExternalDirectoryRequired));
        Assert.False(AgentApprovalPresentation.CanRememberAllowAlways(null));
        Assert.False(AgentApprovalPresentation.CanRememberAllowAlways("future_reason_code"));
    }

    [Fact]
    public void A_user_rule_ask_is_never_remembered()
    {
        // Remembering a user rule's ask would silently override the rule.
        Assert.False(AgentApprovalPresentation.CanRememberAllowAlways(
            AgentApprovalSummaryPresentation.RuleAsks));
    }

    [Theory]
    [InlineData(AgentApprovalSummaryPresentation.TerminalDeleteRequiresApproval, true)]
    [InlineData(AgentApprovalSummaryPresentation.TerminalNetworkRequiresApproval, true)]
    [InlineData(AgentApprovalSummaryPresentation.GitConfigMutationRequiresApproval, true)]
    [InlineData(AgentApprovalSummaryPresentation.TerminalProtectedPathRequiresApproval, false)]
    [InlineData(AgentApprovalSummaryPresentation.RuleAsks, false)]
    [InlineData(AgentApprovalSummaryPresentation.ExternalDirectoryRequired, true)]
    [InlineData("future_reason_code", false)]
    public void Tui_and_gui_offer_the_same_remember_choice(string reason, bool expected)
    {
        // The remember rule exists twice: the TUI prompt's reason set and the GUI
        // presentation predicate. Nothing but this test stops one from drifting,
        // and a drift would offer "always" on one surface only. The interactive
        // surfaces offer the directory decisions, mirroring offerDirectory: true.
        Assert.Equal(expected, AgentApprovalPresentation.CanRememberAllowAlways(reason));
        Assert.Equal(
            expected,
            RuAgent.Cli.Tui.ApprovalPrompt.For(reason, offerDirectory: true).Choices
                .Any(choice => choice.Decision == RuAgent.Cli.Tui.ApprovalPrompt.AllowAlways));
    }

    [Fact]
    public void A_protected_path_ask_is_never_remembered()
    {
        // The reason has dedicated copy so the user sees why, but "always" would
        // turn one approval into standing access to a credential directory.
        Assert.False(AgentApprovalPresentation.CanRememberAllowAlways(
            AgentApprovalSummaryPresentation.TerminalProtectedPathRequiresApproval));
        Assert.Contains(
            "protected",
            AgentApprovalSummaryPresentation.Render(
                AgentApprovalSummaryPresentation.TerminalProtectedPathRequiresApproval),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_explicit_rule_ask_never_promises_session_memory()
    {
        // The evaluator matches an explicit ask rule ahead of the session-memory
        // allow, so remembering the intent cannot suppress the next ask; the
        // label must stay single-use instead of promising memory the server
        // ignores.
        Assert.False(AgentApprovalPresentation.CanRememberForSession(
            AgentApprovalSummaryPresentation.RuleAsks));
    }

    [Fact]
    public void The_single_preset_control_offers_the_ladder_only()
    {
        // The permission preset lives in exactly one control: the Settings dropdown.
        // The composer's duplicate ComboBox was retired, so the session preset is
        // chosen where it is persisted, not on the message input row.
        var chatView = GuiSource.NamedElements(GuiSource.Xml("Views", "ChatView.xaml"));
        Assert.False(chatView.ContainsKey("AgentPresetBox"), "AgentPresetBox was retired from the composer.");

        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var presetButton = GuiSource.Require(document, "PresetButton");

        var tags = presetButton.Descendants()
            .Select(element => GuiSource.AttributeValue(element, "Tag"))
            .Where(tag => tag is not null)
            .ToList();

        Assert.Contains("plan", tags);
        Assert.Contains("standard", tags);
        Assert.Contains("full_access", tags);
        Assert.DoesNotContain("trusted", tags);
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
                     nameof(AgentRunController.ApproveAlwaysAsync),
                     nameof(AgentRunController.DenyAsync),
                     nameof(AgentRunController.CancelAsync),
                 })
        {
            Assert.NotNull(typeof(AgentRunController).GetMethod(method));
        }

        var assembly = typeof(AgentRunController).Assembly;
        Assert.Null(assembly.GetType("RuApp.Services.ChatWebSocketClient"));
    }

    [Fact]
    public void Full_access_confirmation_names_the_windows_authority_and_remaining_controls()
    {
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "ConfirmFullAccessAsync");

        Assert.Contains("current Windows user", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside the workspace", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timeout", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancellation", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_approval_panel_takes_the_keyboard_once_per_new_approval()
    {
        // The once-per-approval rule is AgentRunPanelViewModel.ApprovalArrived
        // (AgentRunPanelViewModelTests); the view focuses only from that event.
        string handler = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OnRunPanelApprovalArrived");
        Assert.Contains("FocusDecision", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("FocusDecision", GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "UpdateAgentInlineState"), StringComparison.Ordinal);
    }

    [Fact]
    public void Full_access_confirmation_precedes_the_preset_commit()
    {
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "SendAgentTurnAsync");
        int confirmation = body.IndexOf("ConfirmFullAccessAsync", StringComparison.Ordinal);
        int preset = body.IndexOf("EnsureAgentPresetAsync", StringComparison.Ordinal);

        Assert.True(confirmation >= 0, "The Full Access gate must remain present.");
        Assert.True(preset > confirmation, "The Full Access gate must run before EnsureAgentPresetAsync.");
    }

    [Fact]
    public void Settings_permission_copy_matches_the_canonical_matrix()
    {
        // The single preset control keeps the three canonical names and adds one
        // truthful subtitle per option describing what that preset actually does.
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var named = GuiSource.NamedElements(document);

        Assert.Contains(
            document.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    GuiSource.AttributeValue(element, "Text"),
                    "Permission Preset",
                    StringComparison.Ordinal));

        Assert.Equal("Plan", GuiSource.AttributeValue(named["PresetPlanText"], "Text"));
        Assert.Equal("Standard", GuiSource.AttributeValue(named["PresetStandardText"], "Text"));
        Assert.Equal("Full Access", GuiSource.AttributeValue(named["PresetFullAccessText"], "Text"));

        Assert.Equal(
            "Reads and web run automatically; edits and terminal are refused.",
            OptionSubtitle(named["PresetPlanText"]));
        Assert.Equal(
            "Workspace edits and terminal commands run automatically. External working directories and explicit rules ask.",
            OptionSubtitle(named["PresetStandardText"]));
        Assert.Equal(
            "Skips approval prompts; terminal runs as your Windows user.",
            OptionSubtitle(named["PresetFullAccessText"]));
    }

    private static string OptionSubtitle(XElement nameText) =>
        nameText.Parent!.Elements()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Skip(1)
            .Select(element => GuiSource.AttributeValue(element, "Text"))
            .First(text => !string.IsNullOrWhiteSpace(text))!;
}
