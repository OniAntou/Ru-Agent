using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiCardTests
{
    private static IReadOnlyList<CardItem> Items(int count) =>
        Enumerable.Range(1, count).Select(i => new CardItem($"model {i:D2}", "Free")).ToList();

    [Fact]
    public void Compose_PadsEveryRowToTheCardWidth()
    {
        var lines = TuiCard.ComposeList(60, "model", "esc", Items(3), 0, 8, "footer");

        Assert.All(lines, line => Assert.Equal(60, line.Width));
        Assert.All(lines, line => Assert.Equal(60, TuiText.VisibleWidth(line.Text)));
    }

    [Fact]
    public void Compose_OpensWithTheTitleAndItsHintOnOneRow()
    {
        var lines = TuiCard.ComposeList(60, "model", "esc", Items(3), 0, 8, null)
            .Select(line => line.Text)
            .ToList();

        Assert.Equal(CardRowKind.Spacer, Kind(0));
        var title = lines[1];
        Assert.StartsWith("  model", title, StringComparison.Ordinal);
        Assert.EndsWith("esc", title.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_MarksTheChosenEntryWithABullet()
    {
        var lines = TuiCard.ComposeList(60, "model", "esc", Items(3), 1, 8, null);

        Assert.Equal(CardRowKind.Selected, lines.Single(l => l.Kind == CardRowKind.Selected).Kind);
        Assert.Contains(lines, l => l.Text.Contains("● model 02", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(l => l.Kind == CardRowKind.Item));
    }

    [Fact]
    public void Compose_RightsAlignsTheTrailingText()
    {
        var line = TuiCard
            .ComposeList(60, "model", "esc", new[] { new CardItem("gpt", "Free") }, 0, 8, null)
            .Single(l => l.Kind == CardRowKind.Selected);

        var text = TuiText.Strip(line.Text);
        Assert.EndsWith("Free", text.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal(60, TuiText.VisibleWidth(text));
    }

    [Fact]
    public void Compose_AddsAGroupHeadingOnlyWhenTheGroupChanges()
    {
        var items = new[]
        {
            new CardItem("a", Group: "Recent"),
            new CardItem("b", Group: "Recent"),
            new CardItem("c", Group: "OpenCode Go"),
        };

        var lines = TuiCard.ComposeList(60, "model", "esc", items, 0, 8, null);
        var sections = lines.Where(l => l.Kind == CardRowKind.Section).Select(l => l.Text.Trim()).ToList();

        Assert.Equal(["Recent", "OpenCode Go"], sections);
    }

    [Fact]
    public void Compose_KeepsTheChosenEntryOnScreen()
    {
        var lines = TuiCard.ComposeList(60, "model", "esc", Items(30), 20, 8, null);
        var visible = lines.Where(l => l.Kind is CardRowKind.Item or CardRowKind.Selected).ToList();

        Assert.True(visible.Count <= 9, $"showed {visible.Count} entries");
        Assert.Contains(visible, l => l.Kind == CardRowKind.Selected && l.Text.Contains("model 21", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_SaysWhenNothingMatches()
    {
        var lines = TuiCard.ComposeList(60, "model", "esc", [], 0, 8, null);

        Assert.Contains(lines, l => l.Kind == CardRowKind.Disabled && l.Text.Contains("no matches", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_ShowsTheSearchFieldOnlyWhenAskedFor()
    {
        Assert.DoesNotContain(
            TuiCard.ComposeList(60, "model", "esc", Items(2), 0, 8, null),
            l => l.Kind == CardRowKind.Search);
        Assert.Contains(
            TuiCard.ComposeList(60, "model", "esc", Items(2), 0, 8, null, "Search"),
            l => l.Kind == CardRowKind.Search);
    }

    [Fact]
    public void Width_StaysCompactAndNeverBelowTheMinimum()
    {
        Assert.Equal(TuiCard.MinWidth, TuiCard.ComputeWidth(4));
        Assert.Equal(TuiCard.MaxWidth, TuiCard.ComputeWidth(400));
        Assert.Equal(72, TuiCard.ComputeWidth(80));
    }

    [Fact]
    public void Compose_DrawsTheHeaderRuleUnderTheTitle()
    {
        var lines = TuiCard.ComposeList(60, "model", "esc", Items(2), 0, 8, null);

        Assert.Equal(CardRowKind.Title, lines[1].Kind);
        Assert.Equal(CardRowKind.Rule, lines[2].Kind);
        Assert.Equal(new string('─', 60), lines[2].Text);
    }

    [Fact]
    public void Compose_ShowsTheScrollPositionOnlyWhenTheListIsClipped()
    {
        var clipped = TuiCard
            .ComposeList(60, "model", "esc", Items(30), 20, 8, "↑↓ select")
            .Single(line => line.Kind == CardRowKind.Footer);
        var short_ = TuiCard
            .ComposeList(60, "model", "esc", Items(3), 1, 8, "↑↓ select")
            .Single(line => line.Kind == CardRowKind.Footer);

        Assert.EndsWith("21/30", TuiText.Strip(clipped.Text).TrimEnd(), StringComparison.Ordinal);
        Assert.StartsWith("↑↓ select", TuiText.Strip(clipped.Text).TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain("/3", TuiText.Strip(short_.Text), StringComparison.Ordinal);
    }

    private static CardRowKind Kind(int index) =>
        TuiCard.ComposeList(60, "model", "esc", Items(2), 0, 8, null)[index].Kind;
}

/// <summary>
/// The card has to look like a card, which is a claim about pixels and not about text: these
/// read the driver's cell buffer and check the fill behind the rows and the reversed colours
/// on the chosen entry.
/// </summary>
public sealed class TgCardTests
{
    private static readonly ChoiceItem[] Models =
    [
        new("Space Bunny Free Default / OpenCode", "Free", "a"),
        new("Muse Spark 1.3 Free Default", "Free", "b"),
        new("GPT-6 Luna", string.Empty, "c"),
    ];

    private static void WithPicker(Action<IApplication> assert)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            window.ShowPicker("model", Models);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            assert(app);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Picker_FillsTheCardInsteadOfDrawingABorder()
    {
        WithPicker(app =>
        {
            var rows = TgSnapshot.Capture(app);
            var screen = string.Join('\n', rows);
            var card = rows.Where(row => row.Contains("  model", StringComparison.Ordinal)).ToList();

            Assert.Single(card);
            Assert.Contains("Search", screen, StringComparison.Ordinal);
            Assert.DoesNotContain("╭", string.Join('\n', rows.TakeWhile(row => !row.Contains("┌", StringComparison.Ordinal))), StringComparison.Ordinal);
            Assert.DoesNotContain("│", card[0], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Picker_PaintsTheCardFillBehindEveryRow()
    {
        WithPicker(app =>
        {
            var rows = TgSnapshot.Capture(app);
            var titleRow = Array.FindIndex(rows, row => row.Contains("  model", StringComparison.Ordinal));
            Assert.True(titleRow > 0);

            var expected = TgPalette.ToColor(TgPalette.CardBgRgb());
            Assert.Equal(expected, TgSnapshot.CellAttribute(app, titleRow, 20).Background);
        });
    }

    [Fact]
    public void Picker_ReversesTheChosenEntryOntoTheHighlightBar()
    {
        WithPicker(app =>
        {
            var rows = TgSnapshot.Capture(app);
            var chosen = Array.FindIndex(rows, row => row.Contains("● Space Bunny", StringComparison.Ordinal));
            Assert.True(chosen > 0);

            var attr = TgSnapshot.CellAttribute(app, chosen, 20);
            Assert.Equal(TgPalette.ToColor(TgPalette.CardSelectionRgb()), attr.Background);
            Assert.Equal(TgPalette.ToColor(TgPalette.CardOnSelectionRgb()), attr.Foreground);

            // An entry that is not chosen keeps the card fill, so only one row is a bar.
            var other = Array.FindIndex(rows, row => row.Contains("Muse Spark", StringComparison.Ordinal));
            Assert.Equal(
                TgPalette.ToColor(TgPalette.CardBgRgb()),
                TgSnapshot.CellAttribute(app, other, 20).Background);
        });
    }

    [Fact]
    public void Picker_DrawsTheHeaderRuleInTheLineToneOnTheCardFill()
    {
        WithPicker(app =>
        {
            var rows = TgSnapshot.Capture(app);
            var titleRow = Array.FindIndex(rows, row => row.Contains("  model", StringComparison.Ordinal));
            Assert.True(titleRow > 0);
            var ruleRow = titleRow + 1;
            var fill = TgPalette.ToColor(TgPalette.CardBgRgb());
            var cardColumns = Enumerable.Range(0, app.Driver!.Cols)
                .Where(col => TgSnapshot.CellAttribute(app, titleRow, col).Background == fill).ToArray();
            Assert.NotEmpty(cardColumns);
            var left = cardColumns[0];
            var width = cardColumns.Length;
            Assert.Equal(Enumerable.Range(left, width), cardColumns);
            // The full-width sky may remain visible outside the floating card.
            Assert.Equal(new string('─', width), rows[ruleRow].Substring(left, width));
            foreach (var col in cardColumns)
            {
                var attr = TgSnapshot.CellAttribute(app, ruleRow, col);
                Assert.Equal(TgPalette.ToColor(TuiToken.Line), attr.Foreground);
                Assert.Equal(fill, attr.Background);
            }
        });
    }

    [Fact]
    public void Picker_DeleteKey_RequestsTheRowUnderTheCursorOnlyWhenAllowed()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var requested = new List<string>();
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            window.PanelDeleteRequested += value => requested.Add(value);
            window.ShowPicker("sessions", Models, allowDelete: false);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(Key.Delete);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            // A picker without the delete affordance ignores the key.
            Assert.Empty(requested);
            window.ShowPicker("sessions", Models, allowDelete: true);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            app.InjectKey(Key.Delete);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(800), () =>
        {
            Assert.Equal(["a"], requested);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void TypingInThePicker_NarrowsTheCard()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            window.ShowPicker("model", Models);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(new Terminal.Gui.Input.Key('l'));
            app.InjectKey(new Terminal.Gui.Input.Key('u'));
            app.InjectKey(new Terminal.Gui.Input.Key('n'));
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            var rows = TgSnapshot.Capture(app);
            var card = string.Join('\n', rows.Where(row => row.Trim().Length > 0));

            // The search field carries what was typed and the list is down to the one match.
            Assert.Contains("Search lun", card, StringComparison.Ordinal);
            Assert.Contains("GPT-6 Luna", card, StringComparison.Ordinal);
            Assert.DoesNotContain("Muse Spark", card, StringComparison.Ordinal);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
