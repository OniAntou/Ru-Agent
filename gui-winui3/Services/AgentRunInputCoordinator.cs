namespace RuApp.Services;

/// <summary>Owns question request identity, answer submission, and successful-card cleanup.</summary>
internal sealed class AgentRunInputCoordinator
{
    internal string? Id { get; private set; }
    internal string? Question { get; private set; }
    internal IReadOnlyList<string> Options { get; private set; } = Array.Empty<string>();
    internal bool HasInput => Id is not null;

    internal void Set(string? id, string? question, IReadOnlyList<string>? options)
    {
        Id = id;
        Question = question;
        Options = options ?? Array.Empty<string>();
    }

    internal void SetId(string? id) => Id = id;
    internal void SetQuestion(string? question) => Question = question;
    internal void SetOptions(IReadOnlyList<string>? options) => Options = options ?? Array.Empty<string>();
    internal void Clear() => Set(null, null, null);

    internal async Task SubmitAsync(
        string answer,
        string? runId,
        string? inputId,
        IAgentRunClient client,
        Action<string> setStatus,
        Func<string, string, bool> ownsRequest,
        Action clearPendingInput,
        Func<CancellationToken, Task> refresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(ownsRequest);
        ArgumentNullException.ThrowIfNull(clearPendingInput);
        ArgumentNullException.ThrowIfNull(refresh);
        if (runId is null || inputId is null)
        {
            throw new InvalidOperationException("No Agent question is pending.");
        }

        setStatus("Submitting answer...");
        // The question card is removed on the same successful resolve as the
        // answer POST, before refresh can replay a later state frame.
        await client.SubmitInputAsync(runId, inputId, answer, cancellationToken).ConfigureAwait(false);
        if (ownsRequest(runId, inputId))
        {
            clearPendingInput();
            await refresh(cancellationToken).ConfigureAwait(false);
        }
    }
}
