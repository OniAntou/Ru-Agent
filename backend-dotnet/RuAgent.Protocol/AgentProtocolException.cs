namespace RuAgent.Protocol;

public sealed class AgentProtocolException : Exception
{
    public AgentProtocolException(string code, string? message = null)
        : base(message ?? code)
    {
        Code = code;
    }

    public string Code { get; }
}
