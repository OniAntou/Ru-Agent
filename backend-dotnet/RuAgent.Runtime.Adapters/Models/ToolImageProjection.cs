using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Models;

/// <summary>
/// Provider-native shapes for a tool result that carries an image (ADR 0026). Without
/// an image each protocol keeps its plain-text tool result unchanged.
/// </summary>
public static class ToolImageProjection
{
    /// <summary>Responses <c>function_call_output.output</c>: text, or input_text then input_image parts.</summary>
    public static JsonNode ResponsesOutput(ModelTurn turn)
    {
        if (turn.Images.Count == 0)
        {
            return JsonValue.Create(turn.Content)!;
        }

        var output = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = turn.Content });
        foreach (var image in turn.Images)
        {
            output.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = image.DataUrl });
        }

        return output;
    }

    /// <summary>Anthropic <c>tool_result.content</c>: text, or a text block then base64 image blocks.</summary>
    public static JsonNode AnthropicContent(ModelTurn turn)
    {
        if (turn.Images.Count == 0)
        {
            return JsonValue.Create(turn.Content)!;
        }

        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = turn.Content });
        foreach (var image in turn.Images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = image.MimeType,
                    ["data"] = image.Base64Data,
                },
            });
        }

        return content;
    }
}
