namespace RuAgent.Harness.Core.Tests.Fakes;

using RuAgent.Harness.Core.Prompting;

public sealed class FakeModelStepRunner : IModelStepRunner
{
    private readonly Queue<QueuedStep> _steps = new();
    private readonly IList<string>? _trace;

    public FakeModelStepRunner(IList<string>? trace = null) => _trace = trace;

    public List<ModelStepRequest> Requests { get; } = new();

    public List<SystemPromptContext> PromptContexts { get; } = new();

    public void Enqueue(AssistantStep step, params string[] deltas) =>
        _steps.Enqueue(new QueuedStep(null, step, deltas));

    public void Enqueue(ModelStepResult result, params string[] deltas) =>
        _steps.Enqueue(new QueuedStep(result, null, deltas));

    public async Task<ModelStepResult> RunStepAsync(
        ModelStepRequest request,
        Func<AssistantDelta, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        PromptContexts.Add(request.PromptContext);
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

        return queued.Result
            ?? new ModelStepResult(queued.Step!, ReceiptFor(request.PromptContext));
    }

    private static SystemPromptReceipt ReceiptFor(SystemPromptContext context)
    {
        // A real receipt bound to a rendered prompt: the coordinator now rejects
        // the all-zero fixture shape, so the fake composes exactly like the model step.
        return SystemPromptComposer.Compose(context, nativeToolCalling: true).Receipt;
    }

    private sealed record QueuedStep(
        ModelStepResult? Result,
        AssistantStep? Step,
        IReadOnlyList<string> Deltas);
}
