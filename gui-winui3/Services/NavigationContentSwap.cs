namespace RuApp.Services;

/// <summary>Owns pending navigation swaps; called only on the window's UI thread.</summary>
internal sealed class NavigationContentSwap
{
    private long _generation;

    public void Show(object? current, object? next, Action<object?> apply,
        Action<Action> transition, Action restoreVisibility)
    {
        var generation = ++_generation;
        // Returning to the mounted page also supersedes an outgoing fade.
        if (ReferenceEquals(current, next))
        {
            restoreVisibility();
            return;
        }
        transition(() =>
        {
            if (generation == _generation) apply(next);
        });
    }

    public void Cancel() => _generation++;
}
