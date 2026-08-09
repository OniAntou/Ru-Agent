using RuApp.Models;

namespace RuApp.Services;

public sealed class SubtitlePlacementCache
{
    private RectPx _rect;
    private int _fontSize;
    private bool _hasValue;

    public bool ShouldApply(RectPx rect, int fontSize)
    {
        if (_hasValue && _rect == rect && _fontSize == fontSize)
        {
            return false;
        }

        _rect = rect;
        _fontSize = fontSize;
        _hasValue = true;
        return true;
    }

    public void Reset() => _hasValue = false;
}
