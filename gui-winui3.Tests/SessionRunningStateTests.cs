using System.Text.Json;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionRunningStateTests
{
    [Fact]
    public void Running_state_is_live_accessible_and_does_not_rebuild_sidebar_rows()
    {
        var row = JsonSerializer.Deserialize<SessionInfo>("""{"id":"one","title":"Build","is_agent_running":false}""")!;
        var fingerprint = SidebarRowFingerprint.ForSessions([row]);
        var notifications = new List<string?>();
        row.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        row.IsAgentRunning = true;
        Assert.Contains(nameof(SessionInfo.IsAgentRunning), notifications);
        Assert.Contains(nameof(SessionInfo.AgentStatusText), notifications);
        Assert.Contains(nameof(SessionInfo.TooltipText), notifications);
        Assert.Equal("Running", row.AgentStatusText);
        Assert.Contains("Running", row.TooltipText);
        Assert.Equal(fingerprint, SidebarRowFingerprint.ForSessions([row]));
        notifications.Clear();
        row.IsAgentRunning = true;
        Assert.Empty(notifications);
        row.IsAgentRunning = false;
        Assert.Equal(string.Empty, row.AgentStatusText);
        Assert.DoesNotContain("Running", row.TooltipText);
    }

    [Fact]
    public void A_wait_on_the_user_is_named_and_shown_without_rebuilding_sidebar_rows()
    {
        var row = JsonSerializer.Deserialize<SessionInfo>("""{"id":"one","title":"Build","is_agent_running":false,"agent_wait":"approval"}""")!;
        Assert.True(row.NeedsUserAction);
        Assert.Equal("Needs approval", row.AgentStatusText);
        Assert.Contains("Needs approval", row.TooltipText);
        var fingerprint = SidebarRowFingerprint.ForSessions([row]);

        row.SetAgentState(false, "question");
        Assert.Equal("Has a question", row.AgentStatusText);
        row.SetAgentState(false, "workspace_trust");
        Assert.Equal("Needs folder trust", row.AgentStatusText);
        row.SetAgentState(false, "user");
        Assert.Equal("Waiting for you", row.AgentStatusText);
        Assert.Equal(fingerprint, SidebarRowFingerprint.ForSessions([row]));

        row.SetAgentState(true, null);
        Assert.False(row.NeedsUserAction);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, row.NeedsUserActionVisibility);
        Assert.Equal("Running", row.AgentStatusText);
    }

    [Fact]
    public void Agent_state_changes_notify_only_after_both_fields_are_stored()
    {
        var row = new SessionInfo { Id = "one", IsAgentRunning = true };
        var seen = new List<(string? Property, bool Running, string? Wait)>();
        row.PropertyChanged += (_, args) => seen.Add((args.PropertyName, row.IsAgentRunning, row.AgentWait));

        row.SetAgentState(false, "approval");

        Assert.Contains(seen, entry => entry.Property == nameof(SessionInfo.IsAgentRunning));
        Assert.Contains(seen, entry => entry.Property == nameof(SessionInfo.AgentWait));
        Assert.All(seen, entry => Assert.Equal((false, "approval"), (entry.Running, entry.Wait)));
        seen.Clear();
        row.SetAgentState(false, "approval");
        Assert.Empty(seen);
    }

    [Fact]
    public void A_local_draft_adopts_live_status_before_durable_history_arrives()
    {
        var drafts = new SessionDraftPresentation();
        var row = new SessionInfo { Id = "draft" };
        drafts.Activate(row.Id);
        drafts.Register(row, null, null);
        Assert.True(Assert.Single(drafts.Project([new() { Id = row.Id, IsAgentRunning = true }])).IsAgentRunning);
        Assert.False(Assert.Single(drafts.Project([new() { Id = row.Id, IsAgentRunning = false }])).IsAgentRunning);
        Assert.Equal("question", Assert.Single(drafts.Project([new() { Id = row.Id, AgentWait = "question" }])).AgentWait);
    }
}
