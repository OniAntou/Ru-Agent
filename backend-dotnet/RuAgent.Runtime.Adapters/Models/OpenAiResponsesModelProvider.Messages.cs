using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Runtime.Adapters.Models;

public sealed partial class OpenAiResponsesModelProvider
{
    private static ResponsesAssistantMessages? ReadAssistantMessages(ModelRequest request, JsonArray? output)
    {
        if (!request.NativeToolCalling || output is null) return null;
        var messages = output.OfType<JsonObject>().Where(item => ReadString(item["type"]) == "message").ToArray();
        if (!messages.Any(message => message.ContainsKey("phase"))) return null;
        if (messages.Length > ResponsesAssistantMessages.MaximumMessages
            || System.Text.Encoding.UTF8.GetByteCount(new JsonArray(messages.Select(message => message.DeepClone()).ToArray()).ToJsonString())
                > ResponsesAssistantMessages.MaximumBytes)
            throw new ModelProviderException("response_too_large", false);
        try { return new ResponsesAssistantMessages(messages); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new ModelProviderException("malformed_payload", false, error);
        }
    }
}
