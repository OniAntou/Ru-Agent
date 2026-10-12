using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp;

public sealed partial class MainWindow
{
    private sealed record SidebarMoveCommand(string SessionId, string GroupKey, string? GroupPath, string? BeforeId = null);

    private void OnSessionMenuOpening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        foreach (var subMenu in menu.Items.OfType<MenuFlyoutSubItem>())
        {
            if (subMenu.Tag is not string id || !Sessions.Any(row => row.Id == id)) continue;
            subMenu.Items.Clear();
            var row = Sessions.First(session => session.Id == id);
            if (row.ClientSource != "cli")
            {
                AddSidebarMoveMenuItem(subMenu, "Recents", new(id, SidebarSessionOrganization.RecentsKey, null));
                foreach (var group in _lastWorkspaceGroups.Where(group => group.Path is not null))
                    AddSidebarMoveMenuItem(subMenu, group.Label, new(id, group.Key, group.Path));
            }
            var identity = SidebarGroupFor(row);
            var siblings = _lastWorkspaceGroups.FirstOrDefault(group => group.Key == identity.Key)?.Sessions.ToList();
            var index = siblings?.FindIndex(session => session.Id == id) ?? -1;
            if (siblings is null || index < 0) continue;
            subMenu.IsEnabled = subMenu.Items.Count > 0 || siblings.Count > 1;
            if (subMenu.Items.Count > 0 && siblings.Count > 1) subMenu.Items.Add(new MenuFlyoutSeparator());
            if (index > 0) AddSidebarMoveMenuItem(subMenu, "Move up", new(id, identity.Key, identity.Path, siblings[index - 1].Id));
            if (index < siblings.Count - 1) AddSidebarMoveMenuItem(subMenu, "Move down", new(id, identity.Key, identity.Path,
                index + 2 < siblings.Count ? siblings[index + 2].Id : null));
        }
    }

    private void AddSidebarMoveMenuItem(MenuFlyoutSubItem menu, string text, SidebarMoveCommand command)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = command };
        item.Click += OnSidebarMoveMenuClick;
        menu.Items.Add(item);
    }

    private void OnSidebarMoveMenuClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            if (sender is MenuFlyoutItem { Tag: SidebarMoveCommand command })
                MoveSidebarSession(command.SessionId, command.GroupKey, command.GroupPath, command.BeforeId);
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Sidebar menu move failed: {ex.Message}");
            SetSessionActionError("Could not move this session. Try again.");
        }
    }
}
