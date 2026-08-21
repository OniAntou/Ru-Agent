using System.Collections.Specialized;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class BatchObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_publishes_one_reset_after_replacing_items()
    {
        var items = new BatchObservableCollection<int> { 1, 2 };
        var actions = new List<NotifyCollectionChangedAction>();
        items.CollectionChanged += (_, args) => actions.Add(args.Action);

        items.ReplaceAll(new[] { 3, 4, 5 });

        Assert.Equal(new[] { 3, 4, 5 }, items);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, actions);
    }
}
