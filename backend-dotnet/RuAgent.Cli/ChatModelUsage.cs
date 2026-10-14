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
        usage.ContextLimit = null;
        usage.InputRate = null;
        usage.OutputRate = null;
        if (selection.IsRetainedRun)
        {
            usage.ContextLimit = selection.Protocol is "openai_responses" or "chat_completions"
                ? selection.ContextWindow
                : null;
        }

        // Sequential/data-only chat never reads a catalog for numbers it cannot show.
        if (!hasHeader || string.IsNullOrWhiteSpace(selection.Model)
            || (selection.IsRetainedRun && string.IsNullOrWhiteSpace(selection.Backend)))
        {
            return;
        }

        try
        {
            var details = await readModelDetails(
                selection.Backend is "(provider unknown)" ? null : selection.Backend,
                selection.Model, cancellationToken).ConfigureAwait(false);
            if (details is null)
            {
                return;
            }

            if (!selection.IsRetainedRun)
            {
                usage.ContextLimit = details.ContextLength;
            }
            if (details.InputCost is { } inputRate && details.OutputCost is { } outputRate)
            {
                usage.InputRate = inputRate;
                usage.OutputRate = outputRate;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TuiDebug.Log("usage", $"model details read failed: {CliErrorText.ShortMessage(ex.Message)}");
        }
    }
}
