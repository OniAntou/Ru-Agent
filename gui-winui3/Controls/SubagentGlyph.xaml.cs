using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RuApp.Controls;

/// <summary>Stable branch marks distinguish identities without avatars or an extra asset pipeline.</summary>
public sealed partial class SubagentGlyph : UserControl
{
    public static readonly DependencyProperty AgentIdProperty = DependencyProperty.Register(nameof(AgentId), typeof(string),
        typeof(SubagentGlyph), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty RoleProperty = DependencyProperty.Register(nameof(Role), typeof(string),
        typeof(SubagentGlyph), new PropertyMetadata("review", Changed));
    public string AgentId { get => (string)GetValue(AgentIdProperty); set => SetValue(AgentIdProperty, value); }
    public string Role { get => (string)GetValue(RoleProperty); set => SetValue(RoleProperty, value); }
    public SubagentGlyph() { InitializeComponent(); Paint(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((SubagentGlyph)sender).Paint();
    private void Paint()
    {
        if (Branch is null) return;
        uint hash = 2166136261;
        foreach (var c in AgentId) hash = unchecked((hash ^ c) * 16777619);
        var key = (hash % 4) switch { 0 => "RuInfoBrush", 1 => "RuVioletBrush", 2 => "RuPinkBrush", _ => "RuAccentBrush" };
        var brush = (Brush)Application.Current.Resources[key];
        Branch.Stroke = RootNode.Fill = ReviewNode.Stroke = CodingNode.Stroke = brush;
        Orientation.Angle = (hash >> 3) % 4 * 90;
        CodingNode.Visibility = Role == "coding" ? Visibility.Visible : Visibility.Collapsed;
        ReviewNode.Visibility = Role == "coding" ? Visibility.Collapsed : Visibility.Visible;
    }
}
