namespace RuApp.Services;

internal sealed class ChatTurnPresenter
{
    private readonly ChatTurnQueue _queue;

    public ChatTurnPresenter(ChatTurnQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public ChatTurnUpdate Apply(ChatEventFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrWhiteSpace(frame.CorrelationId)
            || !_queue.TryGetActive(frame.CorrelationId, out var turn))
        {
            return new ChatTurnUpdate(ChatTurnUpdateKind.Ignored);
        }

        return frame.Type switch
        {
            "session" => new ChatTurnUpdate(
                ChatTurnUpdateKind.Session,
                turn,
                SessionId: frame.SessionId),
            "reset" => new ChatTurnUpdate(ChatTurnUpdateKind.Reset, turn),
            "chunk" => new ChatTurnUpdate(
                ChatTurnUpdateKind.Delta,
                turn,
                frame.Content ?? string.Empty),
            "done" when _queue.Complete(frame.CorrelationId) => new ChatTurnUpdate(
                ChatTurnUpdateKind.Done,
                turn,
                frame.Content,
                frame.SessionId),
            "error" when _queue.Complete(frame.CorrelationId) => new ChatTurnUpdate(
                ChatTurnUpdateKind.Error,
                turn,
                frame.Content,
                frame.SessionId,
                frame.ErrorCode),
            _ => new ChatTurnUpdate(ChatTurnUpdateKind.Ignored),
        };
    }
}
