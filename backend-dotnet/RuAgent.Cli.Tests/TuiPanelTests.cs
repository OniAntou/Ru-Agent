using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiPanelTests
{
    private static readonly IReadOnlyList<PanelSection> Sample =
    [
        new("session", [new PanelRow("session", "a2026184"), new PanelRow("preset", "standard")]),
    ];

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void Compose_EveryLineFillsExactlyTheWidth(int width)
    {
        var lines = TuiPanel.Compose(width, "status", Sample, "esc");

        Assert.All(lines, line => Assert.Equal(width, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void Compose_DrawsTitleSectionsAndRows()
    {
        var text = string.Join("\n", TuiPanel.Compose(80, "status", Sample, "esc").Select(TuiText.Strip));

        Assert.Contains("status", text, StringComparison.Ordinal);
        Assert.Contains("session", text, StringComparison.Ordinal);
        Assert.Contains("a2026184", text, StringComparison.Ordinal);
        Assert.Contains("preset", text, StringComparison.Ordinal);
        Assert.Contains("standard", text, StringComparison.Ordinal);
        Assert.Contains("esc", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ClosesEveryRowWithABorder()
    {
        var lines = TuiPanel.Compose(60, "status", Sample).Select(TuiText.Strip).ToList();

        Assert.StartsWith("╭", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("╮", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("╯", lines[^1], StringComparison.Ordinal);
        foreach (var row in lines.Where(l => l.StartsWith('│')))
        {
            Assert.EndsWith("│", row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Compose_DropsTheDetailWhenTheLabelColumnLeavesNoRoom()
    {
        var narrow = TuiPanel.Compose(24, "s", Sample).Select(TuiText.Strip).ToList();

        Assert.All(narrow, line => Assert.Equal(24, TuiText.VisibleWidth(line)));
        Assert.Contains(narrow, row => row.Contains("session", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_TruncatesAVeryLongRowInsteadOfWrapping()
    {
        var longValue = new string('x', 500);
        var lines = TuiPanel.Compose(50, "status", [new PanelSection(null, [new PanelRow("path", longValue)])]);

        Assert.All(lines, line => Assert.Equal(50, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void Compose_WithoutFooterStillClosesTheFrame()
    {
        var lines = TuiPanel.Compose(40, "status", Sample).Select(TuiText.Strip).ToList();

        Assert.EndsWith("╯", lines[^1], StringComparison.Ordinal);
        Assert.Equal(40, TuiText.VisibleWidth(string.Join(string.Empty, lines[^1])));
    }

    [Fact]
    public void Compose_ForAnUnknownTitle_UsesTheDefaultLabel()
    {
        var text = string.Join("\n", TuiPanel.Compose(40, "   ", Sample).Select(TuiText.Strip));

        Assert.Contains("info", text, StringComparison.Ordinal);
    }
}

public sealed class TuiHelpPanelTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void Help_ListsEveryKnownCommand(int width)
    {
        var text = string.Join("\n", TuiSurfaces.Help(width).Select(TuiText.Strip));

        foreach (var command in InteractiveLineReader.KnownCommands)
        {
            Assert.Contains(command.Command, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Help_CarriesEachCommandDescription()
    {
        var text = string.Join("\n", TuiSurfaces.Help(120).Select(TuiText.Strip));

        Assert.Contains("Switch or show the active model", text, StringComparison.Ordinal);
        Assert.Contains("Quit", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_FramesItselfAtEveryWidth()
    {
        foreach (var width in new[] { 24, 40, 60, 100 })
        {
            Assert.All(TuiSurfaces.Help(width), line => Assert.Equal(width, TuiText.VisibleWidth(line)));
        }
    }
}

public sealed class TuiStatusCardTests
{
    private static TuiStatusSnapshot Snapshot() => new(
        SessionId: "a20261842de34c8b99ab5fc312a6f5e4",
        Preset: "standard",
        Backend: "Kilo Gateway",
        Model: "kilocode/gpt-5-codex",
        Workspace: "D:\\LLM",
        Turn: 3,
        TotalTokens: 12400,
        ToolsSettled: 4,
        ApiKeyConfigured: true);

    [Fact]
    public void Status_ShowsTheRunFacts()
    {
        var text = string.Join("\n", TuiSurfaces.Status(100, Snapshot()).Select(TuiText.Strip));

        Assert.Contains("a20261842de34c8b99a…", text, StringComparison.Ordinal);
        Assert.Contains("standard", text, StringComparison.Ordinal);
        Assert.Contains("Kilo Gateway", text, StringComparison.Ordinal);
        Assert.Contains("kilocode/gpt-5-codex", text, StringComparison.Ordinal);
        Assert.Contains("12400", text, StringComparison.Ordinal);
        Assert.Contains("…\\LLM", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ReportsAMissingKeyOnlyWhenItIsKnown()
    {
        var missing = Snapshot() with { ApiKeyConfigured = false };
        var unknown = Snapshot() with { ApiKeyConfigured = null };

        Assert.Contains("missing", string.Join("\n", TuiSurfaces.Status(100, missing).Select(TuiText.Strip)), StringComparison.Ordinal);
        Assert.DoesNotContain("missing", string.Join("\n", TuiSurfaces.Status(100, unknown).Select(TuiText.Strip)), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_WithoutATokenTallyShowsADash()
    {
        var text = string.Join("\n", TuiSurfaces.Status(100, Snapshot() with { TotalTokens = null }).Select(TuiText.Strip));

        Assert.Contains("—", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    public void Status_FramesItselfAtEveryWidth(int width)
    {
        Assert.All(TuiSurfaces.Status(width, Snapshot()), line => Assert.Equal(width, TuiText.VisibleWidth(line)));
    }
}

public sealed class TuiSurfacesPickerTests
{
    [Fact]
    public void BackendChoices_PutsTheCurrentBackendFirst()
    {
        var items = TuiSurfaces.BackendChoices("Anthropic");

        Assert.Equal("anthropic", items[0].Value);
        Assert.Contains("current", items[0].Detail, StringComparison.Ordinal);
        Assert.Equal(7, items.Count);
    }

    [Fact]
    public void BackendChoices_WithAnUnknownBackend_KeepsTheDefaultOrder()
    {
        var items = TuiSurfaces.BackendChoices("nonsense");

        Assert.Equal("openai", items[0].Value);
        Assert.DoesNotContain(items, i => i.Detail.Contains("current", StringComparison.Ordinal));
    }

    [Fact]
    public void ModelChoices_SkipsEmptyAndDuplicateModels()
    {
        var items = TuiSurfaces.ModelChoices("OpenAI", [
            ("OpenAI", "gpt-4o"),
            ("Anthropic", "  "),
            ("Custom OpenAI", "gpt-4o"),
            ("Anthropic", "claude-sonnet-4-5"),
        ]);

        Assert.Equal(2, items.Count);
        Assert.Equal("gpt-4o", items[0].Value);
        Assert.Contains("current", items[0].Detail, StringComparison.Ordinal);
        Assert.Equal("claude-sonnet-4-5", items[1].Value);
    }

    [Fact]
    public void ModelChoices_WithNothingConfigured_IsEmpty()
    {
        var items = TuiSurfaces.ModelChoices("OpenAI", [("OpenAI", null), ("Custom OpenAI", "")]);

        Assert.Empty(items);
    }

    [Fact]
    public void BackendChoices_ListsCustomAndModelsDevBackends()
    {
        var items = TuiSurfaces.BackendChoices("Custom OpenAI");

        Assert.Equal("custom", items[0].Value);
        Assert.Contains(items, i => i.Value == "models.dev:opencode");
        Assert.Contains(items, i => i.Value == "models.dev:kilo");
        Assert.Contains(items, i => i.Value == "models.dev:openrouter");
        Assert.Contains(items, i => i.Value == "models.dev:cloudflare-workers-ai");
        Assert.Contains("current", items[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendChoices_MarksProvidersWithoutACredential()
    {
        var items = TuiSurfaces.BackendChoices("OpenAI", new Dictionary<string, bool>
        {
            ["openai"] = false,
            ["anthropic"] = true,
        });

        var openAi = Assert.Single(items, i => i.Value == "openai");
        Assert.Contains("current", openAi.Detail, StringComparison.Ordinal);
        Assert.Contains("no key", openAi.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("no key", Assert.Single(items, i => i.Value == "anthropic").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelCatalogChoices_OrdersCurrentFreeSupportedUnsupported()
    {
        var items = TuiSurfaces.ModelCatalogChoices(
            "models.dev:openrouter",
            "b-model",
            [("b-model", true), ("a-model", true), ("free-model:free", true), ("z-model", false), ("b-model", true)],
            new Dictionary<string, bool> { ["models.dev:openrouter"] = true });

        Assert.Equal(4, items.Count);
        Assert.Equal("b-model", items[0].Value);
        Assert.Contains("current", items[0].Detail, StringComparison.Ordinal);
        Assert.Equal("free-model:free", items[1].Value);
        Assert.Contains("free", items[1].Detail, StringComparison.Ordinal);
        Assert.True(items[1].Enabled);
        Assert.Equal("a-model", items[2].Value);
        Assert.True(items[2].Enabled);
        Assert.Equal("z-model", items[3].Value);
        Assert.False(items[3].Enabled);
        Assert.Contains("unsupported", items[3].Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x:free", true)]
    [InlineData("x:FREE", true)]
    [InlineData("x-model", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsFreeModel_DetectsTheFreeSuffix(string? id, bool expected)
    {
        Assert.Equal(expected, TuiSurfaces.IsFreeModel(id));
    }

    [Fact]
    public void MiddleTruncate_KeepsBothEndsOfLongIds()
    {
        var shortened = TuiText.MiddleTruncate("aion-labs/apertus-8b-instruct-2509:free", 24);

        Assert.Equal(24, TuiText.VisibleWidth(shortened));
        Assert.StartsWith("aion-labs", shortened, StringComparison.Ordinal);
        Assert.EndsWith(":free", shortened, StringComparison.Ordinal);
        Assert.Contains("…", shortened, StringComparison.Ordinal);
        Assert.Equal("short", TuiText.MiddleTruncate("short", 24));
    }

    [Fact]
    public void ModelCatalogChoices_MarksMissingCredentialAndEmptyCatalog()
    {
        var items = TuiSurfaces.ModelCatalogChoices(
            "models.dev:openrouter",
            null,
            [("a-model", true)],
            new Dictionary<string, bool> { ["models.dev:openrouter"] = false });

        Assert.Single(items);
        Assert.Contains("no key", items[0].Detail, StringComparison.Ordinal);

        var currentOnly = TuiSurfaces.ModelCatalogChoices("OpenAI", "gpt-4o", []);
        Assert.Single(currentOnly);
        Assert.Equal("gpt-4o", currentOnly[0].Value);
    }

    [Fact]
    public void ModelChoices_MarksModelsOfProvidersWithoutACredential()
    {
        var items = TuiSurfaces.ModelChoices("OpenAI", [
            ("OpenAI", "gpt-4o"),
            ("Anthropic", "claude-sonnet-4-5"),
        ], new Dictionary<string, bool> { ["openai"] = true, ["anthropic"] = false });

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain("no key", items[0].Detail, StringComparison.Ordinal);
        Assert.Contains("no key", items[1].Detail, StringComparison.Ordinal);
        Assert.Equal("claude-sonnet-4-5", items[1].Value);
    }

    [Fact]
    public void BackendChoices_OrdersBaseThenCurrentThenSignedInThenTheRest()
    {
        var items = TuiSurfaces.BackendChoices(
            "models.dev:mid",
            new Dictionary<string, bool> { ["models.dev:mid"] = true, ["models.dev:aaa"] = true },
            [("mid", "Mid"), ("zzz", "Zzz"), ("aaa", "Aaa"), ("mmm", "Mmm")]);

        Assert.Equal(
            ["openai", "anthropic", "custom", "models.dev:mid", "models.dev:aaa", "models.dev:mmm", "models.dev:zzz"],
            items.Select(i => i.Value));
        Assert.Contains("current", items[3].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(items.Take(3), i => i.Detail.Contains("no key", StringComparison.Ordinal));
    }

    [Fact]
    public void BackendChoices_ListsEveryLiveProviderExactlyOnceAndKeepsTheBaseRows()
    {
        var items = TuiSurfaces.BackendChoices(
            "openai",
            null,
            [("groq", "Groq"), ("openrouter", "OpenRouter"), ("groq", "Groq again")]);

        var values = items.Select(i => i.Value).ToList();
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["openai", "anthropic", "custom"], values.Take(3));
        Assert.Contains("models.dev:groq", values);
        Assert.Contains("models.dev:openrouter", values);
        Assert.Equal(5, values.Count);
    }

    [Fact]
    public void BackendChoices_FallsBackToTheKnownProvidersWhenTheCatalogIsMissing()
    {
        var items = TuiSurfaces.BackendChoices("openai", null, []);

        Assert.Equal(7, items.Count);
        Assert.Contains(items, i => i.Value == "models.dev:openrouter");
        Assert.Contains(items, i => i.Value == "models.dev:kilo");
    }

    [Fact]
    public void ReasoningChoices_LeadsWithDefaultThenTheRealLevels()
    {
        var items = TuiSurfaces.ReasoningChoices("high", ["low", "high", "low", "  "]);

        Assert.Equal(["default", "low", "high"], items.Select(i => i.Value));
        Assert.Equal("current", items[2].Detail);
        Assert.Equal(string.Empty, items[0].Detail);
    }

    [Fact]
    public void ReasoningChoices_WithoutLevelsIsEmptySoTheCallerExplains()
    {
        Assert.Empty(TuiSurfaces.ReasoningChoices("default", []));
        Assert.Empty(TuiSurfaces.ReasoningChoices("default", ["default"]));
    }

    [Fact]
    public void ReasoningChoices_TreatsAnUnsetEffortAsDefault()
    {
        var items = TuiSurfaces.ReasoningChoices(null, ["enabled", "disabled"]);

        Assert.Equal(["default", "enabled", "disabled"], items.Select(i => i.Value));
        Assert.Equal("current", items[0].Detail);
    }
}

public sealed class ChoiceListTests
{
    private static ChoiceList Create() => new("model", [
        new ChoiceItem("a", "first", "a"),
        new ChoiceItem("b", "second", "b"),
        new ChoiceItem("c", "third", "c"),
    ]);

    [Fact]
    public void Constructor_RejectsAnEmptyList()
    {
        Assert.Throws<ArgumentException>(() => new ChoiceList("x", []));
    }

    [Fact]
    public void Move_WrapsInBothDirections()
    {
        var list = Create();

        list.Move(-1);
        Assert.Equal("c", list.Current.Value);

        list.Move(1);
        Assert.Equal("a", list.Current.Value);

        list.Move(2);
        Assert.Equal("c", list.Current.Value);
    }

    [Theory]
    [InlineData('1', "a")]
    [InlineData('3', "c")]
    public void HandleKey_DigitBothMovesAndConfirms(char digit, string expected)
    {
        var list = Create();

        var outcome = list.HandleKey(new ConsoleKeyInfo(digit, ConsoleKey.D1, false, false, false));

        Assert.Equal(ChoiceOutcome.Confirmed, outcome);
        Assert.Equal(expected, list.Current.Value);
    }

    [Fact]
    public void HandleKey_OutOfRangeDigitIsIgnored()
    {
        var list = Create();
        list.Selected = 1;

        var outcome = list.HandleKey(new ConsoleKeyInfo('9', ConsoleKey.D9, false, false, false));

        Assert.Equal(ChoiceOutcome.None, outcome);
        Assert.Equal("b", list.Current.Value);
    }

    [Fact]
    public void HandleKey_ArrowsMoveAndEscapeCancels()
    {
        var list = Create();

        Assert.Equal(ChoiceOutcome.Moved, list.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false)));
        Assert.Equal("b", list.Current.Value);

        Assert.Equal(ChoiceOutcome.Moved, list.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false)));
        Assert.Equal("a", list.Current.Value);

        Assert.Equal(ChoiceOutcome.Cancelled, list.HandleKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)));
    }

    [Fact]
    public void HandleKey_EnterConfirmsTheHighlightedRow()
    {
        var list = Create();
        list.Selected = 2;

        var outcome = list.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        Assert.Equal(ChoiceOutcome.Confirmed, outcome);
        Assert.Equal("c", list.Current.Value);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(100)]
    public void Render_FramesTheListAtEveryWidth(int width)
    {
        var lines = Create().Render(width);

        Assert.Equal(5, lines.Count);
        Assert.All(lines, line => Assert.Equal(width, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void Render_MarksTheSelectedRow()
    {
        var list = Create();
        list.Selected = 1;

        var rows = list.Render(60).Select(TuiText.Strip).ToList();

        Assert.Contains("❯", rows.First(r => r.Contains("b", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain("❯", rows.First(r => r.Contains("  a", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    private static ChoiceList Providers() => new("backend", [
        new ChoiceItem("models.dev:groq", "Models.dev: Groq", "models.dev:groq"),
        new ChoiceItem("models.dev:openrouter", "Models.dev: OpenRouter", "models.dev:openrouter"),
        new ChoiceItem("models.dev:llama", "Models.dev: Llama Network", "models.dev:llama"),
    ]);

    [Fact]
    public void Filter_NarrowsBySubstringAcrossLabelDetailAndValue()
    {
        var list = Providers();

        foreach (var key in "groq")
        {
            Assert.True(list.TryAppendFilter(key));
        }

        Assert.Equal("groq", list.Filter);
        Assert.Equal(1, list.MatchedCount);
        Assert.Equal(3, list.TotalCount);
        Assert.Equal("models.dev:groq", list.Current.Value);
    }

    [Fact]
    public void Filter_MatchesTheDisplayNameWhenNeitherIdNorLabelCarriesTheQuery()
    {
        var list = Providers();

        foreach (var key in "network")
        {
            Assert.True(list.TryAppendFilter(key));
        }

        Assert.Equal("models.dev:llama", Assert.Single(list.Items).Value);
    }

    [Fact]
    public void Filter_IsCaseInsensitiveAndCoversTheDetail()
    {
        var list = Providers();

        foreach (var key in "NETWORK")
        {
            Assert.True(list.TryAppendFilter(key));
        }

        Assert.Equal("models.dev:llama", Assert.Single(list.Items).Value);
    }

    [Fact]
    public void Filter_LeavesDigitsToTheNumberedPickShortcut()
    {
        var list = Providers();

        Assert.False(list.TryAppendFilter('3'));
        Assert.Equal(string.Empty, list.Filter);
        Assert.Equal(3, list.Items.Count);
        Assert.True(list.TrySelectDigit('3'));
        Assert.Equal("models.dev:llama", list.Current.Value);
    }

    [Fact]
    public void Filter_RefusesControlCharactersAndSpaces()
    {
        var list = Providers();

        Assert.False(list.TryAppendFilter(' '));
        Assert.False(list.TryAppendFilter('\t'));
        Assert.False(list.TryAppendFilter('!'));
        Assert.Equal(string.Empty, list.Filter);
    }

    [Fact]
    public void Filter_AcceptsTheIdSeparators()
    {
        var list = Providers();

        Assert.True(list.TryAppendFilter('-'));
        Assert.True(list.TryAppendFilter('_'));
        Assert.True(list.TryAppendFilter('.'));
        Assert.Equal("-_.", list.Filter);
    }

    [Fact]
    public void TrimFilter_RestoresTheWholeList()
    {
        var list = Providers();
        list.TryAppendFilter('o');
        list.TryAppendFilter('g');

        Assert.True(list.TryTrimFilter());
        Assert.True(list.TryTrimFilter());
        Assert.Equal(string.Empty, list.Filter);
        Assert.Equal(3, list.MatchedCount);
        Assert.Equal(3, list.Items.Count);
        Assert.False(list.TryTrimFilter());
    }

    [Fact]
    public void Filter_WithoutAMatchShowsOneDisabledRowThatCannotBePicked()
    {
        var list = Providers();
        list.TryAppendFilter('z');
        list.TryAppendFilter('z');
        list.TryAppendFilter('z');

        Assert.Equal(0, list.MatchedCount);
        var only = Assert.Single(list.Items);
        Assert.False(only.Enabled);
        Assert.Contains("no match", only.Label, StringComparison.Ordinal);
        Assert.Equal(ChoiceOutcome.None, list.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)));
    }

    [Fact]
    public void Filter_KeepsTheCursorOnAnEnabledRowWhenTheListShrinks()
    {
        var list = Providers();
        list.Selected = 2;
        Assert.Equal("models.dev:llama", list.Current.Value);

        foreach (var key in "groq")
        {
            list.TryAppendFilter(key);
        }

        Assert.True(list.Current.Enabled);
        Assert.Equal("models.dev:groq", list.Current.Value);
    }

    [Fact]
    public void Filter_NeverLetsTheCursorFallOffTheEnd()
    {
        var list = Providers();
        list.Selected = 2;
        list.TryAppendFilter('o');

        list.Move(5);
        Assert.InRange(list.Selected, 0, list.Items.Count - 1);
        Assert.Equal(list.Current, list.Items[list.Selected]);
    }

    [Fact]
    public void Render_ReportsTheMatchRatioOnlyWhileFiltering()
    {
        var unfiltered = Providers();
        Assert.DoesNotContain(unfiltered.Render(70).Select(TuiText.Strip), r => r.Contains("filter:", StringComparison.Ordinal));

        var list = Providers();
        list.TryAppendFilter('g');
        var footer = list.Render(70).Select(TuiText.Strip).First(r => r.Contains("filter:", StringComparison.Ordinal));

        Assert.Contains("filter: g", footer, StringComparison.Ordinal);
        Assert.Contains("1/3", footer, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NamesTheFooterAndTheRowCountUntilFilteringTakesOver()
    {
        var list = new ChoiceList("model", [
            new ChoiceItem("m-1", "one", "m-1"),
            new ChoiceItem("m-2", "two", "m-2"),
            new ChoiceItem("m-3", "three", "m-3"),
        ], footer: "openai");

        var unfiltered = list.Render(70).Select(TuiText.Strip).First(r => r.Contains("openai", StringComparison.Ordinal));
        Assert.Contains("openai · 3 rows", unfiltered, StringComparison.Ordinal);

        list.TryAppendFilter('m');
        var filtered = list.Render(70).Select(TuiText.Strip).First(r => r.Contains("filter:", StringComparison.Ordinal));
        Assert.Contains("filter: m", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("rows", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_IsCappedSoALongTypingRunCannotGrowTheQueryForever()
    {
        var list = Providers();

        for (var i = 0; i < 100; i++)
        {
            list.TryAppendFilter('g');
        }

        Assert.Equal(64, list.Filter.Length);
    }
}
