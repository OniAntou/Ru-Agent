using System.Text.Json;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private async Task<(string InputId, string Question, IReadOnlyList<string> Options)?> ReadInputRequestAsync(
        string runId,
        string? payloadRef,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payloadRef))
        {
            return null;
        }

        try
        {
            var text = await _agentClient.GetPayloadAsync(runId, payloadRef, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("input_id", out var inputNode)
                || inputNode.ValueKind != JsonValueKind.String
                || inputNode.GetString() is not { Length: > 0 } inputId)
            {
                return null;
            }

            var question = root.TryGetProperty("question", out var questionNode)
                && questionNode.ValueKind == JsonValueKind.String
                    ? questionNode.GetString() ?? string.Empty
                    : string.Empty;
            var options = new List<string>();
            if (root.TryGetProperty("options", out var optionsNode)
                && optionsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in optionsNode.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } option)
                    {
                        options.Add(option);
                    }
                }
            }

            return (inputId, question, options);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DescribeInputRequest(
        string runId,
        string inputId,
        string question,
        IReadOnlyList<string> options)
    {
        var text = $"input_required: run {runId} is waiting for input {inputId}";
        if (!string.IsNullOrWhiteSpace(question))
        {
            text += $": {question}";
        }

        if (options.Count > 0)
        {
            text += $" (options: {string.Join(", ", options)})";
        }

        return text + $". Answer it with 'ru agent answer {runId} {inputId} \"<answer>\"'.";
    }

}
