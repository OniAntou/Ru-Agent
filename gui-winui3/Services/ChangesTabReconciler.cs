using System.Collections.ObjectModel;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>Keeps unchanged file rows, and their open diffs, alive while the live feed advances.</summary>
public static class ChangesTabReconciler
{
    public static void Apply(ObservableCollection<ChangesTabRow> current, IReadOnlyList<ChangesTabRow> next)
    {
        for (int index = 0; index < next.Count; index++)
        {
            var incoming = next[index];
            int existing = -1;
            for (int candidate = index; candidate < current.Count; candidate++)
            {
                if (current[candidate].Key == incoming.Key)
                {
                    existing = candidate;
                    break;
                }
            }

            if (existing < 0)
            {
                current.Insert(index, incoming);
                continue;
            }

            if (existing != index) current.Move(existing, index);
            var retained = current[index];
            if (!retained.HasSameContent(incoming))
            {
                incoming.IsExpanded = retained.IsExpanded;
                current[index] = incoming;
            }
        }

        while (current.Count > next.Count) current.RemoveAt(current.Count - 1);
    }
}
