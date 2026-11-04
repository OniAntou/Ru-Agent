using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class RetiredSessionExchangeUiTests
{
    [Fact]
    public void Session_menu_has_no_external_exchange_actions_and_keeps_native_usage()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        foreach (var name in new[] { "ExportClaudeCodeItem", "ExportCodexItem", "ImportSessionItem" })
            Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(GuiSource.Xaml + "Name") == name);
        GuiSource.Require(document, "SessionUsageItem");
        var code = GuiSource.ViewText("MainWindow");
        Assert.DoesNotContain("OnExportSessionMenuClick", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnImportSessionMenuClick", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Gui_api_and_binary_no_longer_expose_external_session_exchange()
    {
        Assert.Null(typeof(ApiService).GetMethod("ImportSessionAsync"));
        Assert.Null(typeof(ApiService).GetMethod("ExportSessionAsync"));
        Assert.DoesNotContain(typeof(ApiService).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "RuAgent.SessionInterchange");
    }
}
