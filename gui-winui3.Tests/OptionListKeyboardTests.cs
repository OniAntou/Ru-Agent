using RuApp.Services;
using Windows.System;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The key handling both option flyouts share, pinned away from the XAML.
/// </summary>
/// <remarks>
/// The model list is every model a provider publishes, which on models.dev is several
/// hundred rows. As tab stops that is a trap: Tab lands in the list and then has to come
/// back out one row at a time. These tests are about the rules that make the keyboard a
/// complete way to use the list, and about the two that stop it becoming a second,
/// conflicting answer about what is selected.
/// </remarks>
public sealed class OptionListKeyboardTests
{
    [Fact]
    public void The_cursor_starts_nowhere_so_an_unopened_list_marks_no_row()
    {
        var keys = new OptionListKeyboard(3, _ => { }, () => { });

        Assert.Equal(-1, keys.Cursor);
    }

    [Fact]
    public void Arrows_move_the_cursor_one_row_and_stop_at_both_ends()
    {
        var keys = new OptionListKeyboard(3, _ => { }, () => { });

        Assert.True(keys.HandleKey(VirtualKey.Down, spaceActivates: false));
        Assert.Equal(0, keys.Cursor);

        Assert.True(keys.HandleKey(VirtualKey.Down, spaceActivates: false));
        Assert.Equal(1, keys.Cursor);

        Assert.True(keys.HandleKey(VirtualKey.Down, spaceActivates: false));
        Assert.True(keys.HandleKey(VirtualKey.Down, spaceActivates: false));
        Assert.Equal(2, keys.Cursor);

        // Past the end is the end, not a wrap: a picker that wraps sends Down from the
        // last row to the top, which reads as a different list appearing.
        Assert.True(keys.HandleKey(VirtualKey.Up, spaceActivates: false));
        Assert.Equal(1, keys.Cursor);
    }

    [Fact]
    public void Arrowing_off_the_top_leaves_the_mark_on_the_first_row()
    {
        // It used to clear the cursor instead, and a highlight that vanishes while the
        // user is still pressing Up reads as a broken control, not as the end of a list.
        var keys = new OptionListKeyboard(3, _ => { }, () => { });
        keys.MoveCursor(0);

        keys.MoveCursorBy(-1);
        keys.MoveCursorBy(-1);

        Assert.Equal(0, keys.Cursor);
    }

    [Fact]
    public void An_unopened_list_takes_the_first_arrow_as_the_first_row()
    {
        // The cursor starts at -1, which is not a row. Reading it as row minus one would
        // make the first Down land on the second row.
        var keys = new OptionListKeyboard(5, _ => { }, () => { });

        keys.HandleKey(VirtualKey.Down, spaceActivates: false);

        Assert.Equal(0, keys.Cursor);
    }

    [Fact]
    public void Home_and_End_jump_to_the_ends_of_the_whole_list()
    {
        var keys = new OptionListKeyboard(30, _ => { }, () => { });

        Assert.True(keys.HandleKey(VirtualKey.End, spaceActivates: false));
        Assert.Equal(29, keys.Cursor);

        Assert.True(keys.HandleKey(VirtualKey.Home, spaceActivates: false));
        Assert.Equal(0, keys.Cursor);
    }

    [Fact]
    public void Page_keys_move_a_screenful_rather_than_a_row()
    {
        var keys = new OptionListKeyboard(40, _ => { }, () => { });
        keys.MoveCursor(20);

        Assert.True(keys.HandleKey(VirtualKey.PageDown, spaceActivates: false));
        Assert.Equal(28, keys.Cursor);

        Assert.True(keys.HandleKey(VirtualKey.PageUp, spaceActivates: false));
        Assert.Equal(20, keys.Cursor);
    }

    [Fact]
    public void Enter_commits_the_row_under_the_cursor()
    {
        var committed = new List<int>();
        var keys = new OptionListKeyboard(5, committed.Add, () => { });
        keys.MoveCursor(3);

        Assert.True(keys.HandleKey(VirtualKey.Enter, spaceActivates: false));

        Assert.Equal([3], committed);
    }

    [Fact]
    public void Enter_with_no_row_under_the_cursor_commits_nothing()
    {
        var committed = new List<int>();
        var keys = new OptionListKeyboard(5, committed.Add, () => { });

        keys.ActivateCursor();

        // Committing row zero because it was the nearest thing to nothing would change
        // the model without the user having pointed at one.
        Assert.Empty(committed);
    }

    [Fact]
    public void Escape_dismisses_without_committing()
    {
        var committed = new List<int>();
        var dismissed = false;
        var keys = new OptionListKeyboard(3, committed.Add, () => dismissed = true);
        keys.MoveCursor(2);

        Assert.True(keys.HandleKey(VirtualKey.Escape, spaceActivates: false));

        Assert.True(dismissed);
        Assert.Empty(committed);
    }

