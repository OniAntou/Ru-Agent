using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

/// <summary> Picks the workspace-group, show-more, or session template for sidebar rows.</summary>
public sealed class SidebarRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupTemplate { get; set; }

    public DataTemplate? SessionTemplate { get; set; }

    public DataTemplate? ShowMoreTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        WorkspaceGroupRow => GroupTemplate,
        WorkspaceShowMoreRow => ShowMoreTemplate,
        SessionInfo => SessionTemplate,
        _ => base.SelectTemplateCore(item),
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
