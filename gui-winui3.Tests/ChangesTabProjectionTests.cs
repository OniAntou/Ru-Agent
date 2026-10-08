using System.Linq;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChangesTabProjectionTests
{
    [Fact]
    public void Non_edit_activities_are_ignored()
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("a.txt"),
            new AgentActivityItem(Activity(AgentActivityState.Completed, AgentActivityKind.Search, symbol: "query")),
        });

        Assert.Single(projection.Rows);
        Assert.Equal("a.txt", projection.Rows[0].FileName);
    }

    [Fact]
    public void Rows_are_newest_first()
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("first.txt"),
            Edit("second.txt"),
            Edit("third.txt"),
        });

        Assert.Equal(
            new[] { "third.txt", "second.txt", "first.txt" },
            projection.Rows.Select(row => row.FileName));
    }

    [Fact]
    public void The_row_cap_keeps_the_newest_rows_and_totals_cover_everything()
    {
        var edits = Enumerable.Range(0, ChangesTabProjection.MaxRows + 5)
            .Select(index => Edit($"file-{index}.txt", added: 1, removed: 2))
            .ToArray();

        var projection = ChangesTabProjection.Project(edits);

        Assert.Equal(ChangesTabProjection.MaxRows, projection.Rows.Count);
        Assert.Equal($"file-{ChangesTabProjection.MaxRows + 4}.txt", projection.Rows[0].FileName);
        Assert.Equal(ChangesTabProjection.MaxRows + 5, projection.Added);
        Assert.Equal((ChangesTabProjection.MaxRows + 5) * 2, projection.Removed);
    }

    [Fact]
    public void Rows_carry_the_preview_and_the_expansion_starts_folded()
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("ChatView.xaml", added: 3, removed: 1, lineStart: 42, detail: "@@ old-line ==> new-line"),
        });

        var row = projection.Rows[0];
        Assert.Equal(3, row.Added);
        Assert.Equal(1, row.Removed);
        Assert.Equal(42, row.StartLine);
        Assert.Equal("old-line", row.BeforeText);
        Assert.Equal("new-line", row.AfterText);
        Assert.False(row.IsExpanded);

        row.IsExpanded = true;
        Assert.Equal("\uE70D", row.ExpansionGlyph);
    }

    [Fact]
    public void Null_deltas_read_as_zero()
    {
        var projection = ChangesTabProjection.Project(new[] { Edit("empty.txt") });

        Assert.Equal(0, projection.Rows[0].Added);
        Assert.Equal(0, projection.Rows[0].Removed);
    }

    private static AgentActivityItem Edit(
        string fileName,
        int? added = null,
        int? removed = null,
        int? lineStart = null,
        string? detail = null) =>
        new(Activity(
            AgentActivityState.Completed,
            AgentActivityKind.Edit,
            fileName: fileName,
            lineStart: lineStart,
            added: added,
            removed: removed,
            detail: detail));

    private static AgentActivityModel Activity(
        AgentActivityState state,
        AgentActivityKind kind,
        string? fileName = null,
        int? lineStart = null,
        int? added = null,
        int? removed = null,
        string? symbol = null,
        string? detail = null) =>
        new(
            "act_22222222222222222222222222222222",
            kind,
            state,
            fileName,
            lineStart,
            null,
            symbol,
            null,
            added,
            removed,
            null,
            "succeeded",
            null,
            null,
            1,
            detail,
            Todos: null);
}
