using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    private ConversationWidthState? _conversationWidth;
    private bool _conversationWidthDragging;
    private bool _conversationWidthDragRight;
    private bool _conversationWidthMoved;
    private double _conversationWidthDragOrigin;
    private double _conversationWidthDragStart;

    private void InitializeConversationWidth()
    {
        _conversationWidth = Resources.TryGetValue("ConversationWidthState", out var resource)
            ? resource as ConversationWidthState
            : null;
        if (_conversationWidth is null) return;
        Loaded += (_, _) => UpdateConversationWidthViewport();
        UpdateConversationWidthViewport();
    }

    private void UpdateConversationWidthViewport()
    {
        var width = ChatContentGrid.ActualWidth;
        if (width > 0) _conversationWidth?.SetViewportWidth(width);
    }

    private void OnChatContentSizeChanged(object sender, SizeChangedEventArgs e) =>
        _conversationWidth?.SetViewportWidth(e.NewSize.Width);

    private void OnConversationWidthHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_conversationWidth is null || sender is not UIElement handle) return;
        var point = e.GetCurrentPoint(ChatContentGrid);
        if (!point.Properties.IsLeftButtonPressed) return;
        _conversationWidthDragging = true;
        _conversationWidthDragRight = ReferenceEquals(sender, ConversationWidthRightHandle);
        _conversationWidthDragOrigin = point.Position.X;
        _conversationWidthDragStart = _conversationWidth.ContentWidth;
        _conversationWidthMoved = false;
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnConversationWidthHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_conversationWidthDragging || _conversationWidth is not { } width
            || sender is not UIElement)
        {
            return;
        }

        var x = e.GetCurrentPoint(ChatContentGrid).Position.X;
        var delta = x - _conversationWidthDragOrigin;
        if (Math.Abs(delta) > 1) _conversationWidthMoved = true;
        var outward = _conversationWidthDragRight ? delta : -delta;
        width.PreviewWidth(_conversationWidthDragStart + outward * 2);
        e.Handled = true;
    }

    private void OnConversationWidthHandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_conversationWidthDragging) return;
        _conversationWidthDragging = false;
        if (_conversationWidth is not null)
        {
            if (_conversationWidthMoved) _conversationWidth.Commit();
            else _conversationWidth.CancelDrag();
        }

        if (sender is UIElement handle) handle.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnConversationWidthHandlePointerCanceled(object sender, PointerRoutedEventArgs e) =>
        CancelConversationWidthDrag(sender, e);

    private void OnConversationWidthHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        CancelConversationWidthDrag(sender, e);

    private void CancelConversationWidthDrag(object sender, PointerRoutedEventArgs e)
    {
        if (!_conversationWidthDragging) return;
        _conversationWidthDragging = false;
        _conversationWidth?.CancelDrag();
        if (sender is UIElement handle) handle.ReleasePointerCapture(e.Pointer);
    }
}
