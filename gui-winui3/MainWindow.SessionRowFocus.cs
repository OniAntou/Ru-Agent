using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace RuApp;

public sealed partial class MainWindow
{
    private sealed class SessionRowInteraction
    {
        public SessionRowInteraction() { }
        public bool PointerInside;
        public bool MenuOpen;
    }
    private readonly ConditionalWeakTable<Grid, SessionRowInteraction> _sessionRowInteraction = new();
    private readonly ConditionalWeakTable<MenuFlyout, Grid> _sessionMenuOwners = new();

    private void SessionItem_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Grid row) UpdateSessionRowActions(row);
    }

    private void SessionItem_LostFocus(object sender, RoutedEventArgs e)
    {
        // Let focus settle on its new element before deciding whether it left.
        if (sender is Grid row) DispatcherQueue.TryEnqueue(() => UpdateSessionRowActions(row));
    }

    private void UpdateSessionRowActions(Grid row)
    {
        if (row.XamlRoot is null) return;
        var state = _sessionRowInteraction.GetOrCreateValue(row);
        var focusedInside = false;
        for (var node = FocusManager.GetFocusedElement(row.XamlRoot) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, row)) { focusedInside = true; break; }
        SetSessionRowActionsVisible(row, state.PointerInside || state.MenuOpen || focusedInside);
    }

    private void SetSessionMenuOpen(MenuFlyout menu, bool open)
    {
        if (!open)
        {
            if (_sessionMenuOwners.TryGetValue(menu, out var owner))
            {
                _sessionMenuOwners.Remove(menu);
                _sessionRowInteraction.GetOrCreateValue(owner).MenuOpen = false;
                DispatcherQueue.TryEnqueue(() => UpdateSessionRowActions(owner));
            }
            return;
        }
        for (DependencyObject? node = menu.Target; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not Grid { Tag: string } row) continue;
            _sessionRowInteraction.GetOrCreateValue(row).MenuOpen = open;
            _sessionMenuOwners.Remove(menu);
            _sessionMenuOwners.Add(menu, row);
            UpdateSessionRowActions(row);
            break;
        }
    }

    private void OnSessionMenuClosed(object sender, object e)
    {
        if (sender is MenuFlyout menu) SetSessionMenuOpen(menu, false);
    }
}
