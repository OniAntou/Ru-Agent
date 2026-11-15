using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    /// <summary>The same singleton the root and message templates bind to.</summary>
    public ConversationWidthState WidthState => ConversationWidthState.Instance;

    private ConversationWidthState? _conversationWidth;
    private bool _conversationWidthDragging;
    private bool _conversationWidthDragRight;
    private bool _conversationWidthMoved;
    private double _conversationWidthDragOrigin;
    private double _conversationWidthDragStart;

    private void InitializeConversationWidth()
    {
        _conversationWidth = ConversationWidthState.Instance;
        _conversationWidth.PropertyChanged += OnConversationWidthChanged;
        ApplyConversationWidth(Messages);
        Loaded += (_, _) => UpdateConversationWidthViewport();
        UpdateConversationWidthViewport();
    }

    private void OnConversationWidthChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ConversationWidthState.ContentWidth)) return;
        ApplyConversationWidth(Messages);
    }

    private void ApplyConversationWidth(System.Collections.Generic.IEnumerable<Models.ChatMessageItem>? messages)
    {
        var width = _conversationWidth?.ContentWidth ?? ConversationWidthState.DefaultWidth;
        UpdateConversationWidthLayerBounds(width);
        if (messages is not null)
        {
            foreach (var message in messages) message.ContentWidth = width;
        }

        if (RootGrid is not null && RootGrid.ActualWidth > 0) ApplyInputWidth();
    }

    /// <summary>
    /// Keeps the invisible grab layer on the reading column's edges, including the
    /// side-panel reserve. The bounds come from the same arithmetic that sizes every
    /// message column (<see cref="LayoutMessageColumn"/>), not from measuring a realized
    /// row: this runs right after a width change, before the next layout pass, so a
    /// measured row still had its old (or first-measure) width. That left the handles
    /// stranded inside the text and the real edges unresponsive until a resize.
    /// </summary>
    private void UpdateConversationWidthLayerBounds(double width)
    {
        if (ChatListView is null || ChatContentGrid is null) return;
        var bounds = ConversationColumnBounds.For(
            hostWidth: ChatListView.ActualWidth > 0 ? ChatListView.ActualWidth : ChatContentGrid.ActualWidth,
            hostLeft: ChatListView.ActualWidth > 0
                      && VisualTransforms.TryTransformPoint(ChatListView, ChatContentGrid, new Windows.Foundation.Point(0, 0), out var listOrigin)
                ? listOrigin.X
                : 0,
            paddingLeft: ChatListView.Padding.Left,
            paddingRight: ChatListView.Padding.Right,
            sidePanelReserve: _sidePanelReserveWidth,
            contentWidth: width);

        ConversationWidthLayer.HorizontalAlignment = HorizontalAlignment.Left;
        ConversationWidthLayer.Margin = new Thickness(bounds.Left - ConversationWidthState.HandleOutset, 20, 0, 20);
        ConversationWidthLayer.Width = bounds.Width + ConversationWidthState.HandleOutset * 2;
    }

    private void UpdateConversationWidthViewport()
    {
        var hostWidth = ChatContentGrid.ActualWidth;
        if (hostWidth <= 0 || ChatListView is null) return;
        var available = Math.Max(0,
            hostWidth - _sidePanelReserveWidth - ChatListView.Padding.Left - ChatListView.Padding.Right);
        _conversationWidth?.SetViewportWidth(available + ConversationWidthState.EdgeBudget);
    }

    private void OnChatContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateConversationWidthViewport();
        UpdateConversationWidthLayerBounds(_conversationWidth?.ContentWidth ?? ConversationWidthState.DefaultWidth);
    }

    private void OnConversationWidthHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_conversationWidth is null || sender is not UIElement handle) return;
        var point = e.GetCurrentPoint(ChatContentGrid);
        if (!point.Properties.IsLeftButtonPressed) return;
        _conversationWidth.BeginDrag();
        _conversationWidthDragging = true;
        _conversationWidthDragRight = ReferenceEquals(sender, ConversationWidthRightHandle);
        _conversationWidthDragOrigin = point.Position.X;
        _conversationWidthDragStart = _conversationWidth.ContentWidth;
        _conversationWidthMoved = false;
        SetConversationWidthGripVisible(sender, true);
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnConversationWidthHandlePointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetConversationWidthGripVisible(sender, true);

    private void OnConversationWidthHandlePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_conversationWidthDragging) SetConversationWidthGripVisible(sender, false);
    }

    private void SetConversationWidthGripVisible(object sender, bool visible)
    {
        if (ReferenceEquals(sender, ConversationWidthLeftHandle))
            ConversationWidthLeftGrip.Opacity = visible ? 1 : 0;
        else if (ReferenceEquals(sender, ConversationWidthRightHandle))
            ConversationWidthRightGrip.Opacity = visible ? 1 : 0;
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
        if (_conversationWidth is not null && !_conversationWidthMoved)
        {
            _conversationWidth.CancelDrag();
        }

        if (sender is UIElement handle) handle.ReleasePointerCapture(e.Pointer);
        if (sender is UIElement released) SetConversationWidthGripVisible(released, false);
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
        if (sender is UIElement released) SetConversationWidthGripVisible(released, false);
    }
}
