using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// One pending Host-intercepted question, parsed from the <c>input.requested</c>
/// payload blob. The durable event carries only the payload ref, so the live
/// stream and the session-open replay both read the question from here.
/// </summary>
public static class AgentInputRequest
{
    public sealed record PendingInput(string InputId, string Question, IReadOnlyList<string> Options);

    /// <summary>
    /// The question, its id, and its optional canned answers, or null when the
    /// blob is missing, malformed, or carries no input id.
    /// </summary>
    public static PendingInput? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("input_id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idElement.GetString()))
            {
                return null;
            }

            string question = root.TryGetProperty("question", out var questionElement)
                && questionElement.ValueKind == JsonValueKind.String
                ? questionElement.GetString() ?? string.Empty
                : string.Empty;
            var options = new List<string>();
            if (root.TryGetProperty("options", out var optionElements)
                && optionElements.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in optionElements.EnumerateArray())
                {
                    if (options.Count >= AgentQuestionPresentation.MaximumOptions)
                    {
                        break;
                    }

                    if (option.ValueKind == JsonValueKind.String
                        && option.GetString() is { Length: > 0 } text)
                    {
                        options.Add(text);
                    }
                }
            }

            return new PendingInput(idElement.GetString()!, question, options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
