using RuAgent.Client;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

internal readonly record struct ChatModelUsageSelection(
    string? Backend,
    string? Model,
    bool IsRetainedRun = false,
    long? ContextWindow = null,
    string? Protocol = null);

/// <summary>
/// Resolves optional display metadata without changing the selected provider/model.
/// A retained run owns its context window; the live catalog supplies only its rates.
/// </summary>
internal sealed class ChatModelUsage(
    Func<string?, string, CancellationToken, Task<CatalogModelDetails?>> readModelDetails)
{
    public async Task RefreshAsync(
        TuiUsageTracker usage,
        ChatModelUsageSelection selection,
        bool hasHeader,
        CancellationToken cancellationToken)
    {
        long? context = selection.IsRetainedRun
            && selection.Protocol is "openai_responses" or "chat_completions" or "anthropic_messages"
                ? selection.ContextWindow : null;
        double? input = null;
        double? output = null;
        // Do not publish an empty intermediate window while awaiting catalog metadata.
        // Sequential/data-only chat never reads numbers it cannot show.
        if (hasHeader && !string.IsNullOrWhiteSpace(selection.Model)
            && (!selection.IsRetainedRun || !string.IsNullOrWhiteSpace(selection.Backend)))
        {
            try
            {
                var details = await readModelDetails(
                    selection.Backend is "(provider unknown)" ? null : selection.Backend,
                    selection.Model, cancellationToken).ConfigureAwait(false);
                if (details is not null)
                {
                    if (!selection.IsRetainedRun) context = details.ContextLength;
                    if (details.InputCost is { } inputRate && details.OutputCost is { } outputRate)
                    {
                        input = inputRate;
                        output = outputRate;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                TuiDebug.Log("usage", $"model details read failed: {ex.GetType().Name}");
            }
        }
        usage.ContextLimit = context;
        usage.ProviderProtocol = selection.Protocol;
        usage.InputRate = input;
        usage.OutputRate = output;
    }
}
