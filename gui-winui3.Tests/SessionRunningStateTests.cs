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
    public void A_local_draft_adopts_live_status_before_durable_history_arrives()
    {
        var drafts = new SessionDraftPresentation();
        var row = new SessionInfo { Id = "draft" };
        drafts.Activate(row.Id);
        drafts.Register(row, null, null);
        Assert.True(Assert.Single(drafts.Project([new() { Id = row.Id, IsAgentRunning = true }])).IsAgentRunning);
        Assert.False(Assert.Single(drafts.Project([new() { Id = row.Id, IsAgentRunning = false }])).IsAgentRunning);
    }
}
