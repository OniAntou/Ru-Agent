using Xunit;

namespace RuApp.Tests;

public sealed class SessionExportMenuSourceContractTests
{
    /// <summary>
    /// Both formats are offered from the same row menu, and each item names itself: a
    /// single "Export" item would leave the reader to guess which agent it meant.
    /// </summary>
    [Fact]
    public void The_row_menu_offers_both_agents_by_name()
    {
        var document = GuiSource.Xml("MainWindow.xaml");

        foreach (var name in new[] { "ExportClaudeCodeItem", "ExportCodexItem" })
        {
            var item = GuiSource.Require(document, name);
            Assert.Equal("OnExportSessionMenuClick", GuiSource.AttributeValue(item, "Click"));
            Assert.Equal("{x:Bind Id}", GuiSource.AttributeValue(item, "Tag"));
        }
    }

    [Fact]
    public void The_handler_reads_the_format_off_the_item_that_was_clicked()
    {
        var body = GuiSource.MethodBody(GuiSource.Text("MainWindow.xaml.cs"), "OnExportSessionMenuClick");

        Assert.Contains("ExportCodexItem", body, StringComparison.Ordinal);
        Assert.Contains("\"codex\"", body, StringComparison.Ordinal);
        Assert.Contains("ExportSessionAsync", body, StringComparison.Ordinal);

        // The outcome is reported either way: an export that wrote nothing has to say so,
        // or a click that silently did nothing looks like it worked.
        Assert.Contains("outcome.Ok", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Import sits beside export in the same row menu, and the file — not the handler —
    /// decides which agent wrote it.
    /// </summary>
    [Fact]
    public void The_row_menu_also_offers_import_and_asks_instead_of_guessing()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        var item = GuiSource.Require(document, "ImportSessionItem");
        Assert.Equal("OnImportSessionMenuClick", GuiSource.AttributeValue(item, "Click"));

        var body = GuiSource.MethodBody(GuiSource.Text("MainWindow.xaml.cs"), "OnImportSessionMenuClick");
        Assert.Contains("HarnessFromFileName", body, StringComparison.Ordinal);
        Assert.Contains("ImportSessionAsync", body, StringComparison.Ordinal);

        // An unknown name opens a dialog naming what Ru does recognise, rather than picking
        // a format that would then refuse the file.
        Assert.Contains("Cannot tell which agent wrote this file", body, StringComparison.Ordinal);

        // What could not be stored is reported, not swallowed.
        Assert.Contains("DroppedEntries", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The GUI writes where the shared layout says, not where it guesses: the same pure
    /// resolver the CLI uses places the files, so the two surfaces cannot drift into
    /// writing one session to two different directories.
    /// </summary>
    [Fact]
    public void The_gui_places_files_with_the_shared_layout()
    {
        Assert.Contains(
            "SessionFileLayout.TargetsFor",
            GuiSource.Text("Services", "ApiService.SessionInterchange.cs"),
            StringComparison.Ordinal);
    }
}
