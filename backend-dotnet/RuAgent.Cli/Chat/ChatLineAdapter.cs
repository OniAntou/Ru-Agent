using RuAgent.Cli.Tui;

namespace RuAgent.Cli.Chat;

/// <summary>Reads data into the same controller used by the native UI; no command/run dispatch.</summary>
internal static class ChatLineAdapter
{
    internal static async Task RunAsync(TextReader input, TextWriter prompts, PlainChatView view,
        ChatSessionEngine engine, Func<Task> waitForAttachments, CancellationToken ct)
    {
        var reader = new PlainChatInput();
        try
        {
            while (!engine.IsStopped && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineWithContinuationAsync(input, prompts, "> ", "... ", ct).ConfigureAwait(false);
                if (line is null) break;
                view.InputText = line.Trim();
                engine.Submit();
                await engine.WaitForIdleAsync(ct).ConfigureAwait(false);
                await waitForAttachments().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelling interactive input is a normal chat exit. The controller and
            // session owner below still settle cancellation and attachment cleanup.
        }
        finally
        {
            engine.Stop();
            await engine.Completion.ConfigureAwait(false);
            await engine.WaitForShutdownSettlementAsync().ConfigureAwait(false);
        }
    }
}
