namespace RuAgent.Runtime.Worker;

public sealed record WorkerV1Options
{
    public WorkerV1Options(string token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || token.Length > 4096
            || token.Any(character => character > 127 || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("Worker token is invalid.", nameof(token));
        }

        Token = token;
    }

    public string Token { get; }
}
