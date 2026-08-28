namespace RuApp.Services;

internal sealed record ChatTurn(
    string TurnId,
    string SessionId,
    string CorrelationId,
    int Generation);

internal sealed class ChatTurnSendException : Exception
{
    public ChatTurnSendException(ChatTurn turn, Exception innerException)
        : base("Chat turn could not be sent.", innerException)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(innerException);
        TurnId = turn.TurnId;
        CorrelationId = turn.CorrelationId;
    }

    public string TurnId { get; }

    public string CorrelationId { get; }
}
