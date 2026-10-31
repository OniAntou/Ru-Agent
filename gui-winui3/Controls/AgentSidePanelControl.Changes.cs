using Microsoft.UI.Xaml;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class AgentSidePanelControl
{
    private int _changeRevealGeneration;

    public void RevealChange(string? activityId)
    {
        int generation = ++_changeRevealGeneration;
        var row = Changes.FirstOrDefault(change => change.ActivityId == activityId);
        if (row is null) return;
        row.IsExpanded = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _changeRevealGeneration || !Changes.Contains(row)
                || _mode != AgentSidePanelMode.Panel || _activeTab != SidePanelTabKind.Changes) return;
            ChangesList.UpdateLayout();
            if (ChangesList.ContainerFromItem(row) is FrameworkElement container)
                container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        });
    }
}
