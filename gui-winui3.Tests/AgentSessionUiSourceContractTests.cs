using System.Reflection;
using System.Xml.Linq;
using RuAgent.Harness.Core;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Composer and transcript layout, plus the session-scoped agent state the composer
/// drives.
///
/// Layout is asserted on the element tree — which control sits in which column and
/// inside which container — so moving or renaming markup is caught by structure
/// rather than by the order substrings happen to appear in the file.
/// </summary>
public sealed class AgentSessionUiSourceContractTests
{
    /// <summary>Names of surfaces removed with the Agent-mode selector and the chat transport.</summary>
    private static readonly string[] RetiredXamlNames =
    [
        "AgentModeFlyout", "AgentModeChatOption", "AgentModeAgentOption",
        "AgentSettingsButton", "AgentSettingsFlyout", "AgentWorkspaceBox",
    ];

    [Fact]
    public void The_composer_shows_one_static_agent_indicator()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        var agentButton = GuiSource.Require(document, "AgentModeButton");
        Assert.Equal("Button", agentButton.Name.LocalName);
        Assert.Equal("Collapsed", GuiSource.AttributeValue(agentButton, "Visibility"));
        Assert.Equal("AgentModeButton", GuiSource.AttributeValue(agentButton, "AutomationId"));
        Assert.Equal("Coding Agent", GuiSource.AttributeValue(agentButton, "Name"));

        // The indicator is a label, not a selector: a mode flyout was retired.
        var named = GuiSource.NamedElements(document);
        Assert.True(named.ContainsKey("AgentModeIcon"), "AgentModeIcon must exist.");
        Assert.True(named.ContainsKey("AgentModeLabel"), "AgentModeLabel must exist.");
        Assert.Equal("Agent", GuiSource.AttributeValue(named["AgentModeLabel"], "Text"));

        // The preset selector is the only permission control on the composer.
        Assert.True(named.ContainsKey("AgentPresetBox"), "AgentPresetBox must exist.");

        foreach (var retired in RetiredXamlNames)
        {
            Assert.False(named.ContainsKey(retired), $"{retired} was retired and must not return.");
        }

