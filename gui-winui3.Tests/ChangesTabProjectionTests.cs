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
    public void A_live_edit_also_present_in_history_is_counted_once()
    {
        var edit = Edit("same.txt", added: 3, removed: 1);
        var result = ChangesTabProjection.Project(new[] { edit, edit });
        Assert.Single(result.Rows);
        Assert.Equal(3, result.Added);
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void Reopened_history_and_the_next_live_run_share_session_totals()
    {
        var oldEdit = Edit("old.txt", added: 4);
        var liveEdit = Edit("new.txt", added: 2);
        var history = new[] { new ChatMessageItem
        {
            Role = "assistant",
            AgentActivities = new System.Collections.ObjectModel.ObservableCollection<AgentActivityItem>(new[] { oldEdit }),
        } };
        var reopened = ChangesTabProjection.ProjectSession(history, Array.Empty<AgentActivityItem>());
        Assert.Equal(4, reopened.Added);
        var next = ChangesTabProjection.ProjectSession(history, new[] { liveEdit });
        Assert.Equal(6, next.Added);
        Assert.Equal(new[] { "new.txt", "old.txt" }, next.Rows.Select(row => row.FileName));
    }

    [Fact]
    public void Refreshing_unchanged_edits_keeps_the_open_diff_and_sends_no_collection_reset()
    {
        var activity = Edit("file.txt", added: 2);
        var rows = new System.Collections.ObjectModel.ObservableCollection<ChangesTabRow>();
        ChangesTabReconciler.Apply(rows, ChangesTabProjection.Project(new[] { activity }).Rows);
        var retained = rows[0];
        retained.IsExpanded = true;
        int notifications = 0;
        rows.CollectionChanged += (_, _) => notifications++;

        ChangesTabReconciler.Apply(rows, ChangesTabProjection.Project(new[] { activity }).Rows);
        Assert.Same(retained, rows[0]);
        Assert.True(rows[0].IsExpanded);
        Assert.Equal(0, notifications);

        ChangesTabReconciler.Apply(rows, ChangesTabProjection.Project(new[] { activity, Edit("later.txt") }).Rows);
        Assert.Same(retained, rows[1]);
        Assert.True(rows[1].IsExpanded);
    }

    [Fact]
    public void Updated_diff_content_keeps_expansion_but_other_sessions_do_not_inherit_it()
    {
        var current = new ChangesTabRow("file.txt", 1, 0, "", "a", 1, "ok", "edit-1") { IsExpanded = true };
        var rows = new System.Collections.ObjectModel.ObservableCollection<ChangesTabRow>(new[] { current });
        ChangesTabReconciler.Apply(rows, new[] { new ChangesTabRow("file.txt", 2, 0, "", "b", 1, "ok", "edit-1") });
        Assert.True(rows[0].IsExpanded);
        Assert.Equal("b", rows[0].AfterText);
        ChangesTabReconciler.Apply(rows, new[] { new ChangesTabRow("file.txt", 1, 0, "", "a", 1, "ok", "other-session") });
        Assert.False(rows[0].IsExpanded);
    }

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
            "act_" + System.Guid.NewGuid().ToString("N"),
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
