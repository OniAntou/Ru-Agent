using RuAgent.Client;

namespace RuAgent.Cli;

/// <summary>Reads durable replies with bounded concurrency, preserving model-step order and payload kinds.</summary>
internal static class ChatAssistantPayloadReader
{
    private const int MaximumConcurrency = 4;

    internal static async Task<IReadOnlyList<AgentPayloadContent>> ReadAsync(
        IReadOnlyList<string> payloadRefs,
        Func<string, CancellationToken, Task<AgentPayloadContent>> read,
        CancellationToken cancellationToken)
    {
        var results = new AgentPayloadContent[payloadRefs.Count];
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var next = -1;
        async Task ReadWorkerAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= results.Length) return;
                try
                {
                    results[index] = await read(payloadRefs[index], pending.Token).ConfigureAwait(false);
                    pending.Token.ThrowIfCancellationRequested();
                }
                catch
                {
                    // A failed payload invalidates reconciliation. Stop the remaining
                    // reads and observe all workers before their token is disposed.
                    pending.Cancel();
                    throw;
                }
            }
        }

        var workers = Enumerable.Range(0, Math.Min(MaximumConcurrency, results.Length))
            .Select(_ => ReadWorkerAsync()).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        return results;
    }
}