    [Fact]
    public void A_key_the_list_does_not_use_is_left_alone()
    {
        var dismissed = false;
        var keys = new OptionListKeyboard(3, _ => { }, () => dismissed = true);

        Assert.False(keys.HandleKey(VirtualKey.Tab, spaceActivates: false));
        Assert.False(keys.HandleKey(VirtualKey.A, spaceActivates: false));
        Assert.False(keys.HandleKey(VirtualKey.Back, spaceActivates: false));
        Assert.False(dismissed);
    }

    [Fact]
    public void Space_is_a_character_when_a_text_field_holds_the_focus()
    {
        // The model picker's focus is the search box, so Space types "gpt 4o". Treating
        // it as a commit would pick a model in the middle of typing a query.
        var committed = new List<int>();
        var keys = new OptionListKeyboard(3, committed.Add, () => { });
        keys.MoveCursor(1);

        Assert.False(keys.HandleKey(VirtualKey.Space, spaceActivates: false));
        Assert.Empty(committed);
        Assert.Equal(1, keys.Cursor);
    }

    [Fact]
    public void Space_commits_when_no_text_field_holds_the_focus()
    {
        // The reasoning list has no field, so the space bar would otherwise be dead.
        var committed = new List<int>();
        var keys = new OptionListKeyboard(3, committed.Add, () => { });
        keys.MoveCursor(0);

        Assert.True(keys.HandleKey(VirtualKey.Space, spaceActivates: true));
        Assert.Equal([0], committed);
    }

    [Fact]
    public void The_view_is_told_where_the_cursor_went_so_it_can_repaint()
    {
        var seen = new List<int>();
        var keys = new OptionListKeyboard(3, _ => { }, () => { });
        keys.CursorChanged += seen.Add;

        keys.MoveCursor(1);
        keys.MoveCursorBy(1);

        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void Repainting_is_not_asked_for_when_the_cursor_did_not_move()
    {
        // A pointer crossing a row, or a key press at the end of the list, lands on the
        // index the cursor is already on. Repainting then is work per mouse move for no
        // visible change.
        var repaints = 0;
        var keys = new OptionListKeyboard(3, _ => { }, () => { });
        keys.CursorChanged += _ => repaints++;

        Assert.True(keys.MoveCursor(1));
        Assert.False(keys.MoveCursor(1));
        Assert.True(keys.MoveCursor(0));
        Assert.True(keys.MoveCursor(1));

        Assert.Equal(3, repaints);
    }

    [Fact]
    public void The_view_is_asked_to_fetch_a_row_only_when_one_could_be_off_screen()
    {
        // The pointer has just arrived at the row it is asking for, so a fetch per mouse
        // move would be scroll requests for rows that are by definition visible.
        var fetched = new List<int>();
        var keys = new OptionListKeyboard(
            3, _ => { }, () => { }, fetched.Add);

        keys.MoveCursor(1, show: true);
        keys.MoveCursor(0, show: false);
        keys.MoveCursor(2);

        Assert.Equal([1, 2], fetched);
    }

    [Fact]
    public void A_list_the_view_cannot_scroll_still_navigates()
    {
        var keys = new OptionListKeyboard(3, _ => { }, () => { });

        Assert.True(keys.HandleKey(VirtualKey.End, spaceActivates: false));
        Assert.Equal(2, keys.Cursor);
    }

    [Fact]
    public void A_row_can_be_cleared_so_no_row_is_marked()
    {
        var keys = new OptionListKeyboard(3, _ => { }, () => { });
        keys.MoveCursor(1);

        Assert.True(keys.MoveCursor(-1));
        Assert.Equal(-1, keys.Cursor);
    }

    [Fact]
    public void An_empty_list_walks_nowhere_and_commits_nothing()
    {
        var committed = new List<int>();
        var keys = new OptionListKeyboard(
            0, committed.Add, () => { });

        Assert.False(keys.HandleKey(VirtualKey.Down, spaceActivates: false));
        Assert.False(keys.HandleKey(VirtualKey.End, spaceActivates: false));
        Assert.False(keys.HandleKey(VirtualKey.Enter, spaceActivates: false));
        keys.ActivateCursor();

        Assert.Empty(committed);
        Assert.Equal(-1, keys.Cursor);
    }

    [Fact]
    public void Escape_still_leaves_an_empty_list()
    {
        // The model list really can be empty, and a user who has just been told their
        // query matched nothing is looking at a panel with no rows in it. Gating Escape
        // on there being rows would mean the emptier the results get, the harder the
        // list is to back out of.
        var dismissed = false;
        var keys = new OptionListKeyboard(
            0, _ => { }, () => dismissed = true);

        Assert.True(keys.HandleKey(VirtualKey.Escape, spaceActivates: false));
        Assert.True(dismissed);
    }

}
