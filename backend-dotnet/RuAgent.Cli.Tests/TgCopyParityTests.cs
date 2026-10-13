using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// Copy parity between the classic builders and the Terminal.Gui surfaces: a TG view must
/// reproduce the classic row cell for cell at the same width. The TG side needs no ANSI
/// strip -- <see cref="TgSnapshot"/> reads graphemes out of the driver cell buffer -- so
/// only the classic row is stripped and trimmed.
/// </summary>
public sealed class TgCopyParityTests
{
    private const int Width = 100;
    private const int Rows = 30;
    private const string Preset = "Standard";
    private const string Model = "stealth/space-bunny-alpha";
    private const string Provider = "models.dev:kilo";

    private static string[] Render(params View[] views)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        window.Add(views);
        string[]? captured = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            app.Driver!.SetScreenSize(Width, Rows);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            captured = TgSnapshot.Capture(app);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        return captured ?? throw new InvalidOperationException("snapshot capture did not run");
    }

    /// <summary>Classic rows carry ANSI styling; the cell buffer does not.</summary>
    private static string Visible(string styledRow) => TuiText.Strip(styledRow).TrimEnd();

    [Fact]
    public void Composer_PreservesInputAndMetadataInTheNewFilledLayout()
    {
        var typed = new TgComposerView { X = 0, Y = 0, Width = Dim.Fill(), Height = 5 };
        typed.SetMeta(Preset, Model, Provider);
        typed.Input.Text = "fix the busy lock";

        var ghost = new TgComposerView { X = 0, Y = 5, Width = Dim.Fill(), Height = 5 };
        ghost.SetMeta(Preset, Model, Provider);
        ghost.SetPlaceholder("Ask anything…");

        var rows = Render(typed, ghost);

        Assert.Contains("fix the busy lock", rows[1], StringComparison.Ordinal);
        Assert.Contains("Standard", rows[3], StringComparison.Ordinal);
        Assert.Contains(Model, rows[3], StringComparison.Ordinal);
        Assert.Contains("kilo", rows[3], StringComparison.Ordinal);
        Assert.Contains("Ask anything…", rows[6], StringComparison.Ordinal);
        Assert.Contains("Standard", rows[8], StringComparison.Ordinal);
        Assert.Contains("kilo", rows[8], StringComparison.Ordinal);
        Assert.DoesNotContain("models.dev", rows[3], StringComparison.Ordinal);

    }

    [Fact]
    public void Hints_ReproduceTheClassicIdleAndBusyRows()
    {
        var idle = new TgHintsView { X = 0, Y = 0, Width = Dim.Fill() };
        idle.SetIdle("D:/LLM");
        var busy = new TgHintsView { X = 0, Y = 1, Width = Dim.Fill() };
        busy.SetBusy();

        var rows = Render(idle, busy);

        Assert.Equal(Visible(ComposerBox.ComposeHints(Width, busy: false, "D:/LLM")), rows[0]);
        Assert.Equal(Visible(ComposerBox.ComposeHints(Width, busy: true, "D:/LLM")), rows[1]);
        Assert.Contains("/ commands  tab complete  ctrl+o expand", rows[0], StringComparison.Ordinal);
        Assert.Contains("━━━━━ esc interrupt", rows[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Header_ReproducesTheClassicTitleAndUsageRow()
    {
        var classic = Visible(TuiHeader.Compose(Width, "Fix the busy lock", 39413, 20, 0.29));
        var header = new TgHeaderView { X = 0, Y = 0, Width = Dim.Fill() };
        header.SetTitle("Fix the busy lock");
        header.UpdateUsage(39413, 20, 0.29m);

        var row = Render(header)[0];

        Assert.Equal(classic, row);
        Assert.Contains("# Fix the busy lock", row, StringComparison.Ordinal);
        Assert.Contains(
            "39,413" + new string(' ', 2 + TuiUsageMeter.BarWidth + 1) + "20.00%  ($0.29)",
            row,
            StringComparison.Ordinal);
    }
}
