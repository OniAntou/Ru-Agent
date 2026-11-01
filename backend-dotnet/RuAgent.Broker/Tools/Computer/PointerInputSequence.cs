namespace RuAgent.Broker.Tools.Computer;

/// <summary>Pure sequencing for pointer input; the controller supplies native send operations.</summary>
internal static class PointerInputSequence
{
    public static void Drag(
        int fromX,
        int fromY,
        int toX,
        int toY,
        Func<int, int, nint> authorizeTargetAt,
        Action<int, int, nint> sendMove,
        Action<int, int, nint> sendButtonDown,
        Action<int, int, nint> sendButtonUp,
        Action delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizeTargetAt);
        ArgumentNullException.ThrowIfNull(sendMove);
        ArgumentNullException.ThrowIfNull(sendButtonDown);
        ArgumentNullException.ThrowIfNull(sendButtonUp);
        ArgumentNullException.ThrowIfNull(delay);

        cancellationToken.ThrowIfCancellationRequested();
        var root = authorizeTargetAt(fromX, fromY);
        sendMove(fromX, fromY, root);

        var lastX = fromX;
        var lastY = fromY;
        var lastRoot = root;
        var buttonDown = false;
        try
        {
            root = authorizeTargetAt(fromX, fromY);
            sendButtonDown(fromX, fromY, root);
            buttonDown = true;

            const int steps = 12;
            for (var step = 1; step <= steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                delay();
                cancellationToken.ThrowIfCancellationRequested();

                var x = fromX + (toX - fromX) * step / steps;
                var y = fromY + (toY - fromY) * step / steps;
                root = authorizeTargetAt(x, y);
                sendMove(x, y, root);
                lastX = x;
                lastY = y;
                lastRoot = root;
            }
        }
        finally
        {
            if (buttonDown)
            {
                // Release at the last authorized point without moving toward a newly denied target.
                sendButtonUp(lastX, lastY, lastRoot);
            }
        }
    }

    public static void Scroll(
        int x,
        int y,
        int deltaX,
        int deltaY,
        Func<int, int, nint> authorizeTargetAt,
        Action<int, int, nint> sendMove,
        Action<int, nint> sendVertical,
        Action<int, nint> sendHorizontal)
    {
        ArgumentNullException.ThrowIfNull(authorizeTargetAt);
        ArgumentNullException.ThrowIfNull(sendMove);
        ArgumentNullException.ThrowIfNull(sendVertical);
        ArgumentNullException.ThrowIfNull(sendHorizontal);

        var root = authorizeTargetAt(x, y);
        sendMove(x, y, root);
        if (deltaY != 0)
        {
            root = authorizeTargetAt(x, y);
            sendVertical(deltaY, root);
        }

        if (deltaX != 0)
        {
            root = authorizeTargetAt(x, y);
            sendHorizontal(deltaX, root);
        }
    }
}
