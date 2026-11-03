using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace RuApp.Views;

public sealed partial class ChatView
{
    private readonly Dictionary<Flyout, UIElement> _chatFlyoutEscapeRoots = new();
    private KeyEventHandler? _chatFlyoutKeyDownHandler;

    private void AttachChatFlyoutEscape(Flyout flyout)
    {
        DetachChatFlyoutEscape(flyout);
        if (flyout.Content is not UIElement keyboardRoot) return;
        keyboardRoot.AddHandler(
            UIElement.KeyDownEvent,
            _chatFlyoutKeyDownHandler ??= new KeyEventHandler(OnChatFlyoutKeyDown),
            handledEventsToo: true);
        _chatFlyoutEscapeRoots.Add(flyout, keyboardRoot);
        if (ReferenceEquals(flyout, ContextWindowFlyout))
        {
            // This read-only popup has no focusable child. Give its content an
            // explicit keyboard target instead of leaving focus on the native
            // popup window, which has no chat routed-key handler.
            keyboardRoot.IsTabStop = true;
            keyboardRoot.Focus(FocusState.Programmatic);
        }
    }

    private void DetachChatFlyoutEscape(Flyout flyout)
    {
        if (!_chatFlyoutEscapeRoots.Remove(flyout, out var keyboardRoot)) return;
        if (_chatFlyoutKeyDownHandler is { } handler)
            keyboardRoot.RemoveHandler(UIElement.KeyDownEvent, handler);
    }

    private void OnChatOwnedFlyoutClosed(Flyout flyout)
    {
        // A close callback queued by an old popup instance must not detach a newer
        // open instance that now belongs to this same Flyout object.
        if (!flyout.IsOpen) DetachChatFlyoutEscape(flyout);
    }

    private void OnChatFlyoutKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || sender is not UIElement keyboardRoot)
        {
            return;
        }

        var owner = _chatFlyoutEscapeRoots
            .FirstOrDefault(pair => ReferenceEquals(pair.Value, keyboardRoot)).Key;
        if (owner is not null && DismissChatOwnedFlyout(owner)) e.Handled = true;
    }

    /// <summary>Dismisses a chat-owned flyout through its owner before run Escape policy.</summary>
    private bool TryDismissChatOwnedFlyout()
    {
        if (ChatModelFlyout?.IsOpen == true)
        {
            return DismissChatOwnedFlyout(ChatModelFlyout);
        }

        if (ChatReasoningFlyout?.IsOpen == true)
        {
            return DismissChatOwnedFlyout(ChatReasoningFlyout);
        }

        if (ContextWindowFlyout?.IsOpen == true)
        {
            return DismissChatOwnedFlyout(ContextWindowFlyout);
        }

        return false;
    }

    private bool DismissChatOwnedFlyout(Flyout flyout)
    {
        if (!flyout.IsOpen) return false;
        if (ReferenceEquals(flyout, ContextWindowFlyout))
        {
            _contextHoverTimer?.Stop();
            _contextHoverState.Dismiss();
        }

        flyout.Hide();
        return true;
    }

    /// <summary>Whether a chat popup owns Escape for its own dismissal.</summary>
    private bool IsChatFlyoutShowing()
    {
        var xamlRoot = XamlRoot;
        return xamlRoot is not null
            && VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot)
                .Any(popup => popup.Child is FlyoutPresenter or MenuFlyoutPresenter);
    }
}
