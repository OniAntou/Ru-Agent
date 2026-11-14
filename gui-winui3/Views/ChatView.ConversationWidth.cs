using Microsoft.UI.Xaml;
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
        ConversationWidthLayer.Width = width + ConversationWidthState.HandleOutset * 2;
        InputContainer.MaxWidth = width + ConversationWidthState.ComposerGutter;
        ApprovalHostGrid.MaxWidth = width + ConversationWidthState.ComposerGutter;
        WelcomeHeroPanel.MaxWidth = width + ConversationWidthState.ComposerGutter;
        if (messages is not null)
        {
            foreach (var message in messages) message.ContentWidth = width;
        }
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
        if (_conversationWidth is not null)
        {
            if (_conversationWidthMoved) _conversationWidth.Commit();
            else _conversationWidth.CancelDrag();
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
