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
        var row = Assert.Single(result.Rows);
        Assert.Single(row.Edits);
        Assert.Equal(3, result.Added);
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void Repeated_edits_of_one_file_share_a_row_with_summed_counts_newest_edit_first()
    {
        var first = Edit("server.py", added: 3, removed: 1, lineStart: 10);
        var second = Edit("server.py", added: 2, removed: 2, lineStart: 40);
        var projection = ChangesTabProjection.Project(new[] { first, Edit("test_server.py", added: 5), second });

        Assert.Equal(new[] { "server.py", "test_server.py" }, projection.Rows.Select(row => row.FileName));
        var row = projection.Rows[0];
        Assert.Equal(5, row.Added);
        Assert.Equal(3, row.Removed);
        Assert.Equal(new[] { second.ActivityId, first.ActivityId }, row.Edits.Select(edit => edit.ActivityId));
        Assert.Equal("2 edits", row.EditCountText);
        Assert.Equal(string.Empty, projection.Rows[1].EditCountText);
        Assert.Equal(10, projection.Added);
    }

    [Theory]
    [InlineData(AgentActivityState.Failed)]
    [InlineData(AgentActivityState.Started)]
    [InlineData(AgentActivityState.Waiting)]
    public void Edits_that_did_not_complete_are_not_listed_or_counted(AgentActivityState state)
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("server.py", added: 4),
            new AgentActivityItem(Activity(state, AgentActivityKind.Edit, fileName: "server.py", added: 9)),
            new AgentActivityItem(Activity(state, AgentActivityKind.Edit, fileName: "other.py", added: 9)),
        });

        var row = Assert.Single(projection.Rows);
        Assert.Single(row.Edits);
        Assert.Equal(4, projection.Added);
    }

    [Fact]
    public void Unknown_counts_read_as_a_dash_not_as_zero()
    {
        var row = ChangesTabProjection.Project(new[] { Edit("empty.txt") }).Rows[0];

        Assert.Null(row.Added);
        Assert.Equal("—", row.AddedDeltaText);
        Assert.Equal("—", row.RemovedDeltaText);
    }

    [Fact]
    public void An_edit_without_a_file_name_is_listed_as_a_patch()
    {
        var row = Assert.Single(ChangesTabProjection.Project(new[] { Edit("", added: 2) }).Rows);
        Assert.Equal(ChangesTabProjection.UnnamedFile, row.FileName);
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
    public void Another_edit_of_an_open_file_keeps_its_row_expanded()
    {
        var first = Edit("file.txt", added: 1);
        var rows = new System.Collections.ObjectModel.ObservableCollection<ChangesTabRow>();
        ChangesTabReconciler.Apply(rows, ChangesTabProjection.Project(new[] { first }).Rows);
        rows[0].IsExpanded = true;

        ChangesTabReconciler.Apply(rows, ChangesTabProjection.Project(new[] { first, Edit("file.txt", added: 2) }).Rows);

        Assert.True(rows[0].IsExpanded);
        Assert.Equal(2, rows[0].Edits.Count);
        Assert.Equal(3, rows[0].Added);
    }

    [Fact]
    public void Another_session_with_the_same_file_name_does_not_inherit_expansion()
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<ChangesTabRow>();
        ChangesTabReconciler.Apply(rows, ChangesTabProjection.Project(new[] { Edit("file.txt", added: 1) }).Rows);
        rows[0].IsExpanded = true;

        ChangesTabReconciler.Apply(rows, ChangesTabProjection.Project(new[] { Edit("file.txt", added: 1) }).Rows);

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
    public void Rows_are_most_recently_edited_first()
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("first.txt"),
            Edit("second.txt"),
            Edit("third.txt"),
            Edit("first.txt"),
        });

        Assert.Equal(
            new[] { "first.txt", "third.txt", "second.txt" },
            projection.Rows.Select(row => row.FileName));
    }

    [Fact]
    public void The_row_cap_keeps_the_newest_files_and_totals_cover_everything()
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
    public void The_edit_cap_keeps_the_newest_edits_and_the_row_total_covers_all()
    {
        var edits = Enumerable.Range(0, ChangesTabProjection.MaxEditsPerRow + 3)
            .Select(_ => Edit("busy.txt", added: 1)).ToArray();

        var row = Assert.Single(ChangesTabProjection.Project(edits).Rows);

        Assert.Equal(ChangesTabProjection.MaxEditsPerRow, row.Edits.Count);
        Assert.Equal(edits[^1].ActivityId, row.Edits[0].ActivityId);
        Assert.Equal(ChangesTabProjection.MaxEditsPerRow + 3, row.Added);
    }

    [Fact]
    public void Clicking_an_old_edit_keeps_it_in_the_normal_bounded_list_without_changing_totals()
    {
        var edits = Enumerable.Range(0, ChangesTabProjection.MaxRows + 5)
            .Select(index => Edit($"file-{index}.txt", added: 1, removed: 2)).ToArray();
        var projection = ChangesTabProjection.Project(edits, edits[0].ActivityId);

        Assert.Equal(ChangesTabProjection.MaxRows, projection.Rows.Count);
        Assert.True(projection.Rows[0].Contains(edits[^1].ActivityId));
        Assert.True(projection.Rows[^1].Contains(edits[0].ActivityId));
        Assert.Equal(ChangesTabProjection.MaxRows + 5, projection.Added);
        Assert.Equal((ChangesTabProjection.MaxRows + 5) * 2, projection.Removed);
        Assert.DoesNotContain(ChangesTabProjection.Project(edits).Rows, row => row.Contains(edits[0].ActivityId));
    }

    [Fact]
    public void Rows_carry_each_edit_preview_and_the_expansion_starts_folded()
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("ChatView.xaml", added: 3, removed: 1, lineStart: 42, detail: "@@ old-line ==> new-line"),
        });

        var row = projection.Rows[0];
        var edit = Assert.Single(row.Edits);
        Assert.Equal(3, row.Added);
        Assert.Equal(1, row.Removed);
        Assert.Equal(42, edit.StartLine);
        Assert.Equal("Line 42", edit.LocationText);
        Assert.Equal("old-line", edit.BeforeText);
        Assert.Equal("new-line", edit.AfterText);
        Assert.True(edit.HasPreview);
        Assert.False(row.IsExpanded);

        row.IsExpanded = true;
        Assert.Equal("", row.ExpansionGlyph);
    }

    [Fact]
    public void Same_named_files_in_different_folders_get_their_own_rows_with_folders()
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("server.py", added: 1, relativePath: "api/server.py"),
            Edit("server.py", added: 2, relativePath: "worker/server.py"),
            Edit("README.md", added: 3, relativePath: "README.md"),
        });

        Assert.Equal(new[] { "README.md", "worker/server.py", "api/server.py" }, projection.Rows.Select(row => row.RelativePath));
        Assert.Equal("worker", projection.Rows[1].Folder);
        Assert.Null(projection.Rows[0].Folder);
        Assert.True(projection.Rows[0].HasRelativePath);
    }

    [Fact]
    public void A_basename_only_edit_joins_the_one_path_with_its_name()
    {
        var withPath = Edit("server.py", added: 1, relativePath: "api/server.py");
        var absolute = Edit("server.py", added: 2);

        var row = Assert.Single(ChangesTabProjection.Project(new[] { withPath, absolute }).Rows);

        Assert.Equal("api/server.py", row.RelativePath);
        Assert.Equal(3, row.Added);
    }

    [Fact]
    public void A_basename_only_edit_stays_apart_when_its_name_is_ambiguous()
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("server.py", relativePath: "api/server.py"),
            Edit("server.py", relativePath: "worker/server.py"),
            Edit("server.py"),
        });

        Assert.Equal(3, projection.Rows.Count);
        Assert.Single(projection.Rows, row => row.RelativePath is null);
    }

    [Theory]
    [InlineData(".tmp_probe.py", true)]
    [InlineData("tmp_diag.ps1", true)]
    [InlineData("server.py.orig", true)]
    [InlineData("~$report.docx", true)]
    [InlineData("server.py", false)]
    [InlineData("template.html", false)]
    public void Scratch_files_are_recognized_by_name(string fileName, bool scratch)
    {
        Assert.Equal(scratch, ChangesTabFiles.IsScratch(fileName));
        Assert.Equal(scratch, ChangesTabProjection.Project(new[] { Edit(fileName) }).Rows[0].IsScratch);
    }

    [Theory]
    [InlineData("src/app.py", true)]
    [InlineData("../outside.py", false)]
    [InlineData("src/../../outside.py", false)]
    [InlineData(null, false)]
    public void Paths_resolve_only_inside_the_workspace(string? relativePath, bool resolves)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "ru-changes-root");
        var resolved = ChangesTabFiles.Resolve(workspace, relativePath);

        Assert.Equal(resolves, resolved is not null);
        if (resolved is not null) Assert.StartsWith(workspace, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ChangesTabFiles.Resolve(null, "src/app.py"));
    }

    private static AgentActivityItem Edit(
        string fileName,
        int? added = null,
        int? removed = null,
        int? lineStart = null,
        string? detail = null,
        string? relativePath = null) =>
        new(Activity(
            AgentActivityState.Completed,
            AgentActivityKind.Edit,
            fileName: fileName,
            lineStart: lineStart,
            added: added,
            removed: removed,
            detail: detail,
            relativePath: relativePath));

    private static AgentActivityModel Activity(
        AgentActivityState state,
        AgentActivityKind kind,
        string? fileName = null,
        int? lineStart = null,
        int? added = null,
        int? removed = null,
        string? symbol = null,
        string? detail = null,
        string? relativePath = null) =>
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
            Todos: null,
            RelativePath: relativePath);
}
