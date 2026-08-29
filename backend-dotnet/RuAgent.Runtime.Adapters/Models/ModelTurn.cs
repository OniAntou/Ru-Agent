namespace RuAgent.Runtime.Adapters.Models;

public sealed record ModelTurn
{
    public ModelTurn(string role, string content)
    {
        if (string.IsNullOrWhiteSpace(role) || role.Length > 32
            || role is not ("system" or "user" or "assistant" or "tool"))
        {
            throw new ArgumentException("Model turn role is invalid.", nameof(role));
        }

        if (content is null || content.Length > 1_000_000 || content.Contains('\0'))
        {
            throw new ArgumentException("Model turn content exceeds the adapter cap.", nameof(content));
        }

        Role = role;
        Content = content;
    }

    public string Role { get; }

    public string Content { get; }
}
