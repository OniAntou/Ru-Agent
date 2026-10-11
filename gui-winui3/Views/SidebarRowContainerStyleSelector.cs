using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>Only actionable rows receive the session hover/selection surface.</summary>
public sealed class SidebarRowContainerStyleSelector : StyleSelector
{
    public Style? SessionStyle { get; set; }
    public Style? HeadingStyle { get; set; }
    public Style? RecentsStyle { get; set; }
    public Style? EmptyStyle { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container) => item switch
    {
        SidebarSectionRow section => section.IsRecents ? RecentsStyle : HeadingStyle,
        SidebarEmptyRow => EmptyStyle,
        _ => SessionStyle,
    };
}
