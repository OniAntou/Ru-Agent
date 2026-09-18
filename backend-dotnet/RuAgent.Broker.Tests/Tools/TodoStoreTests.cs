using RuAgent.Broker.Tools.Todo;
using Xunit;

namespace RuAgent.Broker.Tests.Tools;

public sealed class TodoStoreTests
{
    private static IReadOnlyList<TodoRow> Rows(params string[] contents) =>
        contents
            .Select((content, index) => new TodoRow(
                content,
                index == 0 ? TodoListValidator.StatusInProgress : TodoListValidator.StatusPending))
            .ToArray();

    [Fact]
    public void A_session_reads_back_what_it_wrote()
    {
        var store = new TodoStore();

        store.Replace("session-a", Rows("one", "two"));

        Assert.Equal(2, store.Read("session-a").Count);
        Assert.Equal("one", store.Read("session-a")[0].Content);
    }

    [Fact]
    public void Two_sessions_do_not_see_each_other()
    {
        var store = new TodoStore();
        store.Replace("session-a", Rows("one"));
        store.Replace("session-b", Rows("two", "three"));

        Assert.Single(store.Read("session-a"));
        Assert.Equal(2, store.Read("session-b").Count);
    }

    [Fact]
    public void A_session_with_no_write_reads_as_empty()
    {
        Assert.Empty(new TodoStore().Read("never-written"));
    }

    [Fact]
    public void A_blank_session_id_reads_as_empty_rather_than_throwing()
    {
        var store = new TodoStore();
        store.Replace("session-a", Rows("one"));

        Assert.Empty(store.Read(string.Empty));
        Assert.Empty(store.Read("   "));
    }

    [Fact]
    public void A_second_write_replaces_the_first_entirely()
    {
        // The model sends the whole list every time, so a row that is not resent is
        // gone rather than merged forward.
        var store = new TodoStore();
        store.Replace("session-a", Rows("one", "two"));

        store.Replace("session-a", Rows("three"));

        var rows = store.Read("session-a");
        Assert.Single(rows);
        Assert.Equal("three", rows[0].Content);
    }

    [Fact]
    public void Removing_a_session_clears_only_that_session()
    {
        var store = new TodoStore();
        store.Replace("session-a", Rows("one"));
        store.Replace("session-b", Rows("two"));

        store.Remove("session-a");

        Assert.Empty(store.Read("session-a"));
        Assert.Single(store.Read("session-b"));
    }

    [Fact]
    public void Removing_a_session_that_was_never_written_is_harmless()
    {
        var store = new TodoStore();

        store.Remove("never-written");

        Assert.Empty(store.Read("never-written"));
    }

    [Fact]
    public void The_stored_list_cannot_be_mutated_by_its_caller()
    {
        // The store hands out what it holds, so a caller that kept the array it passed
        // in must not be able to change the stored plan afterwards.
        var store = new TodoStore();
        var rows = new List<TodoRow> { new("one", TodoListValidator.StatusPending) };
        store.Replace("session-a", rows);

        rows.Add(new TodoRow("two", TodoListValidator.StatusPending));

        Assert.Single(store.Read("session-a"));
    }

    [Fact]
    public void The_summary_counts_each_status()
    {
        var rows = new[]
        {
            new TodoRow("a", TodoListValidator.StatusPending),
            new TodoRow("b", TodoListValidator.StatusPending),
            new TodoRow("c", TodoListValidator.StatusInProgress),
            new TodoRow("d", TodoListValidator.StatusCompleted),
        };

        Assert.Equal(
            "Updated todo list: 2 pending, 1 in progress, 1 completed.",
            TodoStore.Describe(rows));
    }

    [Fact]
    public void The_summary_counts_an_empty_list_as_zeroes()
    {
        Assert.Equal(
            "Updated todo list: 0 pending, 0 in progress, 0 completed.",
            TodoStore.Describe(Array.Empty<TodoRow>()));
    }
}