        Assert.DoesNotContain(
            GuiSource.ClickHandlers(document),
            handler => handler.StartsWith("OnAgentMode", StringComparison.Ordinal));
    }

    [Fact]
    public void Attachment_and_agent_indicator_lead_the_composer_row()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        var composerRow = document.Descendants()
            .FirstOrDefault(element => GuiSource.AttributeValue(element, "ColumnDefinitions") == "Auto,Auto,Auto,*,Auto");
        Assert.True(composerRow is not null, "The composer row layout was not found.");

        var attach = GuiSource.Require(document, "AttachImageButton");
        var indicator = GuiSource.Require(document, "AgentModeButton");

        // Both are direct children of the composer row, on the leading edge.
        Assert.Same(composerRow, attach.Parent);
        Assert.Same(composerRow, indicator.Parent);
        Assert.Equal(0, GuiSource.IntAttribute(attach, "Column", -1));
        Assert.Equal(1, GuiSource.IntAttribute(indicator, "Column", -1));
        Assert.True(
            GuiSource.DocumentOrder(document, attach) < GuiSource.DocumentOrder(document, indicator),
            "The attachment button must precede the agent indicator.");

        // The preset selector follows them, then the send control.
        var preset = GuiSource.Require(document, "AgentPresetBox");
        var send = GuiSource.Require(document, "SendButton");
        Assert.True(
            GuiSource.DocumentOrder(document, indicator) < GuiSource.DocumentOrder(document, preset),
            "The agent indicator must precede the preset selector.");
        Assert.True(
            GuiSource.DocumentOrder(document, preset) < GuiSource.DocumentOrder(document, send),
            "The preset selector must precede send.");

        // The three interactive composer controls stay announceable. The send face
        // resolves through AgentComposerPresentation so the markup and the runtime
        // assignment cannot hold two copies of the same word.
        Assert.Equal("SendButton", GuiSource.AttributeValue(send, "AutomationId"));
        Assert.Equal("{x:Bind SendFaceName}", GuiSource.AttributeValue(send, "Name"));
        Assert.Equal("{x:Bind SendFaceGlyph}", GuiSource.AttributeValue(GuiSource.Require(document, "SendButtonIcon"), "Glyph"));

        Assert.Equal("AttachImageButton", GuiSource.AttributeValue(attach, "AutomationId"));
        Assert.Equal("AgentPresetBox", GuiSource.AttributeValue(preset, "AutomationId"));
        Assert.Equal("Agent permission preset", GuiSource.AttributeValue(preset, "Name"));

        // The two view properties the markup binds to, and the runtime assignment in
        // UpdateSendButtonMode, all resolve from the one vocabulary. ChatView itself
        // cannot be constructed here — it needs the XAML runtime — so the link is
        // checked by reading the source that makes it.
        var code = GuiSource.Text("Views", "ChatView.xaml.cs");
        Assert.Contains("SendFaceName => AgentComposerPresentation.SendName", code, StringComparison.Ordinal);
        Assert.Contains("SendFaceGlyph => AgentComposerPresentation.SendGlyph", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(SendButton, AgentComposerPresentation.StopName)", code, StringComparison.Ordinal);
        Assert.Contains("SendButtonIcon.Glyph = AgentComposerPresentation.StopGlyph", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_transcript_keeps_a_full_width_scroll_host_and_a_centered_reading_column()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        var readingColumn = GuiSource.Require(document, "ReadingColumn");
        var inputContainer = GuiSource.Require(document, "InputContainer");
        var contentGrid = GuiSource.Require(document, "ChatContentGrid");
        var list = GuiSource.Require(document, "ChatListView");

        // The composer sits outside the reading column so it is never scrolled away.
        Assert.False(GuiSource.IsDescendantOf(inputContainer, readingColumn));

        // The scrolling host lives inside the reading column, with the list inside it.
        Assert.True(GuiSource.IsDescendantOf(contentGrid, readingColumn));
        Assert.True(GuiSource.IsDescendantOf(list, contentGrid));

        // The column spans the window; the scroll host inside it is centred and
        // carries the scroll behaviour, which is what keeps the transcript centred
        // at wide sizes without pinning it at narrow ones.
        Assert.Equal("Stretch", GuiSource.AttributeValue(readingColumn, "HorizontalAlignment"));
        Assert.Equal("Center", GuiSource.AttributeValue(contentGrid, "HorizontalAlignment"));
        Assert.Equal("Auto", GuiSource.AttributeValue(list, "VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", GuiSource.AttributeValue(list, "HorizontalScrollBarVisibility"));

        // The scrollbar is themed through resources declared in the list's scope.
        // Keys live in the x: namespace, which AttributeValue deliberately skips, so
        // they are read directly here.
        var keys = list.Descendants()
            .Select(element => element.Attribute(GuiSource.Xaml + "Key")?.Value)
            .Where(key => key is not null)
            .ToList();
        Assert.Contains("ScrollBarThumbFill", keys);
        Assert.Contains("ScrollBarSize", keys);
    }

    [Fact]
    public void Only_the_content_surface_is_width_managed_at_runtime()
    {
        // The reading column is a layout constant; the runtime resize path owns the
        // content grid and the list, which is what makes a narrow window usable.
        var view = typeof(RuApp.Views.ChatView);
        var applyInputWidth = view.GetMethod(
            "ApplyInputWidth",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(applyInputWidth);

        // No retired mode or transport members survive on the type.
        var members = view
            .GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(member => member.Name)
            .ToList();

        foreach (var retired in new[]
                 {
                     "SetAgentMode", "OnAgentModeButtonClick", "OnAgentModeOptionClick",
                     "ChatWebSocketClient", "ChatTurnPresenter", "OnWsFrameReceived",
                     "OnAgentTrustApproveClick", "OnAgentTrustDenyClick",
                 })
        {
            Assert.DoesNotContain(members, member => member.Contains(retired, StringComparison.Ordinal));
        }

        Assert.Contains("UpdateAgentComposerVisuals", members);
        Assert.Contains("SendAgentTurnAsync", members);
        Assert.Contains("ResetAgentState", members);
        Assert.Contains("AbandonActiveAgentReply", members);
    }

    [Fact]
    public void The_composer_offers_standard_and_trusted_but_never_the_legacy_value()
    {
        // FullAccess still exists on the enum as legacy wire compatibility, but the
        // composer must not offer it as a choice.
        Assert.Contains(AgentPermissionPreset.Standard, Enum.GetValues<AgentPermissionPreset>());
        Assert.Contains(AgentPermissionPreset.Trusted, Enum.GetValues<AgentPermissionPreset>());

        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var presetBox = GuiSource.Require(document, "AgentPresetBox");

        var tags = presetBox.Descendants()
            .Select(element => GuiSource.AttributeValue(element, "Tag"))
            .Where(tag => tag is not null)
            .ToList();

        Assert.Contains("standard", tags);
        Assert.Contains("trusted", tags);
        Assert.DoesNotContain("full_access", tags);
    }

    [Fact]
    public void Selecting_a_session_preset_is_version_fenced()
    {
        // A preset write carries the version it was read at, so a concurrent change
        // surfaces as a conflict the caller resolves instead of silently overwriting.
        var method = typeof(AgentRunController).GetMethod(nameof(AgentRunController.SetSessionPresetAsync));

        Assert.NotNull(method);
        var parameters = method!.GetParameters().Select(parameter => parameter.Name).ToList();

        Assert.Contains("expectedVersion", parameters);
        Assert.Contains("interactive", parameters);
    }
}
