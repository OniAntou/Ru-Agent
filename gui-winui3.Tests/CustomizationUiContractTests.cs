using System;
using System.Linq;
using Xunit;

namespace RuApp.Tests;

/// <summary>The instruction editors and the notification settings, as wired in the views.</summary>
public sealed class CustomizationUiContractTests
{
    [Fact]
    public void Customize_has_an_instructions_tab_with_two_editors()
    {
        var document = GuiSource.Xml("Views", "CustomizeView.xaml");
        Assert.Equal("instructions", GuiSource.AttributeValue(GuiSource.Require(document, "InstructionsTab"), "Tag"));
        var panel = GuiSource.Require(document, "InstructionsPanel");
        foreach (var name in new[] { "GlobalInstructionsBox", "WorkspaceInstructionsBox", "GlobalInstructionsSave", "WorkspaceInstructionsSave" })
            Assert.Contains(GuiSource.Require(document, name), panel.Descendants());
        Assert.Contains("never permission", panel.ToString(), StringComparison.Ordinal);

        var code = GuiSource.Text("Views", "CustomizeView.Instructions.cs");
        // Overwriting an outside edit asks first, through the app's one dialog gate.
        Assert.Contains("ContentDialogGate.ShowAsync", GuiSource.MethodBody(code, "ConfirmOverwriteAsync"), StringComparison.Ordinal);
        Assert.Contains("InstructionFileStore.Save(parts.Snapshot, parts.Box.Text)", GuiSource.MethodBody(code, "SaveInstructions"), StringComparison.Ordinal);
        // Save and Discard start disabled and enable only for unsaved changes.
        foreach (var name in new[] { "GlobalInstructionsSave", "GlobalInstructionsRevert", "WorkspaceInstructionsSave", "WorkspaceInstructionsRevert" })
            Assert.Equal("False", GuiSource.AttributeValue(GuiSource.Require(document, name), "IsEnabled"));
        Assert.Contains("readable && changed", GuiSource.MethodBody(code, "UpdateInstructionsState"), StringComparison.Ordinal);
        GuiSource.Require(document, "WorkspaceInstructionsEmpty");
    }

    [Fact]
    public void The_plugins_tab_lists_user_plugins_above_the_built_in_ones()
    {
        var document = GuiSource.Xml("Views", "CustomizeView.xaml");
        var panel = GuiSource.Require(document, "PluginsPanel");
        var list = GuiSource.Require(document, "PluginsList");
        var computerUse = GuiSource.Require(document, "ComputerUseToggle");
        var order = panel.Descendants().ToList();
        Assert.True(order.IndexOf(list) < order.IndexOf(computerUse));
        Assert.Equal("OnNewPluginClick", GuiSource.AttributeValue(GuiSource.Require(document, "NewPluginButton"), "Click"));
        GuiSource.Require(document, "PluginsEmpty");

        var code = GuiSource.Text("Views", "CustomizeView.UserPlugins.cs");
        Assert.Contains("ContentDialogGate.ShowAsync", GuiSource.MethodBody(code, "OnNewPluginClick"), StringComparison.Ordinal);
        Assert.Contains("\"set_enabled\"", GuiSource.MethodBody(code, "OnPluginToggled"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("My team rules", "my-team-rules")]
    [InlineData("Quy tắc Đội", "quy-tac-doi")]
    [InlineData("  2024 Release_Notes ", "release-notes")]
    [InlineData("A very long plugin name that keeps going", "a-very-long-plugin-name")]
    [InlineData("!!!", "")]
    public void New_plugin_suggests_a_valid_id_from_the_name(string name, string expected)
    {
        var id = RuApp.Views.CustomizeView.SuggestPluginId(name);
        Assert.Equal(expected, id);
        if (id.Length > 0) Assert.True(RuAgent.Protocol.UserPlugins.IsValidId(id));
    }

    [Fact]
    public void Settings_offers_each_notification_choice()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        foreach (var name in new[] { "NotifyFinishedToggle", "NotifyAttentionToggle", "NotifyFlashToggle", "NotifySoundToggle", "NotifyToastToggle" })
            Assert.Equal("OnNotificationToggled", GuiSource.AttributeValue(GuiSource.Require(document, name), "Toggled"));
    }

    [Fact]
    public void Notifications_only_reach_the_user_while_ru_is_in_the_background()
    {
        var code = GuiSource.Text("MainWindow.Notifications.cs");
        var deliver = GuiSource.MethodBody(code, "Deliver");
        Assert.Contains("IsRuInForeground()", deliver, StringComparison.Ordinal);
        // A toast plays its own sound; the beep is only the fallback.
        Assert.Contains("!toastShown", deliver, StringComparison.Ordinal);
        // Windows notifications register only while the user has them on.
        Assert.Contains("Unregister()", GuiSource.MethodBody(code, "ApplyToastRegistration"), StringComparison.Ordinal);

        var window = GuiSource.Text("MainWindow.xaml.cs");
        Assert.Equal(2, window.Split("AttachNotifications(_agentController);").Length - 1);
        Assert.Contains("ShutdownNotifications();", GuiSource.MethodBody(window, "OnWindowClosed"), StringComparison.Ordinal);
    }
}
