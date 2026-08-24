namespace RuAgent.Harness.Core.Tests.Fakes;

public sealed class FakeModelStepRunner : IModelStepRunner
{
    private readonly Queue<QueuedStep> _steps = new();
    private readonly IList<string>? _trace;

    public FakeModelStepRunner(IList<string>? trace = null) => _trace = trace;

    public List<ModelStepRequest> Requests { get; } = new();

    public void Enqueue(AssistantStep step, params string[] deltas) =>
        _steps.Enqueue(new QueuedStep(step, deltas));

    public async Task<AssistantStep> RunStepAsync(
        ModelStepRequest request,
        Func<AssistantDelta, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        _trace?.Add($"model:{Requests.Count}");
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("No fake model step was queued.");
        }

        var queued = _steps.Dequeue();
        for (var index = 0; index < queued.Deltas.Count; index++)
        {
            await publishDelta(new AssistantDelta(
                request.ModelStepId,
                index + 1,
                queued.Deltas[index]));
        }

        return queued.Step;
    }

    private sealed record QueuedStep(AssistantStep Step, IReadOnlyList<string> Deltas);
}
