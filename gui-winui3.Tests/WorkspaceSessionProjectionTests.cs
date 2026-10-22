using System.Collections.Generic;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class WorkspaceSessionProjectionTests
{
    [Fact]
    public void Groups_by_workspace_and_sorts_groups_by_newest_session()
    {
        var sessions = new List<SessionInfo>
        {
            S("b1", "B one", "ws_b", "Beta", null),
            S("a1", "A one", "ws_a", "Alpha", null),
            S("a2", "A two", "ws_a", "Alpha", null),
            S("u1", "Lost", null, null, null),
        };

        var projection = WorkspaceSessionProjection.Project(sessions);

        Assert.Equal(3, projection.Groups.Count);
        Assert.Equal("ws_b", projection.Groups[0].Key);
        Assert.Equal("Beta", projection.Groups[0].Label);
        Assert.Equal(["b1"], Ids(projection.Groups[0]));
        Assert.Equal(["a1", "a2"], Ids(projection.Groups[1]));
        Assert.Equal("ungrouped", projection.Groups[2].Key);
        Assert.Equal("Ungrouped", projection.Groups[2].Label);
    }

    [Fact]
    public void LabelForPath_uses_folder_name()
    {
        Assert.Equal("LLM", WorkspaceSessionProjection.LabelForPath(@"D:\Work\LLM"));
        Assert.Equal(@"D:\", WorkspaceSessionProjection.LabelForPath(@"D:\"));
    }

    [Fact]
    public void MergePinned_puts_pinned_first_and_adds_empty_pinned_groups()
    {
        var server = WorkspaceSessionProjection.Project(
            new List<SessionInfo> { S("a1", "A one", "ws_a", "Alpha", @"D:\Work\Alpha") });

        var merged = WorkspaceSessionProjection.MergePinned(
            server.Groups,
            new List<string> { @"D:\Work\Beta", @"D:\Work\Alpha" });

        Assert.Equal(2, merged.Count);
        Assert.Equal("Beta", merged[0].Label);
        Assert.Empty(merged[0].Sessions);
        Assert.Equal(@"D:\Work\Beta", merged[0].Path);
        Assert.Equal(["a1"], Ids(merged[1]));
    }

    private static SessionInfo S(string id, string title, string? wsId, string? label, string? path) =>
        new() { Id = id, Title = title, WorkspaceId = wsId, WorkspaceLabel = label, WorkspacePath = path };

    private static List<string> Ids(WorkspaceGroup group)
    {
        var ids = new List<string>();
        foreach (var s in group.Sessions) ids.Add(s.Id);
        return ids;
    }

}
