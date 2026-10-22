namespace RuApp.Services;

/// <summary>One bounded sidebar width shared by pointer, keyboard and hide/show.</summary>
internal sealed class SidebarLayoutState
{
    public double ExpandedWidth { get; private set; } = ResponsiveLayoutPolicy.DefaultSidebarWidth;
    public bool IsHidden { get; private set; }
    public double VisibleWidth => IsHidden ? 0 : ExpandedWidth;

    public void SetWidth(double width) => ExpandedWidth = ResponsiveLayoutPolicy.ClampSidebarWidth(
        width, ResponsiveLayoutPolicy.GetForWidth(0));

    public void Toggle() => IsHidden = !IsHidden;
}
