using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Models;

public sealed record ModelFunctionCall
{
    private const int MaximumIdChars = 256;
    private const int MaximumNameChars = 128;
    private const int MaximumArgumentBytes = 64 * 1024;

    public ModelFunctionCall(string id, string name, JsonElement arguments)
    {
        ValidateIdentifier(id, MaximumIdChars, nameof(id));
        ValidateIdentifier(name, MaximumNameChars, nameof(name));
        if (arguments.ValueKind != JsonValueKind.Object
            || Encoding.UTF8.GetByteCount(arguments.GetRawText()) > MaximumArgumentBytes)
        {
            throw new ArgumentException("Model function arguments are invalid.", nameof(arguments));
        }

        Id = id;
        Name = name;
        Arguments = arguments.Clone();
    }

    public string Id { get; }

    public string Name { get; }

    public JsonElement Arguments { get; }

    internal static void ValidateIdentifier(string? value, int maximum, string parameterName)
    {
        if (value is not { Length: >= 1 }
            || value.Length > maximum
            || value != value.Trim()
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '_' or '-' or '.')))
        {
            throw new ArgumentException("Model message identifier is invalid.", parameterName);
        }
    }

    internal static void ValidateResultCode(string? value, string parameterName)
    {
        if (value is not { Length: >= 1 and <= 64 }
            || value[0] is < 'a' or > 'z'
            || value.Any(character => !(char.IsAsciiLetterLower(character)
                || char.IsAsciiDigit(character)
                || character is '_' or '.')))
        {
            throw new ArgumentException("Model tool result code is invalid.", parameterName);
        }
    }
}

public sealed record ModelTurn
{
    private const int MaximumContentChars = 1_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ModelTurn(
        string role,
        string content,
        IReadOnlyList<ModelFunctionCall>? toolCalls = null,
        string? toolCallId = null,
        string? toolResultCode = null,
        string? reasoningContent = null,
        IReadOnlyList<ModelImage>? images = null,
        RuAgent.Harness.Core.Prompting.ResponsesAssistantMessages? responsesMessages = null)
    {
        if (role is not ("system" or "user" or "assistant" or "tool"))
        {
            throw new ArgumentException("Model turn role is invalid.", nameof(role));
        }

        // A tool result may carry the image its tool returned (ADR 0026); no other turn does.
        var copiedImages = images?.ToArray() ?? Array.Empty<ModelImage>();
        if (copiedImages.Length > 0 && (role != "tool" || copiedImages.Any(image => image is null)))
        {
            throw new ArgumentException("Only tool turns can carry images.", nameof(images));
        }

        ValidateContent(content, role, toolCalls is { Count: > 0 });
        var calls = toolCalls?.ToArray() ?? Array.Empty<ModelFunctionCall>();
        if (calls.Any(call => call is null))
        {
            throw new ArgumentException("Model turn calls cannot contain null.", nameof(toolCalls));
        }

        ValidateRoleFields(role, content, calls, toolCallId, toolResultCode);
        if (reasoningContent is not null && (role != "assistant" || reasoningContent.Contains('\0')
            || StrictUtf8.GetByteCount(reasoningContent) > RuAgent.Harness.Core.Prompting.ProviderContinuation.MaximumBytes))
            throw new ArgumentException("Invalid private chat reasoning.", nameof(reasoningContent));
        if (responsesMessages is not null && (role != "assistant" || responsesMessages.Text != content))
            throw new ArgumentException("Invalid Responses message history.", nameof(responsesMessages));
        ResponsesMessages = responsesMessages;
        Role = role;
        Content = content;
        ToolCalls = new ReadOnlyCollection<ModelFunctionCall>(calls);
        ToolCallId = toolCallId;
        ToolResultCode = toolResultCode;
        ReasoningContent = reasoningContent;
        Images = Array.AsReadOnly(copiedImages);
    }

    public RuAgent.Harness.Core.Prompting.ResponsesAssistantMessages? ResponsesMessages { get; }
    public string Role { get; }

    public string Content { get; }

    public IReadOnlyList<ModelFunctionCall> ToolCalls { get; }

    public string? ToolCallId { get; }

    public string? ToolResultCode { get; }
    public string? ReasoningContent { get; }

    /// <summary>
    /// Images a tool returned. They are not part of <see cref="ToJsonObject"/>, so the
    /// history byte bound measures text only; <see cref="ModelRequest"/> bounds images.
    /// </summary>
    public IReadOnlyList<ModelImage> Images { get; }

    internal JsonObject ToJsonObject()
    {
        var result = new JsonObject
        {
            ["role"] = Role,
            ["content"] = Content,
        };
        if (ReasoningContent is not null) result["reasoning_content"] = ReasoningContent;
        if (Role == "assistant" && ToolCalls.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var call in ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.GetRawText(),
                    },
                });
            }
            result["tool_calls"] = calls;
        }
        else if (Role == "tool")
        {
            result["tool_call_id"] = ToolCallId;
            result["tool_result_code"] = ToolResultCode;
        }

        return result;
    }

    private static void ValidateContent(string content, string role, bool hasCalls)
    {
        if (content is null || content.Length > MaximumContentChars || content.Contains('\0'))
        {
            throw new ArgumentException("Model turn content exceeds the adapter cap.", nameof(content));
        }

        try
        {
            _ = StrictUtf8.GetByteCount(content);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Model turn content is not valid UTF-8.", nameof(content));
        }

        if (!hasCalls && role is not "tool" && string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Model turn content is required.", nameof(content));
        }
    }

    private static void ValidateRoleFields(
        string role,
        string content,
        IReadOnlyList<ModelFunctionCall> calls,
        string? toolCallId,
        string? toolResultCode)
    {
        if (role == "assistant")
        {
            if (toolCallId is not null || toolResultCode is not null)
            {
                throw new ArgumentException("Assistant turns cannot contain tool result fields.", nameof(role));
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls)
            {
                if (!ids.Add(call.Id))
                {
                    throw new ArgumentException("Assistant calls require unique identifiers.", nameof(calls));
                }
            }

            return;
        }

        if (calls.Count > 0)
        {
            throw new ArgumentException("Only assistant turns can contain tool calls.", nameof(calls));
        }

        if (role == "tool")
        {
            ModelFunctionCall.ValidateIdentifier(toolCallId, 256, nameof(toolCallId));
            ModelFunctionCall.ValidateResultCode(toolResultCode, nameof(toolResultCode));
            return;
        }

        if (toolCallId is not null || toolResultCode is not null)
        {
            throw new ArgumentException("Text turns cannot contain tool result fields.", nameof(role));
        }
    }
}
