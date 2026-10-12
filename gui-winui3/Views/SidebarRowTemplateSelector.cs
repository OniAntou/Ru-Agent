using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

/// <summary> Picks the workspace-group, show-more, or session template for sidebar rows.</summary>
public sealed class SidebarRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CliTemplate { get; set; }
    public DataTemplate? RecentsTemplate { get; set; }
    public DataTemplate? WorkspacesTemplate { get; set; }
    public DataTemplate? EmptyTemplate { get; set; }

    public DataTemplate? GroupTemplate { get; set; }

    public DataTemplate? SessionTemplate { get; set; }

    public DataTemplate? ShowMoreTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        SidebarSectionRow section => section.IsCli ? CliTemplate : section.IsRecents ? RecentsTemplate : WorkspacesTemplate,
        SidebarEmptyRow => EmptyTemplate,
        WorkspaceGroupRow => GroupTemplate,
        WorkspaceShowMoreRow => ShowMoreTemplate,
        SessionInfo => SessionTemplate,
        _ => base.SelectTemplateCore(item),
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
