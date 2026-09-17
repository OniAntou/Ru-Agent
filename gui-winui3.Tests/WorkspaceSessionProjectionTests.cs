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

        var projection = WorkspaceSessionProjection.Project(sessions, "");

        Assert.Equal(3, projection.Groups.Count);
        Assert.Equal("ws_b", projection.Groups[0].Key);
        Assert.Equal("Beta", projection.Groups[0].Label);
        Assert.Equal(["b1"], Ids(projection.Groups[0]));
        Assert.Equal(["a1", "a2"], Ids(projection.Groups[1]));
        Assert.Equal("ungrouped", projection.Groups[2].Key);
        Assert.Equal("Ungrouped", projection.Groups[2].Label);
    }

    [Theory]
    [InlineData("alpha", new[] { "a1", "a2" })]
    [InlineData("B ONE", new[] { "b1" })]
    [InlineData("zzz", new string[0])]
    public void Search_filters_across_groups_and_drops_empties(string query, string[] expected)
    {
        var sessions = new List<SessionInfo>
        {
            S("b1", "B one", "ws_b", "Beta", null),
            S("a1", "A one", "ws_a", "Alpha", null),
            S("a2", "A two", "ws_a", "Alpha", null),
        };

        var projection = WorkspaceSessionProjection.Project(sessions, query);

        Assert.Equal(expected, AllIds(projection));
    }

    private static SessionInfo S(string id, string title, string? wsId, string? label, string? path) =>
        new() { Id = id, Title = title, WorkspaceId = wsId, WorkspaceLabel = label, WorkspacePath = path };

    private static List<string> Ids(WorkspaceGroup group)
    {
        var ids = new List<string>();
        foreach (var s in group.Sessions) ids.Add(s.Id);
        return ids;
    }

    private static List<string> AllIds(WorkspaceProjection projection)
    {
        var ids = new List<string>();
        foreach (var g in projection.Groups) ids.AddRange(Ids(g));
        return ids;
    }
}
