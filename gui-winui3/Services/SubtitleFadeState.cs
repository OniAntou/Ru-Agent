namespace RuApp.Services;

public enum SubtitleFadeAction
{
    None,
    Show,
    StartDelay,
    BeginFade,
    CancelFadeAndShow,
    Hide,
}

public sealed class SubtitleFadeState
{
    private Phase _phase = Phase.Hidden;

    public bool IsVisible => _phase is not Phase.Hidden;

    public SubtitleFadeAction OnTextAvailable()
    {
        SubtitleFadeAction action = _phase == Phase.Fading
            ? SubtitleFadeAction.CancelFadeAndShow
            : SubtitleFadeAction.Show;
        _phase = Phase.Visible;
        return action;
    }

    public SubtitleFadeAction OnTextCleared()
    {
        if (_phase != Phase.Visible)
        {
            return SubtitleFadeAction.None;
        }
        _phase = Phase.Waiting;
        return SubtitleFadeAction.StartDelay;
    }

    public SubtitleFadeAction OnDelayElapsed()
    {
        if (_phase != Phase.Waiting)
        {
            return SubtitleFadeAction.None;
        }
        _phase = Phase.Fading;
        return SubtitleFadeAction.BeginFade;
    }

    public SubtitleFadeAction OnFadeCompleted()
    {
        if (_phase != Phase.Fading)
        {
            return SubtitleFadeAction.None;
        }
        _phase = Phase.Hidden;
        return SubtitleFadeAction.Hide;
    }

    public void Reset() => _phase = Phase.Hidden;

    private enum Phase
    {
        Hidden,
        Visible,
        Waiting,
        Fading,
    }
}
