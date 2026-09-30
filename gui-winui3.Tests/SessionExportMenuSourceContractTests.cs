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
        var body = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "OnExportSessionMenuClick");

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

        var body = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "OnImportSessionMenuClick");
        Assert.Contains("HarnessFromFileName", body, StringComparison.Ordinal);
        Assert.Contains("ImportSessionAsync", body, StringComparison.Ordinal);

        // An unknown name opens a dialog naming what Ru does recognise, rather than picking
        // a format that would then refuse the file.
        Assert.Contains("Cannot tell which agent wrote this file", body, StringComparison.Ordinal);

        // What could not be stored is reported, not swallowed.
        Assert.Contains("DroppedEntries", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The picker offers every format the host reads, and the size cap runs before the
    /// file is pulled into memory — an oversized import has to be refused with a bounded
    /// dialog, not a generic failure after the read.
    /// </summary>
    [Fact]
    public void The_import_picker_reads_the_hosts_formats_and_caps_the_file_before_reading()
    {
        var body = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "OnImportSessionMenuClick");

        Assert.Contains("FileTypeFilter.Add(\".jsonl\")", body, StringComparison.Ordinal);
        Assert.Contains("FileTypeFilter.Add(\".db\")", body, StringComparison.Ordinal);
        Assert.Contains("SessionInterchangeLimits.MaxImportContentBytes", body, StringComparison.Ordinal);
        Assert.Contains("GetBasicPropertiesAsync", body, StringComparison.Ordinal);

        var sizeCheck = body.IndexOf("GetBasicPropertiesAsync", StringComparison.Ordinal);
        var read = body.IndexOf("ReadTextAsync", StringComparison.Ordinal);
        Assert.True(sizeCheck >= 0 && read > sizeCheck, "the size check must precede the read");
    }

    /// <summary>
    /// The hover controls are deliberately not tab stops, so the row menu is the
    /// keyboard path to the same two actions. Without them a keyboard user cannot
    /// delete a chat or open its usage at all.
    /// </summary>
    [Fact]
    public void The_row_menu_reaches_the_two_hover_actions()
    {
        var document = GuiSource.Xml("MainWindow.xaml");

        var usage = GuiSource.Require(document, "SessionUsageItem");
        Assert.Equal("OnSessionUsageClick", GuiSource.AttributeValue(usage, "Click"));
        Assert.Equal("{x:Bind Id}", GuiSource.AttributeValue(usage, "Tag"));

        var delete = GuiSource.Require(document, "DeleteSessionItem");
        Assert.Equal("OnDeleteSessionClick", GuiSource.AttributeValue(delete, "Click"));
        Assert.Equal("{x:Bind Id}", GuiSource.AttributeValue(delete, "Tag"));

        // Both sit in the same context menu as the export/import items, so
        // right-clicking a row offers what the hover controls carry.
        var menu = GuiSource.Require(document, "ExportCodexItem").Parent!;
        Assert.True(GuiSource.IsDescendantOf(usage, menu));
        Assert.True(GuiSource.IsDescendantOf(delete, menu));

        // The menu items name handlers on the window; a missing one silently leaves
        // the action unreachable.
        string code = GuiSource.ViewText("MainWindow");
        Assert.True(GuiSource.HasMember(code, "OnSessionUsageClick"));
        Assert.True(GuiSource.HasMember(code, "OnDeleteSessionClick"));
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
