using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class NavigationHistoryTests
{
    private static NavigationHistory<string> Visited(params string[] places)
    {
        var history = new NavigationHistory<string>();
        foreach (var place in places) history.Record(place);
        return history;
    }

    private static bool Any(string _) => true;

    [Fact]
    public void An_empty_history_goes_nowhere()
    {
        var history = new NavigationHistory<string>();
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.False(history.HasCurrent);
        Assert.False(history.TryMove(-1, Any, out _));
        Assert.Throws<InvalidOperationException>(() => history.Current);
    }

    [Fact]
    public void Back_and_forward_walk_the_visits_in_order()
    {
        var history = Visited("a", "b", "c");
        Assert.Equal("c", history.Current);
        Assert.True(history.CanGoBack);
        Assert.False(history.CanGoForward);

        Assert.True(history.TryMove(-1, Any, out var entry));
        Assert.Equal("b", entry);
        Assert.True(history.TryMove(-1, Any, out entry));
        Assert.Equal("a", entry);
        Assert.False(history.CanGoBack);
        Assert.False(history.TryMove(-1, Any, out _));
        Assert.Equal("a", history.Current);

        Assert.True(history.TryMove(1, Any, out entry));
        Assert.Equal("b", entry);
        Assert.True(history.CanGoForward);
    }

    [Fact]
    public void Showing_the_current_place_again_is_not_a_visit()
    {
        var history = Visited("a", "b");
        history.Record("b");
        Assert.Equal(2, history.Count);
        Assert.True(history.TryMove(-1, Any, out var entry));
        Assert.Equal("a", entry);
    }

    [Fact]
    public void A_new_visit_drops_what_lay_ahead()
    {
        var history = Visited("a", "b", "c");
        history.TryMove(-1, Any, out _);
        history.TryMove(-1, Any, out _);
        history.Record("d");

        Assert.False(history.CanGoForward);
        Assert.Equal(2, history.Count);
        Assert.True(history.TryMove(-1, Any, out var entry));
        Assert.Equal("a", entry);
    }

    [Fact]
    public void A_place_may_be_visited_twice_when_something_else_came_between()
    {
        var history = Visited("a", "b", "a");
        Assert.Equal(3, history.Count);
        history.TryMove(-1, Any, out var entry);
        Assert.Equal("b", entry);
    }

    [Fact]
    public void Entries_that_no_longer_exist_are_skipped_and_forgotten_going_back()
    {
        var history = Visited("a", "gone1", "gone2", "d");
        Assert.True(history.TryMove(-1, place => !place.StartsWith("gone"), out var entry));
        Assert.Equal("a", entry);
        Assert.Equal(2, history.Count);
        Assert.False(history.CanGoBack);
        Assert.True(history.CanGoForward);
        Assert.True(history.TryMove(1, Any, out entry));
        Assert.Equal("d", entry);
    }

    [Fact]
    public void Entries_that_no_longer_exist_are_skipped_and_forgotten_going_forward()
    {
        var history = Visited("a", "gone1", "gone2", "d");
        history.TryMove(-1, Any, out _);
        history.TryMove(-1, Any, out _);
        history.TryMove(-1, Any, out _);
        Assert.Equal("a", history.Current);

        Assert.True(history.TryMove(1, place => !place.StartsWith("gone"), out var entry));
        Assert.Equal("d", entry);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void When_nothing_valid_lies_that_way_the_position_stays_put()
    {
        var history = Visited("a", "gone", "c");
        Assert.False(history.TryMove(-1, place => place == "never", out _));
        Assert.Equal("c", history.Current);
        Assert.Equal(1, history.Count);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void The_oldest_entries_fall_off_at_capacity()
    {
        var history = new NavigationHistory<int>(capacity: 3);
        for (int i = 1; i <= 5; i++) history.Record(i);

        Assert.Equal(3, history.Count);
        Assert.Equal(5, history.Current);
        history.TryMove(-1, _ => true, out var entry);
        history.TryMove(-1, _ => true, out entry);
        Assert.Equal(3, entry);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void A_location_is_a_screen_and_a_session_and_compares_by_value()
    {
        var history = new NavigationHistory<NavLocation>();
        history.Record(new NavLocation(ShellDestination.Chat, "s1"));
        history.Record(new NavLocation(ShellDestination.Chat, "s1"));
        history.Record(new NavLocation(ShellDestination.Customize, null));
        history.Record(new NavLocation(ShellDestination.Chat, "s2"));

        Assert.Equal(3, history.Count);
        history.TryMove(-1, _ => true, out var entry);
        Assert.Equal(new NavLocation(ShellDestination.Customize, null), entry);
    }

    [Fact]
    public void Every_destination_has_a_tag_that_resolves_back_to_it()
    {
        foreach (var destination in Enum.GetValues<ShellDestination>())
            Assert.Equal(destination, ShellRoutePolicy.Resolve(ShellRoutePolicy.TagFor(destination)));
    }

    [Fact]
    public void A_capacity_below_two_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NavigationHistory<int>(capacity: 1));
    }
}
