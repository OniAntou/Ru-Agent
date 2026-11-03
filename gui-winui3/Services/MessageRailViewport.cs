namespace RuApp.Services;

public readonly record struct MessageRailSelection(int FirstMessage, int LastMessage)
{
    public static MessageRailSelection Empty => new(-1, -1);
    public bool IsEmpty => FirstMessage < 0 || LastMessage < FirstMessage;
}

public static partial class MessageRailPolicy
{
    /// <summary>Maps the virtualized panel's visible indices to rail emphasis.</summary>
    public static MessageRailSelection SelectViewport(int messageCount, int firstVisible, int lastVisible,
        double offset, double maximum)
    {
        if (messageCount <= 0 || firstVisible < 0 || firstVisible >= messageCount || lastVisible < firstVisible
            || !ChatScrollPolicy.IsConsistent(offset, maximum)) return MessageRailSelection.Empty;

        var last = System.Math.Min(lastVisible, messageCount - 1);
        // A non-scrolling transcript is already at its end. Use the actual end,
        // not the looser follow-output threshold, which can include offscreen rows.
        var atEnd = maximum == 0 || maximum - offset <= 1;
        if (atEnd && last != messageCount - 1)
            return new(messageCount - 1, messageCount - 1);
        return new(firstVisible, last);
    }

    public static bool IsVisibleTick(int tick, int tickCount, int messageCount, MessageRailSelection selection)
        => !selection.IsEmpty && tick >= 0 && tick < tickCount && messageCount > 0
            && tick >= TickOfMessage(selection.FirstMessage, tickCount, messageCount)
            && tick <= TickOfMessage(selection.LastMessage, tickCount, messageCount);
}
