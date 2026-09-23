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
        Assert.Equal(6, items.Count);
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
}
