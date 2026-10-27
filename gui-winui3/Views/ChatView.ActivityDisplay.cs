using System.ComponentModel;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    private void OnActivityDisplayPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AgentActivityDisplayPreferences.Compact)) return;
        if (DispatcherQueue.HasThreadAccess) RefreshActivityDisplay();
        else DispatcherQueue.TryEnqueue(RefreshActivityDisplay);
    }

    private void RefreshActivityDisplay()
    {
        var compact = AgentActivityDisplayPreferences.Instance.Compact;
        foreach (var message in Messages) message.CompactAgentActivity = compact;
    }

    private static void ApplyActivityDisplayPreference(System.Collections.IList? messages)
    {
        if (messages is null) return;
        var compact = AgentActivityDisplayPreferences.Instance.Compact;
        foreach (var message in messages)
            if (message is ChatMessageItem item) item.CompactAgentActivity = compact;
    }
}
