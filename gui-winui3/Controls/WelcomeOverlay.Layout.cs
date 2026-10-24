using Microsoft.UI.Xaml;

namespace RuApp.Controls;

public sealed partial class WelcomeOverlay
{
    private XamlRoot? _viewportRoot;

    private void BindViewportRoot()
    {
        UnbindViewportRoot();
        _viewportRoot = XamlRoot;
        if (_viewportRoot is not null) _viewportRoot.Changed += OnViewportChanged;
        ApplyResponsiveLayout();
    }

    private void UnbindViewportRoot()
    {
        if (_viewportRoot is not null) _viewportRoot.Changed -= OnViewportChanged;
        _viewportRoot = null;
    }

    private void OnViewportChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        ApplyViewportBounds(sender.Size.Width, sender.Size.Height);

    private void ApplyResponsiveLayout()
    {
        // The shell's desired/arranged width can exceed the visible window. Use
        // the real XAML viewport; centering inside a clipped shell shifts the logo.
        var viewport = (_viewportRoot ?? XamlRoot)?.Size;
        double width = viewport is { Width: > 0 } size ? size.Width : ActualWidth;
        double height = viewport is { Height: > 0 } bounds ? bounds.Height : ActualHeight;
        ApplyViewportBounds(width, height);
    }

    private void ApplyViewportBounds(double viewportWidth, double viewportHeight)
    {
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight)
            || viewportWidth <= 0 || viewportHeight <= 0) return;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Width = viewportWidth;
        Height = viewportHeight;
        ResizeBraille(viewportWidth, viewportHeight);
        _content.MaxWidth = Math.Min(480, Math.Max(0, viewportWidth - 48));
        _brandHost.Width = Math.Min(340, _content.MaxWidth);
        _brandHost.Height = viewportHeight < 300 ? 56 : 76;
    }
}
