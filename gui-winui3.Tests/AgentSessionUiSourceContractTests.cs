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

        // Model + reasoning share one toolbar style (the pill-well experiment
        // looked muddy on dark surfaces and was reverted).
        var model = GuiSource.Require(document, "ChatModelButton");
        var reasoning = GuiSource.Require(document, "ChatReasoningButton");
        Assert.Equal("{StaticResource RuToolbarButtonStyle}", GuiSource.AttributeValue(model, "Style"));
        Assert.Equal("{StaticResource RuToolbarButtonStyle}", GuiSource.AttributeValue(reasoning, "Style"));

        // The markup binds the send face to the shared vocabulary, and the runtime
        // assignment in UpdateSendButtonMode reads the same vocabulary: the two faces
        // carry no literal glyph or announced name of their own. ChatView cannot be
        // constructed here — it needs the XAML runtime — so the invariant is read off
        // the method that does the assigning.
        var code = GuiSource.ViewText("ChatView");
        var literals = GuiSource.StringLiterals(GuiSource.MethodBody(code, "UpdateSendButtonMode"));

        Assert.DoesNotContain(
            literals,
            literal => literal.Length == 1 && literal[0] >= '\uE000' && literal[0] <= '\uF8FF');
        Assert.DoesNotContain("Send message", literals);
        Assert.DoesNotContain("Stop Coding Agent", literals);
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

        // The column spans the window; the scroll host stretches full-width so its
        // scrollbar docks at the window edge, while each message row carries a
        // code-sized MessageColumn (min 820, viewport) that centers itself —
        // centred at wide sizes without pinning at narrow ones.
        Assert.Equal("Stretch", GuiSource.AttributeValue(readingColumn, "HorizontalAlignment"));
        Assert.Equal("Stretch", GuiSource.AttributeValue(contentGrid, "HorizontalAlignment"));

        foreach (var key in new[] { "UserMessageTemplate", "AssistantMessageTemplate" })
        {
            var template = document.Descendants()
                .First(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == key);
            var root = template.Elements().First();
            Assert.Equal("Stretch", GuiSource.AttributeValue(root, "HorizontalAlignment"));
            Assert.Null(GuiSource.AttributeValue(root, "MaxWidth"));

            var column = template.Descendants()
                .First(element => element.Attribute(GuiSource.Xaml + "Name")?.Value == "MessageColumn");
            Assert.Equal("Center", GuiSource.AttributeValue(column, "HorizontalAlignment"));
            Assert.Equal("OnMessageColumnLoaded", GuiSource.AttributeValue(column, "Loaded"));
        }

        // The runtime sizes realized columns on load and on every resize, so a
        // pooled container never keeps a stale width.
        var code = GuiSource.ViewText("ChatView");
        Assert.Contains(
            "LayoutRealizedMessageColumns",
            GuiSource.MethodBody(code, "ApplyInputWidth"),
            StringComparison.Ordinal);
        Assert.NotNull(typeof(RuApp.Views.ChatView).GetMethod(
            "OnMessageColumnLoaded",
            BindingFlags.Instance | BindingFlags.NonPublic));

        // A fresh row must already be at final width on its first frame: Loaded
        // fires after the row measured itself against its content, so sizing
        // only there flashes new rows narrow before they jump wide.
        Assert.Contains(
            "ChatListView.ContainerContentChanging += OnChatContainerContentChanging",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "LayoutMessageColumn",
            GuiSource.MethodBody(code, "OnChatContainerContentChanging"),
            StringComparison.Ordinal);
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
    public void Sidebar_groups_sessions_under_workspace_headers()
    {
        var document = GuiSource.Xml("MainWindow.xaml");

        var header = GuiSource.Require(document, "WorkspaceHeader");
        Assert.Equal("WORKSPACES", GuiSource.AttributeValue(header, "Text"));

        foreach (var name in new[] { "WorkspaceSearchButton", "WorkspaceFilterButton", "WorkspaceNewButton", "WorkspaceSearchBox" })
        {
            Assert.Contains(name, GuiSource.NamedElements(document).Keys);
        }

        var addButton = GuiSource.Require(document, "WorkspaceNewButton");
        Assert.Equal("OnWorkspaceAddClick", GuiSource.AttributeValue(addButton, "Click"));
        Assert.DoesNotContain(
            GuiSource.NamedElements(document).Keys,
            name => string.Equals(name, "WorkspaceAddButton", StringComparison.Ordinal));

        var groupTemplate = document.Descendants()
            .First(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "WorkspaceGroupTemplate");
        var toggle = groupTemplate.Descendants()
            .First(element => element.Attribute(GuiSource.Xaml + "Name")?.Value == "WorkspaceGroupToggle");
        Assert.Equal("Button", toggle.Name.LocalName);
        Assert.Equal("OnWorkspaceGroupClick", GuiSource.AttributeValue(toggle, "Click"));
        Assert.Contains("IsExpanded", GuiSource.AttributeValue(toggle, "CommandParameter"));
        Assert.Equal("WorkspaceGroupToggle_PointerEntered", GuiSource.AttributeValue(toggle, "PointerEntered"));
        Assert.Equal("WorkspaceGroupToggle_PointerExited", GuiSource.AttributeValue(toggle, "PointerExited"));
        Assert.Equal("{StaticResource WorkspaceRowButtonStyle}", GuiSource.AttributeValue(toggle, "Style"));
        Assert.Null(GuiSource.AttributeValue(toggle, "Margin"));

        // One hover frame only: the row button paints nothing itself, so the
        // ListViewItem container owns the single rounded hover fill. A default
        // Button template would add its own square PointerOver fill on top.
        var rowStyle = document.Descendants()
            .First(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "WorkspaceRowButtonStyle");
        var rowTemplate = rowStyle.Descendants()
            .First(element => element.Name.LocalName == "ControlTemplate");
        Assert.DoesNotContain(
            rowTemplate.Descendants().Where(element => element.Name.LocalName == "Setter"),
            setter => (GuiSource.AttributeValue(setter, "Target") ?? string.Empty)
                .Contains("Background", StringComparison.Ordinal));
        Assert.DoesNotContain(
            rowTemplate.Descendants().Where(element => element.Name.LocalName == "VisualState"),
            state => string.Equals(
                GuiSource.AttributeValue(state, "Name"),
                "PointerOver",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            GuiSource.NamedElements(document).Keys,
            name => string.Equals(name, "WorkspaceGroupCount", StringComparison.Ordinal));

        var groupAdd = groupTemplate.Descendants()
            .First(element => element.Attribute(GuiSource.Xaml + "Name")?.Value == "WorkspaceGroupAddButton");
        Assert.Equal("Button", groupAdd.Name.LocalName);
        Assert.Equal("OnWorkspaceGroupAddClick", GuiSource.AttributeValue(groupAdd, "Click"));
        Assert.Equal("{StaticResource WorkspaceAddButtonStyle}", GuiSource.AttributeValue(groupAdd, "Style"));

        // Pinned workspaces can be removed again; the folder and sessions stay.
        var removeItem = groupTemplate.Descendants()
            .First(element => element.Name.LocalName == "MenuFlyoutItem"
                && GuiSource.AttributeValue(element, "Click") == "OnWorkspaceRemoveClick");
        Assert.Equal("Remove workspace", GuiSource.AttributeValue(removeItem, "Text"));

        // The plus never paints a frame: every state keeps a transparent root
        // and hover only lights the glyph up.
        var addStyle = document.Descendants()
            .First(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "WorkspaceAddButtonStyle");
        var pointerOver = addStyle.Descendants()
            .First(element => element.Name.LocalName == "VisualState"
                && element.Attribute(GuiSource.Xaml + "Name")?.Value == "PointerOver");
        var setters = pointerOver.Descendants()
            .Where(element => element.Name.LocalName == "Setter")
            .ToList();
        Assert.Contains(setters, setter =>
            GuiSource.AttributeValue(setter, "Target") == "Root.Background"
            && GuiSource.AttributeValue(setter, "Value") == "Transparent");
        Assert.Contains(setters, setter =>
            GuiSource.AttributeValue(setter, "Target") == "Glyph.Foreground");

        // The inline plus and chevron only appear on row hover.
        Assert.Equal("0.55", GuiSource.AttributeValue(groupAdd, "Opacity"));
        var chevron = groupTemplate.Descendants()
            .First(element => element.Attribute(GuiSource.Xaml + "Name")?.Value == "WorkspaceGroupChevron");
        Assert.Equal("0.55", GuiSource.AttributeValue(chevron, "Opacity"));

        // No zoom on hover: the style carries no Scale setters at all.
        Assert.DoesNotContain(
            addStyle.Descendants().Where(element => element.Name.LocalName == "Setter"),
            setter => (GuiSource.AttributeValue(setter, "Target") ?? string.Empty)
                .StartsWith("Scale.", StringComparison.Ordinal));

        // The footer status notice is gone from the sidebar.
        Assert.Equal(
            "Collapsed",
            GuiSource.AttributeValue(GuiSource.Require(document, "ShellStatusRow"), "Visibility"));

        // One frame only: the container carries no padding so each row's own
        // control paints full-bleed; the session root keeps the old 10,4 inset.
        var sessionTemplate = document.Descendants()
            .First(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "WorkspaceSessionTemplate");
        Assert.Equal("10,4", GuiSource.AttributeValue(sessionTemplate.Elements().First(), "Padding"));
        var containerStyle = GuiSource.Require(document, "SidebarSessionList").Descendants()
            .First(element => element.Name.LocalName == "Style");
        var paddingSetter = containerStyle.Descendants()
            .First(element => element.Name.LocalName == "Setter"
                && GuiSource.AttributeValue(element, "Property") == "Padding");
        Assert.Equal("0", GuiSource.AttributeValue(paddingSetter, "Value"));
        Assert.True(GuiSource.NamedElements(document).ContainsKey("WorkspaceSearchCount"), "WorkspaceSearchCount must exist.");

        Assert.Contains("SessionTimeText", GuiSource.NamedElements(document).Keys);
        var timeText = GuiSource.NamedElements(document)["SessionTimeText"];
        Assert.Contains("DisplayTime", GuiSource.AttributeValue(timeText, "Text"));

        var app = GuiSource.Xml("App.xaml");
        Assert.True(app.Descendants().Any(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "SessionTimeConverter"));

    }

    [Fact]
    public void Sidebar_render_wires_projection_and_new_chat_cascade()
    {
        var code = GuiSource.ViewText("MainWindow");
        Assert.Contains(
            "WorkspaceSessionProjection.Project",
            GuiSource.MethodBody(code, "RenderWorkspaceSidebar"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveNewChatWorkspace",
            GuiSource.MethodBody(code, "OnNewChatClick"),
            StringComparison.Ordinal);
        Assert.Contains(
            "VirtualKey.Enter",
            GuiSource.MethodBody(code, "OnWorkspaceSearchKeyDown"),
            StringComparison.Ordinal);
        Assert.Contains(
            "FolderPicker",
            GuiSource.MethodBody(code, "OnWorkspaceAddClick"),
            StringComparison.Ordinal);
        Assert.Contains(
            "MergePinned",
            GuiSource.MethodBody(code, "RenderWorkspaceSidebar"),
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateSessionAsync",
            GuiSource.MethodBody(code, "OnWorkspaceGroupAddClick"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Unpin",
            GuiSource.MethodBody(code, "OnWorkspaceRemoveClick"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Opacity",
            GuiSource.MethodBody(code, "SetWorkspaceGroupChromeVisible"),
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter",
            GuiSource.MethodBody(code, "OnWorkspaceGroupClick"),
            StringComparison.Ordinal);
        Assert.Contains(
            "PathStateKey",
            GuiSource.MethodBody(code, "OnWorkspaceGroupClick"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChatView_runs_in_the_requested_workspace_with_app_root_default()
    {
        Assert.NotNull(typeof(RuApp.Views.ChatView).GetMethod(
            "SetRequestedWorkspace",
            BindingFlags.Instance | BindingFlags.Public));

        var code = GuiSource.ViewText("ChatView");
        var body = GuiSource.MethodBody(code, "SendAgentTurnAsync");
        Assert.Contains("_requestedWorkspace", body, StringComparison.Ordinal);
        Assert.Contains("App root workspace is not configured", body, StringComparison.Ordinal);

        // The send/stop glyph must stay legible on the solid accent fill in both
        // themes, so the face update paints OnAccent — never Ink or Danger.
        var sendFace = GuiSource.MethodBody(code, "UpdateSendButtonMode");
        Assert.Contains("RuOnAccentBrush", sendFace, StringComparison.Ordinal);
        Assert.DoesNotContain("RuInkBrush", sendFace, StringComparison.Ordinal);
        Assert.DoesNotContain("RuDangerBrush", sendFace, StringComparison.Ordinal);

        // The primary-button template owns Send hover via VSM; the legacy
        // code-behind hover must not fight it with local Background values.
        var hoverFx = GuiSource.MethodBody(code, "InitializeChatHoverEffects");
        Assert.DoesNotContain("SendButton", hoverFx, StringComparison.Ordinal);

        // Same deal for the secondary New Chat CTA: the template owns hover,
        // so the sidebar hover setup must not touch it either.
        var shellCode = GuiSource.ViewText("MainWindow");
        var sidebarHover = GuiSource.MethodBody(shellCode, "InitializeSidebarHoverEffects");
        Assert.DoesNotContain("NavNewChat", sidebarHover, StringComparison.Ordinal);
    }

    [Fact]
    public void The_composer_offers_the_full_ladder_and_never_the_retired_value()
    {
        // The composer offers the whole ladder: plan, standard, full_access.
        Assert.Contains(AgentPermissionPreset.Plan, Enum.GetValues<AgentPermissionPreset>());
        Assert.Contains(AgentPermissionPreset.Standard, Enum.GetValues<AgentPermissionPreset>());
        Assert.Contains(AgentPermissionPreset.FullAccess, Enum.GetValues<AgentPermissionPreset>());

        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var presetBox = GuiSource.Require(document, "AgentPresetBox");

        var tags = presetBox.Descendants()
            .Select(element => GuiSource.AttributeValue(element, "Tag"))
            .Where(tag => tag is not null)
            .ToList();

        Assert.Contains("plan", tags);
        Assert.Contains("standard", tags);
        Assert.Contains("full_access", tags);
        Assert.DoesNotContain("trusted", tags);
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
