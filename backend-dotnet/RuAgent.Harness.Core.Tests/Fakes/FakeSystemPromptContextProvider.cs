using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core.Tests.Fakes;

public sealed class FakeSystemPromptContextProvider : ISystemPromptContextProvider
{
    private readonly Func<AgentRun, ModelContextSnapshot, SystemPromptContext> _factory;

    public FakeSystemPromptContextProvider(
        Func<AgentRun, ModelContextSnapshot, SystemPromptContext>? factory = null)
    {
        _factory = factory ?? ((_, _) => SystemPromptContext.Empty);
    }

    public List<SystemPromptContext> Contexts { get; } = new();

    public Task<SystemPromptContext> BuildAsync(
        AgentRun run,
        ModelContextSnapshot modelContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = _factory(run, modelContext);
        Contexts.Add(context);
        return Task.FromResult(context);
    }
}
