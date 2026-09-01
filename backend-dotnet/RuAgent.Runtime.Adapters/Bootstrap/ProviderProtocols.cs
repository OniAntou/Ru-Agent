namespace RuAgent.Runtime.Adapters.Bootstrap;

public static class ProviderProtocols
{
    public const string ChatCompletions = "chat_completions";
    public const string OpenAiResponses = "openai_responses";
    public const string AnthropicMessages = "anthropic_messages";

    public static string Require(string value)
    {
        if (value is ChatCompletions or OpenAiResponses or AnthropicMessages) return value;
        throw new ArgumentException("Provider protocol is invalid.", nameof(value));
    }
}
