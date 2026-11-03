using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Controls;

public sealed partial class TerminalActivityCard : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(AgentActivityItem), typeof(TerminalActivityCard),
        new PropertyMetadata(null, (sender, _) => ((TerminalActivityCard)sender).Bindings.Update()));

    public TerminalActivityCard() => InitializeComponent();

    public AgentActivityItem? Item
    {
        get => (AgentActivityItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public event RoutedEventHandler? CopyRequested;
    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyRequested?.Invoke(this, e);

    public Brush StateBrush(bool failed, AgentActivityState state)
    {
        var key = failed ? "RuDangerBrush" : state == AgentActivityState.Waiting ? "RuWarningBrush"
            : state == AgentActivityState.Started ? "RuAccentBrush" : "RuSuccessBrush";
        return Application.Current?.Resources.TryGetValue(key, out var resource) == true && resource is Brush brush
            ? brush : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
