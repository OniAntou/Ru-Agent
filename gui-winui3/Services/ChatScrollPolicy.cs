namespace RuApp.Services;

/// <summary>Decides follow behaviour from one observed transcript viewport.</summary>
public static class ChatScrollPolicy
{
    public static bool IsConsistent(double offset, double maximum) =>
        double.IsFinite(offset) && double.IsFinite(maximum) && offset >= 0 && maximum >= 0 && offset <= maximum;

    public static bool IsNearBottom(double offset, double maximum) =>
        IsConsistent(offset, maximum) && maximum > 0 && maximum - offset <= UiTiming.ChatFollowThresholdPx;

    public static bool CanFollow(double offset, double maximum, bool following) =>
        IsConsistent(offset, maximum) && maximum > 0 && (IsNearBottom(offset, maximum) || following);

    public static bool FollowAfterViewChange(double offset, double maximum, bool following) =>
        !IsConsistent(offset, maximum) || maximum == 0 ? following : following || IsNearBottom(offset, maximum);

    public static double RestoreAnchorOffset(double offset, double currentY, double savedY, double maximum) =>
        Math.Clamp(offset + currentY - savedY, 0, maximum);
}
