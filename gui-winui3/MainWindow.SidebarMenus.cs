using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;
using RuApp.ViewModels;

namespace RuApp;

public sealed partial class MainWindow
{
    private void OnSessionMenuOpening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        SetSessionMenuOpen(menu, true);
        foreach (var subMenu in menu.Items.OfType<MenuFlyoutSubItem>())
        {
            if (subMenu.Tag is not string id) continue;
            subMenu.Items.Clear();
            var targets = Sidebar.MoveTargets(id);
            foreach (var target in targets)
            {
                if (target.IsSeparator)
                {
                    subMenu.Items.Add(new MenuFlyoutSeparator());
                    continue;
                }

                var item = new MenuFlyoutItem { Text = target.Text, Tag = target };
                item.Click += OnSidebarMoveMenuClick;
                subMenu.Items.Add(item);
            }

            subMenu.IsEnabled = targets.Count > 0;
        }
    }

    private void OnSidebarMoveMenuClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            if (sender is MenuFlyoutItem { Tag: SidebarMoveTarget target })
            {
                MoveSidebarSession(target.SessionId, target.GroupKey, target.GroupPath, target.BeforeId);
            }
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Sidebar menu move failed: {ex.Message}");
            SetSessionActionError("Could not move this session. Try again.");
        }
    }
}
