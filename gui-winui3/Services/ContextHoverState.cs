namespace RuApp.Services;

/// <summary>Prevents a dismissed hover popup from reopening until the pointer leaves its owner.</summary>
internal sealed class ContextHoverState
{
    private bool _pointerInside;
    private bool _dismissedUntilExit;

    internal bool CanOpen => _pointerInside && !_dismissedUntilExit;

    internal void ObservePointer(bool inside)
    {
        _pointerInside = inside;
        if (!inside) _dismissedUntilExit = false;
    }

    internal void Dismiss()
    {
        if (_pointerInside) _dismissedUntilExit = true;
    }
}
