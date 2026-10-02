using Terminal.Gui.App;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Reads the driver's cell buffer as plain text rows. This is the Terminal.Gui analogue
/// of <c>TuiScreenSimulator</c>: tests assert what the operator sees without a terminal.
/// </summary>
public static class TgSnapshot
{
    public static string[] Capture(IApplication app)
    {
        var driver = app.Driver ?? throw new InvalidOperationException("driver not initialised");
        var contents = driver.Contents ?? throw new InvalidOperationException("cell buffer not initialised");
        var rows = new string[driver.Rows];
        for (var r = 0; r < driver.Rows; r++)
        {
            var line = new System.Text.StringBuilder(driver.Cols);
            for (var c = 0; c < driver.Cols; c++)
            {
                line.Append(contents[r, c].Grapheme ?? " ");
            }

            rows[r] = line.ToString().TrimEnd();
        }

        return rows;
    }

    public static Attr CellAttribute(IApplication app, int row, int col)
    {
        var driver = app.Driver ?? throw new InvalidOperationException("driver not initialised");
        var contents = driver.Contents ?? throw new InvalidOperationException("cell buffer not initialised");
        return contents[row, col].Attribute ?? Attr.Default;
    }
}
