using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;
using Windows.System;
using Windows.UI.Core;

namespace RuApp.Controls;

public sealed partial class AgentSidePanelControl
{
    private void OnTabStripKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Scope shortcuts to the tab strip so Ctrl+W/Tab in a terminal or an
        // installed Office preview retains that control's own behavior.
        bool control = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            & CoreVirtualKeyStates.Down) != 0;
        bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            & CoreVirtualKeyStates.Down) != 0;
        if (control && e.Key == VirtualKey.W)
        {
            TabClosed?.Invoke(this, _activeTab);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_mode == AgentSidePanelMode.Card) ExpandButton.Focus(FocusState.Keyboard);
                else FocusSelectedTab();
            });
            e.Handled = true;
        }
        else if (control && e.Key == VirtualKey.Tab && _openTabs.Count > 1)
        {
            int index = IndexOfOpen(_activeTab);
            int next = (index + (shift ? -1 : 1) + _openTabs.Count) % _openTabs.Count;
            var kind = _openTabs[next];
            TabActivated?.Invoke(this, kind);
            FocusSelectedTab();
            e.Handled = true;
        }
    }

    private void FocusSelectedTab()
    {
        if (_mode != AgentSidePanelMode.Panel) return;
        if (ChipFor(_activeTab).Child is Microsoft.UI.Xaml.Controls.StackPanel children
            && children.Children[0] is Microsoft.UI.Xaml.Controls.Button tab)
            tab.Focus(FocusState.Keyboard);
    }
}
