using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The grouped <c>/model</c> rows: one disabled heading per provider, models ordered the way
/// the single-provider picker orders them, and positional values that keep the provider with
/// the model a pick returns.
/// </summary>
public sealed class TuiModelGroupTests
{
    private static TuiSurfaces.ModelGroup Group(
        string wireChoice,
        string display,
        bool isCurrent,
        params (string Id, bool Supported)[] models) =>
        new(wireChoice, wireChoice, display, models, Error: null, IsCurrent: isCurrent);

    [Fact]
    public void OrderedGroupModels_PutsCurrentThenFreeThenSupportedThenUnsupported()
    {
        var group = Group("models.dev:kilo", "Kilo Gateway", isCurrent: true,
            [("b-model", true), ("free-model:free", true), ("a-model", true), ("z-model", false), ("current-model", true)]);

        var ordered = TuiSurfaces.OrderedGroupModels("current-model", group);

        Assert.Equal(
            ["current-model", "free-model:free", "a-model", "b-model", "z-model"],
            ordered.Select(item => item.Id));
        Assert.Equal([true, true, true, true, false], ordered.Select(item => item.Supported));
    }

    [Fact]
    public void OrderedGroupModels_OnlyMarksCurrentForTheActiveProvider()
    {
        var group = Group("models.dev:opencode-go", "OpenCode Go", isCurrent: false,
            [("b-model", true), ("a-model", true)]);

        var ordered = TuiSurfaces.OrderedGroupModels("a-model", group);

        Assert.Equal(["a-model", "b-model"], ordered.Select(item => item.Id));
    }

    [Fact]
    public void GroupedModelChoices_AddsAHeadingPerProviderAndPositionalTargets()
    {
        var groups = new List<TuiSurfaces.ModelGroup>
        {
            Group("models.dev:kilo", "Kilo Gateway", isCurrent: true,
                [("kilo-model", true), ("kilo-free:free", true)]),
            Group("models.dev:opencode-go", "OpenCode Go", isCurrent: false,
                [("go-model", true)]),
        };

        var (items, targets) = TuiSurfaces.GroupedModelChoices(
            "kilo-model", groups, new Dictionary<string, bool> { ["models.dev:kilo"] = true });

        Assert.Equal(["Kilo Gateway", "kilo-model", "kilo-free:free", "", "OpenCode Go", "go-model"],
            items.Select(item => item.Label));
        Assert.False(items[0].Enabled);
        Assert.True(items[1].Enabled);
        Assert.Equal("current", items[1].Detail);
        Assert.Contains("free", items[2].Detail, StringComparison.Ordinal);
        Assert.False(items[3].Enabled);
        Assert.False(items[4].Enabled);
        Assert.Equal("#0", items[1].Value);
        Assert.Equal("#2", items[5].Value);
        Assert.Equal(3, targets.Count);
        Assert.Equal("models.dev:opencode-go", targets[2].WireChoice);
        Assert.Equal("go-model", targets[2].Model);
    }

    [Fact]
    public void GroupedModelChoices_ExplicitlyMarksAnUnavailableCatalog()
    {
        var groups = new List<TuiSurfaces.ModelGroup>
        {
            new("models.dev:kilo", "models.dev:kilo", "Kilo Gateway", Array.Empty<(string, bool)>(), Error: "unauthorized"),
        };

        var (items, targets) = TuiSurfaces.GroupedModelChoices(null, groups);

        var heading = Assert.Single(items);
        Assert.Contains("catalog unavailable", heading.Detail, StringComparison.Ordinal);
        Assert.False(heading.Enabled);
        Assert.Empty(targets);
    }

    [Fact]
    public void GroupModelDetail_ReportsEveryTagThatApplies()
    {
        Assert.Equal("current", TuiSurfaces.GroupModelDetail("m", supported: true, isCurrent: true, "openai"));
        Assert.Equal("free", TuiSurfaces.GroupModelDetail("m:free", supported: true, isCurrent: false, "openai"));
        Assert.Equal("unsupported", TuiSurfaces.GroupModelDetail("m", supported: false, isCurrent: false, "openai"));
        Assert.Equal("no key", TuiSurfaces.GroupModelDetail(
            "m", supported: true, isCurrent: false, "models.dev:kilo",
            new Dictionary<string, bool> { ["models.dev:kilo"] = false }));
        Assert.Equal(string.Empty, TuiSurfaces.GroupModelDetail("m", supported: true, isCurrent: false, "openai"));
    }
}
